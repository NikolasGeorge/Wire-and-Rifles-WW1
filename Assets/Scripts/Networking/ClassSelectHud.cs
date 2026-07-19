using FishNet;
using UnityEngine;

// Class selection screen. Shows whenever this client is connected but has no
// live player object: on first join and again after each death.
public class ClassSelectHud : MonoBehaviour
{
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
    }

    private void OnGUI()
    {
        if (!ShouldShow())
        {
            return;
        }

        const float buttonWidth = 340f;
        const float buttonHeight = 68f;
        const float spacing = 10f;

        PlayerClassDefinition[] classes = PlayerClasses.Definitions;

        float totalHeight = classes.Length * (buttonHeight + spacing) - spacing;
        float x = (Screen.width - buttonWidth) * 0.5f;
        float y = (Screen.height - totalHeight) * 0.5f;

        GUIStyle titleStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 28,
            fontStyle = FontStyle.Bold
        };

        GUI.Label(new Rect(0f, y - 70f, Screen.width, 50f), "SELECT CLASS", titleStyle);

        GUIStyle buttonStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 16,
            alignment = TextAnchor.MiddleCenter,
            richText = true
        };

        for (int i = 0; i < classes.Length; i++)
        {
            PlayerClassDefinition definition = classes[i];

            string label = definition.displayName + "\n<size=12>" + definition.description
                + "\n" + definition.weapon + " (" + definition.reserveAmmo + ")  |  " + definition.grenade
                + "\n" + definition.equipmentSlot1 + ", " + definition.equipmentSlot2
                + (definition.customizableLoadout ? "  (customizable)" : "") + "</size>";

            Rect rect = new Rect(x, y + i * (buttonHeight + spacing), buttonWidth, buttonHeight);

            if (GUI.Button(rect, label, buttonStyle))
            {
                ClassSpawnManager.Instance.RequestSpawn((PlayerClass)i);
            }
        }
    }
}
