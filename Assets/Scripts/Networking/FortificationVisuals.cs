using UnityEngine;

// Placeholder prop prefabs (from the Stylized WW1 pack) used as fortification
// visuals. Loaded from Resources so no scene or Inspector wiring is needed.
public class FortificationVisuals : ScriptableObject
{
    public GameObject sandbags;
    public GameObject lowWire;
    public GameObject highWire;
    public GameObject trenchWall;
    public GameObject ammoCrate;
    public GameObject medCrate;
    public GameObject toolbox;

    [Header("Trench Furniture")]
    [Tooltip("Prop_Trench_Planks_02")]
    public GameObject duckBoards;
    [Tooltip("Prop_Iron_Sheet_04")]
    public GameObject corrugatedRoof;
    [Tooltip("Prop_Ladder_01")]
    public GameObject ladder;
    [Tooltip("Prop_Trench_Bridge_01")]
    public GameObject makeshiftFloor;

    private static FortificationVisuals cached;

    public static FortificationVisuals Load()
    {
        if (cached == null)
        {
            cached = Resources.Load<FortificationVisuals>("FortificationVisuals");
        }

        return cached;
    }

    public GameObject GetPrefab(FortificationType type)
    {
        switch (type)
        {
            case FortificationType.Sandbags: return sandbags;
            case FortificationType.LowWire: return lowWire;
            case FortificationType.HighWire: return highWire;
            case FortificationType.TrenchWall: return trenchWall;
            case FortificationType.AmmoCrate: return ammoCrate;
            case FortificationType.MedCrate: return medCrate;
            // No dedicated prop yet — falls back to the generic box visual.
            case FortificationType.Toolbox: return toolbox != null ? toolbox : ammoCrate;
            case FortificationType.DuckBoards: return duckBoards;
            case FortificationType.CorrugatedRoof: return corrugatedRoof;
            case FortificationType.Ladder: return ladder;
            case FortificationType.MakeshiftFloor: return makeshiftFloor;
            default: return null;
        }
    }
}
