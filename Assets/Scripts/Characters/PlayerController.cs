using FishNet.Object;
using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(CharacterController))]
public class PlayerController : NetworkBehaviour
{
    [Header("References")]
    public Camera playerCamera;

    [Header("Movement")]
    public float walkSpeed = 4.5f;
    public float sprintSpeed = 6.5f;
    public float jumpHeight = 1.1f;
    public float gravity = -20f;

    [Header("Weapon Movement")]
    public float weaponMoveSpeedMultiplier = 1f;

    [Header("Look")]
    public float mouseSensitivity = 0.12f;
    public float minPitch = -80f;
    public float maxPitch = 80f;

    private CharacterController characterController;
    private Vector3 verticalVelocity;
    private Vector2 currentMoveInput;
    private float pitch;
    private bool isMoving;
    private bool isSprinting;
    private float lastSprintEndTime = -999f;

    public bool IsMoving => isMoving;
    public bool IsSprinting => isSprinting;
    public bool IsGrounded => characterController != null && characterController.isGrounded;
    public float LastSprintEndTime => lastSprintEndTime;
    public Vector2 CurrentMoveInput => currentMoveInput;
    public bool SprintInputHeld => Keyboard.current != null && (Keyboard.current.leftShiftKey.isPressed || Keyboard.current.rightShiftKey.isPressed);

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

        HandleLook();
        HandleMovement();
        HandleCursor();
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

        if (Keyboard.current.wKey.isPressed) moveInput.y += 1f;
        if (Keyboard.current.sKey.isPressed) moveInput.y -= 1f;
        if (Keyboard.current.dKey.isPressed) moveInput.x += 1f;
        if (Keyboard.current.aKey.isPressed) moveInput.x -= 1f;

        moveInput = Vector2.ClampMagnitude(moveInput, 1f);

        currentMoveInput = moveInput;
        isMoving = currentMoveInput.sqrMagnitude > 0.01f;

        bool wasSprinting = isSprinting;
        bool sprintInputHeld = Keyboard.current.leftShiftKey.isPressed || Keyboard.current.rightShiftKey.isPressed;

        isSprinting = sprintInputHeld && isMoving;

        if (wasSprinting && !isSprinting)
        {
            lastSprintEndTime = Time.time;
        }

        float currentSpeed = isSprinting ? sprintSpeed : walkSpeed;
        currentSpeed *= Mathf.Max(0f, weaponMoveSpeedMultiplier);

        Vector3 moveDirection = transform.right * moveInput.x + transform.forward * moveInput.y;

        if (grounded && Keyboard.current.spaceKey.wasPressedThisFrame)
        {
            verticalVelocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);
        }

        verticalVelocity.y += gravity * Time.deltaTime;

        Vector3 finalMovement = moveDirection * currentSpeed;
        finalMovement.y = verticalVelocity.y;

        characterController.Move(finalMovement * Time.deltaTime);
    }

    public void SetWeaponMoveSpeedMultiplier(float multiplier)
    {
        weaponMoveSpeedMultiplier = Mathf.Max(0f, multiplier);
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