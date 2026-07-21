using UnityEngine;

// Per-weapon view models (Low Poly Weapon Pack 4 WWII). Weapons without an
// entry keep the default rifle model on the Player prefab.
public class WeaponVisuals : ScriptableObject
{
    public GameObject lmg;
    public GameObject pistol;

    [Header("ADS pose per model (weaponHolder local position/rotation while aiming)")]
    [Tooltip("Tune these in the Inspector until the iron sights line up. The default rifle pose is (0, -0.08, 0.25).")]
    public Vector3 lmgAimPosition = new Vector3(0f, -0.18f, 0.1f);
    public Vector3 lmgAimEuler = Vector3.zero;
    public float lmgModelScale = 0.6f;
    public Vector3 pistolAimPosition = new Vector3(0f, -0.1f, 0.3f);
    public Vector3 pistolAimEuler = Vector3.zero;
    public float pistolModelScale = 1f;

    public float GetModelScale(WeaponId weapon)
    {
        switch (weapon)
        {
            case WeaponId.Lmg: return lmgModelScale;
            case WeaponId.Pistol: return pistolModelScale;
            default: return 1f;
        }
    }

    public bool TryGetAimPose(WeaponId weapon, out Vector3 position, out Vector3 euler)
    {
        switch (weapon)
        {
            case WeaponId.Lmg:
                position = lmgAimPosition;
                euler = lmgAimEuler;
                return true;

            case WeaponId.Pistol:
                position = pistolAimPosition;
                euler = pistolAimEuler;
                return true;

            default:
                position = Vector3.zero;
                euler = Vector3.zero;
                return false;
        }
    }

    private static WeaponVisuals cached;

    public static WeaponVisuals Load()
    {
        if (cached == null)
        {
            cached = Resources.Load<WeaponVisuals>("WeaponVisuals");
        }

        return cached;
    }

    public GameObject GetModel(WeaponId weapon)
    {
        switch (weapon)
        {
            case WeaponId.Lmg: return lmg;
            case WeaponId.Pistol: return pistol;
            default: return null;
        }
    }
}
