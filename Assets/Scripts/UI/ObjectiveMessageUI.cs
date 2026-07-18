using System.Collections;
using TMPro;
using UnityEngine;

public class ObjectiveMessageUI : MonoBehaviour
{
    public PlayerTeam localPlayerTeam;
    public TMP_Text messageText;
    public CanvasGroup canvasGroup;

    [Header("Timing")]
    public float showTime = 1.4f;
    public float fadeTime = 0.35f;

    [Header("Colors")]
    public Color friendlyColor = new Color(0.1f, 0.45f, 1f, 1f);
    public Color enemyColor = new Color(1f, 0.15f, 0.1f, 1f);
    public Color neutralColor = Color.white;
    public Color contestedColor = new Color(1f, 0.85f, 0.15f, 1f);

    private Coroutine messageCoroutine;

    private void Awake()
    {
        if (localPlayerTeam == null)
        {
            localPlayerTeam = FindLocalPlayerTeam();
        }

        if (canvasGroup == null)
        {
            canvasGroup = GetComponent<CanvasGroup>();
        }

        if (canvasGroup != null)
        {
            canvasGroup.alpha = 0f;
        }

        if (messageText != null)
        {
            messageText.text = "";
        }
    }

    private void OnEnable()
    {
        ObjectiveCaptureZone.OnObjectiveEvent += HandleObjectiveEvent;
    }

    private void OnDisable()
    {
        ObjectiveCaptureZone.OnObjectiveEvent -= HandleObjectiveEvent;
    }

    private void HandleObjectiveEvent(ObjectiveCaptureZone objective, ObjectiveEventType eventType, Team relatedTeam)
    {
        if (objective == null)
        {
            return;
        }

        string message = BuildMessage(objective, eventType, relatedTeam);
        Color messageColor = GetMessageColor(eventType, relatedTeam);

        ShowMessage(message, messageColor);
    }

    private string BuildMessage(ObjectiveCaptureZone objective, ObjectiveEventType eventType, Team relatedTeam)
    {
        string letter = objective.objectiveLetter;

        switch (eventType)
        {
            case ObjectiveEventType.ContestedStarted:
                return "OBJECTIVE " + letter + " CONTESTED";

            case ObjectiveEventType.ContestedEnded:
                return "OBJECTIVE " + letter + " UNCONTESTED";

            case ObjectiveEventType.Neutralized:
                return "OBJECTIVE " + letter + " NEUTRALIZED";

            case ObjectiveEventType.Captured:
                if (localPlayerTeam != null && relatedTeam == localPlayerTeam.team)
                {
                    return "OBJECTIVE " + letter + " CAPTURED";
                }

                return "OBJECTIVE " + letter + " LOST";

            default:
                return "OBJECTIVE " + letter;
        }
    }

    private Color GetMessageColor(ObjectiveEventType eventType, Team relatedTeam)
    {
        if (eventType == ObjectiveEventType.ContestedStarted)
        {
            return contestedColor;
        }

        if (eventType == ObjectiveEventType.Neutralized || relatedTeam == Team.Neutral)
        {
            return neutralColor;
        }

        if (localPlayerTeam == null)
        {
            return neutralColor;
        }

        return relatedTeam == localPlayerTeam.team ? friendlyColor : enemyColor;
    }

    private void ShowMessage(string message, Color color)
    {
        if (messageText == null || canvasGroup == null)
        {
            return;
        }

        messageText.text = message;
        messageText.color = color;

        if (messageCoroutine != null)
        {
            StopCoroutine(messageCoroutine);
        }

        messageCoroutine = StartCoroutine(ShowMessageRoutine());
    }

    private IEnumerator ShowMessageRoutine()
    {
        canvasGroup.alpha = 1f;

        yield return new WaitForSeconds(showTime);

        float timer = 0f;

        while (timer < fadeTime)
        {
            timer += Time.deltaTime;
            float progress = Mathf.Clamp01(timer / fadeTime);

            canvasGroup.alpha = Mathf.Lerp(1f, 0f, progress);

            yield return null;
        }

        canvasGroup.alpha = 0f;

        if (messageText != null)
        {
            messageText.text = "";
        }

        messageCoroutine = null;
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