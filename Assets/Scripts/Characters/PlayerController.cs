using FishNet.Object.Prediction;
using FishNet.Transporting;
using FishNet.Utility.Template;
using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(CharacterController))]
public class PlayerController : TickNetworkBehaviour
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

    [Header("Ladders")]
    public float climbSpeed = 3f;

    [Header("Debug")]
    [Tooltip("F9 toggles a speedometer overlay for testing movement tuning.")]
    public bool speedometerEnabled;

    // --- Prediction data types ---------------------------------------------

    public struct OneTimeInput
    {
        public bool Jump;

        public void ResetState()
        {
            Jump = false;
        }
    }

    public struct ReplicateData : IReplicateData
    {
        public ReplicateData(Vector2 moveInput, bool sprintHeld, bool crouchHeld, bool onLadder, float pitch, OneTimeInput oneTimeInputs)
        {
            MoveInput = moveInput;
            SprintHeld = sprintHeld;
            CrouchHeld = crouchHeld;
            OnLadder = onLadder;
            Pitch = pitch;
            OneTimeInputs = oneTimeInputs;

            _tick = 0;
        }

        public Vector2 MoveInput;
        public bool SprintHeld;
        public bool CrouchHeld;
        public bool OnLadder;
        public float Pitch;
        public OneTimeInput OneTimeInputs;

        private uint _tick;

        public void Dispose()
        {
            OneTimeInputs.ResetState();
        }

        public uint GetTick() => _tick;
        public void SetTick(uint value) => _tick = value;
    }

    public struct ReconcileData : IReconcileData
    {
        public ReconcileData(Vector3 position, float verticalVelocity, bool isCrouching, bool isSprinting, bool isMoving)
        {
            Position = position;
            VerticalVelocity = verticalVelocity;
            IsCrouching = isCrouching;
            IsSprinting = isSprinting;
            IsMoving = isMoving;

            _tick = 0;
        }

        public Vector3 Position;
        public float VerticalVelocity;
        public bool IsCrouching;
        public bool IsSprinting;
        public bool IsMoving;

        private uint _tick;

        public void Dispose() { }
        public uint GetTick() => _tick;
        public void SetTick(uint value) => _tick = value;
    }

    // -------------------------------------------------------------------

    private CharacterController characterController;
    private float verticalVelocity;
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

    private float lastHorizontalSpeed;

    private OneTimeInput oneTimeInputs;
    private ReplicateData lastTickedReplicateData;

    public bool SprintInputHeld => GameSettings.ToggleSprint ? sprintToggled : GameSettings.Held(GameAction.Sprint);

    private void Awake()
    {
        characterController = GetComponent<CharacterController>();

        if (playerCamera == null)
        {
            playerCamera = GetComponentInChildren<Camera>();
        }

        SetTickCallbacks(TickCallback.Tick | TickCallback.PostTick);
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

        // Crouch camera lerp is purely cosmetic and reads from replicated
        // isCrouching, so it can stay a plain per-frame Update concern.
        HandleCrouchCamera();

        if (!PauseMenu.IsOpen && !FortificationBuilder.MenuOpen
            && GameSettings.Pressed(GameAction.Jump))
        {
            oneTimeInputs.Jump = true;
        }

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
            + "\nvert " + verticalVelocity.ToString("0.00") + " m/s"
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

    protected override void TimeManager_OnTick()
    {
        PerformReplicate(BuildMoveData());
    }

    protected override void TimeManager_OnPostTick()
    {
        CreateReconcile();
    }

    private ReplicateData BuildMoveData()
    {
        if (!IsOwner)
        {
            return default;
        }

        Vector2 moveInput = Vector2.zero;

        if (GameSettings.Held(GameAction.MoveForward)) moveInput.y += 1f;
        if (GameSettings.Held(GameAction.MoveBackward)) moveInput.y -= 1f;
        if (GameSettings.Held(GameAction.MoveRight)) moveInput.x += 1f;
        if (GameSettings.Held(GameAction.MoveLeft)) moveInput.x -= 1f;

        moveInput = Vector2.ClampMagnitude(moveInput, 1f);

        bool crouchHeld = GameSettings.Held(GameAction.Crouch);

        // Sprint-toggle latching is a UI concern layered on top of the raw
        // held input, so it is resolved here (once per tick, owner only)
        // rather than inside the deterministic replicate body.
        bool sprintHeld;
        bool movingNow = moveInput.sqrMagnitude > 0.01f;

        if (GameSettings.ToggleSprint)
        {
            if (GameSettings.Pressed(GameAction.Sprint))
            {
                sprintToggled = !sprintToggled;
            }

            if (!movingNow || crouchHeld)
            {
                sprintToggled = false;
            }

            sprintHeld = sprintToggled;
        }
        else
        {
            sprintToggled = false;
            sprintHeld = GameSettings.Held(GameAction.Sprint);
        }

        ReplicateData md = new(moveInput, sprintHeld, crouchHeld, IsOnLadder, pitch, oneTimeInputs);

        oneTimeInputs.ResetState();

        return md;
    }

    public override void CreateReconcile()
    {
        ReconcileData rd = new(transform.position, verticalVelocity, isCrouching, isSprinting, isMoving);
        PerformReconcile(rd);
    }

    [Replicate]
    private void PerformReplicate(ReplicateData rd, ReplicateState state = ReplicateState.Invalid, Channel channel = Channel.Unreliable)
    {
        // Always use the tick delta as the timestep inside replicate, never
        // Time.deltaTime — replays run this method faster than real time.
        float delta = (float)TimeManager.TickDelta;
        bool useDefaultForces = false;

        if (!IsServerStarted && !IsOwner)
        {
            if (state.ContainsTicked())
            {
                lastTickedReplicateData.Dispose();
                lastTickedReplicateData = rd;
            }
            else if (state.IsFuture())
            {
                if (rd.GetTick() - lastTickedReplicateData.GetTick() > 1)
                {
                    useDefaultForces = true;
                }
                else
                {
                    rd.Dispose();
                    rd = lastTickedReplicateData;
                    // Jumping two ticks in a row is unlikely; don't predict it.
                    rd.OneTimeInputs.Jump = false;
                }
            }
        }

        currentMoveInput = rd.MoveInput;
        isMoving = currentMoveInput.sqrMagnitude > 0.01f;

        bool grounded = characterController.isGrounded;

        ApplyCrouchState(rd.CrouchHeld);

        bool wasSprinting = isSprinting;
        isSprinting = rd.SprintHeld && isMoving && !isCrouching;

        if (wasSprinting && !isSprinting)
        {
            lastSprintEndTime = Time.time;
        }

        Vector3 forces;

        if (useDefaultForces)
        {
            // Character controllers are problematic with colliders: passing
            // Vector3.zero risks other colliders clipping through, so apply
            // a very insignificant amount of force instead.
            forces = new Vector3(0f, -1f, 0f);
            lastHorizontalSpeed = 0f;
        }
        else if (rd.OnLadder && !rd.OneTimeInputs.Jump)
        {
            if (grounded && verticalVelocity < 0f)
            {
                verticalVelocity = -2f;
            }

            float climb = rd.MoveInput.y * -Mathf.Sin(rd.Pitch * Mathf.Deg2Rad);

            // Holding forward while level with the ladder still creeps you
            // up, so players never get stuck partway.
            if (rd.MoveInput.y > 0f && Mathf.Abs(climb) < 0.2f)
            {
                climb = 0.2f;
            }

            verticalVelocity = climb * climbSpeed;

            float ladderSpeed = ComputeCurrentSpeed(grounded) * 0.4f;
            Vector3 moveDirection = transform.right * rd.MoveInput.x + transform.forward * rd.MoveInput.y;
            Vector3 ladderMovement = moveDirection * ladderSpeed;
            ladderMovement.y = verticalVelocity;

            forces = ladderMovement;
            lastHorizontalSpeed = new Vector3(ladderMovement.x, 0f, ladderMovement.z).magnitude;
        }
        else
        {
            if (grounded && verticalVelocity < 0f)
            {
                verticalVelocity = -2f;
            }

            if (grounded && !isCrouching && rd.OneTimeInputs.Jump)
            {
                verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
            }

            verticalVelocity += gravity * delta;

            float currentSpeed = ComputeCurrentSpeed(grounded);
            Vector3 moveDirection = transform.right * rd.MoveInput.x + transform.forward * rd.MoveInput.y;
            Vector3 finalMovement = moveDirection * currentSpeed;
            finalMovement.y = verticalVelocity;

            forces = finalMovement;
            lastHorizontalSpeed = new Vector3(finalMovement.x, 0f, finalMovement.z).magnitude;
        }

        characterController.Move(forces * delta);
    }

    // Environmental/weapon speed modifiers are plain fields set from outside
    // (weapon ADS, wire/duckboard triggers) rather than replicated inputs —
    // acceptable minor prediction drift for a prototype, same tradeoff the
    // project already accepts for client-raycast hit detection.
    private float ComputeCurrentSpeed(bool grounded)
    {
        float currentSpeed = isSprinting ? sprintSpeed : walkSpeed;
        currentSpeed *= Mathf.Max(0f, weaponMoveSpeedMultiplier);

        if (isCrouching)
        {
            currentSpeed *= crouchSpeedMultiplier;
        }

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

        return currentSpeed;
    }

    [Reconcile]
    private void PerformReconcile(ReconcileData rd, Channel channel = Channel.Unreliable)
    {
        verticalVelocity = rd.VerticalVelocity;
        isCrouching = rd.IsCrouching;
        isSprinting = rd.IsSprinting;
        isMoving = rd.IsMoving;

        ApplyCrouchDimensions();

        // It is VERY important to disable the CharacterController before
        // repositioning it directly. Without this, the transform shows the
        // correct position but the controller's internal physics stay at
        // the prior position until the next simulate.
        characterController.enabled = false;
        transform.position = rd.Position;
        characterController.enabled = true;
    }

    // Hold Ctrl to crouch: shorter capsule, lower camera, slower movement,
    // no sprint or jump. Standing back up requires headroom.
    private void ApplyCrouchState(bool crouchHeld)
    {
        CacheStandDimensions();

        if (crouchHeld && !isCrouching)
        {
            isCrouching = true;
            ApplyCrouchDimensions();
        }
        else if (!crouchHeld && isCrouching && HasHeadroomToStand())
        {
            isCrouching = false;
            ApplyCrouchDimensions();
        }
    }

    private void ApplyCrouchDimensions()
    {
        CacheStandDimensions();

        if (isCrouching)
        {
            float crouchHeight = standHeight * crouchHeightMultiplier;
            characterController.height = crouchHeight;
            characterController.center = standCenter - Vector3.up * (standHeight - crouchHeight) * 0.5f;
        }
        else
        {
            characterController.height = standHeight;
            characterController.center = standCenter;
        }
    }

    private void CacheStandDimensions()
    {
        if (crouchCached)
        {
            return;
        }

        crouchCached = true;
        standHeight = characterController.height;
        standCenter = characterController.center;

        if (playerCamera != null)
        {
            standCameraLocalY = playerCamera.transform.localPosition.y;
        }
    }

    private void HandleCrouchCamera()
    {
        if (playerCamera == null)
        {
            return;
        }

        CacheStandDimensions();

        float targetY = isCrouching
            ? standCameraLocalY - standHeight * (1f - crouchHeightMultiplier)
            : standCameraLocalY;

        Vector3 cameraLocal = playerCamera.transform.localPosition;
        cameraLocal.y = Mathf.Lerp(cameraLocal.y, targetY, Time.deltaTime * crouchCameraLerpSpeed);
        playerCamera.transform.localPosition = cameraLocal;
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
