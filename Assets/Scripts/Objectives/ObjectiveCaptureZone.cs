using System;
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

[RequireComponent(typeof(Collider))]
public class ObjectiveCaptureZone : NetworkBehaviour
{
    public static event Action<ObjectiveCaptureZone, ObjectiveEventType, Team> OnObjectiveEvent;

    [Header("Display")]
    public string objectiveLetter = "A";
    public string objectiveName = "Objective A";

    [Header("Ownership")]
    public Team controllingTeam = Team.Neutral;

    [Range(-100f, 100f)]
    public float controlPercent;

    [Header("Capture")]
    public float captureTimeFromNeutral = 8f;
    public bool allowContestedTugOfWar = true;
    public float contestedCaptureMultiplier = 1f;

    [Header("Decay")]
    public bool decayWhenNoTeamHasAdvantage = true;
    public float decayTimeToStableState = 10f;

    [Header("Debug")]
    public bool showDebugLogs = true;

    private readonly HashSet<PlayerTeam> playersInZone = new HashSet<PlayerTeam>();

    private bool wasContested;

    // Server-written replicated capture state. The public fields above remain
    // what UI reads; connected clients copy the synced values into them.
    private readonly SyncVar<float> syncControlPercent = new SyncVar<float>();
    private readonly SyncVar<Team> syncControllingTeam = new SyncVar<Team>();
    private readonly SyncVar<bool> syncIsContested = new SyncVar<bool>();

    // True on a connected client that is not the server. Offline single-player
    // keeps the original fully-local simulation.
    private bool IsRemoteClientOnly => IsClientInitialized && !IsServerInitialized;

    public float ControlPercent => controlPercent;
    public float ControlProgress01 => Mathf.Clamp01(Mathf.Abs(controlPercent) / 100f);
    public bool IsContested { get; private set; }
    public int PlayerCount => playersInZone.Count;
    public int AlliedPowerCount { get; private set; }
    public int CentralPowerCount { get; private set; }

    private void Reset()
    {
        Collider zoneCollider = GetComponent<Collider>();
        zoneCollider.isTrigger = true;
    }

    private void Awake()
    {
        Collider zoneCollider = GetComponent<Collider>();
        zoneCollider.isTrigger = true;

        SetControlPercentFromTeam(controllingTeam);
    }

    private void Update()
    {
        if (IsRemoteClientOnly)
        {
            controlPercent = syncControlPercent.Value;
            controllingTeam = syncControllingTeam.Value;
            IsContested = syncIsContested.Value;
            return;
        }

        SimulateCapture();

        if (IsServerInitialized)
        {
            syncControlPercent.Value = controlPercent;
            syncControllingTeam.Value = controllingTeam;
            syncIsContested.Value = IsContested;
        }
    }

    private void SimulateCapture()
    {
        CleanupMissingPlayers();
        CountTeams();
        UpdateContestState();

        float capturePower = GetCapturePower();

        if (Mathf.Approximately(capturePower, 0f))
        {
            if (decayWhenNoTeamHasAdvantage && !IsContested)
            {
                DecayTowardStableState();
            }

            return;
        }

        float captureSpeed = 100f / Mathf.Max(0.01f, captureTimeFromNeutral);
        controlPercent += capturePower * captureSpeed * Time.deltaTime;
        controlPercent = Mathf.Clamp(controlPercent, -100f, 100f);

        UpdateControllingTeam();
    }

    private void OnTriggerEnter(Collider other)
    {
        if (IsRemoteClientOnly)
        {
            return;
        }

        PlayerTeam playerTeam = other.GetComponentInParent<PlayerTeam>();

        if (playerTeam == null || playerTeam.team == Team.Neutral)
        {
            return;
        }

        playersInZone.Add(playerTeam);

        if (showDebugLogs)
        {
            Debug.Log(playerTeam.team + " entered " + objectiveName);
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (IsRemoteClientOnly)
        {
            return;
        }

        PlayerTeam playerTeam = other.GetComponentInParent<PlayerTeam>();

        if (playerTeam == null)
        {
            return;
        }

        playersInZone.Remove(playerTeam);

        if (showDebugLogs)
        {
            Debug.Log(playerTeam.team + " left " + objectiveName);
        }
    }

    private void UpdateContestState()
    {
        IsContested = AlliedPowerCount > 0 && CentralPowerCount > 0;

        if (IsContested == wasContested)
        {
            return;
        }

        wasContested = IsContested;

        if (IsContested)
        {
            RaiseObjectiveEvent(ObjectiveEventType.ContestedStarted, Team.Neutral);

            if (showDebugLogs)
            {
                Debug.Log(objectiveName + " contested.");
            }
        }
        else
        {
            RaiseObjectiveEvent(ObjectiveEventType.ContestedEnded, Team.Neutral);

            if (showDebugLogs)
            {
                Debug.Log(objectiveName + " no longer contested.");
            }
        }
    }

    private float GetCapturePower()
    {
        int netPower = AlliedPowerCount - CentralPowerCount;

        if (netPower == 0)
        {
            return 0f;
        }

        if (IsContested && !allowContestedTugOfWar)
        {
            return 0f;
        }

        float power = netPower;

        if (IsContested)
        {
            power *= contestedCaptureMultiplier;
        }

        return power;
    }

    private void CountTeams()
    {
        AlliedPowerCount = 0;
        CentralPowerCount = 0;

        foreach (PlayerTeam playerTeam in playersInZone)
        {
            if (playerTeam == null || !playerTeam.CanCaptureObjective())
            {
                continue;
            }

            if (playerTeam.team == Team.AlliedPowers)
            {
                AlliedPowerCount++;
            }
            else if (playerTeam.team == Team.CentralPowers)
            {
                CentralPowerCount++;
            }
        }
    }

    private void UpdateControllingTeam()
    {
        if (controllingTeam == Team.AlliedPowers && controlPercent <= 0f)
        {
            controllingTeam = Team.Neutral;
            controlPercent = 0f;

            RaiseObjectiveEvent(ObjectiveEventType.Neutralized, Team.Neutral);

            if (showDebugLogs)
            {
                Debug.Log(objectiveName + " neutralized.");
            }

            return;
        }

        if (controllingTeam == Team.CentralPowers && controlPercent >= 0f)
        {
            controllingTeam = Team.Neutral;
            controlPercent = 0f;

            RaiseObjectiveEvent(ObjectiveEventType.Neutralized, Team.Neutral);

            if (showDebugLogs)
            {
                Debug.Log(objectiveName + " neutralized.");
            }

            return;
        }

        if (controllingTeam == Team.Neutral && controlPercent >= 100f)
        {
            controllingTeam = Team.AlliedPowers;
            controlPercent = 100f;

            RaiseObjectiveEvent(ObjectiveEventType.Captured, controllingTeam);

            if (showDebugLogs)
            {
                Debug.Log(objectiveName + " captured by " + controllingTeam);
            }

            return;
        }

        if (controllingTeam == Team.Neutral && controlPercent <= -100f)
        {
            controllingTeam = Team.CentralPowers;
            controlPercent = -100f;

            RaiseObjectiveEvent(ObjectiveEventType.Captured, controllingTeam);

            if (showDebugLogs)
            {
                Debug.Log(objectiveName + " captured by " + controllingTeam);
            }
        }
    }

    private void DecayTowardStableState()
    {
        float targetPercent = GetStableControlPercent();
        float decaySpeed = 100f / Mathf.Max(0.01f, decayTimeToStableState);

        controlPercent = Mathf.MoveTowards(controlPercent, targetPercent, decaySpeed * Time.deltaTime);
        controlPercent = Mathf.Clamp(controlPercent, -100f, 100f);
    }

    private float GetStableControlPercent()
    {
        if (controllingTeam == Team.AlliedPowers)
        {
            return 100f;
        }

        if (controllingTeam == Team.CentralPowers)
        {
            return -100f;
        }

        return 0f;
    }

    private void SetControlPercentFromTeam(Team team)
    {
        if (team == Team.AlliedPowers)
        {
            controlPercent = 100f;
        }
        else if (team == Team.CentralPowers)
        {
            controlPercent = -100f;
        }
        else
        {
            controlPercent = 0f;
        }
    }

    private void RaiseObjectiveEvent(ObjectiveEventType eventType, Team relatedTeam)
    {
        if (IsServerInitialized)
        {
            // All clients (including the host's client) receive it via the RPC.
            ObserversRaiseObjectiveEvent(eventType, relatedTeam);
            return;
        }

        OnObjectiveEvent?.Invoke(this, eventType, relatedTeam);
    }

    [ObserversRpc]
    private void ObserversRaiseObjectiveEvent(ObjectiveEventType eventType, Team relatedTeam)
    {
        OnObjectiveEvent?.Invoke(this, eventType, relatedTeam);
    }

    private void CleanupMissingPlayers()
    {
        playersInZone.RemoveWhere(playerTeam => playerTeam == null || !playerTeam.gameObject.activeInHierarchy);
    }
}