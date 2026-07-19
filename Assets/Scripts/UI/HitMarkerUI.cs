using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class HitMarkerUI : MonoBehaviour
{
    public GameObject hitMarkerObject;
    public CanvasGroup canvasGroup;
    public TMP_Text damageText;

    [Header("Line Groups")]
    public GameObject bodyHitGroup;
    public GameObject headshotHitGroup;
    public GameObject killGroup;
    public GameObject headshotKillGroup;

    [Header("Audio")]
    public AudioSource audioSource;
    public AudioClip killSound;
    public AudioClip headshotKillSound;
    [Range(0f, 1f)]
    public float killSoundVolume = 1f;
    [Range(0f, 1f)]
    public float headshotKillSoundVolume = 1f;

    [Header("Timing")]
    public float showTime = 0.08f;
    public float fadeTime = 0.18f;

    [Header("Kill Animation Only")]
    public float killStartScale = 1.18f;
    public float killEndScale = 1f;
    public float killFadeOutScale = 1.12f;

    [Header("Damage")]
    public bool roundDamage = true;

    [Tooltip("Hits arriving within this window (e.g. shotgun pellets landing the same frame) are summed into one marker instead of flickering separately.")]
    public float damageBufferWindow = 0.05f;

    [Header("Colors")]
    public Color normalHitColor = Color.white;
    public Color killHitColor = Color.red;

    private readonly Dictionary<GameObject, List<LineData>> cachedLines = new Dictionary<GameObject, List<LineData>>();
    private Coroutine animationCoroutine;
    private GameObject activeGroup;
    private bool activeHitIsKill;

    private Coroutine bufferCoroutine;
    private float bufferedDamage;
    private bool bufferedIsKill;
    private bool bufferedIsHeadshot;

    private class LineData
    {
        public RectTransform rectTransform;
        public Vector2 originalPosition;
        public Image image;
    }

    private void Awake()
    {
        if (canvasGroup == null && hitMarkerObject != null)
        {
            canvasGroup = hitMarkerObject.GetComponent<CanvasGroup>();
        }

        if (audioSource == null)
        {
            audioSource = GetComponent<AudioSource>();
        }

        if (audioSource == null)
        {
            audioSource = gameObject.AddComponent<AudioSource>();
        }

        audioSource.playOnAwake = false;
        audioSource.spatialBlend = 0f;

        CacheGroup(bodyHitGroup);
        CacheGroup(headshotHitGroup);
        CacheGroup(killGroup);
        CacheGroup(headshotKillGroup);

        SetAllGroupsVisible(false);
        ResetAllGroupPositions();

        if (hitMarkerObject != null)
        {
            hitMarkerObject.SetActive(false);
        }
    }

    public void ShowHitMarker()
    {
        ShowHitMarker(0f, false, false);
    }

    public void ShowHitMarker(float damageAmount)
    {
        ShowHitMarker(damageAmount, false, false);
    }

    public void ShowHitMarker(float damageAmount, bool isKill)
    {
        ShowHitMarker(damageAmount, isKill, false);
    }

    public void ShowHitMarker(float damageAmount, bool isKill, bool isHeadshot)
    {
        if (hitMarkerObject == null)
        {
            return;
        }

        // Accumulate into a short buffer so a shotgun blast's pellets, which
        // each resolve their own hit independently and can land in different
        // frames, are reported as one combined-damage marker instead of
        // several flickering ones.
        bufferedDamage += damageAmount;
        bufferedIsKill |= isKill;
        bufferedIsHeadshot |= isHeadshot;

        if (bufferCoroutine == null)
        {
            bufferCoroutine = StartCoroutine(FlushBufferedHit());
        }
    }

    private IEnumerator FlushBufferedHit()
    {
        yield return new WaitForSeconds(damageBufferWindow);

        float damageAmount = bufferedDamage;
        bool isKill = bufferedIsKill;
        bool isHeadshot = bufferedIsHeadshot;

        bufferedDamage = 0f;
        bufferedIsKill = false;
        bufferedIsHeadshot = false;
        bufferCoroutine = null;

        DisplayHitMarker(damageAmount, isKill, isHeadshot);
    }

    private void DisplayHitMarker(float damageAmount, bool isKill, bool isHeadshot)
    {
        activeGroup = GetGroupForHit(isKill, isHeadshot);
        activeHitIsKill = isKill;

        if (activeGroup == null)
        {
            return;
        }

        Color markerColor = isKill ? killHitColor : normalHitColor;

        SetAllGroupsVisible(false);
        ResetAllGroupPositions();

        activeGroup.SetActive(true);

        SetDamageText(damageAmount, markerColor);
        SetGroupColor(activeGroup, markerColor);

        if (activeHitIsKill)
        {
            SetGroupScale(activeGroup, killStartScale);
        }
        else
        {
            SetGroupScale(activeGroup, 1f);
        }

        hitMarkerObject.SetActive(true);

        if (canvasGroup != null)
        {
            canvasGroup.alpha = 1f;
        }

        PlayKillSound(isKill, isHeadshot);

        if (animationCoroutine != null)
        {
            StopCoroutine(animationCoroutine);
        }

        animationCoroutine = StartCoroutine(AnimateHitMarker());
    }

    private void PlayKillSound(bool isKill, bool isHeadshot)
    {
        if (!isKill || audioSource == null)
        {
            return;
        }

        if (isHeadshot && headshotKillSound != null)
        {
            audioSource.PlayOneShot(headshotKillSound, headshotKillSoundVolume);
            return;
        }

        if (killSound != null)
        {
            audioSource.PlayOneShot(killSound, killSoundVolume);
        }
    }

    private GameObject GetGroupForHit(bool isKill, bool isHeadshot)
    {
        if (isKill && isHeadshot)
        {
            return headshotKillGroup;
        }

        if (isKill)
        {
            return killGroup;
        }

        if (isHeadshot)
        {
            return headshotHitGroup;
        }

        return bodyHitGroup;
    }

    private IEnumerator AnimateHitMarker()
    {
        float timer = 0f;

        while (timer < showTime)
        {
            timer += Time.deltaTime;
            float progress = Mathf.Clamp01(timer / showTime);

            if (activeHitIsKill)
            {
                float scale = Mathf.Lerp(killStartScale, killEndScale, progress);
                SetGroupScale(activeGroup, scale);
            }
            else
            {
                SetGroupScale(activeGroup, 1f);
            }

            yield return null;
        }

        timer = 0f;

        while (timer < fadeTime)
        {
            timer += Time.deltaTime;
            float progress = Mathf.Clamp01(timer / fadeTime);

            if (canvasGroup != null)
            {
                canvasGroup.alpha = Mathf.Lerp(1f, 0f, progress);
            }

            if (activeHitIsKill)
            {
                float scale = Mathf.Lerp(killEndScale, killFadeOutScale, progress);
                SetGroupScale(activeGroup, scale);
            }
            else
            {
                SetGroupScale(activeGroup, 1f);
            }

            yield return null;
        }

        SetAllGroupsVisible(false);
        ResetAllGroupPositions();

        if (hitMarkerObject != null)
        {
            hitMarkerObject.SetActive(false);
        }

        if (canvasGroup != null)
        {
            canvasGroup.alpha = 1f;
        }

        animationCoroutine = null;
    }

    private void CacheGroup(GameObject group)
    {
        if (group == null || cachedLines.ContainsKey(group))
        {
            return;
        }

        List<LineData> lines = new List<LineData>();
        Image[] images = group.GetComponentsInChildren<Image>(true);

        foreach (Image image in images)
        {
            RectTransform rectTransform = image.GetComponent<RectTransform>();

            if (rectTransform == null)
            {
                continue;
            }

            LineData lineData = new LineData
            {
                rectTransform = rectTransform,
                originalPosition = rectTransform.anchoredPosition,
                image = image
            };

            lines.Add(lineData);
        }

        cachedLines.Add(group, lines);
    }

    private void SetDamageText(float damageAmount, Color markerColor)
    {
        if (damageText == null)
        {
            return;
        }

        damageText.color = markerColor;

        if (damageAmount <= 0f)
        {
            damageText.text = "";
            return;
        }

        damageText.text = roundDamage ? Mathf.RoundToInt(damageAmount).ToString() : damageAmount.ToString("0.0");
    }

    private void SetGroupColor(GameObject group, Color markerColor)
    {
        if (group == null || !cachedLines.ContainsKey(group))
        {
            return;
        }

        foreach (LineData line in cachedLines[group])
        {
            if (line.image != null)
            {
                line.image.color = markerColor;
            }
        }
    }

    private void SetGroupScale(GameObject group, float scale)
    {
        if (group == null || !cachedLines.ContainsKey(group))
        {
            return;
        }

        foreach (LineData line in cachedLines[group])
        {
            if (line.rectTransform != null)
            {
                line.rectTransform.anchoredPosition = line.originalPosition * scale;
            }
        }
    }

    private void ResetAllGroupPositions()
    {
        ResetGroupPosition(bodyHitGroup);
        ResetGroupPosition(headshotHitGroup);
        ResetGroupPosition(killGroup);
        ResetGroupPosition(headshotKillGroup);
    }

    private void ResetGroupPosition(GameObject group)
    {
        SetGroupScale(group, 1f);
    }

    private void SetAllGroupsVisible(bool visible)
    {
        SetGroupVisible(bodyHitGroup, visible);
        SetGroupVisible(headshotHitGroup, visible);
        SetGroupVisible(killGroup, visible);
        SetGroupVisible(headshotKillGroup, visible);
    }

    private void SetGroupVisible(GameObject group, bool visible)
    {
        if (group != null)
        {
            group.SetActive(visible);
        }
    }
}