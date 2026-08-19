using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public enum GroomShapeCurveChannel
{
    Bend,
    X,
    Y,
    Z,
    // Curl (spiral/coil) magnitude profiles. Unlike Bend/X/Y/Z these have no per-POST override -
    // see PostShapeCurveBridge.EvaluateRoot, which routes these two straight to the group
    // registry rather than through the POST-editing snapshot mechanism.
    CurlFrequency,
    CurlDiameter
}

// Canonical group-root length profiles for shape angles. The slider remains the authored
// magnitude; these curves are a normalized 0..1 multiplier from card root (t=0) to tip (t=1).
// Bend defaults to the legacy t^2 progression. X/Y/Z default to 1 so existing cards retain
// their exact whole-card orientation until a curve is deliberately edited.
public static class GroomShapeCurveRegistry
{
    private sealed class CurveSet
    {
        public AnimationCurve bend = CreateDefault(GroomShapeCurveChannel.Bend);
        public AnimationCurve x = CreateDefault(GroomShapeCurveChannel.X);
        public AnimationCurve y = CreateDefault(GroomShapeCurveChannel.Y);
        public AnimationCurve z = CreateDefault(GroomShapeCurveChannel.Z);
        public AnimationCurve curlFrequency = CreateDefault(GroomShapeCurveChannel.CurlFrequency);
        public AnimationCurve curlDiameter = CreateDefault(GroomShapeCurveChannel.CurlDiameter);
    }

    private static readonly Dictionary<int, CurveSet> byGroup = new Dictionary<int, CurveSet>();

    public static AnimationCurve GetCurve(int groupId, GroomShapeCurveChannel channel)
    {
        CurveSet set = GetSet(groupId);
        switch (channel)
        {
            case GroomShapeCurveChannel.Bend: return set.bend;
            case GroomShapeCurveChannel.X: return set.x;
            case GroomShapeCurveChannel.Y: return set.y;
            case GroomShapeCurveChannel.Z: return set.z;
            case GroomShapeCurveChannel.CurlFrequency: return set.curlFrequency;
            default: return set.curlDiameter;
        }
    }

    public static float Evaluate(int groupId, GroomShapeCurveChannel channel, float t)
    {
        AnimationCurve curve = GetCurve(groupId, channel);
        return Mathf.Clamp01(curve.Evaluate(Mathf.Clamp01(t)));
    }

    public static void SetCurve(int groupId, GroomShapeCurveChannel channel, AnimationCurve curve)
    {
        CurveSet set = GetSet(groupId);
        AnimationCurve clean = SanitizeCurve(channel, curve);
        switch (channel)
        {
            case GroomShapeCurveChannel.Bend: set.bend = clean; break;
            case GroomShapeCurveChannel.X: set.x = clean; break;
            case GroomShapeCurveChannel.Y: set.y = clean; break;
            case GroomShapeCurveChannel.Z: set.z = clean; break;
            case GroomShapeCurveChannel.CurlFrequency: set.curlFrequency = clean; break;
            case GroomShapeCurveChannel.CurlDiameter: set.curlDiameter = clean; break;
        }
    }

    public static void Reset(int groupId, GroomShapeCurveChannel channel)
    {
        SetCurve(groupId, channel, CreateDefault(channel));
    }

    public static void ClearAll()
    {
        byGroup.Clear();
    }

    public static List<GroomCurveKeySaveData> Export(int groupId, GroomShapeCurveChannel channel)
    {
        List<GroomCurveKeySaveData> result = new List<GroomCurveKeySaveData>();
        foreach (Keyframe key in GetCurve(groupId, channel).keys)
        {
            result.Add(new GroomCurveKeySaveData
            {
                time = key.time,
                value = key.value,
                inTangent = key.inTangent,
                outTangent = key.outTangent
            });
        }
        return result;
    }

    public static void Import(int groupId, GroomShapeCurveChannel channel, List<GroomCurveKeySaveData> saved)
    {
        if (saved == null || saved.Count < 2)
        {
            Reset(groupId, channel);
            return;
        }

        List<Keyframe> keys = new List<Keyframe>();
        foreach (GroomCurveKeySaveData item in saved)
        {
            if (item == null) continue;
            keys.Add(new Keyframe(
                Mathf.Clamp01(item.time),
                Mathf.Clamp01(item.value),
                Finite(item.inTangent) ? item.inTangent : 0f,
                Finite(item.outTangent) ? item.outTangent : 0f));
        }
        SetCurve(groupId, channel, new AnimationCurve(keys.ToArray()));
    }

    public static void RefreshGroup(int groupId)
    {
        foreach (HairCard card in UnityEngine.Object.FindObjectsByType<HairCard>(FindObjectsSortMode.None))
            if (card != null && card.groupId == groupId)
                card.GenerateMesh();
    }

    public static AnimationCurve CreateDefault(GroomShapeCurveChannel channel)
    {
        AnimationCurve curve;
        if (channel == GroomShapeCurveChannel.Bend)
        {
            // Piecewise Hermite keys/tangents reproduce y=t^2 exactly.
            curve = new AnimationCurve(
                new Keyframe(0f, 0f, 0f, 0f),
                new Keyframe(.5f, .25f, 1f, 1f),
                new Keyframe(1f, 1f, 2f, 2f));
        }
        else
        {
            curve = new AnimationCurve(
                new Keyframe(0f, 1f, 0f, 0f),
                new Keyframe(1f, 1f, 0f, 0f));
        }
        curve.preWrapMode = WrapMode.ClampForever;
        curve.postWrapMode = WrapMode.ClampForever;
        return curve;
    }

    private static CurveSet GetSet(int groupId)
    {
        if (!byGroup.TryGetValue(groupId, out CurveSet set) || set == null)
        {
            set = new CurveSet();
            byGroup[groupId] = set;
        }
        return set;
    }

    private static AnimationCurve SanitizeCurve(GroomShapeCurveChannel channel, AnimationCurve source)
    {
        if (source == null || source.length < 2) return CreateDefault(channel);

        List<Keyframe> sorted = source.keys
            .Select(k => new Keyframe(
                Mathf.Clamp01(k.time),
                Mathf.Clamp01(k.value),
                Finite(k.inTangent) ? k.inTangent : 0f,
                Finite(k.outTangent) ? k.outTangent : 0f))
            .OrderBy(k => k.time)
            .ToList();

        List<Keyframe> unique = new List<Keyframe>();
        foreach (Keyframe key in sorted)
        {
            if (unique.Count > 0 && Mathf.Abs(unique[unique.Count - 1].time - key.time) < .0001f)
                unique[unique.Count - 1] = key;
            else
                unique.Add(key);
        }

        if (unique.Count == 0) return CreateDefault(channel);
        if (unique[0].time > .0001f)
            unique.Insert(0, new Keyframe(0f, Mathf.Clamp01(source.Evaluate(0f))));
        else
        {
            Keyframe first = unique[0];
            first.time = 0f;
            unique[0] = first;
        }

        int lastIndex = unique.Count - 1;
        if (unique[lastIndex].time < .9999f)
            unique.Add(new Keyframe(1f, Mathf.Clamp01(source.Evaluate(1f))));
        else
        {
            Keyframe last = unique[lastIndex];
            last.time = 1f;
            unique[lastIndex] = last;
        }

        AnimationCurve result = new AnimationCurve(unique.ToArray());
        result.preWrapMode = WrapMode.ClampForever;
        result.postWrapMode = WrapMode.ClampForever;
        return result;
    }

    private static bool Finite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
}

[DefaultExecutionOrder(5210)]
public class GroomShapeCurveAuthority : MonoBehaviour
{
    private static HairProjectSaveData pendingRestore;
    private static int pendingRestoreFrames;

    private ModelViewer viewer;
    private GameObject boundPanel;
    private FieldInfo loadedModelField;
    private GameObject lastLoadedModel;
    private float nextScan;
    private GameObject popup;
    private GroomShapeCurveEditor popupEditor;
    private int popupGroup = -1;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<GroomShapeCurveAuthority>() != null) return;
        GameObject go = new GameObject("GroomShapeCurveAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<GroomShapeCurveAuthority>();
    }

    public static void Capture(HairProjectSaveData data)
    {
        if (data == null || data.groups == null) return;
        foreach (GroupSaveData group in data.groups)
        {
            if (group == null) continue;
            group.bendCurve = GroomShapeCurveRegistry.Export(group.groupId, GroomShapeCurveChannel.Bend);
            group.xAngleCurve = GroomShapeCurveRegistry.Export(group.groupId, GroomShapeCurveChannel.X);
            group.yAngleCurve = GroomShapeCurveRegistry.Export(group.groupId, GroomShapeCurveChannel.Y);
            group.zAngleCurve = GroomShapeCurveRegistry.Export(group.groupId, GroomShapeCurveChannel.Z);
            group.curlFrequencyCurve = GroomShapeCurveRegistry.Export(group.groupId, GroomShapeCurveChannel.CurlFrequency);
            group.curlDiameterCurve = GroomShapeCurveRegistry.Export(group.groupId, GroomShapeCurveChannel.CurlDiameter);
        }
    }

    public static void QueueRestore(HairProjectSaveData data)
    {
        pendingRestore = data;
        pendingRestoreFrames = 0;
    }

    void Update()
    {
        ResolveViewer();
        if (viewer == null) return;

        CheckModelLifecycle();
        TryRestorePending();

        if (popup != null && popupGroup != viewer.currentGroupId)
            ClosePopup();

        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + .10f;

        if (viewer.groomingSliderPanelGO == null) return;
        if (boundPanel != viewer.groomingSliderPanelGO)
        {
            boundPanel = viewer.groomingSliderPanelGO;
            ClosePopup();
        }

        EnsureCurveRow("Bend Angle_Row", "BEND PROFILE", GroomShapeCurveChannel.Bend);
        EnsureCurveRow("Offset X_Row", "X ANGLE PROFILE", GroomShapeCurveChannel.X);
        EnsureCurveRow("Offset Y_Row", "Y ANGLE PROFILE", GroomShapeCurveChannel.Y);
        EnsureCurveRow("Offset Z_Row", "Z ANGLE PROFILE", GroomShapeCurveChannel.Z);
        EnsureCurveRow("Curl Frequency_Row", "CURL FREQUENCY PROFILE", GroomShapeCurveChannel.CurlFrequency);
        EnsureCurveRow("Curl Diameter_Row", "CURL DIAMETER PROFILE", GroomShapeCurveChannel.CurlDiameter);
    }

    private void ResolveViewer()
    {
        if (viewer != null) return;
        viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer == null) return;
        loadedModelField = typeof(ModelViewer).GetField("loadedModel", BindingFlags.Instance | BindingFlags.NonPublic);
        lastLoadedModel = loadedModelField?.GetValue(viewer) as GameObject;
    }

    private void CheckModelLifecycle()
    {
        if (loadedModelField == null || viewer == null) return;
        GameObject loaded = loadedModelField.GetValue(viewer) as GameObject;
        if (loaded == lastLoadedModel) return;

        lastLoadedModel = loaded;
        GroomShapeCurveRegistry.ClearAll();
        ClosePopup();
        pendingRestoreFrames = 0;
    }

    private void TryRestorePending()
    {
        if (pendingRestore == null) return;

        int expectedCards = pendingRestore.hairCards != null ? pendingRestore.hairCards.Count : 0;
        int actualCards = FindObjectsByType<HairCard>(FindObjectsSortMode.None).Length;
        if (actualCards < expectedCards) return;
        if (++pendingRestoreFrames < 2) return;

        HairProjectSaveData restore = pendingRestore;
        pendingRestore = null;
        pendingRestoreFrames = 0;
        GroomShapeCurveRegistry.ClearAll();

        if (restore.groups != null)
        {
            foreach (GroupSaveData group in restore.groups)
            {
                if (group == null) continue;
                GroomShapeCurveRegistry.Import(group.groupId, GroomShapeCurveChannel.Bend, group.bendCurve);
                GroomShapeCurveRegistry.Import(group.groupId, GroomShapeCurveChannel.X, group.xAngleCurve);
                GroomShapeCurveRegistry.Import(group.groupId, GroomShapeCurveChannel.Y, group.yAngleCurve);
                GroomShapeCurveRegistry.Import(group.groupId, GroomShapeCurveChannel.Z, group.zAngleCurve);
                GroomShapeCurveRegistry.Import(group.groupId, GroomShapeCurveChannel.CurlFrequency, group.curlFrequencyCurve);
                GroomShapeCurveRegistry.Import(group.groupId, GroomShapeCurveChannel.CurlDiameter, group.curlDiameterCurve);
                GroomShapeCurveRegistry.RefreshGroup(group.groupId);
            }
        }
    }

    private void EnsureCurveRow(string targetRowName, string label, GroomShapeCurveChannel channel)
    {
        if (boundPanel == null) return;
        Transform target = boundPanel.transform.Find(targetRowName);
        if (target == null) return;

        string rowName = "ShapeCurve_" + channel + "_Row";
        Transform existing = boundPanel.transform.Find(rowName);
        GameObject row;
        if (existing == null)
            row = BuildCurveRow(rowName, label, channel);
        else
            row = existing.gameObject;

        row.SetActive(target.gameObject.activeSelf);
        int desired = Mathf.Min(target.GetSiblingIndex() + 1, boundPanel.transform.childCount - 1);
        if (row.transform.GetSiblingIndex() != desired)
            row.transform.SetSiblingIndex(desired);
    }

    private GameObject BuildCurveRow(string rowName, string label, GroomShapeCurveChannel channel)
    {
        GameObject row = new GameObject(rowName, typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        row.transform.SetParent(boundPanel.transform, false);
        row.GetComponent<LayoutElement>().preferredHeight = 27f;

        HorizontalLayoutGroup layout = row.GetComponent<HorizontalLayoutGroup>();
        layout.spacing = 6f;
        layout.padding = new RectOffset(135, 0, 0, 0);
        layout.childControlWidth = false;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;

        AddRowLabel(row.transform, label, 150f);
        AddButton(row.transform, "EDIT CURVE", 112f, () => OpenEditor(channel));
        AddButton(row.transform, "RESET", 68f, () =>
        {
            int gid = viewer != null ? viewer.currentGroupId : 0;
            GroomShapeCurveRegistry.Reset(gid, channel);
            GroomShapeCurveRegistry.RefreshGroup(gid);
            if (popupEditor != null && popupGroup == gid && popupEditor.Channel == channel)
                popupEditor.RefreshAll();
        });
        return row;
    }

    private void OpenEditor(GroomShapeCurveChannel channel)
    {
        if (viewer == null || boundPanel == null) return;

        bool sameEditorAlreadyOpen = popup != null && popupEditor != null
            && popupGroup == viewer.currentGroupId && popupEditor.Channel == channel;
        if (sameEditorAlreadyOpen)
        {
            ClosePopup();
            return;
        }

        ClosePopup();

        Canvas canvas = boundPanel.GetComponentInParent<Canvas>();
        if (canvas == null) return;

        popupGroup = viewer.currentGroupId;
        popup = new GameObject("GroomShapeCurveEditor", typeof(RectTransform), typeof(Image));
        popup.transform.SetParent(canvas.transform, false);
        popup.transform.SetAsLastSibling();

        RectTransform root = popup.GetComponent<RectTransform>();
        root.anchorMin = new Vector2(.5f, .5f);
        root.anchorMax = new Vector2(.5f, .5f);
        root.pivot = new Vector2(.5f, .5f);
        root.sizeDelta = new Vector2(670f, 455f);
        root.anchoredPosition = Vector2.zero;
        popup.GetComponent<Image>().color = new Color(.105f, .115f, .13f, .985f);

        AddPopupText(root, "Title", ChannelTitle(channel) + "  •  LENGTH CURVE", 18f,
            new Vector2(.05f, .90f), new Vector2(.95f, .98f), TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
        AddPopupText(root, "Hint", "ROOT 0 → TIP 1    |    curve value = 0–1 multiplier    |    click graph to add • drag points • right-click point to remove", 11f,
            new Vector2(.05f, .835f), new Vector2(.95f, .90f), TextAlignmentOptions.MidlineLeft, FontStyles.Normal);

        GameObject graphGO = new GameObject("Graph", typeof(RectTransform), typeof(Image), typeof(GroomCurveGraphInput));
        graphGO.transform.SetParent(root, false);
        RectTransform graph = graphGO.GetComponent<RectTransform>();
        graph.anchorMin = new Vector2(.07f, .18f);
        graph.anchorMax = new Vector2(.93f, .82f);
        graph.offsetMin = Vector2.zero;
        graph.offsetMax = Vector2.zero;
        graphGO.GetComponent<Image>().color = new Color(.055f, .06f, .07f, 1f);

        Transform gridRoot = CreateStretchChild(graph, "Grid").transform;
        Transform lineRoot = CreateStretchChild(graph, "CurveLine").transform;
        Transform pointRoot = CreateStretchChild(graph, "Points").transform;
        BuildGrid(gridRoot);

        AddPopupText(root, "Zero", "0", 10f, new Vector2(.035f, .16f), new Vector2(.065f, .21f), TextAlignmentOptions.Center, FontStyles.Normal);
        AddPopupText(root, "One", "1", 10f, new Vector2(.035f, .79f), new Vector2(.065f, .84f), TextAlignmentOptions.Center, FontStyles.Normal);
        AddPopupText(root, "RootLabel", "ROOT", 10f, new Vector2(.06f, .115f), new Vector2(.14f, .17f), TextAlignmentOptions.Center, FontStyles.Bold);
        AddPopupText(root, "TipLabel", "TIP", 10f, new Vector2(.86f, .115f), new Vector2(.94f, .17f), TextAlignmentOptions.Center, FontStyles.Bold);

        popupEditor = popup.AddComponent<GroomShapeCurveEditor>();
        popupEditor.Bind(this, popupGroup, channel, graph, lineRoot, pointRoot);
        graphGO.GetComponent<GroomCurveGraphInput>().Bind(popupEditor);

        AddPopupButton(root, "RESET", new Vector2(.41f, .060f), new Vector2(.59f, .080f), popupEditor.ResetDefault);

        Canvas.ForceUpdateCanvases();
        popupEditor.RefreshAll();
    }

    public void ClosePopup()
    {
        if (popup != null) Destroy(popup);
        popup = null;
        popupEditor = null;
        popupGroup = -1;
    }

    private static string ChannelTitle(GroomShapeCurveChannel channel)
    {
        switch (channel)
        {
            case GroomShapeCurveChannel.Bend: return "BEND";
            case GroomShapeCurveChannel.X: return "X ANGLE";
            case GroomShapeCurveChannel.Y: return "Y ANGLE";
            case GroomShapeCurveChannel.Z: return "Z ANGLE";
            case GroomShapeCurveChannel.CurlFrequency: return "CURL FREQUENCY";
            default: return "CURL DIAMETER";
        }
    }

    private static void AddRowLabel(Transform parent, string label, float width)
    {
        GameObject go = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        go.GetComponent<LayoutElement>().preferredWidth = width;
        go.GetComponent<LayoutElement>().preferredHeight = 25f;
        TextMeshProUGUI text = go.GetComponent<TextMeshProUGUI>();
        text.text = label;
        text.fontSize = 10f;
        text.color = new Color(.78f, .82f, .87f, 1f);
        text.alignment = TextAlignmentOptions.MidlineLeft;
        text.raycastTarget = false;
    }

    private static void AddButton(Transform parent, string label, float width, UnityEngine.Events.UnityAction action)
    {
        GameObject go = new GameObject(label + "Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        LayoutElement le = go.GetComponent<LayoutElement>();
        le.preferredWidth = width;
        le.minWidth = width;
        le.preferredHeight = 25f;
        le.minHeight = 25f;
        go.GetComponent<Image>().color = new Color(.20f, .42f, .67f, 1f);
        go.GetComponent<Button>().onClick.AddListener(action);
        AddButtonText(go.transform, label, 10f);
    }

    private static GameObject CreateStretchChild(RectTransform parent, string name)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        RectTransform rect = go.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        return go;
    }

    private static void BuildGrid(Transform root)
    {
        for (int i = 0; i <= 4; i++)
        {
            float n = i / 4f;
            CreateGridLine(root, true, n);
            CreateGridLine(root, false, n);
        }
    }

    private static void CreateGridLine(Transform parent, bool vertical, float normalized)
    {
        GameObject go = new GameObject(vertical ? "VGrid" : "HGrid", typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        RectTransform rect = go.GetComponent<RectTransform>();
        if (vertical)
        {
            rect.anchorMin = new Vector2(normalized, 0f);
            rect.anchorMax = new Vector2(normalized, 1f);
            rect.sizeDelta = new Vector2(1f, 0f);
        }
        else
        {
            rect.anchorMin = new Vector2(0f, normalized);
            rect.anchorMax = new Vector2(1f, normalized);
            rect.sizeDelta = new Vector2(0f, 1f);
        }
        go.GetComponent<Image>().color = new Color(.23f, .25f, .29f, .55f);
        go.GetComponent<Image>().raycastTarget = false;
    }

    private static TextMeshProUGUI AddPopupText(RectTransform parent, string name, string content, float size,
        Vector2 anchorMin, Vector2 anchorMax, TextAlignmentOptions align, FontStyles style)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        RectTransform rect = go.GetComponent<RectTransform>();
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        TextMeshProUGUI text = go.GetComponent<TextMeshProUGUI>();
        text.text = content;
        text.fontSize = size;
        text.fontStyle = style;
        text.alignment = align;
        text.color = Color.white;
        text.raycastTarget = false;
        text.enableWordWrapping = false;
        return text;
    }

    private static void AddPopupButton(RectTransform parent, string label, Vector2 anchorMin, Vector2 anchorMax, UnityEngine.Events.UnityAction action)
    {
        GameObject go = new GameObject(label + "Button", typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        RectTransform rect = go.GetComponent<RectTransform>();
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        go.GetComponent<Image>().color = new Color(.20f, .42f, .67f, 1f);
        go.GetComponent<Button>().onClick.AddListener(action);
        AddButtonText(go.transform, label, 11f);
    }

    private static void AddButtonText(Transform parent, string label, float size)
    {
        GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textGO.transform.SetParent(parent, false);
        RectTransform rect = textGO.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        TextMeshProUGUI text = textGO.GetComponent<TextMeshProUGUI>();
        text.text = label;
        text.fontSize = size;
        text.fontStyle = FontStyles.Bold;
        text.alignment = TextAlignmentOptions.Center;
        text.color = Color.white;
        text.raycastTarget = false;
    }
}

public class GroomShapeCurveEditor : MonoBehaviour
{
    private const int SampleCount = 64;
    private readonly List<RectTransform> lineSegments = new List<RectTransform>();
    private readonly List<GroomCurvePointHandle> pointHandles = new List<GroomCurvePointHandle>();

    private GroomShapeCurveAuthority owner;
    private int groupId;
    private GroomShapeCurveChannel channel;
    private RectTransform graph;
    private Transform lineRoot;
    private Transform pointRoot;

    public GroomShapeCurveChannel Channel => channel;

    public void Bind(GroomShapeCurveAuthority authority, int gid, GroomShapeCurveChannel curveChannel,
        RectTransform graphRect, Transform lines, Transform points)
    {
        owner = authority;
        groupId = gid;
        channel = curveChannel;
        graph = graphRect;
        lineRoot = lines;
        pointRoot = points;
    }

    public void RefreshAll()
    {
        if (graph == null) return;
        Canvas.ForceUpdateCanvases();
        EnsureLines();
        EnsurePoints();
        RefreshLines();
        RefreshPoints();
    }

    public void ResetDefault()
    {
        GroomShapeCurveRegistry.Reset(groupId, channel);
        GroomShapeCurveRegistry.RefreshGroup(groupId);
        RefreshAll();
    }

    public void AddKey(Vector2 normalized)
    {
        AnimationCurve curve = GroomShapeCurveRegistry.GetCurve(groupId, channel);
        float time = Mathf.Clamp(normalized.x, .005f, .995f);
        float value = Mathf.Clamp01(normalized.y);

        foreach (Keyframe existing in curve.keys)
            if (Mathf.Abs(existing.time - time) < .012f)
                return;

        curve.AddKey(new Keyframe(time, value));
        Smooth(curve);
        GroomShapeCurveRegistry.RefreshGroup(groupId);
        RefreshAll();
    }

    public void DragKey(GroomCurvePointHandle handle, int index, Vector2 normalized)
    {
        AnimationCurve curve = GroomShapeCurveRegistry.GetCurve(groupId, channel);
        Keyframe[] keys = curve.keys;
        if (index < 0 || index >= keys.Length) return;

        float time;
        if (index == 0) time = 0f;
        else if (index == keys.Length - 1) time = 1f;
        else time = Mathf.Clamp(normalized.x, keys[index - 1].time + .005f, keys[index + 1].time - .005f);
        float value = Mathf.Clamp01(normalized.y);

        Keyframe moved = keys[index];
        moved.time = time;
        moved.value = value;
        int newIndex = curve.MoveKey(index, moved);
        Smooth(curve);
        handle.SetKeyIndex(newIndex);
        GroomShapeCurveRegistry.RefreshGroup(groupId);
        RefreshLines();
        RefreshPoints();
    }

    public void RemoveKey(int index)
    {
        AnimationCurve curve = GroomShapeCurveRegistry.GetCurve(groupId, channel);
        if (index <= 0 || index >= curve.length - 1) return;
        curve.RemoveKey(index);
        Smooth(curve);
        GroomShapeCurveRegistry.RefreshGroup(groupId);
        RefreshAll();
    }

    public bool ScreenToNormalized(PointerEventData eventData, out Vector2 normalized)
    {
        normalized = Vector2.zero;
        if (graph == null || eventData == null) return false;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(graph, eventData.position, eventData.pressEventCamera, out Vector2 local))
            return false;
        Rect rect = graph.rect;
        if (rect.width <= .001f || rect.height <= .001f) return false;
        normalized.x = Mathf.Clamp01((local.x - rect.xMin) / rect.width);
        normalized.y = Mathf.Clamp01((local.y - rect.yMin) / rect.height);
        return true;
    }

    private void EnsureLines()
    {
        if (lineRoot == null) return;
        int wanted = SampleCount - 1;
        while (lineSegments.Count < wanted)
        {
            GameObject go = new GameObject("Segment", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(lineRoot, false);
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(.5f, .5f);
            rect.anchorMax = new Vector2(.5f, .5f);
            rect.pivot = new Vector2(.5f, .5f);
            Image image = go.GetComponent<Image>();
            image.color = new Color(.22f, .72f, 1f, 1f);
            image.raycastTarget = false;
            lineSegments.Add(rect);
        }
    }

    private void EnsurePoints()
    {
        if (pointRoot == null) return;
        int wanted = GroomShapeCurveRegistry.GetCurve(groupId, channel).length;
        while (pointHandles.Count < wanted)
        {
            GameObject go = new GameObject("CurvePoint", typeof(RectTransform), typeof(Image), typeof(GroomCurvePointHandle));
            go.transform.SetParent(pointRoot, false);
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(.5f, .5f);
            rect.anchorMax = new Vector2(.5f, .5f);
            rect.pivot = new Vector2(.5f, .5f);
            rect.sizeDelta = new Vector2(14f, 14f);
            go.GetComponent<Image>().color = new Color(1f, .78f, .24f, 1f);
            GroomCurvePointHandle handle = go.GetComponent<GroomCurvePointHandle>();
            handle.Bind(this, pointHandles.Count);
            pointHandles.Add(handle);
        }
        while (pointHandles.Count > wanted)
        {
            int last = pointHandles.Count - 1;
            if (pointHandles[last] != null) Destroy(pointHandles[last].gameObject);
            pointHandles.RemoveAt(last);
        }
        for (int i = 0; i < pointHandles.Count; i++)
            if (pointHandles[i] != null) pointHandles[i].Bind(this, i);
    }

    private void RefreshLines()
    {
        if (graph == null) return;
        EnsureLines();
        Rect rect = graph.rect;
        AnimationCurve curve = GroomShapeCurveRegistry.GetCurve(groupId, channel);
        for (int i = 0; i < lineSegments.Count; i++)
        {
            float t0 = i / (float)(SampleCount - 1);
            float t1 = (i + 1) / (float)(SampleCount - 1);
            Vector2 a = GraphPoint(rect, t0, Mathf.Clamp01(curve.Evaluate(t0)));
            Vector2 b = GraphPoint(rect, t1, Mathf.Clamp01(curve.Evaluate(t1)));
            Vector2 delta = b - a;
            RectTransform line = lineSegments[i];
            if (line == null) continue;
            line.anchoredPosition = (a + b) * .5f;
            line.sizeDelta = new Vector2(delta.magnitude + 1f, 2.5f);
            line.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
        }
    }

    private void RefreshPoints()
    {
        if (graph == null) return;
        EnsurePoints();
        Rect rect = graph.rect;
        Keyframe[] keys = GroomShapeCurveRegistry.GetCurve(groupId, channel).keys;
        for (int i = 0; i < pointHandles.Count && i < keys.Length; i++)
        {
            GroomCurvePointHandle handle = pointHandles[i];
            if (handle == null) continue;
            handle.SetKeyIndex(i);
            handle.GetComponent<RectTransform>().anchoredPosition = GraphPoint(rect, keys[i].time, keys[i].value);
        }
    }

    private static Vector2 GraphPoint(Rect rect, float x, float y)
    {
        return new Vector2(rect.xMin + Mathf.Clamp01(x) * rect.width, rect.yMin + Mathf.Clamp01(y) * rect.height);
    }

    private static void Smooth(AnimationCurve curve)
    {
        if (curve == null) return;
        for (int i = 0; i < curve.length; i++)
            curve.SmoothTangents(i, 0f);
        curve.preWrapMode = WrapMode.ClampForever;
        curve.postWrapMode = WrapMode.ClampForever;
    }
}

public class GroomCurveGraphInput : MonoBehaviour, IPointerClickHandler
{
    private GroomShapeCurveEditor editor;

    public void Bind(GroomShapeCurveEditor curveEditor)
    {
        editor = curveEditor;
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        if (editor == null || eventData == null || eventData.button != PointerEventData.InputButton.Left) return;
        if (editor.ScreenToNormalized(eventData, out Vector2 normalized))
            editor.AddKey(normalized);
    }
}

public class GroomCurvePointHandle : MonoBehaviour, IDragHandler, IPointerDownHandler, IPointerClickHandler
{
    private GroomShapeCurveEditor editor;
    private int keyIndex;

    public void Bind(GroomShapeCurveEditor curveEditor, int index)
    {
        editor = curveEditor;
        keyIndex = index;
    }

    public void SetKeyIndex(int index)
    {
        keyIndex = index;
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (editor == null) return;
        if (editor.ScreenToNormalized(eventData, out Vector2 normalized))
            editor.DragKey(this, keyIndex, normalized);
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        if (editor == null || eventData == null) return;
        if (eventData.button == PointerEventData.InputButton.Right)
        {
            editor.RemoveKey(keyIndex);
            eventData.Use();
        }
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        // Consume point clicks so they never bubble to the graph and accidentally create
        // a second key underneath the handle the user was trying to select/drag.
        if (eventData != null) eventData.Use();
    }
}
