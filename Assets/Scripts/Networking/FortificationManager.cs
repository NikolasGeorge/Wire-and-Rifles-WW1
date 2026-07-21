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
    MedCrate = 5
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
    public int suppliesPerTick = 5;
    public float supplyTickInterval = 30f;
    public int suppliesCap = 300;

    // Per-team build resources, server-written.
    private readonly SyncVar<int> syncAlliedSupplies = new SyncVar<int>();
    private readonly SyncVar<int> syncCentralSupplies = new SyncVar<int>();
    private float supplyTimer;

    public int GetSupplies(Team team)
    {
        return team == Team.CentralPowers ? syncCentralSupplies.Value : syncAlliedSupplies.Value;
    }

    public static int GetCost(FortificationType type)
    {
        switch (type)
        {
            case FortificationType.LowWire: return 8;
            case FortificationType.HighWire: return 12;
            case FortificationType.TrenchWall: return 30;
            case FortificationType.AmmoCrate: return 5;
            case FortificationType.MedCrate: return 5;
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

    public static string GetDisplayName(FortificationType type)
    {
        switch (type)
        {
            case FortificationType.LowWire: return "Low Wire";
            case FortificationType.HighWire: return "High Wire";
            case FortificationType.TrenchWall: return "Trench Wall";
            case FortificationType.AmmoCrate: return "Ammo Box";
            case FortificationType.MedCrate: return "Med Box";
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
            case FortificationType.TrenchWall: return 60f;
            case FortificationType.AmmoCrate: return 10f;
            case FortificationType.MedCrate: return 10f;
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
        if (Physics.Raycast(position + Vector3.up * 2f, Vector3.down, out RaycastHit groundHit, 10f,
            Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            // Structures normally sit on terrain. Stacking is allowed only
            // onto a COMPLETED structure of the same type whose stack limit
            // is above 1 (sandbags/walls, never wire) — the count check
            // below caps how high the pile can go.
            FortificationStructure supporting = groundHit.collider.GetComponentInParent<FortificationStructure>();

            if (supporting != null)
            {
                bool stackable = supporting.type == type
                    && supporting.complete
                    && GetStackLimit(type, maxSameTypeStack) > 1;

                if (!stackable)
                {
                    Debug.Log("[Fortifications] Place rejected: cannot place on another structure.");
                    return;
                }
            }

            if (Vector3.Angle(groundHit.normal, Vector3.up) > maxGroundAngle)
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
        if (type == FortificationType.AmmoCrate || type == FortificationType.MedCrate)
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

            float multiplier = Mathf.Max(1f, PlayerClasses.Get(FindPlayerByClientId(entry.Key).AssignedClass).buildDigMultiplier);
            float buildTime = GetBuildTime(record.type);
            float maxHealth = GetMaxHealth(record.type);

            if (!record.Complete)
            {
                bool wasComplete = record.Complete;
                record.progress = Mathf.Clamp01(record.progress + multiplier * deltaTime / buildTime);
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
        if (!IsServerStarted || (type != FortificationType.AmmoCrate && type != FortificationType.MedCrate))
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

    [ServerRpc(RequireOwnership = false)]
    public void ReportStructureDamage(int structureId, float damage, NetworkConnection sender = null)
    {
        if (sender == null || sender.FirstObject == null)
        {
            return;
        }

        StructureRecord record = FindRecord(structureId);

        // Blueprints cannot be damaged (first-version rule).
        if (record == null || !record.Complete)
        {
            return;
        }

        PlayerNetworkSetup shooter = sender.FirstObject.GetComponent<PlayerNetworkSetup>();

        if (shooter == null || shooter.AssignedTeam == record.team)
        {
            return;
        }

        // Small arms deal reduced structure damage.
        float structureDamage = Mathf.Clamp(damage, 0f, 300f);
        record.health -= structureDamage;
        record.dirty = true;

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

        // High wire is enlarged uniformly (1.3x). The visual sits in a
        // wrapper so the gameplay colliders below keep their exact authored
        // sizes — no invisible barrier above the model.
        if (type == FortificationType.HighWire)
        {
            GameObject wrapper = new GameObject(root.name);
            wrapper.transform.SetPositionAndRotation(root.transform.position, root.transform.rotation);
            root.transform.SetParent(wrapper.transform, true);
            root.transform.localScale *= 1.3f;
            root = wrapper;
        }

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
        if (isLocalPlacer && (type == FortificationType.AmmoCrate || type == FortificationType.MedCrate))
        {
            AddAoeRing(root, crateEffectRadius, type == FortificationType.AmmoCrate
                ? new Color(0.9f, 0.75f, 0.3f, 0.05f)
                : new Color(0.35f, 0.9f, 0.45f, 0.05f));
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

        return root;
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

            default:
                AddBox(root, new Vector3(0f, 0.35f, 0f), new Vector3(0.9f, 0.7f, 0.7f), false);
                break;
        }
    }

    private static void AddBox(GameObject root, Vector3 center, Vector3 size, bool isTrigger)
    {
        BoxCollider box = root.AddComponent<BoxCollider>();
        box.center = center;
        box.size = size;
        box.isTrigger = isTrigger;
        box.enabled = false;
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

    private readonly List<Renderer> blueprintRenderers = new List<Renderer>();
    private readonly List<Material[]> originalMaterials = new List<Material[]>();

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
        if (type == FortificationType.AmmoCrate || type == FortificationType.MedCrate)
        {
            return;
        }

        foreach (Collider collider in GetComponents<Collider>())
        {
            collider.enabled = true;
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
