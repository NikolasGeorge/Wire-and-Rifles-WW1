using System.Collections;
using FishNet.Connection;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

public class PlayerNetworkSetup : NetworkBehaviour
{
    [Header("Team Assignment")]
    public Color alliedBodyColor = new Color(0.25f, 0.4f, 0.85f);
    public Color centralBodyColor = new Color(0.8f, 0.25f, 0.2f);

    [Header("Soldier Model")]
    [Tooltip("Clone the soldier rig (with hitboxes) from a scene dummy of the matching team instead of showing the capsule.")]
    public bool useSoldierModelFromDummies = true;

    [Tooltip("Idle/Run controller applied to the cloned rig's Animator.")]
    public RuntimeAnimatorController soldierAnimatorController;

    [Tooltip("Humanoid avatar of the soldier rig (Characters.fbx). Used when the cloned rig has no Animator of its own.")]
    public Avatar soldierAvatar;

    [Header("Enemy Visibility")]
    [Tooltip("Boosts saturation/brightness on enemy uniforms (relative to the LOCAL viewer's team) so they pop against the terrain — no outline, just more vivid color. Always on.")]
    public bool boostEnemyContrast = true;
    public float enemyContrastSaturationMultiplier = 1.4f;
    public float enemyContrastSaturationAdd = 0.15f;
    public float enemyContrastValueMultiplier = 1.15f;
    public float enemyContrastValueAdd = 0.1f;

    private bool soldierModelCreated;
    private HelmetPopOff soldierModelHelmet;

    // Server-assigned team, alternated per spawned player.
    private readonly SyncVar<Team> syncTeam = new SyncVar<Team>();

    // Server-assigned class, chosen on the class selection screen.
    private readonly SyncVar<PlayerClass> syncClass = new SyncVar<PlayerClass>();

    // Chosen primary weapon (only Assault has more than one option).
    private readonly SyncVar<WeaponId> syncWeapon = new SyncVar<WeaponId>();

    [Header("Scout Flare Spotting")]
    public float flareCooldown = 10f;
    public float spotRadius = 20f;
    public float spotDuration = 8f;

    [Tooltip("How long a fired flare burns and keeps revealing enemies beneath it.")]
    public float flareBurnSeconds = 24f;

    [Tooltip("Muzzle velocity along the crosshair. The flare goes exactly where it is aimed — aim high to get height.")]
    public float flareLaunchSpeed = 25.5f;

    // Spot replication: the server bumps the pulse and sets the spotting team;
    // every client restarts its local countdown on the pulse change.
    private readonly SyncVar<int> syncSpotPulse = new SyncVar<int>();
    private readonly SyncVar<Team> syncSpottedByTeam = new SyncVar<Team>();

    private float spottedRemaining;
    private float lastFlareTime = -999f;
    private float serverLastFlareTime = -999f;

    // The local player's first-person camera, for projecting spot markers.
    public static Camera LocalPlayerCamera { get; private set; }

    private static int serverTeamAssignCounter;

    // Set by ClassSpawnManager on the server instance just before Spawn().
    [HideInInspector] public Team pendingTeam = Team.Neutral;
    [HideInInspector] public PlayerClass pendingClass = PlayerClass.Assault;
    [HideInInspector] public WeaponId pendingWeapon = WeaponId.BoltAction;
    [HideInInspector] public GrenadeType pendingGrenade = GrenadeType.Frag;
    [HideInInspector] public EquipmentType pendingEquipment1 = EquipmentType.AmmoPouch;
    [HideInInspector] public EquipmentType pendingEquipment2 = EquipmentType.Bandages;

    // Replicated loadout (grenade/equipment systems read these when built).
    private readonly SyncVar<GrenadeType> syncGrenade = new SyncVar<GrenadeType>();
    private readonly SyncVar<EquipmentType> syncEquipment1 = new SyncVar<EquipmentType>();
    private readonly SyncVar<EquipmentType> syncEquipment2 = new SyncVar<EquipmentType>();

    public GrenadeType AssignedGrenade => syncGrenade.Value;
    public EquipmentType AssignedEquipment1 => syncEquipment1.Value;
    public EquipmentType AssignedEquipment2 => syncEquipment2.Value;

    private readonly SyncVar<int> syncGrenadesLeft = new SyncVar<int>();
    public int GrenadesLeft => syncGrenadesLeft.Value;

    // Ammo crates restock grenades one at a time up to the per-life cap.
    public void ServerRestockGrenade()
    {
        if (syncGrenadesLeft.Value < grenadesPerLife)
        {
            syncGrenadesLeft.Value++;
        }
    }

    [Header("Grenades")]
    public int grenadesPerLife = 2;
    public float grenadeDamageRadius = 14f;
    public float grenadeMaxDamage = 90f;
    public float grenadeFlareSpotRadius = 15f;

    // ---- Melee tools (Shovel is universal; Axe is Engineer/Assault only) ----
    [Header("Melee Tools")]
    public float shovelMeleeRange = 2.2f;
    public float axeMeleeRange = 2.2f;
    public float shovelPlayerDamage = 70f;
    public float axePlayerDamage = 80f;
    public float shovelStructureDamage = 75f;
    public float shovelSwingCooldown = 0.75f;
    public float axeSwingCooldown = 0.9f;

    [Tooltip("The axe's anti-structure bonus over the shovel. Derived rather than a separate field so the two can never drift apart; per-structure resistances apply on top.")]
    public float axeStructureBonus = 4f;

    public float AxeStructureDamage => shovelStructureDamage * axeStructureBonus;

    private float serverNextShovelSwingTime;
    private float serverNextAxeSwingTime;

    // Called by PlayerItemSlots after its own local raycast resolves what
    // the swing hit (player / structure / neither).
    [ServerRpc]
    public void RequestMeleeAttack(Vector3 origin, Vector3 hitPoint, NetworkObject targetObject, int structureId, DamageType damageType)
    {
        if (damageType != DamageType.Shovel && damageType != DamageType.Axe)
        {
            return;
        }

        bool isAxe = damageType == DamageType.Axe;
        float cooldown = isAxe ? axeSwingCooldown : shovelSwingCooldown;
        float range = isAxe ? axeMeleeRange : shovelMeleeRange;

        if (isAxe)
        {
            if (Time.time < serverNextAxeSwingTime)
            {
                return;
            }

            serverNextAxeSwingTime = Time.time + cooldown;
        }
        else
        {
            if (Time.time < serverNextShovelSwingTime)
            {
                return;
            }

            serverNextShovelSwingTime = Time.time + cooldown;
        }

        // Sanity checks against a spoofed origin/hit point, matching the
        // rifle's shot-origin/range tolerance conventions.
        Vector3 approximateEyePosition = transform.position + Vector3.up * 1.6f;

        if (Vector3.Distance(approximateEyePosition, origin) > 2f
            || Vector3.Distance(origin, hitPoint) > range + 1f)
        {
            return;
        }

        PlayerNetworkHealth ownHealth = GetComponent<PlayerNetworkHealth>();

        if (ownHealth != null)
        {
            ownHealth.ServerCancelSpawnProtection();
        }

        float structureDamage = isAxe ? AxeStructureDamage : shovelStructureDamage;
        float playerDamage = isAxe ? axePlayerDamage : shovelPlayerDamage;

        if (structureId >= 0)
        {
            if (FortificationManager.Instance != null)
            {
                FortificationManager.Instance.ServerDamageStructureDirect(structureId, structureDamage, damageType, AssignedTeam, this);
            }

            return;
        }

        if (targetObject == null || targetObject == NetworkObject)
        {
            return;
        }

        PlayerNetworkHealth targetHealth = targetObject.GetComponent<PlayerNetworkHealth>();

        if (targetHealth == null || targetHealth.IsDead)
        {
            return;
        }

        PlayerTeam targetTeam = targetObject.GetComponentInChildren<PlayerTeam>(true);

        if (targetTeam != null && targetTeam.team != Team.Neutral && targetTeam.team == AssignedTeam)
        {
            return;
        }

        bool killed = targetHealth.ServerTakeDamage(playerDamage, transform.position);
        ServerReportHit(playerDamage, killed, false);
    }

    // ---- Suppression relay ----
    // The server decides a bullet passed close to this player; only the
    // owner needs to know, since every suppression effect is local.
    // Throttled PER ATTACKER, never globally — a global throttle would drop
    // a second shooter's fire entirely.
    //
    // Rounds arriving inside a throttle window are SUMMED rather than
    // discarded, so an automatic weapon's rate advantage survives the
    // throttle: the client receives the same total damage-weight either way.
    private readonly System.Collections.Generic.Dictionary<int, float> pendingSuppression =
        new System.Collections.Generic.Dictionary<int, float>();
    private readonly System.Collections.Generic.Dictionary<int, float> suppressionFlushTimes =
        new System.Collections.Generic.Dictionary<int, float>();

    public void ServerNotifySuppression(int attackerClientId, float potentialDamage)
    {
        if (!IsServerInitialized || Owner == null || potentialDamage <= 0f)
        {
            return;
        }

        pendingSuppression.TryGetValue(attackerClientId, out float pending);
        pending += potentialDamage;

        if (suppressionFlushTimes.TryGetValue(attackerClientId, out float nextFlush) && Time.time < nextFlush)
        {
            pendingSuppression[attackerClientId] = pending;
            return;
        }

        suppressionFlushTimes[attackerClientId] = Time.time + 0.15f;
        pendingSuppression[attackerClientId] = 0f;

        TargetSuppression(Owner, attackerClientId, pending);
    }

    // Everyone near a blast gets rattled, friend or foe — it is concussion,
    // not suppression, so it ignores teams entirely.
    public static void ServerApplyBlastShake(Vector3 position, float radius)
    {
        if (radius <= 0f)
        {
            return;
        }

        foreach (PlayerNetworkSetup player in FindObjectsByType<PlayerNetworkSetup>(FindObjectsSortMode.None))
        {
            float distance = Vector3.Distance(player.transform.position, position);

            if (distance <= radius)
            {
                player.ServerNotifyBlastShake(1f - distance / radius);
            }
        }
    }

    public void ServerNotifyBlastShake(float strength)
    {
        if (!IsServerInitialized || Owner == null || strength <= 0f)
        {
            return;
        }

        TargetBlastShake(Owner, strength);
    }

    [TargetRpc]
    private void TargetBlastShake(NetworkConnection connection, float strength)
    {
        PlayerSuppression suppression = GetComponent<PlayerSuppression>();

        if (suppression != null)
        {
            suppression.ApplyBlastShake(strength);
        }
    }

    // Headshot death effect: a fixed burst of full suppression, sent whole
    // rather than through the damage-accumulating relay above.
    public void ServerNotifyDeathShock(float seconds)
    {
        if (!IsServerInitialized || Owner == null || seconds <= 0f)
        {
            return;
        }

        TargetDeathShock(Owner, seconds);
    }

    [TargetRpc]
    private void TargetDeathShock(NetworkConnection connection, float seconds)
    {
        PlayerSuppression suppression = GetComponent<PlayerSuppression>();

        if (suppression != null)
        {
            suppression.ApplyDeathShock(seconds);
        }
    }

    // Headshot kill visual: pops the victim's helmet off the soldier model
    // for everyone watching. The owner's own remoteBody is disabled (first
    // person has no visible body), so they are excluded.
    public void ServerNotifyHelmetPop(Vector3 attackDirection)
    {
        if (!IsServerInitialized)
        {
            return;
        }

        ObserversPopHelmet(attackDirection);
    }

    [ObserversRpc(ExcludeOwner = true)]
    private void ObserversPopHelmet(Vector3 attackDirection)
    {
        if (soldierModelHelmet != null)
        {
            soldierModelHelmet.PopOff(attackDirection);
        }
    }

    [TargetRpc]
    private void TargetSuppression(NetworkConnection connection, int attackerClientId, float potentialDamage)
    {
        PlayerSuppression suppression = GetComponent<PlayerSuppression>();

        if (suppression != null)
        {
            suppression.RegisterNearMiss(attackerClientId, potentialDamage);
        }
    }

    // ---- Hit feedback (hit marker + damage number) ----
    // Every source of player-dealt damage routes through here so melee,
    // grenades, fire and structure hits all get the same feedback the rifle
    // already had. Server-only; the marker is drawn on the attacker's client.
    [System.NonSerialized] public HitMarkerUI hitMarkerUI;

    public void ServerReportHit(float damage, bool killed, bool isHeadshot)
    {
        // `this == null` catches a despawned thrower: grenade and fire damage
        // can land after the player who caused it has already been destroyed.
        if (this == null || !IsServerInitialized || damage <= 0f || Owner == null)
        {
            return;
        }

        TargetHitFeedback(Owner, damage, killed, isHeadshot);
    }

    [TargetRpc]
    private void TargetHitFeedback(NetworkConnection connection, float damage, bool killed, bool isHeadshot)
    {
        if (hitMarkerUI == null)
        {
            hitMarkerUI = FindFirstObjectByType<HitMarkerUI>(FindObjectsInactive.Include);
        }

        if (hitMarkerUI != null)
        {
            hitMarkerUI.ShowHitMarker(damage, killed, isHeadshot);
        }
    }

    private bool classApplied;

    public Team AssignedTeam => syncTeam.Value;
    public PlayerClass AssignedClass => syncClass.Value;

    [Header("Owner Only")]
    [Tooltip("First-person camera root. Also holds the AudioListener and weapon view-model. Disabled on non-owned players.")]
    public GameObject cameraRoot;

    [Tooltip("Components that read local input. Disabled on non-owned players.")]
    public Behaviour[] ownerOnlyBehaviours;

    [Header("Remote Only")]
    [Tooltip("Visible body other players see. Hidden on the owned player so it never blocks the first-person view.")]
    public GameObject remoteBody;

    private void Awake()
    {
        // Needed on every instance: the server scans shot lines against all
        // of them, and the owner runs the local effects.
        if (GetComponent<PlayerSuppression>() == null)
        {
            gameObject.AddComponent<PlayerSuppression>();
        }

        syncSpotPulse.OnChange += OnSpotPulseChanged;
    }

    private void OnSpotPulseChanged(int previous, int next, bool asServer)
    {
        if (next > 0)
        {
            spottedRemaining = spotDuration;
        }
    }

    private void Update()
    {
        if (spottedRemaining > 0f)
        {
            spottedRemaining -= Time.deltaTime;
        }

        if (!IsOwner)
        {
            return;
        }

    }

    // Fired from the Flare Gun equipment slot (see PlayerItemSlots), not a
    // bare keypress — the Scout has to actually bring the gun up.
    public bool FlareReady => Time.time - lastFlareTime >= flareCooldown;

    public void TryFireFlare()
    {
        if (!PlayerClasses.Get(AssignedClass).canSpot || !FlareReady)
        {
            return;
        }

        Camera aimCamera = cameraRoot != null ? cameraRoot.GetComponent<Camera>() : null;

        if (aimCamera == null)
        {
            return;
        }

        lastFlareTime = Time.time;

        Ray aimRay = aimCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
        RequestFlare(aimRay.origin + aimRay.direction * 0.4f, aimRay.direction);
    }

    [ServerRpc]
    private void RequestFlare(Vector3 origin, Vector3 direction)
    {
        if (!PlayerClasses.Get(AssignedClass).canSpot)
        {
            return;
        }

        if (Time.time - serverLastFlareTime < flareCooldown * 0.9f)
        {
            return;
        }

        serverLastFlareTime = Time.time;

        // Fired straight down the crosshair — no forced upward bias. Where
        // the flare goes is entirely the player's aim, so lofting one over a
        // ridge is a skill rather than something the gun does for you.
        Vector3 velocity = direction.normalized * flareLaunchSpeed;

        if (FortificationManager.Instance != null)
        {
            FortificationManager.Instance.ServerRunFlare(origin, velocity, AssignedTeam,
                flareBurnSeconds, spotRadius);
        }
    }

    // ---- Grenades (slot 2) ----
    // The owner throws; the server validates, simulates the same
    // deterministic arc every client renders, then explodes: damage +
    // terrain crater for frag-likes, smoke cloud for smoke, area spot for
    // flare.

    // Cooking is tracked on the SERVER, not the client. The client only says
    // "pin pulled" and later "thrown"; the server owns the clock, so nobody
    // can cook a grenade to zero and claim a full fuse — or hold one forever.
    private bool serverCooking;
    private float serverCookStartTime;

    [ServerRpc]
    public void RequestBeginCook()
    {
        PlayerNetworkHealth health = GetComponent<PlayerNetworkHealth>();

        if (health != null && health.State != PlayerLifeState.Alive)
        {
            return;
        }

        if (syncGrenadesLeft.Value <= 0 || serverCooking)
        {
            return;
        }

        serverCooking = true;
        serverCookStartTime = Time.time;
        StartCoroutine(ServerWatchCook());
    }

    // Hold it past the fuse and it goes off in your hand.
    private System.Collections.IEnumerator ServerWatchCook()
    {
        while (serverCooking)
        {
            PlayerNetworkHealth health = GetComponent<PlayerNetworkHealth>();

            // Killed mid-cook: the grenade dies with them rather than
            // rewarding a corpse with a free explosion.
            if (health != null && health.State != PlayerLifeState.Alive)
            {
                serverCooking = false;
                yield break;
            }

            if (Time.time - serverCookStartTime >= GrenadeArc.FuseSeconds)
            {
                serverCooking = false;

                if (syncGrenadesLeft.Value > 0)
                {
                    syncGrenadesLeft.Value--;
                    ServerExplode(transform.position + Vector3.up * 1.1f, AssignedGrenade, AssignedTeam);
                }

                yield break;
            }

            yield return null;
        }
    }

    [ServerRpc]
    public void RequestThrowGrenade(Vector3 origin, Vector3 velocity)
    {
        PlayerNetworkHealth health = GetComponent<PlayerNetworkHealth>();

        if (health != null && health.State != PlayerLifeState.Alive)
        {
            return;
        }

        // The pin has to have been pulled first — this is also what stops a
        // client throwing without ever paying the cook time.
        if (!serverCooking || syncGrenadesLeft.Value <= 0)
        {
            return;
        }

        if (Vector3.Distance(origin, transform.position) > 4f)
        {
            return;
        }

        // Whatever is left of the fuse after however long they held it. The
        // floor keeps a last-instant throw visibly leaving the hand.
        float remainingFuse = Mathf.Max(0.15f, GrenadeArc.FuseSeconds - (Time.time - serverCookStartTime));

        serverCooking = false;
        velocity = Vector3.ClampMagnitude(velocity, 26f);
        syncGrenadesLeft.Value--;

        // Handed to the fortification manager rather than run here: this
        // player may be dead and despawned before the fuse ends.
        FortificationManager manager = FortificationManager.Instance;

        if (manager == null)
        {
            return;
        }

        manager.ObserversWorldGrenadeVisual(origin, velocity, AssignedGrenade, remainingFuse);
        manager.RunPersistentEffect(
            ServerGrenadeFuse(origin, velocity, AssignedGrenade, AssignedTeam, remainingFuse));
    }

    // Real frame time for BOTH stepping and the fuse clock, matching the
    // client visual exactly — a fixed 0.02 step per frame ran the fuse at
    // several times real speed on fast machines, detonating mid-flight.
    private System.Collections.IEnumerator ServerGrenadeFuse(Vector3 position, Vector3 velocity,
        GrenadeType grenadeType, Team throwerTeam, float fuseSeconds)
    {
        float elapsed = 0f;
        bool resting = false;

        while (elapsed < fuseSeconds)
        {
            float deltaTime = Mathf.Min(Time.deltaTime, 0.05f);

            if (!resting)
            {
                resting = GrenadeArc.Step(ref position, ref velocity, deltaTime);
            }

            elapsed += deltaTime;
            yield return null;
        }

        ServerExplode(position, grenadeType, throwerTeam);
    }

    // How much of a target the blast can actually reach, 0 to 1. Cover
    // blocks explosives: three samples up the body, and damage scales with
    // how many of them the blast has a clear line to. Fully behind a wall is
    // zero; only your head showing over sandbags is a third.
    private static readonly RaycastHit[] blastHits = new RaycastHit[16];

    private static float BlastExposure(Vector3 blast, Vector3 targetFeet)
    {
        int clear = 0;

        for (int i = 0; i < 3; i++)
        {
            Vector3 samplePoint = targetFeet + Vector3.up * (0.3f + i * 0.6f);
            Vector3 delta = samplePoint - blast;
            float distance = delta.magnitude;

            if (distance < 0.05f)
            {
                clear++;
                continue;
            }

            int hitCount = Physics.RaycastNonAlloc(blast, delta / distance, blastHits, distance,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);

            bool blocked = false;

            for (int h = 0; h < hitCount; h++)
            {
                // Bodies are not cover — a teammate standing in the way must
                // not shield you from a blast.
                if (blastHits[h].collider.GetComponentInParent<PlayerNetworkSetup>() != null
                    || blastHits[h].collider.GetComponentInParent<HealthComponent>() != null)
                {
                    continue;
                }

                blocked = true;
                break;
            }

            if (!blocked)
            {
                clear++;
            }
        }

        return clear / 3f;
    }

    // May run after this player has been despawned (see the manager's
    // persistent-effect note), so every visual goes out through the manager
    // and anything that talks back to the thrower is null-guarded.
    private void ServerExplode(Vector3 position, GrenadeType grenadeType, Team throwerTeam)
    {
        FortificationManager manager = FortificationManager.Instance;
        bool smoke = grenadeType == GrenadeType.Smoke;

        if (manager != null)
        {
            manager.ObserversWorldExplosionFx(position, smoke);
        }

        if (smoke)
        {
            return;
        }

        if (grenadeType == GrenadeType.Flare)
        {
            // No damage — it pops and burns where it landed, revealing anyone
            // who walks through for as long as it lasts. A small upward kick
            // so it sits above the dirt rather than inside it.
            if (manager != null)
            {
                manager.ServerRunFlare(position + Vector3.up * 0.5f, Vector3.up * 3f, throwerTeam,
                    flareBurnSeconds, grenadeFlareSpotRadius);
            }

            return;
        }

        // Frag / Stick / Incendiary: radial damage with linear falloff.
        // Teammates are spared — but the thrower is NOT: your own grenade at
        // your feet hurts you. Stick trades power for coverage: double the
        // area (radius x1.41) at reduced damage.
        float radius = grenadeDamageRadius;
        float maxDamage = grenadeMaxDamage;

        if (grenadeType == GrenadeType.Stick)
        {
            radius *= 1.41f;
            maxDamage *= 0.6f;
        }

        // Incendiary's threat is the lingering fire, not the blast.
        if (grenadeType == GrenadeType.Incendiary)
        {
            maxDamage *= 0.2f;
        }

        foreach (PlayerNetworkHealth target in FindObjectsByType<PlayerNetworkHealth>(FindObjectsSortMode.None))
        {
            PlayerNetworkSetup targetSetup = target.GetComponent<PlayerNetworkSetup>();

            if (targetSetup == null || (targetSetup.AssignedTeam == throwerTeam && targetSetup != this))
            {
                continue;
            }

            float distance = Vector3.Distance(target.transform.position, position);

            if (distance <= radius)
            {
                float damage = maxDamage * (1f - distance / radius)
                    * BlastExposure(position, target.transform.position);

                if (damage <= 0f)
                {
                    continue;
                }

                bool killed = target.ServerTakeDamage(damage, position);

                // Self-damage is real, but it should not read as "you hit
                // someone" on your own screen.
                if (targetSetup != this)
                {
                    ServerReportHit(damage, killed, false);
                }
            }
        }

        // Practice dummies run the older HealthComponent rather than the
        // networked one, so without this a grenade landing among them does
        // nothing at all — and gives the thrower no damage numbers.
        foreach (HealthComponent dummy in FindObjectsByType<HealthComponent>(FindObjectsSortMode.None))
        {
            if (dummy.GetComponentInParent<PlayerNetworkSetup>() != null || dummy.IsDead)
            {
                continue;
            }

            PlayerTeam dummyTeam = dummy.GetComponentInParent<PlayerTeam>();

            if (dummyTeam != null && dummyTeam.team == throwerTeam)
            {
                continue;
            }

            float dummyDistance = Vector3.Distance(dummy.transform.position, position);

            if (dummyDistance > radius)
            {
                continue;
            }

            float dummyDamage = maxDamage * (1f - dummyDistance / radius)
                * BlastExposure(position, dummy.transform.position);

            if (dummyDamage <= 0f)
            {
                continue;
            }

            bool dummyKilled = dummy.TakeDamage(dummyDamage);
            ServerReportHit(dummyDamage, dummyKilled, false);
        }

        // The jolt carries well past the lethal radius — you feel a shell
        // land near you long before it could have hurt you.
        ServerApplyBlastShake(position, radius * 1.6f);

        if (TerrainDigManager.Instance != null)
        {
            TerrainDigManager.Instance.ServerAddCrater(position);
        }

        if (FortificationManager.Instance != null)
        {
            FortificationManager.Instance.ServerDamageStructuresInRadius(position, radius, maxDamage, DamageType.Explosive, throwerTeam, this);
        }

        // Incendiary leaves a burning patch: contact damage plus a burn
        // debuff that halves all healing.
        if (grenadeType == GrenadeType.Incendiary && manager != null)
        {
            manager.RunPersistentEffect(ServerFireCreep(position, throwerTeam));
            manager.ObserversWorldFireFx(position, fireCreepRadius, fireCreepDuration);
        }
    }

    [Header("Incendiary Fire Creep")]
    public float fireCreepRadius = 4f;
    public float fireCreepDuration = 10f;
    public float fireCreepDamagePerSecond = 10f;
    public float burnDebuffDuration = 5f;

    private System.Collections.IEnumerator ServerFireCreep(Vector3 position, Team throwerTeam)
    {
        float elapsed = 0f;
        const float tick = 0.5f;
        float tickTimer = 0f;

        while (elapsed < fireCreepDuration)
        {
            elapsed += Time.deltaTime;
            tickTimer += Time.deltaTime;

            if (tickTimer >= tick)
            {
                tickTimer = 0f;

                foreach (PlayerNetworkHealth target in FindObjectsByType<PlayerNetworkHealth>(FindObjectsSortMode.None))
                {
                    PlayerNetworkSetup targetSetup = target.GetComponent<PlayerNetworkSetup>();

                    if (targetSetup == null || (targetSetup.AssignedTeam == throwerTeam && targetSetup != this))
                    {
                        continue;
                    }

                    Vector3 flat = target.transform.position - position;
                    flat.y = 0f;

                    // Touching the fire ignites you: the DOT (and halved
                    // healing) lingers for the full burn duration after you
                    // leave, refreshed while you stay inside.
                    if (flat.magnitude <= fireCreepRadius)
                    {
                        // Attributed to this player so the burn DOT's damage
                        // still shows hit markers back on their screen.
                        target.ServerIgnite(fireCreepDamagePerSecond, burnDebuffDuration,
                            targetSetup == this ? null : this);
                    }
                }

                if (FortificationManager.Instance != null)
                {
                    FortificationManager.Instance.ServerDamageStructuresInRadius(
                        position, fireCreepRadius, fireCreepDamagePerSecond * tick, DamageType.Fire, throwerTeam, this);
                }
            }

            yield return null;
        }
    }

    // Explosion, fire and crate visuals deliberately live on
    // FortificationManager instead of here — an RPC on this object cannot
    // fire once the player has despawned, which is exactly when a grenade
    // thrown just before dying needs to go off.

    // ---- Throwable supply crates (gadget slots) ----
    // Support's ammo crate and Medic's med kit are thrown like a grenade;
    // where the box lands, a completed AOE crate structure appears (replacing
    // that player's previous one).

    private float serverNextCrateThrowTime;

    [ServerRpc]
    public void RequestThrowSupplyCrate(Vector3 origin, Vector3 velocity, FortificationType crateType)
    {
        PlayerNetworkHealth health = GetComponent<PlayerNetworkHealth>();

        if (health != null && health.State != PlayerLifeState.Alive)
        {
            return;
        }

        if (!FortificationManager.IsDeployableCrate(crateType))
        {
            return;
        }

        // Only a player actually carrying that equipment can throw it.
        EquipmentType required = EquipmentForCrate(crateType);

        if (AssignedEquipment1 != required && AssignedEquipment2 != required)
        {
            return;
        }

        if (Time.time < serverNextCrateThrowTime || Vector3.Distance(origin, transform.position) > 4f)
        {
            return;
        }

        serverNextCrateThrowTime = Time.time + 5f;
        velocity = Vector3.ClampMagnitude(velocity, 16f);

        // Hosted on the manager: a crate whose thrower despawns mid-flight
        // would otherwise never finalise, leaving a registered, invisible
        // crate dispensing supplies forever at its last airborne position.
        FortificationManager manager = FortificationManager.Instance;

        if (manager == null)
        {
            return;
        }

        manager.ObserversWorldCrateVisual(origin, velocity);
        manager.RunPersistentEffect(ServerCrateLanding(origin, velocity, crateType));
    }

    // Which carried equipment lets you throw a given deployable.
    public static EquipmentType EquipmentForCrate(FortificationType crateType)
    {
        switch (crateType)
        {
            case FortificationType.AmmoCrate: return EquipmentType.AmmoCrate;
            case FortificationType.Toolbox: return EquipmentType.Toolbox;
            default: return EquipmentType.MedicalKit;
        }
    }

    // Which deployable a piece of equipment throws, if any.
    public static FortificationType? CrateForEquipment(EquipmentType equipment)
    {
        switch (equipment)
        {
            case EquipmentType.AmmoCrate: return FortificationType.AmmoCrate;
            case EquipmentType.MedicalKit: return FortificationType.MedCrate;
            case EquipmentType.Toolbox: return FortificationType.Toolbox;
            default: return null;
        }
    }


    private System.Collections.IEnumerator ServerCrateLanding(Vector3 position, Vector3 velocity, FortificationType crateType)
    {
        FortificationManager manager = FortificationManager.Instance;

        if (manager == null)
        {
            yield break;
        }

        // The crate starts dispensing the moment it leaves your hand; its
        // AOE follows the flying box.
        int crateId = manager.ServerCreateThrownCrate(crateType, position, AssignedTeam, OwnerId);

        if (crateId < 0)
        {
            yield break;
        }

        float elapsed = 0f;
        bool resting = false;

        while (!resting && elapsed < 4f)
        {
            float deltaTime = Mathf.Min(Time.deltaTime, 0.05f);
            resting = GrenadeArc.Step(ref position, ref velocity, deltaTime, false);
            elapsed += deltaTime;
            manager.ServerMoveThrownCrate(crateId, position);
            yield return null;
        }

        manager.ServerFinalizeThrownCrate(crateId, position);
    }

    // Server: mark this player as spotted for the given team.
    public void ServerApplySpotted(Team spottingTeam)
    {
        syncSpottedByTeam.Value = spottingTeam;
        syncSpotPulse.Value = syncSpotPulse.Value + 1;
        spottedRemaining = spotDuration;
    }

    // The flare's own light and arc live in FlareVisual, spawned by the
    // fortification manager so it outlives whoever fired it.

    private void OnGUI()
    {
        DrawSpottedMarker();
        DrawFlareCooldown();
    }

    // Red marker drawn over this player for enemies of the spotting team —
    // screen-space, so it shows through walls.
    private void DrawSpottedMarker()
    {
        if (spottedRemaining <= 0f || IsOwner)
        {
            return;
        }

        if (ClassSelectHud.LastKnownTeam != syncSpottedByTeam.Value)
        {
            return;
        }

        PlayerNetworkHealth targetHealth = GetComponent<PlayerNetworkHealth>();

        if (targetHealth != null && targetHealth.IsDead)
        {
            return;
        }

        Camera viewer = LocalPlayerCamera;

        if (viewer == null || !viewer.isActiveAndEnabled)
        {
            return;
        }

        Vector3 screenPoint = viewer.WorldToScreenPoint(transform.position + Vector3.up * 2.2f);

        if (screenPoint.z <= 0f)
        {
            return;
        }

        GUIStyle markerStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 22,
            fontStyle = FontStyle.Bold
        };
        markerStyle.normal.textColor = new Color(1f, 0.15f, 0.1f);

        float x = screenPoint.x;
        float y = Screen.height - screenPoint.y;

        GUI.Label(new Rect(x - 40f, y - 16f, 80f, 24f), "▼", markerStyle);

        GUIStyle distanceStyle = new GUIStyle(markerStyle) { fontSize = 11 };
        float distance = Vector3.Distance(viewer.transform.position, transform.position);
        GUI.Label(new Rect(x - 40f, y + 6f, 80f, 16f), Mathf.RoundToInt(distance) + "m", distanceStyle);
    }

    private void DrawFlareCooldown()
    {
        if (!IsOwner || !PlayerClasses.Get(AssignedClass).canSpot)
        {
            return;
        }

        float remaining = flareCooldown - (Time.time - lastFlareTime);

        GUIStyle style = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            alignment = TextAnchor.LowerRight
        };
        style.normal.textColor = remaining <= 0f
            ? new Color(0.6f, 1f, 0.6f, 0.85f)
            : new Color(1f, 1f, 1f, 0.6f);

        string text = remaining <= 0f
            ? "FLARE GUN (G): READY"
            : "FLARE GUN (G): " + Mathf.CeilToInt(remaining) + "s";

        GUI.Label(new Rect(0f, Screen.height - 58f, Screen.width - 16f, 44f), text, style);
    }

    public override void OnStartServer()
    {
        base.OnStartServer();

        Team team = pendingTeam;

        if (team == Team.Neutral)
        {
            // Fallback for spawns that bypassed ClassSpawnManager.
            team = serverTeamAssignCounter % 2 == 0 ? Team.AlliedPowers : Team.CentralPowers;
            serverTeamAssignCounter++;
        }

        syncTeam.Value = team;
        syncClass.Value = pendingClass;
        syncWeapon.Value = pendingWeapon;
        syncGrenade.Value = pendingGrenade;
        syncEquipment1.Value = pendingEquipment1;
        syncEquipment2.Value = pendingEquipment2;
        syncGrenadesLeft.Value = grenadesPerLife;

        ApplyTeam(team);
        ApplyClass();
    }

    public override void OnStartClient()
    {
        base.OnStartClient();

        ApplyTeam(syncTeam.Value);
        ApplyClass();

        // Footsteps for everyone, driven from replicated movement.
        if (GetComponent<FootstepAudio>() == null)
        {
            gameObject.AddComponent<FootstepAudio>();
        }

        if (IsOwner)
        {
            // Remember the team so the spawn selector on the next death knows
            // which objectives are friendly.
            ClassSelectHud.LastKnownTeam = syncTeam.Value;

            if (remoteBody != null)
            {
                remoteBody.SetActive(false);
            }

            WireLocalPlayerUI();
            return;
        }

        if (cameraRoot != null)
        {
            cameraRoot.SetActive(false);
        }

        foreach (Behaviour behaviour in ownerOnlyBehaviours)
        {
            if (behaviour != null)
            {
                behaviour.enabled = false;
            }
        }
    }

    private void ApplyTeam(Team team)
    {
        PlayerTeam localTeam = GetComponent<PlayerTeam>();

        if (localTeam != null)
        {
            localTeam.team = team;
        }

        if (remoteBody == null)
        {
            return;
        }

        if (useSoldierModelFromDummies)
        {
            EnsureSoldierModel(team);
        }

        if (!soldierModelCreated)
        {
            // Capsule fallback: tint only the capsule's own renderer, never a
            // cloned soldier model.
            Renderer bodyRenderer = remoteBody.GetComponent<Renderer>();

            if (bodyRenderer != null)
            {
                bodyRenderer.material.color = team == Team.AlliedPowers ? alliedBodyColor : centralBodyColor;

                if (boostEnemyContrast)
                {
                    StartCoroutine(ApplyEnemyContrastWhenReady(bodyRenderer.gameObject, team));
                }
            }
        }
    }

    // Waits until this client knows its OWN player's team (spawn order
    // between local and remote players is not guaranteed), then boosts
    // saturation/brightness on the target's renderers if — from THIS
    // viewer's perspective — the target is an enemy. Purely a local visual
    // tweak; never touches shared materials or replicated state.
    private IEnumerator ApplyEnemyContrastWhenReady(GameObject visualRoot, Team targetTeam)
    {
        // Generous timeout: a joining client can sit on the class-select
        // screen (still no owned player object) well after other players'
        // models have already spawned in around them.
        float timeoutAt = Time.time + 60f;
        Team? localTeam = null;

        while (localTeam == null && Time.time < timeoutAt)
        {
            localTeam = FindLocalViewerTeam();

            if (localTeam == null)
            {
                yield return null;
            }
        }

        if (localTeam == null || localTeam.Value == Team.Neutral || targetTeam == Team.Neutral
            || localTeam.Value == targetTeam)
        {
            yield break;
        }

        BoostRendererContrast(visualRoot);
    }

    private static Team? FindLocalViewerTeam()
    {
        foreach (PlayerNetworkSetup setup in FindObjectsByType<PlayerNetworkSetup>(FindObjectsSortMode.None))
        {
            if (setup.IsOwner)
            {
                return setup.AssignedTeam;
            }
        }

        return null;
    }

    private void BoostRendererContrast(GameObject visualRoot)
    {
        foreach (Renderer renderer in visualRoot.GetComponentsInChildren<Renderer>(true))
        {
            Material[] materials = renderer.materials; // instantiates per-renderer copies

            for (int i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];

                string colorProperty = material.HasProperty("_BaseColor") ? "_BaseColor"
                    : material.HasProperty("_Color") ? "_Color" : null;

                if (colorProperty == null)
                {
                    continue;
                }

                Color original = material.GetColor(colorProperty);
                Color.RGBToHSV(original, out float hue, out float saturation, out float value);

                saturation = Mathf.Clamp01(saturation * enemyContrastSaturationMultiplier + enemyContrastSaturationAdd);
                value = Mathf.Clamp01(value * enemyContrastValueMultiplier + enemyContrastValueAdd);

                Color boosted = Color.HSVToRGB(hue, saturation, value);
                boosted.a = original.a;
                material.SetColor(colorProperty, boosted);
            }

            renderer.materials = materials;
        }
    }

    // The scene dummies already carry fully set up soldier rigs from the WW1
    // asset pack, including bone-attached hitboxes. Clone the rig of a dummy
    // on the matching team under RemoteBody so remote players show the right
    // uniform and take hitbox-multiplied damage.
    private void EnsureSoldierModel(Team team)
    {
        if (soldierModelCreated)
        {
            return;
        }

        HealthComponent dummySource = FindDummySource(team);

        if (dummySource == null)
        {
            return;
        }

        // The non-uniform capsule scale compensation below has to live on a
        // wrapper that never rotates. Rotating the same transform that also
        // carries a non-uniform scale (the downed pose does exactly this)
        // shears the model — reported as bodies going short and wide when
        // downed. The rig itself gets its own scale of (1,1,1) and is what
        // rotates for the downed pose.
        GameObject scaleAnchor = new GameObject("SoldierModel");
        scaleAnchor.transform.SetParent(remoteBody.transform, false);

        // Clone the WHOLE dummy so the skinned meshes keep their bone
        // references (skeleton and LOD meshes are separate siblings; cloning
        // only one of them leaves the copy skinned to the original dummy).
        GameObject clone = Instantiate(dummySource.gameObject, scaleAnchor.transform);
        clone.name = "SoldierModelRig";
        clone.SetActive(true);

        // Strip the dummy's gameplay from the copy — the player's networked
        // components own all of that.
        Destroy(clone.GetComponent<HealthComponent>());

        PlayerTeam cloneTeam = clone.GetComponent<PlayerTeam>();

        if (cloneTeam != null)
        {
            Destroy(cloneTeam);
        }

        foreach (DownedWorldMarker marker in clone.GetComponentsInChildren<DownedWorldMarker>(true))
        {
            Destroy(marker.gameObject);
        }

        soldierModelHelmet = clone.GetComponentInChildren<HelmetPopOff>(true);

        // Any OTHER rigidbodies in the copy would fall or fight the player's
        // movement, so the hitbox colliders join RemoteBody's kinematic
        // rigidbody as a compound. The helmet keeps its own rigidbody —
        // HelmetPopOff holds it kinematic while attached and only frees it
        // for the pop-off launch, so it never fights player movement.
        foreach (Rigidbody body in clone.GetComponentsInChildren<Rigidbody>(true))
        {
            if (soldierModelHelmet != null && soldierModelHelmet.helmet != null
                && body.transform == soldierModelHelmet.helmet)
            {
                continue;
            }

            Destroy(body);
        }

        // RemoteBody is scaled and offset for the capsule; counteract both so
        // the soldier stands at the player's feet with its authored scale.
        // The compensation goes on the non-rotating anchor, not the rig.
        Transform cloneTransform = clone.transform;
        Vector3 parentScale = remoteBody.transform.lossyScale;
        Vector3 sourceScale = dummySource.transform.lossyScale;
        scaleAnchor.transform.localScale = new Vector3(
            parentScale.x == 0f ? 1f : sourceScale.x / parentScale.x,
            parentScale.y == 0f ? 1f : sourceScale.y / parentScale.y,
            parentScale.z == 0f ? 1f : sourceScale.z / parentScale.z);
        cloneTransform.localScale = Vector3.one;
        cloneTransform.rotation = transform.rotation;
        cloneTransform.position = transform.position;

        // The dummy's pivot is not at its feet; measure the renderers and
        // raise the clone so its lowest point sits at the player's feet.
        Bounds modelBounds = new Bounds(cloneTransform.position, Vector3.zero);
        bool hasBounds = false;

        foreach (Renderer modelRenderer in clone.GetComponentsInChildren<Renderer>(false))
        {
            if (!hasBounds)
            {
                modelBounds = modelRenderer.bounds;
                hasBounds = true;
            }
            else
            {
                modelBounds.Encapsulate(modelRenderer.bounds);
            }
        }

        if (hasBounds)
        {
            float sink = transform.position.y - modelBounds.min.y;
            cloneTransform.position += Vector3.up * sink;
        }

        // Animate the rig: assign the idle/run controller and a driver that
        // reads the player's actual movement.
        Animator rigAnimator = clone.GetComponentInChildren<Animator>(true);

        if (rigAnimator == null)
        {
            // The dummy rig ships without an Animator; add one on the rig
            // root (the subtree that owns the bones) with the soldier avatar.
            SkinnedMeshRenderer rigMesh = clone.GetComponentInChildren<SkinnedMeshRenderer>(true);

            if (rigMesh != null && soldierAvatar != null)
            {
                Transform rigRoot = rigMesh.rootBone != null ? rigMesh.rootBone : rigMesh.transform;

                while (rigRoot.parent != null && rigRoot.parent != clone.transform)
                {
                    rigRoot = rigRoot.parent;
                }

                rigAnimator = rigRoot.gameObject.AddComponent<Animator>();
                rigAnimator.avatar = soldierAvatar;
            }
        }

        if (rigAnimator != null && soldierAnimatorController != null)
        {
            rigAnimator.runtimeAnimatorController = soldierAnimatorController;
            rigAnimator.applyRootMotion = false;
            rigAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            SoldierAnimationDriver driver = clone.AddComponent<SoldierAnimationDriver>();
            driver.animator = rigAnimator;
            driver.movementRoot = transform;
        }
        else
        {
            Debug.LogWarning("[PlayerNetworkSetup] Soldier animation not applied. Animator: "
                + (rigAnimator != null) + ", controller: " + (soldierAnimatorController != null)
                + ", avatar: " + (soldierAvatar != null));
        }

        // The rig's hitboxes take over bullet, revive, and zone-trigger
        // duties; hide and disable the capsule.
        MeshRenderer capsuleRenderer = remoteBody.GetComponent<MeshRenderer>();

        if (capsuleRenderer != null)
        {
            capsuleRenderer.enabled = false;
        }

        CapsuleCollider capsuleCollider = remoteBody.GetComponent<CapsuleCollider>();

        if (capsuleCollider != null)
        {
            capsuleCollider.enabled = false;
        }

        soldierModelCreated = true;

        if (boostEnemyContrast)
        {
            StartCoroutine(ApplyEnemyContrastWhenReady(clone, team));
        }
    }

    private HealthComponent FindDummySource(Team team)
    {
        foreach (HealthComponent dummy in FindObjectsByType<HealthComponent>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            // Never clone from another player.
            if (dummy.GetComponentInParent<PlayerNetworkSetup>() != null)
            {
                continue;
            }

            PlayerTeam dummyTeam = dummy.GetComponentInParent<PlayerTeam>();

            if (dummyTeam == null || dummyTeam.team != team)
            {
                continue;
            }

            if (dummy.GetComponentInChildren<SkinnedMeshRenderer>(true) == null)
            {
                continue;
            }

            return dummy;
        }

        return null;
    }

    // Applies the chosen class's stats to this instance. Runs once per
    // instance (the host reaches it from both server and client callbacks).
    private void ApplyClass()
    {
        if (classApplied)
        {
            return;
        }

        classApplied = true;

        PlayerClassDefinition definition = PlayerClasses.Get(syncClass.Value);

        PlayerNetworkHealth health = GetComponent<PlayerNetworkHealth>();

        if (health != null)
        {
            health.maxHealth = definition.maxHealth;
            health.ServerResetHealthToMax();
        }

        BoltActionRifle rifle = GetComponent<BoltActionRifle>();

        if (rifle != null)
        {
            // Reserve ammo is weapon-defined inside the profile.
            rifle.ApplyWeaponProfile(WeaponProfiles.Get(syncWeapon.Value));
            ApplyWeaponViewModel(rifle, syncWeapon.Value);
        }

        PlayerController controller = GetComponent<PlayerController>();

        if (controller != null && !Mathf.Approximately(definition.moveSpeedMultiplier, 1f))
        {
            controller.walkSpeed *= definition.moveSpeedMultiplier;
            controller.sprintSpeed *= definition.moveSpeedMultiplier;
        }
    }

    // Re-applies the view model state (used by the item-slot system after it
    // re-enables weapon renderers on switching back to the rifle).
    public void RefreshWeaponViewModel()
    {
        BoltActionRifle rifle = GetComponent<BoltActionRifle>();

        if (rifle != null)
        {
            ApplyWeaponViewModel(rifle, syncWeapon.Value);
        }
    }

    // Swap the held weapon model for weapons that have a dedicated view
    // model (LMG, pistol). The default rifle model is hidden, not destroyed,
    // so weapons without an entry keep it.
    private void ApplyWeaponViewModel(BoltActionRifle rifle, WeaponId weapon)
    {
        if (rifle.weaponHolder == null)
        {
            return;
        }

        Transform existing = rifle.weaponHolder.Find("WeaponViewModel");

        if (existing != null)
        {
            Destroy(existing.gameObject);
        }

        WeaponVisuals visuals = WeaponVisuals.Load();
        GameObject prefab = visuals != null ? visuals.GetModel(weapon) : null;

        foreach (Renderer partRenderer in rifle.weaponHolder.GetComponentsInChildren<Renderer>(true))
        {
            partRenderer.enabled = prefab == null;
        }

        if (prefab == null)
        {
            return;
        }

        GameObject model = Instantiate(prefab, rifle.weaponHolder);
        model.name = "WeaponViewModel";
        model.transform.localPosition = Vector3.zero;
        model.transform.localRotation = Quaternion.identity;
        model.transform.localScale = Vector3.one * visuals.GetModelScale(weapon);

        foreach (Collider modelCollider in model.GetComponentsInChildren<Collider>(true))
        {
            Destroy(modelCollider);
        }

        // Each model needs its own ADS pose so the iron sights line up
        // (tunable on the WeaponVisuals asset in Resources).
        if (visuals.TryGetAimPose(weapon, out Vector3 aimPosition, out Vector3 aimEuler))
        {
            rifle.aimWeaponLocalPosition = aimPosition;
            rifle.aimWeaponLocalRotation = aimEuler;
        }
    }

    public void MoveOwnerToTeamSpawn()
    {
        if (!IsOwner)
        {
            return;
        }

        PlayerTeam localTeam = GetComponent<PlayerTeam>();

        if (localTeam == null)
        {
            return;
        }

        Transform spawnPoint = TeamSpawnArea.GetSpawnPoint(localTeam.team);

        if (spawnPoint == null)
        {
            return;
        }

        // The CharacterController caches its position; it must be disabled
        // for a teleport to stick.
        CharacterController controller = GetComponent<CharacterController>();

        if (controller != null)
        {
            controller.enabled = false;
        }

        transform.position = spawnPoint.position;
        transform.rotation = Quaternion.Euler(0f, spawnPoint.eulerAngles.y, 0f);

        if (controller != null)
        {
            controller.enabled = true;
        }
    }

    // Scene UI kept Inspector references to the pre-networking scene Player.
    // A spawned prefab cannot carry those, so the owned player assigns itself
    // into the scene UI when it spawns. Replaced per-phase as UI becomes networked.
    private void WireLocalPlayerUI()
    {
        BoltActionRifle rifle = GetComponent<BoltActionRifle>();
        PlayerTeam team = GetComponent<PlayerTeam>();
        Camera playerCamera = cameraRoot != null ? cameraRoot.GetComponent<Camera>() : null;

        LocalPlayerCamera = playerCamera;

        // Everyone carries a shovel and can build/repair (hold F); placement
        // options inside are gated by class (Engineer structures, Support
        // ammo box, Medic med box).
        if (GetComponent<FortificationBuilder>() == null)
        {
            FortificationBuilder builder = gameObject.AddComponent<FortificationBuilder>();
            builder.playerCamera = playerCamera;
        }

        if (GetComponent<PauseMenu>() == null)
        {
            gameObject.AddComponent<PauseMenu>();
        }

        if (GetComponent<PlayerItemSlots>() == null)
        {
            gameObject.AddComponent<PlayerItemSlots>();
        }

        // Shared by every damage source (melee, grenades, fire, structures).
        hitMarkerUI = FindFirstObjectByType<HitMarkerUI>(FindObjectsInactive.Include);

        if (rifle != null)
        {
            rifle.hitMarkerUI = hitMarkerUI;

            AmmoUI ammoUI = FindFirstObjectByType<AmmoUI>(FindObjectsInactive.Include);
            if (ammoUI != null)
            {
                ammoUI.rifle = rifle;
            }
        }

        CrosshairUI crosshairUI = FindFirstObjectByType<CrosshairUI>(FindObjectsInactive.Include);
        if (crosshairUI != null)
        {
            crosshairUI.rifle = rifle;
            crosshairUI.playerCamera = playerCamera;
        }

        ReviveInteractor reviveInteractor = FindFirstObjectByType<ReviveInteractor>(FindObjectsInactive.Include);
        if (reviveInteractor != null)
        {
            reviveInteractor.playerCamera = playerCamera;
            reviveInteractor.localPlayerTeam = team;
        }

        foreach (ObjectiveUI ui in FindObjectsByType<ObjectiveUI>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            ui.localPlayerTeam = team;
        }

        foreach (ObjectiveHUDLayout ui in FindObjectsByType<ObjectiveHUDLayout>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            ui.localPlayerTeam = team;
        }

        foreach (ObjectiveMessageUI ui in FindObjectsByType<ObjectiveMessageUI>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            ui.localPlayerTeam = team;
        }

        foreach (TeamTicketUI ui in FindObjectsByType<TeamTicketUI>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            ui.localPlayerTeam = team;
        }

        foreach (DownedWorldMarker marker in FindObjectsByType<DownedWorldMarker>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            marker.localPlayerTeam = team;
            marker.playerCamera = playerCamera;
        }
    }
}
