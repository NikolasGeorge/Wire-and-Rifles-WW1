using UnityEngine;

public class PlayerTeam : MonoBehaviour
{
    public Team team = Team.AlliedPowers;

    [Header("Capture State")]
    public bool canCaptureObjectives = true;
    public bool isDowned;

    public bool CanCaptureObjective()
    {
        if (!canCaptureObjectives)
        {
            return false;
        }

        if (isDowned)
        {
            return false;
        }

        HealthComponent health = GetComponentInParent<HealthComponent>();

        if (health != null && health.IsDead)
        {
            return false;
        }

        return team != Team.Neutral;
    }
}