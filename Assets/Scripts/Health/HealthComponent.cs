using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class HealthComponent : MonoBehaviour
{
    public float maxHealth = 100f;
    public float currentHealth;

    [Header("Downed State")]
    public bool useDownedState = true;
    public float bleedOutTime = 30f;
    public float reviveHealth = 50f;
    public bool allowDamageToFinishDowned = true;

    [Header("Downed Pose")]
    public bool rotateWhenDowned = true;
    public Transform downedRotationRoot;
    public bool useRelativeDownedRotation = true;
    public Vector3 downedLocalEulerAngles = new Vector3(-90f, 0f, 0f);

    [Header("Downed Movement Lock")]
    public bool disableCharacterControllerWhenDowned = true;
    public CharacterController characterControllerToDisable;
    public bool freezeRigidbodyWhenDowned = true;
    public Rigidbody rigidbodyToFreeze;
    public MonoBehaviour[] movementComponentsToDisableWhileDowned;

    [Header("Downed Player Collision")]
    public bool ignorePlayerCollisionWhileDowned = true;
    public bool autoFindPlayerCollisionColliders = true;
    public Collider[] playerCollisionColliders;
    public Collider[] ownCollidersToIgnorePlayerCollision;

    [Header("Downed Wall Push")]
    public bool preventDownedPoseClipping = true;
    public LayerMask downedCollisionMask = ~0;
    public float downedClipProbeRadius = 0.24f;
    public float downedHorizontalPushStep = 0.08f;
    public float downedMaxPushPerIteration = 0.25f;
    public int downedClipResolveIterations = 12;
    public bool keepOriginalYDuringClipResolve = true;
    public bool ignoreGroundLikeColliders = true;
    public float groundLikeColliderMaxHeight = 0.45f;

    [Header("Downed Ground Snap")]
    public bool snapDownedBodyToGround = true;
    public LayerMask downedGroundMask = ~0;
    public float groundSnapRayHeight = 2f;
    public float groundSnapRayDistance = 6f;
    public float groundSnapSkin = 0.03f;
    public float minimumGroundNormalY = 0.55f;
    public float maxGroundSnapDistance = 1.25f;
    public bool rejectHighGroundHits = true;
    public float maxGroundHitHeightAboveBodyBottom = 0.45f;

    [Header("Respawn")]
    public bool respawnOnDeath = true;
    public float respawnDelay = 2f;
    public bool hideTargetOnDeath;

    [Header("Tickets")]
    public bool consumeTicketOnFullDeath = true;
    public int deathTicketCost = 1;

    private bool isDead;
    private bool isDowned;
    private float bleedOutRemaining;
    private Coroutine bleedOutCoroutine;
    private Renderer[] renderers;
    private Collider[] colliders;
    private Quaternion originalLocalRotation;
    private Quaternion standingLocalRotationBeforeDowned;

    private bool movementLocked;
    private bool characterControllerWasEnabled;
    private bool rigidbodyWasKinematic;
    private bool rigidbodyUsedGravity;
    private RigidbodyConstraints originalRigidbodyConstraints;
    private bool[] movementComponentOriginalStates;

    public bool IsDead => isDead;
    public bool IsDowned => isDowned;
    public float BleedOutRemaining => bleedOutRemaining;
    public float BleedOutProgress01 => bleedOutTime <= 0f ? 0f : Mathf.Clamp01(bleedOutRemaining / bleedOutTime);

    private void Awake()
    {
        if (downedRotationRoot == null)
        {
            downedRotationRoot = transform;
        }

        if (characterControllerToDisable == null)
        {
            characterControllerToDisable = GetComponentInParent<CharacterController>();
        }

        if (rigidbodyToFreeze == null)
        {
            rigidbodyToFreeze = GetComponentInParent<Rigidbody>();
        }

        originalLocalRotation = downedRotationRoot.localRotation;
        standingLocalRotationBeforeDowned = originalLocalRotation;

        renderers = GetComponentsInChildren<Renderer>(true);
        colliders = GetComponentsInChildren<Collider>(true);

        if (ownCollidersToIgnorePlayerCollision == null || ownCollidersToIgnorePlayerCollision.Length == 0)
        {
            ownCollidersToIgnorePlayerCollision = colliders;
        }

        CacheMovementComponentStates();

        Respawn();
    }

    public bool TakeDamage(float damage)
    {
        if (isDead)
        {
            return false;
        }

        if (isDowned)
        {
            if (allowDamageToFinishDowned)
            {
                FullDie("Finished while downed");
                return true;
            }

            return false;
        }

        currentHealth -= damage;
        currentHealth = Mathf.Max(currentHealth, 0f);

        Debug.Log(gameObject.name + " took " + damage + " damage. HP: " + currentHealth);

        if (currentHealth <= 0f)
        {
            if (useDownedState)
            {
                EnterDownedState();
                return false;
            }

            FullDie("Killed");
            return true;
        }

        return false;
    }

    private void EnterDownedState()
    {
        if (isDead || isDowned)
        {
            return;
        }

        isDowned = true;
        currentHealth = 0f;
        bleedOutRemaining = bleedOutTime;

        CaptureStandingRotationBeforeDowned();
        LockMovementForDowned();
        SetPlayerCollisionIgnored(true);
        ApplyDownedPose();
        ResolveDownedWallClipping();
        SnapDownedBodyToGround();
        SetPlayerTeamDownedState(true);

        if (bleedOutCoroutine != null)
        {
            StopCoroutine(bleedOutCoroutine);
        }

        bleedOutCoroutine = StartCoroutine(BleedOutRoutine());

        Debug.Log(gameObject.name + " is downed.");
    }

    private IEnumerator BleedOutRoutine()
    {
        while (bleedOutRemaining > 0f && isDowned && !isDead)
        {
            bleedOutRemaining -= Time.deltaTime;
            bleedOutRemaining = Mathf.Max(bleedOutRemaining, 0f);

            yield return null;
        }

        if (isDowned && !isDead)
        {
            FullDie("Bleedout");
        }
    }

    public bool Revive()
    {
        if (!isDowned || isDead)
        {
            return false;
        }

        if (bleedOutCoroutine != null)
        {
            StopCoroutine(bleedOutCoroutine);
            bleedOutCoroutine = null;
        }

        isDowned = false;
        bleedOutRemaining = 0f;
        currentHealth = Mathf.Clamp(reviveHealth, 1f, maxHealth);

        RestoreNormalPoseFromDowned();
        SetPlayerCollisionIgnored(false);
        RestoreMovementAfterDowned();
        SetPlayerTeamDownedState(false);

        Debug.Log(gameObject.name + " revived with " + currentHealth + " HP.");

        return true;
    }

    public bool CanBeRevivedBy(PlayerTeam reviverTeam)
    {
        if (!isDowned || isDead)
        {
            return false;
        }

        if (reviverTeam == null || reviverTeam.team == Team.Neutral)
        {
            return false;
        }

        PlayerTeam targetTeam = GetComponentInParent<PlayerTeam>();

        if (targetTeam == null)
        {
            return false;
        }

        return targetTeam.team == reviverTeam.team;
    }

    private void FullDie(string reason)
    {
        if (isDead)
        {
            return;
        }

        isDead = true;
        isDowned = false;
        currentHealth = 0f;
        bleedOutRemaining = 0f;

        if (bleedOutCoroutine != null)
        {
            StopCoroutine(bleedOutCoroutine);
            bleedOutCoroutine = null;
        }

        SetPlayerTeamDownedState(false);

        Debug.Log(gameObject.name + " died. Reason: " + reason);

        ConsumeDeathTickets();

        if (hideTargetOnDeath)
        {
            SetTargetVisible(false);
            SetTargetColliders(false);
        }

        if (respawnOnDeath)
        {
            StartCoroutine(RespawnAfterDelay());
        }
    }

    private void LockMovementForDowned()
    {
        if (movementLocked)
        {
            return;
        }

        movementLocked = true;

        if (disableCharacterControllerWhenDowned && characterControllerToDisable != null)
        {
            characterControllerWasEnabled = characterControllerToDisable.enabled;
            characterControllerToDisable.enabled = false;
        }

        if (freezeRigidbodyWhenDowned && rigidbodyToFreeze != null)
        {
            rigidbodyWasKinematic = rigidbodyToFreeze.isKinematic;
            rigidbodyUsedGravity = rigidbodyToFreeze.useGravity;
            originalRigidbodyConstraints = rigidbodyToFreeze.constraints;

            rigidbodyToFreeze.linearVelocity = Vector3.zero;
            rigidbodyToFreeze.angularVelocity = Vector3.zero;
            rigidbodyToFreeze.isKinematic = true;
            rigidbodyToFreeze.useGravity = false;
            rigidbodyToFreeze.constraints = RigidbodyConstraints.FreezeAll;
        }

        if (movementComponentsToDisableWhileDowned == null)
        {
            return;
        }

        if (movementComponentOriginalStates == null || movementComponentOriginalStates.Length != movementComponentsToDisableWhileDowned.Length)
        {
            CacheMovementComponentStates();
        }

        for (int i = 0; i < movementComponentsToDisableWhileDowned.Length; i++)
        {
            MonoBehaviour movementComponent = movementComponentsToDisableWhileDowned[i];

            if (movementComponent == null || movementComponent == this)
            {
                continue;
            }

            movementComponentOriginalStates[i] = movementComponent.enabled;
            movementComponent.enabled = false;
        }
    }

    private void RestoreMovementAfterDowned()
    {
        if (!movementLocked)
        {
            return;
        }

        movementLocked = false;

        if (disableCharacterControllerWhenDowned && characterControllerToDisable != null)
        {
            characterControllerToDisable.enabled = characterControllerWasEnabled;
        }

        if (freezeRigidbodyWhenDowned && rigidbodyToFreeze != null)
        {
            rigidbodyToFreeze.isKinematic = rigidbodyWasKinematic;
            rigidbodyToFreeze.useGravity = rigidbodyUsedGravity;
            rigidbodyToFreeze.constraints = originalRigidbodyConstraints;
        }

        if (movementComponentsToDisableWhileDowned == null || movementComponentOriginalStates == null)
        {
            return;
        }

        for (int i = 0; i < movementComponentsToDisableWhileDowned.Length; i++)
        {
            MonoBehaviour movementComponent = movementComponentsToDisableWhileDowned[i];

            if (movementComponent == null || movementComponent == this)
            {
                continue;
            }

            if (i < movementComponentOriginalStates.Length)
            {
                movementComponent.enabled = movementComponentOriginalStates[i];
            }
        }
    }

    private void CacheMovementComponentStates()
    {
        if (movementComponentsToDisableWhileDowned == null)
        {
            movementComponentOriginalStates = null;
            return;
        }

        movementComponentOriginalStates = new bool[movementComponentsToDisableWhileDowned.Length];

        for (int i = 0; i < movementComponentsToDisableWhileDowned.Length; i++)
        {
            MonoBehaviour movementComponent = movementComponentsToDisableWhileDowned[i];

            if (movementComponent != null)
            {
                movementComponentOriginalStates[i] = movementComponent.enabled;
            }
        }
    }

    private void CaptureStandingRotationBeforeDowned()
    {
        if (downedRotationRoot == null)
        {
            return;
        }

        standingLocalRotationBeforeDowned = downedRotationRoot.localRotation;
    }

    private void ApplyDownedPose()
    {
        if (!rotateWhenDowned || downedRotationRoot == null)
        {
            return;
        }

        if (useRelativeDownedRotation)
        {
            downedRotationRoot.localRotation = standingLocalRotationBeforeDowned * Quaternion.Euler(downedLocalEulerAngles);
        }
        else
        {
            downedRotationRoot.localRotation = Quaternion.Euler(downedLocalEulerAngles);
        }
    }

    private void RestoreNormalPoseFromDowned()
    {
        if (!rotateWhenDowned || downedRotationRoot == null)
        {
            return;
        }

        downedRotationRoot.localRotation = standingLocalRotationBeforeDowned;
    }

    private void RestoreOriginalPose()
    {
        if (!rotateWhenDowned || downedRotationRoot == null)
        {
            return;
        }

        downedRotationRoot.localRotation = originalLocalRotation;
    }

    private void SetPlayerCollisionIgnored(bool ignored)
    {
        if (!ignorePlayerCollisionWhileDowned)
        {
            return;
        }

        RefreshPlayerCollisionColliders();

        if (ownCollidersToIgnorePlayerCollision == null || playerCollisionColliders == null)
        {
            return;
        }

        foreach (Collider ownCollider in ownCollidersToIgnorePlayerCollision)
        {
            if (ownCollider == null)
            {
                continue;
            }

            foreach (Collider playerCollider in playerCollisionColliders)
            {
                if (playerCollider == null || ownCollider == playerCollider)
                {
                    continue;
                }

                Physics.IgnoreCollision(ownCollider, playerCollider, ignored);
            }
        }
    }

    private void RefreshPlayerCollisionColliders()
    {
        if (!autoFindPlayerCollisionColliders)
        {
            return;
        }

        List<Collider> foundColliders = new List<Collider>();

        PlayerController[] playerControllers = FindObjectsByType<PlayerController>(FindObjectsSortMode.None);

        foreach (PlayerController playerController in playerControllers)
        {
            if (playerController == null)
            {
                continue;
            }

            Collider[] foundOnPlayer = playerController.GetComponentsInParent<Collider>(true);

            foreach (Collider foundCollider in foundOnPlayer)
            {
                if (foundCollider == null || IsOwnCollider(foundCollider))
                {
                    continue;
                }

                if (!foundColliders.Contains(foundCollider))
                {
                    foundColliders.Add(foundCollider);
                }
            }

            Collider[] foundOnChildren = playerController.GetComponentsInChildren<Collider>(true);

            foreach (Collider foundCollider in foundOnChildren)
            {
                if (foundCollider == null || IsOwnCollider(foundCollider))
                {
                    continue;
                }

                if (!foundColliders.Contains(foundCollider))
                {
                    foundColliders.Add(foundCollider);
                }
            }
        }

        if (foundColliders.Count > 0)
        {
            playerCollisionColliders = foundColliders.ToArray();
        }
    }

    private void ResolveDownedWallClipping()
    {
        if (!preventDownedPoseClipping || downedRotationRoot == null)
        {
            return;
        }

        float originalY = transform.position.y;

        for (int i = 0; i < downedClipResolveIterations; i++)
        {
            Bounds bodyBounds = GetCombinedRendererBounds();

            if (bodyBounds.size.sqrMagnitude <= 0.001f)
            {
                return;
            }

            Vector3[] samplePoints = GetDownedBodySamplePoints(bodyBounds);
            Vector3 totalPush = Vector3.zero;

            foreach (Vector3 samplePoint in samplePoints)
            {
                Collider[] overlaps = Physics.OverlapSphere(
                    samplePoint,
                    downedClipProbeRadius,
                    downedCollisionMask,
                    QueryTriggerInteraction.Ignore
                );

                foreach (Collider overlap in overlaps)
                {
                    if (overlap == null || IsOwnCollider(overlap))
                    {
                        continue;
                    }

                    if (ShouldIgnoreAsGround(samplePoint, overlap))
                    {
                        continue;
                    }

                    Vector3 pushDirection = GetHorizontalPushDirection(samplePoint, bodyBounds.center, overlap);

                    if (pushDirection.sqrMagnitude < 0.0001f)
                    {
                        continue;
                    }

                    totalPush += pushDirection.normalized * downedHorizontalPushStep;
                }
            }

            totalPush.y = 0f;

            if (totalPush.sqrMagnitude < 0.0001f)
            {
                break;
            }

            totalPush = Vector3.ClampMagnitude(totalPush, downedMaxPushPerIteration);

            Vector3 newPosition = transform.position + totalPush;

            if (keepOriginalYDuringClipResolve)
            {
                newPosition.y = originalY;
            }

            transform.position = newPosition;
        }
    }

    private void SnapDownedBodyToGround()
    {
        if (!snapDownedBodyToGround)
        {
            return;
        }

        Bounds bodyBounds = GetCombinedRendererBounds();

        if (bodyBounds.size.sqrMagnitude <= 0.001f)
        {
            return;
        }

        Vector3[] samplePoints = GetGroundSnapSamplePoints(bodyBounds);
        bool foundGround = false;
        float highestValidGroundY = float.NegativeInfinity;

        foreach (Vector3 samplePoint in samplePoints)
        {
            Vector3 rayOrigin = samplePoint + Vector3.up * groundSnapRayHeight;
            float rayDistance = groundSnapRayHeight + groundSnapRayDistance;

            RaycastHit[] hits = Physics.RaycastAll(
                rayOrigin,
                Vector3.down,
                rayDistance,
                downedGroundMask,
                QueryTriggerInteraction.Ignore
            );

            foreach (RaycastHit hit in hits)
            {
                if (hit.collider == null || IsOwnCollider(hit.collider))
                {
                    continue;
                }

                if (hit.normal.y < minimumGroundNormalY)
                {
                    continue;
                }

                if (rejectHighGroundHits && hit.point.y > bodyBounds.min.y + maxGroundHitHeightAboveBodyBottom)
                {
                    continue;
                }

                if (hit.point.y > highestValidGroundY)
                {
                    highestValidGroundY = hit.point.y;
                    foundGround = true;
                }
            }
        }

        if (!foundGround)
        {
            return;
        }

        float targetBodyBottomY = highestValidGroundY + groundSnapSkin;
        float verticalDelta = targetBodyBottomY - bodyBounds.min.y;
        verticalDelta = Mathf.Clamp(verticalDelta, -maxGroundSnapDistance, maxGroundSnapDistance);

        if (Mathf.Abs(verticalDelta) <= 0.001f)
        {
            return;
        }

        Vector3 newPosition = transform.position;
        newPosition.y += verticalDelta;
        transform.position = newPosition;
    }

    private Bounds GetCombinedRendererBounds()
    {
        bool hasBounds = false;
        Bounds combinedBounds = new Bounds(transform.position, Vector3.zero);

        if (renderers == null)
        {
            return combinedBounds;
        }

        foreach (Renderer targetRenderer in renderers)
        {
            if (targetRenderer == null || !targetRenderer.enabled)
            {
                continue;
            }

            if (!hasBounds)
            {
                combinedBounds = targetRenderer.bounds;
                hasBounds = true;
            }
            else
            {
                combinedBounds.Encapsulate(targetRenderer.bounds);
            }
        }

        return combinedBounds;
    }

    private Vector3[] GetDownedBodySamplePoints(Bounds bodyBounds)
    {
        Vector3 center = bodyBounds.center;

        Vector3 horizontalSize = new Vector3(bodyBounds.size.x, 0f, bodyBounds.size.z);
        bool xIsLonger = horizontalSize.x >= horizontalSize.z;

        Vector3 longAxis = xIsLonger ? Vector3.right : Vector3.forward;
        Vector3 sideAxis = xIsLonger ? Vector3.forward : Vector3.right;

        float halfLength = (xIsLonger ? bodyBounds.extents.x : bodyBounds.extents.z) * 0.85f;
        float halfWidth = (xIsLonger ? bodyBounds.extents.z : bodyBounds.extents.x) * 0.75f;

        return new Vector3[]
        {
            center,
            center + longAxis * halfLength,
            center - longAxis * halfLength,
            center + longAxis * halfLength * 0.5f,
            center - longAxis * halfLength * 0.5f,
            center + sideAxis * halfWidth,
            center - sideAxis * halfWidth
        };
    }

    private Vector3[] GetGroundSnapSamplePoints(Bounds bodyBounds)
    {
        Vector3 center = bodyBounds.center;
        float halfX = bodyBounds.extents.x * 0.75f;
        float halfZ = bodyBounds.extents.z * 0.75f;

        return new Vector3[]
        {
            center,
            center + new Vector3(halfX, 0f, 0f),
            center - new Vector3(halfX, 0f, 0f),
            center + new Vector3(0f, 0f, halfZ),
            center - new Vector3(0f, 0f, halfZ),
            center + new Vector3(halfX, 0f, halfZ),
            center + new Vector3(halfX, 0f, -halfZ),
            center + new Vector3(-halfX, 0f, halfZ),
            center + new Vector3(-halfX, 0f, -halfZ)
        };
    }

    private bool ShouldIgnoreAsGround(Vector3 samplePoint, Collider overlap)
    {
        if (!ignoreGroundLikeColliders)
        {
            return false;
        }

        Vector3 closestPoint = overlap.ClosestPoint(samplePoint);
        Vector3 difference = samplePoint - closestPoint;
        Vector3 horizontalDifference = new Vector3(difference.x, 0f, difference.z);

        bool mostlyVerticalContact = horizontalDifference.sqrMagnitude < 0.0001f;
        bool colliderIsShort = overlap.bounds.size.y <= groundLikeColliderMaxHeight;
        bool closestPointIsBelowSample = closestPoint.y <= samplePoint.y;

        return mostlyVerticalContact && colliderIsShort && closestPointIsBelowSample;
    }

    private Vector3 GetHorizontalPushDirection(Vector3 samplePoint, Vector3 bodyCenter, Collider overlap)
    {
        Vector3 closestPoint = overlap.ClosestPoint(samplePoint);
        Vector3 pushDirection = samplePoint - closestPoint;
        pushDirection.y = 0f;

        if (pushDirection.sqrMagnitude >= 0.0001f)
        {
            return pushDirection;
        }

        pushDirection = bodyCenter - overlap.bounds.center;
        pushDirection.y = 0f;

        if (pushDirection.sqrMagnitude >= 0.0001f)
        {
            return pushDirection;
        }

        pushDirection = transform.position - overlap.bounds.center;
        pushDirection.y = 0f;

        if (pushDirection.sqrMagnitude >= 0.0001f)
        {
            return pushDirection;
        }

        return -transform.forward;
    }

    private bool IsOwnCollider(Collider otherCollider)
    {
        if (otherCollider.transform == transform || otherCollider.transform.IsChildOf(transform))
        {
            return true;
        }

        if (colliders == null)
        {
            return false;
        }

        foreach (Collider ownCollider in colliders)
        {
            if (ownCollider == otherCollider)
            {
                return true;
            }
        }

        return false;
    }

    private void ConsumeDeathTickets()
    {
        if (!consumeTicketOnFullDeath)
        {
            return;
        }

        if (deathTicketCost <= 0)
        {
            return;
        }

        if (TeamTicketManager.Instance == null)
        {
            return;
        }

        PlayerTeam playerTeam = GetComponentInParent<PlayerTeam>();

        if (playerTeam == null)
        {
            return;
        }

        TeamTicketManager.Instance.ConsumeTickets(playerTeam.team, deathTicketCost, gameObject.name + " full death");
    }

    private IEnumerator RespawnAfterDelay()
    {
        yield return new WaitForSeconds(respawnDelay);

        Respawn();
    }

    private void Respawn()
    {
        if (bleedOutCoroutine != null)
        {
            StopCoroutine(bleedOutCoroutine);
            bleedOutCoroutine = null;
        }

        currentHealth = maxHealth;
        isDead = false;
        isDowned = false;
        bleedOutRemaining = 0f;

        RestoreOriginalPose();
        SetPlayerCollisionIgnored(false);
        RestoreMovementAfterDowned();
        SetPlayerTeamDownedState(false);
        SetTargetVisible(true);
        SetTargetColliders(true);

        Debug.Log(gameObject.name + " respawned.");
    }

    private void SetPlayerTeamDownedState(bool downed)
    {
        PlayerTeam playerTeam = GetComponentInParent<PlayerTeam>();

        if (playerTeam != null)
        {
            playerTeam.isDowned = downed;
        }
    }

    private void SetTargetVisible(bool visible)
    {
        foreach (Renderer targetRenderer in renderers)
        {
            if (targetRenderer != null)
            {
                targetRenderer.enabled = visible;
            }
        }
    }

    private void SetTargetColliders(bool enabled)
    {
        foreach (Collider targetCollider in colliders)
        {
            if (targetCollider != null)
            {
                targetCollider.enabled = enabled;
            }
        }
    }
}