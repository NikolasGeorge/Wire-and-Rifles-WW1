using UnityEngine;

// Real grenade models (Stylized WW1 pack) and particle effects (War FX)
// used by the grenade system. Loaded from Resources so no scene or
// Inspector wiring is needed; every consumer falls back to primitives if a
// prefab is missing.
public class GrenadeVisuals : ScriptableObject
{
    public GameObject fragGrenade;
    public GameObject stickGrenade;
    public GameObject explosionFx;
    public GameObject smokeFx;

    private static GrenadeVisuals cached;

    public static GrenadeVisuals Load()
    {
        if (cached == null)
        {
            cached = Resources.Load<GrenadeVisuals>("GrenadeVisuals");
        }

        return cached;
    }

    public GameObject GetGrenadeModel(GrenadeType type)
    {
        return type == GrenadeType.Stick ? stickGrenade : fragGrenade;
    }
}
