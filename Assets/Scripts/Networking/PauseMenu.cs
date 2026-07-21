using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

// Owner-side pause menu (Escape). Gameplay keeps running (multiplayer), but
// look and fire input are suppressed while open via IsOpen.
//
// Hosts the settings screen: Graphics, Audio, Keybinds, Gameplay. All values
// live in GameSettings (PlayerPrefs-backed); this class is just the UI.
public class PauseMenu : MonoBehaviour
{
    public static bool IsOpen { get; private set; }

    private enum Panel { Root, Options }
    private enum OptionsTab { Graphics, Audio, Keybinds, Gameplay }

    private Panel panel = Panel.Root;
    private OptionsTab optionsTab = OptionsTab.Graphics;

    // Non-null while waiting for the player to press a key for a rebind.
    private GameAction? rebindTarget;
    private Vector2 keybindScroll;

    private PlayerNetworkHealth health;

    private void Start()
    {
        health = GetComponent<PlayerNetworkHealth>();
    }

    private void OnDestroy()
    {
        IsOpen = false;
    }

    private void Update()
    {
        if (Keyboard.current == null)
        {
            return;
        }

        if (Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            if (rebindTarget != null)
            {
                rebindTarget = null; // cancel a pending rebind
            }
            else if (IsOpen && panel == Panel.Options)
            {
                panel = Panel.Root; // back out of options first
            }
            else
            {
                SetOpen(!IsOpen);
            }

            return;
        }

        // Listening for a rebind: the next non-Escape key becomes the bind.
        if (rebindTarget != null && IsOpen)
        {
            foreach (KeyControl control in Keyboard.current.allKeys)
            {
                if (control.keyCode == Key.Escape || control.keyCode == Key.None)
                {
                    continue;
                }

                if (control.wasPressedThisFrame)
                {
                    GameSettings.SetKey(rebindTarget.Value, control.keyCode);
                    GameSettings.Save();
                    rebindTarget = null;
                    break;
                }
            }
        }
    }

    private void SetOpen(bool open)
    {
        IsOpen = open;

        if (!open)
        {
            panel = Panel.Root;
            rebindTarget = null;
            GameSettings.Save(); // persist any pending slider/toggle changes
        }

        Cursor.lockState = open ? CursorLockMode.None : CursorLockMode.Locked;
        Cursor.visible = open;
    }

    // ---- GUI ----

    private void OnGUI()
    {
        if (!IsOpen)
        {
            return;
        }

        GUI.color = new Color(0f, 0f, 0f, 0.6f);
        GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
        GUI.color = Color.white;

        const float width = 480f;
        const float panelHeight = 520f;
        float x = (Screen.width - width) * 0.5f;
        float y = (Screen.height - panelHeight) * 0.5f;

        GUILayout.BeginArea(new Rect(x, y, width, panelHeight), GUI.skin.box);
        GUILayout.Space(10f);

        GUILayout.Label(panel == Panel.Root ? "PAUSED" : "OPTIONS", TitleStyle());
        GUILayout.Space(12f);

        if (panel == Panel.Root)
        {
            DrawRoot();
        }
        else
        {
            DrawOptions();
        }

        GUILayout.EndArea();
    }

    private void DrawRoot()
    {
        if (GUILayout.Button("RESUME", ButtonStyle(), GUILayout.Height(44f)))
        {
            SetOpen(false);
        }

        GUILayout.Space(8f);

        if (GUILayout.Button("OPTIONS", ButtonStyle(), GUILayout.Height(44f)))
        {
            panel = Panel.Options;
        }

        GUILayout.Space(8f);

        if (GUILayout.Button("SUICIDE", ButtonStyle(), GUILayout.Height(44f)))
        {
            if (health != null)
            {
                health.RequestSuicide();
            }

            SetOpen(false);
        }

        GUILayout.Space(14f);
        GUILayout.Label("The game keeps running while paused.", HintStyle());
    }

    private void DrawOptions()
    {
        // Tab bar.
        GUILayout.BeginHorizontal();
        DrawTabButton("GRAPHICS", OptionsTab.Graphics);
        DrawTabButton("AUDIO", OptionsTab.Audio);
        DrawTabButton("KEYBINDS", OptionsTab.Keybinds);
        DrawTabButton("GAMEPLAY", OptionsTab.Gameplay);
        GUILayout.EndHorizontal();

        GUILayout.Space(14f);

        switch (optionsTab)
        {
            case OptionsTab.Graphics: DrawGraphics(); break;
            case OptionsTab.Audio: DrawAudio(); break;
            case OptionsTab.Keybinds: DrawKeybinds(); break;
            case OptionsTab.Gameplay: DrawGameplay(); break;
        }

        GUILayout.FlexibleSpace();

        if (GUILayout.Button("BACK", ButtonStyle(), GUILayout.Height(40f)))
        {
            panel = Panel.Root;
            GameSettings.Save();
        }
    }

    private void DrawTabButton(string label, OptionsTab tab)
    {
        GUIStyle style = new GUIStyle(GUI.skin.button) { fontSize = 12, fontStyle = FontStyle.Bold };

        if (optionsTab == tab)
        {
            style.normal.textColor = new Color(0.5f, 0.8f, 1f);
            style.fontStyle = FontStyle.BoldAndItalic;
        }

        if (GUILayout.Button(label, style, GUILayout.Height(30f)))
        {
            optionsTab = tab;
            rebindTarget = null;
        }
    }

    private void DrawGraphics()
    {
        // Quality selector: < Name >
        GUILayout.BeginHorizontal();
        GUILayout.Label("Quality", RowLabelStyle(), GUILayout.Width(160f));

        string[] names = QualitySettings.names;
        int level = Mathf.Clamp(GameSettings.QualityLevel, 0, names.Length - 1);

        if (GUILayout.Button("<", GUILayout.Width(34f), GUILayout.Height(28f)))
        {
            GameSettings.QualityLevel = Mathf.Max(0, level - 1);
            GameSettings.Save();
        }

        GUILayout.Label(names[level], CenterValueStyle(), GUILayout.Width(160f), GUILayout.Height(28f));

        if (GUILayout.Button(">", GUILayout.Width(34f), GUILayout.Height(28f)))
        {
            GameSettings.QualityLevel = Mathf.Min(names.Length - 1, level + 1);
            GameSettings.Save();
        }

        GUILayout.EndHorizontal();
        GUILayout.Space(10f);

        bool fullscreen = ToggleRow("Fullscreen", GameSettings.Fullscreen);
        if (fullscreen != GameSettings.Fullscreen)
        {
            GameSettings.Fullscreen = fullscreen;
            GameSettings.Save();
        }

        bool vsync = ToggleRow("VSync", GameSettings.VSync);
        if (vsync != GameSettings.VSync)
        {
            GameSettings.VSync = vsync;
            GameSettings.Save();
        }
    }

    private void DrawAudio()
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label("Master Volume", RowLabelStyle(), GUILayout.Width(160f));

        float volume = GUILayout.HorizontalSlider(GameSettings.MasterVolume, 0f, 1f,
            GUILayout.Width(200f), GUILayout.Height(28f));

        GUILayout.Label(Mathf.RoundToInt(volume * 100f) + "%", CenterValueStyle(), GUILayout.Width(50f));
        GUILayout.EndHorizontal();

        if (!Mathf.Approximately(volume, GameSettings.MasterVolume))
        {
            GameSettings.MasterVolume = volume;
            GameSettings.ApplyAudio(); // live, persisted on close
        }

        GUILayout.Space(10f);

        bool muted = ToggleRow("Mute", GameSettings.Muted);
        if (muted != GameSettings.Muted)
        {
            GameSettings.Muted = muted;
            GameSettings.Save();
        }
    }

    private void DrawGameplay()
    {
        bool tap = ToggleRow("Tap to Build/Repair (instead of Hold)", GameSettings.TapToBuild);
        if (tap != GameSettings.TapToBuild)
        {
            GameSettings.TapToBuild = tap;
            GameSettings.Save();
        }

        bool toggleAds = ToggleRow("Toggle ADS", GameSettings.ToggleAds);
        if (toggleAds != GameSettings.ToggleAds)
        {
            GameSettings.ToggleAds = toggleAds;
            GameSettings.Save();
        }

        bool toggleSprint = ToggleRow("Toggle Sprint", GameSettings.ToggleSprint);
        if (toggleSprint != GameSettings.ToggleSprint)
        {
            GameSettings.ToggleSprint = toggleSprint;
            GameSettings.Save();
        }
    }

    private void DrawKeybinds()
    {
        keybindScroll = GUILayout.BeginScrollView(keybindScroll, GUILayout.Height(320f));

        foreach (GameAction action in Enum.GetValues(typeof(GameAction)))
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Prettify(action.ToString()), RowLabelStyle(), GUILayout.Width(150f));

            bool listening = rebindTarget == action;
            string keyLabel = listening ? "PRESS A KEY..." : GameSettings.DisplayName(action);

            GUIStyle bindStyle = new GUIStyle(GUI.skin.button) { fontSize = 12 };
            if (listening)
            {
                bindStyle.normal.textColor = new Color(1f, 0.8f, 0.3f);
            }

            if (GUILayout.Button(keyLabel, bindStyle, GUILayout.Width(150f), GUILayout.Height(26f)))
            {
                rebindTarget = listening ? (GameAction?)null : action;
            }

            GUILayout.EndHorizontal();
            GUILayout.Space(2f);
        }

        GUILayout.EndScrollView();
        GUILayout.Space(8f);

        if (GUILayout.Button("RESET TO DEFAULTS", ButtonStyle(), GUILayout.Height(32f)))
        {
            GameSettings.ResetKeybindsToDefault();
            GameSettings.Save();
            rebindTarget = null;
        }
    }

    // Left-aligned label + right-aligned ON/OFF toggle button in one row.
    private bool ToggleRow(string label, bool value)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, RowLabelStyle(), GUILayout.Width(320f));

        GUIStyle style = new GUIStyle(GUI.skin.button) { fontSize = 12, fontStyle = FontStyle.Bold };
        style.normal.textColor = value ? new Color(0.5f, 0.9f, 0.5f) : new Color(0.9f, 0.5f, 0.5f);

        bool result = value;

        if (GUILayout.Button(value ? "ON" : "OFF", style, GUILayout.Width(80f), GUILayout.Height(28f)))
        {
            result = !value;
        }

        GUILayout.EndHorizontal();
        GUILayout.Space(6f);

        return result;
    }

    // ---- Styles / helpers ----

    private static GUIStyle TitleStyle()
    {
        GUIStyle style = new GUIStyle(GUI.skin.label)
        {
            fontSize = 24,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };
        style.normal.textColor = Color.white;
        return style;
    }

    private static GUIStyle ButtonStyle()
    {
        return new GUIStyle(GUI.skin.button) { fontSize = 15, fontStyle = FontStyle.Bold };
    }

    private static GUIStyle RowLabelStyle()
    {
        GUIStyle style = new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.MiddleLeft };
        style.normal.textColor = Color.white;
        return style;
    }

    private static GUIStyle CenterValueStyle()
    {
        GUIStyle style = new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.MiddleCenter };
        style.normal.textColor = new Color(0.8f, 0.9f, 1f);
        return style;
    }

    private static GUIStyle HintStyle()
    {
        GUIStyle style = new GUIStyle(GUI.skin.label) { fontSize = 12, alignment = TextAnchor.MiddleCenter };
        style.normal.textColor = new Color(1f, 1f, 1f, 0.6f);
        return style;
    }

    // "MoveForward" -> "Move Forward"
    private static string Prettify(string name)
    {
        System.Text.StringBuilder builder = new System.Text.StringBuilder();

        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
            {
                builder.Append(' ');
            }

            builder.Append(name[i]);
        }

        return builder.ToString();
    }
}
