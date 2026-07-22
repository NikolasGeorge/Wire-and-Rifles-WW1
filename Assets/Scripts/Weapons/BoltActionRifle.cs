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

    // Incoming-fire state for this player; widens spread while suppressed.
    private PlayerSuppression suppression;

    [Tooltip("Scales how hard this weapon's near-misses suppress, on top of damage weighting.")]
    public float suppressionMultiplier = 1f;

    [Header("Aiming Down Sights")]
    public bool isAiming;
    // Latch for toggle-ADS gameplay option (see GameSettings.ToggleAds).
    private bool adsToggleState;
    public bool canAimDuringSprintLockout;
    public Vector3 aimWeaponLocalPosition = new Vector3(0f, -0.08f, 0.25f);
    public Vector3 aimWeaponLocalRotation = Vector3.zero;
    public float hipFieldOfView = 70f;
    public float aimFieldOfView = 45f;
    public float aimMoveSpeed = 12f;
    public float aimFovSpeed = 10f;

    [Range(0f, 1f)]
    public float aimAccuracy01;

    [Header("Class Weapon")]
    public WeaponFireMode fireMode = WeaponFireMode.BoltAction;
    public float damageMid = -1f;
    public float damageLong = -1f;
    public float closeRangeEnd = 50f;
    public float midRangeEnd = 100f;
    public int pelletsPerShot = 1;
    public float pelletSpreadAngle = 10f;
    public bool shellByShellReload;
    public bool requiresDeploySetup;
    public float deploySetupTime = 1.5f;

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

    // Class loadouts override the WeaponData default reserve at spawn. Stored
    // so Start() cannot stomp it regardless of callback ordering.
    private int classReserveAmmoOverride = -1;

    private float deployTimer;
    private float lastDeployBlockedFireTime = -999f;

    public bool IsDeployed => !requiresDeploySetup || deployTimer >= deploySetupTime;

    // Deploy is an incentive, not a gate: the weapon always fires, but a
    // completed deploy shrinks spread and recoil to these fractions.
    public float deployedSpreadMultiplier = 0.35f;
    public float deployedRecoilMultiplier = 0.4f;

    private bool DeployBonusActive => requiresDeploySetup && deployTimer >= deploySetupTime;

    // Replaces the shared WeaponData with a per-instance copy carrying this
    // class's primary stats. Runs on every instance (server and clients) from
    // PlayerNetworkSetup.ApplyClass, so all sides agree on damage, fire rate,
    // and clip size, and the server's fire-rate validation uses the right
    // interval.
    public void ApplyWeaponProfile(WeaponProfile profile)
    {
        if (weaponData == null)
        {
            return;
        }

        weaponData = Instantiate(weaponData);
        weaponData.weaponName = profile.displayName;
        weaponData.damage = profile.damageClose;
        weaponData.range = profile.range;
        weaponData.muzzleVelocity = profile.muzzleVelocity;
        weaponData.clipSize = profile.clipSize;
        weaponData.startingReserveAmmo = profile.reserveAmmo;
        weaponData.boltCycleTime = profile.fireInterval;
        weaponData.reloadTime = profile.reloadTime;
        weaponData.baseInaccuracyAngle = profile.baseInaccuracyAngle;
        weaponData.movingInaccuracyPenalty = profile.movingInaccuracyPenalty;
        weaponData.airborneInaccuracyPenalty = profile.airborneInaccuracyPenalty;
        weaponData.aimingInaccuracyMultiplier = profile.aimingInaccuracyMultiplier;
        weaponData.aimAccuracyBuildTime = profile.aimAccuracyBuildTime;
        weaponData.aimMoveSpeedMultiplier = profile.aimMoveSpeedMultiplier;
        weaponData.useRapidFireAccuracyPenalty = profile.useRapidFirePenalty;

        fireMode = profile.fireMode;
        damageMid = profile.damageMid;
        damageLong = profile.damageLong;
        closeRangeEnd = profile.closeRangeEnd;
        midRangeEnd = profile.midRangeEnd;
        pelletsPerShot = Mathf.Max(1, profile.pelletsPerShot);
        pelletSpreadAngle = profile.pelletSpreadAngle;
        shellByShellReload = profile.shellByShellReload;
        requiresDeploySetup = profile.requiresDeploySetup;
        deploySetupTime = profile.deploySetupTime;

        // Unset in a profile means "ordinary", not "cannot suppress".
        suppressionMultiplier = profile.suppressionMultiplier > 0f ? profile.suppressionMultiplier : 1f;

        hipFieldOfView = profile.hipFieldOfView;
        aimFieldOfView = profile.aimFieldOfView;

        if (weaponRecoil != null)
        {
            weaponRecoil.hipRecoilMultiplier = profile.hipRecoilMultiplier;
            weaponRecoil.aimRecoilMultiplier = profile.aimRecoilMultiplier;
            weaponRecoil.cameraPitchKick = profile.cameraPitchKick;
            weaponRecoil.cameraYawRandom = profile.cameraYawRandom;
        }

        currentAmmo = weaponData.clipSize;
        SetClassReserveAmmo(profile.reserveAmmo);

        if (IsServerInitialized)
        {
            serverCurrentAmmo = weaponData.clipSize;
        }
    }

    // Server-side resupply from ammo crates: tops up the authoritative
    // reserve (capped at the weapon's full loadout) and mirrors the new count
    // back to the owning client.
    public void ServerGrantReserveAmmo(int amount)
    {
        if (!IsServerInitialized || weaponData == null)
        {
            return;
        }

        int cap = classReserveAmmoOverride >= 0 ? classReserveAmmoOverride : weaponData.startingReserveAmmo;

        if (serverReserveAmmo >= cap)
        {
            return;
        }

        serverReserveAmmo = Mathf.Min(cap, serverReserveAmmo + amount);

        if (Owner != null && Owner.IsActive)
        {
            TargetReserveAmmoUpdated(Owner, serverReserveAmmo);
        }
    }

    [TargetRpc]
    private void TargetReserveAmmoUpdated(NetworkConnection connection, int newReserve)
    {
        // Resupply is otherwise completely silent — the number in the corner
        // just quietly climbs and nobody notices the crate is working.
        if (newReserve > reserveAmmo)
        {
            ProceduralAudio.PlayAt(ProceduralAudio.Resupply, transform.position, 0.35f);
        }

        reserveAmmo = newReserve;
    }

    // Players spawn with the profile's full reserve, but the CAP is 150% of
    // it — Support's ammo crate can overfill everyone half again beyond a
    // fresh spawn.
    public void SetClassReserveAmmo(int reserve)
    {
        classReserveAmmoOverride = reserve * 3 / 2;
        reserveAmmo = reserve;

        if (IsServerInitialized)
        {
            serverReserveAmmo = reserve;
        }
    }
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

            if (DeployBonusActive)
            {
                inaccuracy *= deployedSpreadMultiplier;
            }

            // Being shot at makes you shoot worse. Added rather than scaled
            // so it still bites a fully-settled ADS shot.
            if (suppression != null)
            {
                inaccuracy += suppression.InaccuracyPenalty;
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

        suppression = GetComponentInParent<PlayerSuppression>();

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
        reserveAmmo = classReserveAmmoOverride >= 0 ? classReserveAmmoOverride : weaponData.startingReserveAmmo;

        Debug.Log(weaponData.weaponName + " loaded. Ammo: " + currentAmmo + "/" + reserveAmmo);
    }

    private void OnDisable()
    {
        if (playerController != null)
        {
            playerController.SetWeaponMoveSpeedMultiplier(1f);
        }
    }

    // Called when the item-slot system switches away from the rifle, so ADS
    // zoom and the aim move-speed penalty never stay stuck.
    public void ForceStopAiming()
    {
        isAiming = false;
        adsToggleState = false;

        if (playerCamera != null)
        {
            playerCamera.fieldOfView = hipFieldOfView;
        }

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
            serverReserveAmmo = classReserveAmmoOverride >= 0 ? classReserveAmmoOverride : weaponData.startingReserveAmmo;
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
        HandleAimPoseTuning();
        UpdateWeaponSprintPose();

        // LMG setup: deploying means holding aim in place for the setup time.
        if (requiresDeploySetup)
        {
            bool movingBreaksDeploy = playerController != null && playerController.IsMoving;
            deployTimer = isAiming && !movingBreaksDeploy ? deployTimer + Time.deltaTime : 0f;
        }

        if (Mouse.current == null || Keyboard.current == null || weaponData == null)
        {
            return;
        }

        if (PauseMenu.IsOpen || FortificationBuilder.MenuOpen)
        {
            return;
        }

        bool firePressed = fireMode == WeaponFireMode.Automatic
            ? Mouse.current.leftButton.isPressed
            : Mouse.current.leftButton.wasPressedThisFrame;

        if (firePressed)
        {
            TryFire();
        }

        if (GameSettings.Pressed(GameAction.Reload))
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

        bool aimInputActive;

        if (GameSettings.ToggleAds)
        {
            // Right-click flips ADS on/off and holds it.
            if (Mouse.current.rightButton.wasPressedThisFrame)
            {
                adsToggleState = !adsToggleState;
            }

            aimInputActive = adsToggleState;
        }
        else
        {
            adsToggleState = false;
            aimInputActive = Mouse.current.rightButton.isPressed;
        }

        bool blockedBySprint = IsSprintFireLocked && !canAimDuringSprintLockout;

        isAiming = aimInputActive && !blockedBySprint && !isReloading;

        // Keep the toggle latch in sync when aiming is force-broken, so the
        // next right-click always turns ADS on rather than off.
        if (!isAiming)
        {
            adsToggleState = false;
        }

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

        // Capture the aim ray from the camera's orientation at the moment of
        // the trigger pull, before recoil kicks the camera for the NEXT shot.
        Ray ray = GetShotRay();

        PlayFireSound();
        SpawnMuzzleFlash();

        if (weaponRecoil != null)
        {
            float recoilMultiplier = weaponRecoil.GetRecoilMultiplier(isAiming);

            if (DeployBonusActive)
            {
                recoilMultiplier *= deployedRecoilMultiplier;
            }

            weaponRecoil.ApplyRecoil(recoilMultiplier);
        }

        if (pelletsPerShot > 1)
        {
            FirePellets(ray);
            return;
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

    // Shotgun: every pellet gets its own spread ray and resolves its own
    // impact/dummy damage locally, but pellets that hit the same networked
    // target are combined into ONE server report whose damage multiplier is
    // the sum of the per-pellet hitbox multipliers (weaponData.damage is
    // per-pellet). Only the first networked target hit is reported — one
    // trigger pull, one server-validated hit.
    private void FirePellets(Ray aimRay)
    {
        Vector3 shotStartPoint = muzzlePoint != null ? muzzlePoint.position : playerCamera.transform.position;

        NetworkObject reportTarget = null;
        float reportMultiplier = 0f;
        bool reportHeadshot = false;
        Vector3 reportEndPoint = Vector3.zero;
        Vector3 reportNormal = Vector3.zero;

        // One aim ray (captured pre-recoil) carries the inaccuracy; every
        // pellet then deviates within the fixed spread cone around it.
        float spreadRadius = Mathf.Tan(pelletSpreadAngle * 0.5f * Mathf.Deg2Rad);

        for (int pellet = 0; pellet < pelletsPerShot; pellet++)
        {
            float spreadAngle = Random.Range(0f, Mathf.PI * 2f);
            float radius = Random.Range(0f, spreadRadius);

            Vector3 pelletDirection = aimRay.direction;
            pelletDirection += playerCamera.transform.right * Mathf.Cos(spreadAngle) * radius;
            pelletDirection += playerCamera.transform.up * Mathf.Sin(spreadAngle) * radius;

            Ray ray = new Ray(aimRay.origin, pelletDirection.normalized);
            bool didHit = Physics.Raycast(ray, out RaycastHit hit, weaponData.range, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);

            NetworkObject pelletTarget = null;

            if (didHit)
            {
                pelletTarget = hit.collider.GetComponentInParent<NetworkObject>();

                if (pelletTarget == NetworkObject)
                {
                    pelletTarget = null;
                }

                if (pelletTarget != null)
                {
                    GetHitboxInfo(hit.collider, out float pelletMultiplier, out bool pelletHeadshot);

                    if (reportTarget == null)
                    {
                        reportTarget = pelletTarget;
                        reportEndPoint = hit.point;
                        reportNormal = hit.normal;
                    }

                    if (pelletTarget == reportTarget)
                    {
                        reportMultiplier += pelletMultiplier;
                        reportHeadshot |= pelletHeadshot;
                    }
                }
            }

            Vector3 pelletEnd = didHit ? hit.point : ray.origin + ray.direction * weaponData.range;
            StartCoroutine(ResolveShotAfterTravel(ray, didHit, hit, shotStartPoint, pelletEnd, pelletTarget));
        }

        if (reportTarget != null)
        {
            ServerReportFire(shotStartPoint, reportEndPoint, true, reportNormal, reportTarget, reportMultiplier, reportHeadshot);
        }
        else
        {
            // No networked victim: still report so the server spends the shell
            // and observers get the fire effects.
            Ray centerRay = playerCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            ServerReportFire(shotStartPoint, shotStartPoint + centerRay.direction * weaponData.range, false, Vector3.zero, null, 1f, false);
        }
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

        // Player-built structures: report damage to the server (blueprints
        // are ignored server-side; friendly fire on structures is blocked).
        FortificationStructure fortification = hit.collider.GetComponentInParent<FortificationStructure>();

        if (fortification != null)
        {
            if (FortificationManager.Instance != null)
            {
                float structureShotDistance = Vector3.Distance(shotStartPoint, shotEndPoint);
                FortificationManager.Instance.ReportStructureDamage(
                    fortification.id, GetBaseDamageAtDistance(structureShotDistance, false), DamageType.Bullet);
            }

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

            float shotDistance = Vector3.Distance(shotStartPoint, shotEndPoint);
            float finalDamage = GetBaseDamageAtDistance(shotDistance, isHeadshot) * damageMultiplier;
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

    // What a round would have done had it connected at this range. Drives
    // suppression: how badly a near-miss rattles you scales with how badly
    // it could have hurt you.
    public float GetPotentialDamageAt(float distance)
    {
        return GetBaseDamageAtDistance(distance, false);
    }

    // The same figure scaled by the weapon's own suppression character, so a
    // sniper round unsettles far more than its damage alone would suggest.
    public float GetSuppressionWeightAt(float distance)
    {
        return GetPotentialDamageAt(distance) * suppressionMultiplier;
    }

    // Base (pre-hitbox-multiplier) damage at a given shot distance. Headshots
    // ignore falloff and always use the close-range value, so they stay
    // rewarding at any range.
    private float GetBaseDamageAtDistance(float distance, bool isHeadshot)
    {
        if (weaponData == null)
        {
            return 0f;
        }

        if (isHeadshot || damageMid < 0f)
        {
            return weaponData.damage;
        }

        if (distance <= closeRangeEnd)
        {
            return weaponData.damage;
        }

        if (distance <= midRangeEnd)
        {
            return damageMid;
        }

        return damageLong >= 0f ? damageLong : damageMid;
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

        // Semi-auto and automatic weapons reuse this coroutine purely as the
        // fire interval; only real bolt actions get the bolt sound.
        if (fireMode == WeaponFireMode.BoltAction)
        {
            PlayBoltCycleSound();
        }

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

        if (shellByShellReload)
        {
            // Tube-fed: one shell per reloadTime until full or dry.
            while (currentAmmo < weaponData.clipSize && reserveAmmo > 0)
            {
                yield return new WaitForSeconds(weaponData.reloadTime);

                currentAmmo++;
                reserveAmmo--;
            }
        }
        else
        {
            yield return new WaitForSeconds(weaponData.reloadTime);

            int ammoNeeded = weaponData.clipSize - currentAmmo;
            int ammoToLoad = Mathf.Min(ammoNeeded, reserveAmmo);

            currentAmmo += ammoToLoad;
            reserveAmmo -= ammoToLoad;
        }

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

        // Attacking forfeits spawn protection.
        PlayerNetworkHealth shooterHealth = GetComponent<PlayerNetworkHealth>();

        if (shooterHealth != null)
        {
            shooterHealth.ServerCancelSpawnProtection();
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

        // Rounds cracking past an enemy suppress them, hit or miss — a hit
        // just counts for more.
        PlayerSuppression.ServerApplyShotSuppression(this, shotStartPoint, shotEndPoint,
            shooterTeam != null ? shooterTeam.team : Team.Neutral, OwnerId,
            didHit ? targetObject : null);

        if (!didHit || targetObject == null || targetObject == NetworkObject)
        {
            return;
        }

        float clampedMultiplier = Mathf.Clamp(damageMultiplier, 0f, headshotDamageMultiplier * Mathf.Max(1, pelletsPerShot));

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

        float finalDamage = GetBaseDamageAtDistance(Vector3.Distance(shotStartPoint, shotEndPoint), isHeadshot) * damageMultiplier;
        bool killedTarget = false;

        PlayerNetworkHealth playerHealth = targetObject.GetComponent<PlayerNetworkHealth>();

        if (playerHealth != null)
        {
            if (playerHealth.IsDead)
            {
                yield break;
            }

            killedTarget = playerHealth.ServerTakeDamage(finalDamage, shotStartPoint);
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

        // Death effect on the man who took it: his last moments white out.
        if (killedTarget && isHeadshot)
        {
            PlayerSuppression.ServerApplyHeadshotKillEffect(targetObject);
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
        if (weaponData.boltCycleSound == null || fireMode != WeaponFireMode.BoltAction)
        {
            yield break;
        }

        yield return new WaitForSeconds(Mathf.Max(0f, weaponData.boltCycleSoundDelay));

        AudioSource.PlayClipAtPoint(weaponData.boltCycleSound, transform.position + Vector3.up * 1.5f, weaponData.boltCycleSoundVolume);
    }

    // ---- Sight alignment tuning (editor helper) ----
    // Hold LEFT ALT while aiming: arrows move the weapon (up/down = height,
    // left/right = sideways), [ ] = closer/further, - = shrink, = = grow.
    // The on-screen readout shows the values to copy into the WeaponVisuals
    // asset (aim position + model scale).
    private bool aimTuningUsed;

    private void HandleAimPoseTuning()
    {
        if (!isAiming || Keyboard.current == null || !Keyboard.current.leftAltKey.isPressed)
        {
            return;
        }

        float moveStep = 0.05f * Time.deltaTime;
        float scaleStep = 0.3f * Time.deltaTime;
        Vector3 delta = Vector3.zero;
        float scaleDelta = 0f;

        if (Keyboard.current.upArrowKey.isPressed) delta.y += moveStep;
        if (Keyboard.current.downArrowKey.isPressed) delta.y -= moveStep;
        if (Keyboard.current.rightArrowKey.isPressed) delta.x += moveStep;
        if (Keyboard.current.leftArrowKey.isPressed) delta.x -= moveStep;
        if (Keyboard.current.rightBracketKey.isPressed) delta.z += moveStep;
        if (Keyboard.current.leftBracketKey.isPressed) delta.z -= moveStep;
        if (Keyboard.current.equalsKey.isPressed) scaleDelta += scaleStep;
        if (Keyboard.current.minusKey.isPressed) scaleDelta -= scaleStep;

        if (delta == Vector3.zero && scaleDelta == 0f)
        {
            return;
        }

        aimTuningUsed = true;
        aimWeaponLocalPosition += delta;

        if (scaleDelta != 0f && weaponHolder != null)
        {
            Transform model = weaponHolder.Find("WeaponViewModel");

            if (model != null)
            {
                float scale = Mathf.Max(0.05f, model.localScale.x + scaleDelta);
                model.localScale = Vector3.one * scale;
            }
        }
    }

    private void OnGUI()
    {
        if (IsOwner && aimTuningUsed && Keyboard.current != null && Keyboard.current.leftAltKey.isPressed)
        {
            string scaleText = "";

            if (weaponHolder != null)
            {
                Transform model = weaponHolder.Find("WeaponViewModel");

                if (model != null)
                {
                    scaleText = "   Model Scale: " + model.localScale.x.ToString("0.00");
                }
            }

            GUI.Label(new Rect(10f, Screen.height - 60f, 900f, 25f),
                "AIM TUNE  Position: (" + aimWeaponLocalPosition.x.ToString("0.000") + ", "
                + aimWeaponLocalPosition.y.ToString("0.000") + ", "
                + aimWeaponLocalPosition.z.ToString("0.000") + ")" + scaleText
                + "   → copy into WeaponVisuals asset");
        }

        if (!IsOwner || !requiresDeploySetup)
        {
            return;
        }

        string message = null;

        if (isAiming && !IsDeployed)
        {
            message = "DEPLOYING... " + Mathf.CeilToInt((deploySetupTime - deployTimer) * 10f) / 10f + "s";
        }
        else if (isAiming && DeployBonusActive)
        {
            message = "DEPLOYED";
        }

        if (message == null)
        {
            return;
        }

        GUIStyle style = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 16,
            fontStyle = FontStyle.Bold
        };
        style.normal.textColor = new Color(1f, 0.9f, 0.5f);

        GUI.Label(new Rect(0f, Screen.height * 0.6f, Screen.width, 28f), message, style);
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

        if (shellByShellReload)
        {
            while (serverCurrentAmmo < weaponData.clipSize && serverReserveAmmo > 0)
            {
                yield return new WaitForSeconds(weaponData.reloadTime);

                serverCurrentAmmo++;
                serverReserveAmmo--;
            }
        }
        else
        {
            yield return new WaitForSeconds(weaponData.reloadTime);

            int ammoNeeded = weaponData.clipSize - serverCurrentAmmo;
            int ammoToLoad = Mathf.Min(ammoNeeded, serverReserveAmmo);

            serverCurrentAmmo += ammoToLoad;
            serverReserveAmmo -= ammoToLoad;
        }

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