using System.Collections.Generic;
using FishNet;
using UnityEngine;

// Class + spawn selection screen. Shows whenever this client is connected but
// has no live player object: on first join and again after each death.
public class ClassSelectHud : MonoBehaviour
{
    // Left-to-right display order of the class bar.
    private static readonly PlayerClass[] ClassOrder =
    {
        PlayerClass.Assault,
        PlayerClass.Support,
        PlayerClass.Medic,
        PlayerClass.Scout,
        PlayerClass.Engineer,
        PlayerClass.Officer
    };

    // The owner's team, reported by PlayerNetworkSetup on spawn so the spawn
    // selector knows which objectives are friendly after the first life.
    public static Team LastKnownTeam { get; set; } = Team.Neutral;

    private PlayerClass selectedClass = PlayerClass.Assault;
    private int selectedWeaponIndex;
    private int selectedSpawnIndex = -1;
    private Team selectedTeam = Team.Neutral;

    private Camera fallbackCamera;

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

        // Without a live player there is no camera in the scene (the player
        // prefab owns it), so provide a battlefield overview behind the menu.
        bool needFallbackCamera = !InstanceFinder.IsClientStarted
            || InstanceFinder.ClientManager.Connection.FirstObject == null;

        EnsureFallbackCamera(needFallbackCamera);
    }

    private void EnsureFallbackCamera(bool active)
    {
        if (!active)
        {
            if (fallbackCamera != null)
            {
                fallbackCamera.gameObject.SetActive(false);
            }

            return;
        }

        if (fallbackCamera == null)
        {
            Vector3 focus = Vector3.zero;
            int zoneCount = 0;

            foreach (ObjectiveCaptureZone zone in FindObjectsByType<ObjectiveCaptureZone>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                focus += zone.transform.position;
                zoneCount++;
            }

            if (zoneCount > 0)
            {
                focus /= zoneCount;
            }

            GameObject cameraObject = new GameObject("ClassSelectCamera");
            fallbackCamera = cameraObject.AddComponent<Camera>();
            cameraObject.AddComponent<AudioListener>();
            cameraObject.transform.position = focus + new Vector3(0f, 30f, -35f);
            cameraObject.transform.LookAt(focus);
        }

        fallbackCamera.gameObject.SetActive(true);
    }

    private void OnGUI()
    {
        if (!ShouldShow())
        {
            return;
        }

        GuiScale.Begin();

        // After the first life, default to the team already assigned.
        if (selectedTeam == Team.Neutral && LastKnownTeam != Team.Neutral)
        {
            selectedTeam = LastKnownTeam;
        }

        DrawTeamSelector();
        DrawClassBar();
        DrawWeaponSelector();
        DrawSpawnSelector();
        DrawDeployButton();
    }

    private void DrawTeamSelector()
    {
        const float buttonWidth = 210f;
        const float buttonHeight = 38f;
        const float spacing = 12f;
        const float rowY = 12f;

        float totalWidth = buttonWidth * 2f + spacing;
        float x = (GuiScale.Width - totalWidth) * 0.5f;

        GUIStyle buttonStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 15,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };

        if (GUI.Button(new Rect(x, rowY, buttonWidth, buttonHeight),
            (selectedTeam == Team.AlliedPowers ? "▶ " : "") + "ALLIED POWERS", buttonStyle))
        {
            selectedTeam = Team.AlliedPowers;
            selectedSpawnIndex = -1;
        }

        if (GUI.Button(new Rect(x + buttonWidth + spacing, rowY, buttonWidth, buttonHeight),
            (selectedTeam == Team.CentralPowers ? "▶ " : "") + "CENTRAL POWERS", buttonStyle))
        {
            selectedTeam = Team.CentralPowers;
            selectedSpawnIndex = -1;
        }
    }

    // Horizontal class bar across the top of the screen.
    private void DrawClassBar()
    {
        const float buttonWidth = 150f;
        const float buttonHeight = 86f;
        const float spacing = 8f;
        const float barY = 96f;

        float totalWidth = ClassOrder.Length * (buttonWidth + spacing) - spacing;
        float x = (GuiScale.Width - totalWidth) * 0.5f;

        GUIStyle titleStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 24,
            fontStyle = FontStyle.Bold
        };

        GUI.Label(new Rect(0f, 58f, GuiScale.Width, 32f), "SELECT CLASS", titleStyle);

        GUIStyle buttonStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 14,
            alignment = TextAnchor.MiddleCenter,
            richText = true
        };

        for (int i = 0; i < ClassOrder.Length; i++)
        {
            PlayerClass playerClass = ClassOrder[i];
            PlayerClassDefinition definition = PlayerClasses.Get(playerClass);

            string marker = playerClass == selectedClass ? "▶ " : "";
            string label = marker + definition.displayName
                + "\n<size=11>" + definition.grenade
                + "\n" + definition.equipmentSlot1
                + "\n" + definition.equipmentSlot2 + "</size>";

            Rect rect = new Rect(x + i * (buttonWidth + spacing), barY, buttonWidth, buttonHeight);

            if (GUI.Button(rect, label, buttonStyle))
            {
                if (selectedClass != playerClass)
                {
                    selectedWeaponIndex = 0;
                }

                selectedClass = playerClass;
            }
        }
    }

    // Primary weapon row: only drawn when the class has a choice (Assault).
    private void DrawWeaponSelector()
    {
        WeaponId[] options = PlayerClasses.Get(selectedClass).weaponOptions;

        if (options == null || options.Length <= 1)
        {
            selectedWeaponIndex = 0;
            return;
        }

        const float buttonWidth = 160f;
        const float buttonHeight = 32f;
        const float spacing = 8f;
        const float rowY = 190f;

        selectedWeaponIndex = Mathf.Clamp(selectedWeaponIndex, 0, options.Length - 1);

        float totalWidth = options.Length * (buttonWidth + spacing) - spacing;
        float x = (GuiScale.Width - totalWidth) * 0.5f;

        GUIStyle buttonStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 12,
            alignment = TextAnchor.MiddleCenter
        };

        for (int i = 0; i < options.Length; i++)
        {
            string label = (selectedWeaponIndex == i ? "▶ " : "") + WeaponProfiles.Get(options[i]).displayName;
            Rect rect = new Rect(x + i * (buttonWidth + spacing), rowY, buttonWidth, buttonHeight);

            if (GUI.Button(rect, label, buttonStyle))
            {
                selectedWeaponIndex = i;
            }
        }
    }

    // Spawn point row below the class bar: team base plus every objective the
    // player's team currently owns.
    private void DrawSpawnSelector()
    {
        const float buttonWidth = 92f;
        const float buttonHeight = 40f;
        const float spacing = 8f;
        const float rowY = 256f;

        List<ObjectiveCaptureZone> zones = ClassSpawnManager.GetZonesSortedByLetter();

        float totalWidth = (zones.Count + 1) * (buttonWidth + spacing) - spacing;
        float x = (GuiScale.Width - totalWidth) * 0.5f;

        GUIStyle labelStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 15,
            fontStyle = FontStyle.Bold
        };

        GUI.Label(new Rect(0f, rowY - 26f, GuiScale.Width, 22f), "SPAWN POINT", labelStyle);

        GUIStyle buttonStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 13,
            alignment = TextAnchor.MiddleCenter
        };

        Rect baseRect = new Rect(x, rowY, buttonWidth, buttonHeight);
        string baseLabel = (selectedSpawnIndex < 0 ? "▶ " : "") + "BASE";

        if (GUI.Button(baseRect, baseLabel, buttonStyle))
        {
            selectedSpawnIndex = -1;
        }

        for (int i = 0; i < zones.Count; i++)
        {
            bool owned = selectedTeam != Team.Neutral && zones[i].controllingTeam == selectedTeam;

            // A spawn that stopped being available falls back to base.
            if (selectedSpawnIndex == i && !owned)
            {
                selectedSpawnIndex = -1;
            }

            GUI.enabled = owned;

            string label = (selectedSpawnIndex == i ? "▶ " : "") + zones[i].objectiveLetter;
            Rect rect = new Rect(x + (i + 1) * (buttonWidth + spacing), rowY, buttonWidth, buttonHeight);

            if (GUI.Button(rect, label, buttonStyle))
            {
                selectedSpawnIndex = i;
            }

            GUI.enabled = true;
        }
    }

    private void DrawDeployButton()
    {
        const float width = 220f;
        const float height = 52f;

        GUIStyle deployStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 20,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };

        Rect rect = new Rect((GuiScale.Width - width) * 0.5f, 318f, width, height);

        bool teamPicked = selectedTeam != Team.Neutral;
        GUI.enabled = teamPicked;

        if (GUI.Button(rect, teamPicked ? "DEPLOY" : "PICK A TEAM", deployStyle))
        {
            ClassSpawnManager.Instance.RequestSpawn(selectedTeam, selectedClass, selectedWeaponIndex, selectedSpawnIndex);
        }

        GUI.enabled = true;
    }
}
