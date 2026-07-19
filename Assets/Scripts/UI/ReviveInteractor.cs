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

    // Either a PlayerNetworkHealth (networked player) or a HealthComponent
    // (local practice dummy).
    private Component currentTarget;
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

        Component target = FindReviveTarget();

        if (target == null)
        {
            // Looking away hides the prompt but keeps the checkpointed
            // progress for the remembered target, unless they are no longer
            // revivable (revived by someone else, died, despawned).
            if (currentTarget == null || !IsStillRevivable(currentTarget))
            {
                HidePromptAndReset();
                return;
            }

            SnapTimerToCheckpoint();

            if (revivePromptUI != null)
            {
                revivePromptUI.Hide();
            }

            return;
        }

        if (target != currentTarget)
        {
            currentTarget = target;
            reviveTimer = 0f;
        }

        bool reviveHeld = Keyboard.current[reviveKey].isPressed;
        float holdTime = Mathf.Max(0.01f, reviveHoldTime);

        if (!reviveHeld)
        {
            // Releasing keeps the last completed quarter (25/50/75%) instead
            // of resetting, so a briefly interrupted revive resumes from there.
            float checkpoint = SnapTimerToCheckpoint();

            if (revivePromptUI != null)
            {
                revivePromptUI.ShowAvailablePrompt(checkpoint);
            }

            return;
        }

        reviveTimer += Time.deltaTime;

        float progress01 = Mathf.Clamp01(reviveTimer / holdTime);

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
            if (currentTarget is PlayerNetworkHealth playerHealth)
            {
                // Networked player: the server validates and applies the revive.
                playerHealth.RequestRevive();
                Debug.Log("Requested revive of " + playerHealth.name);
            }
            else if (currentTarget is HealthComponent dummyHealth)
            {
                if (dummyHealth.Revive())
                {
                    Debug.Log("Revived " + dummyHealth.name);
                }
            }

            HidePromptAndReset();
        }
    }

    private Component FindReviveTarget()
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

        Component closestValidTarget = null;
        float closestDistance = float.MaxValue;

        foreach (RaycastHit hit in hits)
        {
            if (hit.collider == null)
            {
                continue;
            }

            Component candidate = null;

            PlayerNetworkHealth playerHealth = hit.collider.GetComponentInParent<PlayerNetworkHealth>();

            if (playerHealth != null)
            {
                if (playerHealth.CanBeRevivedBy(localPlayerTeam))
                {
                    candidate = playerHealth;
                }
            }
            else
            {
                HealthComponent health = hit.collider.GetComponentInParent<HealthComponent>();

                if (health != null && health.CanBeRevivedBy(localPlayerTeam))
                {
                    candidate = health;
                }
            }

            if (candidate == null)
            {
                continue;
            }

            if (hit.distance < closestDistance)
            {
                closestDistance = hit.distance;
                closestValidTarget = candidate;
            }
        }

        return closestValidTarget;
    }

    private float SnapTimerToCheckpoint()
    {
        float holdTime = Mathf.Max(0.01f, reviveHoldTime);
        float checkpoint = Mathf.Floor(Mathf.Clamp01(reviveTimer / holdTime) / 0.25f) * 0.25f;
        checkpoint = Mathf.Min(checkpoint, 0.75f);
        reviveTimer = checkpoint * holdTime;

        return checkpoint;
    }

    private bool IsStillRevivable(Component target)
    {
        if (target is PlayerNetworkHealth playerHealth)
        {
            return playerHealth.CanBeRevivedBy(localPlayerTeam);
        }

        if (target is HealthComponent dummyHealth)
        {
            return dummyHealth.CanBeRevivedBy(localPlayerTeam);
        }

        return false;
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