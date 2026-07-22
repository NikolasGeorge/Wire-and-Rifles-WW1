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

    [Range(0f, 1f)]
    [Tooltip("Horizontal move speed multiplier while airborne.")]
    public float airControlMultiplier = 0.75f;

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
    private PlayerSuppression suppression;

    [Header("Debug")]
    [Tooltip("F9 toggles a speedometer overlay for testing movement tuning.")]
    public bool speedometerEnabled;
    private float lastHorizontalSpeed;

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

        // Pause and build menus own the cursor and look while open; movement
        // continues (the game does not stop in multiplayer). Without this the
        // cursor re-locks the instant you click a build tile.
        if (!PauseMenu.IsOpen && !FortificationBuilder.MenuOpen)
        {
            HandleLook();
            HandleCursor();
        }

        HandleMovement();

        if (Keyboard.current.f9Key.wasPressedThisFrame)
        {
            speedometerEnabled = !speedometerEnabled;
        }
    }

    // Testing overlay: horizontal speed (what walk/sprint/duckboard/air
    // tuning actually changes), vertical speed, and ground state.
    private void OnGUI()
    {
        if (!IsOwner || !speedometerEnabled)
        {
            return;
        }

        GuiScale.Begin();

        GUIStyle style = new GUIStyle(GUI.skin.label)
        {
            fontSize = 20,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.UpperLeft
        };
        style.normal.textColor = Color.white;

        string text = lastHorizontalSpeed.ToString("0.00") + " m/s"
            + "\nvert " + verticalVelocity.y.ToString("0.00") + " m/s"
            + "\n" + (IsGrounded ? "grounded" : "airborne");

        GUI.Label(new Rect(16f, 16f, 260f, 90f), text, style);
    }

    private void HandleLook()
    {
        Vector2 mouseDelta = Mouse.current.delta.ReadValue();

        transform.Rotate(Vector3.up * mouseDelta.x * mouseSensitivity);

        pitch -= mouseDelta.y * mouseSensitivity;
        pitch = Mathf.Clamp(pitch, minPitch, maxPitch);

        if (playerCamera != null)
        {
            // Suppression shake is applied as an offset here, where the
            // camera rotation is authored from scratch each frame, so it
            // sways the actual aim without ever compounding.
            if (suppression == null)
            {
                suppression = GetComponent<PlayerSuppression>();
            }

            Vector3 shake = suppression != null ? suppression.CameraShakeEuler : Vector3.zero;
            playerCamera.transform.localRotation = Quaternion.Euler(pitch + shake.x, shake.y, shake.z);
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

        if (Time.time < environmentBoostUntil)
        {
            currentSpeed *= environmentBoostMultiplier;
        }

        // Airborne control is halved: you commit to a jump rather than
        // steering through it. Applied to horizontal movement only, so the
        // jump arc itself is untouched.
        if (!grounded)
        {
            currentSpeed *= airControlMultiplier;
        }

        Vector3 moveDirection = transform.right * moveInput.x + transform.forward * moveInput.y;

        // On a ladder gravity is suspended: look up and walk forward to
        // climb, look down to descend, jump to push off.
        if (IsOnLadder && !GameSettings.Pressed(GameAction.Jump))
        {
            float climb = moveInput.y * -Mathf.Sin(pitch * Mathf.Deg2Rad);

            // Holding forward while level with the ladder still creeps you
            // up, so players never get stuck partway.
            if (moveInput.y > 0f && Mathf.Abs(climb) < 0.2f)
            {
                climb = 0.2f;
            }

            verticalVelocity.y = climb * climbSpeed;

            Vector3 ladderMovement = moveDirection * (currentSpeed * 0.4f);
            ladderMovement.y = verticalVelocity.y;

            characterController.Move(ladderMovement * Time.deltaTime);
            lastHorizontalSpeed = new Vector3(ladderMovement.x, 0f, ladderMovement.z).magnitude;
            return;
        }

        if (grounded && !isCrouching && GameSettings.Pressed(GameAction.Jump))
        {
            verticalVelocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);
        }

        verticalVelocity.y += gravity * Time.deltaTime;

        Vector3 finalMovement = moveDirection * currentSpeed;
        finalMovement.y = verticalVelocity.y;

        characterController.Move(finalMovement * Time.deltaTime);
        lastHorizontalSpeed = new Vector3(finalMovement.x, 0f, finalMovement.z).magnitude;
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

    // Kept separate from the slow so wire and duck boards can apply at once
    // and multiply out, rather than one silently overwriting the other.
    private float environmentBoostMultiplier = 1f;
    private float environmentBoostUntil;

    public void ApplyEnvironmentSpeedBoost(float multiplier, float duration = 0.25f)
    {
        environmentBoostMultiplier = Mathf.Max(1f, multiplier);
        environmentBoostUntil = Time.time + duration;
    }

    [Header("Ladders")]
    public float climbSpeed = 3f;

    // Refreshed every frame the player is inside a ladder's climb volume;
    // expires on its own so stepping off a ladder needs no exit event.
    private float onLadderUntil;

    public void SetOnLadder()
    {
        onLadderUntil = Time.time + 0.2f;
    }

    public bool IsOnLadder => Time.time < onLadderUntil;

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