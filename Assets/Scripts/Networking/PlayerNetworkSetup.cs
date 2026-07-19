using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

public class PlayerNetworkSetup : NetworkBehaviour
{
    [Header("Team Assignment")]
    public Color alliedBodyColor = new Color(0.25f, 0.4f, 0.85f);
    public Color centralBodyColor = new Color(0.8f, 0.25f, 0.2f);

    [Header("Soldier Model")]
    [Tooltip("Clone the soldier rig (with hitboxes) from a scene dummy of the matching team instead of showing the capsule.")]
    public bool useSoldierModelFromDummies = true;

    [Tooltip("Idle/Run controller applied to the cloned rig's Animator.")]
    public RuntimeAnimatorController soldierAnimatorController;

    [Tooltip("Humanoid avatar of the soldier rig (Characters.fbx). Used when the cloned rig has no Animator of its own.")]
    public Avatar soldierAvatar;

    private bool soldierModelCreated;

    // Server-assigned team, alternated per spawned player.
    private readonly SyncVar<Team> syncTeam = new SyncVar<Team>();

    // Server-assigned class, chosen on the class selection screen.
    private readonly SyncVar<PlayerClass> syncClass = new SyncVar<PlayerClass>();

    // Chosen primary weapon (only Assault has more than one option).
    private readonly SyncVar<WeaponId> syncWeapon = new SyncVar<WeaponId>();

    [Header("Scout Flare Spotting")]
    public float flareCooldown = 10f;
    public float flareRange = 80f;
    public float spotRadius = 20f;
    public float spotDuration = 8f;

    // Spot replication: the server bumps the pulse and sets the spotting team;
    // every client restarts its local countdown on the pulse change.
    private readonly SyncVar<int> syncSpotPulse = new SyncVar<int>();
    private readonly SyncVar<Team> syncSpottedByTeam = new SyncVar<Team>();

    private float spottedRemaining;
    private float lastFlareTime = -999f;
    private float serverLastFlareTime = -999f;

    // The local player's first-person camera, for projecting spot markers.
    public static Camera LocalPlayerCamera { get; private set; }

    private static int serverTeamAssignCounter;

    // Set by ClassSpawnManager on the server instance just before Spawn().
    [HideInInspector] public Team pendingTeam = Team.Neutral;
    [HideInInspector] public PlayerClass pendingClass = PlayerClass.Assault;
    [HideInInspector] public WeaponId pendingWeapon = WeaponId.BoltAction;

    private bool classApplied;

    public Team AssignedTeam => syncTeam.Value;
    public PlayerClass AssignedClass => syncClass.Value;

    [Header("Owner Only")]
    [Tooltip("First-person camera root. Also holds the AudioListener and weapon view-model. Disabled on non-owned players.")]
    public GameObject cameraRoot;

    [Tooltip("Components that read local input. Disabled on non-owned players.")]
    public Behaviour[] ownerOnlyBehaviours;

    [Header("Remote Only")]
    [Tooltip("Visible body other players see. Hidden on the owned player so it never blocks the first-person view.")]
    public GameObject remoteBody;

    private void Awake()
    {
        syncSpotPulse.OnChange += OnSpotPulseChanged;
    }

    private void OnSpotPulseChanged(int previous, int next, bool asServer)
    {
        if (next > 0)
        {
            spottedRemaining = spotDuration;
        }
    }

    private void Update()
    {
        if (spottedRemaining > 0f)
        {
            spottedRemaining -= Time.deltaTime;
        }

        if (!IsOwner)
        {
            return;
        }

        // Scout flare gun: G to fire, spots enemies around the landing point.
        if (PlayerClasses.Get(AssignedClass).canSpot
            && UnityEngine.InputSystem.Keyboard.current != null
            && UnityEngine.InputSystem.Keyboard.current.gKey.wasPressedThisFrame
            && Time.time - lastFlareTime >= flareCooldown)
        {
            Camera aimCamera = cameraRoot != null ? cameraRoot.GetComponent<Camera>() : null;

            if (aimCamera != null)
            {
                lastFlareTime = Time.time;
                Ray aimRay = aimCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
                RequestFlare(aimRay.origin, aimRay.direction);
            }
        }
    }

    [ServerRpc]
    private void RequestFlare(Vector3 origin, Vector3 direction)
    {
        if (!PlayerClasses.Get(AssignedClass).canSpot)
        {
            return;
        }

        if (Time.time - serverLastFlareTime < flareCooldown * 0.9f)
        {
            return;
        }

        serverLastFlareTime = Time.time;

        Vector3 landing = Physics.Raycast(origin, direction, out RaycastHit hit, flareRange,
            Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
            ? hit.point
            : origin + direction.normalized * flareRange;

        ObserversFlareEffect(landing);

        // Spot every living enemy near the flare.
        foreach (PlayerNetworkSetup target in FindObjectsByType<PlayerNetworkSetup>(FindObjectsSortMode.None))
        {
            if (target == this || target.AssignedTeam == AssignedTeam)
            {
                continue;
            }

            PlayerNetworkHealth targetHealth = target.GetComponent<PlayerNetworkHealth>();

            if (targetHealth != null && targetHealth.IsDead)
            {
                continue;
            }

            if (Vector3.Distance(target.transform.position, landing) <= spotRadius)
            {
                target.ServerApplySpotted(AssignedTeam);
            }
        }
    }

    // Server: mark this player as spotted for the given team.
    public void ServerApplySpotted(Team spottingTeam)
    {
        syncSpottedByTeam.Value = spottingTeam;
        syncSpotPulse.Value = syncSpotPulse.Value + 1;
        spottedRemaining = spotDuration;
    }

    [ObserversRpc]
    private void ObserversFlareEffect(Vector3 landing)
    {
        GameObject flare = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        flare.name = "FlareEffect";
        Destroy(flare.GetComponent<Collider>());
        flare.transform.position = landing + Vector3.up * 0.4f;
        flare.transform.localScale = Vector3.one * 0.35f;

        Renderer flareRenderer = flare.GetComponent<Renderer>();
        flareRenderer.material.color = Color.red;

        Light flareLight = flare.AddComponent<Light>();
        flareLight.color = new Color(1f, 0.25f, 0.15f);
        flareLight.intensity = 4f;
        flareLight.range = 18f;

        Destroy(flare, spotDuration);
    }

    private void OnGUI()
    {
        DrawSpottedMarker();
        DrawFlareCooldown();
    }

    // Red marker drawn over this player for enemies of the spotting team —
    // screen-space, so it shows through walls.
    private void DrawSpottedMarker()
    {
        if (spottedRemaining <= 0f || IsOwner)
        {
            return;
        }

        if (ClassSelectHud.LastKnownTeam != syncSpottedByTeam.Value)
        {
            return;
        }

        PlayerNetworkHealth targetHealth = GetComponent<PlayerNetworkHealth>();

        if (targetHealth != null && targetHealth.IsDead)
        {
            return;
        }

        Camera viewer = LocalPlayerCamera;

        if (viewer == null || !viewer.isActiveAndEnabled)
        {
            return;
        }

        Vector3 screenPoint = viewer.WorldToScreenPoint(transform.position + Vector3.up * 2.2f);

        if (screenPoint.z <= 0f)
        {
            return;
        }

        GUIStyle markerStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 22,
            fontStyle = FontStyle.Bold
        };
        markerStyle.normal.textColor = new Color(1f, 0.15f, 0.1f);

        float x = screenPoint.x;
        float y = Screen.height - screenPoint.y;

        GUI.Label(new Rect(x - 40f, y - 16f, 80f, 24f), "▼", markerStyle);

        GUIStyle distanceStyle = new GUIStyle(markerStyle) { fontSize = 11 };
        float distance = Vector3.Distance(viewer.transform.position, transform.position);
        GUI.Label(new Rect(x - 40f, y + 6f, 80f, 16f), Mathf.RoundToInt(distance) + "m", distanceStyle);
    }

    private void DrawFlareCooldown()
    {
        if (!IsOwner || !PlayerClasses.Get(AssignedClass).canSpot)
        {
            return;
        }

        float remaining = flareCooldown - (Time.time - lastFlareTime);

        GUIStyle style = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            alignment = TextAnchor.LowerRight
        };
        style.normal.textColor = remaining <= 0f
            ? new Color(0.6f, 1f, 0.6f, 0.85f)
            : new Color(1f, 1f, 1f, 0.6f);

        string text = remaining <= 0f
            ? "FLARE GUN (G): READY"
            : "FLARE GUN (G): " + Mathf.CeilToInt(remaining) + "s";

        GUI.Label(new Rect(0f, Screen.height - 58f, Screen.width - 16f, 44f), text, style);
    }

    public override void OnStartServer()
    {
        base.OnStartServer();

        Team team = pendingTeam;

        if (team == Team.Neutral)
        {
            // Fallback for spawns that bypassed ClassSpawnManager.
            team = serverTeamAssignCounter % 2 == 0 ? Team.AlliedPowers : Team.CentralPowers;
            serverTeamAssignCounter++;
        }

        syncTeam.Value = team;
        syncClass.Value = pendingClass;
        syncWeapon.Value = pendingWeapon;

        ApplyTeam(team);
        ApplyClass();
    }

    public override void OnStartClient()
    {
        base.OnStartClient();

        ApplyTeam(syncTeam.Value);
        ApplyClass();

        // Footsteps for everyone, driven from replicated movement.
        if (GetComponent<FootstepAudio>() == null)
        {
            gameObject.AddComponent<FootstepAudio>();
        }

        if (IsOwner)
        {
            // Remember the team so the spawn selector on the next death knows
            // which objectives are friendly.
            ClassSelectHud.LastKnownTeam = syncTeam.Value;

            if (remoteBody != null)
            {
                remoteBody.SetActive(false);
            }

            WireLocalPlayerUI();
            return;
        }

        if (cameraRoot != null)
        {
            cameraRoot.SetActive(false);
        }

        foreach (Behaviour behaviour in ownerOnlyBehaviours)
        {
            if (behaviour != null)
            {
                behaviour.enabled = false;
            }
        }
    }

    private void ApplyTeam(Team team)
    {
        PlayerTeam localTeam = GetComponent<PlayerTeam>();

        if (localTeam != null)
        {
            localTeam.team = team;
        }

        if (remoteBody == null)
        {
            return;
        }

        if (useSoldierModelFromDummies)
        {
            EnsureSoldierModel(team);
        }

        if (!soldierModelCreated)
        {
            // Capsule fallback: tint only the capsule's own renderer, never a
            // cloned soldier model.
            Renderer bodyRenderer = remoteBody.GetComponent<Renderer>();

            if (bodyRenderer != null)
            {
                bodyRenderer.material.color = team == Team.AlliedPowers ? alliedBodyColor : centralBodyColor;
            }
        }
    }

    // The scene dummies already carry fully set up soldier rigs from the WW1
    // asset pack, including bone-attached hitboxes. Clone the rig of a dummy
    // on the matching team under RemoteBody so remote players show the right
    // uniform and take hitbox-multiplied damage.
    private void EnsureSoldierModel(Team team)
    {
        if (soldierModelCreated)
        {
            return;
        }

        HealthComponent dummySource = FindDummySource(team);

        if (dummySource == null)
        {
            return;
        }

        // Clone the WHOLE dummy so the skinned meshes keep their bone
        // references (skeleton and LOD meshes are separate siblings; cloning
        // only one of them leaves the copy skinned to the original dummy).
        GameObject clone = Instantiate(dummySource.gameObject, remoteBody.transform);
        clone.name = "SoldierModel";
        clone.SetActive(true);

        // Strip the dummy's gameplay from the copy — the player's networked
        // components own all of that.
        Destroy(clone.GetComponent<HealthComponent>());

        PlayerTeam cloneTeam = clone.GetComponent<PlayerTeam>();

        if (cloneTeam != null)
        {
            Destroy(cloneTeam);
        }

        foreach (DownedWorldMarker marker in clone.GetComponentsInChildren<DownedWorldMarker>(true))
        {
            Destroy(marker.gameObject);
        }

        foreach (HelmetPopOff helmet in clone.GetComponentsInChildren<HelmetPopOff>(true))
        {
            helmet.enabled = false;
        }

        // Any rigidbodies in the copy (e.g. helmets) would fall or fight the
        // player's movement; the hitbox colliders instead join RemoteBody's
        // kinematic rigidbody as a compound.
        foreach (Rigidbody body in clone.GetComponentsInChildren<Rigidbody>(true))
        {
            Destroy(body);
        }

        // RemoteBody is scaled and offset for the capsule; counteract both so
        // the soldier stands at the player's feet with its authored scale.
        Transform cloneTransform = clone.transform;
        Vector3 parentScale = remoteBody.transform.lossyScale;
        Vector3 sourceScale = dummySource.transform.lossyScale;
        cloneTransform.localScale = new Vector3(
            parentScale.x == 0f ? 1f : sourceScale.x / parentScale.x,
            parentScale.y == 0f ? 1f : sourceScale.y / parentScale.y,
            parentScale.z == 0f ? 1f : sourceScale.z / parentScale.z);
        cloneTransform.rotation = transform.rotation;
        cloneTransform.position = transform.position;

        // The dummy's pivot is not at its feet; measure the renderers and
        // raise the clone so its lowest point sits at the player's feet.
        Bounds modelBounds = new Bounds(cloneTransform.position, Vector3.zero);
        bool hasBounds = false;

        foreach (Renderer modelRenderer in clone.GetComponentsInChildren<Renderer>(false))
        {
            if (!hasBounds)
            {
                modelBounds = modelRenderer.bounds;
                hasBounds = true;
            }
            else
            {
                modelBounds.Encapsulate(modelRenderer.bounds);
            }
        }

        if (hasBounds)
        {
            float sink = transform.position.y - modelBounds.min.y;
            cloneTransform.position += Vector3.up * sink;
        }

        // Animate the rig: assign the idle/run controller and a driver that
        // reads the player's actual movement.
        Animator rigAnimator = clone.GetComponentInChildren<Animator>(true);

        if (rigAnimator == null)
        {
            // The dummy rig ships without an Animator; add one on the rig
            // root (the subtree that owns the bones) with the soldier avatar.
            SkinnedMeshRenderer rigMesh = clone.GetComponentInChildren<SkinnedMeshRenderer>(true);

            if (rigMesh != null && soldierAvatar != null)
            {
                Transform rigRoot = rigMesh.rootBone != null ? rigMesh.rootBone : rigMesh.transform;

                while (rigRoot.parent != null && rigRoot.parent != clone.transform)
                {
                    rigRoot = rigRoot.parent;
                }

                rigAnimator = rigRoot.gameObject.AddComponent<Animator>();
                rigAnimator.avatar = soldierAvatar;
            }
        }

        if (rigAnimator != null && soldierAnimatorController != null)
        {
            rigAnimator.runtimeAnimatorController = soldierAnimatorController;
            rigAnimator.applyRootMotion = false;
            rigAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            SoldierAnimationDriver driver = clone.AddComponent<SoldierAnimationDriver>();
            driver.animator = rigAnimator;
            driver.movementRoot = transform;
        }
        else
        {
            Debug.LogWarning("[PlayerNetworkSetup] Soldier animation not applied. Animator: "
                + (rigAnimator != null) + ", controller: " + (soldierAnimatorController != null)
                + ", avatar: " + (soldierAvatar != null));
        }

        // The rig's hitboxes take over bullet, revive, and zone-trigger
        // duties; hide and disable the capsule.
        MeshRenderer capsuleRenderer = remoteBody.GetComponent<MeshRenderer>();

        if (capsuleRenderer != null)
        {
            capsuleRenderer.enabled = false;
        }

        CapsuleCollider capsuleCollider = remoteBody.GetComponent<CapsuleCollider>();

        if (capsuleCollider != null)
        {
            capsuleCollider.enabled = false;
        }

        soldierModelCreated = true;
    }

    private HealthComponent FindDummySource(Team team)
    {
        foreach (HealthComponent dummy in FindObjectsByType<HealthComponent>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            // Never clone from another player.
            if (dummy.GetComponentInParent<PlayerNetworkSetup>() != null)
            {
                continue;
            }

            PlayerTeam dummyTeam = dummy.GetComponentInParent<PlayerTeam>();

            if (dummyTeam == null || dummyTeam.team != team)
            {
                continue;
            }

            if (dummy.GetComponentInChildren<SkinnedMeshRenderer>(true) == null)
            {
                continue;
            }

            return dummy;
        }

        return null;
    }

    // Applies the chosen class's stats to this instance. Runs once per
    // instance (the host reaches it from both server and client callbacks).
    private void ApplyClass()
    {
        if (classApplied)
        {
            return;
        }

        classApplied = true;

        PlayerClassDefinition definition = PlayerClasses.Get(syncClass.Value);

        PlayerNetworkHealth health = GetComponent<PlayerNetworkHealth>();

        if (health != null)
        {
            health.maxHealth = definition.maxHealth;
            health.ServerResetHealthToMax();
        }

        BoltActionRifle rifle = GetComponent<BoltActionRifle>();

        if (rifle != null)
        {
            // Reserve ammo is weapon-defined inside the profile.
            rifle.ApplyWeaponProfile(WeaponProfiles.Get(syncWeapon.Value));
        }

        PlayerController controller = GetComponent<PlayerController>();

        if (controller != null && !Mathf.Approximately(definition.moveSpeedMultiplier, 1f))
        {
            controller.walkSpeed *= definition.moveSpeedMultiplier;
            controller.sprintSpeed *= definition.moveSpeedMultiplier;
        }
    }

    public void MoveOwnerToTeamSpawn()
    {
        if (!IsOwner)
        {
            return;
        }

        PlayerTeam localTeam = GetComponent<PlayerTeam>();

        if (localTeam == null)
        {
            return;
        }

        Transform spawnPoint = TeamSpawnArea.GetSpawnPoint(localTeam.team);

        if (spawnPoint == null)
        {
            return;
        }

        // The CharacterController caches its position; it must be disabled
        // for a teleport to stick.
        CharacterController controller = GetComponent<CharacterController>();

        if (controller != null)
        {
            controller.enabled = false;
        }

        transform.position = spawnPoint.position;
        transform.rotation = Quaternion.Euler(0f, spawnPoint.eulerAngles.y, 0f);

        if (controller != null)
        {
            controller.enabled = true;
        }
    }

    // Scene UI kept Inspector references to the pre-networking scene Player.
    // A spawned prefab cannot carry those, so the owned player assigns itself
    // into the scene UI when it spawns. Replaced per-phase as UI becomes networked.
    private void WireLocalPlayerUI()
    {
        BoltActionRifle rifle = GetComponent<BoltActionRifle>();
        PlayerTeam team = GetComponent<PlayerTeam>();
        Camera playerCamera = cameraRoot != null ? cameraRoot.GetComponent<Camera>() : null;

        LocalPlayerCamera = playerCamera;

        // Engineers get build controls (keys 4-7).
        if (PlayerClasses.Get(AssignedClass).buildDigMultiplier > 1f
            && GetComponent<FortificationBuilder>() == null)
        {
            FortificationBuilder builder = gameObject.AddComponent<FortificationBuilder>();
            builder.playerCamera = playerCamera;
        }

        if (rifle != null)
        {
            rifle.hitMarkerUI = FindFirstObjectByType<HitMarkerUI>(FindObjectsInactive.Include);

            AmmoUI ammoUI = FindFirstObjectByType<AmmoUI>(FindObjectsInactive.Include);
            if (ammoUI != null)
            {
                ammoUI.rifle = rifle;
            }
        }

        CrosshairUI crosshairUI = FindFirstObjectByType<CrosshairUI>(FindObjectsInactive.Include);
        if (crosshairUI != null)
        {
            crosshairUI.rifle = rifle;
            crosshairUI.playerCamera = playerCamera;
        }

        ReviveInteractor reviveInteractor = FindFirstObjectByType<ReviveInteractor>(FindObjectsInactive.Include);
        if (reviveInteractor != null)
        {
            reviveInteractor.playerCamera = playerCamera;
            reviveInteractor.localPlayerTeam = team;
        }

        foreach (ObjectiveUI ui in FindObjectsByType<ObjectiveUI>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            ui.localPlayerTeam = team;
        }

        foreach (ObjectiveHUDLayout ui in FindObjectsByType<ObjectiveHUDLayout>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            ui.localPlayerTeam = team;
        }

        foreach (ObjectiveMessageUI ui in FindObjectsByType<ObjectiveMessageUI>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            ui.localPlayerTeam = team;
        }

        foreach (TeamTicketUI ui in FindObjectsByType<TeamTicketUI>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            ui.localPlayerTeam = team;
        }

        foreach (DownedWorldMarker marker in FindObjectsByType<DownedWorldMarker>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            marker.localPlayerTeam = team;
            marker.playerCamera = playerCamera;
        }
    }
}
