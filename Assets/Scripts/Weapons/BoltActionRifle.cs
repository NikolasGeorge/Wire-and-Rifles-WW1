using System.Collections;
using FishNet.Connection;
using FishNet.Object;
using UnityEngine;
using UnityEngine.InputSystem;

public class BoltActionRifle : NetworkBehaviour
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

    // Server-authoritative mirror of ammo and fire timing, used to validate
    // client shot reports. Client-side fields above stay for responsiveness.
    private int serverCurrentAmmo;
    private int serverReserveAmmo;
    private bool serverIsReloading;
    private float serverLastShotTime = -999f;

    private const float ServerFireCooldownLeniency = 0.9f;
    private const float ServerShotOriginTolerance = 4f;
    private const float ServerRangeTolerance = 1.1f;
    private const float ApproximateEyeHeight = 1.6f;

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

    public override void OnStartServer()
    {
        base.OnStartServer();

        if (weaponData != null)
        {
            serverCurrentAmmo = weaponData.clipSize;
            serverReserveAmmo = weaponData.startingReserveAmmo;
        }
    }

    private void Update()
    {
        if (!IsOwner)
        {
            return;
        }

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

        bool didHit = Physics.Raycast(ray, out RaycastHit hit, weaponData.range, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);

        Vector3 shotStartPoint = muzzlePoint != null ? muzzlePoint.position : ray.origin;
        Vector3 shotEndPoint = didHit ? hit.point : ray.origin + ray.direction * weaponData.range;
        Vector3 hitNormal = didHit ? hit.normal : Vector3.zero;

        NetworkObject targetObject = null;
        float damageMultiplier = 1f;
        bool isHeadshot = false;

        if (didHit)
        {
            GetHitboxInfo(hit.collider, out damageMultiplier, out isHeadshot);

            targetObject = hit.collider.GetComponentInParent<NetworkObject>();

            if (targetObject == NetworkObject)
            {
                targetObject = null;
            }
        }

        ServerReportFire(shotStartPoint, shotEndPoint, didHit, hitNormal, targetObject, damageMultiplier, isHeadshot);

        StartCoroutine(ResolveShotAfterTravel(ray, didHit, hit, shotStartPoint, shotEndPoint, targetObject));
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

    private IEnumerator ResolveShotAfterTravel(Ray ray, bool didHit, RaycastHit hit, Vector3 shotStartPoint, Vector3 shotEndPoint, NetworkObject targetObject)
    {
        float travelDistance = didHit ? Vector3.Distance(shotStartPoint, shotEndPoint) : weaponData.range;
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

        if (targetObject != null)
        {
            // Networked target: the server applies damage and sends the hit
            // marker back via TargetRpc.
            yield break;
        }

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

            GetHitboxInfo(hit.collider, out float damageMultiplier, out bool isHeadshot);

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

        // Scheduled with the engine so the line can never outlive its timer,
        // even if this coroutine is interrupted before reaching Destroy below.
        Destroy(lineObject, Mathf.Max(0f, travelTime) + shotLineDuration + 0.1f);

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
        SpawnBulletImpactAtPoint(hit.point, hit.normal);
    }

    private void SpawnBulletImpactAtPoint(Vector3 point, Vector3 normal)
    {
        if (bulletImpactPrefab == null)
        {
            return;
        }

        Vector3 spawnPosition = point + normal * 0.01f;
        Quaternion spawnRotation = Quaternion.LookRotation(normal);

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
        ServerReportReload();
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

    private void GetHitboxInfo(Collider hitCollider, out float damageMultiplier, out bool isHeadshot)
    {
        HitboxDamageZone damageZone = hitCollider.GetComponent<HitboxDamageZone>();

        if (damageZone == null)
        {
            damageZone = hitCollider.GetComponentInParent<HitboxDamageZone>();
        }

        if (damageZone != null)
        {
            isHeadshot = damageZone.countsAsHeadshot;
            damageMultiplier = damageZone.damageMultiplier;
        }
        else
        {
            isHeadshot = IsHeadshot(hitCollider);
            damageMultiplier = isHeadshot ? headshotDamageMultiplier : 1f;
        }
    }

    [ServerRpc]
    private void ServerReportFire(Vector3 shotStartPoint, Vector3 shotEndPoint, bool didHit, Vector3 hitNormal, NetworkObject targetObject, float damageMultiplier, bool isHeadshot)
    {
        if (weaponData == null)
        {
            return;
        }

        if (serverIsReloading || serverCurrentAmmo <= 0)
        {
            Debug.Log("[Rifle Server] Shot rejected: reloading or out of ammo (" + serverCurrentAmmo + ").");
            return;
        }

        if (Time.time - serverLastShotTime < weaponData.boltCycleTime * ServerFireCooldownLeniency)
        {
            Debug.Log("[Rifle Server] Shot rejected: fired faster than bolt cycle allows.");
            return;
        }

        Vector3 approximateEyePosition = transform.position + Vector3.up * ApproximateEyeHeight;

        if (Vector3.Distance(approximateEyePosition, shotStartPoint) > ServerShotOriginTolerance)
        {
            Debug.Log("[Rifle Server] Shot rejected: origin too far from player (" + Vector3.Distance(approximateEyePosition, shotStartPoint).ToString("F1") + "m).");
            return;
        }

        if (didHit && Vector3.Distance(shotStartPoint, shotEndPoint) > weaponData.range * ServerRangeTolerance)
        {
            Debug.Log("[Rifle Server] Shot rejected: hit beyond weapon range.");
            return;
        }

        serverCurrentAmmo--;
        serverLastShotTime = Time.time;

        ObserversPlayFireEffects(shotStartPoint, shotEndPoint, didHit, hitNormal);

        if (!didHit || targetObject == null || targetObject == NetworkObject)
        {
            return;
        }

        float clampedMultiplier = Mathf.Clamp(damageMultiplier, 0f, headshotDamageMultiplier);

        StartCoroutine(ServerApplyHitAfterTravel(Owner, targetObject, shotStartPoint, shotEndPoint, clampedMultiplier, isHeadshot));
    }

    private IEnumerator ServerApplyHitAfterTravel(NetworkConnection shooter, NetworkObject targetObject, Vector3 shotStartPoint, Vector3 shotEndPoint, float damageMultiplier, bool isHeadshot)
    {
        float travelTime = GetBulletTravelTime(Vector3.Distance(shotStartPoint, shotEndPoint));

        if (travelTime > 0f)
        {
            yield return new WaitForSeconds(travelTime);
        }

        if (targetObject == null)
        {
            yield break;
        }

        PlayerTeam targetTeam = targetObject.GetComponentInChildren<PlayerTeam>(true);

        if (IsFriendlyFireBlocked(targetTeam))
        {
            yield break;
        }

        float finalDamage = weaponData.damage * damageMultiplier;
        bool killedTarget = false;

        PlayerNetworkHealth playerHealth = targetObject.GetComponent<PlayerNetworkHealth>();

        if (playerHealth != null)
        {
            if (playerHealth.IsDead)
            {
                yield break;
            }

            killedTarget = playerHealth.ServerTakeDamage(finalDamage);
        }
        else
        {
            HealthComponent health = targetObject.GetComponentInChildren<HealthComponent>(true);

            if (health != null)
            {
                if (health.IsDead)
                {
                    yield break;
                }

                killedTarget = health.TakeDamage(finalDamage);
            }
        }

        if (shooter != null && shooter.IsActive)
        {
            TargetHitConfirmed(shooter, finalDamage, killedTarget, isHeadshot);
        }
    }

    [TargetRpc]
    private void TargetHitConfirmed(NetworkConnection connection, float damage, bool killedTarget, bool isHeadshot)
    {
        if (hitMarkerUI != null)
        {
            hitMarkerUI.ShowHitMarker(damage, killedTarget, isHeadshot);
        }
    }

    [ObserversRpc(ExcludeOwner = true)]
    private void ObserversPlayFireEffects(Vector3 shotStartPoint, Vector3 shotEndPoint, bool didHit, Vector3 hitNormal)
    {
        if (weaponData == null)
        {
            return;
        }

        if (weaponData.fireSound != null)
        {
            AudioSource.PlayClipAtPoint(weaponData.fireSound, shotStartPoint, weaponData.fireSoundVolume);
        }

        StartCoroutine(RemoteBoltCycleSound());

        float travelTime = GetBulletTravelTime(Vector3.Distance(shotStartPoint, shotEndPoint));

        if (showShotLineInGame)
        {
            StartCoroutine(ShowTravelLine(shotStartPoint, shotEndPoint, travelTime));
        }

        if (didHit)
        {
            StartCoroutine(RemoteImpactAfterTravel(shotEndPoint, hitNormal, travelTime));
        }
    }

    private IEnumerator RemoteImpactAfterTravel(Vector3 point, Vector3 normal, float travelTime)
    {
        if (travelTime > 0f)
        {
            yield return new WaitForSeconds(travelTime);
        }

        SpawnBulletImpactAtPoint(point, normal);
    }

    private IEnumerator RemoteBoltCycleSound()
    {
        if (weaponData.boltCycleSound == null)
        {
            yield break;
        }

        yield return new WaitForSeconds(Mathf.Max(0f, weaponData.boltCycleSoundDelay));

        AudioSource.PlayClipAtPoint(weaponData.boltCycleSound, transform.position + Vector3.up * 1.5f, weaponData.boltCycleSoundVolume);
    }

    [ServerRpc]
    private void ServerReportReload()
    {
        if (weaponData == null || serverIsReloading || serverCurrentAmmo >= weaponData.clipSize || serverReserveAmmo <= 0)
        {
            return;
        }

        StartCoroutine(ServerReload());
        ObserversPlayReloadSound();
    }

    private IEnumerator ServerReload()
    {
        serverIsReloading = true;

        yield return new WaitForSeconds(weaponData.reloadTime);

        int ammoNeeded = weaponData.clipSize - serverCurrentAmmo;
        int ammoToLoad = Mathf.Min(ammoNeeded, serverReserveAmmo);

        serverCurrentAmmo += ammoToLoad;
        serverReserveAmmo -= ammoToLoad;

        serverIsReloading = false;
    }

    [ObserversRpc(ExcludeOwner = true)]
    private void ObserversPlayReloadSound()
    {
        if (weaponData == null || weaponData.reloadSound == null)
        {
            return;
        }

        AudioSource.PlayClipAtPoint(weaponData.reloadSound, transform.position + Vector3.up * 1.5f, weaponData.reloadSoundVolume);
    }
}