using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Object;
using UnityEngine;

// Spawns a player object for a connection once it has picked a class.
// Replaces Fish-Net's automatic PlayerSpawner so class selection can gate
// spawning both on join and after death.
public class ClassSpawnManager : NetworkBehaviour
{
    public static ClassSpawnManager Instance { get; private set; }

    public NetworkObject playerPrefab;

    // Team per connection, stable across deaths. Assigned alternately on the
    // first spawn request.
    private readonly Dictionary<int, Team> teamAssignments = new Dictionary<int, Team>();
    private int teamAssignCounter;

    private void Awake()
    {
        Instance = this;
    }

    [ServerRpc(RequireOwnership = false)]
    public void RequestSpawn(PlayerClass playerClass, NetworkConnection sender = null)
    {
        if (sender == null || playerPrefab == null)
        {
            return;
        }

        // Already has a live player.
        if (sender.FirstObject != null)
        {
            return;
        }

        if (!teamAssignments.TryGetValue(sender.ClientId, out Team team))
        {
            team = teamAssignCounter % 2 == 0 ? Team.AlliedPowers : Team.CentralPowers;
            teamAssignCounter++;
            teamAssignments[sender.ClientId] = team;
        }

        Vector3 position = Vector3.zero;
        Quaternion rotation = Quaternion.identity;
        Transform spawnPoint = TeamSpawnArea.GetSpawnPoint(team);

        if (spawnPoint != null)
        {
            position = spawnPoint.position;
            rotation = Quaternion.Euler(0f, spawnPoint.eulerAngles.y, 0f);
        }

        NetworkObject instance = Instantiate(playerPrefab, position, rotation);

        PlayerNetworkSetup setup = instance.GetComponent<PlayerNetworkSetup>();

        if (setup != null)
        {
            setup.pendingTeam = team;
            setup.pendingClass = playerClass;
        }

        ServerManager.Spawn(instance, sender);
    }
}
