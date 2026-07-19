using System;
using System.Collections.Generic;
using UnityEngine;

[ExecuteAlways]
public class ObjectiveHUDLayout : MonoBehaviour
{
    public PlayerTeam localPlayerTeam;
    public ObjectiveUI[] objectiveMarkers;

    [Header("Layout")]
    public float markerSpacing = 90f;
    public float yPosition = 0f;
    public bool flipOrderForCentralPowers = true;

    [Tooltip("Anchor the marker row to the top-center of the screen, this far down from the top edge.")]
    public bool anchorToTop = true;
    public float topOffset = 70f;

    private void Awake()
    {
        if (localPlayerTeam == null)
        {
            localPlayerTeam = FindAnyObjectByType<PlayerTeam>();
        }

        if (Application.isPlaying)
        {
            EnsureMarkersForAllZones();
        }

        LayoutMarkers();
    }

    // The scene only hand-maintains one marker (Objective A). At runtime,
    // clone it for every other capture zone and rebuild the array sorted by
    // letter, so adding a zone to the map needs no UI work.
    private void EnsureMarkersForAllZones()
    {
        ObjectiveUI template = null;

        if (objectiveMarkers != null)
        {
            foreach (ObjectiveUI marker in objectiveMarkers)
            {
                if (marker != null)
                {
                    template = marker;
                    break;
                }
            }
        }

        if (template == null)
        {
            template = GetComponentInChildren<ObjectiveUI>(true);
        }

        if (template == null)
        {
            // Fall back to any complete HUD marker in the scene. Require the
            // letter and percent texts so a partial marker (e.g. a world-space
            // objective indicator) is never used as the clone template.
            foreach (ObjectiveUI candidate in FindObjectsByType<ObjectiveUI>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (candidate.objectiveLetterText != null && candidate.progressText != null)
                {
                    template = candidate;
                    break;
                }
            }
        }

        if (template == null)
        {
            return;
        }

        // Include inactive: Fish-Net keeps scene zones deactivated on clients
        // until the server spawn message arrives.
        List<ObjectiveCaptureZone> zones = new List<ObjectiveCaptureZone>(
            FindObjectsByType<ObjectiveCaptureZone>(FindObjectsInactive.Include, FindObjectsSortMode.None));

        zones.Sort((a, b) => string.Compare(a.objectiveLetter, b.objectiveLetter, StringComparison.Ordinal));

        List<ObjectiveUI> markers = new List<ObjectiveUI>();

        foreach (ObjectiveCaptureZone zone in zones)
        {
            ObjectiveUI marker = FindExistingMarker(zone);

            // The template itself may already be bound to this zone (the
            // hand-built A marker) without being listed in the array yet.
            if (marker == null && template.objective == zone)
            {
                marker = template;
            }

            if (marker == null)
            {
                marker = Instantiate(template, template.transform.parent);
                marker.name = "ObjectiveMarker_" + zone.objectiveLetter;
                marker.objective = zone;
            }

            markers.Add(marker);
        }

        if (markers.Count > 0)
        {
            objectiveMarkers = markers.ToArray();
        }
    }

    private ObjectiveUI FindExistingMarker(ObjectiveCaptureZone zone)
    {
        if (objectiveMarkers == null)
        {
            return null;
        }

        foreach (ObjectiveUI marker in objectiveMarkers)
        {
            if (marker != null && marker.objective == zone)
            {
                return marker;
            }
        }

        return null;
    }

    private float ensureMarkersTimer;

    private void Update()
    {
        // Re-scan periodically: zones can appear after Awake (Fish-Net
        // activates scene NetworkObjects once their spawn message arrives).
        if (Application.isPlaying)
        {
            ensureMarkersTimer += Time.unscaledDeltaTime;

            if (ensureMarkersTimer >= 1f)
            {
                ensureMarkersTimer = 0f;
                EnsureMarkersForAllZones();
            }
        }

        LayoutMarkers();
    }

    private void OnValidate()
    {
        LayoutMarkers();
    }

    private void LayoutMarkers()
    {
        if (objectiveMarkers == null || objectiveMarkers.Length == 0)
        {
            return;
        }

        int count = objectiveMarkers.Length;
        bool flipOrder = ShouldFlipOrder();

        for (int i = 0; i < count; i++)
        {
            if (objectiveMarkers[i] == null)
            {
                continue;
            }

            RectTransform markerRect = objectiveMarkers[i].GetComponent<RectTransform>();

            if (markerRect == null)
            {
                continue;
            }

            int visualIndex = flipOrder ? count - 1 - i : i;
            float centeredIndex = visualIndex - ((count - 1) * 0.5f);

            if (anchorToTop && Application.isPlaying)
            {
                // Place in absolute screen space so the row sits at the top
                // of the SCREEN no matter what panel the markers live under.
                Canvas canvas = markerRect.GetComponentInParent<Canvas>();
                float scale = canvas != null ? canvas.scaleFactor : 1f;

                markerRect.position = new Vector3(
                    Screen.width * 0.5f + centeredIndex * markerSpacing * scale,
                    Screen.height - (topOffset + yPosition) * scale,
                    0f);
            }
            else
            {
                markerRect.anchoredPosition = new Vector2(centeredIndex * markerSpacing, yPosition);
            }
        }
    }

    private bool ShouldFlipOrder()
    {
        if (!flipOrderForCentralPowers || localPlayerTeam == null)
        {
            return false;
        }

        return localPlayerTeam.team == Team.CentralPowers;
    }
}