using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

public class BoltActionRifle : MonoBehaviour
{
    public Camera playerCamera;
    public PlayerController playerController;
    public WeaponData weaponData;
    public WeaponRecoil weaponRecoil;
    public Transform muzzlePoint;
    public GameObject muzzleFlashPrefab;
    public GameObject bulletImpactPrefab;
    public float bulletImpactLifetime = 2f;
    public AudioSource audioSource;
    public HitMarkerUI hitMarkerUI;

    [Header("Friendly Fire")]
    public PlayerTeam shooterTeam;
    public bool allowFriendlyFire;
    public bool showFriendlyFireDebugLogs = true;

    [Header("Weapon Sprint Pose")]
    public Transform weaponHolder;
    public bool forceSprintPoseForTesting;
    public bool captureNormalPoseOnStart = true;
    public bool keepWeaponLoweredDuringSprintLockout = true;
    public Vector3 normalWeaponLocalPosition;
    public Vector3 sprintWeaponLocalPosition = new Vector3(0.15f, -0.45f, 0.15f);
    public Vector3 normalWeaponLocalRotation;
    public Vector3 sprintWeaponLocalRotation = new Vector3(25f, -10f, 8f);
    public float weaponPoseMoveSpeed = 10f;

    [Header("Aiming Down Sights")]
    public bool isAiming;
    public bool canAimDuringSprintLockout;
    public Vector3 aimWeaponLocalPosition = new Vector3(0f, -0.08f, 0.25f);
    public Vector3 aimWeaponLocalRotation = Vector3.zero;
    public float hipFieldOfView = 70f;
    public float aimFieldOfView = 45f;
    public float aimMoveSpeed = 12f;
    public float aimFovSpeed = 10f;

    [Range(0f, 1f)]
    public float aimAccuracy01;

    [Header("Headshot")]
    public float headshotDamageMultiplier = 2f;

    [Header("Debug Visuals")]
    public bool showShotLineInGame = true;
    public float shotLineDuration = 0.08f;
    public float shotLineWidth = 0.015f;
    public Color shotLineColor = Color.red;

    private int currentAmmo;
    private int reserveAmmo;
    private bool isCyclingBolt;
    private bool isReloading;
    private int rapidFireChainShotIndex;
    private float lastShotTime = -999f;
    private float rapidFireCurrentPenaltyPercent;

    public int CurrentAmmo => currentAmmo;
    public int ReserveAmmo => reserveAmmo;
    public bool IsReloading => isReloading;
    public bool IsEmpty => currentAmmo <= 0;
    public float RapidFireCurrentPenaltyPercent => rapidFireCurrentPenaltyPercent;

    public bool IsSprintFireLocked
    {
        get
        {
            if (playerController == null || weaponData == null)
            {
                return false;
            }

            if (playerController.IsSprinting)
            {
                return true;
            }

            float timeSinceSprintEnded = Time.time - playerController.LastSprintEndTime;

            return timeSinceSprintEnded < weaponData.sprintFireLockoutTime;
        }
    }

    public float SprintFireLockoutRemaining
    {
        get
        {
            if (playerController == null || weaponData == null)
            {
                return 0f;
            }

            if (playerController.IsSprinting)
            {
                return weaponData.sprintFireLockoutTime;
            }

            float timeSinceSprintEnded = Time.time - playerController.LastSprintEndTime;
            float remainingTime = weaponData.sprintFireLockoutTime - timeSinceSprintEnded;

            return Mathf.Max(0f, remainingTime);
        }
    }

    public float CurrentInaccuracyAngle
    {
        get
        {
            if (weaponData == null)
            {
                return 0f;
            }

            float inaccuracy = weaponData.baseInaccuracyAngle;

            if (playerController != null)
            {
                if (playerController.IsMoving)
                {
                    inaccuracy += weaponData.movingInaccuracyPenalty;
                }

                if (!playerController.IsGrounded)
                {
                    inaccuracy += weaponData.airborneInaccuracyPenalty;
                }
            }

            float aimMultiplier = Mathf.Lerp(1f, weaponData.aimingInaccuracyMultiplier, aimAccuracy01);
            inaccuracy *= aimMultiplier;

            if (weaponData.useRapidFireAccuracyPenalty && rapidFireCurrentPenaltyPercent > 0f)
            {
                inaccuracy *= 1f + rapidFireCurrentPenaltyPercent;
            }

            return inaccuracy;
        }
    }

    private void Awake()
    {
        if (playerCamera == null)
        {
            playerCamera = GetComponentInChildren<Camera>();
        }

        if (playerController == null)
        {
            playerController = GetComponent<PlayerController>();
        }

        if (weaponRecoil == null)
        {
            weaponRecoil = GetComponent<WeaponRecoil>();
        }

        if (shooterTeam == null)
        {
            shooterTeam = GetComponentInParent<PlayerTeam>();
        }

        if (audioSource == null)
        {
            audioSource = GetComponent<AudioSource>();
        }

        if (audioSource == null)
        {
            audioSource = gameObject.AddComponent<AudioSource>();
        }

        audioSource.playOnAwake = false;
        audioSource.spatialBlend = 0f;
    }

    private void Start()
    {
        if (weaponData == null)
        {
            Debug.LogError("No WeaponData assigned to BoltActionRifle.");
            return;
        }

        if (playerCamera != null)
        {
            playerCamera.fieldOfView = hipFieldOfView;
        }

        if (weaponHolder != null && captureNormalPoseOnStart)
        {
            normalWeaponLocalPosition = weaponHolder.localPosition;
            normalWeaponLocalRotation = weaponHolder.localEulerAngles;
        }

        currentAmmo = weaponData.clipSize;
        reserveAmmo = weaponData.startingReserveAmmo;

        Debug.Log(weaponData.weaponName + " loaded. Ammo: " + currentAmmo + "/" + reserveAmmo);
    }

    private void OnDisable()
    {
        if (playerController != null)
        {
            playerController.SetWeaponMoveSpeedMultiplier(1f);
        }
    }

    private void Update()
    {
        UpdateRapidFirePenaltyReset();
        UpdateAiming();
        UpdateWeaponSprintPose();

        if (Mouse.current == null || Keyboard.current == null || weaponData == null)
        {
            return;
        }

        if (Mouse.current.leftButton.wasPressedThisFrame)
        {
            TryFire();
        }

        if (Keyboard.current.rKey.wasPressedThisFrame)
        {
            TryReload();
        }
    }

    private void UpdateRapidFirePenaltyReset()
    {
        if (weaponData == null || !weaponData.useRapidFireAccuracyPenalty)
        {
            rapidFireChainShotIndex = 0;
            rapidFireCurrentPenaltyPercent = 0f;
            return;
        }

        if (Time.time - lastShotTime > weaponData.rapidFireResetTime)
        {
            rapidFireChainShotIndex = 0;
            rapidFireCurrentPenaltyPercent = 0f;
        }
    }

    private void UpdateAiming()
    {
        if (Mouse.current == null)
        {
            isAiming = false;
            aimAccuracy01 = 0f;
            UpdateCameraFov(false);
            ApplyAimMoveSpeedPenalty(false);
            return;
        }

        bool aimInputHeld = Mouse.current.rightButton.isPressed;
        bool blockedBySprint = IsSprintFireLocked && !canAimDuringSprintLockout;

        isAiming = aimInputHeld && !blockedBySprint && !isReloading;

        bool groundedForAimBonus = playerController == null || playerController.IsGrounded;
        bool shouldApplyAimAccuracy = isAiming && groundedForAimBonus;

        float targetAimAccuracy = shouldApplyAimAccuracy ? 1f : 0f;
        float buildTime = weaponData != null ? weaponData.aimAccuracyBuildTime : 0.2f;

        if (buildTime <= 0f)
        {
            aimAccuracy01 = targetAimAccuracy;
        }
        else
        {
            aimAccuracy01 = Mathf.MoveTowards(aimAccuracy01, targetAimAccuracy, Time.deltaTime / buildTime);
        }

        UpdateCameraFov(isAiming);
        ApplyAimMoveSpeedPenalty(isAiming);
    }

    private void ApplyAimMoveSpeedPenalty(bool aiming)
    {
        if (playerController == null)
        {
            return;
        }

        if (weaponData == null)
        {
            playerController.SetWeaponMoveSpeedMultiplier(1f);
            return;
        }

        playerController.SetWeaponMoveSpeedMultiplier(aiming ? weaponData.aimMoveSpeedMultiplier : 1f);
    }

    private void UpdateCameraFov(bool aiming)
    {
        if (playerCamera == null)
        {
            return;
        }

        float targetFov = aiming ? aimFieldOfView : hipFieldOfView;
        playerCamera.fieldOfView = Mathf.Lerp(playerCamera.fieldOfView, targetFov, Time.deltaTime * aimFovSpeed);
    }

    private void UpdateWeaponSprintPose()
    {
        if (weaponHolder == null)
        {
            Debug.LogWarning("Weapon holder is not assigned.");
            return;
        }

        bool shouldLowerWeapon = forceSprintPoseForTesting;

        if (playerController != null)
        {
            if (playerController.IsSprinting)
            {
                shouldLowerWeapon = true;
            }
            else if (keepWeaponLoweredDuringSprintLockout && IsSprintFireLocked && !isAiming)
            {
                shouldLowerWeapon = true;
            }
        }

        Vector3 basePosition;
        Vector3 baseRotation;

        if (shouldLowerWeapon)
        {
            basePosition = sprintWeaponLocalPosition;
            baseRotation = sprintWeaponLocalRotation;
        }
        else if (isAiming)
        {
            basePosition = aimWeaponLocalPosition;
            baseRotation = aimWeaponLocalRotation;
        }
        else
        {
            basePosition = normalWeaponLocalPosition;
            baseRotation = normalWeaponLocalRotation;
        }

        Vector3 recoilPositionOffset = weaponRecoil != null ? weaponRecoil.PositionOffset : Vector3.zero;
        Vector3 recoilRotationOffset = weaponRecoil != null ? weaponRecoil.RotationOffset : Vector3.zero;

        Vector3 targetPosition = basePosition + recoilPositionOffset;
        Quaternion targetRotation = Quaternion.Euler(baseRotation + recoilRotationOffset);

        float moveSpeed = isAiming ? aimMoveSpeed : weaponPoseMoveSpeed;

        weaponHolder.localPosition = Vector3.Lerp(weaponHolder.localPosition, targetPosition, Time.deltaTime * moveSpeed);
        weaponHolder.localRotation = Quaternion.Slerp(weaponHolder.localRotation, targetRotation, Time.deltaTime * moveSpeed);
    }

    private void TryFire()
    {
        if (isReloading)
        {
            Debug.Log("Cannot fire while reloading.");
            return;
        }

        if (isCyclingBolt)
        {
            Debug.Log("Cycling bolt.");
            return;
        }

        if (!CanFireAfterSprint())
        {
            Debug.Log("Cannot fire while sprinting or recovering from sprint.");
            return;
        }

        if (currentAmmo <= 0)
        {
            Debug.Log("Empty. Press R to reload.");
            return;
        }

        Fire();
        currentAmmo--;

        Debug.Log("Ammo: " + currentAmmo + "/" + reserveAmmo);

        StartCoroutine(CycleBolt());
    }

    private bool CanFireAfterSprint()
    {
        return !IsSprintFireLocked;
    }

    private void Fire()
    {
        UpdateRapidFirePenaltyForShot();

        Ray ray = GetShotRay();

        PlayFireSound();
        SpawnMuzzleFlash();

        if (weaponRecoil != null)
        {
            float recoilMultiplier = weaponRecoil.GetRecoilMultiplier(isAiming);
            weaponRecoil.ApplyRecoil(recoilMultiplier);
        }

        StartCoroutine(ResolveShotAfterTravel(ray));
    }

    private void UpdateRapidFirePenaltyForShot()
    {
        if (weaponData == null || !weaponData.useRapidFireAccuracyPenalty)
        {
            rapidFireChainShotIndex = 0;
            rapidFireCurrentPenaltyPercent = 0f;
            lastShotTime = Time.time;
            return;
        }

        if (Time.time - lastShotTime <= weaponData.rapidFireResetTime)
        {
            rapidFireChainShotIndex++;
        }
        else
        {
            rapidFireChainShotIndex = 0;
        }

        if (rapidFireChainShotIndex <= 0)
        {
            rapidFireCurrentPenaltyPercent = 0f;
        }
        else
        {
            float penalty = weaponData.rapidFireSecondShotPenalty - ((rapidFireChainShotIndex - 1) * weaponData.rapidFirePenaltyStep);
            rapidFireCurrentPenaltyPercent = Mathf.Max(weaponData.rapidFireMinimumPenalty, penalty);
        }

        lastShotTime = Time.time;
    }

    private IEnumerator ResolveShotAfterTravel(Ray ray)
    {
        Vector3 shotStartPoint = muzzlePoint != null ? muzzlePoint.position : ray.origin;
        Vector3 shotEndPoint = ray.origin + ray.direction * weaponData.range;

        bool didHit = Physics.Raycast(ray, out RaycastHit hit, weaponData.range, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);

        float travelDistance = weaponData.range;

        if (didHit)
        {
            shotEndPoint = hit.point;
            travelDistance = Vector3.Distance(shotStartPoint, hit.point);
        }

        float travelTime = GetBulletTravelTime(travelDistance);

        if (showShotLineInGame && muzzlePoint != null)
        {
            StartCoroutine(ShowTravelLine(shotStartPoint, shotEndPoint, travelTime));
        }

        if (travelTime > 0f)
        {
            yield return new WaitForSeconds(travelTime);
        }

        if (!didHit)
        {
            Debug.Log("Missed.");
            yield break;
        }

        if (hit.collider == null)
        {
            yield break;
        }

        SpawnBulletImpact(hit);

        Debug.Log("Hit: " + hit.collider.name);

        HealthComponent health = hit.collider.GetComponentInParent<HealthComponent>();

        if (health != null)
        {
            PlayerTeam targetTeam = health.GetComponentInParent<PlayerTeam>();

            if (IsFriendlyFireBlocked(targetTeam))
            {
                if (showFriendlyFireDebugLogs)
                {
                    Debug.Log("Friendly fire blocked on " + health.gameObject.name);
                }

                yield break;
            }

            HitboxDamageZone damageZone = hit.collider.GetComponent<HitboxDamageZone>();

            if (damageZone == null)
            {
                damageZone = hit.collider.GetComponentInParent<HitboxDamageZone>();
            }

            Debug.Log("Hit collider: " + hit.collider.name + " | Damage Zone: " + (damageZone != null ? damageZone.zoneName : "None"));

            bool isHeadshot = false;
            float damageMultiplier = 1f;

            if (damageZone != null)
            {
                isHeadshot = damageZone.countsAsHeadshot;
                damageMultiplier = damageZone.damageMultiplier;
            }
            else
            {
                isHeadshot = IsHeadshot(hit.collider);
                damageMultiplier = isHeadshot ? headshotDamageMultiplier : 1f;
            }

            float finalDamage = weaponData.damage * damageMultiplier;
            bool killedTarget = health.TakeDamage(finalDamage);

            if (killedTarget && isHeadshot)
            {
                HelmetPopOff helmetPopOff = health.GetComponent<HelmetPopOff>();

                if (helmetPopOff != null)
                {
                    helmetPopOff.PopOff(ray.direction);
                }
            }

            if (hitMarkerUI != null)
            {
                hitMarkerUI.ShowHitMarker(finalDamage, killedTarget, isHeadshot);
            }
        }
        else
        {
            Debug.LogWarning("No HealthComponent found on " + hit.collider.name + " or its parents.");
        }
    }

    private bool IsFriendlyFireBlocked(PlayerTeam targetTeam)
    {
        if (allowFriendlyFire)
        {
            return false;
        }

        if (shooterTeam == null || targetTeam == null)
        {
            return false;
        }

        if (shooterTeam.team == Team.Neutral || targetTeam.team == Team.Neutral)
        {
            return false;
        }

        return shooterTeam.team == targetTeam.team;
    }

    private float GetBulletTravelTime(float distance)
    {
        if (weaponData == null || !weaponData.useBulletTravelTime)
        {
            return 0f;
        }

        if (weaponData.muzzleVelocity <= 0f)
        {
            return 0f;
        }

        float travelTime = distance / weaponData.muzzleVelocity;

        return Mathf.Max(weaponData.minimumBulletTravelTime, travelTime);
    }

    private IEnumerator ShowTravelLine(Vector3 startPoint, Vector3 endPoint, float travelTime)
    {
        GameObject lineObject = new GameObject("BulletTravelLine");
        LineRenderer lineRenderer = lineObject.AddComponent<LineRenderer>();

        lineRenderer.positionCount = 2;
        lineRenderer.startWidth = shotLineWidth;
        lineRenderer.endWidth = shotLineWidth;
        lineRenderer.startColor = shotLineColor;
        lineRenderer.endColor = shotLineColor;
        lineRenderer.material = new Material(Shader.Find("Sprites/Default"));

        if (travelTime <= 0f)
        {
            lineRenderer.SetPosition(0, startPoint);
            lineRenderer.SetPosition(1, endPoint);

            yield return new WaitForSeconds(shotLineDuration);

            Destroy(lineObject);
            yield break;
        }

        float timer = 0f;

        while (timer < travelTime)
        {
            timer += Time.deltaTime;

            float progress = Mathf.Clamp01(timer / travelTime);
            Vector3 currentEndPoint = Vector3.Lerp(startPoint, endPoint, progress);

            lineRenderer.SetPosition(0, startPoint);
            lineRenderer.SetPosition(1, currentEndPoint);

            yield return null;
        }

        lineRenderer.SetPosition(0, startPoint);
        lineRenderer.SetPosition(1, endPoint);

        yield return new WaitForSeconds(shotLineDuration);

        Destroy(lineObject);
    }

    private bool IsHeadshot(Collider hitCollider)
    {
        string colliderName = hitCollider.name.ToLower();

        return colliderName.Contains("head");
    }

    private Ray GetShotRay()
    {
        Ray centerRay = playerCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));

        float inaccuracyAngle = CurrentInaccuracyAngle;

        if (inaccuracyAngle <= 0f)
        {
            return centerRay;
        }

        float randomAngle = Random.Range(0f, Mathf.PI * 2f);
        float randomRadius = Random.Range(0f, Mathf.Tan(inaccuracyAngle * Mathf.Deg2Rad));

        Vector3 spreadDirection = centerRay.direction;
        spreadDirection += playerCamera.transform.right * Mathf.Cos(randomAngle) * randomRadius;
        spreadDirection += playerCamera.transform.up * Mathf.Sin(randomAngle) * randomRadius;
        spreadDirection.Normalize();

        return new Ray(centerRay.origin, spreadDirection);
    }

    private void PlayFireSound()
    {
        if (weaponData.fireSound == null || audioSource == null)
        {
            return;
        }

        audioSource.PlayOneShot(weaponData.fireSound, weaponData.fireSoundVolume);
    }

    private void PlayReloadSound()
    {
        if (weaponData.reloadSound == null || audioSource == null)
        {
            return;
        }

        audioSource.PlayOneShot(weaponData.reloadSound, weaponData.reloadSoundVolume);
    }

    private void PlayBoltCycleSound()
    {
        if (weaponData.boltCycleSound == null || audioSource == null)
        {
            return;
        }

        audioSource.PlayOneShot(weaponData.boltCycleSound, weaponData.boltCycleSoundVolume);
    }

    private void SpawnMuzzleFlash()
    {
        if (muzzleFlashPrefab == null || muzzlePoint == null)
        {
            return;
        }

        GameObject flash = Instantiate(muzzleFlashPrefab, muzzlePoint.position, muzzlePoint.rotation);

        ParticleSystem[] particleSystems = flash.GetComponentsInChildren<ParticleSystem>();

        foreach (ParticleSystem particleSystem in particleSystems)
        {
            ParticleSystem.MainModule main = particleSystem.main;
            main.loop = false;

            particleSystem.Clear();
            particleSystem.Play();
        }

        Destroy(flash, 0.25f);
    }

    private void SpawnBulletImpact(RaycastHit hit)
    {
        if (bulletImpactPrefab == null)
        {
            return;
        }

        Vector3 spawnPosition = hit.point + hit.normal * 0.01f;
        Quaternion spawnRotation = Quaternion.LookRotation(hit.normal);

        GameObject impact = Instantiate(bulletImpactPrefab, spawnPosition, spawnRotation);

        ParticleSystem[] particleSystems = impact.GetComponentsInChildren<ParticleSystem>();

        foreach (ParticleSystem particleSystem in particleSystems)
        {
            ParticleSystem.MainModule main = particleSystem.main;
            main.loop = false;

            particleSystem.Clear();
            particleSystem.Play();
        }

        Destroy(impact, bulletImpactLifetime);
    }

    private IEnumerator CycleBolt()
    {
        isCyclingBolt = true;

        float delay = Mathf.Clamp(weaponData.boltCycleSoundDelay, 0f, weaponData.boltCycleTime);

        if (delay > 0f)
        {
            yield return new WaitForSeconds(delay);
        }

        PlayBoltCycleSound();

        float remainingTime = weaponData.boltCycleTime - delay;

        if (remainingTime > 0f)
        {
            yield return new WaitForSeconds(remainingTime);
        }

        isCyclingBolt = false;
    }

    private void TryReload()
    {
        if (isReloading)
        {
            return;
        }

        if (playerController != null && playerController.IsSprinting)
        {
            Debug.Log("Cannot reload while sprinting.");
            return;
        }

        if (currentAmmo >= weaponData.clipSize)
        {
            Debug.Log("Clip already full.");
            return;
        }

        if (reserveAmmo <= 0)
        {
            Debug.Log("No reserve ammo.");
            return;
        }

        StartCoroutine(Reload());
    }

    private IEnumerator Reload()
    {
        isReloading = true;
        Debug.Log("Reloading...");

        PlayReloadSound();

        yield return new WaitForSeconds(weaponData.reloadTime);

        int ammoNeeded = weaponData.clipSize - currentAmmo;
        int ammoToLoad = Mathf.Min(ammoNeeded, reserveAmmo);

        currentAmmo += ammoToLoad;
        reserveAmmo -= ammoToLoad;

        isReloading = false;

        Debug.Log("Reloaded. Ammo: " + currentAmmo + "/" + reserveAmmo);
    }
}