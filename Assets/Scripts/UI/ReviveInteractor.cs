using UnityEngine;
using UnityEngine.InputSystem;

public class ReviveInteractor : MonoBehaviour
{
    public Camera playerCamera;
    public PlayerTeam localPlayerTeam;
    public RevivePromptUI revivePromptUI;

    [Header("Revive")]
    public float reviveDistance = 3f;
    public float reviveRadius = 0.35f;
    public float reviveHoldTime = 2f;
    public Key reviveKey = Key.E;

    [Header("Display")]
    public string reviverDisplayName = "Player";
    public bool useGettingRevivedStyleText = true;

    private HealthComponent currentTarget;
    private float reviveTimer;

    private void Awake()
    {
        if (playerCamera == null)
        {
            playerCamera = GetComponentInChildren<Camera>();
        }

        if (localPlayerTeam == null)
        {
            localPlayerTeam = GetComponentInParent<PlayerTeam>();
        }

        if (revivePromptUI == null)
        {
            revivePromptUI = FindAnyObjectByType<RevivePromptUI>();
        }

        if (revivePromptUI != null)
        {
            revivePromptUI.Hide();
        }
    }

    private void Update()
    {
        if (Keyboard.current == null)
        {
            HidePromptAndReset();
            return;
        }

        HealthComponent target = FindReviveTarget();

        if (target == null)
        {
            HidePromptAndReset();
            return;
        }

        if (target != currentTarget)
        {
            currentTarget = target;
            reviveTimer = 0f;
        }

        bool reviveHeld = Keyboard.current[reviveKey].isPressed;

        if (!reviveHeld)
        {
            reviveTimer = 0f;

            if (revivePromptUI != null)
            {
                revivePromptUI.ShowAvailablePrompt();
            }

            return;
        }

        reviveTimer += Time.deltaTime;

        float progress01 = Mathf.Clamp01(reviveTimer / Mathf.Max(0.01f, reviveHoldTime));

        if (revivePromptUI != null)
        {
            if (useGettingRevivedStyleText)
            {
                revivePromptUI.ShowGettingRevivedBy(progress01, reviverDisplayName);
            }
            else
            {
                revivePromptUI.ShowReviving(progress01, currentTarget.name);
            }
        }

        if (reviveTimer >= reviveHoldTime)
        {
            bool revived = currentTarget.Revive();

            if (revived)
            {
                Debug.Log("Revived " + currentTarget.name);
            }

            HidePromptAndReset();
        }
    }

    private HealthComponent FindReviveTarget()
    {
        if (playerCamera == null || localPlayerTeam == null)
        {
            return null;
        }

        Ray ray = new Ray(playerCamera.transform.position, playerCamera.transform.forward);
        RaycastHit[] hits = Physics.SphereCastAll(ray, reviveRadius, reviveDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);

        if (hits == null || hits.Length == 0)
        {
            return null;
        }

        HealthComponent closestValidTarget = null;
        float closestDistance = float.MaxValue;

        foreach (RaycastHit hit in hits)
        {
            if (hit.collider == null)
            {
                continue;
            }

            HealthComponent health = hit.collider.GetComponentInParent<HealthComponent>();

            if (health == null)
            {
                continue;
            }

            if (!health.CanBeRevivedBy(localPlayerTeam))
            {
                continue;
            }

            if (hit.distance < closestDistance)
            {
                closestDistance = hit.distance;
                closestValidTarget = health;
            }
        }

        return closestValidTarget;
    }

    private void HidePromptAndReset()
    {
        currentTarget = null;
        reviveTimer = 0f;

        if (revivePromptUI != null)
        {
            revivePromptUI.Hide();
        }
    }
}