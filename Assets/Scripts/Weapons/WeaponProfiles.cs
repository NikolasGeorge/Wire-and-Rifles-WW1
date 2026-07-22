using UnityEngine;

public enum WeaponId : byte
{
    BoltAction = 0,
    ScopedBoltAction = 1,
    SemiAutoRifle = 2,
    Shotgun = 3,
    Lmg = 4,
    Pistol = 5
}

public enum WeaponFireMode : byte
{
    BoltAction = 0,
    SemiAuto = 1,
    Automatic = 2
}

// Per-class primary stat blocks. Applied on top of the shared WeaponData at
// spawn (BoltActionRifle.ApplyWeaponProfile), so one rifle script drives every
// weapon. fireInterval reuses WeaponData.boltCycleTime, which the server
// already validates fire rate against. reserveAmmo is weapon-defined and
// overrides the class default.
public struct WeaponProfile
{
    public string displayName;
    public WeaponFireMode fireMode;

    // Body damage by range band. Headshots always use damageClose (headshots
    // ignore falloff) before the hitbox multiplier is applied.
    public float damageClose;
    public float damageMid;
    public float damageLong;

    // Band edges in meters: close is 0..closeRangeEnd, mid runs to
    // midRangeEnd, long is everything past that.
    public float closeRangeEnd;
    public float midRangeEnd;

    public float range;
    public float muzzleVelocity;
    public int clipSize;
    public int reserveAmmo;
    public float fireInterval;
    public float reloadTime;

    // Tube-fed reload: reloadTime is per shell, loaded one at a time.
    public bool shellByShellReload;

    public float baseInaccuracyAngle;
    public float movingInaccuracyPenalty;
    public float airborneInaccuracyPenalty;
    public float aimingInaccuracyMultiplier;
    public float aimAccuracyBuildTime;
    public float aimMoveSpeedMultiplier;

    public float hipFieldOfView;
    public float aimFieldOfView;

    public int pelletsPerShot;
    public float pelletSpreadAngle;

    public bool requiresDeploySetup;
    public float deploySetupTime;
    public bool useRapidFirePenalty;

    // Scales how hard this weapon's near-misses suppress, on top of the
    // damage weighting. Left unset (0) it is treated as 1.
    public float suppressionMultiplier;

    public float hipRecoilMultiplier;
    public float aimRecoilMultiplier;
    public float cameraPitchKick;
    public float cameraYawRandom;
}

public static class WeaponProfiles
{
    public static WeaponProfile Get(WeaponId id)
    {
        switch (id)
        {
            case WeaponId.ScopedBoltAction:
                return new WeaponProfile
                {
                    displayName = "Scoped Bolt-Action (6x)",
                    fireMode = WeaponFireMode.BoltAction,
                    damageClose = 100f,
                    damageMid = 100f,
                    damageLong = 80f,
                    closeRangeEnd = 100f,
                    midRangeEnd = 200f,
                    range = 300f,
                    muzzleVelocity = 750f,
                    clipSize = 5,
                    reserveAmmo = 30,
                    fireInterval = 1.5f,
                    reloadTime = 3f,
                    baseInaccuracyAngle = 0.15f,
                    movingInaccuracyPenalty = 1.5f,
                    airborneInaccuracyPenalty = 5f,
                    aimingInaccuracyMultiplier = 0.2f,
                    aimAccuracyBuildTime = 0.35f,
                    aimMoveSpeedMultiplier = 0.5f,
                    hipFieldOfView = 80f,
                    // ~6x magnification.
                    aimFieldOfView = 13f,
                    pelletsPerShot = 1,
                    useRapidFirePenalty = true,
                    hipRecoilMultiplier = 1f,
                    aimRecoilMultiplier = 0.75f,
                    cameraPitchKick = 3.6f,
                    cameraYawRandom = 0.35f
                };

            case WeaponId.SemiAutoRifle:
                return new WeaponProfile
                {
                    displayName = "Semi-Auto Rifle",
                    fireMode = WeaponFireMode.SemiAuto,
                    // 2-shot down close (140) and mid (100), 3-shot long.
                    damageClose = 70f,
                    damageMid = 50f,
                    damageLong = 40f,
                    closeRangeEnd = 50f,
                    midRangeEnd = 100f,
                    range = 120f,
                    muzzleVelocity = 650f,
                    clipSize = 10,
                    reserveAmmo = 30,
                    fireInterval = 0.35f,
                    reloadTime = 2.7f,
                    baseInaccuracyAngle = 0.45f,
                    movingInaccuracyPenalty = 1f,
                    airborneInaccuracyPenalty = 3.5f,
                    aimingInaccuracyMultiplier = 0.4f,
                    aimAccuracyBuildTime = 0.2f,
                    aimMoveSpeedMultiplier = 0.6f,
                    hipFieldOfView = 80f,
                    aimFieldOfView = 50f,
                    pelletsPerShot = 1,
                    useRapidFirePenalty = true,
                    hipRecoilMultiplier = 1f,
                    aimRecoilMultiplier = 0.8f,
                    cameraPitchKick = 2f,
                    cameraYawRandom = 0.3f
                };

            case WeaponId.Shotgun:
                return new WeaponProfile
                {
                    displayName = "Trench Shotgun",
                    fireMode = WeaponFireMode.BoltAction,
                    // Per pellet: 15 pellets, all landing close = 150 = full
                    // kill; 10 pellets close = 100 = down.
                    damageClose = 10f,
                    damageMid = 8f,
                    damageLong = 6f,
                    closeRangeEnd = 20f,
                    midRangeEnd = 40f,
                    range = 100f,
                    muzzleVelocity = 400f,
                    clipSize = 5,
                    reserveAmmo = 25,
                    // Pump time.
                    fireInterval = 0.8f,
                    // Per shell.
                    reloadTime = 0.55f,
                    shellByShellReload = true,
                    baseInaccuracyAngle = 3.5f,
                    movingInaccuracyPenalty = 1f,
                    airborneInaccuracyPenalty = 5f,
                    aimingInaccuracyMultiplier = 0.75f,
                    aimAccuracyBuildTime = 0.15f,
                    aimMoveSpeedMultiplier = 0.8f,
                    hipFieldOfView = 80f,
                    aimFieldOfView = 60f,
                    pelletsPerShot = 15,
                    pelletSpreadAngle = 5f,
                    useRapidFirePenalty = false,
                    hipRecoilMultiplier = 1f,
                    aimRecoilMultiplier = 0.9f,
                    cameraPitchKick = 4.5f,
                    cameraYawRandom = 0.6f
                };

            case WeaponId.Lmg:
                return new WeaponProfile
                {
                    displayName = "LMG",
                    fireMode = WeaponFireMode.Automatic,
                    // Low per-shot damage: the LMG's value is volume of fire
                    // and the suppression it creates, not raw lethality.
                    damageClose = 35f,
                    damageMid = 28f,
                    damageLong = 21f,
                    closeRangeEnd = 50f,
                    midRangeEnd = 100f,
                    range = 120f,
                    muzzleVelocity = 650f,
                    clipSize = 50,
                    reserveAmmo = 100,
                    fireInterval = 0.12f,
                    reloadTime = 5f,
                    // Fires undeployed with heavy recoil and wide spread;
                    // deploying (hold aim while still) shrinks both.
                    baseInaccuracyAngle = 2.2f,
                    movingInaccuracyPenalty = 4f,
                    airborneInaccuracyPenalty = 8f,
                    aimingInaccuracyMultiplier = 0.5f,
                    aimAccuracyBuildTime = 0.35f,
                    aimMoveSpeedMultiplier = 0.55f,
                    hipFieldOfView = 80f,
                    aimFieldOfView = 55f,
                    pelletsPerShot = 1,
                    requiresDeploySetup = true,
                    deploySetupTime = 1.5f,
                    useRapidFirePenalty = false,
                    hipRecoilMultiplier = 1.6f,
                    aimRecoilMultiplier = 1f,
                    cameraPitchKick = 2.4f,
                    cameraYawRandom = 0.4f
                };

            case WeaponId.Pistol:
                return new WeaponProfile
                {
                    displayName = "Service Pistol",
                    fireMode = WeaponFireMode.SemiAuto,
                    // 3-shot down close, 4-shot mid, 5-shot long.
                    damageClose = 40f,
                    damageMid = 32f,
                    damageLong = 24f,
                    closeRangeEnd = 25f,
                    midRangeEnd = 50f,
                    range = 60f,
                    muzzleVelocity = 300f,
                    clipSize = 7,
                    reserveAmmo = 28,
                    fireInterval = 0.25f,
                    reloadTime = 2f,
                    baseInaccuracyAngle = 0.8f,
                    movingInaccuracyPenalty = 0.6f,
                    airborneInaccuracyPenalty = 2.5f,
                    aimingInaccuracyMultiplier = 0.5f,
                    aimAccuracyBuildTime = 0.15f,
                    aimMoveSpeedMultiplier = 0.85f,
                    hipFieldOfView = 80f,
                    aimFieldOfView = 65f,
                    pelletsPerShot = 1,
                    useRapidFirePenalty = true,
                    hipRecoilMultiplier = 1f,
                    aimRecoilMultiplier = 0.8f,
                    cameraPitchKick = 1.8f,
                    cameraYawRandom = 0.4f
                };

            default:
                return new WeaponProfile
                {
                    displayName = "Bolt-Action Rifle",
                    fireMode = WeaponFireMode.BoltAction,
                    // 1-shot down through 100m; 80 past that (no down on a
                    // fresh target). Headshots ignore falloff.
                    damageClose = 100f,
                    damageMid = 100f,
                    damageLong = 80f,
                    closeRangeEnd = 50f,
                    midRangeEnd = 100f,
                    range = 150f,
                    muzzleVelocity = 700f,
                    clipSize = 5,
                    reserveAmmo = 30,
                    fireInterval = 1.2f,
                    reloadTime = 2.5f,
                    baseInaccuracyAngle = 0.25f,
                    movingInaccuracyPenalty = 0.75f,
                    airborneInaccuracyPenalty = 3f,
                    aimingInaccuracyMultiplier = 0.35f,
                    aimAccuracyBuildTime = 0.2f,
                    aimMoveSpeedMultiplier = 0.65f,
                    hipFieldOfView = 80f,
                    aimFieldOfView = 30f,
                    pelletsPerShot = 1,
                    useRapidFirePenalty = true,
                    hipRecoilMultiplier = 1f,
                    aimRecoilMultiplier = 0.75f,
                    cameraPitchKick = 3.3f,
                    cameraYawRandom = 0.35f
                };
        }
    }
}
