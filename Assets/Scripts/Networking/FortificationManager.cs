using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Object;
using UnityEngine;

public enum FortificationType : byte
{
    Sandbags = 0,
    BarbedWire = 1,
    AmmoCrate = 2,
    MedCrate = 3
}

// Server-authoritative fortification building. Engineers request a build via
// ServerRpc; the server validates and broadcasts the structure to every
// client, which each construct identical primitive-based visuals locally (no
// spawnable prefab needed). Late joiners get the full build list replayed via
// TargetRpc. Crate resupply/heal effects run server-side only.
public class FortificationManager : NetworkBehaviour
{
    public static FortificationManager Instance { get; private set; }

    [Header("Placement Rules")]
    public float maxPlaceDistance = 5f;
    public int maxBuildsPerPlayer = 10;
    public float perPlayerBuildCooldown = 1f;

    [Header("Crate Effects")]
    public float crateEffectRadius = 3.5f;
    public float crateEffectInterval = 2f;
    public int ammoPerTick = 5;
    public float healPerTick = 10f;

    private class BuildRecord
    {
        public int id;
        public FortificationType type;
        public Vector3 position;
        public float yaw;
        public int builderClientId;
        public float completeAtTime;
    }

    private readonly List<BuildRecord> serverBuilds = new List<BuildRecord>();
    private readonly Dictionary<int, float> serverLastBuildTime = new Dictionary<int, float>();
    private readonly Dictionary<int, GameObject> clientStructures = new Dictionary<int, GameObject>();
    private int nextBuildId;
    private float crateEffectTimer;

    // Base build seconds by type; divided by the builder's buildDigMultiplier.
    private static float GetBuildDuration(FortificationType type)
    {
        switch (type)
        {
            case FortificationType.BarbedWire: return 3f;
            case FortificationType.AmmoCrate: return 5f;
            case FortificationType.MedCrate: return 5f;
            default: return 4f;
        }
    }

    private void Awake()
    {
        Instance = this;
    }

    [ServerRpc(RequireOwnership = false)]
    public void RequestBuild(FortificationType type, Vector3 position, float yaw, NetworkConnection sender = null)
    {
        if (sender == null || sender.FirstObject == null)
        {
            return;
        }

        PlayerNetworkSetup setup = sender.FirstObject.GetComponent<PlayerNetworkSetup>();

        if (setup == null)
        {
            return;
        }

        PlayerClassDefinition definition = PlayerClasses.Get(setup.AssignedClass);

        // Only the Engineer builds (identified by its build bonus).
        if (definition.buildDigMultiplier <= 1f)
        {
            return;
        }

        PlayerNetworkHealth health = sender.FirstObject.GetComponent<PlayerNetworkHealth>();

        if (health != null && health.State != PlayerLifeState.Alive)
        {
            return;
        }

        if (Vector3.Distance(sender.FirstObject.transform.position, position) > maxPlaceDistance + 1f)
        {
            return;
        }

        if (serverLastBuildTime.TryGetValue(sender.ClientId, out float lastTime)
            && Time.time - lastTime < perPlayerBuildCooldown)
        {
            return;
        }

        int activeCount = 0;

        foreach (BuildRecord record in serverBuilds)
        {
            if (record.builderClientId == sender.ClientId)
            {
                activeCount++;
            }
        }

        if (activeCount >= maxBuildsPerPlayer)
        {
            return;
        }

        // Snap to the ground so fortifications never float.
        if (Physics.Raycast(position + Vector3.up * 2f, Vector3.down, out RaycastHit groundHit, 10f,
            Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            position = groundHit.point;
        }

        serverLastBuildTime[sender.ClientId] = Time.time;

        float duration = GetBuildDuration(type) / Mathf.Max(1f, definition.buildDigMultiplier);

        BuildRecord build = new BuildRecord
        {
            id = nextBuildId++,
            type = type,
            position = position,
            yaw = yaw,
            builderClientId = sender.ClientId,
            completeAtTime = Time.time + duration
        };

        serverBuilds.Add(build);

        ObserversBuildFortification(build.id, type, position, yaw, duration);
    }

    [ObserversRpc]
    private void ObserversBuildFortification(int id, FortificationType type, Vector3 position, float yaw, float duration)
    {
        BuildStructure(id, type, position, yaw, duration);
    }

    // Replay existing fortifications to a client that joined after they were
    // built (already-completed ones rise instantly).
    public override void OnSpawnServer(NetworkConnection connection)
    {
        base.OnSpawnServer(connection);

        foreach (BuildRecord build in serverBuilds)
        {
            float remaining = Mathf.Max(0.1f, build.completeAtTime - Time.time);
            TargetBuildFortification(connection, build.id, build.type, build.position, build.yaw, remaining);
        }
    }

    [TargetRpc]
    private void TargetBuildFortification(NetworkConnection connection, int id, FortificationType type, Vector3 position, float yaw, float duration)
    {
        BuildStructure(id, type, position, yaw, duration);
    }

    private void Update()
    {
        if (!IsServerInitialized)
        {
            return;
        }

        crateEffectTimer += Time.deltaTime;

        if (crateEffectTimer < crateEffectInterval)
        {
            return;
        }

        crateEffectTimer = 0f;
        ApplyCrateEffects();
    }

    // Server: completed ammo/med crates periodically resupply and heal any
    // living player standing near them.
    private void ApplyCrateEffects()
    {
        PlayerNetworkSetup[] players = FindObjectsByType<PlayerNetworkSetup>(FindObjectsSortMode.None);

        foreach (BuildRecord build in serverBuilds)
        {
            if (Time.time < build.completeAtTime)
            {
                continue;
            }

            if (build.type != FortificationType.AmmoCrate && build.type != FortificationType.MedCrate)
            {
                continue;
            }

            foreach (PlayerNetworkSetup player in players)
            {
                if (Vector3.Distance(player.transform.position, build.position) > crateEffectRadius)
                {
                    continue;
                }

                if (build.type == FortificationType.AmmoCrate)
                {
                    BoltActionRifle rifle = player.GetComponent<BoltActionRifle>();

                    if (rifle != null)
                    {
                        rifle.ServerGrantReserveAmmo(ammoPerTick);
                    }
                }
                else
                {
                    PlayerNetworkHealth health = player.GetComponent<PlayerNetworkHealth>();

                    if (health != null)
                    {
                        health.ServerHeal(healPerTick);
                    }
                }
            }
        }
    }

    // ---- Client-side structure construction (identical on every machine) ----

    private void BuildStructure(int id, FortificationType type, Vector3 position, float yaw, float duration)
    {
        if (clientStructures.ContainsKey(id))
        {
            return;
        }

        GameObject root = new GameObject("Fortification_" + type + "_" + id);
        root.transform.position = position;
        root.transform.rotation = Quaternion.Euler(0f, yaw, 0f);

        switch (type)
        {
            case FortificationType.Sandbags:
                BuildSandbags(root);
                break;
            case FortificationType.BarbedWire:
                BuildBarbedWire(root, duration);
                break;
            case FortificationType.AmmoCrate:
                BuildCrate(root, new Color(0.35f, 0.4f, 0.22f), false);
                break;
            case FortificationType.MedCrate:
                BuildCrate(root, new Color(0.9f, 0.9f, 0.88f), true);
                break;
        }

        FortificationRise rise = root.AddComponent<FortificationRise>();
        rise.duration = duration;

        clientStructures[id] = root;
    }

    private static GameObject MakeBlock(Transform parent, Vector3 localPosition, Vector3 scale, Color color, bool keepCollider)
    {
        GameObject block = GameObject.CreatePrimitive(PrimitiveType.Cube);
        block.transform.SetParent(parent, false);
        block.transform.localPosition = localPosition;
        block.transform.localScale = scale;
        block.GetComponent<Renderer>().material.color = color;

        if (!keepCollider)
        {
            Object.Destroy(block.GetComponent<Collider>());
        }

        return block;
    }

    private static void BuildSandbags(GameObject root)
    {
        Color sandbag = new Color(0.55f, 0.47f, 0.32f);
        Color sandbagDark = new Color(0.48f, 0.4f, 0.27f);

        // Bottom row of three bags, top row of two — solid cover that blocks
        // movement and bullets.
        MakeBlock(root.transform, new Vector3(-0.75f, 0.25f, 0f), new Vector3(0.75f, 0.5f, 0.5f), sandbag, true);
        MakeBlock(root.transform, new Vector3(0f, 0.25f, 0f), new Vector3(0.75f, 0.5f, 0.5f), sandbagDark, true);
        MakeBlock(root.transform, new Vector3(0.75f, 0.25f, 0f), new Vector3(0.75f, 0.5f, 0.5f), sandbag, true);
        MakeBlock(root.transform, new Vector3(-0.38f, 0.72f, 0f), new Vector3(0.75f, 0.45f, 0.5f), sandbagDark, true);
        MakeBlock(root.transform, new Vector3(0.38f, 0.72f, 0f), new Vector3(0.75f, 0.45f, 0.5f), sandbag, true);
    }

    private static void BuildBarbedWire(GameObject root, float buildDuration)
    {
        Color post = new Color(0.3f, 0.25f, 0.2f);
        Color wire = new Color(0.35f, 0.35f, 0.35f);

        MakeBlock(root.transform, new Vector3(-1.4f, 0.45f, 0f), new Vector3(0.08f, 0.9f, 0.08f), post, false);
        MakeBlock(root.transform, new Vector3(1.4f, 0.45f, 0f), new Vector3(0.08f, 0.9f, 0.08f), post, false);

        MakeBlock(root.transform, new Vector3(0f, 0.25f, 0f), new Vector3(2.8f, 0.035f, 0.035f), wire, false);
        MakeBlock(root.transform, new Vector3(0f, 0.5f, 0f), new Vector3(2.8f, 0.035f, 0.035f), wire, false);
        MakeBlock(root.transform, new Vector3(0f, 0.75f, 0f), new Vector3(2.8f, 0.035f, 0.035f), wire, false);

        // Trigger zone that slows anyone wading through; active only once
        // construction finishes.
        BoxCollider slowZone = root.AddComponent<BoxCollider>();
        slowZone.isTrigger = true;
        slowZone.center = new Vector3(0f, 0.5f, 0f);
        slowZone.size = new Vector3(2.9f, 1f, 1f);

        WireSlowZone slow = root.AddComponent<WireSlowZone>();
        slow.activeAfterTime = Time.time + buildDuration;
    }

    private static void BuildCrate(GameObject root, Color bodyColor, bool medical)
    {
        MakeBlock(root.transform, new Vector3(0f, 0.3f, 0f), new Vector3(0.9f, 0.6f, 0.6f), bodyColor, true);
        MakeBlock(root.transform, new Vector3(0f, 0.62f, 0f), new Vector3(0.95f, 0.06f, 0.65f), bodyColor * 0.8f, false);

        if (medical)
        {
            Color cross = new Color(0.8f, 0.1f, 0.1f);
            MakeBlock(root.transform, new Vector3(0f, 0.66f, 0f), new Vector3(0.5f, 0.03f, 0.14f), cross, false);
            MakeBlock(root.transform, new Vector3(0f, 0.66f, 0f), new Vector3(0.14f, 0.03f, 0.5f), cross, false);
        }
    }
}

// Rises the structure out of the ground over the build duration.
public class FortificationRise : MonoBehaviour
{
    public float duration = 4f;

    private float timer;
    private Vector3 targetPosition;
    private float sinkDepth = 0.9f;

    private void Start()
    {
        targetPosition = transform.position;
        transform.position = targetPosition + Vector3.down * sinkDepth;
    }

    private void Update()
    {
        if (timer >= duration)
        {
            return;
        }

        timer += Time.deltaTime;
        float progress = duration <= 0f ? 1f : Mathf.Clamp01(timer / duration);
        transform.position = targetPosition + Vector3.down * (sinkDepth * (1f - progress));
    }
}

// Slows any player wading through barbed wire. Runs on the owning client
// (movement is client-authoritative).
public class WireSlowZone : MonoBehaviour
{
    [Range(0.1f, 1f)]
    public float slowMultiplier = 0.35f;

    public float activeAfterTime;

    private void OnTriggerStay(Collider other)
    {
        if (Time.time < activeAfterTime)
        {
            return;
        }

        PlayerController controller = other.GetComponentInParent<PlayerController>();

        if (controller != null && controller.enabled)
        {
            controller.ApplyEnvironmentSlow(slowMultiplier);
        }
    }
}
