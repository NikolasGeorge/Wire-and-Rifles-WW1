using System;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

public class TeamTicketManager : NetworkBehaviour
{
    public static TeamTicketManager Instance { get; private set; }

    public static event Action OnTicketsChanged;

    [Header("Tickets")]
    public int startingTickets = 1000;
    public int alliedPowersTickets;
    public int centralPowersTickets;

    [Header("Ticket Bleed")]
    public bool enableObjectiveTicketBleed = true;
    public float bleedInterval = 10f;
    public ObjectiveCaptureZone[] objectives;

    [Header("Debug")]
    public bool showDebugLogs = true;

    // Server-written replicated ticket counts. The public fields above remain
    // what UI reads; connected clients copy the synced values into them.
    private readonly SyncVar<int> syncAlliedTickets = new SyncVar<int>();
    private readonly SyncVar<int> syncCentralTickets = new SyncVar<int>();

    private float bleedTimer;

    // True on a connected client that is not the server. Offline single-player
    // keeps the original fully-local behavior.
    private bool IsRemoteClientOnly => IsClientInitialized && !IsServerInitialized;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;

        alliedPowersTickets = startingTickets;
        centralPowersTickets = startingTickets;

        if (objectives == null || objectives.Length == 0)
        {
            objectives = FindObjectsByType<ObjectiveCaptureZone>(FindObjectsSortMode.None);
        }

        syncAlliedTickets.OnChange += OnAlliedTicketsSynced;
        syncCentralTickets.OnChange += OnCentralTicketsSynced;

        OnTicketsChanged?.Invoke();
    }

    public override void OnStartServer()
    {
        base.OnStartServer();

        syncAlliedTickets.Value = alliedPowersTickets;
        syncCentralTickets.Value = centralPowersTickets;
    }

    private void OnAlliedTicketsSynced(int previous, int next, bool asServer)
    {
        if (asServer)
        {
            return;
        }

        alliedPowersTickets = next;
        OnTicketsChanged?.Invoke();
    }

    private void OnCentralTicketsSynced(int previous, int next, bool asServer)
    {
        if (asServer)
        {
            return;
        }

        centralPowersTickets = next;
        OnTicketsChanged?.Invoke();
    }

    private void Update()
    {
        if (IsRemoteClientOnly)
        {
            return;
        }

        if (!enableObjectiveTicketBleed)
        {
            return;
        }

        if (objectives == null || objectives.Length == 0)
        {
            return;
        }

        bleedTimer += Time.deltaTime;

        if (bleedTimer < bleedInterval)
        {
            return;
        }

        bleedTimer = 0f;
        ApplyObjectiveTicketBleed();
    }

    public void ConsumeDeathTicket(Team team)
    {
        ConsumeTickets(team, 1, "Death");
    }

    public void ConsumeTickets(Team team, int amount, string reason)
    {
        if (IsRemoteClientOnly)
        {
            return;
        }

        if (amount <= 0 || team == Team.Neutral)
        {
            return;
        }

        if (team == Team.AlliedPowers)
        {
            alliedPowersTickets = Mathf.Max(0, alliedPowersTickets - amount);
        }
        else if (team == Team.CentralPowers)
        {
            centralPowersTickets = Mathf.Max(0, centralPowersTickets - amount);
        }

        if (IsServerInitialized)
        {
            syncAlliedTickets.Value = alliedPowersTickets;
            syncCentralTickets.Value = centralPowersTickets;
        }

        if (showDebugLogs)
        {
            Debug.Log(team + " lost " + amount + " ticket(s). Reason: " + reason);
        }

        OnTicketsChanged?.Invoke();

        CheckForTicketLoss(team);
    }

    private void ApplyObjectiveTicketBleed()
    {
        int alliedOwned = CountObjectivesOwnedBy(Team.AlliedPowers);
        int centralOwned = CountObjectivesOwnedBy(Team.CentralPowers);
        int totalObjectives = objectives.Length;

        int majorityThreshold = Mathf.FloorToInt(totalObjectives * 0.5f) + 1;

        if (alliedOwned >= majorityThreshold)
        {
            int bleedAmount = alliedOwned - majorityThreshold + 1;
            ConsumeTickets(Team.CentralPowers, bleedAmount, "Objective Bleed");
            return;
        }

        if (centralOwned >= majorityThreshold)
        {
            int bleedAmount = centralOwned - majorityThreshold + 1;
            ConsumeTickets(Team.AlliedPowers, bleedAmount, "Objective Bleed");
        }
    }

    private int CountObjectivesOwnedBy(Team team)
    {
        int count = 0;

        foreach (ObjectiveCaptureZone objective in objectives)
        {
            if (objective == null)
            {
                continue;
            }

            if (objective.controllingTeam == team)
            {
                count++;
            }
        }

        return count;
    }

    private void CheckForTicketLoss(Team team)
    {
        if (team == Team.AlliedPowers && alliedPowersTickets <= 0)
        {
            Debug.Log("Allied Powers have no tickets remaining.");
        }
        else if (team == Team.CentralPowers && centralPowersTickets <= 0)
        {
            Debug.Log("Central Powers have no tickets remaining.");
        }
    }

    public int GetTickets(Team team)
    {
        if (team == Team.AlliedPowers)
        {
            return alliedPowersTickets;
        }

        if (team == Team.CentralPowers)
        {
            return centralPowersTickets;
        }

        return 0;
    }
}
