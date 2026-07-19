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

    private bool soldierModelCreated;

    // Server-assigned team, alternated per spawned player.
    private readonly SyncVar<Team> syncTeam = new SyncVar<Team>();

    // Server-assigned class, chosen on the class selection screen.
    private readonly SyncVar<PlayerClass> syncClass = new SyncVar<PlayerClass>();

    private static int serverTeamAssignCounter;

    // Set by ClassSpawnManager on the server instance just before Spawn().
    [HideInInspector] public Team pendingTeam = Team.Neutral;
    [HideInInspector] public PlayerClass pendingClass = PlayerClass.Assault;

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

        ApplyTeam(team);
        ApplyClass();
    }

    public override void OnStartClient()
    {
        base.OnStartClient();

        ApplyTeam(syncTeam.Value);
        ApplyClass();

        if (IsOwner)
        {
            MoveOwnerToTeamSpawn();

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
            rifle.SetClassReserveAmmo(definition.reserveAmmo);
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
