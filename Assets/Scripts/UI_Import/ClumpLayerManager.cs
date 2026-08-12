using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

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
        public bool enabled = false;
        public int pointCount = 100;
        [Range(0f, 1f)] public float globalStrength = 1f;
        public float brushRadius = 0.08f;
        [Range(0f, 1f)] public float brushStrength = 0.5f;
        [Range(0f, 1f)] public float brushFalloff = 0.5f;
        [Range(0f, 1f)] public float brushValue = 1f;
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
    private readonly Dictionary<int, bool> expandedGroups = new Dictionary<int, bool>();
    private readonly List<LineRenderer> guideLines = new List<LineRenderer>();

    private ModelViewer viewer;
    private Camera mainCamera;
    private GameObject guideRoot;
    private Material guideMaterial;

    private GameObject brushRoot;
    private LineRenderer brushOuterRing;
    private LineRenderer brushFalloffRing;
    private Material brushMaterial;

    private bool paintMode;
    private int paintGroupId = -1;
    private int visualGroupId = -1;
    private float nextUIScanTime;

    public void Init(ModelViewer owner)
    {
        viewer = owner;
        mainCamera = owner.mainCamera != null ? owner.mainCamera : Camera.main;
        EnsureBrushVisuals();
        SetBrushVisible(false);
    }

    void Update()
    {
        if (viewer == null) return;

        if (Time.unscaledTime >= nextUIScanTime)
        {
            nextUIScanTime = Time.unscaledTime + 0.25f;
            EnsureGroupModifierUI();
        }

        if (visualGroupId >= 0 && expandedGroups.TryGetValue(visualGroupId, out bool expanded) && expanded)
            RefreshGuideVisuals(GetOrCreateLayer(visualGroupId));
        else
            ClearGuides();

        HandlePainting();
    }

    void HandlePainting()
    {
        if (!paintMode || paintGroupId < 0 || Mouse.current == null || mainCamera == null)
        {
            SetBrushVisible(false);
            return;
        }

        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
        {
            SetBrushVisible(false);
            return;
        }

        ClumpLayer layer = GetOrCreateLayer(paintGroupId);
        Ray ray = mainCamera.ScreenPointToRay(Mouse.current.position.ReadValue());
        RaycastHit[] hits = Physics.RaycastAll(ray, 1000f);
        RaycastHit? modelHit = hits
            .Where(h => h.collider.GetComponent<HairCard>() == null)
            .OrderBy(h => h.distance)
            .Cast<RaycastHit?>()
            .FirstOrDefault();

        if (!modelHit.HasValue)
        {
            SetBrushVisible(false);
            return;
        }

        RaycastHit hit = modelHit.Value;
        UpdateBrushVisuals(layer, hit.point, hit.normal);
        if (Mouse.current.leftButton.isPressed)
            Paint(layer, hit.point);
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

    void ToggleLayer(int groupId)
    {
        ClumpLayer layer = GetOrCreateLayer(groupId);
        layer.enabled = !layer.enabled;
        if (layer.enabled && layer.points.Count == 0 && layer.pointCount > 0)
            Regenerate(layer);
        ApplyLayer(layer);
        RebuildModifierUI(groupId);
    }

    void ToggleExpanded(int groupId)
    {
        bool expanded = expandedGroups.TryGetValue(groupId, out bool current) && current;
        expandedGroups[groupId] = !expanded;
        visualGroupId = !expanded ? groupId : (visualGroupId == groupId ? -1 : visualGroupId);
        if (expanded) StopPainting(groupId);
        RebuildModifierUI(groupId);
    }

    void TogglePaint(int groupId)
    {
        if (paintMode && paintGroupId == groupId)
        {
            StopPainting(groupId);
            RebuildModifierUI(groupId);
            return;
        }

        if (paintMode) StopPainting(paintGroupId);

        ClumpLayer layer = GetOrCreateLayer(groupId);
        layer.enabled = true;
        if (layer.points.Count == 0 && layer.pointCount > 0)
            Regenerate(layer);

        paintMode = true;
        paintGroupId = groupId;
        visualGroupId = groupId;
        viewer.ToggleGroomingMode(false);
        ApplyLayer(layer);
        RebuildAllModifierUI();
    }

    void StopPainting(int groupId)
    {
        if (!paintMode) return;
        if (groupId >= 0 && paintGroupId != groupId) return;

        paintMode = false;
        paintGroupId = -1;
        SetBrushVisible(false);
        if (viewer != null) viewer.ToggleGroomingMode(true);
    }

    void Regenerate(ClumpLayer layer)
    {
        HairCard[] cards = FindObjectsByType<HairCard>(FindObjectsSortMode.None)
            .Where(c => c.groupId == layer.groupId)
            .ToArray();

        layer.pointCount = Mathf.Clamp(layer.pointCount, 0, 100);
        layer.points.Clear();

        if (cards.Length == 0 || layer.pointCount == 0)
        {
            ApplyLayer(layer);
            ClearGuides();
            return;
        }

        for (int i = 0; i < layer.pointCount; i++)
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

            layer.points.Add(new ClumpPoint { position = p, normal = n, strength = 0f });
        }

        ApplyLayer(layer);
        RefreshGuideVisuals(layer);
    }

    void Paint(ClumpLayer layer, Vector3 center)
    {
        float radius = Mathf.Max(0.001f, layer.brushRadius);
        float falloffAmount = Mathf.Clamp01(layer.brushFalloff);
        float innerRadius = radius * (1f - falloffAmount);
        float targetValue = Mathf.Clamp01(layer.brushValue);
        float strength = Mathf.Clamp01(layer.brushStrength);
        float temporalBlend = 1f - Mathf.Exp(-12f * strength * Time.deltaTime);

        foreach (ClumpPoint point in layer.points)
        {
            float d = Vector3.Distance(center, point.position);
            if (d > radius) continue;

            float spatialWeight = 1f;
            if (falloffAmount > 0.0001f && d > innerRadius)
            {
                float t = Mathf.InverseLerp(radius, innerRadius, d);
                spatialWeight = Mathf.SmoothStep(0f, 1f, t);
            }

            point.strength = Mathf.Lerp(point.strength, targetValue, temporalBlend * spatialWeight);
        }

        ApplyLayer(layer);
        RefreshGuideVisuals(layer);
    }

    void ApplyLayer(ClumpLayer layer)
    {
        HairCard[] cards = FindObjectsByType<HairCard>(FindObjectsSortMode.None)
            .Where(c => c.groupId == layer.groupId)
            .ToArray();

        if (!layer.enabled || layer.points.Count == 0)
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

    void CycleDebug(int groupId)
    {
        ClumpLayer layer = GetOrCreateLayer(groupId);
        layer.debugMode = layer.debugMode == DebugMode.Tone
            ? DebugMode.Length
            : layer.debugMode == DebugMode.Length ? DebugMode.Off : DebugMode.Tone;
        visualGroupId = groupId;
        RefreshGuideVisuals(layer);
        RebuildModifierUI(groupId);
    }

    void EnsureGroupModifierUI()
    {
        RectTransform[] rects = FindObjectsByType<RectTransform>(FindObjectsSortMode.None);
        List<RectTransform> groupItems = rects
            .Where(r => r.name.StartsWith("GroupItem_"))
            .OrderBy(r => r.GetSiblingIndex())
            .ToList();

        foreach (RectTransform groupItem in groupItems)
        {
            if (!int.TryParse(groupItem.name.Substring("GroupItem_".Length), out int groupId)) continue;
            Transform parent = groupItem.parent;
            if (parent == null) continue;
            if (parent.Find("ClumpModifier_" + groupId) != null) continue;
            BuildModifierUI(parent, groupItem, groupId);
        }
    }

    void RebuildAllModifierUI()
    {
        RectTransform[] modifiers = FindObjectsByType<RectTransform>(FindObjectsSortMode.None)
            .Where(r => r.name.StartsWith("ClumpModifier_"))
            .ToArray();
        foreach (RectTransform modifier in modifiers)
            Destroy(modifier.gameObject);
        nextUIScanTime = 0f;
    }

    void RebuildModifierUI(int groupId)
    {
        RectTransform existing = FindObjectsByType<RectTransform>(FindObjectsSortMode.None)
            .FirstOrDefault(r => r.name == "ClumpModifier_" + groupId);
        if (existing != null) Destroy(existing.gameObject);
        nextUIScanTime = 0f;
    }

    void BuildModifierUI(Transform parent, RectTransform groupItem, int groupId)
    {
        ClumpLayer layer = GetOrCreateLayer(groupId);
        bool expanded = expandedGroups.TryGetValue(groupId, out bool state) && state;

        GameObject root = new GameObject("ClumpModifier_" + groupId, typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup));
        root.transform.SetParent(parent, false);
        root.transform.SetSiblingIndex(groupItem.GetSiblingIndex() + 1);
        RectTransform rootRect = root.GetComponent<RectTransform>();
        rootRect.sizeDelta = new Vector2(0f, expanded ? 515f : 34f);
        root.GetComponent<Image>().color = new Color(0.11f, 0.13f, 0.11f, 0.98f);

        VerticalLayoutGroup layout = root.GetComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(10, 10, 4, 6);
        layout.spacing = 4f;
        layout.childControlWidth = true;
        layout.childControlHeight = false;
        layout.childForceExpandHeight = false;

        GameObject header = new GameObject("ClumpHeader", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        header.transform.SetParent(root.transform, false);
        header.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 26f);
        HorizontalLayoutGroup headerLayout = header.GetComponent<HorizontalLayoutGroup>();
        headerLayout.spacing = 6f;
        headerLayout.childControlWidth = false;
        headerLayout.childControlHeight = true;

        GameObject expandButton = AddButton(header.transform, expanded ? "[-] CLUMP" : "[+] CLUMP", () => ToggleExpanded(groupId), 205f, 26f);
        expandButton.GetComponent<Image>().color = new Color(0.14f, 0.20f, 0.15f);

        GameObject toggleButton = AddButton(header.transform, layer.enabled ? "ON" : "OFF", () => ToggleLayer(groupId), 72f, 26f);
        toggleButton.GetComponent<Image>().color = layer.enabled
            ? new Color(0.18f, 0.48f, 0.22f)
            : new Color(0.28f, 0.28f, 0.28f);

        if (!expanded) return;

        AddSlider(root.transform, "POINT COUNT", 0f, 100f, layer.pointCount, v => layer.pointCount = Mathf.RoundToInt(v), true);
        AddButton(root.transform, "REGENERATE POINTS", () => { Regenerate(layer); RebuildModifierUI(groupId); }, -1f, 28f);

        AddButton(root.transform, paintMode && paintGroupId == groupId ? "PAINT: ON" : "PAINT: OFF", () => TogglePaint(groupId), -1f, 28f);
        AddSlider(root.transform, "BRUSH SIZE", 0.01f, 0.4f, layer.brushRadius, v => layer.brushRadius = v);
        AddSlider(root.transform, "BRUSH STRENGTH", 0f, 1f, layer.brushStrength, v => layer.brushStrength = v);
        AddSlider(root.transform, "BRUSH FALLOFF", 0f, 1f, layer.brushFalloff, v => layer.brushFalloff = v);
        AddSlider(root.transform, "PAINT VALUE", 0f, 1f, layer.brushValue, v => layer.brushValue = v);

        AddButton(root.transform, "VIS: " + layer.debugMode.ToString().ToUpperInvariant(), () => CycleDebug(groupId), -1f, 28f);
        AddSlider(root.transform, "GLOBAL CLUMP", 0f, 1f, layer.globalStrength, v => { layer.globalStrength = v; ApplyLayer(layer); });

        AddText(root.transform, "CLUMP CURVE  root -> tip", 12, 18f).alignment = TextAlignmentOptions.Left;
        GameObject previewGO = new GameObject("CurvePreview", typeof(RectTransform), typeof(ClumpCurvePreviewGraphic));
        previewGO.transform.SetParent(root.transform, false);
        previewGO.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 75f);
        ClumpCurvePreviewGraphic preview = previewGO.GetComponent<ClumpCurvePreviewGraphic>();
        preview.curveProvider = () => layer.curve;
        preview.color = new Color(0.2f, 1f, 0.35f, 1f);

        AddSlider(root.transform, "CURVE EARLY 25%", 0f, 1f, layer.curve.Evaluate(0.25f), v => RebuildCurve(layer, 0, v, groupId));
        AddSlider(root.transform, "CURVE MID 65%", 0f, 1f, layer.curve.Evaluate(0.65f), v => RebuildCurve(layer, 1, v, groupId));
        AddSlider(root.transform, "CURVE TIP 100%", 0f, 1f, layer.curve.Evaluate(1f), v => RebuildCurve(layer, 2, v, groupId));
    }

    void RebuildCurve(ClumpLayer layer, int controlIndex, float value, int groupId)
    {
        float early = layer.curve.Evaluate(0.25f);
        float mid = layer.curve.Evaluate(0.65f);
        float tip = layer.curve.Evaluate(1f);
        if (controlIndex == 0) early = value;
        else if (controlIndex == 1) mid = value;
        else tip = value;

        layer.curve = new AnimationCurve(
            new Keyframe(0f, 0f),
            new Keyframe(0.25f, early),
            new Keyframe(0.65f, mid),
            new Keyframe(1f, tip));
        ApplyLayer(layer);

        ClumpCurvePreviewGraphic preview = FindObjectsByType<ClumpCurvePreviewGraphic>(FindObjectsSortMode.None)
            .FirstOrDefault(p => p.transform.parent != null && p.transform.parent.name == "ClumpModifier_" + groupId);
        if (preview != null) preview.SetVerticesDirty();
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

    GameObject AddButton(Transform parent, string label, Action action, float width = -1f, float height = 28f)
    {
        GameObject go = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(width > 0f ? width : 0f, height);
        go.GetComponent<Image>().color = new Color(0.16f, 0.25f, 0.17f);
        go.GetComponent<Button>().onClick.AddListener(() => action());
        TextMeshProUGUI t = AddText(go.transform, label, 12, 0f);
        RectTransform tr = t.rectTransform;
        tr.anchorMin = Vector2.zero;
        tr.anchorMax = Vector2.one;
        tr.offsetMin = Vector2.zero;
        tr.offsetMax = Vector2.zero;
        return go;
    }

    Slider AddSlider(Transform parent, string label, float min, float max, float value, Action<float> changed, bool wholeNumbers = false)
    {
        GameObject row = new GameObject(label, typeof(RectTransform));
        row.transform.SetParent(parent, false);
        row.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 38f);

        VerticalLayoutGroup v = row.AddComponent<VerticalLayoutGroup>();
        v.spacing = 1f;
        v.childControlWidth = true;
        v.childControlHeight = false;

        string FormatValue(float x) => wholeNumbers ? Mathf.RoundToInt(x).ToString() : x.ToString("F2");
        TextMeshProUGUI txt = AddText(row.transform, label + ": " + FormatValue(value), 11, 16f);
        txt.alignment = TextAlignmentOptions.Left;

        GameObject sgo = new GameObject("Slider", typeof(RectTransform), typeof(Slider));
        sgo.transform.SetParent(row.transform, false);
        sgo.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 17f);
        Slider s = sgo.GetComponent<Slider>();
        s.minValue = min;
        s.maxValue = max;
        s.wholeNumbers = wholeNumbers;
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
        hr.sizeDelta = new Vector2(11f, 15f);
        s.handleRect = hr;

        s.onValueChanged.AddListener(x =>
        {
            txt.text = label + ": " + FormatValue(x);
            changed(x);
        });
        return s;
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
                color = Color.Lerp(new Color(0f, 0.12f, 0f, 0.75f), new Color(0.1f, 1f, 0.2f, 1f), s);
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

    void EnsureBrushVisuals()
    {
        if (brushRoot != null) return;
        brushRoot = new GameObject("ClumpPaintBrushVisual");
        brushRoot.transform.SetParent(transform, false);

        Shader shader = Shader.Find("Sprites/Default");
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader != null) brushMaterial = new Material(shader) { name = "ClumpBrushRuntimeMaterial" };

        brushOuterRing = CreateBrushRing("BrushOuterRing", 0.0022f);
        brushFalloffRing = CreateBrushRing("BrushFalloffRing", 0.0012f);
    }

    LineRenderer CreateBrushRing(string name, float width)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(brushRoot.transform, false);
        LineRenderer line = go.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.loop = true;
        line.positionCount = 64;
        line.startWidth = width;
        line.endWidth = width;
        line.numCornerVertices = 2;
        line.numCapVertices = 2;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
        if (brushMaterial != null) line.sharedMaterial = brushMaterial;
        return line;
    }

    void UpdateBrushVisuals(ClumpLayer layer, Vector3 center, Vector3 normal)
    {
        EnsureBrushVisuals();
        SetBrushVisible(true);

        normal = normal.normalized;
        Vector3 tangent = Vector3.Cross(normal, Mathf.Abs(Vector3.Dot(normal, Vector3.up)) > 0.95f ? Vector3.right : Vector3.up).normalized;
        Vector3 bitangent = Vector3.Cross(normal, tangent).normalized;
        Vector3 liftedCenter = center + normal * 0.0025f;

        float outerRadius = Mathf.Max(0.001f, layer.brushRadius);
        float innerRadius = outerRadius * (1f - Mathf.Clamp01(layer.brushFalloff));
        Color valueColor = Color.Lerp(new Color(1f, 0.18f, 0.12f, 1f), new Color(0.15f, 1f, 0.25f, 1f), layer.brushValue);

        SetRingGeometry(brushOuterRing, liftedCenter, tangent, bitangent, outerRadius);
        brushOuterRing.startColor = valueColor;
        brushOuterRing.endColor = valueColor;

        bool showInner = innerRadius > outerRadius * 0.02f && innerRadius < outerRadius * 0.995f;
        brushFalloffRing.gameObject.SetActive(showInner);
        if (showInner)
        {
            SetRingGeometry(brushFalloffRing, liftedCenter, tangent, bitangent, innerRadius);
            Color innerColor = new Color(valueColor.r, valueColor.g, valueColor.b, 0.45f);
            brushFalloffRing.startColor = innerColor;
            brushFalloffRing.endColor = innerColor;
        }
    }

    void SetRingGeometry(LineRenderer line, Vector3 center, Vector3 tangent, Vector3 bitangent, float radius)
    {
        int count = line.positionCount;
        for (int i = 0; i < count; i++)
        {
            float a = (i / (float)count) * Mathf.PI * 2f;
            Vector3 offset = tangent * Mathf.Cos(a) * radius + bitangent * Mathf.Sin(a) * radius;
            line.SetPosition(i, center + offset);
        }
    }

    void SetBrushVisible(bool visible)
    {
        if (brushRoot != null) brushRoot.SetActive(visible);
    }

    void OnDisable()
    {
        StopPainting(-1);
    }

    void OnDestroy()
    {
        if (guideMaterial != null) Destroy(guideMaterial);
        if (brushMaterial != null) Destroy(brushMaterial);
    }
}

public class ClumpCurvePreviewGraphic : MaskableGraphic
{
    public Func<AnimationCurve> curveProvider;
    private const int Samples = 48;
    private const float StrokeWidth = 2.5f;

    void Update()
    {
        SetVerticesDirty();
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        AnimationCurve curve = curveProvider != null ? curveProvider() : null;
        if (curve == null) return;

        Rect r = rectTransform.rect;
        float pad = 7f;
        Rect plot = new Rect(r.xMin + pad, r.yMin + pad, Mathf.Max(1f, r.width - pad * 2f), Mathf.Max(1f, r.height - pad * 2f));
        DrawGrid(vh, plot);

        Vector2 previous = CurvePoint(plot, 0f, Mathf.Clamp01(curve.Evaluate(0f)));
        for (int i = 1; i <= Samples; i++)
        {
            float t = i / (float)Samples;
            Vector2 current = CurvePoint(plot, t, Mathf.Clamp01(curve.Evaluate(t)));
            AddLine(vh, previous, current, StrokeWidth, color);
            previous = current;
        }
    }

    Vector2 CurvePoint(Rect plot, float x, float y)
    {
        return new Vector2(Mathf.Lerp(plot.xMin, plot.xMax, x), Mathf.Lerp(plot.yMin, plot.yMax, y));
    }

    void DrawGrid(VertexHelper vh, Rect plot)
    {
        Color grid = new Color(1f, 1f, 1f, 0.13f);
        AddLine(vh, new Vector2(plot.xMin, plot.yMin), new Vector2(plot.xMax, plot.yMin), 1f, grid);
        AddLine(vh, new Vector2(plot.xMin, plot.yMin), new Vector2(plot.xMin, plot.yMax), 1f, grid);
        AddLine(vh, new Vector2(plot.xMin, plot.yMax), new Vector2(plot.xMax, plot.yMax), 1f, grid);
        AddLine(vh, new Vector2(plot.xMax, plot.yMin), new Vector2(plot.xMax, plot.yMax), 1f, grid);
        for (int i = 1; i < 4; i++)
        {
            float x = Mathf.Lerp(plot.xMin, plot.xMax, i / 4f);
            float y = Mathf.Lerp(plot.yMin, plot.yMax, i / 4f);
            AddLine(vh, new Vector2(x, plot.yMin), new Vector2(x, plot.yMax), 1f, grid);
            AddLine(vh, new Vector2(plot.xMin, y), new Vector2(plot.xMax, y), 1f, grid);
        }
    }

    void AddLine(VertexHelper vh, Vector2 a, Vector2 b, float width, Color c)
    {
        Vector2 dir = (b - a).normalized;
        if (dir.sqrMagnitude < 0.0001f) return;
        Vector2 n = new Vector2(-dir.y, dir.x) * width * 0.5f;
        int start = vh.currentVertCount;

        UIVertex v = UIVertex.simpleVert;
        v.color = c;
        v.position = a - n; vh.AddVert(v);
        v.position = a + n; vh.AddVert(v);
        v.position = b + n; vh.AddVert(v);
        v.position = b - n; vh.AddVert(v);
        vh.AddTriangle(start, start + 1, start + 2);
        vh.AddTriangle(start, start + 2, start + 3);
    }
}
