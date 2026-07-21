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

    public float deconstructHoldTime = 2f;
    private float deconstructTimer;
    private FortificationStructure deconstructTarget;

    private void Start()
    {
        health = GetComponent<PlayerNetworkHealth>();
        setup = GetComponent<PlayerNetworkSetup>();

        if (playerCamera == null)
        {
            playerCamera = GetComponentInChildren<Camera>();
        }
    }

    private void OnDestroy()
    {
        ClearGhost();
        StopBuilding();
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
        }

        return types;
    }

    private void Update()
    {
        if (Keyboard.current == null)
        {
            return;
        }

        if (health != null && health.State != PlayerLifeState.Alive)
        {
            ClearGhost();
            StopBuilding();
            return;
        }

        if (PauseMenu.IsOpen)
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

    // Digging requires the trench shovel held (an equipment slot):
    // PlayerItemSlots calls this every frame LMB (dig) or RMB (fill) is
    // held. The server paces scoops by class dig multiplier (Engineer 2x)
    // and enforces every rule.
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

        if (TerrainDigManager.BlockedByStructure(hit.point, digManager.structureClearance))
        {
            SetDigBlockedReason("TOO CLOSE TO A STRUCTURE");
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

    private void HandlePlacementSelection()
    {
        List<FortificationType> types = GetPlaceableTypes();

        // 1-4 belong to the item-slot system; structures live on 5-8.
        Key[] keys = { Key.Digit5, Key.Digit6, Key.Digit7, Key.Digit8 };

        for (int i = 0; i < types.Count && i < keys.Length; i++)
        {
            if (Keyboard.current[keys[i]].wasPressedThisFrame)
            {
                if (ghost != null && ghostType == types[i])
                {
                    ClearGhost();
                }
                else
                {
                    StartGhost(types[i]);
                }

                return;
            }
        }

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

        foreach (Renderer ghostRenderer in ghost.GetComponentsInChildren<Renderer>(true))
        {
            Material ghostMaterial = new Material(transparentShader)
            {
                color = new Color(0.4f, 0.9f, 1f, 0.2f)
            };

            Material[] materials = new Material[ghostRenderer.sharedMaterials.Length];

            for (int i = 0; i < materials.Length; i++)
            {
                materials[i] = ghostMaterial;
            }

            ghostRenderer.materials = materials;
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

        ghost.transform.position = targetPoint;
        ghost.transform.rotation = Quaternion.Euler(0f, ghostYaw, 0f);

        ghostValid = true;
        ghostInvalidReason = "";

        if (!grounded || Vector3.Distance(transform.position, targetPoint) > maxPlaceDistance)
        {
            ghostValid = false;
            ghostInvalidReason = "TOO FAR";
        }
        else if (supporting != null && !CanStackOn(supporting))
        {
            ghostValid = false;
            ghostInvalidReason = supporting.type == ghostType && !supporting.complete
                ? "FINISH THE ONE BELOW FIRST"
                : "MUST BE PLACED ON GROUND";
        }
        else if (supporting == null && Vector3.Angle(groundNormal, Vector3.up) > maxGroundAngle)
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

        // Confirm.
        if (ghostValid && GameSettings.Pressed(GameAction.Interact) && FortificationManager.Instance != null)
        {
            FortificationManager.Instance.RequestPlace(ghostType, ghost.transform.position, ghostYaw);
            ClearGhost();
        }
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

        if (targetId != buildingStructureId)
        {
            buildingStructureId = targetId;

            if (FortificationManager.Instance != null)
            {
                FortificationManager.Instance.SetBuilding(buildingStructureId);
            }
        }
    }

    // Mirrors the server rule: you may stack only onto a COMPLETED structure
    // of the same type, and only for types that allow a stack (not wire).
    private bool CanStackOn(FortificationStructure supporting)
    {
        int limit = FortificationManager.GetStackLimit(ghostType,
            FortificationManager.Instance != null ? FortificationManager.Instance.maxSameTypeStack : 2);

        return supporting.type == ghostType && supporting.complete && limit > 1;
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
        string text = "SUPPLIES: " + supplies + "    PLACE: ";

        for (int i = 0; i < types.Count; i++)
        {
            text += " [" + (5 + i) + "] " + FortificationManager.GetDisplayName(types[i])
                + " (" + FortificationManager.GetCost(types[i]) + ")";
        }


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

        // Checkpoint ticks at 25/50/75.
        GUI.color = new Color(0f, 0f, 0f, 0.8f);

        for (int i = 1; i <= 3; i++)
        {
            GUI.DrawTexture(new Rect(x + barWidth * 0.25f * i - 1f, y, 2f, barHeight), Texture2D.whiteTexture);
        }

        GUI.color = Color.white;
    }
}
