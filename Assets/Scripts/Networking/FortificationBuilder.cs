using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Owner-side fortification controls, added to every owned player:
// - Placement (class-gated): number keys select a buildable and show a ghost
//   preview at the aimed ground point. Scroll rotates it (yaw only), F
//   confirms, X cancels. Free-form placement, upright always.
// - Building/repair (everyone): hold F near a friendly blueprint or damaged
//   structure to work on it with the shovel. Engineers work at 2x.
public class FortificationBuilder : MonoBehaviour
{
    public Camera playerCamera;
    public float maxPlaceDistance = 6f;
    public float maxGroundAngle = 50f;
    public float buildInteractRange = 4f;

    private PlayerNetworkHealth health;
    private PlayerNetworkSetup setup;

    private GameObject ghost;
    private FortificationType ghostType;
    private float ghostYaw;
    private bool ghostValid;
    private string ghostInvalidReason = "";

    private int buildingStructureId = -1;
    private float structureScanTimer;
    private FortificationStructure nearestWorkable;
    private bool workLatched; // tap-to-build latch (see GameSettings.TapToBuild)
    private float workSoundTimer;

    public float deconstructHoldTime = 2f;
    private float deconstructTimer;
    private FortificationStructure deconstructTarget;

    private void Start()
    {
        // MenuOpen is static so every system (look, cursor, firing) can gate
        // on one shared flag. That means it MUST be force-cleared the moment
        // a fresh local player comes online — a stale true left over from a
        // previous life (or a Play session with domain reload disabled)
        // would otherwise freeze look/cursor/firing on next spawn with no
        // menu visibly open to explain why.
        CloseBuildMenu();

        health = GetComponent<PlayerNetworkHealth>();
        setup = GetComponent<PlayerNetworkSetup>();

        if (playerCamera == null)
        {
            playerCamera = GetComponentInChildren<Camera>();
        }
    }

    private void OnDestroy()
    {
        // MenuOpen is static and players are despawned on death: without
        // this the flag survives the body and every later life spawns with
        // look disabled and a loose cursor.
        CloseBuildMenu();
        ClearGhost();
        StopBuilding();
    }

    private static void CloseBuildMenu()
    {
        if (!MenuOpen)
        {
            return;
        }

        MenuOpen = false;
        SetMenuCursor(false);
    }

    // Placement options for this player's class.
    private List<FortificationType> GetPlaceableTypes()
    {
        List<FortificationType> types = new List<FortificationType>();

        if (setup == null)
        {
            return types;
        }

        // Crates are no longer placed via ghost — Support/Medic THROW them
        // from their gadget slot instead.
        if (PlayerClasses.Get(setup.AssignedClass).buildDigMultiplier > 1f)
        {
            types.Add(FortificationType.Sandbags);
            types.Add(FortificationType.LowWire);
            types.Add(FortificationType.HighWire);
            types.Add(FortificationType.TrenchWall);
            types.Add(FortificationType.DuckBoards);
            types.Add(FortificationType.MakeshiftFloor);
            types.Add(FortificationType.Ladder);
            types.Add(FortificationType.CorrugatedRoof);
        }

        return types;
    }

    // ---- Build menu ----
    // Too many buildables to keep hanging off number keys, so B opens a
    // list. Gated on the shovel being out: the tool you build with is the
    // tool that opens the menu.
    // Static like PauseMenu.IsOpen, and for the same reason: movement,
    // looking, firing and slot switching all have to stand down while it is
    // up, and only the local player ever has a builder.
    public static bool MenuOpen { get; private set; }

    private PlayerItemSlots slots;

    // Class is the only gate — the menu opens whatever you are holding.
    private bool CanOpenBuildMenu()
    {
        return setup != null && PlayerClasses.Get(setup.AssignedClass).buildDigMultiplier > 1f;
    }

    private void HandleBuildMenu()
    {
        bool wasOpen = MenuOpen;

        if (Keyboard.current.bKey.wasPressedThisFrame && (MenuOpen || CanOpenBuildMenu()))
        {
            MenuOpen = !MenuOpen;
        }

        if (MenuOpen && Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            MenuOpen = false;
        }

        // Putting the shovel away closes it.
        if (MenuOpen && !CanOpenBuildMenu())
        {
            MenuOpen = false;
        }

        if (MenuOpen != wasOpen)
        {
            SetMenuCursor(MenuOpen);
        }
    }

    // The grid is clicked, so the cursor has to come back while it is up.
    private static void SetMenuCursor(bool open)
    {
        Cursor.lockState = open ? CursorLockMode.None : CursorLockMode.Locked;
        Cursor.visible = open;
    }

    private void SelectBuildable(FortificationType type)
    {
        StartGhost(type);
        MenuOpen = false;
        SetMenuCursor(false);
    }

    [Header("Build Menu")]
    public int buildMenuColumns = 5;
    public float buildMenuTileSize = 104f;

    private void DrawBuildMenu()
    {
        if (!MenuOpen || FortificationManager.Instance == null)
        {
            return;
        }

        List<FortificationType> types = GetPlaceableTypes();

        if (types.Count == 0)
        {
            return;
        }

        const float pad = 8f;
        const float headerHeight = 40f;
        float labelHeight = 20f;
        float cell = buildMenuTileSize;
        int columns = Mathf.Max(1, buildMenuColumns);
        int rows = Mathf.CeilToInt(types.Count / (float)columns);

        float width = columns * cell + pad * 2f;
        float height = headerHeight + rows * (cell + labelHeight) + pad * 2f;
        float x = (Screen.width - width) * 0.5f;
        float y = (Screen.height - height) * 0.5f;

        // Panel.
        GUI.color = new Color(0.06f, 0.05f, 0.04f, 0.88f);
        GUI.DrawTexture(new Rect(x, y, width, height), Texture2D.whiteTexture);
        GUI.color = new Color(0.55f, 0.48f, 0.36f, 0.9f);
        DrawFrame(new Rect(x, y, width, height), 2f);
        GUI.color = Color.white;

        // Header: what you have, and what you are earning.
        int supplies = setup != null ? FortificationManager.Instance.GetSupplies(setup.AssignedTeam) : 0;
        float perMinute = FortificationManager.Instance.SuppliesPerMinute;

        GUIStyle headerStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 18,
            fontStyle = FontStyle.BoldAndItalic,
            alignment = TextAnchor.MiddleCenter
        };
        headerStyle.normal.textColor = new Color(0.95f, 0.92f, 0.82f);

        GUI.Label(new Rect(x, y + 8f, width, 26f),
            supplies + " Build Point(s)   +" + Mathf.RoundToInt(perMinute) + " per Minute", headerStyle);

        GUI.color = new Color(0.55f, 0.48f, 0.36f, 0.5f);
        GUI.DrawTexture(new Rect(x + pad, y + headerHeight - 2f, width - pad * 2f, 1f), Texture2D.whiteTexture);
        GUI.color = Color.white;

        for (int i = 0; i < types.Count; i++)
        {
            int column = i % columns;
            int row = i / columns;

            Rect tile = new Rect(
                x + pad + column * cell,
                y + headerHeight + pad + row * (cell + labelHeight),
                cell - 4f,
                cell - 4f);

            DrawBuildTile(tile, types[i], supplies, labelHeight);
        }
    }

    private void DrawBuildTile(Rect tile, FortificationType type, int supplies, float labelHeight)
    {
        int cost = FortificationManager.GetCost(type);
        bool affordable = supplies >= cost;
        bool hovered = tile.Contains(Event.current.mousePosition);

        // Tile background, brightened under the cursor.
        GUI.color = hovered
            ? new Color(0.28f, 0.25f, 0.19f, 1f)
            : new Color(0.16f, 0.15f, 0.12f, 1f);
        GUI.DrawTexture(tile, Texture2D.whiteTexture);

        GUI.color = new Color(0.45f, 0.4f, 0.3f, 0.8f);
        DrawFrame(tile, 1f);
        GUI.color = Color.white;

        // Rendered prop thumbnail; falls back to the name if it could not be
        // built (missing prefab and no primitive fallback).
        Texture icon = StructureIcons.Get(type);

        if (icon != null)
        {
            GUI.DrawTexture(new Rect(tile.x + 3f, tile.y + 3f, tile.width - 6f, tile.height - 6f),
                icon, ScaleMode.ScaleToFit);
        }

        GUIStyle nameStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 10,
            alignment = TextAnchor.LowerCenter,
            wordWrap = true
        };
        nameStyle.normal.textColor = new Color(0.92f, 0.9f, 0.82f, 0.95f);

        GUI.Label(new Rect(tile.x, tile.y + tile.height - 30f, tile.width, 28f),
            FortificationManager.GetDisplayName(type), nameStyle);

        GUIStyle costStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };
        costStyle.normal.textColor = affordable
            ? new Color(0.85f, 0.82f, 0.72f)
            : new Color(1f, 0.42f, 0.36f);

        GUI.Label(new Rect(tile.x, tile.y + tile.height + 1f, tile.width, labelHeight),
            cost + " BP", costStyle);

        // Invisible button over the whole tile so the art is the control.
        if (GUI.Button(tile, GUIContent.none, GUIStyle.none) && affordable)
        {
            SelectBuildable(type);
        }
    }

    private static void DrawFrame(Rect rect, float thickness)
    {
        GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, thickness), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(rect.x, rect.y, thickness, rect.height), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), Texture2D.whiteTexture);
    }

    private void Update()
    {
        if (Keyboard.current == null)
        {
            return;
        }

        if (health != null && health.State != PlayerLifeState.Alive)
        {
            CloseBuildMenu();
            ClearGhost();
            StopBuilding();
            return;
        }

        if (PauseMenu.IsOpen)
        {
            StopBuilding();
            return;
        }

        HandleBuildMenu();

        // While the menu is up its number keys pick structures, so nothing
        // else should read input this frame.
        if (MenuOpen)
        {
            StopBuilding();
            return;
        }

        HandlePlacementSelection();

        if (ghost != null)
        {
            // A raised ghost owns the Interact key; drop any build latch so
            // it does not resume once the ghost is cleared.
            workLatched = false;
            UpdateGhost();
        }
        else
        {
            HandleBuildInteraction();
            HandleDeconstruct();
        }

        digAimTimer -= Time.deltaTime;
    }

    // Digging is universal (slot 3, the Shovel — see PlayerItemSlots): a
    // swing that lands on open ground calls this once with fill=false;
    // holding RMB calls it every frame with fill=true. The server paces
    // scoops by class dig multiplier (Engineer 2x) and enforces every rule.
    private float digAimTimer;
    private string digBlockedReason = "";
    private float digReasonTime = -999f;

    public void TryDigScoop(bool fill)
    {
        if (TerrainDigManager.Instance == null || !TerrainDigManager.Instance.DiggingAvailable
            || playerCamera == null)
        {
            return;
        }

        TerrainDigManager digManager = TerrainDigManager.Instance;

        if (!Physics.Raycast(playerCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f)),
            out RaycastHit hit, digManager.maxDigDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            SetDigBlockedReason("AIM AT NEARBY GROUND TO DIG");
            return;
        }

        if (hit.collider.GetComponent<TerrainCollider>() == null)
        {
            SetDigBlockedReason("CAN ONLY DIG NATURAL GROUND");
            return;
        }

        // Client-side pacing mirrors the server's rate limit so we do not
        // spam RPCs that would just be dropped.
        float digMultiplier = setup != null
            ? Mathf.Max(0.1f, PlayerClasses.Get(setup.AssignedClass).buildDigMultiplier)
            : 1f;

        if (digAimTimer <= 0f)
        {
            digAimTimer = TerrainDigManager.Instance.scoopInterval / digMultiplier;
            digManager.RequestDig(hit.point, fill);
            ProceduralAudio.PlayAt(ProceduralAudio.Dig, hit.point, 0.55f);
        }
    }

    private void SetDigBlockedReason(string reason)
    {
        digBlockedReason = reason;
        digReasonTime = Time.time;
    }

    // Engineer: hold G near any friendly structure to tear it down (partial
    // supply refund handled server-side).
    private void HandleDeconstruct()
    {
        bool isEngineer = setup != null && PlayerClasses.Get(setup.AssignedClass).buildDigMultiplier > 1f;

        if (!isEngineer || !GameSettings.Held(GameAction.Deconstruct) || FortificationManager.Instance == null)
        {
            deconstructTimer = 0f;
            deconstructTarget = null;
            return;
        }

        FortificationStructure target = FindNearestFriendly();

        if (target == null)
        {
            deconstructTimer = 0f;
            deconstructTarget = null;
            return;
        }

        if (target != deconstructTarget)
        {
            deconstructTarget = target;
            deconstructTimer = 0f;
        }

        deconstructTimer += Time.deltaTime;

        if (deconstructTimer >= deconstructHoldTime)
        {
            deconstructTimer = 0f;
            deconstructTarget = null;
            FortificationManager.Instance.RequestDeconstruct(target.id);
        }
    }

    private FortificationStructure FindNearestFriendly()
    {
        FortificationStructure nearest = null;
        float nearestDistance = float.MaxValue;
        Team myTeam = setup != null ? setup.AssignedTeam : Team.Neutral;

        foreach (FortificationStructure structure in FindObjectsByType<FortificationStructure>(FindObjectsSortMode.None))
        {
            if (structure.team != myTeam)
            {
                continue;
            }

            float distance = Vector3.Distance(transform.position, structure.transform.position);

            if (distance <= buildInteractRange && distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = structure;
            }
        }

        return nearest;
    }

    // Structures are chosen from the B menu now; this only handles cancelling
    // a raised ghost.
    private void HandlePlacementSelection()
    {
        if (ghost != null && Keyboard.current.xKey.wasPressedThisFrame)
        {
            ClearGhost();
        }
    }

    // Entry point for gadget slots (ammo/med crate equipment).
    public void TryToggleGhost(FortificationType type)
    {
        if (ghost != null && ghostType == type)
        {
            ClearGhost();
        }
        else
        {
            StartGhost(type);
        }
    }

    private void StartGhost(FortificationType type)
    {
        ClearGhost();
        StopBuilding();

        ghostType = type;
        ghostYaw = transform.eulerAngles.y;
        ghost = FortificationManager.BuildVisual(type, transform.position, Quaternion.Euler(0f, ghostYaw, 0f), out _);
        ghost.name = "PlacementGhost";

        // Ghost is preview-only: transparent hologram, nothing collides.
        foreach (Collider collider in ghost.GetComponentsInChildren<Collider>(true))
        {
            Destroy(collider);
        }

        Shader transparentShader = Shader.Find("Sprites/Default");
        ghostMaterials.Clear();

        foreach (Renderer ghostRenderer in ghost.GetComponentsInChildren<Renderer>(true))
        {
            Material ghostMaterial = new Material(transparentShader)
            {
                color = ghostValidColor
            };

            Material[] materials = new Material[ghostRenderer.sharedMaterials.Length];

            for (int i = 0; i < materials.Length; i++)
            {
                materials[i] = ghostMaterial;
            }

            ghostRenderer.materials = materials;
            ghostMaterials.Add(ghostMaterial);
        }
    }

    [Header("Ghost Colors")]
    public Color ghostValidColor = new Color(0.4f, 0.9f, 1f, 0.2f);
    public Color ghostInvalidColor = new Color(1f, 0.25f, 0.2f, 0.28f);

    // Materials driving the ghost preview, recoloured every frame so an
    // illegal spot reads as red before you ever press place.
    private readonly List<Material> ghostMaterials = new List<Material>();

    private void ApplyGhostTint(bool valid)
    {
        Color tint = valid ? ghostValidColor : ghostInvalidColor;

        foreach (Material material in ghostMaterials)
        {
            if (material != null)
            {
                material.color = tint;
            }
        }
    }

    private void ClearGhost()
    {
        if (ghost != null)
        {
            Destroy(ghost);
            ghost = null;
        }
    }

    private void UpdateGhost()
    {
        // Rotate in 5-degree steps (yaw only): scroll wheel, or R /
        // Shift+R as a reliable fallback.
        if (Mouse.current != null)
        {
            float scroll = Mouse.current.scroll.ReadValue().y;

            if (Mathf.Abs(scroll) > 0.01f)
            {
                ghostYaw += Mathf.Sign(scroll) * 5f;
            }
        }

        if (Keyboard.current.leftBracketKey.wasPressedThisFrame)
        {
            ghostYaw -= 5f;
        }

        if (Keyboard.current.rightBracketKey.wasPressedThisFrame)
        {
            ghostYaw += 5f;
        }

        ghostYaw = Mathf.Round(ghostYaw / 5f) * 5f;

        Vector3 targetPoint;
        bool grounded = false;
        FortificationStructure supporting = null;
        Vector3 groundNormal = Vector3.up;

        if (playerCamera != null
            && Physics.Raycast(playerCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f)),
                out RaycastHit hit, maxPlaceDistance + 4f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            targetPoint = hit.point;
            groundNormal = hit.normal;
            grounded = true;
            supporting = hit.collider.GetComponentInParent<FortificationStructure>();
        }
        else
        {
            Vector3 forward = transform.forward;
            forward.y = 0f;
            targetPoint = transform.position + forward.normalized * 3f;
        }

        // Floors and roofs lock to their neighbours so a walkway or a roofed
        // stretch comes out flush instead of hand-aligned.
        if (SnapsToNeighbours(ghostType))
        {
            targetPoint = SnapToNeighbour(targetPoint, ghostType);
        }

        ghost.transform.position = targetPoint;
        ghost.transform.rotation = Quaternion.Euler(0f, ghostYaw, 0f);

        ghostValid = true;
        ghostInvalidReason = "";

        if (!grounded || Vector3.Distance(transform.position, targetPoint) > maxPlaceDistance)
        {
            ghostValid = false;
            ghostInvalidReason = "TOO FAR";
        }
        // Anything solid counts as a foundation, so there is no on-ground
        // rule left to fail — only the slope of actual terrain matters.
        else if (supporting == null && !FortificationManager.KeepsPlacedHeight(ghostType)
            && Vector3.Angle(groundNormal, Vector3.up) > maxGroundAngle)
        {
            ghostValid = false;
            ghostInvalidReason = "GROUND TOO STEEP";
        }
        else if (CountNearbySameType(targetPoint)
            >= FortificationManager.GetStackLimit(ghostType, FortificationManager.Instance != null ? FortificationManager.Instance.maxSameTypeStack : 2))
        {
            bool isWire = ghostType == FortificationType.LowWire || ghostType == FortificationType.HighWire;
            ghostValid = false;
            ghostInvalidReason = isWire ? "WIRE ALREADY HERE — OFFSET IT" : "STACK LIMIT (MAX 2)";
        }
        else if (setup != null && FortificationManager.Instance != null
            && FortificationManager.Instance.GetSupplies(setup.AssignedTeam) < FortificationManager.GetCost(ghostType))
        {
            ghostValid = false;
            ghostInvalidReason = "NOT ENOUGH SUPPLIES ("
                + FortificationManager.Instance.GetSupplies(setup.AssignedTeam) + "/"
                + FortificationManager.GetCost(ghostType) + ")";
        }

        ApplyGhostTint(ghostValid);

        // Confirm.
        if (ghostValid && GameSettings.Pressed(GameAction.Interact) && FortificationManager.Instance != null)
        {
            FortificationManager.Instance.RequestPlace(ghostType, ghost.transform.position, ghostYaw);
            ClearGhost();
        }
    }

    [Header("Snapping")]
    [Tooltip("How close a matching neighbour must be before the ghost locks to it (floors and roofs).")]
    public float floorSnapRange = 3.5f;

    private static bool SnapsToNeighbours(FortificationType type)
    {
        return type == FortificationType.MakeshiftFloor
            || type == FortificationType.CorrugatedRoof;
    }

    // Tile pitch for the snap grid. The floor's is fixed because it also
    // drives its collider; the roof's is measured off the ghost, so it stays
    // correct whatever prop is assigned to it.
    private float TileSizeFor(FortificationType type)
    {
        if (type == FortificationType.MakeshiftFloor)
        {
            return FortificationManager.MakeshiftFloorTileSize;
        }

        if (ghost != null)
        {
            Bounds bounds = new Bounds(ghost.transform.position, Vector3.zero);
            bool found = false;

            foreach (Renderer renderer in ghost.GetComponentsInChildren<Renderer>(true))
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

            if (found)
            {
                return Mathf.Max(0.5f, Mathf.Max(bounds.size.x, bounds.size.z));
            }
        }

        return 3f;
    }

    // Snaps the ghost to the nearest matching neighbour's grid: same yaw,
    // same height, and offset by a whole number of tiles along that
    // neighbour's own axes. Because it adopts the neighbour's rotation, a run
    // stays aligned no matter which way the player is facing while placing.
    private Vector3 SnapToNeighbour(Vector3 desired, FortificationType type)
    {
        FortificationStructure nearest = null;
        float nearestDistance = floorSnapRange;

        foreach (FortificationStructure structure in FortificationStructure.All)
        {
            if (structure == null || structure.type != type)
            {
                continue;
            }

            float distance = Vector3.Distance(structure.transform.position, desired);

            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = structure;
            }
        }

        if (nearest == null)
        {
            return desired;
        }

        float tile = TileSizeFor(type);
        Transform anchor = nearest.transform;

        // Offset from the neighbour expressed in ITS local axes, rounded to
        // whole tiles, then rebuilt as a world position.
        Vector3 local = anchor.InverseTransformPoint(desired);
        local.x = Mathf.Round(local.x / tile) * tile;
        local.z = Mathf.Round(local.z / tile) * tile;
        local.y = 0f;

        // Never snap exactly on top of the neighbour.
        if (Mathf.Approximately(local.x, 0f) && Mathf.Approximately(local.z, 0f))
        {
            local.x = Mathf.Abs(local.x) >= Mathf.Abs(local.z) ? tile : 0f;
            local.z = Mathf.Approximately(local.x, 0f) ? tile : 0f;
        }

        ghostYaw = anchor.eulerAngles.y;
        return anchor.TransformPoint(local);
    }

    // Client-side mirror of the server's same-type stacking rule so the ghost
    // turns red before a doomed request is sent.
    private int CountNearbySameType(Vector3 position)
    {
        int count = 0;
        float radius = FortificationManager.Instance != null ? FortificationManager.Instance.stackCheckRadius : 1.5f;

        foreach (FortificationStructure structure in FindObjectsByType<FortificationStructure>(FindObjectsSortMode.None))
        {
            if (structure.type != ghostType)
            {
                continue;
            }

            Vector3 flat = structure.transform.position - position;
            float vertical = Mathf.Abs(flat.y);
            flat.y = 0f;

            if (flat.magnitude <= radius && vertical <= 4f)
            {
                count++;
            }
        }

        return count;
    }

    // ---- Shovel work: build blueprints, repair damaged structures ----

    private void HandleBuildInteraction()
    {
        structureScanTimer -= Time.deltaTime;

        if (structureScanTimer <= 0f)
        {
            structureScanTimer = 0.25f;
            nearestWorkable = FindNearestWorkable();
        }

        bool interactActive;

        if (GameSettings.TapToBuild)
        {
            // Tap Interact to latch build/repair; tap again (or walk away /
            // finish the structure) to stop.
            if (nearestWorkable == null)
            {
                workLatched = false;
            }
            else if (GameSettings.Pressed(GameAction.Interact))
            {
                workLatched = !workLatched;
            }

            interactActive = workLatched;
        }
        else
        {
            workLatched = false;
            interactActive = GameSettings.Held(GameAction.Interact);
        }

        bool wantsToWork = interactActive && nearestWorkable != null;
        int targetId = wantsToWork ? nearestWorkable.id : -1;

        // Rhythmic work sound while actually building or repairing. Local
        // only — it is feedback for the person swinging, not a world event.
        if (wantsToWork)
        {
            workSoundTimer -= Time.deltaTime;

            if (workSoundTimer <= 0f)
            {
                workSoundTimer = 0.55f;
                ProceduralAudio.PlayAt(ProceduralAudio.BuildTick, nearestWorkable.transform.position, 0.5f);
            }
        }
        else
        {
            workSoundTimer = 0f;
        }

        if (targetId != buildingStructureId)
        {
            buildingStructureId = targetId;

            if (FortificationManager.Instance != null)
            {
                FortificationManager.Instance.SetBuilding(buildingStructureId);
            }
        }
    }

    // "HOLD F TO BUILD " / "TAP F TO BUILD ", using the current Interact
    // bind and the tap-vs-hold gameplay setting.
    private static string BuildPromptPrefix(string verb)
    {
        string key = GameSettings.DisplayName(GameAction.Interact);
        return (GameSettings.TapToBuild ? "TAP " : "HOLD ") + key + " TO " + verb + " ";
    }

    private void StopBuilding()
    {
        if (buildingStructureId != -1)
        {
            buildingStructureId = -1;

            if (FortificationManager.Instance != null)
            {
                FortificationManager.Instance.SetBuilding(-1);
            }
        }
    }

    private FortificationStructure FindNearestWorkable()
    {
        FortificationStructure nearest = null;
        float nearestDistance = float.MaxValue;
        Team myTeam = setup != null ? setup.AssignedTeam : Team.Neutral;

        foreach (FortificationStructure structure in FindObjectsByType<FortificationStructure>(FindObjectsSortMode.None))
        {
            if (structure.team != myTeam)
            {
                continue;
            }

            bool needsWork = !structure.complete || structure.health < structure.maxHealth - 1f;

            if (!needsWork)
            {
                continue;
            }

            float distance = Vector3.Distance(transform.position, structure.transform.position);

            if (distance <= buildInteractRange && distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = structure;
            }
        }

        return nearest;
    }

    // ---- HUD ----

    private void OnGUI()
    {
        if (health != null && health.State != PlayerLifeState.Alive)
        {
            return;
        }

        if (FortificationManager.Instance == null)
        {
            GUIStyle warning = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.LowerCenter
            };
            warning.normal.textColor = Color.red;
            GUI.Label(new Rect(0f, Screen.height - 90f, Screen.width, 24f),
                "FORTIFICATION MANAGER MISSING — add the FortificationManager component to the ClassSpawnManager object",
                warning);
            return;
        }

        DrawPlacementHud();
        DrawWorkableBar();
        DrawDeconstructBar();
        DrawSuppliesOverlay();
    }

    private void DrawDeconstructBar()
    {
        if (deconstructTarget == null || deconstructTimer <= 0f)
        {
            return;
        }

        float barWidth = 220f;
        float barHeight = 10f;
        float x = (Screen.width - barWidth) * 0.5f;
        float y = Screen.height * 0.68f;

        GUIStyle labelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 13,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };
        labelStyle.normal.textColor = new Color(1f, 0.6f, 0.4f);

        GUI.Label(new Rect(0f, y - 20f, Screen.width, 18f),
            "DECONSTRUCTING " + deconstructTarget.displayName.ToUpper(), labelStyle);

        GUI.color = new Color(0f, 0f, 0f, 0.55f);
        GUI.DrawTexture(new Rect(x - 2f, y - 2f, barWidth + 4f, barHeight + 4f), Texture2D.whiteTexture);
        GUI.color = new Color(1f, 0.55f, 0.3f, 0.9f);
        GUI.DrawTexture(new Rect(x, y, barWidth * Mathf.Clamp01(deconstructTimer / deconstructHoldTime), barHeight), Texture2D.whiteTexture);
        GUI.color = Color.white;
    }

    // Tab shows both teams' supply pools (stand-in for the scoreboard).
    private void DrawSuppliesOverlay()
    {
        if (Keyboard.current == null || !Keyboard.current.tabKey.isPressed || FortificationManager.Instance == null)
        {
            return;
        }

        float width = 340f;
        float x = (Screen.width - width) * 0.5f;
        float y = Screen.height * 0.18f;

        GUI.color = new Color(0f, 0f, 0f, 0.75f);
        GUI.DrawTexture(new Rect(x, y, width, 74f), Texture2D.whiteTexture);
        GUI.color = Color.white;

        GUIStyle titleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 14,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };
        titleStyle.normal.textColor = Color.white;

        GUI.Label(new Rect(x, y + 6f, width, 20f), "TEAM SUPPLIES", titleStyle);

        GUIStyle rowStyle = new GUIStyle(titleStyle) { fontStyle = FontStyle.Normal, fontSize = 13 };
        GUI.Label(new Rect(x, y + 28f, width, 20f),
            "ALLIED POWERS: " + FortificationManager.Instance.GetSupplies(Team.AlliedPowers), rowStyle);
        GUI.Label(new Rect(x, y + 48f, width, 20f),
            "CENTRAL POWERS: " + FortificationManager.Instance.GetSupplies(Team.CentralPowers), rowStyle);
    }

    private void DrawPlacementHud()
    {
        DrawBuildMenu();
        DrawDigHint();

        List<FortificationType> types = GetPlaceableTypes();

        if (types.Count == 0)
        {
            return;
        }

        GUIStyle style = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            alignment = TextAnchor.LowerLeft
        };
        style.normal.textColor = new Color(1f, 1f, 1f, 0.75f);

        int supplies = setup != null ? FortificationManager.Instance.GetSupplies(setup.AssignedTeam) : 0;
        string text = "SUPPLIES: " + supplies
            + (CanOpenBuildMenu() ? "    [B] BUILD MENU" : "    SHOVEL OUT TO BUILD");


        if (ghost != null)
        {
            style.normal.textColor = ghostValid ? new Color(0.5f, 1f, 0.6f) : new Color(1f, 0.45f, 0.35f);
            text = ghostValid
                ? "F = PLACE " + FortificationManager.GetDisplayName(ghostType).ToUpper()
                    + " (" + FortificationManager.GetCost(ghostType) + " supplies)   scroll or [ ] = rotate (5°)   X = cancel"
                : ghostInvalidReason + "   X = cancel";
        }

        GUI.Label(new Rect(16f, Screen.height - 78f, Screen.width - 32f, 22f), text, style);
    }

    // Red hint when the player is trying to dig but the dig is blocked.
    private void DrawDigHint()
    {
        if (string.IsNullOrEmpty(digBlockedReason) || Time.time - digReasonTime > 0.25f)
        {
            return;
        }

        GUIStyle style = new GUIStyle(GUI.skin.label)
        {
            fontSize = 13,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };
        style.normal.textColor = new Color(1f, 0.45f, 0.35f);

        GUI.Label(new Rect(0f, Screen.height * 0.6f, Screen.width, 20f), digBlockedReason, style);
    }

    // Progress/health bar for the nearest structure needing work, with the
    // 25/50/75 checkpoint ticks.
    private void DrawWorkableBar()
    {
        if (ghost != null || nearestWorkable == null)
        {
            return;
        }

        float barWidth = 260f;
        float barHeight = 14f;
        float x = (Screen.width - barWidth) * 0.5f;
        float y = Screen.height * 0.62f;

        string label;
        float fill;
        Color fillColor;

        if (!nearestWorkable.complete)
        {
            fill = nearestWorkable.progress;
            fillColor = new Color(0.4f, 0.75f, 1f, 0.9f);
            label = (buildingStructureId == nearestWorkable.id ? "BUILDING " : BuildPromptPrefix("BUILD"))
                + nearestWorkable.displayName.ToUpper() + "  " + Mathf.RoundToInt(fill * 100f) + "%";
        }
        else
        {
            fill = nearestWorkable.maxHealth <= 0f ? 0f : nearestWorkable.health / nearestWorkable.maxHealth;
            fillColor = new Color(1f, 0.75f, 0.3f, 0.9f);
            label = (buildingStructureId == nearestWorkable.id ? "REPAIRING " : BuildPromptPrefix("REPAIR"))
                + nearestWorkable.displayName.ToUpper() + "  " + Mathf.RoundToInt(fill * 100f) + "%";
        }

        GUIStyle labelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 13,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };
        labelStyle.normal.textColor = Color.white;

        GUI.Label(new Rect(0f, y - 22f, Screen.width, 20f), label, labelStyle);

        GUI.color = new Color(0f, 0f, 0f, 0.55f);
        GUI.DrawTexture(new Rect(x - 2f, y - 2f, barWidth + 4f, barHeight + 4f), Texture2D.whiteTexture);

        GUI.color = fillColor;
        GUI.DrawTexture(new Rect(x, y, barWidth * Mathf.Clamp01(fill), barHeight), Texture2D.whiteTexture);

        // Checkpoint ticks at 25/50/75. While building, a passed checkpoint
        // is banked — progress can never fall back below it — so reached
        // ticks are drawn bright and unreached ones stay dark.
        for (int i = 1; i <= 3; i++)
        {
            bool banked = !nearestWorkable.complete && fill >= i * 0.25f;

            GUI.color = banked
                ? new Color(1f, 1f, 1f, 0.95f)
                : new Color(0f, 0f, 0f, 0.8f);

            float tickWidth = banked ? 3f : 2f;
            GUI.DrawTexture(new Rect(x + barWidth * 0.25f * i - tickWidth * 0.5f, y, tickWidth, barHeight),
                Texture2D.whiteTexture);
        }

        GUI.color = Color.white;
    }
}
