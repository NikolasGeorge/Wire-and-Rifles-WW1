using System.Collections;
using UnityEngine;

public class HelmetPopOff : MonoBehaviour
{
    public Transform helmet;

    [Header("Physics")]
    public float horizontalForce = 3f;
    public float upwardForce = 7f;
    public float torqueForce = 10f;
    public bool reverseHorizontalDirection;

    [Header("Collider")]
    public Vector3 helmetColliderCenter = new Vector3(0f, 0.03f, 0f);
    public Vector3 helmetColliderSize = new Vector3(0.35f, 0.18f, 0.35f);
    public bool ignoreTargetColliders = true;
    public bool ignorePlayerColliders = true;

    [Header("Reset")]
    public float resetDelay = 2f;

    private Transform originalParent;
    private Vector3 originalLocalPosition;
    private Quaternion originalLocalRotation;
    private Vector3 originalLocalScale;

    private Rigidbody helmetRigidbody;
    private BoxCollider helmetCollider;
    private Collider[] targetColliders;
    private Coroutine resetCoroutine;

    private void Awake()
    {
        if (helmet == null)
        {
            return;
        }

        originalParent = helmet.parent;
        originalLocalPosition = helmet.localPosition;
        originalLocalRotation = helmet.localRotation;
        originalLocalScale = helmet.localScale;

        helmetRigidbody = helmet.GetComponent<Rigidbody>();

        if (helmetRigidbody == null)
        {
            helmetRigidbody = helmet.gameObject.AddComponent<Rigidbody>();
        }

        helmetCollider = helmet.GetComponent<BoxCollider>();

        if (helmetCollider == null)
        {
            helmetCollider = helmet.gameObject.AddComponent<BoxCollider>();
        }

        targetColliders = GetComponentsInChildren<Collider>(true);

        ApplyColliderSettings();
        IgnoreTargetCollisions();
        IgnorePlayerCollisions();
        SetHelmetAttachedState();
    }

    public void PopOff(Vector3 attackDirection)
    {
        if (helmet == null || helmetRigidbody == null)
        {
            return;
        }

        if (resetCoroutine != null)
        {
            StopCoroutine(resetCoroutine);
        }

        helmet.SetParent(null, true);

        ApplyColliderSettings();
        IgnoreTargetCollisions();
        IgnorePlayerCollisions();

        if (helmetCollider != null)
        {
            helmetCollider.enabled = true;
        }

        helmetRigidbody.isKinematic = false;
        helmetRigidbody.useGravity = true;
        helmetRigidbody.linearVelocity = Vector3.zero;
        helmetRigidbody.angularVelocity = Vector3.zero;

        Vector3 horizontalDirection = new Vector3(attackDirection.x, 0f, attackDirection.z);

        if (horizontalDirection.sqrMagnitude < 0.001f)
        {
            horizontalDirection = helmet.forward;
        }

        horizontalDirection.Normalize();

        if (reverseHorizontalDirection)
        {
            horizontalDirection *= -1f;
        }

        Vector3 force = horizontalDirection * horizontalForce + Vector3.up * upwardForce;

        helmetRigidbody.AddForce(force, ForceMode.Impulse);
        helmetRigidbody.AddTorque(Random.insideUnitSphere * torqueForce, ForceMode.Impulse);

        resetCoroutine = StartCoroutine(ResetAfterDelay());
    }

    private IEnumerator ResetAfterDelay()
    {
        yield return new WaitForSeconds(resetDelay);

        ResetHelmet();
    }

    private void ResetHelmet()
    {
        if (helmet == null || helmetRigidbody == null)
        {
            return;
        }

        if (!helmetRigidbody.isKinematic)
        {
            helmetRigidbody.linearVelocity = Vector3.zero;
            helmetRigidbody.angularVelocity = Vector3.zero;
        }

        SetHelmetAttachedState();

        helmet.SetParent(originalParent, false);
        helmet.localPosition = originalLocalPosition;
        helmet.localRotation = originalLocalRotation;
        helmet.localScale = originalLocalScale;

        resetCoroutine = null;
    }

    private void ApplyColliderSettings()
    {
        if (helmetCollider == null)
        {
            return;
        }

        helmetCollider.center = helmetColliderCenter;
        helmetCollider.size = helmetColliderSize;
    }

    private void IgnoreTargetCollisions()
    {
        if (!ignoreTargetColliders || helmetCollider == null || targetColliders == null)
        {
            return;
        }

        foreach (Collider targetCollider in targetColliders)
        {
            if (targetCollider == null || targetCollider == helmetCollider)
            {
                continue;
            }

            Physics.IgnoreCollision(helmetCollider, targetCollider, true);
        }
    }

    private void IgnorePlayerCollisions()
    {
        if (!ignorePlayerColliders || helmetCollider == null)
        {
            return;
        }

        CharacterController[] characterControllers = FindObjectsByType<CharacterController>(FindObjectsSortMode.None);

        foreach (CharacterController characterController in characterControllers)
        {
            if (characterController != null)
            {
                Physics.IgnoreCollision(helmetCollider, characterController, true);
            }
        }

        PlayerController[] playerControllers = FindObjectsByType<PlayerController>(FindObjectsSortMode.None);

        foreach (PlayerController playerController in playerControllers)
        {
            Collider[] playerColliders = playerController.GetComponentsInChildren<Collider>(true);

            foreach (Collider playerCollider in playerColliders)
            {
                if (playerCollider == null || playerCollider == helmetCollider)
                {
                    continue;
                }

                Physics.IgnoreCollision(helmetCollider, playerCollider, true);
            }
        }
    }

    private void SetHelmetAttachedState()
    {
        if (helmetRigidbody != null)
        {
            helmetRigidbody.useGravity = false;
            helmetRigidbody.isKinematic = true;
        }

        if (helmetCollider != null)
        {
            helmetCollider.enabled = false;
        }
    }
}