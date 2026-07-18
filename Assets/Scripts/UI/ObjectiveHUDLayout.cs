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

    private void Awake()
    {
        if (localPlayerTeam == null)
        {
            localPlayerTeam = FindAnyObjectByType<PlayerTeam>();
        }

        LayoutMarkers();
    }

    private void Update()
    {
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

            markerRect.anchoredPosition = new Vector2(centeredIndex * markerSpacing, yPosition);
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