using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

public static class HitMarkerBuilder
{
    [MenuItem("Tools/Wire and Warfare/Create Hit Marker UI")]
    private static void CreateHitMarkerUI()
    {
        Canvas canvas = Object.FindFirstObjectByType<Canvas>();

        if (canvas == null)
        {
            GameObject canvasObject = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        }

        GameObject hitMarker = CreateRectObject("HitMarker", canvas.transform, Vector2.zero, Vector2.zero);
        hitMarker.AddComponent<CanvasGroup>();

        CreateDamageText(hitMarker.transform);

        GameObject bodyHitGroup = CreateRectObject("BodyHitGroup", hitMarker.transform, Vector2.zero, Vector2.zero);
        CreateLine(bodyHitGroup.transform, "TopLeft", new Vector2(-22f, 22f), 45f);
        CreateLine(bodyHitGroup.transform, "TopRight", new Vector2(22f, 22f), -45f);
        CreateLine(bodyHitGroup.transform, "BottomLeft", new Vector2(-22f, -22f), -45f);
        CreateLine(bodyHitGroup.transform, "BottomRight", new Vector2(22f, -22f), 45f);

        GameObject headshotHitGroup = CreateRectObject("HeadshotHitGroup", hitMarker.transform, Vector2.zero, Vector2.zero);
        CreateLine(headshotHitGroup.transform, "InnerTopLeft", new Vector2(-22f, 22f), 45f);
        CreateLine(headshotHitGroup.transform, "InnerTopRight", new Vector2(22f, 22f), -45f);
        CreateLine(headshotHitGroup.transform, "InnerBottomLeft", new Vector2(-22f, -22f), -45f);
        CreateLine(headshotHitGroup.transform, "InnerBottomRight", new Vector2(22f, -22f), 45f);
        CreateLine(headshotHitGroup.transform, "OuterTopLeft", new Vector2(-32f, 32f), 45f);
        CreateLine(headshotHitGroup.transform, "OuterTopRight", new Vector2(32f, 32f), -45f);
        CreateLine(headshotHitGroup.transform, "OuterBottomLeft", new Vector2(-32f, -32f), -45f);
        CreateLine(headshotHitGroup.transform, "OuterBottomRight", new Vector2(32f, -32f), 45f);

        GameObject killGroup = CreateRectObject("KillGroup", hitMarker.transform, Vector2.zero, Vector2.zero);
        CreateLine(killGroup.transform, "TopLeft", new Vector2(-44f, 44f), 45f);
        CreateLine(killGroup.transform, "TopRight", new Vector2(44f, 44f), -45f);
        CreateLine(killGroup.transform, "BottomLeft", new Vector2(-44f, -44f), -45f);
        CreateLine(killGroup.transform, "BottomRight", new Vector2(44f, -44f), 45f);

        GameObject headshotKillGroup = CreateRectObject("HeadshotKillGroup", hitMarker.transform, Vector2.zero, Vector2.zero);
        CreateLine(headshotKillGroup.transform, "InnerTopLeft", new Vector2(-44f, 44f), 45f);
        CreateLine(headshotKillGroup.transform, "InnerTopRight", new Vector2(44f, 44f), -45f);
        CreateLine(headshotKillGroup.transform, "InnerBottomLeft", new Vector2(-44f, -44f), -45f);
        CreateLine(headshotKillGroup.transform, "InnerBottomRight", new Vector2(44f, -44f), 45f);
        CreateLine(headshotKillGroup.transform, "OuterTopLeft", new Vector2(-56f, 56f), 45f);
        CreateLine(headshotKillGroup.transform, "OuterTopRight", new Vector2(56f, 56f), -45f);
        CreateLine(headshotKillGroup.transform, "OuterBottomLeft", new Vector2(-56f, -56f), -45f);
        CreateLine(headshotKillGroup.transform, "OuterBottomRight", new Vector2(56f, -56f), 45f);

        bodyHitGroup.SetActive(true);
        headshotHitGroup.SetActive(false);
        killGroup.SetActive(false);
        headshotKillGroup.SetActive(false);

        Selection.activeGameObject = hitMarker;
    }

    private static GameObject CreateRectObject(string objectName, Transform parent, Vector2 position, Vector2 size)
    {
        GameObject gameObject = new GameObject(objectName, typeof(RectTransform));
        gameObject.transform.SetParent(parent, false);

        RectTransform rectTransform = gameObject.GetComponent<RectTransform>();
        rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
        rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        rectTransform.pivot = new Vector2(0.5f, 0.5f);
        rectTransform.anchoredPosition = position;
        rectTransform.sizeDelta = size;
        rectTransform.localScale = Vector3.one;

        gameObject.layer = LayerMask.NameToLayer("UI");

        return gameObject;
    }

    private static void CreateLine(Transform parent, string objectName, Vector2 position, float rotationZ)
    {
        GameObject lineObject = CreateRectObject(objectName, parent, position, new Vector2(26f, 4f));

        Image image = lineObject.AddComponent<Image>();
        image.color = Color.white;
        image.raycastTarget = false;
        image.maskable = false;

        RectTransform rectTransform = lineObject.GetComponent<RectTransform>();
        rectTransform.localRotation = Quaternion.Euler(0f, 0f, rotationZ);
    }

    private static void CreateDamageText(Transform parent)
    {
        GameObject damageObject = CreateRectObject("DamageText", parent, new Vector2(-70f, 0f), new Vector2(60f, 30f));

        TextMeshProUGUI text = damageObject.AddComponent<TextMeshProUGUI>();
        text.text = "12";
        text.fontSize = 28f;
        text.alignment = TextAlignmentOptions.MidlineRight;
        text.color = Color.white;
        text.raycastTarget = false;
    }
}