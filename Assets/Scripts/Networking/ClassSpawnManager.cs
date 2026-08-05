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

    // Zones in HUD order (sorted by letter), shared with the spawn-select UI.
    public static List<ObjectiveCaptureZone> GetZonesSortedByLetter()
    {
        List<ObjectiveCaptureZone> zones = new List<ObjectiveCaptureZone>(
            FindObjectsByType<ObjectiveCaptureZone>(FindObjectsInactive.Include, FindObjectsSortMode.None));

        zones.Sort((a, b) => string.CompareOrdinal(a.objectiveLetter, b.objectiveLetter));

        return zones;
    }

    // requestedTeam: the player's own pick from the join screen; Neutral
    // falls back to the alternating auto-assignment.
    // weaponIndex indexes the class's weaponOptions (validated server-side).
    // spawnObjectiveIndex: -1 spawns at the team base; otherwise an index
    // into GetZonesSortedByLetter(), valid only while that team owns the zone.
    [ServerRpc(RequireOwnership = false)]
    public void RequestSpawn(Team requestedTeam, PlayerClass playerClass, int weaponIndex,
        GrenadeType grenade, EquipmentType equipment1, EquipmentType equipment2,
        int spawnObjectiveIndex, NetworkConnection sender = null)
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

        Team team;

        if (requestedTeam == Team.AlliedPowers || requestedTeam == Team.CentralPowers)
        {
            team = requestedTeam;
            teamAssignments[sender.ClientId] = team;
        }
        else if (!teamAssignments.TryGetValue(sender.ClientId, out team))
        {
            team = teamAssignCounter % 2 == 0 ? Team.AlliedPowers : Team.CentralPowers;
            teamAssignCounter++;
            teamAssignments[sender.ClientId] = team;
        }

        Vector3 position = Vector3.zero;
        Quaternion rotation = Quaternion.identity;
        bool positioned = false;

        if (spawnObjectiveIndex >= 0)
        {
            List<ObjectiveCaptureZone> zones = GetZonesSortedByLetter();

            if (spawnObjectiveIndex < zones.Count && zones[spawnObjectiveIndex].controllingTeam == team)
            {
                float angle = Random.Range(0f, Mathf.PI * 2f);
                Vector3 ringOffset = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * 2.5f;
                position = zones[spawnObjectiveIndex].transform.position + ringOffset + Vector3.up * 0.5f;
                positioned = true;
            }
        }

        if (!positioned)
        {
            Transform spawnPoint = TeamSpawnArea.GetSpawnPoint(team);

            if (spawnPoint != null)
            {
                position = spawnPoint.position;
                rotation = Quaternion.Euler(0f, spawnPoint.eulerAngles.y, 0f);
            }
        }

        NetworkObject instance = Instantiate(playerPrefab, position, rotation);

        PlayerNetworkSetup setup = instance.GetComponent<PlayerNetworkSetup>();

        if (setup != null)
        {
            setup.pendingTeam = team;
            setup.pendingClass = playerClass;

            WeaponId[] options = PlayerClasses.Get(playerClass).weaponOptions;
            int clampedIndex = options != null && options.Length > 0
                ? Mathf.Clamp(weaponIndex, 0, options.Length - 1)
                : -1;
            setup.pendingWeapon = clampedIndex >= 0 ? options[clampedIndex] : WeaponId.BoltAction;

            // Grenade/equipment: forced to the class kit unless the class has
            // a customizable loadout (Assault), whose picks are pool-checked.
            LoadoutSelection sanitized = LoadoutData.Sanitize(playerClass, new LoadoutSelection
            {
                weaponIndex = clampedIndex,
                grenade = grenade,
                equipment1 = equipment1,
                equipment2 = equipment2
            });

            setup.pendingGrenade = sanitized.grenade;
            setup.pendingEquipment1 = sanitized.equipment1;
            setup.pendingEquipment2 = sanitized.equipment2;
        }

        ServerManager.Spawn(instance, sender);
    }
}
