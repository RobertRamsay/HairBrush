using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using TMPro;

public class ClumpLayerManager : MonoBehaviour
{
    public enum DebugMode { Tone, Length, Off }

    [Serializable]
    public class ClumpPoint
    {
        public Vector3 position;
        public Vector3 normal;
        [Range(0f, 1f)] public float strength;
    }

    [Serializable]
    public class ClumpLayer
    {
        public int groupId;
        public bool attached;
        public bool enabled = true;
        public int pointCount = 100;
        [Range(0f, 1f)] public float globalStrength = 1f;
        public float brushRadius = 0.08f;
        public float guideLength = 0.04f;
        public DebugMode debugMode = DebugMode.Tone;
        public AnimationCurve curve = new AnimationCurve(
            new Keyframe(0f, 0f),
            new Keyframe(0.25f, 0.08f),
            new Keyframe(0.65f, 0.65f),
            new Keyframe(1f, 1f));
        public List<ClumpPoint> points = new List<ClumpPoint>();
    }

    private readonly Dictionary<int, ClumpLayer> layers = new Dictionary<int, ClumpLayer>();
    private readonly List<LineRenderer> guideLines = new List<LineRenderer>();

    private ModelViewer viewer;
    private Camera mainCamera;
    private GameObject panel;
    private GameObject curvePanel;
    private GameObject guideRoot;
    private Material guideMaterial;

    private TextMeshProUGUI titleText;
    private TextMeshProUGUI debugText;
    private TextMeshProUGUI paintText;
    private TextMeshProUGUI attachText;
    private TextMeshProUGUI enabledText;

    private Slider strengthSlider;
    private Slider brushSlider;
    private Slider curveEarlySlider;
    private Slider curveMidSlider;
    private Slider curveTipSlider;

    private bool paintMode;
    private float paintValue = 1f;
    private int editingGroupId = -1;

    public void Init(ModelViewer owner)
    {
        if (panel != null) return;
        viewer = owner;
        mainCamera = owner.mainCamera != null ? owner.mainCamera : Camera.main;
        BuildUI();
        panel.SetActive(false);
        if (curvePanel != null) curvePanel.SetActive(false);
    }

    public void ToggleForCurrentGroup()
    {
        if (viewer == null || panel == null) return;

        if (panel.activeSelf)
        {
            ClosePanel();
            return;
        }

        editingGroupId = viewer.currentGroupId;
        ClumpLayer layer = GetOrCreateLayer(editingGroupId);
        if (layer.points.Count == 0) Regenerate(layer);

        panel.SetActive(true);
        titleText.text = "CLUMP LAYER — GROUP " + editingGroupId;
        SyncUI(layer);

        // Opening a layer is an edit/preview session. The hair shows the result immediately,
        // but a brand-new layer is not owned by the group until ADD TO GROUP is pressed.
        ApplyLayer(layer, true);
        RefreshGuideVisuals(layer);
    }

    void Update()
    {
        if (panel == null || !panel.activeSelf || viewer == null || editingGroupId < 0) return;
        ClumpLayer layer = GetOrCreateLayer(editingGroupId);
        RefreshGuideVisuals(layer);

        if (!paintMode || Mouse.current == null || !Mouse.current.leftButton.isPressed) return;
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;
        if (mainCamera == null) return;

        Ray ray = mainCamera.ScreenPointToRay(Mouse.current.position.ReadValue());
        RaycastHit[] hits = Physics.RaycastAll(ray, 1000f);
        RaycastHit? modelHit = hits
            .Where(h => h.collider.GetComponent<HairCard>() == null)
            .OrderBy(h => h.distance)
            .Cast<RaycastHit?>()
            .FirstOrDefault();

        if (!modelHit.HasValue) return;
        Paint(layer, modelHit.Value.point, paintValue);
    }

    ClumpLayer GetOrCreateLayer(int groupId)
    {
        if (!layers.TryGetValue(groupId, out ClumpLayer layer))
        {
            layer = new ClumpLayer { groupId = groupId };
            layers[groupId] = layer;
        }
        return layer;
    }

    void Regenerate(ClumpLayer layer)
    {
        HairCard[] cards = FindObjectsByType<HairCard>(FindObjectsSortMode.None)
            .Where(c => c.groupId == layer.groupId)
            .ToArray();

        layer.points.Clear();
        if (cards.Length == 0)
        {
            ClearGuides();
            return;
        }

        int count = Mathf.Max(1, layer.pointCount);
        for (int i = 0; i < count; i++)
        {
            HairCard seed = cards[i % cards.Length];
            Vector3 p = seed.GetSpawnHitPoint();
            Vector3 n = seed.GetSurfaceNormal().normalized;

            if (cards.Length > 1)
            {
                HairCard other = cards[(i * 17 + 7) % cards.Length];
                float blend = Mathf.Repeat(i * 0.6180339f, 1f);
                p = Vector3.Lerp(p, other.GetSpawnHitPoint(), blend);
                n = Vector3.Slerp(n, other.GetSurfaceNormal().normalized, blend).normalized;

                // The interpolation above can float slightly off the scalp. Project it back
                // onto the actual imported collision surface before storing the clump point.
                RaycastHit[] hits = Physics.RaycastAll(p + n * 0.12f, -n, 0.30f);
                RaycastHit? surfaceHit = hits
                    .Where(h => h.collider.GetComponent<HairCard>() == null)
                    .OrderBy(h => h.distance)
                    .Cast<RaycastHit?>()
                    .FirstOrDefault();
                if (surfaceHit.HasValue)
                {
                    p = surfaceHit.Value.point;
                    n = surfaceHit.Value.normal.normalized;
                }
            }

            layer.points.Add(new ClumpPoint
            {
                position = p,
                normal = n,
                strength = 0f
            });
        }

        ApplyLayer(layer, true);
        RefreshGuideVisuals(layer);
    }

    void Paint(ClumpLayer layer, Vector3 center, float value)
    {
        float radius = Mathf.Max(0.001f, layer.brushRadius);
        foreach (ClumpPoint point in layer.points)
        {
            float d = Vector3.Distance(center, point.position);
            if (d > radius) continue;
            float falloff = 1f - Mathf.Clamp01(d / radius);
            point.strength = Mathf.Lerp(point.strength, value, falloff * 0.25f);
        }

        ApplyLayer(layer, true);
        RefreshGuideVisuals(layer);
    }

    void ApplyLayer(ClumpLayer layer, bool preview)
    {
        HairCard[] cards = FindObjectsByType<HairCard>(FindObjectsSortMode.None)
            .Where(c => c.groupId == layer.groupId)
            .ToArray();

        bool shouldApply = layer.enabled && layer.points.Count > 0 && (layer.attached || preview);
        if (!shouldApply)
        {
            foreach (HairCard card in cards) card.ClearClumpModifier();
            return;
        }

        foreach (HairCard card in cards)
        {
            ClumpPoint nearest = layer.points
                .OrderBy(p => Vector3.SqrMagnitude(card.GetSpawnHitPoint() - p.position))
                .First();

            card.SetClumpModifier(
                nearest.position,
                nearest.normal,
                nearest.strength * layer.globalStrength,
                layer.curve);
        }
    }

    void AttachCurrentLayer()
    {
        if (editingGroupId < 0) return;
        ClumpLayer layer = GetOrCreateLayer(editingGroupId);
        layer.attached = true;
        layer.enabled = true;
        ApplyLayer(layer, false);
        SyncUI(layer);
    }

    void RemoveCurrentLayer()
    {
        if (editingGroupId < 0) return;
        ClumpLayer layer = GetOrCreateLayer(editingGroupId);
        layer.attached = false;
        layer.enabled = false;
        ApplyLayer(layer, false);
        SyncUI(layer);
    }

    void ToggleEnabled()
    {
        if (editingGroupId < 0) return;
        ClumpLayer layer = GetOrCreateLayer(editingGroupId);
        layer.enabled = !layer.enabled;
        ApplyLayer(layer, true);
        SyncUI(layer);
    }

    void ClosePanel()
    {
        paintMode = false;
        if (paintText != null) paintText.text = "PAINT: OFF";
        if (curvePanel != null) curvePanel.SetActive(false);

        if (editingGroupId >= 0)
        {
            ClumpLayer layer = GetOrCreateLayer(editingGroupId);
            // A draft layer is preview-only. Closing without ADD TO GROUP restores the groom.
            ApplyLayer(layer, false);
        }

        ClearGuides();
        panel.SetActive(false);
        editingGroupId = -1;
    }

    void EnsureGuidePool(int count)
    {
        if (guideRoot == null)
        {
            guideRoot = new GameObject("ClumpGuideVisuals");
            guideRoot.transform.SetParent(transform, false);
        }

        if (guideMaterial == null)
        {
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader != null) guideMaterial = new Material(shader) { name = "ClumpGuideRuntimeMaterial" };
        }

        while (guideLines.Count < count)
        {
            GameObject go = new GameObject("ClumpGuide_" + guideLines.Count);
            go.transform.SetParent(guideRoot.transform, false);
            LineRenderer line = go.AddComponent<LineRenderer>();
            line.positionCount = 2;
            line.useWorldSpace = true;
            line.startWidth = 0.0018f;
            line.endWidth = 0.0010f;
            line.numCapVertices = 2;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            if (guideMaterial != null) line.sharedMaterial = guideMaterial;
            guideLines.Add(line);
        }
    }

    void RefreshGuideVisuals(ClumpLayer layer)
    {
        if (layer.debugMode == DebugMode.Off)
        {
            ClearGuides();
            return;
        }

        EnsureGuidePool(layer.points.Count);
        for (int i = 0; i < guideLines.Count; i++)
        {
            LineRenderer line = guideLines[i];
            bool active = i < layer.points.Count;
            line.gameObject.SetActive(active);
            if (!active) continue;

            ClumpPoint point = layer.points[i];
            float s = Mathf.Clamp01(point.strength * layer.globalStrength);
            float length = layer.guideLength;
            Color color;

            if (layer.debugMode == DebugMode.Tone)
            {
                color = Color.Lerp(new Color(0f, 0.12f, 0f, 0.75f), new Color(0.1f, 1f, 0.2f, 1f), s);
            }
            else
            {
                color = new Color(0.1f, 1f, 0.2f, 1f);
                length *= Mathf.Lerp(0.15f, 1f, s);
            }

            line.startColor = color;
            line.endColor = color;
            line.SetPosition(0, point.position + point.normal * 0.001f);
            line.SetPosition(1, point.position + point.normal * (0.001f + length));
        }
    }

    void ClearGuides()
    {
        foreach (LineRenderer line in guideLines)
            if (line != null) line.gameObject.SetActive(false);
    }

    void BuildUI()
    {
        Canvas canvas = FindObjectsByType<Canvas>(FindObjectsSortMode.None).FirstOrDefault();
        if (canvas == null) return;

        panel = new GameObject("ClumpLayerModal", typeof(RectTransform), typeof(Image));
        panel.transform.SetParent(canvas.transform, false);
        RectTransform r = panel.GetComponent<RectTransform>();
        r.anchorMin = r.anchorMax = new Vector2(0.5f, 0.5f);
        r.pivot = new Vector2(0.5f, 0.5f);
        r.sizeDelta = new Vector2(390f, 500f);
        panel.GetComponent<Image>().color = new Color(0.08f, 0.09f, 0.08f, 0.96f);

        VerticalLayoutGroup layout = panel.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(18, 18, 16, 16);
        layout.spacing = 7f;
        layout.childControlWidth = true;
        layout.childControlHeight = false;

        titleText = AddText(panel.transform, "CLUMP LAYER", 22, 34);
        AddText(panel.transform, "Preview active group, then add when ready", 13, 22)
            .color = new Color(0.65f, 0.75f, 0.65f);

        attachText = AddButton(panel.transform, "ADD TO GROUP", AttachCurrentLayer)
            .GetComponentInChildren<TextMeshProUGUI>();
        enabledText = AddButton(panel.transform, "ENABLED", ToggleEnabled)
            .GetComponentInChildren<TextMeshProUGUI>();
        AddButton(panel.transform, "REMOVE FROM GROUP", RemoveCurrentLayer);
        AddButton(panel.transform, "REGENERATE 100 POINTS", () =>
        {
            ClumpLayer l = GetOrCreateLayer(editingGroupId);
            l.pointCount = 100;
            Regenerate(l);
        });

        paintText = AddButton(panel.transform, "PAINT: OFF", () =>
        {
            paintMode = !paintMode;
            paintText.text = paintMode ? "PAINT: ON" : "PAINT: OFF";
        }).GetComponentInChildren<TextMeshProUGUI>();

        AddButton(panel.transform, "PAINT VALUE: 1.0 / ERASE", () =>
        {
            paintValue = paintValue > 0.5f ? 0f : 1f;
        });

        debugText = AddButton(panel.transform, "VIS: TONE", CycleDebug)
            .GetComponentInChildren<TextMeshProUGUI>();

        strengthSlider = AddSlider(panel.transform, "GLOBAL CLUMP", 0f, 1f, 1f, v =>
        {
            ClumpLayer l = GetOrCreateLayer(editingGroupId);
            l.globalStrength = v;
            ApplyLayer(l, true);
        });

        brushSlider = AddSlider(panel.transform, "BRUSH RADIUS", 0.01f, 0.4f, 0.08f, v =>
        {
            GetOrCreateLayer(editingGroupId).brushRadius = v;
        });

        AddButton(panel.transform, "EDIT CLUMP CURVE", OpenCurveModal);
        AddButton(panel.transform, "CLOSE", ClosePanel);

        BuildCurveModal(canvas.transform);
    }

    void BuildCurveModal(Transform canvas)
    {
        curvePanel = new GameObject("ClumpCurveModal", typeof(RectTransform), typeof(Image));
        curvePanel.transform.SetParent(canvas, false);
        RectTransform r = curvePanel.GetComponent<RectTransform>();
        r.anchorMin = r.anchorMax = new Vector2(0.5f, 0.5f);
        r.pivot = new Vector2(0.5f, 0.5f);
        r.anchoredPosition = new Vector2(-410f, 0f);
        r.sizeDelta = new Vector2(330f, 300f);
        curvePanel.GetComponent<Image>().color = new Color(0.07f, 0.075f, 0.07f, 0.98f);

        VerticalLayoutGroup layout = curvePanel.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(16, 16, 14, 14);
        layout.spacing = 8f;
        layout.childControlWidth = true;
        layout.childControlHeight = false;

        AddText(curvePanel.transform, "CLUMP CURVE", 20, 32);
        AddText(curvePanel.transform, "Influence from root → tip", 13, 22)
            .color = new Color(0.65f, 0.75f, 0.65f);

        curveEarlySlider = AddSlider(curvePanel.transform, "EARLY (25%)", 0f, 1f, 0.08f, v => RebuildCurve());
        curveMidSlider = AddSlider(curvePanel.transform, "MID (65%)", 0f, 1f, 0.65f, v => RebuildCurve());
        curveTipSlider = AddSlider(curvePanel.transform, "TIP (100%)", 0f, 1f, 1f, v => RebuildCurve());
        AddButton(curvePanel.transform, "DONE", () => curvePanel.SetActive(false));
        curvePanel.SetActive(false);
    }

    void OpenCurveModal()
    {
        if (editingGroupId < 0 || curvePanel == null) return;
        ClumpLayer l = GetOrCreateLayer(editingGroupId);
        curveEarlySlider.SetValueWithoutNotify(l.curve.Evaluate(0.25f));
        curveMidSlider.SetValueWithoutNotify(l.curve.Evaluate(0.65f));
        curveTipSlider.SetValueWithoutNotify(l.curve.Evaluate(1f));
        curvePanel.SetActive(true);
    }

    void RebuildCurve()
    {
        if (editingGroupId < 0) return;
        ClumpLayer l = GetOrCreateLayer(editingGroupId);
        l.curve = new AnimationCurve(
            new Keyframe(0f, 0f),
            new Keyframe(0.25f, curveEarlySlider.value),
            new Keyframe(0.65f, curveMidSlider.value),
            new Keyframe(1f, curveTipSlider.value));
        ApplyLayer(l, true);
    }

    void CycleDebug()
    {
        if (editingGroupId < 0) return;
        ClumpLayer l = GetOrCreateLayer(editingGroupId);
        l.debugMode = l.debugMode == DebugMode.Tone
            ? DebugMode.Length
            : l.debugMode == DebugMode.Length ? DebugMode.Off : DebugMode.Tone;
        debugText.text = "VIS: " + l.debugMode.ToString().ToUpperInvariant();
        RefreshGuideVisuals(l);
    }

    void SyncUI(ClumpLayer l)
    {
        strengthSlider.SetValueWithoutNotify(l.globalStrength);
        brushSlider.SetValueWithoutNotify(l.brushRadius);
        debugText.text = "VIS: " + l.debugMode.ToString().ToUpperInvariant();
        attachText.text = l.attached ? "UPDATE GROUP CLUMP" : "ADD TO GROUP";
        enabledText.text = l.enabled ? "ENABLED" : "DISABLED";
    }

    TextMeshProUGUI AddText(Transform parent, string text, int size, float height)
    {
        GameObject go = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, height);
        TextMeshProUGUI t = go.GetComponent<TextMeshProUGUI>();
        t.text = text;
        t.fontSize = size;
        t.color = Color.white;
        t.alignment = TextAlignmentOptions.Center;
        return t;
    }

    GameObject AddButton(Transform parent, string label, Action action)
    {
        GameObject go = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 32f);
        go.GetComponent<Image>().color = new Color(0.16f, 0.25f, 0.17f);
        Button b = go.GetComponent<Button>();
        b.onClick.AddListener(() => action());

        TextMeshProUGUI t = AddText(go.transform, label, 13, 0f);
        RectTransform tr = t.rectTransform;
        tr.anchorMin = Vector2.zero;
        tr.anchorMax = Vector2.one;
        tr.offsetMin = Vector2.zero;
        tr.offsetMax = Vector2.zero;
        return go;
    }

    Slider AddSlider(Transform parent, string label, float min, float max, float value, Action<float> changed)
    {
        GameObject row = new GameObject(label, typeof(RectTransform));
        row.transform.SetParent(parent, false);
        row.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 44f);

        VerticalLayoutGroup v = row.AddComponent<VerticalLayoutGroup>();
        v.spacing = 2f;
        v.childControlWidth = true;
        v.childControlHeight = false;

        TextMeshProUGUI txt = AddText(row.transform, label + ": " + value.ToString("F2"), 13, 18f);

        GameObject sgo = new GameObject("Slider", typeof(RectTransform), typeof(Slider));
        sgo.transform.SetParent(row.transform, false);
        sgo.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 20f);
        Slider s = sgo.GetComponent<Slider>();
        s.minValue = min;
        s.maxValue = max;
        s.value = value;

        GameObject bg = new GameObject("Background", typeof(RectTransform), typeof(Image));
        bg.transform.SetParent(sgo.transform, false);
        RectTransform br = bg.GetComponent<RectTransform>();
        br.anchorMin = new Vector2(0f, 0.40f);
        br.anchorMax = new Vector2(1f, 0.60f);
        br.offsetMin = Vector2.zero;
        br.offsetMax = Vector2.zero;
        bg.GetComponent<Image>().color = new Color(0.2f, 0.2f, 0.2f);

        GameObject fillArea = new GameObject("Fill Area", typeof(RectTransform));
        fillArea.transform.SetParent(sgo.transform, false);
        RectTransform far = fillArea.GetComponent<RectTransform>();
        far.anchorMin = new Vector2(0f, 0.30f);
        far.anchorMax = new Vector2(1f, 0.70f);
        far.offsetMin = new Vector2(5f, 0f);
        far.offsetMax = new Vector2(-5f, 0f);

        GameObject fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
        fill.transform.SetParent(fillArea.transform, false);
        RectTransform fr = fill.GetComponent<RectTransform>();
        fr.anchorMin = Vector2.zero;
        fr.anchorMax = Vector2.one;
        fr.offsetMin = Vector2.zero;
        fr.offsetMax = Vector2.zero;
        fill.GetComponent<Image>().color = new Color(0.2f, 0.7f, 0.3f);
        s.fillRect = fr;

        GameObject handleArea = new GameObject("Handle Slide Area", typeof(RectTransform));
        handleArea.transform.SetParent(sgo.transform, false);
        RectTransform har = handleArea.GetComponent<RectTransform>();
        har.anchorMin = Vector2.zero;
        har.anchorMax = Vector2.one;
        har.offsetMin = new Vector2(7f, 0f);
        har.offsetMax = new Vector2(-7f, 0f);

        GameObject handle = new GameObject("Handle", typeof(RectTransform), typeof(Image));
        handle.transform.SetParent(handleArea.transform, false);
        handle.GetComponent<Image>().color = Color.white;
        RectTransform hr = handle.GetComponent<RectTransform>();
        hr.sizeDelta = new Vector2(12f, 18f);
        s.handleRect = hr;

        s.onValueChanged.AddListener(x =>
        {
            txt.text = label + ": " + x.ToString("F2");
            changed(x);
        });
        return s;
    }

    void OnDestroy()
    {
        if (guideMaterial != null) Destroy(guideMaterial);
    }
}
