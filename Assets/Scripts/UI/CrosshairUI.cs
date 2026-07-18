using UnityEngine;

public class CrosshairUI : MonoBehaviour
{
    public BoltActionRifle rifle;
    public Camera playerCamera;
    public Canvas canvas;

    public RectTransform topLine;
    public RectTransform bottomLine;
    public RectTransform leftLine;
    public RectTransform rightLine;

    [Header("Center Indicator")]
    public GameObject normalCenterObject;
    public GameObject sprintCenterObject;
    public bool showSprintIndicatorDuringLockout = true;

    [Header("Aiming")]
    public bool hideCrosshairWhileAiming = true;

    [Header("Crosshair Shape")]
    public float minimumGap = 8f;
    public float maxGap = 250f;
    public float visualMultiplier = 1f;
    public float smoothSpeed = 20f;

    private float currentGap;

    private void Awake()
    {
        if (playerCamera == null && rifle != null)
        {
            playerCamera = rifle.playerCamera;
        }

        if (canvas == null)
        {
            canvas = GetComponentInParent<Canvas>();
        }

        currentGap = minimumGap;
    }

    private void Update()
    {
        if (rifle == null || playerCamera == null)
        {
            return;
        }

        if (hideCrosshairWhileAiming && rifle.isAiming)
        {
            SetLineVisibility(false);
            SetCenterObjectsVisible(false, false);
            return;
        }

        bool isSprinting = rifle.playerController != null && rifle.playerController.IsSprinting;
        bool isSprintFireLocked = rifle.IsSprintFireLocked;
        bool showSprintIndicator = showSprintIndicatorDuringLockout ? isSprintFireLocked : isSprinting;

        UpdateCenterIndicator(showSprintIndicator);
        SetLineVisibility(!isSprintFireLocked);

        if (isSprintFireLocked)
        {
            return;
        }

        float targetGap = CalculateGapFromAccuracy();
        currentGap = Mathf.Lerp(currentGap, targetGap, Time.deltaTime * smoothSpeed);

        if (topLine != null)
        {
            topLine.anchoredPosition = new Vector2(0f, currentGap);
        }

        if (bottomLine != null)
        {
            bottomLine.anchoredPosition = new Vector2(0f, -currentGap);
        }

        if (leftLine != null)
        {
            leftLine.anchoredPosition = new Vector2(-currentGap, 0f);
        }

        if (rightLine != null)
        {
            rightLine.anchoredPosition = new Vector2(currentGap, 0f);
        }
    }

    private void UpdateCenterIndicator(bool showSprintIndicator)
    {
        SetCenterObjectsVisible(!showSprintIndicator, showSprintIndicator);
    }

    private void SetCenterObjectsVisible(bool showNormal, bool showSprint)
    {
        if (normalCenterObject != null)
        {
            normalCenterObject.SetActive(showNormal);
        }

        if (sprintCenterObject != null)
        {
            sprintCenterObject.SetActive(showSprint);
        }
    }

    private float CalculateGapFromAccuracy()
    {
        float inaccuracyAngle = rifle.CurrentInaccuracyAngle;

        if (inaccuracyAngle <= 0f)
        {
            return minimumGap;
        }

        float angleRadians = inaccuracyAngle * Mathf.Deg2Rad;
        float halfVerticalFovRadians = playerCamera.fieldOfView * 0.5f * Mathf.Deg2Rad;
        float halfScreenHeight = Screen.height * 0.5f;

        float screenRadiusPixels = Mathf.Tan(angleRadians) / Mathf.Tan(halfVerticalFovRadians) * halfScreenHeight;

        float canvasScale = canvas != null ? canvas.scaleFactor : 1f;
        float uiGap = screenRadiusPixels / canvasScale;

        float finalGap = minimumGap + uiGap * visualMultiplier;

        return Mathf.Clamp(finalGap, minimumGap, maxGap);
    }

    private void SetLineVisibility(bool visible)
    {
        if (topLine != null)
        {
            topLine.gameObject.SetActive(visible);
        }

        if (bottomLine != null)
        {
            bottomLine.gameObject.SetActive(visible);
        }

        if (leftLine != null)
        {
            leftLine.gameObject.SetActive(visible);
        }

        if (rightLine != null)
        {
            rightLine.gameObject.SetActive(visible);
        }
    }
}