using FishNet.Object;
using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(CharacterController))]
public class PlayerController : NetworkBehaviour
{
    [Header("References")]
    public Camera playerCamera;

    [Header("Movement")]
    public float walkSpeed = 3.15f;
    public float sprintSpeed = 4.55f;
    public float jumpHeight = 0.77f;
    public float gravity = -20f;

    [Header("Crouch")]
    [Range(0.3f, 0.9f)]
    public float crouchHeightMultiplier = 0.75f;
    [Range(0.1f, 1f)]
    public float crouchSpeedMultiplier = 0.5f;
    public float crouchCameraLerpSpeed = 10f;

    [Header("Weapon Movement")]
    public float weaponMoveSpeedMultiplier = 1f;

    [Header("Look")]
    public float mouseSensitivity = 0.12f;
    public float minPitch = -80f;
    public float maxPitch = 80f;

    private CharacterController characterController;
    private Vector3 verticalVelocity;
    private float environmentSlowMultiplier = 1f;
    private float environmentSlowUntil;
    private Vector2 currentMoveInput;
    private float pitch;
    private bool isMoving;
    private bool isSprinting;
    private float lastSprintEndTime = -999f;

    private bool isCrouching;
    private float standHeight;
    private Vector3 standCenter;
    private float standCameraLocalY;
    private bool crouchCached;

    public bool IsCrouching => isCrouching;

    public bool IsMoving => isMoving;
    public bool IsSprinting => isSprinting;
    public bool IsGrounded => characterController != null && characterController.isGrounded;
    public float LastSprintEndTime => lastSprintEndTime;
    public Vector2 CurrentMoveInput => currentMoveInput;
    private bool sprintToggled;

    public bool SprintInputHeld => GameSettings.ToggleSprint ? sprintToggled : GameSettings.Held(GameAction.Sprint);

    private void Awake()
    {
        characterController = GetComponent<CharacterController>();

        if (playerCamera == null)
        {
            playerCamera = GetComponentInChildren<Camera>();
        }
    }

    public override void OnStartClient()
    {
        base.OnStartClient();

        if (IsOwner)
        {
            LockCursor();
        }
    }

    private void Update()
    {
        if (!IsOwner)
        {
            return;
        }

        if (Keyboard.current == null || Mouse.current == null)
        {
            return;
        }

        // Pause menu owns the cursor and look while open; movement continues
        // (the game does not stop in multiplayer).
        if (!PauseMenu.IsOpen)
        {
            HandleLook();
            HandleCursor();
        }

        HandleMovement();
    }

    private void HandleLook()
    {
        Vector2 mouseDelta = Mouse.current.delta.ReadValue();

        transform.Rotate(Vector3.up * mouseDelta.x * mouseSensitivity);

        pitch -= mouseDelta.y * mouseSensitivity;
        pitch = Mathf.Clamp(pitch, minPitch, maxPitch);

        if (playerCamera != null)
        {
            playerCamera.transform.localRotation = Quaternion.Euler(pitch, 0f, 0f);
        }
    }

    private void HandleMovement()
    {
        bool grounded = characterController.isGrounded;

        if (grounded && verticalVelocity.y < 0f)
        {
            verticalVelocity.y = -2f;
        }

        Vector2 moveInput = Vector2.zero;

        if (GameSettings.Held(GameAction.MoveForward)) moveInput.y += 1f;
        if (GameSettings.Held(GameAction.MoveBackward)) moveInput.y -= 1f;
        if (GameSettings.Held(GameAction.MoveRight)) moveInput.x += 1f;
        if (GameSettings.Held(GameAction.MoveLeft)) moveInput.x -= 1f;

        moveInput = Vector2.ClampMagnitude(moveInput, 1f);

        currentMoveInput = moveInput;
        isMoving = currentMoveInput.sqrMagnitude > 0.01f;

        HandleCrouch(grounded);

        bool wasSprinting = isSprinting;
        bool sprintInputHeld;

        if (GameSettings.ToggleSprint)
        {
            // Tap to latch sprint; it drops when you stop moving or crouch.
            if (GameSettings.Pressed(GameAction.Sprint))
            {
                sprintToggled = !sprintToggled;
            }

            if (!isMoving || isCrouching)
            {
                sprintToggled = false;
            }

            sprintInputHeld = sprintToggled;
        }
        else
        {
            sprintToggled = false;
            sprintInputHeld = GameSettings.Held(GameAction.Sprint);
        }

        isSprinting = sprintInputHeld && isMoving && !isCrouching;

        if (wasSprinting && !isSprinting)
        {
            lastSprintEndTime = Time.time;
        }

        float currentSpeed = isSprinting ? sprintSpeed : walkSpeed;
        currentSpeed *= Mathf.Max(0f, weaponMoveSpeedMultiplier);

        if (isCrouching)
        {
            currentSpeed *= crouchSpeedMultiplier;
        }

        // Environmental slow (barbed wire). Re-applied every frame the player
        // stays inside a zone; expires on its own shortly after leaving.
        if (Time.time < environmentSlowUntil)
        {
            currentSpeed *= environmentSlowMultiplier;
        }

        Vector3 moveDirection = transform.right * moveInput.x + transform.forward * moveInput.y;

        if (grounded && !isCrouching && GameSettings.Pressed(GameAction.Jump))
        {
            verticalVelocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);
        }

        verticalVelocity.y += gravity * Time.deltaTime;

        Vector3 finalMovement = moveDirection * currentSpeed;
        finalMovement.y = verticalVelocity.y;

        characterController.Move(finalMovement * Time.deltaTime);
    }

    // Hold Ctrl to crouch: shorter capsule, lower camera, slower movement,
    // no sprint or jump. Standing back up requires headroom.
    private void HandleCrouch(bool grounded)
    {
        if (!crouchCached)
        {
            crouchCached = true;
            standHeight = characterController.height;
            standCenter = characterController.center;

            if (playerCamera != null)
            {
                standCameraLocalY = playerCamera.transform.localPosition.y;
            }
        }

        bool wantsCrouch = GameSettings.Held(GameAction.Crouch);

        if (wantsCrouch && !isCrouching)
        {
            isCrouching = true;

            float crouchHeight = standHeight * crouchHeightMultiplier;
            characterController.height = crouchHeight;
            characterController.center = standCenter - Vector3.up * (standHeight - crouchHeight) * 0.5f;
        }
        else if (!wantsCrouch && isCrouching && HasHeadroomToStand())
        {
            isCrouching = false;
            characterController.height = standHeight;
            characterController.center = standCenter;
        }

        if (playerCamera != null)
        {
            float targetY = isCrouching
                ? standCameraLocalY - standHeight * (1f - crouchHeightMultiplier)
                : standCameraLocalY;

            Vector3 cameraLocal = playerCamera.transform.localPosition;
            cameraLocal.y = Mathf.Lerp(cameraLocal.y, targetY, Time.deltaTime * crouchCameraLerpSpeed);
            playerCamera.transform.localPosition = cameraLocal;
        }
    }

    private bool HasHeadroomToStand()
    {
        float crouchHeight = characterController.height;
        Vector3 castStart = transform.position + characterController.center + Vector3.up * (crouchHeight * 0.5f - characterController.radius);
        float castDistance = (standHeight - crouchHeight) + 0.05f;

        return !Physics.SphereCast(castStart, characterController.radius * 0.9f, Vector3.up, out _,
            castDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
    }

    public void SetWeaponMoveSpeedMultiplier(float multiplier)
    {
        weaponMoveSpeedMultiplier = Mathf.Max(0f, multiplier);
    }

    public void ApplyEnvironmentSlow(float multiplier, float duration = 0.25f)
    {
        environmentSlowMultiplier = Mathf.Clamp01(multiplier);
        environmentSlowUntil = Time.time + duration;
    }

    public void AddCameraRecoil(float pitchKick, float yawKick)
    {
        pitch -= pitchKick;
        pitch = Mathf.Clamp(pitch, minPitch, maxPitch);

        transform.Rotate(Vector3.up * yawKick);

        if (playerCamera != null)
        {
            playerCamera.transform.localRotation = Quaternion.Euler(pitch, 0f, 0f);
        }
    }

    private void HandleCursor()
    {
        if (Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            UnlockCursor();
        }

        if (Mouse.current.leftButton.wasPressedThisFrame && Cursor.lockState != CursorLockMode.Locked)
        {
            LockCursor();
        }
    }

    private void LockCursor()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    private void UnlockCursor()
    {
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }
}