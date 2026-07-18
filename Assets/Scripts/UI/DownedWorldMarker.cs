using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class DownedWorldMarker : MonoBehaviour
{
    public HealthComponent health;
    public PlayerTeam targetTeam;
    public PlayerTeam localPlayerTeam;
    public Camera playerCamera;

    [Header("Marker Root")]
    public GameObject markerRoot;

    [Header("Marker Anchor")]
    public Transform markerAnchor;
    public Vector3 anchorOffset = new Vector3(0f, 0.05f, 0f);

    [Header("Circle")]
    public Image circleBackground;
    public Image circleFill;
    public bool fillClockwise = false;

    [Header("Text")]
    public TMP_Text plusText;

    [Header("Visibility")]
    public bool showOnlyFriendlies = true;
    public float maxVisibleDistance = 60f;

    [Header("Background Colors")]
    public Color backgroundColor = new Color(0.15f, 0.15f, 0.15f, 0.65f);

    [Header("Drain Colors")]
    public Color friendlyDrainColor = new Color(0.1f, 0.45f, 1f, 1f);
    public Color enemyDrainColor = new Color(1f, 0.15f, 0.1f, 1f);

    [Header("Plus Colors")]
    public Color friendlyPlusColor = Color.white;
    public Color enemyPlusColor = Color.white;

    private void Awake()
    {
        if (health == null)
        {
            health = GetComponentInParent<HealthComponent>();
        }

        if (targetTeam == null)
        {
            targetTeam = GetComponentInParent<PlayerTeam>();
        }

        if (localPlayerTeam == null)
        {
            localPlayerTeam = FindLocalPlayerTeam();
        }

        if (playerCamera == null)
        {
            playerCamera = Camera.main;
        }

        SetupImages();

        if (markerRoot != null)
        {
            markerRoot.SetActive(false);
        }
    }

    private void Update()
    {
        if (health == null || markerRoot == null)
        {
            return;
        }

        if (playerCamera == null)
        {
            playerCamera = Camera.main;
        }

        bool shouldShow = ShouldShowMarker();
        markerRoot.SetActive(shouldShow);

        if (!shouldShow)
        {
            return;
        }

        UpdateMarkerPosition();
        UpdateCircle();
        UpdateText();
        UpdateColors();
    }

    private void SetupImages()
    {
        if (circleBackground != null)
        {
            circleBackground.type = Image.Type.Simple;
            circleBackground.color = backgroundColor;
            circleBackground.raycastTarget = false;
        }

        if (circleFill != null)
        {
            circleFill.type = Image.Type.Filled;
            circleFill.fillMethod = Image.FillMethod.Radial360;
            circleFill.fillOrigin = 2;
            circleFill.fillClockwise = fillClockwise;
            circleFill.fillAmount = 1f;
            circleFill.raycastTarget = false;
        }
    }

    private bool ShouldShowMarker()
    {
        if (!health.IsDowned || health.IsDead)
        {
            return false;
        }

        if (playerCamera == null)
        {
            return false;
        }

        if (targetTeam == null || targetTeam.team == Team.Neutral)
        {
            return false;
        }

        if (showOnlyFriendlies)
        {
            if (localPlayerTeam == null)
            {
                return false;
            }

            if (targetTeam.team != localPlayerTeam.team)
            {
                return false;
            }
        }

        Vector3 markerPosition = GetMarkerWorldPosition();
        float distance = Vector3.Distance(playerCamera.transform.position, markerPosition);

        return distance <= maxVisibleDistance;
    }

    private void UpdateMarkerPosition()
    {
        transform.position = GetMarkerWorldPosition();

        Vector3 directionToCamera = transform.position - playerCamera.transform.position;

        if (directionToCamera.sqrMagnitude > 0.001f)
        {
            transform.rotation = Quaternion.LookRotation(directionToCamera.normalized);
        }
    }

    private Vector3 GetMarkerWorldPosition()
    {
        if (markerAnchor != null)
        {
            return markerAnchor.position + anchorOffset;
        }

        return health.transform.position + anchorOffset;
    }

    private void UpdateCircle()
    {
        if (circleFill == null)
        {
            return;
        }

        circleFill.fillClockwise = fillClockwise;
        circleFill.fillAmount = health.BleedOutProgress01;
    }

    private void UpdateText()
    {
        if (plusText != null)
        {
            plusText.text = "+";
        }
    }

    private void UpdateColors()
    {
        bool isFriendly = IsFriendly();

        if (circleBackground != null)
        {
            circleBackground.color = backgroundColor;
        }

        if (circleFill != null)
        {
            circleFill.color = isFriendly ? friendlyDrainColor : enemyDrainColor;
        }

        if (plusText != null)
        {
            plusText.color = isFriendly ? friendlyPlusColor : enemyPlusColor;
        }
    }

    private bool IsFriendly()
    {
        if (localPlayerTeam == null || targetTeam == null)
        {
            return true;
        }

        return localPlayerTeam.team == targetTeam.team;
    }

    private PlayerTeam FindLocalPlayerTeam()
    {
        PlayerController playerController = FindAnyObjectByType<PlayerController>();

        if (playerController != null)
        {
            PlayerTeam playerTeam = playerController.GetComponentInParent<PlayerTeam>();

            if (playerTeam != null)
            {
                return playerTeam;
            }
        }

        GameObject playerObject = GameObject.FindGameObjectWithTag("Player");

        if (playerObject != null)
        {
            return playerObject.GetComponentInParent<PlayerTeam>();
        }

        return null;
    }
}