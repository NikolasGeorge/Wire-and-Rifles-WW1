using FishNet.Connection;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;
using UnityEngine.InputSystem;

public enum PlayerLifeState : byte
{
    Alive = 0,
    Downed = 1,
    Dead = 2
}

// Server-authoritative health, downed, bleedout, give-up, and respawn for
// networked players. Practice dummies keep the original HealthComponent.
public class PlayerNetworkHealth : NetworkBehaviour
{
    [Header("Health")]
    public float maxHealth = 100f;

    [Header("Downed")]
    public bool useDownedState = true;
    public float bleedOutTime = 30f;
    public float giveUpDelay = 5f;

    [Header("Revive")]
    public float reviveHealth = 50f;
    [Tooltip("Server-side validation range for a revive request. Keep a little above the interactor's revive distance.")]
    public float maxReviveDistance = 4.5f;

    [Header("Respawn")]
    public float respawnDelay = 3f;

    [Header("Downed Pose")]
    public Vector3 downedBodyLocalPosition = new Vector3(0f, 0.35f, 0f);
    public Vector3 downedBodyLocalEulerAngles = new Vector3(90f, 0f, 0f);
    public float downedCameraHeight = 0.5f;

    private readonly SyncVar<float> syncHealth = new SyncVar<float>();
    private readonly SyncVar<PlayerLifeState> syncState = new SyncVar<PlayerLifeState>();
    private readonly SyncVar<float> syncBleedOut = new SyncVar<float>();

    private PlayerNetworkSetup setup;
    private PlayerController playerController;
    private BoltActionRifle rifle;
    private PlayerTeam playerTeam;

    private Vector3 standingBodyLocalPosition;
    private Quaternion standingBodyLocalRotation;
    private Vector3 standingCameraLocalPosition;

    private float serverBleedOutRemaining;
    private float serverRespawnTimer;

    public PlayerLifeState State => syncState.Value;
    public bool IsDowned => State == PlayerLifeState.Downed;
    public bool IsDead => State == PlayerLifeState.Dead;
    public float CurrentHealth => syncHealth.Value;
    public float BleedOutRemaining => syncBleedOut.Value;
    public float BleedOutProgress01 => bleedOutTime <= 0f ? 0f : Mathf.Clamp01(syncBleedOut.Value / bleedOutTime);
    public float DownedElapsed => IsDowned ? Mathf.Max(0f, bleedOutTime - syncBleedOut.Value) : 0f;

    private void Awake()
    {
        setup = GetComponent<PlayerNetworkSetup>();
        playerController = GetComponent<PlayerController>();
        rifle = GetComponent<BoltActionRifle>();
        playerTeam = GetComponent<PlayerTeam>();

        if (setup != null && setup.remoteBody != null)
        {
            standingBodyLocalPosition = setup.remoteBody.transform.localPosition;
            standingBodyLocalRotation = setup.remoteBody.transform.localRotation;
        }

        if (setup != null && setup.cameraRoot != null)
        {
            standingCameraLocalPosition = setup.cameraRoot.transform.localPosition;
        }

        syncState.OnChange += OnStateChanged;

        EnsureDownedMarker();
    }

    // The scene's practice dummies already carry a fully built DownedWorldMarker
    // hierarchy. Clone one under this player at runtime and rebind it, so the
    // prefab needs no hand-maintained marker copy.
    private void EnsureDownedMarker()
    {
        if (GetComponentInChildren<DownedWorldMarker>(true) != null)
        {
            return;
        }

        DownedWorldMarker template = null;

        foreach (DownedWorldMarker candidate in FindObjectsByType<DownedWorldMarker>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (candidate.GetComponentInParent<PlayerNetworkHealth>() == null)
            {
                template = candidate;
                break;
            }
        }

        if (template == null)
        {
            return;
        }

        DownedWorldMarker marker = Instantiate(template.gameObject, transform).GetComponent<DownedWorldMarker>();
        marker.name = "DownedWorldMarker";
        marker.health = null;
        marker.playerHealth = this;
        marker.targetTeam = playerTeam;
        marker.localPlayerTeam = null;
        marker.playerCamera = null;
        marker.markerAnchor = null;
        marker.anchorOffset = new Vector3(0f, 0.7f, 0f);
    }

    public override void OnStartServer()
    {
        base.OnStartServer();

        syncHealth.Value = maxHealth;
        syncState.Value = PlayerLifeState.Alive;
    }

    private void Update()
    {
        if (IsServerInitialized)
        {
            ServerUpdate();
        }

        if (IsOwner)
        {
            OwnerUpdate();
        }
    }

    // ---- Server ----

    private void ServerUpdate()
    {
        if (syncState.Value == PlayerLifeState.Downed)
        {
            serverBleedOutRemaining -= Time.deltaTime;
            syncBleedOut.Value = Mathf.Max(0f, serverBleedOutRemaining);

            if (serverBleedOutRemaining <= 0f)
            {
                ServerFullDie("Bleedout");
            }
        }
        else if (syncState.Value == PlayerLifeState.Dead)
        {
            serverRespawnTimer -= Time.deltaTime;

            if (serverRespawnTimer <= 0f)
            {
                ServerRespawn();
            }
        }
    }

    // Returns true when this damage fully killed the player.
    public bool ServerTakeDamage(float damage)
    {
        if (!IsServerInitialized || damage <= 0f)
        {
            return false;
        }

        if (syncState.Value == PlayerLifeState.Dead)
        {
            return false;
        }

        if (syncState.Value == PlayerLifeState.Downed)
        {
            ServerFullDie("Finished while downed");
            return true;
        }

        float newHealth = Mathf.Max(0f, syncHealth.Value - damage);
        syncHealth.Value = newHealth;

        if (newHealth > 0f)
        {
            return false;
        }

        if (useDownedState)
        {
            ServerEnterDowned();
            return false;
        }

        ServerFullDie("Killed");
        return true;
    }

    private void ServerEnterDowned()
    {
        serverBleedOutRemaining = bleedOutTime;
        syncBleedOut.Value = bleedOutTime;
        syncState.Value = PlayerLifeState.Downed;

        SetTeamDownedFlag(true);
    }

    private void ServerFullDie(string reason)
    {
        if (syncState.Value == PlayerLifeState.Dead)
        {
            return;
        }

        syncState.Value = PlayerLifeState.Dead;
        syncHealth.Value = 0f;
        syncBleedOut.Value = 0f;
        serverRespawnTimer = respawnDelay;

        SetTeamDownedFlag(false);

        Debug.Log(gameObject.name + " died. Reason: " + reason);

        if (TeamTicketManager.Instance != null && playerTeam != null)
        {
            TeamTicketManager.Instance.ConsumeTickets(playerTeam.team, 1, gameObject.name + " death");
        }
    }

    private void ServerRespawn()
    {
        syncHealth.Value = maxHealth;
        syncBleedOut.Value = 0f;
        syncState.Value = PlayerLifeState.Alive;
    }

    // Client-side eligibility check used by ReviveInteractor before it shows
    // the prompt. The server re-validates everything in ServerRequestRevive.
    public bool CanBeRevivedBy(PlayerTeam reviverTeam)
    {
        if (!IsDowned || IsOwner)
        {
            return false;
        }

        if (reviverTeam == null || reviverTeam.team == Team.Neutral)
        {
            return false;
        }

        return playerTeam != null && playerTeam.team == reviverTeam.team;
    }

    // Called on the reviver's client after their hold-E completes. This runs
    // on the DOWNED player's object, so ownership cannot be required.
    public void RequestRevive()
    {
        ServerRequestRevive();
    }

    [ServerRpc(RequireOwnership = false)]
    private void ServerRequestRevive(NetworkConnection sender = null)
    {
        if (syncState.Value != PlayerLifeState.Downed)
        {
            return;
        }

        if (sender == null || sender.FirstObject == null)
        {
            return;
        }

        NetworkObject reviverObject = sender.FirstObject;

        PlayerTeam reviverTeam = reviverObject.GetComponent<PlayerTeam>();

        if (reviverTeam == null || playerTeam == null || reviverTeam.team != playerTeam.team)
        {
            return;
        }

        PlayerNetworkHealth reviverHealth = reviverObject.GetComponent<PlayerNetworkHealth>();

        if (reviverHealth != null && reviverHealth.State != PlayerLifeState.Alive)
        {
            return;
        }

        float distance = Vector3.Distance(reviverObject.transform.position, transform.position);

        if (distance > maxReviveDistance)
        {
            return;
        }

        ServerRevive();
    }

    private void ServerRevive()
    {
        serverBleedOutRemaining = 0f;
        syncBleedOut.Value = 0f;
        syncHealth.Value = Mathf.Clamp(reviveHealth, 1f, maxHealth);
        syncState.Value = PlayerLifeState.Alive;

        SetTeamDownedFlag(false);

        Debug.Log(gameObject.name + " revived with " + syncHealth.Value + " HP.");
    }

    [ServerRpc]
    private void ServerGiveUp()
    {
        if (syncState.Value != PlayerLifeState.Downed)
        {
            return;
        }

        float downedElapsed = bleedOutTime - serverBleedOutRemaining;

        if (downedElapsed < giveUpDelay * 0.9f)
        {
            return;
        }

        ServerFullDie("Gave up");
    }

    private void SetTeamDownedFlag(bool downed)
    {
        if (playerTeam != null)
        {
            playerTeam.isDowned = downed;
        }
    }

    // ---- Owner ----

    private void OwnerUpdate()
    {
        if (!IsDowned || Keyboard.current == null)
        {
            return;
        }

        if (Keyboard.current.rKey.wasPressedThisFrame && DownedElapsed >= giveUpDelay)
        {
            ServerGiveUp();
        }
    }

    private void OnGUI()
    {
        if (!IsOwner || !IsDowned)
        {
            return;
        }

        GUIStyle style = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 24,
            fontStyle = FontStyle.Bold
        };
        style.normal.textColor = new Color(1f, 0.3f, 0.25f);

        string text = "YOU ARE DOWN\nBleeding out in " + Mathf.CeilToInt(BleedOutRemaining) + "s";

        float giveUpAvailableIn = giveUpDelay - DownedElapsed;

        if (giveUpAvailableIn <= 0f)
        {
            text += "\nPress R to give up";
        }
        else
        {
            text += "\nGive up available in " + Mathf.CeilToInt(giveUpAvailableIn) + "s";
        }

        GUI.Label(new Rect(0f, Screen.height * 0.55f, Screen.width, 120f), text, style);
    }

    // ---- All clients: visuals from replicated state ----

    private void OnStateChanged(PlayerLifeState previous, PlayerLifeState next, bool asServer)
    {
        if (asServer)
        {
            // The host applies visuals through its client callback; a dedicated
            // server has nothing to show. Team capture flags are set separately.
            return;
        }

        ApplyDownedVisuals(next != PlayerLifeState.Alive);

        // Client-side team flag so capture eligibility looks right everywhere.
        if (playerTeam != null)
        {
            playerTeam.isDowned = next == PlayerLifeState.Downed;
        }

        if (previous == PlayerLifeState.Dead && next == PlayerLifeState.Alive && IsOwner && setup != null)
        {
            setup.MoveOwnerToTeamSpawn();
        }
    }

    private void ApplyDownedVisuals(bool downed)
    {
        if (setup != null && setup.remoteBody != null)
        {
            Transform body = setup.remoteBody.transform;
            body.localPosition = downed ? downedBodyLocalPosition : standingBodyLocalPosition;
            body.localRotation = downed
                ? standingBodyLocalRotation * Quaternion.Euler(downedBodyLocalEulerAngles)
                : standingBodyLocalRotation;
        }

        if (!IsOwner)
        {
            return;
        }

        if (playerController != null)
        {
            playerController.enabled = !downed;
        }

        if (rifle != null)
        {
            rifle.enabled = !downed;
        }

        if (setup != null && setup.cameraRoot != null)
        {
            Vector3 cameraPosition = standingCameraLocalPosition;

            if (downed)
            {
                cameraPosition.y = downedCameraHeight;
            }

            setup.cameraRoot.transform.localPosition = cameraPosition;
        }
    }
}
