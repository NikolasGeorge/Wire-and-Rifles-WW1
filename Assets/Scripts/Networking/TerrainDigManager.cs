using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Object;
using UnityEngine;

// Server-authoritative shallow digging on the scene's Unity Terrain.
//
// Design rules (from the digging notes):
// - Shallow only: dig depth is capped ~3 feet (0.9m) below the ORIGINAL
//   surface. Unity heightmaps cannot overhang, so tunnels are impossible.
// - Digging is blocked near placed structures, so wire and sandbags cannot
//   be undermined.
// - Every class can dig; Engineer digs at its buildDigMultiplier rate.
// - Fill (Shift) restores ground back toward the original height, never
//   above it — no player-made mounds.
//
// Replication: the server validates each scoop and broadcasts it; clients
// apply scoops deterministically to their local terrain. Late joiners get
// the full scoop history. Original heights are snapshotted on load and
// restored on exit so the editor's TerrainData asset is never permanently
// modified by play sessions.
public class TerrainDigManager : NetworkBehaviour
{
    public static TerrainDigManager Instance { get; private set; }

    [Header("Dig Shape")]
    [Tooltip("Meters below the original surface a player can dig. ~3 feet.")]
    public float maxDigDepth = 0.9f;
    public float digRadius = 1.5f;
    [Tooltip("Meters of depth removed per scoop at the dig center.")]
    public float depthPerScoop = 0.1f;

    [Header("Dig Rules")]
    public float maxDigDistance = 4f;
    [Tooltip("No digging within this range of any placed structure.")]
    public float structureClearance = 2f;
    [Tooltip("Seconds between scoops at 1x dig speed.")]
    public float scoopInterval = 0.8f;

    private struct DigOp
    {
        public Vector3 point;
        public bool fill;
        public float radiusScale;
        public float scoopMultiplier;
    }

    private readonly List<DigOp> serverOps = new List<DigOp>();
    private readonly Dictionary<int, float> serverNextScoopTime = new Dictionary<int, float>();

    // Local terrain state.
    private Terrain terrain;
    private TerrainData terrainData;
    private float[,] originalHeights;
    private bool terrainOffsetApplied;

    // Ops that arrived before the terrain finished initializing (Fish-Net
    // delivers the late-join sync RPCs before Start runs). Replayed once
    // ready — without this, joining clients silently dropped existing digs.
    private readonly List<(Vector3 point, bool fill, float radiusScale, float scoopMultiplier)> pendingOps =
        new List<(Vector3, bool, float, float)>();

    private void Awake()
    {
        Instance = this;
    }

    private void Start()
    {
        InitializeTerrain();

        // Replay any ops that arrived before the terrain was ready.
        if (originalHeights != null && pendingOps.Count > 0)
        {
            foreach ((Vector3 point, bool fill, float radiusScale, float scoopMultiplier) op in pendingOps)
            {
                ApplyDigLocal(op.point, op.fill, op.radiusScale, op.scoopMultiplier);
            }

            pendingOps.Clear();
        }
    }

    // Snapshot the terrain and, if it sits at height zero (a flat default
    // terrain), raise every sample by maxDigDepth while lowering the
    // transform by the same amount. The surface stays exactly where it was,
    // but the heightmap now has headroom to dig DOWN into.
    private void InitializeTerrain()
    {
        terrain = Terrain.activeTerrain;

        if (terrain == null)
        {
            Debug.LogWarning("[Digging] No Terrain in scene — digging disabled.");
            return;
        }

        terrainData = terrain.terrainData;

        int resolution = terrainData.heightmapResolution;
        originalHeights = terrainData.GetHeights(0, 0, resolution, resolution);

        float sampleSpacing = terrainData.size.x / (resolution - 1);

        if (sampleSpacing > 1f)
        {
            Debug.LogWarning("[Digging] Terrain heightmap spacing is " + sampleSpacing.ToString("0.0")
                + "m per sample — dug holes will look chunky. Reduce Terrain Width/Length or raise"
                + " Heightmap Resolution for finer digging.");
        }

        float normalizedDepth = maxDigDepth / terrainData.size.y;
        float minHeight = float.MaxValue;

        foreach (float height in originalHeights)
        {
            minHeight = Mathf.Min(minHeight, height);
        }

        if (minHeight < normalizedDepth)
        {
            float[,] raised = (float[,])originalHeights.Clone();

            for (int y = 0; y < resolution; y++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    raised[y, x] = Mathf.Min(1f, raised[y, x] + normalizedDepth);
                }
            }

            terrainData.SetHeights(0, 0, raised);
            terrain.transform.position += Vector3.down * maxDigDepth;
            originalHeights = raised;
            terrainOffsetApplied = true;
        }
    }

    // Put the terrain asset back exactly how we found it (editor safety —
    // runtime SetHeights writes into the shared TerrainData asset).
    private void RestoreTerrain()
    {
        if (terrainData == null || originalHeights == null)
        {
            return;
        }

        if (terrainOffsetApplied)
        {
            int resolution = terrainData.heightmapResolution;
            float normalizedDepth = maxDigDepth / terrainData.size.y;
            float[,] restored = (float[,])originalHeights.Clone();

            for (int y = 0; y < resolution; y++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    restored[y, x] = Mathf.Max(0f, restored[y, x] - normalizedDepth);
                }
            }

            terrainData.SetHeights(0, 0, restored);
            terrain.transform.position += Vector3.up * maxDigDepth;
        }
        else
        {
            terrainData.SetHeights(0, 0, originalHeights);
        }

        originalHeights = null;
    }

    private void OnDestroy()
    {
        RestoreTerrain();

        if (Instance == this)
        {
            Instance = null;
        }
    }

    private void OnApplicationQuit()
    {
        RestoreTerrain();
    }

    public bool DiggingAvailable => terrain != null;

    // Shared placement rule: no digging near structures (protects wire and
    // sandbag foundations), checked on both client (for the hint) and server.
    public static bool BlockedByStructure(Vector3 point, float clearance)
    {
        foreach (FortificationStructure structure in FindObjectsByType<FortificationStructure>(FindObjectsSortMode.None))
        {
            // Supply crates don't restrict digging — only defensive
            // structures (wire, sandbags, walls) protect their footing.
            if (structure.type == FortificationType.AmmoCrate || structure.type == FortificationType.MedCrate)
            {
                continue;
            }

            Vector3 flat = structure.transform.position - point;
            flat.y = 0f;

            if (flat.magnitude <= clearance)
            {
                return true;
            }
        }

        return false;
    }

    // ---- Server ----

    [ServerRpc(RequireOwnership = false)]
    public void RequestDig(Vector3 point, bool fill, NetworkConnection sender = null)
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

        if (Vector3.Distance(sender.FirstObject.transform.position, point) > maxDigDistance + 2f)
        {
            return;
        }

        if (BlockedByStructure(point, structureClearance))
        {
            return;
        }

        // Rate limit per player, honoring the class dig multiplier.
        float digMultiplier = Mathf.Max(0.1f, PlayerClasses.Get(setup.AssignedClass).buildDigMultiplier);
        float interval = scoopInterval / digMultiplier;

        if (serverNextScoopTime.TryGetValue(sender.ClientId, out float nextAllowed) && Time.time < nextAllowed)
        {
            return;
        }

        serverNextScoopTime[sender.ClientId] = Time.time + interval * 0.9f;

        serverOps.Add(new DigOp { point = point, fill = fill, radiusScale = 1f, scoopMultiplier = 1f });
        ObserversApplyDig(point, fill, 1f, 1f);
    }

    // Server-only: explosion crater — one big wide scoop. Only craters when
    // the blast is near the terrain surface.
    public void ServerAddCrater(Vector3 point, float radiusScale = 2f, float scoopMultiplier = 5f)
    {
        if (!IsServerStarted || terrain == null)
        {
            return;
        }

        float surfaceHeight = terrain.SampleHeight(point) + terrain.transform.position.y;

        if (Mathf.Abs(point.y - surfaceHeight) > 2f)
        {
            return;
        }

        serverOps.Add(new DigOp { point = point, fill = false, radiusScale = radiusScale, scoopMultiplier = scoopMultiplier });
        ObserversApplyDig(point, false, radiusScale, scoopMultiplier);
    }

    public override void OnSpawnServer(NetworkConnection connection)
    {
        base.OnSpawnServer(connection);

        if (serverOps.Count == 0)
        {
            return;
        }

        // Replay dig history for late joiners in batches.
        const int batchSize = 200;

        for (int start = 0; start < serverOps.Count; start += batchSize)
        {
            int count = Mathf.Min(batchSize, serverOps.Count - start);
            Vector3[] points = new Vector3[count];
            bool[] fills = new bool[count];
            float[] radiusScales = new float[count];
            float[] scoopMultipliers = new float[count];

            for (int i = 0; i < count; i++)
            {
                points[i] = serverOps[start + i].point;
                fills[i] = serverOps[start + i].fill;
                radiusScales[i] = serverOps[start + i].radiusScale;
                scoopMultipliers[i] = serverOps[start + i].scoopMultiplier;
            }

            TargetSyncDigOps(connection, points, fills, radiusScales, scoopMultipliers);
        }
    }

    // ---- Client application ----

    [ObserversRpc]
    private void ObserversApplyDig(Vector3 point, bool fill, float radiusScale, float scoopMultiplier)
    {
        ApplyDigLocal(point, fill, radiusScale, scoopMultiplier);
    }

    [TargetRpc]
    private void TargetSyncDigOps(NetworkConnection connection, Vector3[] points, bool[] fills,
        float[] radiusScales, float[] scoopMultipliers)
    {
        for (int i = 0; i < points.Length; i++)
        {
            ApplyDigLocal(points[i], fills[i], radiusScales[i], scoopMultipliers[i]);
        }
    }

    // Lower (or raise) a smooth circular patch of terrain around the point,
    // clamped between originalHeight - maxDigDepth and originalHeight.
    private void ApplyDigLocal(Vector3 point, bool fill, float radiusScale, float scoopMultiplier)
    {
        if (originalHeights == null)
        {
            // Terrain not initialized yet — hold the op and replay it later.
            pendingOps.Add((point, fill, radiusScale, scoopMultiplier));
            return;
        }

        if (terrain == null)
        {
            return;
        }

        float effectiveRadius = digRadius * Mathf.Max(0.1f, radiusScale);

        int resolution = terrainData.heightmapResolution;
        Vector3 terrainPosition = terrain.transform.position;
        Vector3 size = terrainData.size;

        float normalizedX = (point.x - terrainPosition.x) / size.x;
        float normalizedZ = (point.z - terrainPosition.z) / size.z;

        if (normalizedX < 0f || normalizedX > 1f || normalizedZ < 0f || normalizedZ > 1f)
        {
            return;
        }

        int centerX = Mathf.RoundToInt(normalizedX * (resolution - 1));
        int centerZ = Mathf.RoundToInt(normalizedZ * (resolution - 1));

        int radiusSamples = Mathf.Max(1, Mathf.CeilToInt(effectiveRadius / (size.x / (resolution - 1))));

        int minX = Mathf.Clamp(centerX - radiusSamples, 0, resolution - 1);
        int maxX = Mathf.Clamp(centerX + radiusSamples, 0, resolution - 1);
        int minZ = Mathf.Clamp(centerZ - radiusSamples, 0, resolution - 1);
        int maxZ = Mathf.Clamp(centerZ + radiusSamples, 0, resolution - 1);

        int width = maxX - minX + 1;
        int height = maxZ - minZ + 1;

        // GetHeights/SetHeights are (row, column) = (z, x).
        float[,] patch = terrainData.GetHeights(minX, minZ, width, height);

        float normalizedScoop = depthPerScoop * Mathf.Max(0.1f, scoopMultiplier) / size.y;
        float normalizedDepth = maxDigDepth / size.y;
        float sampleSpacing = size.x / (resolution - 1);

        for (int z = 0; z < height; z++)
        {
            for (int x = 0; x < width; x++)
            {
                int sampleX = minX + x;
                int sampleZ = minZ + z;

                float worldDistance = new Vector2((sampleX - centerX) * sampleSpacing,
                    (sampleZ - centerZ) * sampleSpacing).magnitude;

                if (worldDistance > effectiveRadius)
                {
                    continue;
                }

                // Smooth falloff toward the rim of the scoop.
                float falloff = Mathf.SmoothStep(1f, 0f, worldDistance / effectiveRadius);
                float delta = normalizedScoop * falloff * (fill ? 1f : -1f);

                float original = originalHeights[sampleZ, sampleX];
                float current = patch[z, x];

                patch[z, x] = Mathf.Clamp(current + delta, original - normalizedDepth, original);
            }
        }

        terrainData.SetHeights(minX, minZ, patch);
    }
}
