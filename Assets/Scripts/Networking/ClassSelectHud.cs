using System.Collections.Generic;
using FishNet;
using UnityEngine;

// Battlefield-style deploy screen shown whenever this client has no live
// player:
//   - Top-down map camera over the battlefield.
//   - Objectives and the team base drawn as clickable spawn markers on the
//     map itself.
//   - Class strip and loadout bar along the bottom, DEPLOY bottom-right.
// First join shows a team pick; after that the team is remembered (CHANGE
// TEAM bottom-left). Loadout choices persist per class (LoadoutData).
public class ClassSelectHud : MonoBehaviour
{
    private static readonly PlayerClass[] ClassOrder =
    {
        PlayerClass.Assault,
        PlayerClass.Support,
        PlayerClass.Medic,
        PlayerClass.Scout,
        PlayerClass.Engineer,
        PlayerClass.Officer
    };

    public static Team LastKnownTeam { get; set; } = Team.Neutral;

    [Header("Map Camera")]
    public float minCameraHeight = 60f;
    public float maxCameraHeight = 220f;

    private PlayerClass selectedClass = PlayerClass.Assault;
    private LoadoutSelection loadout;
    private bool loadoutLoaded;
    private int selectedSpawnIndex = -1;
    private Team selectedTeam = Team.Neutral;
    private bool teamConfirmed;

    private Camera fallbackCamera;
    private bool cameraFramed;
    private static Texture2D discTexture;

    // Which loadout dropdown is open: 0 none, 1 weapon, 2 grenade,
    // 3 equipment slot 1, 4 equipment slot 2.
    private int openDropdown;

    private bool ShouldShow()
    {
        if (!InstanceFinder.IsClientStarted)
        {
            return false;
        }

        if (InstanceFinder.ClientManager.Connection.FirstObject != null)
        {
            return false;
        }

        return ClassSpawnManager.Instance != null && ClassSpawnManager.Instance.gameObject.activeInHierarchy;
    }

    private void Update()
    {
        if (ShouldShow())
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        bool needFallbackCamera = !InstanceFinder.IsClientStarted
            || InstanceFinder.ClientManager.Connection.FirstObject == null;

        EnsureFallbackCamera(needFallbackCamera);
    }

    // Straight-down map view framing the objectives and both base areas.
    private void EnsureFallbackCamera(bool active)
    {
        if (!active)
        {
            if (fallbackCamera != null)
            {
                fallbackCamera.gameObject.SetActive(false);
            }

            cameraFramed = false;
            return;
        }

        if (fallbackCamera == null)
        {
            GameObject cameraObject = new GameObject("DeployMapCamera");
            fallbackCamera = cameraObject.AddComponent<Camera>();
            cameraObject.AddComponent<AudioListener>();

            // A tight near plane at map height avoids depth-fighting flicker
            // on overlapping ground geometry seen from straight above.
            fallbackCamera.nearClipPlane = 5f;
            fallbackCamera.farClipPlane = 2000f;
        }

        fallbackCamera.gameObject.SetActive(true);

        // Frame the battlefield ONCE per activation; recomputing every frame
        // caused visible shaking.
        if (cameraFramed)
        {
            return;
        }

        cameraFramed = true;

        Vector3 focus = Vector3.zero;
        float extent = 30f;
        int pointCount = 0;

        foreach (Vector3 point in GetFramingPoints())
        {
            focus += point;
            pointCount++;
        }

        if (pointCount > 0)
        {
            focus /= pointCount;

            foreach (Vector3 point in GetFramingPoints())
            {
                Vector3 flat = point - focus;
                flat.y = 0f;
                extent = Mathf.Max(extent, flat.magnitude);
            }
        }

        float height = Mathf.Clamp(extent * 1.6f, minCameraHeight, maxCameraHeight);

        fallbackCamera.transform.position = focus + Vector3.up * height;
        fallbackCamera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
    }

    private IEnumerable<Vector3> GetFramingPoints()
    {
        foreach (ObjectiveCaptureZone zone in FindObjectsByType<ObjectiveCaptureZone>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            yield return zone.transform.position;
        }

        Transform allied = TeamSpawnArea.GetSpawnPoint(Team.AlliedPowers);
        Transform central = TeamSpawnArea.GetSpawnPoint(Team.CentralPowers);

        if (allied != null)
        {
            yield return allied.position;
        }

        if (central != null)
        {
            yield return central.position;
        }
    }

    private void EnsureLoadout()
    {
        if (!loadoutLoaded)
        {
            loadoutLoaded = true;
            loadout = LoadoutData.Get(selectedClass);
        }
    }

    private void SelectClass(PlayerClass playerClass)
    {
        if (selectedClass == playerClass)
        {
            return;
        }

        LoadoutData.Set(selectedClass, loadout);
        selectedClass = playerClass;
        loadout = LoadoutData.Get(playerClass);
    }

    private static Texture2D DiscTexture()
    {
        if (discTexture != null)
        {
            return discTexture;
        }

        const int size = 64;
        discTexture = new Texture2D(size, size, TextureFormat.RGBA32, false);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x - size * 0.5f + 0.5f;
                float dy = y - size * 0.5f + 0.5f;
                float distance = Mathf.Sqrt(dx * dx + dy * dy) / (size * 0.5f);
                float alpha = Mathf.Clamp01((1f - distance) * 8f);
                discTexture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
        }

        discTexture.Apply();

        return discTexture;
    }

    private void OnGUI()
    {
        if (!ShouldShow())
        {
            return;
        }

        GuiScale.Begin();
        EnsureLoadout();

        if (!teamConfirmed && selectedTeam == Team.Neutral && LastKnownTeam != Team.Neutral)
        {
            selectedTeam = LastKnownTeam;
            teamConfirmed = true;
        }

        if (!teamConfirmed)
        {
            DrawTeamScreen();
            return;
        }

        // While a dropdown is open, everything except that dropdown is inert
        // so its option list (drawn last, on top) gets the clicks.
        GUI.enabled = openDropdown == 0;

        DrawTopBar();
        DrawMapSpawnMarkers();
        DrawBottomPanel();
        DrawDeployButton();
        DrawChangeTeamButton();

        GUI.enabled = true;
        DrawOpenDropdownList();
    }

    // ---- Screen 1: team selection ----

    private void DrawTeamScreen()
    {
        GUI.color = new Color(0f, 0f, 0f, 0.65f);
        GUI.DrawTexture(new Rect(0f, 0f, GuiScale.Width, GuiScale.Height), Texture2D.whiteTexture);
        GUI.color = Color.white;

        GUIStyle titleStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 38,
            fontStyle = FontStyle.Bold
        };
        titleStyle.normal.textColor = Color.white;

        GUI.Label(new Rect(0f, GuiScale.Height * 0.26f, GuiScale.Width, 48f), "WIRE AND WARFARE", titleStyle);

        GUIStyle subtitleStyle = new GUIStyle(titleStyle) { fontSize = 17, fontStyle = FontStyle.Normal };
        subtitleStyle.normal.textColor = new Color(1f, 1f, 1f, 0.75f);
        GUI.Label(new Rect(0f, GuiScale.Height * 0.26f + 52f, GuiScale.Width, 26f), "SELECT YOUR TEAM", subtitleStyle);

        const float buttonWidth = 300f;
        const float buttonHeight = 80f;
        const float spacing = 40f;

        float y = GuiScale.Height * 0.45f;
        float x = (GuiScale.Width - buttonWidth * 2f - spacing) * 0.5f;

        GUIStyle buttonStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 20,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };

        if (GUI.Button(new Rect(x, y, buttonWidth, buttonHeight), "ALLIED POWERS", buttonStyle))
        {
            selectedTeam = Team.AlliedPowers;
            teamConfirmed = true;
            selectedSpawnIndex = -1;
        }

        if (GUI.Button(new Rect(x + buttonWidth + spacing, y, buttonWidth, buttonHeight), "CENTRAL POWERS", buttonStyle))
        {
            selectedTeam = Team.CentralPowers;
            teamConfirmed = true;
            selectedSpawnIndex = -1;
        }
    }

    // ---- Deploy screen chrome ----

    private void DrawTopBar()
    {
        GUI.color = new Color(0f, 0f, 0f, 0.55f);
        GUI.DrawTexture(new Rect(0f, 0f, GuiScale.Width, 44f), Texture2D.whiteTexture);
        GUI.color = Color.white;

        GUIStyle nameStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 16,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft
        };
        nameStyle.normal.textColor = Color.white;

        string teamName = selectedTeam == Team.AlliedPowers ? "ALLIED POWERS" : "CENTRAL POWERS";
        GUI.Label(new Rect(20f, 0f, 600f, 44f), "WIRE AND WARFARE   |   " + teamName, nameStyle);

        GUIStyle hintStyle = new GUIStyle(nameStyle)
        {
            alignment = TextAnchor.MiddleRight,
            fontSize = 13,
            fontStyle = FontStyle.Normal
        };
        hintStyle.normal.textColor = new Color(1f, 1f, 1f, 0.7f);
        GUI.Label(new Rect(GuiScale.Width - 420f, 0f, 400f, 44f), "CLICK A MARKER ON THE MAP TO PICK YOUR SPAWN", hintStyle);
    }

    // Objectives and the team base as clickable markers projected onto the
    // map view.
    private void DrawMapSpawnMarkers()
    {
        if (fallbackCamera == null || !fallbackCamera.isActiveAndEnabled)
        {
            return;
        }

        List<ObjectiveCaptureZone> zones = ClassSpawnManager.GetZonesSortedByLetter();

        for (int i = 0; i < zones.Count; i++)
        {
            bool owned = zones[i].controllingTeam == selectedTeam;

            if (selectedSpawnIndex == i && !owned)
            {
                selectedSpawnIndex = -1;
            }

            Color markerColor;

            if (zones[i].controllingTeam == Team.Neutral)
            {
                markerColor = new Color(0.55f, 0.55f, 0.55f);
            }
            else
            {
                markerColor = owned ? new Color(0.25f, 0.5f, 0.95f) : new Color(0.85f, 0.25f, 0.2f);
            }

            if (DrawMapMarker(zones[i].transform.position, zones[i].objectiveLetter, markerColor,
                selectedSpawnIndex == i, owned))
            {
                selectedSpawnIndex = i;
            }
        }

        Transform baseSpawn = TeamSpawnArea.GetSpawnPoint(selectedTeam);

        if (baseSpawn != null)
        {
            if (DrawMapMarker(baseSpawn.position, "HQ", new Color(0.25f, 0.5f, 0.95f),
                selectedSpawnIndex < 0, true))
            {
                selectedSpawnIndex = -1;
            }
        }
    }

    // Returns true when clicked. Selectable markers get a white selection
    // ring; unselectable ones are dimmed.
    private bool DrawMapMarker(Vector3 worldPosition, string label, Color color, bool selected, bool selectable)
    {
        Vector3 screenPoint = fallbackCamera.WorldToScreenPoint(worldPosition);

        if (screenPoint.z <= 0f)
        {
            return false;
        }

        float x = screenPoint.x / GuiScale.Factor;
        float y = (Screen.height - screenPoint.y) / GuiScale.Factor;

        const float diameter = 46f;
        Rect rect = new Rect(x - diameter * 0.5f, y - diameter * 0.5f, diameter, diameter);

        if (selected)
        {
            GUI.color = Color.white;
            float ring = diameter + 10f;
            GUI.DrawTexture(new Rect(x - ring * 0.5f, y - ring * 0.5f, ring, ring), DiscTexture());
        }

        GUI.color = selectable ? color : color * 0.65f;
        GUI.DrawTexture(rect, DiscTexture());
        GUI.color = Color.white;

        GUIStyle letterStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = label.Length > 1 ? 15 : 20,
            fontStyle = FontStyle.Bold
        };
        letterStyle.normal.textColor = Color.white;
        GUI.Label(rect, label, letterStyle);

        if (selected)
        {
            GUIStyle deployHereStyle = new GUIStyle(letterStyle) { fontSize = 11 };
            GUI.Label(new Rect(x - 60f, y + diameter * 0.5f + 2f, 120f, 16f), "DEPLOY HERE", deployHereStyle);
        }

        if (!selectable || openDropdown != 0)
        {
            return false;
        }

        Event current = Event.current;

        return current.type == EventType.MouseDown && current.button == 0
            && rect.Contains(current.mousePosition) && ClickConsumed(current);
    }

    private static bool ClickConsumed(Event current)
    {
        current.Use();
        return true;
    }

    // ---- Bottom panel: classes + loadout ----

    private void DrawBottomPanel()
    {
        float panelTop = GuiScale.Height - 150f;

        GUI.color = new Color(0f, 0f, 0f, 0.7f);
        GUI.DrawTexture(new Rect(0f, panelTop, GuiScale.Width, 150f), Texture2D.whiteTexture);
        GUI.color = Color.white;

        DrawClassStrip(panelTop + 10f);
        DrawLoadoutStrip(panelTop + 58f);
        DrawKitLine(panelTop + 100f);
    }

    private void DrawClassStrip(float y)
    {
        const float buttonWidth = 128f;
        const float buttonHeight = 36f;
        const float spacing = 6f;

        float totalWidth = ClassOrder.Length * (buttonWidth + spacing) - spacing;
        float x = (GuiScale.Width - totalWidth) * 0.5f;

        for (int i = 0; i < ClassOrder.Length; i++)
        {
            PlayerClass playerClass = ClassOrder[i];
            bool isSelected = playerClass == selectedClass;

            GUIStyle style = new GUIStyle(GUI.skin.button)
            {
                fontSize = isSelected ? 15 : 13,
                fontStyle = isSelected ? FontStyle.Bold : FontStyle.Normal,
                alignment = TextAnchor.MiddleCenter
            };

            if (isSelected)
            {
                style.normal.textColor = Color.white;
            }

            Rect rect = new Rect(x + i * (buttonWidth + spacing), y, buttonWidth, buttonHeight);

            string label = (isSelected ? "✚ " : "") + PlayerClasses.Get(playerClass).displayName.ToUpper();

            if (GUI.Button(rect, label, style))
            {
                SelectClass(playerClass);
            }
        }
    }

    // Dropdown geometry, shared by the header row and the open option list.
    private void GetDropdownRect(int id, out Rect rect)
    {
        float y = GuiScale.Height - 92f;
        float x = (GuiScale.Width - 672f) * 0.5f;

        switch (id)
        {
            case 1: rect = new Rect(x, y, 210f, 32f); break;
            case 2: rect = new Rect(x + 218f, y, 190f, 32f); break;
            case 3: rect = new Rect(x + 416f, y, 120f, 32f); break;
            default: rect = new Rect(x + 544f, y, 128f, 32f); break;
        }
    }

    // Option labels for a dropdown in the current state.
    private List<string> GetDropdownOptions(int id)
    {
        List<string> options = new List<string>();

        switch (id)
        {
            case 1:
                WeaponId[] weapons = PlayerClasses.Get(selectedClass).weaponOptions;

                foreach (WeaponId weapon in weapons)
                {
                    options.Add(WeaponProfiles.Get(weapon).displayName);
                }

                break;

            case 2:
                foreach (GrenadeType grenade in LoadoutData.AssaultGrenadePool)
                {
                    options.Add(LoadoutData.GetGrenadeName(grenade));
                }

                break;

            case 3:
            case 4:
                EquipmentType other = id == 3 ? loadout.equipment2 : loadout.equipment1;

                foreach (EquipmentType equipment in LoadoutData.AssaultEquipmentPool)
                {
                    if (equipment != other)
                    {
                        options.Add(LoadoutData.GetEquipmentName(equipment));
                    }
                }

                break;
        }

        return options;
    }

    private void ApplyDropdownChoice(int id, int optionIndex)
    {
        switch (id)
        {
            case 1:
                loadout.weaponIndex = optionIndex;
                break;

            case 2:
                loadout.grenade = LoadoutData.AssaultGrenadePool[optionIndex];
                break;

            case 3:
            case 4:
                EquipmentType other = id == 3 ? loadout.equipment2 : loadout.equipment1;
                int seen = 0;

                foreach (EquipmentType equipment in LoadoutData.AssaultEquipmentPool)
                {
                    if (equipment == other)
                    {
                        continue;
                    }

                    if (seen == optionIndex)
                    {
                        if (id == 3)
                        {
                            loadout.equipment1 = equipment;
                        }
                        else
                        {
                            loadout.equipment2 = equipment;
                        }

                        break;
                    }

                    seen++;
                }

                break;
        }

        LoadoutData.Set(selectedClass, loadout);
    }

    private void DrawLoadoutStrip(float y)
    {
        bool customizable = PlayerClasses.Get(selectedClass).customizableLoadout;
        WeaponId[] weapons = PlayerClasses.Get(selectedClass).weaponOptions;

        if (weapons == null || weapons.Length == 0)
        {
            return;
        }

        loadout.weaponIndex = Mathf.Clamp(loadout.weaponIndex, 0, weapons.Length - 1);

        GUIStyle buttonStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 12,
            alignment = TextAnchor.MiddleCenter
        };

        DrawDropdownHeader(1, "WEAPON: " + WeaponProfiles.Get(weapons[loadout.weaponIndex]).displayName,
            weapons.Length > 1, buttonStyle);
        DrawDropdownHeader(2, "GRENADE: " + LoadoutData.GetGrenadeName(loadout.grenade), customizable, buttonStyle);
        DrawDropdownHeader(3, LoadoutData.GetEquipmentName(loadout.equipment1), customizable, buttonStyle);
        DrawDropdownHeader(4, LoadoutData.GetEquipmentName(loadout.equipment2), customizable, buttonStyle);
    }

    private void DrawDropdownHeader(int id, string label, bool interactable, GUIStyle style)
    {
        GetDropdownRect(id, out Rect rect);

        // The open dropdown's header stays clickable (to close it); other
        // controls are disabled while any dropdown is open.
        GUI.enabled = interactable && (openDropdown == 0 || openDropdown == id);

        string suffix = interactable ? (openDropdown == id ? "  ▾" : "  ▴") : "";

        if (GUI.Button(rect, label + suffix, style))
        {
            openDropdown = openDropdown == id ? 0 : id;
        }

        GUI.enabled = openDropdown == 0;
    }

    // The open dropdown's option list, drawn last so it sits on top of the
    // panel. Opens upward from its header.
    private void DrawOpenDropdownList()
    {
        if (openDropdown == 0)
        {
            return;
        }

        GetDropdownRect(openDropdown, out Rect header);
        List<string> options = GetDropdownOptions(openDropdown);

        const float rowHeight = 30f;
        float listHeight = options.Count * rowHeight;
        Rect listRect = new Rect(header.x, header.y - listHeight - 4f, header.width, listHeight);

        GUI.color = new Color(0f, 0f, 0f, 0.9f);
        GUI.DrawTexture(new Rect(listRect.x - 2f, listRect.y - 2f, listRect.width + 4f, listRect.height + 4f),
            Texture2D.whiteTexture);
        GUI.color = Color.white;

        GUIStyle optionStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 12,
            alignment = TextAnchor.MiddleLeft
        };

        for (int i = 0; i < options.Count; i++)
        {
            Rect row = new Rect(listRect.x, listRect.y + i * rowHeight, listRect.width, rowHeight);

            if (GUI.Button(row, "  " + options[i], optionStyle))
            {
                ApplyDropdownChoice(openDropdown, i);
                openDropdown = 0;
            }
        }
    }

    private void DrawKitLine(float y)
    {
        GUIStyle style = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            alignment = TextAnchor.MiddleCenter
        };
        style.normal.textColor = new Color(1f, 1f, 1f, 0.6f);

        GUI.Label(new Rect(0f, y, GuiScale.Width, 20f), PlayerClasses.Get(selectedClass).description, style);
    }

    // ---- Deploy / change team ----

    private void DrawDeployButton()
    {
        GUIStyle deployStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 19,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };

        Rect rect = new Rect(GuiScale.Width - 240f, GuiScale.Height - 218f, 210f, 54f);

        if (GUI.Button(rect, "DEPLOY", deployStyle))
        {
            LoadoutData.Set(selectedClass, loadout);

            ClassSpawnManager.Instance.RequestSpawn(selectedTeam, selectedClass, loadout.weaponIndex,
                loadout.grenade, loadout.equipment1, loadout.equipment2, selectedSpawnIndex);
        }
    }

    private void DrawChangeTeamButton()
    {
        GUIStyle style = new GUIStyle(GUI.skin.button) { fontSize = 12 };

        if (GUI.Button(new Rect(30f, GuiScale.Height - 204f, 130f, 30f), "CHANGE TEAM", style))
        {
            teamConfirmed = false;
            selectedTeam = Team.Neutral;
            selectedSpawnIndex = -1;
        }
    }
}
