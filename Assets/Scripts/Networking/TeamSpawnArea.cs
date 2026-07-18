using UnityEngine;

// Marker object for a team's spawn region. Child transforms are the actual
// spawn points; with no children the area's own transform is used.
public class TeamSpawnArea : MonoBehaviour
{
    public Team team = Team.AlliedPowers;

    public static Transform GetSpawnPoint(Team team)
    {
        TeamSpawnArea[] areas = FindObjectsByType<TeamSpawnArea>(FindObjectsSortMode.None);

        foreach (TeamSpawnArea area in areas)
        {
            if (area.team != team)
            {
                continue;
            }

            if (area.transform.childCount == 0)
            {
                return area.transform;
            }

            return area.transform.GetChild(Random.Range(0, area.transform.childCount));
        }

        return null;
    }
}
