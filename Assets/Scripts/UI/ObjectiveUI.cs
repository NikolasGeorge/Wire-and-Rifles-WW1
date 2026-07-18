using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class ObjectiveUI : MonoBehaviour
{
    public ObjectiveCaptureZone objective;
    public PlayerTeam localPlayerTeam;

    [Header("Text")]
    public TMP_Text objectiveLetterText;
    public TMP_Text progressText;

    [Header("Radial Capture UI")]
    public Image ownershipBackground;
    public Image progressFill;

    [Header("Colors")]
    public Color friendlyColor = new Color(0.1f, 0.45f, 1f, 1f);
    public Color enemyColor = new Color(1f, 0.15f, 0.1f, 1f);
    public Color neutralColor = new Color(0.45f, 0.45f, 0.45f, 0.65f);

    private void Awake()
    {
        if (localPlayerTeam == null)
        {
            localPlayerTeam = FindLocalPlayerTeam();
        }

        SetupImages();
        EnsureTextRendersOnTop();
    }

    private void OnValidate()
    {
        EnsureTextRendersOnTop();
    }

    private void Update()
    {
        if (objective == null)
        {
            return;
        }

        float controlPercent = objective.ControlPercent;
        float absoluteProgress = Mathf.Abs(controlPercent) / 100f;

        if (objectiveLetterText != null)
        {
            objectiveLetterText.text = objective.objectiveLetter;
        }

        if (progressText != null)
        {
            int percent = Mathf.RoundToInt(Mathf.Abs(controlPercent));
            progressText.text = percent + "%";
        }

        if (ownershipBackground != null)
        {
            ownershipBackground.color = GetTeamColor(objective.controllingTeam);
        }

        if (progressFill != null)
        {
            progressFill.fillAmount = Mathf.Clamp01(absoluteProgress);
            progressFill.fillClockwise = controlPercent >= 0f;
            progressFill.color = GetTeamColor(GetTeamFromControlPercent(controlPercent));
        }

        EnsureTextRendersOnTop();
    }

    private void SetupImages()
    {
        if (ownershipBackground != null)
        {
            ownershipBackground.type = Image.Type.Simple;
            ownershipBackground.raycastTarget = false;
        }

        if (progressFill != null)
        {
            progressFill.type = Image.Type.Filled;
            progressFill.fillMethod = Image.FillMethod.Radial360;
            progressFill.fillOrigin = 2;
            progressFill.fillAmount = 0f;
            progressFill.raycastTarget = false;
        }
    }

    private void EnsureTextRendersOnTop()
    {
        if (objectiveLetterText != null)
        {
            objectiveLetterText.transform.SetAsLastSibling();
        }

        if (progressText != null)
        {
            progressText.transform.SetAsLastSibling();
        }
    }

    private Team GetTeamFromControlPercent(float percent)
    {
        if (percent > 0f)
        {
            return Team.AlliedPowers;
        }

        if (percent < 0f)
        {
            return Team.CentralPowers;
        }

        return Team.Neutral;
    }

    private Color GetTeamColor(Team team)
    {
        if (team == Team.Neutral)
        {
            return neutralColor;
        }

        if (localPlayerTeam == null)
        {
            return neutralColor;
        }

        return team == localPlayerTeam.team ? friendlyColor : enemyColor;
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