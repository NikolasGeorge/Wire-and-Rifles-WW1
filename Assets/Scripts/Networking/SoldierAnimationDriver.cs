using UnityEngine;

// Drives the cloned soldier rig: crossfades between the controller's Idle
// and Run states from actual root movement. Pauses the animator while the
// player is downed or dead so the downed pose owns the body.
public class SoldierAnimationDriver : MonoBehaviour
{
    public Animator animator;
    public Transform movementRoot;
    public float runSpeedThreshold = 0.6f;
    public float crossFadeTime = 0.15f;

    private PlayerNetworkHealth health;
    private Vector3 lastPosition;
    private bool runningState;
    private bool stateInitialized;

    private void Start()
    {
        if (movementRoot == null)
        {
            movementRoot = transform;
        }

        health = GetComponentInParent<PlayerNetworkHealth>();
        lastPosition = movementRoot.position;
    }

    private void Update()
    {
        if (animator == null)
        {
            return;
        }

        if (health != null && health.State != PlayerLifeState.Alive)
        {
            if (animator.enabled)
            {
                animator.enabled = false;
            }

            return;
        }

        if (!animator.enabled)
        {
            animator.enabled = true;
            stateInitialized = false;
        }

        Vector3 delta = movementRoot.position - lastPosition;
        lastPosition = movementRoot.position;
        delta.y = 0f;

        float speed = Time.deltaTime > 0f ? delta.magnitude / Time.deltaTime : 0f;
        bool shouldRun = speed >= runSpeedThreshold;

        if (!stateInitialized || shouldRun != runningState)
        {
            stateInitialized = true;
            runningState = shouldRun;
            animator.CrossFadeInFixedTime(shouldRun ? "Run" : "Idle", crossFadeTime);
        }
    }
}
