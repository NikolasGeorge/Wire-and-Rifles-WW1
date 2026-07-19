using UnityEngine;
using UnityEngine.InputSystem;

// Owner-side build input for the Engineer. Added at runtime by
// PlayerNetworkSetup on the owning client when the class can build.
// Keys 4-7 place a fortification at the aimed ground point.
public class FortificationBuilder : MonoBehaviour
{
    public Camera playerCamera;
    public float maxPlaceDistance = 4.5f;

    private PlayerNetworkHealth health;
    private float lastHintBlockedTime = -999f;

    private void Start()
    {
        health = GetComponent<PlayerNetworkHealth>();

        if (playerCamera == null)
        {
            playerCamera = GetComponentInChildren<Camera>();
        }
    }

    private void Update()
    {
        if (Keyboard.current == null || FortificationManager.Instance == null)
        {
            return;
        }

        if (health != null && health.State != PlayerLifeState.Alive)
        {
            return;
        }

        if (Keyboard.current.digit4Key.wasPressedThisFrame)
        {
            TryPlace(FortificationType.Sandbags);
        }
        else if (Keyboard.current.digit5Key.wasPressedThisFrame)
        {
            TryPlace(FortificationType.BarbedWire);
        }
        else if (Keyboard.current.digit6Key.wasPressedThisFrame)
        {
            TryPlace(FortificationType.AmmoCrate);
        }
        else if (Keyboard.current.digit7Key.wasPressedThisFrame)
        {
            TryPlace(FortificationType.MedCrate);
        }
    }

    private void TryPlace(FortificationType type)
    {
        Vector3 placePoint;

        if (playerCamera != null
            && Physics.Raycast(playerCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f)),
                out RaycastHit hit, maxPlaceDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            placePoint = hit.point;
        }
        else
        {
            // Nothing aimed at nearby: place on the ground just ahead.
            Vector3 forward = transform.forward;
            forward.y = 0f;
            placePoint = transform.position + forward.normalized * 3f;
        }

        if (Vector3.Distance(transform.position, placePoint) > maxPlaceDistance)
        {
            lastHintBlockedTime = Time.time;
            return;
        }

        FortificationManager.Instance.RequestBuild(type, placePoint, transform.eulerAngles.y);
    }

    private void OnGUI()
    {
        if (health != null && health.State != PlayerLifeState.Alive)
        {
            return;
        }

        GUIStyle style = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            alignment = TextAnchor.LowerRight
        };
        style.normal.textColor = new Color(1f, 1f, 1f, 0.75f);

        string text = "BUILD:  [4] Sandbags   [5] Barbed Wire   [6] Ammo Crate   [7] Med Crate";

        if (Time.time - lastHintBlockedTime < 1.5f)
        {
            text = "TOO FAR — AIM AT GROUND NEARBY\n" + text;
        }

        GUI.Label(new Rect(0f, Screen.height - 58f, Screen.width - 16f, 44f), text, style);
    }
}
