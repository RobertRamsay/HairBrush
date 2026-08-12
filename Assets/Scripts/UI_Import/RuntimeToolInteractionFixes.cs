using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// Runtime interaction polish for dynamically-created tool controls.
// The compact seed row is created entirely in code, so this explicitly handles
// pointer focus/clicks instead of relying on a fragile generated raycast hierarchy.
[DefaultExecutionOrder(1500)]
public class RuntimeToolInteractionFixes : MonoBehaviour
{
    private float nextScan;
    private ClumpLayerManager clumpManager;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        GameObject go = new GameObject("RuntimeToolInteractionFixes");
        DontDestroyOnLoad(go);
        go.AddComponent<RuntimeToolInteractionFixes>();
    }

    void Update()
    {
        if (Time.unscaledTime >= nextScan)
        {
            nextScan = Time.unscaledTime + 0.2f;
            InstallClumpFillButtons();
        }

        HandleSeedControls();
    }

    void HandleSeedControls()
    {
        if (Mouse.current == null) return;

        Vector2 mouse = Mouse.current.position.ReadValue();
        bool pressed = Mouse.current.leftButton.wasPressedThisFrame;

        TMP_InputField[] fields = FindObjectsByType<TMP_InputField>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
            .Where(f => f.gameObject.name == "SeedInput")
            .ToArray();

        foreach (TMP_InputField field in fields)
        {
            RectTransform rect = field.transform as RectTransform;
            if (rect == null) continue;

            bool hover = RectTransformUtility.RectangleContainsScreenPoint(rect, mouse, EventCamera(rect));
            Image image = field.GetComponent<Image>();
            if (image != null)
                image.color = field.isFocused
                    ? new Color(0.20f, 0.38f, 0.24f, 1f)
                    : hover ? new Color(0.24f, 0.30f, 0.22f, 1f) : new Color(0.12f, 0.12f, 0.12f, 1f);

            if (pressed && hover)
            {
                if (EventSystem.current != null)
                    EventSystem.current.SetSelectedGameObject(field.gameObject);
                field.Select();
                field.ActivateInputField();
                field.MoveTextEnd(false);
            }
        }

        Button[] randomButtons = FindObjectsByType<Button>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
            .Where(b => b.gameObject.name == "RButton" && b.transform.parent != null && b.transform.parent.name.EndsWith("_VarianceRow"))
            .ToArray();

        foreach (Button button in randomButtons)
        {
            RectTransform rect = button.transform as RectTransform;
            if (rect == null) continue;

            bool hover = RectTransformUtility.RectangleContainsScreenPoint(rect, mouse, EventCamera(rect));
            Image image = button.GetComponent<Image>();
            if (image != null)
                image.color = hover ? new Color(0.42f, 0.62f, 0.28f, 1f) : new Color(0.27f, 0.34f, 0.20f, 1f);

            if (pressed && hover)
            {
                button.onClick.Invoke();
                if (EventSystem.current != null)
                    EventSystem.current.SetSelectedGameObject(button.gameObject);
            }
        }
    }

    Camera EventCamera(RectTransform rect)
    {
        Canvas canvas = rect.GetComponentInParent<Canvas>();
        if (canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay) return null;
        return canvas.worldCamera;
    }

    void InstallClumpFillButtons()
    {
        if (clumpManager == null)
            clumpManager = FindFirstObjectByType<ClumpLayerManager>();
        if (clumpManager == null) return;

        RectTransform[] modifiers = FindObjectsByType<RectTransform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
            .Where(r => r.name.StartsWith("ClumpModifier_") && r.childCount > 1)
            .ToArray();

        foreach (RectTransform modifier in modifiers)
        {
            if (modifier.Find("FILL 1.0") != null) continue;
            if (!int.TryParse(modifier.name.Substring("ClumpModifier_".Length), out int groupId)) continue;

            GameObject buttonGO = new GameObject("FILL 1.0", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
            buttonGO.transform.SetParent(modifier, false);
            buttonGO.transform.SetSiblingIndex(Mathf.Min(4, modifier.childCount - 1));

            RectTransform br = buttonGO.GetComponent<RectTransform>();
            br.sizeDelta = new Vector2(0f, 28f);
            LayoutElement le = buttonGO.GetComponent<LayoutElement>();
            le.preferredHeight = 28f;
            le.minHeight = 28f;

            Image image = buttonGO.GetComponent<Image>();
            image.color = new Color(0.20f, 0.38f, 0.20f, 1f);

            Button button = buttonGO.GetComponent<Button>();
            ColorBlock colors = button.colors;
            colors.normalColor = new Color(0.20f, 0.38f, 0.20f, 1f);
            colors.highlightedColor = new Color(0.28f, 0.58f, 0.28f, 1f);
            colors.pressedColor = new Color(0.16f, 0.46f, 0.18f, 1f);
            colors.selectedColor = colors.highlightedColor;
            button.colors = colors;
            button.targetGraphic = image;

            GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
            textGO.transform.SetParent(buttonGO.transform, false);
            RectTransform tr = textGO.GetComponent<RectTransform>();
            tr.anchorMin = Vector2.zero;
            tr.anchorMax = Vector2.one;
            tr.offsetMin = Vector2.zero;
            tr.offsetMax = Vector2.zero;
            TextMeshProUGUI text = textGO.GetComponent<TextMeshProUGUI>();
            text.text = "FILL 1.0";
            text.fontSize = 12f;
            text.fontStyle = FontStyles.Bold;
            text.alignment = TextAlignmentOptions.Center;
            text.color = Color.white;
            text.raycastTarget = false;

            int capturedId = groupId;
            button.onClick.AddListener(() => FillClump(capturedId));
        }
    }

    void FillClump(int groupId)
    {
        if (clumpManager == null) return;

        Type managerType = typeof(ClumpLayerManager);
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        MethodInfo getLayer = managerType.GetMethod("GetOrCreateLayer", flags);
        MethodInfo regenerate = managerType.GetMethod("Regenerate", flags);
        MethodInfo apply = managerType.GetMethod("ApplyLayer", flags);
        MethodInfo refresh = managerType.GetMethod("RefreshGuideVisuals", flags);
        if (getLayer == null || apply == null) return;

        object layer = getLayer.Invoke(clumpManager, new object[] { groupId });
        if (layer == null) return;

        Type layerType = layer.GetType();
        FieldInfo pointsField = layerType.GetField("points", BindingFlags.Instance | BindingFlags.Public);
        FieldInfo enabledField = layerType.GetField("enabled", BindingFlags.Instance | BindingFlags.Public);
        FieldInfo pointCountField = layerType.GetField("pointCount", BindingFlags.Instance | BindingFlags.Public);

        IList points = pointsField?.GetValue(layer) as IList;
        if ((points == null || points.Count == 0) && regenerate != null)
        {
            int count = pointCountField != null ? (int)pointCountField.GetValue(layer) : 100;
            if (count > 0)
                regenerate.Invoke(clumpManager, new[] { layer });
            points = pointsField?.GetValue(layer) as IList;
        }

        if (points != null)
        {
            foreach (object point in points)
            {
                if (point == null) continue;
                FieldInfo strength = point.GetType().GetField("strength", BindingFlags.Instance | BindingFlags.Public);
                strength?.SetValue(point, 1f);
            }
        }

        enabledField?.SetValue(layer, true);
        apply.Invoke(clumpManager, new[] { layer });
        refresh?.Invoke(clumpManager, new[] { layer });
    }
}
