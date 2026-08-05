using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;
using UnityEngine.InputSystem;

public enum PlayerLifeState : byte
{
    Alive = 0,
    Downed = 1,
    Dead = 2
}

// Server-authoritative health, downed, bleedout, give-up, and respawn for
// networked players. Practice dummies keep the original HealthComponent.
public class PlayerNetworkHealth : NetworkBehaviour
{
    [Header("Health")]
    public float maxHealth = 100f;

    [Header("Downed")]
    public bool useDownedState = true;
    [Tooltip("Second health pool granted on going down. Damage overflowing past 0 HP carries into it; burning through both in one hit (e.g. a 200-damage headshot) kills outright and skips the downed state.")]
    // Downed buffer: overflow past standing HP eats into this; 100 standing
    // + 50 buffer means 150+ total damage from full health is a no-revive
    // full kill.
    public float downedHealth = 50f;
    public float bleedOutTime = 30f;
    public float giveUpDelay = 5f;

    [Header("Revive")]
    public float reviveHealth = 50f;
    [Tooltip("Server-side validation range for a revive request. Keep a little above the interactor's revive distance.")]
    public float maxReviveDistance = 4.5f;

    [Header("Respawn")]
    public float respawnDelay = 3f;

    [Header("Spawn Protection")]
    [Tooltip("Seconds of invulnerability after spawning. Firing your weapon ends it early.")]
    public float spawnProtectionDuration = 4f;

    [Header("Hit Feedback")]
    public Color damageFlashColor = new Color(0.8f, 0.05f, 0.05f, 0.4f);
    public float damageFlashFadeTime = 0.45f;

    [Tooltip("Ignore health drops smaller than this so replication jitter can never read as a hit.")]
    public float damageFlashMinimum = 0.5f;

    [Header("Heal Feedback")]
    [Tooltip("Soft warm glow around the screen edges while healing. Should read as relief, not as a hit.")]
    public Color healFlashColor = new Color(1f, 0.96f, 0.78f, 0.32f);

    [Tooltip("Long enough that back-to-back heal ticks blend into one steady glow instead of strobing.")]
    public float healFlashFadeTime = 1.2f;

    [Header("Damage Direction")]
    public float damageDirectionDuration = 1.6f;
    public Color damageDirectionColor = new Color(1f, 0.25f, 0.2f, 0.9f);

    [Header("Downed Pose")]
    public Vector3 downedBodyLocalPosition = new Vector3(0f, 0.35f, 0f);
    public Vector3 downedBodyLocalEulerAngles = new Vector3(90f, 0f, 0f);
    public float downedCameraHeight = 0.5f;

    private readonly SyncVar<float> syncHealth = new SyncVar<float>();
    private readonly SyncVar<PlayerLifeState> syncState = new SyncVar<PlayerLifeState>();
    private readonly SyncVar<float> syncBleedOut = new SyncVar<float>();

    private PlayerNetworkSetup setup;
    private PlayerController playerController;
    private BoltActionRifle rifle;
    private PlayerTeam playerTeam;

    private Vector3 standingBodyLocalPosition;
    private Quaternion standingBodyLocalRotation;
    private Vector3 standingCameraLocalPosition;

    private float serverBleedOutRemaining;
    private float serverRespawnTimer;
    private float serverSpawnProtectedUntil;
    private float ownerSpawnTime;
    private float damageFlashStrength;
    private float healFlashStrength;

    // Recent damage sources, for the directional indicators.
    private readonly List<Vector3> damageDirections = new List<Vector3>();
    private readonly List<float> damageDirectionTimes = new List<float>();

    public PlayerLifeState State => syncState.Value;
    public bool IsDowned => State == PlayerLifeState.Downed;
    public bool IsDead => State == PlayerLifeState.Dead;
    public float CurrentHealth => syncHealth.Value;
    public float BleedOutRemaining => syncBleedOut.Value;
    public float BleedOutProgress01 => bleedOutTime <= 0f ? 0f : Mathf.Clamp01(syncBleedOut.Value / bleedOutTime);
    public float DownedElapsed => IsDowned ? Mathf.Max(0f, bleedOutTime - syncBleedOut.Value) : 0f;

    private void Awake()
    {
        setup = GetComponent<PlayerNetworkSetup>();
        playerController = GetComponent<PlayerController>();
        rifle = GetComponent<BoltActionRifle>();
        playerTeam = GetComponent<PlayerTeam>();

        if (setup != null && setup.remoteBody != null)
        {
            standingBodyLocalPosition = setup.remoteBody.transform.localPosition;
            standingBodyLocalRotation = setup.remoteBody.transform.localRotation;
        }

        if (setup != null && setup.cameraRoot != null)
        {
            standingCameraLocalPosition = setup.cameraRoot.transform.localPosition;
        }

        syncState.OnChange += OnStateChanged;
        syncHealth.OnChange += OnHealthSynced;

        EnsureDownedMarker();
    }

    // The scene's practice dummies already carry a fully built DownedWorldMarker
    // hierarchy. Clone one under this player at runtime and rebind it, so the
    // prefab needs no hand-maintained marker copy.
    private void EnsureDownedMarker()
    {
        if (GetComponentInChildren<DownedWorldMarker>(true) != null)
        {
            return;
        }

        DownedWorldMarker template = null;

        foreach (DownedWorldMarker candidate in FindObjectsByType<DownedWorldMarker>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (candidate.GetComponentInParent<PlayerNetworkHealth>() == null)
            {
                template = candidate;
                break;
            }
        }

        if (template == null)
        {
            return;
        }

        DownedWorldMarker marker = Instantiate(template.gameObject, transform).GetComponent<DownedWorldMarker>();
        marker.name = "DownedWorldMarker";
        marker.health = null;
        marker.playerHealth = this;
        marker.targetTeam = playerTeam;
        marker.localPlayerTeam = null;
        marker.playerCamera = null;
        marker.markerAnchor = null;
        marker.anchorOffset = new Vector3(0f, 0.7f, 0f);
    }

    public override void OnStartServer()
    {
        base.OnStartServer();

        syncHealth.Value = maxHealth;
        syncState.Value = PlayerLifeState.Alive;
        serverSpawnProtectedUntil = Time.time + spawnProtectionDuration;
    }

    public override void OnStartClient()
    {
        base.OnStartClient();

        if (IsOwner)
        {
            ownerSpawnTime = Time.time;
        }
    }

    private void OnHealthSynced(float previous, float next, bool asServer)
    {
        if (asServer || !IsOwner)
        {
            return;
        }

        // Thresholded so tiny replication deltas cannot masquerade as a hit
        // and fire the red flash while you are actually being healed.
        if (previous - next >= damageFlashMinimum)
        {
            damageFlashStrength = 1f;
        }
        else if (next > previous)
        {
            healFlashStrength = 1f;

            // Only real treatment gets a sound. The 1 HP/s passive regen
            // would otherwise chime once a second, forever.
            if (next - previous >= 2f)
            {
                ProceduralAudio.PlayAt(ProceduralAudio.HealTick, transform.position, 0.4f);
            }
        }
    }

    // Damage with a known origin, so the victim can be shown where it came
    // from. Sources that have no meaningful position (bleedout, suicide)
    // just call ServerTakeDamage directly.
    public bool ServerTakeDamage(float damage, Vector3 sourcePosition)
    {
        bool killed = ServerTakeDamage(damage);

        if (IsServerInitialized && Owner != null)
        {
            TargetDamageFrom(Owner, sourcePosition);
        }

        return killed;
    }

    [TargetRpc]
    private void TargetDamageFrom(NetworkConnection connection, Vector3 sourcePosition)
    {
        damageDirections.Add(sourcePosition);
        damageDirectionTimes.Add(Time.time);
    }

    // Radial falloff mask built once and stretched over the screen:
    // transparent in the middle, solid at the edges. Generated rather than
    // authored so no texture asset has to be imported and wired up.
    private static Texture2D vignetteTexture;

    private static Texture2D VignetteTexture()
    {
        if (vignetteTexture != null)
        {
            return vignetteTexture;
        }

        const int size = 64;
        vignetteTexture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                // Distance from centre, 0 in the middle and 1 at the edges.
                float nx = x / (size - 1f) * 2f - 1f;
                float ny = y / (size - 1f) * 2f - 1f;
                float distance = Mathf.Sqrt(nx * nx + ny * ny) / Mathf.Sqrt(2f);

                // Nothing at all until well out from the centre, then a
                // smooth ramp into the corners.
                float alpha = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.42f, 1f, distance));
                vignetteTexture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
        }

        vignetteTexture.Apply();
        return vignetteTexture;
    }

    // Red arcs around the crosshair pointing at whoever is hitting you.
    private void DrawDamageDirections()
    {
        Camera camera = setup != null && setup.cameraRoot != null
            ? setup.cameraRoot.GetComponentInChildren<Camera>()
            : null;

        for (int i = damageDirectionTimes.Count - 1; i >= 0; i--)
        {
            if (Time.time - damageDirectionTimes[i] > damageDirectionDuration)
            {
                damageDirectionTimes.RemoveAt(i);
                damageDirections.RemoveAt(i);
            }
        }

        if (camera == null || damageDirections.Count == 0)
        {
            return;
        }

        Vector2 center = new Vector2(GuiScale.Width * 0.5f, GuiScale.Height * 0.5f);
        Matrix4x4 savedMatrix = GUI.matrix;

        for (int i = 0; i < damageDirections.Count; i++)
        {
            Vector3 toSource = damageDirections[i] - transform.position;
            toSource.y = 0f;

            if (toSource.sqrMagnitude < 0.01f)
            {
                continue;
            }

            // Angle of the hit relative to where the player is facing, so the
            // marker sits where they need to turn.
            Vector3 forward = camera.transform.forward;
            forward.y = 0f;

            float angle = Vector3.SignedAngle(forward.normalized, toSource.normalized, Vector3.up);
            float age01 = Mathf.Clamp01((Time.time - damageDirectionTimes[i]) / damageDirectionDuration);

            Color color = damageDirectionColor;
            color.a *= 1f - age01;

            GUI.matrix = savedMatrix;
            GUIUtility.RotateAroundPivot(angle, center);

            GUI.color = color;
            GUI.DrawTexture(new Rect(center.x - 26f, center.y - 108f, 52f, 9f), Texture2D.whiteTexture);
        }

        GUI.matrix = savedMatrix;
        GUI.color = Color.white;
    }

    // Called by the rifle when the owner fires: shooting ends protection.
    public void ServerCancelSpawnProtection()
    {
        serverSpawnProtectedUntil = 0f;
    }

    // Owner-requested suicide (pause menu). Bypasses spawn protection and
    // both health pools, so it is always a full death.
    [ServerRpc]
    public void RequestSuicide()
    {
        serverSpawnProtectedUntil = 0f;
        ServerTakeDamage(maxHealth + downedHealth + 100f);
    }

    // Burn (incendiary): a damage-over-time that lingers after leaving the
    // fire, plus halved healing while it lasts.
    private float serverBurnedUntil;
    private float serverBurnDps;
    private float burnDamageAccumulator;

    // Who lit this player up, so the DOT's damage still reports hit markers
    // to them. Null for self-inflicted burns.
    private PlayerNetworkSetup serverBurnSource;

    public void ServerIgnite(float damagePerSecond, float duration, PlayerNetworkSetup source = null)
    {
        serverBurnedUntil = Mathf.Max(serverBurnedUntil, Time.time + duration);
        serverBurnDps = Mathf.Max(serverBurnDps, damagePerSecond);
        serverBurnSource = source;
    }

    [Header("Passive Regen")]
    public float passiveRegenPerSecond = 1f;
    private float regenAccumulator;

    // Server-side healing from med crates. Only tops up living players.
    public void ServerHeal(float amount)
    {
        if (!IsServerInitialized || syncState.Value != PlayerLifeState.Alive)
        {
            return;
        }

        if (Time.time < serverBurnedUntil)
        {
            amount *= 0.5f;
        }

        if (syncHealth.Value >= maxHealth)
        {
            return;
        }

        syncHealth.Value = Mathf.Min(maxHealth, syncHealth.Value + amount);
    }

    private void Update()
    {
        if (IsServerInitialized)
        {
            // Small passive regen, applied in whole points once per second
            // (goes through ServerHeal so the burn debuff halves it).
            if (syncState.Value == PlayerLifeState.Alive && syncHealth.Value < maxHealth)
            {
                regenAccumulator += passiveRegenPerSecond * Time.deltaTime;

                if (regenAccumulator >= 1f)
                {
                    regenAccumulator -= 1f;
                    ServerHeal(1f);
                }
            }

            // Burn DOT: keeps ticking for its full duration after leaving
            // the fire, in half-second bites.
            if (syncState.Value == PlayerLifeState.Alive && Time.time < serverBurnedUntil && serverBurnDps > 0f)
            {
                burnDamageAccumulator += serverBurnDps * Time.deltaTime;

                if (burnDamageAccumulator >= serverBurnDps * 0.5f)
                {
                    float burnTick = burnDamageAccumulator;
                    burnDamageAccumulator = 0f;

                    bool killed = ServerTakeDamage(burnTick);

                    if (serverBurnSource != null)
                    {
                        serverBurnSource.ServerReportHit(burnTick, killed, false);
                    }
                }
            }
            else if (Time.time >= serverBurnedUntil)
            {
                serverBurnDps = 0f;
                burnDamageAccumulator = 0f;
                serverBurnSource = null;
            }

            ServerUpdate();
        }

        if (IsOwner)
        {
            OwnerUpdate();

            if (damageFlashStrength > 0f)
            {
                damageFlashStrength = Mathf.MoveTowards(
                    damageFlashStrength, 0f, Time.deltaTime / Mathf.Max(0.05f, damageFlashFadeTime));
            }

            if (healFlashStrength > 0f)
            {
                healFlashStrength = Mathf.MoveTowards(
                    healFlashStrength, 0f, Time.deltaTime / Mathf.Max(0.05f, healFlashFadeTime));
            }
        }
    }

    // ---- Server ----

    private void ServerUpdate()
    {
        if (syncState.Value == PlayerLifeState.Downed)
        {
            serverBleedOutRemaining -= Time.deltaTime;
            syncBleedOut.Value = Mathf.Max(0f, serverBleedOutRemaining);

            if (serverBleedOutRemaining <= 0f)
            {
                ServerFullDie("Bleedout");
            }
        }
        else if (syncState.Value == PlayerLifeState.Dead)
        {
            serverRespawnTimer -= Time.deltaTime;

            if (serverRespawnTimer <= 0f)
            {
                ServerRespawn();
            }
        }
    }

    // Returns true when this damage fully killed the player. While downed,
    // health holds the downed pool. Overflow damage past 0 HP carries into
    // that pool, so a single hit strong enough to burn through both pools
    // kills outright and skips the downed state.
    public bool ServerTakeDamage(float damage)
    {
        if (!IsServerInitialized || damage <= 0f)
        {
            return false;
        }

        if (syncState.Value == PlayerLifeState.Dead)
        {
            return false;
        }

        if (Time.time < serverSpawnProtectedUntil)
        {
            return false;
        }

        if (syncState.Value == PlayerLifeState.Downed)
        {
            float remainingPool = syncHealth.Value - damage;

            if (remainingPool <= 0f)
            {
                ServerFullDie("Finished while downed");
                return true;
            }

            syncHealth.Value = remainingPool;
            return false;
        }

        float newHealth = syncHealth.Value - damage;

        if (newHealth > 0f)
        {
            syncHealth.Value = newHealth;
            return false;
        }

        if (!useDownedState)
        {
            syncHealth.Value = 0f;
            ServerFullDie("Killed");
            return true;
        }

        float overflow = -newHealth;
        float downedPool = downedHealth - overflow;

        if (downedPool <= 0f)
        {
            syncHealth.Value = 0f;
            ServerFullDie("Overkill");
            return true;
        }

        syncHealth.Value = downedPool;
        ServerEnterDowned();
        return false;
    }

    private void ServerEnterDowned()
    {
        serverBleedOutRemaining = bleedOutTime;
        syncBleedOut.Value = bleedOutTime;
        syncState.Value = PlayerLifeState.Downed;

        SetTeamDownedFlag(true);
    }

    private void ServerFullDie(string reason)
    {
        if (syncState.Value == PlayerLifeState.Dead)
        {
            return;
        }

        syncState.Value = PlayerLifeState.Dead;
        syncHealth.Value = 0f;
        syncBleedOut.Value = 0f;
        serverRespawnTimer = respawnDelay;

        // Dead bodies must not count as objective presence: keep the
        // incapacitated flag set until the corpse despawns.
        SetTeamDownedFlag(true);

        Debug.Log(gameObject.name + " died. Reason: " + reason);

        if (TeamTicketManager.Instance != null && playerTeam != null)
        {
            TeamTicketManager.Instance.ConsumeTickets(playerTeam.team, 1, gameObject.name + " death");
        }
    }

    private void ServerRespawn()
    {
        // Despawn instead of respawning in place: the owner drops back to the
        // class selection screen and requests a fresh spawn from there.
        Despawn();
    }

    // Server-side helper for class stats applied right after spawn.
    public void ServerResetHealthToMax()
    {
        if (IsServerInitialized && syncState.Value == PlayerLifeState.Alive)
        {
            syncHealth.Value = maxHealth;
        }
    }

    // Client-side eligibility check used by ReviveInteractor before it shows
    // the prompt. The server re-validates everything in ServerRequestRevive.
    public bool CanBeRevivedBy(PlayerTeam reviverTeam)
    {
        if (!IsDowned || IsOwner)
        {
            return false;
        }

        if (reviverTeam == null || reviverTeam.team == Team.Neutral)
        {
            return false;
        }

        return playerTeam != null && playerTeam.team == reviverTeam.team;
    }

    // Called on the reviver's client after their hold-E completes. This runs
    // on the DOWNED player's object, so ownership cannot be required.
    public void RequestRevive()
    {
        ServerRequestRevive();
    }

    [ServerRpc(RequireOwnership = false)]
    private void ServerRequestRevive(NetworkConnection sender = null)
    {
        if (syncState.Value != PlayerLifeState.Downed)
        {
            return;
        }

        if (sender == null || sender.FirstObject == null)
        {
            return;
        }

        NetworkObject reviverObject = sender.FirstObject;

        PlayerTeam reviverTeam = reviverObject.GetComponent<PlayerTeam>();

        if (reviverTeam == null || playerTeam == null || reviverTeam.team != playerTeam.team)
        {
            return;
        }

        PlayerNetworkHealth reviverHealth = reviverObject.GetComponent<PlayerNetworkHealth>();

        if (reviverHealth != null && reviverHealth.State != PlayerLifeState.Alive)
        {
            return;
        }

        float distance = Vector3.Distance(reviverObject.transform.position, transform.position);

        if (distance > maxReviveDistance)
        {
            return;
        }

        // Only medics can revive players, and they restore full health.
        PlayerNetworkSetup reviverSetup = reviverObject.GetComponent<PlayerNetworkSetup>();

        if (reviverSetup == null || !PlayerClasses.Get(reviverSetup.AssignedClass).canRevive)
        {
            return;
        }

        ServerRevive(maxHealth);
    }

    private void ServerRevive(float restoredHealth)
    {
        serverBleedOutRemaining = 0f;
        syncBleedOut.Value = 0f;
        syncHealth.Value = Mathf.Clamp(restoredHealth, 1f, maxHealth);
        syncState.Value = PlayerLifeState.Alive;

        SetTeamDownedFlag(false);

        Debug.Log(gameObject.name + " revived with " + syncHealth.Value + " HP.");
    }

    [ServerRpc]
    private void ServerGiveUp()
    {
        if (syncState.Value != PlayerLifeState.Downed)
        {
            return;
        }

        float downedElapsed = bleedOutTime - serverBleedOutRemaining;

        if (downedElapsed < giveUpDelay * 0.9f)
        {
            return;
        }

        ServerFullDie("Gave up");
    }

    private void SetTeamDownedFlag(bool downed)
    {
        if (playerTeam != null)
        {
            playerTeam.isDowned = downed;
        }
    }

    // ---- Owner ----

    private void OwnerUpdate()
    {
        if (!IsDowned || Keyboard.current == null)
        {
            return;
        }

        if (GameSettings.Pressed(GameAction.Reload) && DownedElapsed >= giveUpDelay)
        {
            ServerGiveUp();
        }
    }

    private void OnGUI()
    {
        if (!IsOwner)
        {
            return;
        }

        GuiScale.Begin();

        // Healing reads as a warm glow around the EDGES only — the centre of
        // the screen stays completely clear so it never sits between you and
        // what you are aiming at.
        if (healFlashStrength > 0f)
        {
            Color glow = healFlashColor;
            glow.a *= healFlashStrength;
            GUI.color = glow;
            GUI.DrawTexture(new Rect(0f, 0f, GuiScale.Width, GuiScale.Height), VignetteTexture());
            GUI.color = Color.white;
        }

        if (damageFlashStrength > 0f)
        {
            Color flash = damageFlashColor;
            flash.a *= damageFlashStrength;
            GUI.color = flash;
            GUI.DrawTexture(new Rect(0f, 0f, GuiScale.Width, GuiScale.Height), Texture2D.whiteTexture);
            GUI.color = Color.white;
        }

        DrawDamageDirections();
        DrawHealthBar();

        if (State == PlayerLifeState.Alive)
        {
            float protectedRemaining = spawnProtectionDuration - (Time.time - ownerSpawnTime);

            if (protectedRemaining > 0f)
            {
                GUIStyle protectionStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 16,
                    fontStyle = FontStyle.Bold
                };
                protectionStyle.normal.textColor = new Color(1f, 1f, 1f, 0.85f);

                GUI.Label(new Rect(0f, GuiScale.Height * 0.12f, GuiScale.Width, 30f),
                    "SPAWN PROTECTION " + Mathf.CeilToInt(protectedRemaining) + "s", protectionStyle);
            }
        }

        if (!IsDowned)
        {
            return;
        }

        GUIStyle style = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 24,
            fontStyle = FontStyle.Bold
        };
        style.normal.textColor = new Color(1f, 0.3f, 0.25f);

        string text = "YOU ARE DOWN\nBleeding out in " + Mathf.CeilToInt(BleedOutRemaining) + "s";

        float giveUpAvailableIn = giveUpDelay - DownedElapsed;

        if (giveUpAvailableIn <= 0f)
        {
            text += "\nPress R to give up";
        }
        else
        {
            text += "\nGive up available in " + Mathf.CeilToInt(giveUpAvailableIn) + "s";
        }

        GUI.Label(new Rect(0f, GuiScale.Height * 0.55f, GuiScale.Width, 120f), text, style);
    }

    // Bottom-left health bar. While downed it shows the remaining downed pool.
    private void DrawHealthBar()
    {
        if (IsDead)
        {
            return;
        }

        const float barWidth = 220f;
        const float barHeight = 16f;
        float x = 24f;
        float y = GuiScale.Height - 46f;

        float maxPool = IsDowned ? downedHealth : maxHealth;
        float fill01 = maxPool <= 0f ? 0f : Mathf.Clamp01(CurrentHealth / maxPool);

        GUI.color = new Color(0f, 0f, 0f, 0.55f);
        GUI.DrawTexture(new Rect(x - 2f, y - 2f, barWidth + 4f, barHeight + 4f), Texture2D.whiteTexture);

        GUI.color = IsDowned
            ? new Color(0.85f, 0.3f, 0.15f, 0.9f)
            : Color.Lerp(new Color(0.85f, 0.2f, 0.15f, 0.9f), new Color(0.3f, 0.8f, 0.3f, 0.9f), fill01);
        GUI.DrawTexture(new Rect(x, y, barWidth * fill01, barHeight), Texture2D.whiteTexture);
        GUI.color = Color.white;

        GUIStyle textStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 13,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft
        };
        textStyle.normal.textColor = Color.white;

        GUI.Label(new Rect(x + 4f, y - 1f, barWidth, barHeight + 2f),
            Mathf.CeilToInt(CurrentHealth) + " / " + Mathf.CeilToInt(maxPool), textStyle);
    }

    // ---- All clients: visuals from replicated state ----

    private void OnStateChanged(PlayerLifeState previous, PlayerLifeState next, bool asServer)
    {
        if (asServer)
        {
            // The host applies visuals through its client callback; a dedicated
            // server has nothing to show. Team capture flags are set separately.
            return;
        }

        ApplyDownedVisuals(next != PlayerLifeState.Alive);

        // Client-side team flag so capture eligibility looks right everywhere.
        // Dead counts as incapacitated too, so corpses never contest a zone.
        if (playerTeam != null)
        {
            playerTeam.isDowned = next != PlayerLifeState.Alive;
        }

        if (previous == PlayerLifeState.Dead && next == PlayerLifeState.Alive && IsOwner && setup != null)
        {
            setup.MoveOwnerToTeamSpawn();
        }
    }

    private Transform soldierModel;
    private Vector3 modelStandingLocalPosition;
    private Quaternion modelStandingLocalRotation;
    private bool modelPoseCached;

    // Downed/dead bodies should not physically block living players.
    private void SetBodyCollisionIgnored(bool ignored)
    {
        Collider[] ownColliders = GetComponentsInChildren<Collider>(true);

        foreach (CharacterController controller in FindObjectsByType<CharacterController>(FindObjectsSortMode.None))
        {
            if (controller.transform == transform)
            {
                continue;
            }

            foreach (Collider ownCollider in ownColliders)
            {
                if (ownCollider != null && ownCollider.enabled)
                {
                    Physics.IgnoreCollision(ownCollider, controller, ignored);
                }
            }
        }
    }

    private void ApplyDownedVisuals(bool downed)
    {
        SetBodyCollisionIgnored(downed);

        if (setup != null && setup.remoteBody != null)
        {
            Transform body = setup.remoteBody.transform;

            if (soldierModel == null)
            {
                // "SoldierModel" is a non-rotating scale-compensation anchor
                // (see PlayerNetworkSetup.EnsureSoldierModel); the actual rig
                // that rotates for the downed pose is its "SoldierModelRig"
                // child, one level deeper.
                soldierModel = body.Find("SoldierModel/SoldierModelRig");
            }

            if (soldierModel != null)
            {
                // Soldier model: pivot is at the feet, so rotating it in
                // place lays the body down at ground level instead of
                // swinging it around the capsule's offset center.
                if (!modelPoseCached)
                {
                    modelStandingLocalPosition = soldierModel.localPosition;
                    modelStandingLocalRotation = soldierModel.localRotation;
                    modelPoseCached = true;
                }

                soldierModel.localPosition = modelStandingLocalPosition;
                soldierModel.localRotation = downed
                    ? modelStandingLocalRotation * Quaternion.Euler(-90f, 0f, 0f)
                    : modelStandingLocalRotation;
            }
            else
            {
                body.localPosition = downed ? downedBodyLocalPosition : standingBodyLocalPosition;
                body.localRotation = downed
                    ? standingBodyLocalRotation * Quaternion.Euler(downedBodyLocalEulerAngles)
                    : standingBodyLocalRotation;
            }
        }

        if (!IsOwner)
        {
            return;
        }

        if (playerController != null)
        {
            playerController.enabled = !downed;
        }

        if (rifle != null)
        {
            rifle.enabled = !downed;
        }

        if (setup != null && setup.cameraRoot != null)
        {
            Vector3 cameraPosition = standingCameraLocalPosition;

            if (downed)
            {
                cameraPosition.y = downedCameraHeight;
            }

            setup.cameraRoot.transform.localPosition = cameraPosition;
        }
    }
}
