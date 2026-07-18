using System.Linq;
using UnityEditor;
using UnityEditor.Toolbars;
using UnityEngine;

namespace FEPM.Editor
{
    public static class FastEnterPlayModeButton
    {
        private const string ButtonPath = "Fast Enter Play Mode/Fast Play Button";
        private static bool _buttonClicked;

        [MainToolbarElement(ButtonPath, defaultDockPosition = MainToolbarDockPosition.Middle)]
        public static MainToolbarElement CreateButton()
        {
            string filename = EditorApplication.isPlaying && _buttonClicked ? "FEPM_IsPlaying" : "FEPM";
            if(EditorGUIUtility.isProSkin) filename = $"d_{filename}";
            
            Texture2D buttonIcon = AssetDatabase.LoadAssetAtPath<Texture2D>(
                $"Packages/io.continis.fast-enter-play-mode/UI/{filename}.png");

            MainToolbarContent buttonContent = new(buttonIcon, "Fast Play");

            EditorApplication.playModeStateChanged -= OnExitingPlayMode;
            EditorApplication.playModeStateChanged += OnExitingPlayMode;

            return new MainToolbarToggle(buttonContent, true, OnButtonClicked) { enabled = !EditorApplication.isPlaying };
        }

        private static void OnExitingPlayMode(PlayModeStateChange change)
        {
            switch (change)
            {
                case PlayModeStateChange.EnteredPlayMode:
                {
                    if(!_buttonClicked) MainToolbar.Refresh(ButtonPath);
                    break;
                }

                case PlayModeStateChange.EnteredEditMode:
                {
                    if (_buttonClicked) EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.None;
                    _buttonClicked = false;
                    MainToolbar.Refresh(ButtonPath);
                    break;
                }
            }
        }

        [MenuItem("Edit/Play Mode/Fast Play #%&P")]
        private static void EnterPlayModeFast()
        {
            OnButtonClicked(true);
        }

        private static void OnButtonClicked(bool b)
        {
            _buttonClicked = true;

            EditorSettings.enterPlayModeOptions =
                EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload;

            EditorApplication.EnterPlaymode();
            EditorApplication.delayCall = () => MainToolbar.Refresh(ButtonPath);
        }
    }
}