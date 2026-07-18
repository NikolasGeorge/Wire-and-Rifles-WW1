using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class RevivePromptUI : MonoBehaviour
{
    [Header("Root")]
    public GameObject promptRoot;

    [Header("Circle")]
    public Image circleBackground;
    public Image circleFill;
    public bool fillClockwise = true;

    [Header("Text")]
    public TMP_Text plusText;
    public TMP_Text statusText;

    [Header("Messages")]
    public string availableText = "HOLD E TO REVIVE";
    public string revivingTextFormat = "REVIVING {0}";
    public string gettingRevivedTextFormat = "GETTING REVIVED BY {0}";

    [Header("Colors")]
    public Color backgroundColor = new Color(0.1f, 0.1f, 0.1f, 0.6f);
    public Color fillColor = new Color(0.45f, 1f, 0.25f, 1f);
    public Color plusColor = new Color(0.45f, 1f, 0.25f, 1f);
    public Color textColor = new Color(0.45f, 1f, 0.25f, 1f);

    private void Awake()
    {
        if (promptRoot == null)
        {
            promptRoot = gameObject;
        }

        SetupImages();
        Hide();
    }

    public void ShowAvailablePrompt()
    {
        Show(0f, availableText);
    }

    public void ShowReviving(float progress01, string targetName)
    {
        string message = string.Format(revivingTextFormat, targetName);
        Show(progress01, message);
    }

    public void ShowGettingRevivedBy(float progress01, string reviverName)
    {
        string message = string.Format(gettingRevivedTextFormat, reviverName);
        Show(progress01, message);
    }

    public void Hide()
    {
        if (promptRoot != null)
        {
            promptRoot.SetActive(false);
        }

        if (circleFill != null)
        {
            circleFill.fillAmount = 0f;
        }
    }

    private void Show(float progress01, string message)
    {
        progress01 = Mathf.Clamp01(progress01);

        if (promptRoot != null)
        {
            promptRoot.SetActive(true);
        }

        if (circleBackground != null)
        {
            circleBackground.color = backgroundColor;
        }

        if (circleFill != null)
        {
            circleFill.color = fillColor;
            circleFill.fillClockwise = fillClockwise;
            circleFill.fillAmount = progress01;
        }

        if (plusText != null)
        {
            plusText.text = "+";
            plusText.color = plusColor;
        }

        if (statusText != null)
        {
            statusText.text = message;
            statusText.color = textColor;
        }
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
            circleFill.fillAmount = 0f;
            circleFill.raycastTarget = false;
        }
    }
}