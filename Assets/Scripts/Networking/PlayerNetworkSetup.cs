using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

public class PlayerNetworkSetup : NetworkBehaviour
{
    [Header("Team Assignment")]
    public Color alliedBodyColor = new Color(0.25f, 0.4f, 0.85f);
    public Color centralBodyColor = new Color(0.8f, 0.25f, 0.2f);

    // Server-assigned team, alternated per spawned player.
    private readonly SyncVar<Team> syncTeam = new SyncVar<Team>();

    private static int serverTeamAssignCounter;

    public Team AssignedTeam => syncTeam.Value;

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

        Team team = serverTeamAssignCounter % 2 == 0 ? Team.AlliedPowers : Team.CentralPowers;
        serverTeamAssignCounter++;

        syncTeam.Value = team;
        ApplyTeam(team);
    }

    public override void OnStartClient()
    {
        base.OnStartClient();

        ApplyTeam(syncTeam.Value);

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

        if (remoteBody != null)
        {
            Renderer bodyRenderer = remoteBody.GetComponentInChildren<Renderer>(true);

            if (bodyRenderer != null)
            {
                bodyRenderer.material.color = team == Team.AlliedPowers ? alliedBodyColor : centralBodyColor;
            }
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
