using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Rebindable player actions. Utility keys (Escape, Tab, ghost rotation, the
// aim-tuning keys) stay hardcoded and are intentionally not listed here.
public enum GameAction
{
    MoveForward,
    MoveBackward,
    MoveLeft,
    MoveRight,
    Jump,
    Crouch,
    Sprint,
    Reload,
    Interact,
    Deconstruct,
    Slot1,
    Slot2,
    Slot3,
    Slot4
}

// Central, persistent client settings (Graphics / Audio / Keybinds /
// Gameplay). Static so every input reader and the pause menu share one
// source of truth; backed by PlayerPrefs so choices survive restarts.
//
// Input code reads keys through Held()/Pressed() instead of touching
// Keyboard.current directly, which is what makes rebinding work.
public static class GameSettings
{
    // ---- Graphics ----
    public static int QualityLevel;
    public static bool Fullscreen;
    public static bool VSync;

    // ---- Audio ----
    public static float MasterVolume;
    public static bool Muted;

    // ---- Gameplay ----
    public static bool TapToBuild;   // tap Interact to latch build/repair, vs. hold
    public static bool ToggleAds;    // right-click toggles ADS, vs. hold
    public static bool ToggleSprint; // tap Sprint to latch, vs. hold

    // ---- Keybinds ----
    private static readonly Dictionary<GameAction, Key> binds = new Dictionary<GameAction, Key>();

    public static event Action OnChanged;

    private static bool loaded;

    private static readonly Dictionary<GameAction, Key> Defaults = new Dictionary<GameAction, Key>
    {
        { GameAction.MoveForward, Key.W },
        { GameAction.MoveBackward, Key.S },
        { GameAction.MoveLeft, Key.A },
        { GameAction.MoveRight, Key.D },
        { GameAction.Jump, Key.Space },
        { GameAction.Crouch, Key.LeftCtrl },
        { GameAction.Sprint, Key.LeftShift },
        { GameAction.Reload, Key.R },
        { GameAction.Interact, Key.F },
        { GameAction.Deconstruct, Key.G },
        { GameAction.Slot1, Key.Digit1 },
        { GameAction.Slot2, Key.Digit2 },
        { GameAction.Slot3, Key.Digit3 },
        { GameAction.Slot4, Key.Digit4 }
    };

    // Apply saved settings at startup, before any scene input runs.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        Load();
        ApplyGraphics();
        ApplyAudio();
    }

    public static void Load()
    {
        if (loaded)
        {
            return;
        }

        loaded = true;

        QualityLevel = PlayerPrefs.GetInt("gfx_quality", QualitySettings.GetQualityLevel());
        Fullscreen = PlayerPrefs.GetInt("gfx_fullscreen", Screen.fullScreen ? 1 : 0) == 1;
        VSync = PlayerPrefs.GetInt("gfx_vsync", QualitySettings.vSyncCount > 0 ? 1 : 0) == 1;

        MasterVolume = PlayerPrefs.GetFloat("aud_master", 1f);
        Muted = PlayerPrefs.GetInt("aud_muted", 0) == 1;

        TapToBuild = PlayerPrefs.GetInt("gp_tapbuild", 0) == 1;
        ToggleAds = PlayerPrefs.GetInt("gp_toggleads", 0) == 1;
        ToggleSprint = PlayerPrefs.GetInt("gp_togglesprint", 0) == 1;

        binds.Clear();

        foreach (KeyValuePair<GameAction, Key> pair in Defaults)
        {
            int stored = PlayerPrefs.GetInt("kb_" + pair.Key, (int)pair.Value);
            binds[pair.Key] = (Key)stored;
        }
    }

    public static void Save()
    {
        PlayerPrefs.SetInt("gfx_quality", QualityLevel);
        PlayerPrefs.SetInt("gfx_fullscreen", Fullscreen ? 1 : 0);
        PlayerPrefs.SetInt("gfx_vsync", VSync ? 1 : 0);

        PlayerPrefs.SetFloat("aud_master", MasterVolume);
        PlayerPrefs.SetInt("aud_muted", Muted ? 1 : 0);

        PlayerPrefs.SetInt("gp_tapbuild", TapToBuild ? 1 : 0);
        PlayerPrefs.SetInt("gp_toggleads", ToggleAds ? 1 : 0);
        PlayerPrefs.SetInt("gp_togglesprint", ToggleSprint ? 1 : 0);

        foreach (KeyValuePair<GameAction, Key> pair in binds)
        {
            PlayerPrefs.SetInt("kb_" + pair.Key, (int)pair.Value);
        }

        PlayerPrefs.Save();

        ApplyGraphics();
        ApplyAudio();

        OnChanged?.Invoke();
    }

    public static void ApplyGraphics()
    {
        int levels = QualitySettings.names.Length;
        QualitySettings.SetQualityLevel(Mathf.Clamp(QualityLevel, 0, levels - 1), true);
        QualitySettings.vSyncCount = VSync ? 1 : 0;

        FullScreenMode mode = Fullscreen ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed;
        Screen.fullScreenMode = mode;
    }

    public static void ApplyAudio()
    {
        AudioListener.volume = Muted ? 0f : Mathf.Clamp01(MasterVolume);
    }

    // ---- Keybinds ----

    public static Key GetKey(GameAction action)
    {
        Load();
        return binds.TryGetValue(action, out Key key) ? key : Key.None;
    }

    public static void SetKey(GameAction action, Key key)
    {
        Load();
        binds[action] = key;
    }

    public static void ResetKeybindsToDefault()
    {
        Load();

        foreach (KeyValuePair<GameAction, Key> pair in Defaults)
        {
            binds[pair.Key] = pair.Value;
        }
    }

    public static string DisplayName(GameAction action)
    {
        Key key = GetKey(action);
        return key == Key.None ? "—" : key.ToString();
    }

    // ---- Input reads (rebind-aware) ----

    public static bool Held(GameAction action)
    {
        if (Keyboard.current == null)
        {
            return false;
        }

        Key key = GetKey(action);
        return key != Key.None && Keyboard.current[key].isPressed;
    }

    public static bool Pressed(GameAction action)
    {
        if (Keyboard.current == null)
        {
            return false;
        }

        Key key = GetKey(action);
        return key != Key.None && Keyboard.current[key].wasPressedThisFrame;
    }
}
