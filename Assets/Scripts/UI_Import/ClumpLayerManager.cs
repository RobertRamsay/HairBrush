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
        public bool enabled = true;
        public int pointCount = 100;
        [Range(0f, 1f)] public float globalStrength = 1f;
        public float brushRadius = 0.08f;
        public float guideLength = 0.04f;
        public DebugMode debugMode = DebugMode.Tone;
        public AnimationCurve curve = new AnimationCurve(
            new Keyframe(0f, 0f), new Keyframe(0.25f, 0.08f),
            new Keyframe(0.65f, 0.65f), new Keyframe(1f, 1f));
        public List<ClumpPoint> points = new List<ClumpPoint>();
    }

    private readonly Dictionary<int, ClumpLayer> layers = new Dictionary<int, ClumpLayer>();
    private ModelViewer viewer;
    private Camera mainCamera;
    private GameObject panel;
    private TextMeshProUGUI titleText;
    private TextMeshProUGUI debugText;
    private TextMeshProUGUI paintText;
    private Slider strengthSlider;
    private Slider brushSlider;
    private Slider curveMidSlider;
    private bool paintMode;
    private float paintValue = 1f;

    public void Init(ModelViewer owner)
    {
        viewer = owner;
        mainCamera = owner.mainCamera != null ? owner.mainCamera : Camera.main;
        BuildUI();
        panel.SetActive(false);
    }

    public void ToggleForCurrentGroup()
    {
        if (viewer == null) return;
        int groupId = viewer.currentGroupId;
        ClumpLayer layer = GetOrCreateLayer(groupId);
        if (layer.points.Count == 0) Regenerate(layer);
        panel.SetActive(!panel.activeSelf);
        titleText.text = "CLUMP LAYER — GROUP " + groupId;
        SyncUI(layer);
        ApplyLayer(layer);
    }

    void Update()
    {
        if (panel == null || !panel.activeSelf || viewer == null) return;
        ClumpLayer layer = GetOrCreateLayer(viewer.currentGroupId);
        DrawDebug(layer);

        if (!paintMode || Mouse.current == null || !Mouse.current.leftButton.isPressed) return;
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;
        if (mainCamera == null) return;

        Ray ray = mainCamera.ScreenPointToRay(Mouse.current.position.ReadValue());
        RaycastHit[] hits = Physics.RaycastAll(ray, 1000f);
        RaycastHit? modelHit = hits.Where(h => h.collider.GetComponent<HairCard>() == null)
            .OrderBy(h => h.distance).Cast<RaycastHit?>().FirstOrDefault();
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
            .Where(c => c.groupId == layer.groupId).ToArray();
        layer.points.Clear();
        if (cards.Length == 0) return;

        // The first implementation samples the group's actual scalp footprint.
        // This keeps all 100 samples useful instead of scattering them over face/neck/bare mesh.
        for (int i = 0; i < Mathf.Max(1, layer.pointCount); i++)
        {
            HairCard seed = cards[i % cards.Length];
            Vector3 p = seed.GetSpawnHitPoint();
            Vector3 n = seed.GetSurfaceNormal().normalized;

            // For dense groups, blend neighbouring roots to distribute samples between cards.
            if (cards.Length > 1)
            {
                HairCard other = cards[(i * 17 + 7) % cards.Length];
                float blend = Mathf.Repeat(i * 0.6180339f, 1f);
                p = Vector3.Lerp(p, other.GetSpawnHitPoint(), blend);
                n = Vector3.Slerp(n, other.GetSurfaceNormal().normalized, blend).normalized;
            }
            layer.points.Add(new ClumpPoint { position = p, normal = n, strength = 0f });
        }
        ApplyLayer(layer);
    }

    void Paint(ClumpLayer layer, Vector3 center, float value)
    {
        float radius = Mathf.Max(0.001f, layer.brushRadius);
        foreach (ClumpPoint point in layer.points)
        {
            float d = Vector3.Distance(center, point.position);
            if (d > radius) continue;
            float falloff = 1f - Mathf.Clamp01(d / radius);
            point.strength = Mathf.Lerp(point.strength, value, falloff * 0.2f);
        }
        ApplyLayer(layer);
    }

    void ApplyLayer(ClumpLayer layer)
    {
        HairCard[] cards = FindObjectsByType<HairCard>(FindObjectsSortMode.None)
            .Where(c => c.groupId == layer.groupId).ToArray();
        if (!layer.enabled || layer.points.Count == 0)
        {
            foreach (HairCard card in cards) card.ClearClumpModifier();
            return;
        }

        foreach (HairCard card in cards)
        {
            ClumpPoint nearest = layer.points.OrderBy(p => Vector3.SqrMagnitude(card.GetSpawnHitPoint() - p.position)).First();
            card.SetClumpModifier(nearest.position, nearest.normal, nearest.strength * layer.globalStrength, layer.curve);
        }
    }

    void DrawDebug(ClumpLayer layer)
    {
        if (layer.debugMode == DebugMode.Off) return;
        foreach (ClumpPoint point in layer.points)
        {
            float s = Mathf.Clamp01(point.strength * layer.globalStrength);
            float length = layer.guideLength;
            Color color;
            if (layer.debugMode == DebugMode.Tone)
            {
                color = Color.Lerp(new Color(0f, 0.12f, 0f), Color.green, s);
            }
            else
            {
                color = Color.green;
                length *= Mathf.Lerp(0.15f, 1f, s);
            }
            Debug.DrawRay(point.position, point.normal * length, color, 0f, false);
        }
    }

    void BuildUI()
    {
        Canvas canvas = FindObjectsByType<Canvas>(FindObjectsSortMode.None).FirstOrDefault();
        if (canvas == null) return;
        panel = new GameObject("ClumpLayerModal", typeof(RectTransform), typeof(Image));
        panel.transform.SetParent(canvas.transform, false);
        RectTransform r = panel.GetComponent<RectTransform>();
        r.anchorMin = r.anchorMax = new Vector2(0.5f, 0.5f); r.pivot = new Vector2(0.5f, 0.5f);
        r.sizeDelta = new Vector2(390f, 430f);
        panel.GetComponent<Image>().color = new Color(0.08f, 0.09f, 0.08f, 0.96f);
        VerticalLayoutGroup layout = panel.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(18, 18, 16, 16); layout.spacing = 8f; layout.childControlHeight = false;

        titleText = AddText(panel.transform, "CLUMP LAYER", 22, 34);
        AddText(panel.transform, "Non-destructive group modifier", 14, 24).color = new Color(0.65f, 0.75f, 0.65f);
        AddButton(panel.transform, "REGENERATE 100 POINTS", () => { ClumpLayer l = GetOrCreateLayer(viewer.currentGroupId); l.pointCount = 100; Regenerate(l); });
        AddButton(panel.transform, "ENABLE / DISABLE", () => { ClumpLayer l = GetOrCreateLayer(viewer.currentGroupId); l.enabled = !l.enabled; ApplyLayer(l); });
        paintText = AddButton(panel.transform, "PAINT: OFF", () => { paintMode = !paintMode; paintText.text = paintMode ? "PAINT: ON" : "PAINT: OFF"; }).GetComponentInChildren<TextMeshProUGUI>();
        AddButton(panel.transform, "PAINT VALUE: 1.0 / ERASE", () => { paintValue = paintValue > 0.5f ? 0f : 1f; });
        debugText = AddButton(panel.transform, "VIS: TONE", CycleDebug).GetComponentInChildren<TextMeshProUGUI>();
        strengthSlider = AddSlider(panel.transform, "GLOBAL CLUMP", 0f, 1f, 1f, v => { ClumpLayer l = GetOrCreateLayer(viewer.currentGroupId); l.globalStrength = v; ApplyLayer(l); });
        brushSlider = AddSlider(panel.transform, "BRUSH RADIUS", 0.01f, 0.4f, 0.08f, v => GetOrCreateLayer(viewer.currentGroupId).brushRadius = v);
        curveMidSlider = AddSlider(panel.transform, "CURVE MID", 0f, 1f, 0.65f, v => { ClumpLayer l = GetOrCreateLayer(viewer.currentGroupId); l.curve = new AnimationCurve(new Keyframe(0f,0f), new Keyframe(0.25f,0.08f), new Keyframe(0.65f,v), new Keyframe(1f,1f)); ApplyLayer(l); });
        AddButton(panel.transform, "CLOSE", () => { paintMode = false; panel.SetActive(false); });
    }

    void CycleDebug()
    {
        ClumpLayer l = GetOrCreateLayer(viewer.currentGroupId);
        l.debugMode = l.debugMode == DebugMode.Tone ? DebugMode.Length : l.debugMode == DebugMode.Length ? DebugMode.Off : DebugMode.Tone;
        debugText.text = "VIS: " + l.debugMode.ToString().ToUpperInvariant();
    }

    void SyncUI(ClumpLayer l)
    {
        strengthSlider.SetValueWithoutNotify(l.globalStrength); brushSlider.SetValueWithoutNotify(l.brushRadius);
        debugText.text = "VIS: " + l.debugMode.ToString().ToUpperInvariant();
    }

    TextMeshProUGUI AddText(Transform parent, string text, int size, float height)
    {
        GameObject go = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI)); go.transform.SetParent(parent, false);
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, height);
        TextMeshProUGUI t = go.GetComponent<TextMeshProUGUI>(); t.text = text; t.fontSize = size; t.color = Color.white; t.alignment = TextAlignmentOptions.Center; return t;
    }

    GameObject AddButton(Transform parent, string label, Action action)
    {
        GameObject go = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(Button)); go.transform.SetParent(parent, false); go.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 34f);
        go.GetComponent<Image>().color = new Color(0.16f, 0.25f, 0.17f); Button b = go.GetComponent<Button>(); b.onClick.AddListener(() => action());
        TextMeshProUGUI t = AddText(go.transform, label, 14, 0f); RectTransform tr = t.rectTransform; tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one; tr.sizeDelta = Vector2.zero; return go;
    }

    Slider AddSlider(Transform parent, string label, float min, float max, float value, Action<float> changed)
    {
        GameObject row = new GameObject(label, typeof(RectTransform)); row.transform.SetParent(parent, false); row.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 46f);
        VerticalLayoutGroup v = row.AddComponent<VerticalLayoutGroup>(); v.spacing = 2f; v.childControlHeight = false;
        TextMeshProUGUI txt = AddText(row.transform, label, 13, 18f);
        GameObject sgo = new GameObject("Slider", typeof(RectTransform), typeof(Slider)); sgo.transform.SetParent(row.transform, false); sgo.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 20f);
        Slider s = sgo.GetComponent<Slider>(); s.minValue = min; s.maxValue = max; s.value = value;
        GameObject bg = new GameObject("Background", typeof(RectTransform), typeof(Image)); bg.transform.SetParent(sgo.transform, false); RectTransform br = bg.GetComponent<RectTransform>(); br.anchorMin = new Vector2(0f,.35f); br.anchorMax = new Vector2(1f,.65f); br.sizeDelta = Vector2.zero; bg.GetComponent<Image>().color = new Color(.2f,.2f,.2f);
        GameObject fillArea = new GameObject("Fill Area", typeof(RectTransform)); fillArea.transform.SetParent(sgo.transform, false); RectTransform fr = fillArea.GetComponent<RectTransform>(); fr.anchorMin = Vector2.zero; fr.anchorMax = Vector2.one; fr.sizeDelta = Vector2.zero;
        GameObject fill = new GameObject("Fill", typeof(RectTransform), typeof(Image)); fill.transform.SetParent(fillArea.transform, false); fill.GetComponent<Image>().color = new Color(.2f,.7f,.3f); s.fillRect = fill.GetComponent<RectTransform>();
        GameObject handleArea = new GameObject("Handle Slide Area", typeof(RectTransform)); handleArea.transform.SetParent(sgo.transform, false); RectTransform hr = handleArea.GetComponent<RectTransform>(); hr.anchorMin = Vector2.zero; hr.anchorMax = Vector2.one; hr.sizeDelta = Vector2.zero;
        GameObject handle = new GameObject("Handle", typeof(RectTransform), typeof(Image)); handle.transform.SetParent(handleArea.transform, false); handle.GetComponent<Image>().color = Color.white; s.handleRect = handle.GetComponent<RectTransform>(); s.handleRect.sizeDelta = new Vector2(14f,20f);
        s.onValueChanged.AddListener(x => { txt.text = label + ": " + x.ToString("F2"); changed(x); }); return s;
    }
}
