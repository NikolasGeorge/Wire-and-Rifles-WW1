using System.Collections.Generic;
using FishNet;
using FishNet.Connection;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

public enum FortificationType : byte
{
    Sandbags = 0,
    LowWire = 1,
    HighWire = 2,
    TrenchWall = 3,
    AmmoCrate = 4,
    MedCrate = 5,
    Toolbox = 6,

    // Trench furniture: walkable and climbable pieces rather than cover.
    DuckBoards = 7,
    CorrugatedRoof = 8,
    Ladder = 9,
    MakeshiftFloor = 10
}

// Server-authoritative blueprint/build system (Engineer class notes):
// - The placer drops a BLUEPRINT: visible, no collision, no function.
// - Anyone on the team builds it by holding F nearby (shovel work);
//   Engineers build at 2x. Multiple builders stack additively.
// - Progress persists if builders walk away (checkpoint behavior).
// - Completed structures have health, block/slow/damage per type, can be
//   damaged by enemy fire and repaired by friendly shovel work.
// - Class boundaries: Engineer owns structures and wire; the ammo box
//   belongs to Support and the med box to Medic.
public class FortificationManager : NetworkBehaviour
{
    public static FortificationManager Instance { get; private set; }

    [Header("Placement Rules")]
    public float maxPlaceDistance = 6f;

    [Tooltip("Lenient by design — players are allowed to build creatively on slopes; the structure stays upright regardless.")]
    public float maxGroundAngle = 50f;

    // Edge length of one makeshift floor tile. Drives both its collider and
    // the side-by-side snapping grid, so if the prop's footprint is not
    // square-2m this is the single value to correct.
    public const float MakeshiftFloorTileSize = 2f;

    [Tooltip("Max structures of the SAME type allowed within stacking range of each other (1 on the ground + 1 on top).")]
    public int maxSameTypeStack = 2;
    public float stackCheckRadius = 1.5f;

    // Wire on wire is pure waste (no stacking benefit), so wire allows only
    // ONE of its type per overlap area. Offset/chained wire lines still work
    // since the check radius is much shorter than a wire segment.
    public static int GetStackLimit(FortificationType type, int defaultLimit)
    {
        if (type == FortificationType.LowWire || type == FortificationType.HighWire)
        {
            return 1;
        }

        return defaultLimit;
    }

    [Header("Team Supplies")]
    [Tooltip("Each team's build pool at round start. Placing costs supplies; the pool grows over time.")]
    public int startingSupplies = 60;
    public int suppliesPerTick = 10;
    public float supplyTickInterval = 10f;
    public int suppliesCap = 300;

    // Income rate for the build menu header.
    public float SuppliesPerMinute =>
        supplyTickInterval <= 0f ? 0f : suppliesPerTick * (60f / supplyTickInterval);

    // Per-team build resources, server-written.
    private readonly SyncVar<int> syncAlliedSupplies = new SyncVar<int>();
    private readonly SyncVar<int> syncCentralSupplies = new SyncVar<int>();
    private float supplyTimer;

    public int GetSupplies(Team team)
    {
        return team == Team.CentralPowers ? syncCentralSupplies.Value : syncAlliedSupplies.Value;
    }

    // Pieces whose whole point is sitting where the player put them, rather
    // than on the ground: they neither snap down on placement nor settle
    // with the terrain afterwards.
    public static bool KeepsPlacedHeight(FortificationType type)
    {
        return type == FortificationType.MakeshiftFloor
            || type == FortificationType.CorrugatedRoof;
    }

    // Thrown deployables (ammo/med/toolbox): no collision, no blueprint
    // stage, one active per player per type, AOE effect while placed.
    public static bool IsDeployableCrate(FortificationType type)
    {
        return type == FortificationType.AmmoCrate
            || type == FortificationType.MedCrate
            || type == FortificationType.Toolbox;
    }

    public static int GetCost(FortificationType type)
    {
        switch (type)
        {
            case FortificationType.LowWire: return 8;
            case FortificationType.HighWire: return 12;
            // A full sandbag wall is three times the work of a sandbag stack.
            case FortificationType.TrenchWall: return 90;
            case FortificationType.AmmoCrate: return 5;
            case FortificationType.MedCrate: return 5;
            case FortificationType.Toolbox: return 5;
            case FortificationType.DuckBoards: return 6;
            case FortificationType.CorrugatedRoof: return 10;
            case FortificationType.Ladder: return 6;
            case FortificationType.MakeshiftFloor: return 8;
            default: return 10;
        }
    }

    [Header("Build Rules")]
    public float buildInteractRange = 4.5f;

    [Header("Wire")]
    public float wireDamagePerSecond = 15f;

    [Tooltip("Entry damage multiplier cap: hitting wire at full sprint costs up to this many times the base entry bite.")]
    public float wireEntrySpeedCap = 3f;

    // Last known positions, only used to measure how fast a player was
    // moving at the moment they FIRST hit wire (before the slow applies).
    private readonly Dictionary<int, Vector3> wireLastPositions = new Dictionary<int, Vector3>();

    [Header("Crate Effects")]
    public float crateEffectRadius = 3.5f;
    public float crateEffectInterval = 2f;
    public int ammoPerTick = 5;
    public float healPerTick = 10f;

    [Tooltip("Med crates tick twice as fast as ammo crates, healing this much per tick.")]
    public float medHealPerTick = 4f;
    private float medEffectTimer;

    [Header("Toolbox (Engineer)")]
    [Tooltip("Build/repair speed multiplier for work done inside a friendly toolbox's radius.")]
    public float toolboxWorkMultiplier = 2f;

    [Tooltip("Fraction of a structure's max health auto-repaired per second by a nearby friendly toolbox.")]
    public float toolboxAutoRepairPerSecond = 0.01f;

    private float toolboxRepairTimer;

    public static string GetDisplayName(FortificationType type)
    {
        switch (type)
        {
            case FortificationType.LowWire: return "Low Wire";
            case FortificationType.HighWire: return "High Wire";
            case FortificationType.TrenchWall: return "Trench Wall";
            case FortificationType.AmmoCrate: return "Ammo Box";
            case FortificationType.MedCrate: return "Med Box";
            case FortificationType.Toolbox: return "Toolbox";
            case FortificationType.DuckBoards: return "Duck Boards";
            case FortificationType.CorrugatedRoof: return "Corrugated Roof";
            case FortificationType.Ladder: return "Ladder";
            case FortificationType.MakeshiftFloor: return "Makeshift Floor";
            default: return "Sandbags";
        }
    }

    // Seconds of shovel work at 1x to complete.
    public static float GetBuildTime(FortificationType type)
    {
        switch (type)
        {
            case FortificationType.LowWire: return 15f;
            case FortificationType.HighWire: return 25f;
            // Three times a sandbag stack's 20s, matching its tripled cost.
            case FortificationType.TrenchWall: return 60f;
            case FortificationType.AmmoCrate: return 10f;
            case FortificationType.MedCrate: return 10f;
            case FortificationType.Toolbox: return 10f;
            case FortificationType.DuckBoards: return 8f;
            case FortificationType.CorrugatedRoof: return 14f;
            case FortificationType.Ladder: return 8f;
            case FortificationType.MakeshiftFloor: return 10f;
            default: return 20f;
        }
    }

    // Structure HP. Sandbags per design: ~100 rifle shots.
    public static float GetMaxHealth(FortificationType type)
    {
        switch (type)
        {
            case FortificationType.LowWire: return 800f;
            case FortificationType.HighWire: return 1500f;
            case FortificationType.TrenchWall: return 20000f;
            case FortificationType.AmmoCrate: return 500f;
            case FortificationType.MedCrate: return 500f;
            case FortificationType.Toolbox: return 500f;
            // Trench furniture is timber and sheet metal, not cover — it
            // gives way far sooner than a sandbag line.
            case FortificationType.DuckBoards: return 1500f;
            case FortificationType.CorrugatedRoof: return 2000f;
            case FortificationType.Ladder: return 1200f;
            case FortificationType.MakeshiftFloor: return 1800f;
            default: return 10000f;
        }
    }

    private class StructureRecord
    {
        public int id;
        public FortificationType type;
        public Team team;
        public Vector3 position;
        public float yaw;
        public float progress;
        public float health;
        public int placerClientId;
        public bool dirty;

        // Height above the terrain surface at the moment of placement (0 on
        // the ground, ~1 for a stacked one). Replicated so every client —
        // including late joiners looking at already-dug ground — settles the
        // structure to the same height.
        public float terrainOffset;

        // Highest 25% build checkpoint banked so far (0, .25, .5, .75).
        // Progress is never allowed to drop below this.
        public float buildCheckpoint;

        public bool Complete => progress >= 1f;
    }

    private readonly List<StructureRecord> serverStructures = new List<StructureRecord>();
    // clientId -> structure id being worked on (build or repair).
    private readonly Dictionary<int, int> serverActiveBuilders = new Dictionary<int, int>();
    private readonly Dictionary<int, FortificationStructure> clientStructures = new Dictionary<int, FortificationStructure>();
    private int nextStructureId;
    private float crateEffectTimer;
    private float wireDamageTimer;
    private float syncTimer;

    private void Awake()
    {
        Instance = this;
    }

    // Without this a torn-down manager stays the live Instance across a
    // scene reload, and every world effect silently routes into a destroyed
    // object.
    private void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }
    }

    public override void OnStartServer()
    {
        base.OnStartServer();

        syncAlliedSupplies.Value = startingSupplies;
        syncCentralSupplies.Value = startingSupplies;
    }

    private StructureRecord FindRecord(int structureId)
    {
        foreach (StructureRecord record in serverStructures)
        {
            if (record.id == structureId)
            {
                return record;
            }
        }

        return null;
    }

    private static PlayerNetworkSetup FindPlayerByClientId(int clientId)
    {
        foreach (PlayerNetworkSetup player in FindObjectsByType<PlayerNetworkSetup>(FindObjectsSortMode.None))
        {
            if (player.Owner != null && player.Owner.ClientId == clientId)
            {
                return player;
            }
        }

        return null;
    }

    // ---- Placement ----

    [ServerRpc(RequireOwnership = false)]
    public void RequestPlace(FortificationType type, Vector3 position, float yaw, NetworkConnection sender = null)
    {
        if (sender == null || sender.FirstObject == null)
        {
            return;
        }

        PlayerNetworkSetup setup = sender.FirstObject.GetComponent<PlayerNetworkSetup>();
        PlayerNetworkHealth health = sender.FirstObject.GetComponent<PlayerNetworkHealth>();

        if (setup == null || (health != null && health.State != PlayerLifeState.Alive))
        {
            return;
        }

        if (!ClassCanPlace(setup.AssignedClass, type))
        {
            Debug.Log("[Fortifications] Place rejected: class cannot place " + type);
            return;
        }

        if (Vector3.Distance(sender.FirstObject.transform.position, position) > maxPlaceDistance + 1.5f)
        {
            Debug.Log("[Fortifications] Place rejected: too far.");
            return;
        }

        // Ground snap + slope check: structures stay upright (yaw only) and
        // refuse terrain that is too steep, instead of tilting.
        //
        // Deliberately skipped for pieces that are placed AT A HEIGHT: a
        // floor snapped level with its neighbour, or a roof lined up
        // overhead, would otherwise be dragged straight back down to the
        // dirt and lose the alignment the player just made.
        if (!KeepsPlacedHeight(type)
            && Physics.Raycast(position + Vector3.up * 2f, Vector3.down, out RaycastHit groundHit, 10f,
            Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            // Anything solid is a foundation — terrain or another structure
            // alike. Blueprints cannot be built on because their colliders
            // stay off until they are finished, so the ray passes through
            // them and finds the ground beneath.
            bool onStructure = groundHit.collider.GetComponentInParent<FortificationStructure>() != null;

            // The slope rule is about refusing to perch things on a cliff
            // face; a structure's own top face is flat by definition.
            if (!onStructure && Vector3.Angle(groundHit.normal, Vector3.up) > maxGroundAngle)
            {
                Debug.Log("[Fortifications] Place rejected: ground too steep.");
                return;
            }

            position = groundHit.point;
        }

        // Stack rule: at most maxSameTypeStack structures of the SAME type
        // within stacking range of each other (1 on the ground + 1 on top).
        int nearbySameType = 0;

        foreach (StructureRecord existing in serverStructures)
        {
            if (existing.type != type)
            {
                continue;
            }

            Vector3 flat = existing.position - position;
            float vertical = Mathf.Abs(flat.y);
            flat.y = 0f;

            if (flat.magnitude <= stackCheckRadius && vertical <= 4f)
            {
                nearbySameType++;
            }
        }

        if (nearbySameType >= GetStackLimit(type, maxSameTypeStack))
        {
            Debug.Log("[Fortifications] Place rejected: stack limit for " + type + ".");
            return;
        }

        // Crates: one active per player — placing a new one replaces the old.
        if (IsDeployableCrate(type))
        {
            for (int i = serverStructures.Count - 1; i >= 0; i--)
            {
                if (serverStructures[i].placerClientId == sender.ClientId && serverStructures[i].type == type)
                {
                    ServerRemoveStructure(serverStructures[i]);
                }
            }
        }

        // Team supply pool pays for every placement.
        int cost = GetCost(type);
        SyncVar<int> pool = setup.AssignedTeam == Team.CentralPowers ? syncCentralSupplies : syncAlliedSupplies;

        if (pool.Value < cost)
        {
            Debug.Log("[Fortifications] Place rejected: not enough team supplies (" + pool.Value + "/" + cost + ").");
            return;
        }

        pool.Value -= cost;

        StructureRecord record = new StructureRecord
        {
            id = nextStructureId++,
            type = type,
            team = setup.AssignedTeam,
            position = position,
            yaw = yaw,
            progress = 0f,
            health = 0f,
            placerClientId = sender.ClientId,
            terrainOffset = HeightAboveTerrain(position, yaw)
        };

        serverStructures.Add(record);

        Debug.Log("[Fortifications] Blueprint placed: " + type + " by client " + sender.ClientId);
        ObserversSpawnStructure(record.id, type, record.team, position, yaw, sender.ClientId, record.terrainOffset);
    }

    // Where a structure currently sits after settling into the terrain. The
    // record keeps the original placement point; this re-derives the live
    // height the same way the structure itself does.
    private static Vector3 SettledPosition(StructureRecord record)
    {
        if (!FortificationStructure.TryGetTerrainSurfaceY(record.position, record.yaw, out float surfaceY))
        {
            return record.position;
        }

        return new Vector3(record.position.x, surfaceY + record.terrainOffset, record.position.z);
    }

    // How far a placement sits above the terrain under its footprint. Uses
    // the same sampling the structure settles with, so a freshly placed
    // structure does not immediately shift.
    private static float HeightAboveTerrain(Vector3 position, float yaw)
    {
        if (!FortificationStructure.TryGetTerrainSurfaceY(position, yaw, out float surfaceY))
        {
            return 0f;
        }

        return Mathf.Max(0f, position.y - surfaceY);
    }

    private static bool ClassCanPlace(PlayerClass playerClass, FortificationType type)
    {
        switch (type)
        {
            case FortificationType.AmmoCrate:
                return playerClass == PlayerClass.Support;
            case FortificationType.MedCrate:
                return playerClass == PlayerClass.Medic;
            case FortificationType.Toolbox:
                return playerClass == PlayerClass.Engineer;
            default:
                // Structures and wire are Engineer identity.
                return PlayerClasses.Get(playerClass).buildDigMultiplier > 1f;
        }
    }

    // ---- Building / repairing (hold F with the shovel) ----

    // structureId -1 stops working.
    [ServerRpc(RequireOwnership = false)]
    public void SetBuilding(int structureId, NetworkConnection sender = null)
    {
        if (sender == null)
        {
            return;
        }

        if (structureId < 0)
        {
            serverActiveBuilders.Remove(sender.ClientId);
        }
        else
        {
            serverActiveBuilders[sender.ClientId] = structureId;
        }
    }

    private void Update()
    {
        if (!IsServerInitialized)
        {
            return;
        }

        AdvanceBuilders(Time.deltaTime);

        // Supply pools grow as the game goes on.
        supplyTimer += Time.deltaTime;

        if (supplyTimer >= supplyTickInterval)
        {
            supplyTimer = 0f;
            syncAlliedSupplies.Value = Mathf.Min(suppliesCap, syncAlliedSupplies.Value + suppliesPerTick);
            syncCentralSupplies.Value = Mathf.Min(suppliesCap, syncCentralSupplies.Value + suppliesPerTick);
        }

        wireDamageTimer += Time.deltaTime;

        // Fast wire ticks: a sprinter crosses a wire strip in ~0.4s, so a
        // slow tick could miss them entirely.
        if (wireDamageTimer >= 0.1f)
        {
            ApplyWireDamage(wireDamageTimer);
            wireDamageTimer = 0f;
        }

        crateEffectTimer += Time.deltaTime;

        if (crateEffectTimer >= crateEffectInterval)
        {
            crateEffectTimer = 0f;
            ApplyCrateEffects(true);
        }

        medEffectTimer += Time.deltaTime;

        if (medEffectTimer >= crateEffectInterval * 0.5f)
        {
            medEffectTimer = 0f;
            ApplyCrateEffects(false);
        }

        toolboxRepairTimer += Time.deltaTime;

        if (toolboxRepairTimer >= 0.5f)
        {
            ApplyToolboxAutoRepair(toolboxRepairTimer);
            toolboxRepairTimer = 0f;
        }

        syncTimer += Time.deltaTime;

        if (syncTimer >= 0.4f)
        {
            syncTimer = 0f;
            SyncDirtyStructures();
        }
    }

    private void AdvanceBuilders(float deltaTime)
    {
        if (serverActiveBuilders.Count == 0)
        {
            return;
        }

        List<int> stale = null;

        foreach (KeyValuePair<int, int> entry in serverActiveBuilders)
        {
            StructureRecord record = FindRecord(entry.Value);
            PlayerNetworkSetup builder = FindPlayerByClientId(entry.Key);

            bool valid = record != null && builder != null;

            if (valid)
            {
                PlayerNetworkHealth builderHealth = builder.GetComponent<PlayerNetworkHealth>();

                valid = (builderHealth == null || builderHealth.State == PlayerLifeState.Alive)
                    && builder.AssignedTeam == record.team
                    && Vector3.Distance(builder.transform.position, record.position) <= buildInteractRange + 1f;
            }

            if (!valid)
            {
                (stale ??= new List<int>()).Add(entry.Key);
                continue;
            }

            float multiplier = Mathf.Max(1f, PlayerClasses.Get(builder.AssignedClass).buildDigMultiplier);

            // A friendly toolbox in range doubles build and repair speed.
            if (HasToolboxCoverage(record.position, record.team))
            {
                multiplier *= toolboxWorkMultiplier;
            }

            float buildTime = GetBuildTime(record.type);
            float maxHealth = GetMaxHealth(record.type);

            if (!record.Complete)
            {
                bool wasComplete = record.Complete;
                // Banked quarters: once a 25/50/75% checkpoint is reached the
                // structure can never fall back below it, so partial work is
                // never wasted.
                record.progress = Mathf.Clamp(record.progress + multiplier * deltaTime / buildTime,
                    record.buildCheckpoint, 1f);
                record.buildCheckpoint = Mathf.Min(0.75f, Mathf.Floor(record.progress * 4f) * 0.25f);
                record.health = record.progress * maxHealth;
                record.dirty = true;

                if (!wasComplete && record.Complete)
                {
                    record.health = maxHealth;
                    ObserversStructureCompleted(record.id);
                }
            }
            else if (record.health < maxHealth)
            {
                // Repair at the same rate building progresses.
                record.health = Mathf.Min(maxHealth, record.health + maxHealth * multiplier * deltaTime / buildTime);
                record.dirty = true;
            }
        }

        if (stale != null)
        {
            foreach (int clientId in stale)
            {
                serverActiveBuilders.Remove(clientId);
            }
        }
    }

    // Thrown supply crates (Support ammo / Medic med kit): the server record
    // exists — complete and dispensing — from the moment of the THROW, its
    // position tracking the flying box, so the AOE works mid-air. The visual
    // structure appears for clients when it lands (Finalize). One active
    // crate of each type per player; a new throw replaces it.
    public int ServerCreateThrownCrate(FortificationType type, Vector3 position, Team team, int placerClientId)
    {
        if (!IsServerStarted || !IsDeployableCrate(type))
        {
            return -1;
        }

        for (int i = serverStructures.Count - 1; i >= 0; i--)
        {
            if (serverStructures[i].placerClientId == placerClientId && serverStructures[i].type == type)
            {
                ServerRemoveStructure(serverStructures[i]);
            }
        }

        StructureRecord record = new StructureRecord
        {
            id = nextStructureId++,
            type = type,
            team = team,
            position = position,
            yaw = 0f,
            progress = 1f,
            health = GetMaxHealth(type),
            placerClientId = placerClientId
        };

        serverStructures.Add(record);

        return record.id;
    }

    public void ServerMoveThrownCrate(int structureId, Vector3 position)
    {
        StructureRecord record = FindRecord(structureId);

        if (record != null)
        {
            record.position = position;
        }
    }

    public void ServerFinalizeThrownCrate(int structureId, Vector3 landingPosition)
    {
        StructureRecord record = FindRecord(structureId);

        if (record == null)
        {
            return;
        }

        record.position = landingPosition;
        record.terrainOffset = HeightAboveTerrain(landingPosition, 0f);
        ObserversSpawnStructure(record.id, record.type, record.team, landingPosition, 0f, record.placerClientId,
            record.terrainOffset);
        ObserversStructureCompleted(record.id);
    }

    // Engineer teardown: full supply refund for blueprints, half for
    // completed structures.
    [ServerRpc(RequireOwnership = false)]
    public void RequestDeconstruct(int structureId, NetworkConnection sender = null)
    {
        if (sender == null || sender.FirstObject == null)
        {
            return;
        }

        StructureRecord record = FindRecord(structureId);
        PlayerNetworkSetup setup = sender.FirstObject.GetComponent<PlayerNetworkSetup>();

        if (record == null || setup == null)
        {
            return;
        }

        if (PlayerClasses.Get(setup.AssignedClass).buildDigMultiplier <= 1f
            || setup.AssignedTeam != record.team
            || Vector3.Distance(sender.FirstObject.transform.position, record.position) > buildInteractRange + 2f)
        {
            return;
        }

        int refund = record.Complete ? GetCost(record.type) / 2 : GetCost(record.type);
        SyncVar<int> pool = record.team == Team.CentralPowers ? syncCentralSupplies : syncAlliedSupplies;
        pool.Value = Mathf.Min(suppliesCap, pool.Value + refund);

        Debug.Log("[Fortifications] Deconstructed " + record.type + ", refunded " + refund + " supplies.");
        ServerRemoveStructure(record);
    }

    // ---- Structure damage ----

    // How much of a hit actually lands, by structure type and damage source.
    // Only Sandbags has tuned numbers so far; everything else stays neutral
    // (100%, matching pre-damage-type behavior) until specified.
    public static float GetDamageMultiplier(FortificationType type, DamageType damageType)
    {
        if (type == FortificationType.Sandbags)
        {
            switch (damageType)
            {
                case DamageType.Bullet: return 0.5f;
                case DamageType.Explosive: return 1.25f;
                case DamageType.Axe: return 2f;
                case DamageType.Shovel: return 0.75f;
                case DamageType.Fire: return 1f;
            }
        }

        return 1f;
    }

    // Bullets: client raycasts, reports the hit, server re-validates the
    // shooter/target here (sender is auto-filled by FishNet from the RPC).
    [ServerRpc(RequireOwnership = false)]
    public void ReportStructureDamage(int structureId, float damage, DamageType damageType, NetworkConnection sender = null)
    {
        if (sender == null || sender.FirstObject == null)
        {
            return;
        }

        PlayerNetworkSetup shooter = sender.FirstObject.GetComponent<PlayerNetworkSetup>();

        if (shooter == null)
        {
            return;
        }

        ApplyStructureDamage(FindRecord(structureId), damage, damageType, shooter.AssignedTeam, shooter);
    }

    // Grenades and fire creep already run server-side (no client to validate
    // against), so they call straight through instead of going via a ServerRpc.
    public void ServerDamageStructuresInRadius(Vector3 position, float radius, float maxDamage, DamageType damageType,
        Team attackerTeam, PlayerNetworkSetup attacker = null)
    {
        if (radius <= 0f)
        {
            return;
        }

        // Copy first: ApplyStructureDamage can remove entries from
        // serverStructures mid-iteration when a hit destroys a structure.
        foreach (StructureRecord record in new List<StructureRecord>(serverStructures))
        {
            float distance = Vector3.Distance(record.position, position);

            if (distance > radius)
            {
                continue;
            }

            float falloffDamage = maxDamage * (1f - distance / radius);
            ApplyStructureDamage(record, falloffDamage, damageType, attackerTeam, attacker);
        }
    }

    // Melee: PlayerNetworkSetup's ServerRpc already validated range/cooldown
    // with the swinger's own authority, so this is a direct server call.
    public void ServerDamageStructureDirect(int structureId, float damage, DamageType damageType,
        Team attackerTeam, PlayerNetworkSetup attacker = null)
    {
        ApplyStructureDamage(FindRecord(structureId), damage, damageType, attackerTeam, attacker);
    }

    private void ApplyStructureDamage(StructureRecord record, float damage, DamageType damageType,
        Team attackerTeam, PlayerNetworkSetup attacker)
    {
        // Blueprints cannot be damaged (first-version rule).
        if (record == null || !record.Complete || attackerTeam == record.team)
        {
            return;
        }

        // Upper bound is only an anti-cheat guard on client-reported bullet
        // damage; it must stay well clear of a legitimate axe swing.
        float multiplier = GetDamageMultiplier(record.type, damageType);
        float structureDamage = Mathf.Clamp(damage * multiplier, 0f, 2000f);
        record.health -= structureDamage;
        record.dirty = true;

        // Hit marker on the attacker's screen, showing the damage that
        // actually landed after the structure's resistance. Destroying a
        // structure is not flagged as a "kill" — that marker and its sound
        // mean an eliminated player.
        if (attacker != null)
        {
            attacker.ServerReportHit(structureDamage, false, false);
        }

        if (record.health <= 0f)
        {
            ServerRemoveStructure(record);
        }
    }

    private void ServerRemoveStructure(StructureRecord record)
    {
        serverStructures.Remove(record);
        ObserversRemoveStructure(record.id);
    }

    // ---- Wire and crate server effects ----

    private readonly HashSet<int> playersInWirePreviousTick = new HashSet<int>();
    private readonly HashSet<int> playersInWireCurrentTick = new HashSet<int>();

    private void ApplyWireDamage(float interval)
    {
        PlayerNetworkSetup[] players = FindObjectsByType<PlayerNetworkSetup>(FindObjectsSortMode.None);

        // Speed snapshot per player — consumed only on wire ENTRY.
        Dictionary<int, float> speeds = new Dictionary<int, float>();

        foreach (PlayerNetworkSetup player in players)
        {
            int key = player.OwnerId;
            float speed = 0f;

            if (wireLastPositions.TryGetValue(key, out Vector3 lastPosition) && interval > 0.001f)
            {
                Vector3 moved = player.transform.position - lastPosition;
                moved.y = 0f;
                speed = moved.magnitude / interval;
            }

            speeds[key] = speed;
            wireLastPositions[key] = player.transform.position;
        }

        playersInWireCurrentTick.Clear();

        foreach (StructureRecord record in serverStructures)
        {
            if (!record.Complete
                || (record.type != FortificationType.LowWire && record.type != FortificationType.HighWire))
            {
                continue;
            }

            Quaternion inverse = Quaternion.Euler(0f, -record.yaw, 0f);

            // Wire settles with the terrain, so damage must be measured from
            // where it now sits — not where it was originally placed.
            Vector3 wirePosition = SettledPosition(record);

            foreach (PlayerNetworkSetup player in players)
            {
                // Wire hurts enemies only; teammates are slowed but unharmed.
                if (player.AssignedTeam == record.team)
                {
                    continue;
                }

                Vector3 local = inverse * (player.transform.position - wirePosition);

                if (Mathf.Abs(local.x) > 1.7f || Mathf.Abs(local.z) > 0.8f || local.y < -0.5f || local.y > 1.6f)
                {
                    continue;
                }

                int key = player.OwnerId;

                if (playersInWireCurrentTick.Contains(key))
                {
                    continue;
                }

                PlayerNetworkHealth health = player.GetComponent<PlayerNetworkHealth>();

                if (health != null && !health.IsDead)
                {
                    playersInWireCurrentTick.Add(key);

                    // Inside the wire: flat damage — the heavy slow is the
                    // ongoing punishment.
                    float damage = wireDamagePerSecond * interval;

                    // Entry bite scales with how fast you HIT the wire
                    // (measured before the slow kicks in): walking in pays
                    // the base bite, sprinting in pays up to the cap.
                    if (!playersInWirePreviousTick.Contains(key))
                    {
                        float entryMultiplier = Mathf.Clamp(speeds[key] / 1.5f, 1f, wireEntrySpeedCap);
                        damage += wireDamagePerSecond * 0.3f * entryMultiplier;
                    }

                    health.ServerTakeDamage(damage);
                }
            }
        }

        playersInWirePreviousTick.Clear();
        playersInWirePreviousTick.UnionWith(playersInWireCurrentTick);
    }

    // True when a completed friendly toolbox covers this position.
    private bool HasToolboxCoverage(Vector3 position, Team team)
    {
        foreach (StructureRecord record in serverStructures)
        {
            if (record.Complete && record.type == FortificationType.Toolbox && record.team == team
                && Vector3.Distance(SettledPosition(record), position) <= crateEffectRadius)
            {
                return true;
            }
        }

        return false;
    }

    // Toolboxes slowly mend friendly structures around them with no player
    // input — the Engineer's "set and forget" area denial support.
    private void ApplyToolboxAutoRepair(float deltaTime)
    {
        List<StructureRecord> toolboxes = null;

        foreach (StructureRecord record in serverStructures)
        {
            if (record.Complete && record.type == FortificationType.Toolbox)
            {
                (toolboxes ??= new List<StructureRecord>()).Add(record);
            }
        }

        if (toolboxes == null)
        {
            return;
        }

        foreach (StructureRecord record in serverStructures)
        {
            float maxHealth = GetMaxHealth(record.type);

            // Only completed structures are repaired; blueprints still need
            // a player with a shovel to finish them.
            if (!record.Complete || record.health >= maxHealth)
            {
                continue;
            }

            foreach (StructureRecord toolbox in toolboxes)
            {
                if (toolbox == record || toolbox.team != record.team
                    || Vector3.Distance(SettledPosition(toolbox), SettledPosition(record)) > crateEffectRadius)
                {
                    continue;
                }

                record.health = Mathf.Min(maxHealth, record.health + maxHealth * toolboxAutoRepairPerSecond * deltaTime);
                record.dirty = true;
                break;
            }
        }
    }

    // Med crates tick on their own faster clock (2x rate, half heal per
    // tick) — smoother healing, same rate per second.
    private void ApplyCrateEffects(bool ammoCrates)
    {
        PlayerNetworkSetup[] players = null;
        FortificationType wantedType = ammoCrates ? FortificationType.AmmoCrate : FortificationType.MedCrate;

        foreach (StructureRecord record in serverStructures)
        {
            if (!record.Complete || record.type != wantedType)
            {
                continue;
            }

            players ??= FindObjectsByType<PlayerNetworkSetup>(FindObjectsSortMode.None);

            foreach (PlayerNetworkSetup player in players)
            {
                // Crates settle with the terrain too (digging is allowed
                // right up against them), so measure from where it now sits.
                if (player.AssignedTeam != record.team
                    || Vector3.Distance(player.transform.position, SettledPosition(record)) > crateEffectRadius)
                {
                    continue;
                }

                if (ammoCrates)
                {
                    BoltActionRifle rifle = player.GetComponent<BoltActionRifle>();

                    if (rifle != null)
                    {
                        rifle.ServerGrantReserveAmmo(ammoPerTick);
                    }

                    // Grenades restock at ammo boxes too, one per tick.
                    player.ServerRestockGrenade();
                }
                else
                {
                    PlayerNetworkHealth health = player.GetComponent<PlayerNetworkHealth>();

                    if (health != null)
                    {
                        health.ServerHeal(medHealPerTick);
                    }
                }
            }
        }
    }

    // ---- Replication ----

    private void SyncDirtyStructures()
    {
        foreach (StructureRecord record in serverStructures)
        {
            if (record.dirty)
            {
                record.dirty = false;
                ObserversSyncStructure(record.id, record.progress, record.health);
            }
        }
    }

    public override void OnSpawnServer(NetworkConnection connection)
    {
        base.OnSpawnServer(connection);

        foreach (StructureRecord record in serverStructures)
        {
            TargetSpawnStructure(connection, record.id, record.type, record.team, record.position, record.yaw,
                record.placerClientId, record.progress, record.health, record.terrainOffset);
        }
    }

    [ObserversRpc]
    private void ObserversSpawnStructure(int id, FortificationType type, Team team, Vector3 position, float yaw,
        int placerClientId, float terrainOffset)
    {
        CreateClientStructure(id, type, team, position, yaw, placerClientId, 0f, 0f, terrainOffset);
    }

    [TargetRpc]
    private void TargetSpawnStructure(NetworkConnection connection, int id, FortificationType type, Team team,
        Vector3 position, float yaw, int placerClientId, float progress, float health, float terrainOffset)
    {
        CreateClientStructure(id, type, team, position, yaw, placerClientId, progress, health, terrainOffset);
    }

    [ObserversRpc]
    private void ObserversSyncStructure(int id, float progress, float health)
    {
        if (clientStructures.TryGetValue(id, out FortificationStructure structure) && structure != null)
        {
            bool wasComplete = structure.complete;
            structure.progress = progress;
            structure.health = health;

            if (!wasComplete && progress >= 1f)
            {
                structure.SetComplete();
            }
        }
    }

    [ObserversRpc]
    private void ObserversStructureCompleted(int id)
    {
        if (clientStructures.TryGetValue(id, out FortificationStructure structure) && structure != null)
        {
            structure.progress = 1f;
            structure.health = structure.maxHealth;
            structure.SetComplete();
        }
    }

    [ObserversRpc]
    private void ObserversRemoveStructure(int id)
    {
        if (clientStructures.TryGetValue(id, out FortificationStructure structure))
        {
            clientStructures.Remove(id);

            if (structure != null)
            {
                Destroy(structure.gameObject);
            }
        }
    }

    // ---- Persistent world effects ----
    // Players are DESPAWNED on death (PlayerNetworkHealth.ServerRespawn), so
    // anything hosted on a player object dies with them: a grenade thrown a
    // second before dying never detonated, a lit fire patch stopped early,
    // and a supply crate still in the air was left registered and dispensing
    // forever with no visual. This manager is a scene object that never
    // despawns, so world effects outlive whoever started them.

    public Coroutine RunPersistentEffect(System.Collections.IEnumerator routine)
    {
        return routine == null ? null : StartCoroutine(routine);
    }

    [ObserversRpc]
    public void ObserversWorldGrenadeVisual(Vector3 origin, Vector3 velocity, GrenadeType grenadeType, float fuseSeconds)
    {
        GrenadeVisual.Spawn(origin, velocity, false, grenadeType, fuseSeconds);
    }

    [ObserversRpc]
    public void ObserversWorldCrateVisual(Vector3 origin, Vector3 velocity)
    {
        GrenadeVisual.Spawn(origin, velocity, true);
    }

    [ObserversRpc]
    public void ObserversWorldExplosionFx(Vector3 position, bool smoke)
    {
        ExplosionFx.Spawn(position, smoke);
    }

    [ObserversRpc]
    private void ObserversWorldFlareVisual(Vector3 origin, Vector3 velocity, float burnSeconds)
    {
        FlareVisual.Spawn(origin, velocity, burnSeconds);
    }

    // A burning flare that keeps revealing enemies underneath it for as long
    // as it hangs, rather than spotting once and going out. Hosted here so a
    // Scout who is killed still lights the ground they fired over.
    public void ServerRunFlare(Vector3 origin, Vector3 velocity, Team spottingTeam,
        float burnSeconds, float radius)
    {
        if (!IsServerStarted)
        {
            return;
        }

        ObserversWorldFlareVisual(origin, velocity, burnSeconds);
        RunPersistentEffect(ServerFlareRoutine(origin, velocity, spottingTeam, burnSeconds, radius));
    }

    private System.Collections.IEnumerator ServerFlareRoutine(Vector3 position, Vector3 velocity,
        Team spottingTeam, float burnSeconds, float radius)
    {
        float elapsed = 0f;
        float spotTimer = 0f;
        bool resting = false;

        while (elapsed < burnSeconds)
        {
            float deltaTime = Mathf.Min(Time.deltaTime, 0.05f);

            if (!resting)
            {
                resting = FlareArc.Step(ref position, ref velocity, deltaTime);
            }

            elapsed += deltaTime;
            spotTimer += deltaTime;

            // Re-spotting on a cadence keeps anyone who walks underneath
            // lit, instead of only whoever happened to be there at launch.
            if (spotTimer >= 0.5f)
            {
                spotTimer = 0f;

                foreach (PlayerNetworkSetup target in FindObjectsByType<PlayerNetworkSetup>(FindObjectsSortMode.None))
                {
                    if (target.AssignedTeam == spottingTeam || target.AssignedTeam == Team.Neutral)
                    {
                        continue;
                    }

                    PlayerNetworkHealth targetHealth = target.GetComponent<PlayerNetworkHealth>();

                    if (targetHealth != null && targetHealth.IsDead)
                    {
                        continue;
                    }

                    if (Vector3.Distance(target.transform.position, position) <= radius)
                    {
                        target.ServerApplySpotted(spottingTeam);
                    }
                }
            }

            yield return null;
        }
    }

    [ObserversRpc]
    public void ObserversWorldFireFx(Vector3 position, float radius, float duration)
    {
        FireCreepFx.Spawn(position, radius, duration);
    }

    // ---- Client-side visuals ----

    private void CreateClientStructure(int id, FortificationType type, Team team, Vector3 position, float yaw,
        int placerClientId, float progress, float health, float terrainOffset)
    {
        if (clientStructures.ContainsKey(id))
        {
            return;
        }

        GameObject root = BuildVisual(type, position, Quaternion.Euler(0f, yaw, 0f), out bool usedFallback);
        root.name = "Fortification_" + type + "_" + id;


        FortificationStructure structure = root.AddComponent<FortificationStructure>();
        structure.id = id;
        structure.type = type;
        structure.team = team;
        structure.displayName = GetDisplayName(type);
        structure.maxHealth = GetMaxHealth(type);
        structure.progress = progress;
        structure.health = health;
        structure.SetTerrainOffset(terrainOffset);

        // Blueprint ghosting: the placer sees their own blueprints at 50%
        // opacity, everyone else at 20%.
        bool isLocalPlacer = InstanceFinder.IsClientStarted
            && InstanceFinder.ClientManager.Connection.ClientId == placerClientId;

        AddGameplayColliders(root, type);
        structure.InitializeAsBlueprint(isLocalPlacer ? 0.5f : 0.2f);

        if (progress >= 1f)
        {
            structure.SetComplete();
        }

        // The thrower sees a faint ground ring showing their crate's AOE.
        if (isLocalPlacer && IsDeployableCrate(type))
        {
            Color ringColor;

            switch (type)
            {
                case FortificationType.AmmoCrate: ringColor = new Color(0.9f, 0.75f, 0.3f, 0.05f); break;
                case FortificationType.Toolbox: ringColor = new Color(0.5f, 0.7f, 1f, 0.05f); break;
                default: ringColor = new Color(0.35f, 0.9f, 0.45f, 0.05f); break;
            }

            AddAoeRing(root, crateEffectRadius, ringColor);
        }

        clientStructures[id] = structure;
    }

    // Flat translucent disc on the ground marking an effect radius. Visual
    // only — no collider.
    private static void AddAoeRing(GameObject parent, float radius, Color color)
    {
        GameObject ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        ring.name = "AoeRing";
        Destroy(ring.GetComponent<Collider>());

        ring.transform.SetParent(parent.transform, false);
        ring.transform.localPosition = new Vector3(0f, 0.03f, 0f);
        ring.transform.localScale = new Vector3(radius * 2f, 0.005f, radius * 2f);

        ring.GetComponent<Renderer>().material = new Material(Shader.Find("Sprites/Default"))
        {
            color = color
        };
    }

    // Instantiates the placeholder prop for a type; falls back to a gray box
    // if the visuals asset is missing.
    public static GameObject BuildVisual(FortificationType type, Vector3 position, Quaternion rotation, out bool usedFallback)
    {
        FortificationVisuals visuals = FortificationVisuals.Load();
        GameObject prefab = visuals != null ? visuals.GetPrefab(type) : null;

        GameObject root;

        if (prefab != null)
        {
            root = Instantiate(prefab, position, rotation);
            usedFallback = false;

            // The prop's own colliders are replaced by our gameplay colliders.
            foreach (Collider collider in root.GetComponentsInChildren<Collider>(true))
            {
                Destroy(collider);
            }

            foreach (Rigidbody body in root.GetComponentsInChildren<Rigidbody>(true))
            {
                Destroy(body);
            }
        }
        else
        {
            root = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.Destroy(root.GetComponent<Collider>());
            root.transform.position = position;
            root.transform.rotation = rotation;
            root.transform.localScale = new Vector3(2f, 1f, 0.5f);
            usedFallback = true;
        }

        // High wire is enlarged uniformly. Done HERE rather than at spawn so
        // the placement ghost is the same size as what actually gets built.
        // The visual sits inside a wrapper, leaving gameplay colliders on the
        // unscaled parent at their authored sizes.
        if (type == FortificationType.HighWire)
        {
            root = WrapVisual(root, wrapper => root.transform.localScale *= 1.3f);
        }

        // Roofs pivot from a corner instead of their middle, so a roof is
        // placed by the edge you are lining up against a wall rather than by
        // an invisible centre point floating in mid-air.
        if (type == FortificationType.CorrugatedRoof)
        {
            root = WrapVisual(root, wrapper =>
            {
                Bounds bounds = MeasureBounds(wrapper);
                Vector3 localMin = wrapper.transform.InverseTransformPoint(bounds.min);
                wrapper.transform.GetChild(0).localPosition -= localMin;
            });
        }

        return root;
    }

    // Re-parents a visual under an empty at the same transform, so it can be
    // scaled or offset without moving the object's own origin.
    private static GameObject WrapVisual(GameObject visual, System.Action<GameObject> adjust)
    {
        GameObject wrapper = new GameObject(visual.name);
        wrapper.transform.SetPositionAndRotation(visual.transform.position, visual.transform.rotation);
        visual.transform.SetParent(wrapper.transform, true);

        adjust(wrapper);
        return wrapper;
    }

    // World-space bounds of every renderer under an object.
    private static Bounds MeasureBounds(GameObject root)
    {
        Bounds bounds = new Bounds(root.transform.position, Vector3.zero);
        bool found = false;

        foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
        {
            if (!found)
            {
                bounds = renderer.bounds;
                found = true;
            }
            else
            {
                bounds.Encapsulate(renderer.bounds);
            }
        }

        return bounds;
    }

    // Type-specific colliders, kept disabled until the structure completes.
    private static void AddGameplayColliders(GameObject root, FortificationType type)
    {
        switch (type)
        {
            case FortificationType.Sandbags:
                AddBox(root, new Vector3(0f, 0.5f, 0f), new Vector3(2f, 1f, 0.6f), false);
                break;

            case FortificationType.LowWire:
                // Thin solid strip: bullets can hit it, players step over it.
                AddBox(root, new Vector3(0f, 0.08f, 0f), new Vector3(3f, 0.16f, 1.2f), false);
                AddSlowTrigger(root, new Vector3(0f, 0.4f, 0f), new Vector3(3.2f, 0.9f, 1.5f));
                break;

            case FortificationType.HighWire:
                // Solid barrier matching the visual height — blocks jumps
                // without an invisible wall above the wire.
                AddBox(root, new Vector3(0f, 0.55f, 0f), new Vector3(3f, 1.1f, 0.3f), false);
                AddSlowTrigger(root, new Vector3(0f, 0.6f, 0f), new Vector3(3.2f, 1.2f, 1.4f));
                break;

            case FortificationType.TrenchWall:
                AddBox(root, new Vector3(0f, 0.9f, 0f), new Vector3(2.4f, 1.8f, 0.35f), false);
                break;

            // Walkable surfaces: thin solid slabs you stand on. Sized to the
            // props they represent — adjust here if a prop's footprint
            // differs from these.
            case FortificationType.DuckBoards:
                AddBox(root, new Vector3(0f, 0.06f, 0f), new Vector3(1.6f, 0.12f, 3f), false);
                // Standing volume just above the planks, so only the team
                // that laid them moves faster along them.
                AddDuckBoardSpeedZone(root, new Vector3(0f, 0.9f, 0f), new Vector3(1.7f, 1.8f, 3.1f));
                break;

            case FortificationType.MakeshiftFloor:
                AddBox(root, new Vector3(0f, 0.08f, 0f), new Vector3(MakeshiftFloorTileSize, 0.16f, MakeshiftFloorTileSize), false);
                break;

            // Overhead sheet. Measured from the prop rather than guessed:
            // a hardcoded box floating above the actual mesh is exactly why
            // this read as non-solid.
            case FortificationType.CorrugatedRoof:
                AddMeasuredBox(root);
                break;

            // The rungs are solid so you cannot walk through it. The climb
            // volume is centred and wraps BOTH faces, so it does not matter
            // which way round the ladder was placed.
            case FortificationType.Ladder:
                AddBox(root, new Vector3(0f, 1.5f, 0f), new Vector3(0.9f, 3f, 0.12f), false);
                AddLadderClimbZone(root, new Vector3(0f, 1.5f, 0f), new Vector3(1.1f, 3.2f, 1.6f));
                break;

            default:
                AddBox(root, new Vector3(0f, 0.35f, 0f), new Vector3(0.9f, 0.7f, 0.7f), false);
                break;
        }
    }

    // Collider that matches whatever the prop actually is, so it stays
    // correct no matter which prefab is assigned to the type.
    private static void AddMeasuredBox(GameObject root)
    {
        Bounds bounds = MeasureBounds(root);

        if (bounds.size.sqrMagnitude < 0.0001f)
        {
            AddBox(root, new Vector3(0f, 0.1f, 0f), new Vector3(3f, 0.2f, 3f), false);
            return;
        }

        Vector3 localCenter = root.transform.InverseTransformPoint(bounds.center);
        AddBox(root, localCenter, bounds.size, false);
    }

    private static void AddBox(GameObject root, Vector3 center, Vector3 size, bool isTrigger)
    {
        BoxCollider box = root.AddComponent<BoxCollider>();
        box.center = center;
        box.size = size;
        box.isTrigger = isTrigger;
        box.enabled = false;
    }

    private static void AddDuckBoardSpeedZone(GameObject root, Vector3 center, Vector3 size)
    {
        BoxCollider box = root.AddComponent<BoxCollider>();
        box.center = center;
        box.size = size;
        box.isTrigger = true;
        box.enabled = false;

        root.AddComponent<DuckBoardSpeedZone>();
    }

    private static void AddLadderClimbZone(GameObject root, Vector3 center, Vector3 size)
    {
        BoxCollider box = root.AddComponent<BoxCollider>();
        box.center = center;
        box.size = size;
        box.isTrigger = true;
        box.enabled = false;

        root.AddComponent<LadderClimbZone>();
    }

    private static void AddSlowTrigger(GameObject root, Vector3 center, Vector3 size)
    {
        BoxCollider box = root.AddComponent<BoxCollider>();
        box.center = center;
        box.size = size;
        box.isTrigger = true;
        box.enabled = false;

        root.AddComponent<WireSlowZone>();
    }
}

// Client-side marker on every fortification. Holds replicated progress and
// health, handles the blueprint (dimmed, no collision) to completed
// (full color, colliders on) transition, and drives the build progress bar.
public class FortificationStructure : MonoBehaviour
{
    public int id;
    public FortificationType type;
    public Team team;
    public string displayName;
    public float progress;
    public float health;
    public float maxHealth;
    public bool complete;

    // Live structures, so per-frame queries (suppression cover checks) do not
    // need a scene-wide search.
    public static readonly List<FortificationStructure> All = new List<FortificationStructure>();

    private readonly List<Renderer> blueprintRenderers = new List<Renderer>();
    private readonly List<Material[]> originalMaterials = new List<Material[]>();

    private void OnEnable()
    {
        All.Add(this);
    }

    private void OnDisable()
    {
        All.Remove(this);
    }

    // ---- Terrain adaptation ----
    // A structure remembers how high it sat above the terrain when placed
    // (0 on the ground, ~1 for one stacked on top). When digging or an
    // explosion lowers the ground beneath it, it settles down to match so
    // nothing is left floating over a fresh crater. Purely local: every
    // client runs the same terrain, so the result matches everywhere.
    [Tooltip("Metres per second the structure settles toward the ground.")]
    public float settleSpeed = 1.5f;

    private float terrainOffset;
    private bool terrainOffsetSet;
    private float settleSampleTimer;
    private float targetY;
    private bool hasTargetY;

    public void SetTerrainOffset(float offset)
    {
        // Floors and roofs hold the height they were placed at; letting them
        // chase the ground is what dragged snapped floors back down.
        if (FortificationManager.KeepsPlacedHeight(type))
        {
            terrainOffsetSet = false;
            return;
        }

        terrainOffset = Mathf.Max(0f, offset);
        terrainOffsetSet = true;

        // Snap immediately on spawn so late joiners never see a structure
        // drop into place from its original pre-dig height.
        if (TryGetTerrainSurfaceY(out float surfaceY))
        {
            Vector3 position = transform.position;
            position.y = surfaceY + terrainOffset;
            transform.position = position;

            targetY = position.y;
            hasTargetY = true;
        }
    }

    // Lowest terrain height under the structure's footprint. Sampling across
    // the length (not just the pivot) is what makes it "sink to the bottom":
    // one end can never be left hanging over the lip of a crater.
    public static bool TryGetTerrainSurfaceY(Vector3 worldPosition, float yaw, out float surfaceY)
    {
        Terrain terrain = Terrain.activeTerrain;

        if (terrain == null)
        {
            surfaceY = 0f;
            return false;
        }

        Vector3 right = Quaternion.Euler(0f, yaw, 0f) * Vector3.right;
        float terrainY = terrain.transform.position.y;
        surfaceY = float.MaxValue;

        for (int i = -2; i <= 2; i++)
        {
            Vector3 sample = worldPosition + right * (i * 0.6f);
            surfaceY = Mathf.Min(surfaceY, terrainY + terrain.SampleHeight(sample));
        }

        return true;
    }

    private bool TryGetTerrainSurfaceY(out float surfaceY)
    {
        return TryGetTerrainSurfaceY(transform.position, transform.eulerAngles.y, out surfaceY);
    }

    private void Update()
    {
        if (!terrainOffsetSet)
        {
            return;
        }

        // Re-sample the ground a few times a second; move every frame so the
        // settle reads as a slide rather than a snap.
        settleSampleTimer -= Time.deltaTime;

        if (settleSampleTimer <= 0f)
        {
            settleSampleTimer = 0.2f;

            if (TryGetTerrainSurfaceY(out float surfaceY))
            {
                targetY = surfaceY + terrainOffset;
                hasTargetY = true;
            }
        }

        if (!hasTargetY || Mathf.Abs(targetY - transform.position.y) < 0.005f)
        {
            return;
        }

        Vector3 settled = transform.position;
        settled.y = Mathf.MoveTowards(settled.y, targetY, settleSpeed * Time.deltaTime);
        transform.position = settled;
    }

    // Blueprint look: a genuinely transparent blue hologram of the future
    // structure (alpha 0.5 for the placer, 0.2 for everyone else). Original
    // materials are stored and restored on completion.
    public void InitializeAsBlueprint(float alpha)
    {
        Shader transparentShader = Shader.Find("Sprites/Default");

        foreach (Renderer partRenderer in GetComponentsInChildren<Renderer>(true))
        {
            blueprintRenderers.Add(partRenderer);
            originalMaterials.Add(partRenderer.sharedMaterials);

            Material ghostMaterial = new Material(transparentShader)
            {
                color = new Color(0.5f, 0.7f, 1f, alpha)
            };

            Material[] ghostMaterials = new Material[partRenderer.sharedMaterials.Length];

            for (int i = 0; i < ghostMaterials.Length; i++)
            {
                ghostMaterials[i] = ghostMaterial;
            }

            partRenderer.materials = ghostMaterials;
        }
    }

    public void SetComplete()
    {
        complete = true;
        progress = 1f;

        for (int i = 0; i < blueprintRenderers.Count; i++)
        {
            if (blueprintRenderers[i] != null && i < originalMaterials.Count)
            {
                blueprintRenderers[i].materials = originalMaterials[i];
            }
        }

        // Supply crates never block players — their colliders stay off (the
        // AOE is a server-side distance check, no physics needed).
        if (FortificationManager.IsDeployableCrate(type))
        {
            return;
        }

        foreach (Collider collider in GetComponents<Collider>())
        {
            collider.enabled = true;
        }
    }
}

// Duck boards are a made road: the team that laid them moves faster along
// them, the enemy gets nothing. Reads the team off the structure it sits on
// so it needs no wiring of its own.
public class DuckBoardSpeedZone : MonoBehaviour
{
    public float speedMultiplier = 1.1f;

    private FortificationStructure structure;

    private void Awake()
    {
        structure = GetComponent<FortificationStructure>();
    }

    private void OnTriggerStay(Collider other)
    {
        PlayerController controller = other.GetComponentInParent<PlayerController>();

        if (controller == null || !controller.enabled || structure == null)
        {
            return;
        }

        PlayerNetworkSetup setup = other.GetComponentInParent<PlayerNetworkSetup>();

        if (setup != null && setup.AssignedTeam == structure.team)
        {
            controller.ApplyEnvironmentSpeedBoost(speedMultiplier);
        }
    }
}

// Lets a player climb a completed ladder. Movement is client-authoritative,
// so like the wire slow this only nudges the local controller.
public class LadderClimbZone : MonoBehaviour
{
    private void OnTriggerStay(Collider other)
    {
        PlayerController controller = other.GetComponentInParent<PlayerController>();

        if (controller != null && controller.enabled)
        {
            controller.SetOnLadder();
        }
    }
}

// Slows any player wading through completed wire. Damage is server-side in
// FortificationManager; this only handles the client-authoritative movement.
public class WireSlowZone : MonoBehaviour
{
    [Range(0.1f, 1f)]
    public float slowMultiplier = 0.3f;

    private void OnTriggerStay(Collider other)
    {
        PlayerController controller = other.GetComponentInParent<PlayerController>();

        if (controller != null && controller.enabled)
        {
            controller.ApplyEnvironmentSlow(slowMultiplier);
        }
    }
}
