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
            default: return null;
        }
    }
}
