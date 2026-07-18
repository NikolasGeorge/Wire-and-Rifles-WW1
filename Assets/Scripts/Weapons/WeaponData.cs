using UnityEngine;

[CreateAssetMenu(menuName = "Weapons/Weapon Data")]
public class WeaponData : ScriptableObject
{
    public string weaponName = "Bolt-Action Rifle";

    [Header("Damage")]
    public float damage = 100f;
    public float range = 150f;

    [Header("Projectile")]
    public bool useBulletTravelTime = true;
    public float muzzleVelocity = 700f;
    public float minimumBulletTravelTime = 0.02f;

    [Header("Accuracy")]
    [Range(0f, 25f)]
    public float baseInaccuracyAngle = 0.25f;

    [Range(0f, 25f)]
    public float movingInaccuracyPenalty = 0.75f;

    [Range(0f, 45f)]
    public float airborneInaccuracyPenalty = 3f;

    [Range(0.05f, 1f)]
    public float aimingInaccuracyMultiplier = 0.35f;

    [Header("Aiming Down Sights")]
    public float aimAccuracyBuildTime = 0.2f;

    [Range(0.1f, 1f)]
    public float aimMoveSpeedMultiplier = 0.65f;

    [Header("Rapid Fire Accuracy")]
    public bool useRapidFireAccuracyPenalty = true;
    public float rapidFireResetTime = 1f;

    [Range(0f, 1f)]
    public float rapidFireSecondShotPenalty = 0.5f;

    [Range(0f, 1f)]
    public float rapidFirePenaltyStep = 0.1f;

    [Range(0f, 1f)]
    public float rapidFireMinimumPenalty = 0.1f;

    [Header("Sprint Handling")]
    [Range(0f, 2f)]
    public float sprintFireLockoutTime = 0.5f;

    [Header("Ammo")]
    public int clipSize = 5;
    public int startingReserveAmmo = 30;

    [Header("Timing")]
    public float boltCycleTime = 1.2f;
    public float reloadTime = 2.5f;

    [Header("Audio")]
    public AudioClip fireSound;
    public float fireSoundVolume = 1f;

    public AudioClip reloadSound;
    public float reloadSoundVolume = 1f;

    public AudioClip boltCycleSound;
    public float boltCycleSoundVolume = 1f;
    public float boltCycleSoundDelay = 0.25f;
}