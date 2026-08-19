using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// Ctrl+Click creates a persistent localized post-affector for the active group.
// Evaluation is deterministic: canonical/authored card state -> POST affectors -> rendered card.
// Evaluated values are never fed back into canonical state.
[DefaultExecutionOrder(3300)]
public class PostAffectorManager : MonoBehaviour
{
    [Serializable]
    public class PostAffector
    {
        public int id;
        public int groupId;
        public Vector3 center;
        public Vector3 normal;
        public float radius = .02f;
        public float falloff = .03f;
        [Range(0f, 1f)] public float weight = 1f;
        public ControlState baseline;
        public ControlState delta;
    }

    [Serializable]
    public struct ControlState
    {
        public float length, width, bend, twist, depth;
        public float segments;
        public float x, y, z;
        public float uScale, vScale, uOffset, vOffset;
    }

    private class CardState
    {
        public ControlState baseState;
        public ControlState lastFinal;
        public bool hasFinal;
    }

    private readonly Dictionary<int, List<PostAffector>> groups = new();
    private readonly Dictionary<HairCard, CardState> cardStates = new();
    private readonly Dictionary<int, bool> predeterminedUVByGroup = new();

    private ModelViewer viewer;
    private GroupPredeterminedUVController uvRouting;
    private FieldInfo hasSelectionField;
    private FieldInfo hitPointField;
    private FieldInfo hitNormalField;
    private FieldInfo strengthRowField;
    private int nextId = 1;
    private int activeId = -1;
    private int activeGroup = -1;
    private float nextUIScan;
    private int lastCreatedFrame = -1;
    private int predeterminedUVCacheFrame = -1;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<PostAffectorManager>() != null) return;
        GameObject go = new GameObject("PostAffectorManager");
        DontDestroyOnLoad(go);
        go.AddComponent<PostAffectorManager>();
    }

    void Update()
    {
        EnsureViewer();
        if (viewer == null) return;

        DetectGroupRootSelection();
        DetectCtrlClick();
        MaintainActiveAuthoring();

        if (Time.unscaledTime >= nextUIScan)
        {
            nextUIScan = Time.unscaledTime + .12f;
            EnsureRowsAndOrder();
            RenameLegacyStrengthToWeight();
        }
    }

    void LateUpdate()
    {
        EnsureViewer();
        if (viewer == null) return;
        UpdateCanonicalBases();
        ApplyAll();
    }

    void EnsureViewer()
    {
        if (viewer != null) return;
        viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer == null) return;
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Type t = typeof(ModelViewer);
        hasSelectionField = t.GetField("hasSelectionHotspot", flags);
        hitPointField = t.GetField("selectionHitPoint", flags);
        hitNormalField = t.GetField("selectionHitNormal", flags);
        strengthRowField = t.GetField("strengthRowGO", flags);
    }

    void DetectGroupRootSelection()
    {
        if (activeId < 0 || EventSystem.current == null) return;
        GameObject selected = EventSystem.current.currentSelectedGameObject;
        if (selected == null || selected.name != "LabelButton") return;
        Transform item = selected.transform.parent;
        if (item == null || !item.name.StartsWith("GroupItem_")) return;

        activeId = -1;
        activeGroup = -1;
        SetField(hasSelectionField, false);
    }

    void DetectCtrlClick()
    {
        if (Mouse.current == null || Keyboard.current == null) return;
        if (!Keyboard.current.ctrlKey.isPressed || !Mouse.current.leftButton.wasPressedThisFrame) return;
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;
        if (!HasSelection()) return;
        if (lastCreatedFrame == Time.frameCount) return;
        lastCreatedFrame = Time.frameCount;
        CreateAffector(viewer.currentGroupId, GetVector(hitPointField), GetVector(hitNormalField));
    }

    void CreateAffector(int groupId, Vector3 center, Vector3 normal)
    {
        if (!groups.TryGetValue(groupId, out List<PostAffector> list))
        {
            list = new List<PostAffector>();
            groups[groupId] = list;
        }

        PostAffector a = new PostAffector
        {
            id = nextId++,
            groupId = groupId,
            center = center,
            normal = normal.sqrMagnitude > .000001f ? normal.normalized : Vector3.up,
            radius = Mathf.Clamp(viewer.brushRadius, .001f, .25f),
            falloff = Mathf.Clamp(viewer.brushFalloffDistance, 0f, .25f),
            weight = 1f,
            baseline = ReadControls(),
            delta = new ControlState()
        };
        list.Add(a);
        activeId = a.id;
        activeGroup = groupId;
        viewer.selectionStrength = 1f;

        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None).Where(c => c.groupId == groupId))
        {
            if (!cardStates.ContainsKey(card))
            {
                ControlState canonical = ReadCanonical(card);
                cardStates[card] = new CardState { baseState = canonical, lastFinal = canonical, hasFinal = false };
            }
        }

        RebuildGroupRows(groupId);
    }

    void MaintainActiveAuthoring()
    {
        PostAffector active = GetActive();
        if (active == null) return;
        if (!HasSelection() || viewer.currentGroupId != active.groupId)
        {
            activeId = -1;
            activeGroup = -1;
            return;
        }

        active.center = GetVector(hitPointField);
        active.normal = GetVector(hitNormalField);
        active.radius = Mathf.Clamp(viewer.brushRadius, .001f, .25f);
        active.falloff = Mathf.Clamp(viewer.brushFalloffDistance, 0f, .25f);

        if (!Mathf.Approximately(viewer.selectionStrength, active.weight))
        {
            active.weight = Mathf.Clamp01(viewer.selectionStrength);
            RebuildGroupRows(active.groupId);
        }

        active.delta = Subtract(ReadControls(), active.baseline);
    }

    // Canonical state is the only upstream source of truth. While a POST is actively
    // authored, ModelViewer's legacy selection path may still call SetParameters on cards;
    // restore canonical immediately so those preview writes cannot pollute the group root.
    void UpdateCanonicalBases()
    {
        bool editingPost = GetActive() != null && HasSelection();

        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
        {
            if (!cardStates.TryGetValue(card, out CardState state))
            {
                ControlState canonical = ReadCanonical(card);
                state = new CardState { baseState = canonical, lastFinal = canonical, hasFinal = false };
                cardStates[card] = state;
            }

            if (editingPost)
            {
                WriteCanonicalOnly(card, state.baseState);
            }
            else
            {
                state.baseState = ReadCanonical(card);
            }
        }

        foreach (HairCard dead in cardStates.Keys.Where(c => c == null).ToArray())
            cardStates.Remove(dead);
    }

    void ApplyAll()
    {
        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
        {
            if (!cardStates.TryGetValue(card, out CardState state))
            {
                ControlState canonical = ReadCanonical(card);
                state = new CardState { baseState = canonical, lastFinal = canonical, hasFinal = false };
                cardStates[card] = state;
            }

            ControlState result = state.baseState;
            if (groups.TryGetValue(card.groupId, out List<PostAffector> list))
                result = Add(result, EffectForCard(card, list));

            // UV MODE is group routing, not a POST-local property. PREDETERMINED therefore
            // hard-routes the final UVs from the card's canonical group assignment and ignores
            // any older Adjustable UV delta stored inside POST. The delta is retained so it can
            // become active again if the whole group is later switched back to ADJUSTABLE.
            if (UsesPredeterminedUVs(card.groupId))
            {
                ControlState canonicalUV = ReadCanonical(card);
                CopyUV(ref result, canonicalUV);
            }

            // Skip the expensive per-card write/mesh-rebuild when this frame's evaluated state
            // is identical to what was already applied last frame. With any POST present this
            // loop runs every frame forever, but the result only actually changes while a
            // control is being dragged - so this turns the steady-state cost of N full mesh
            // regenerations per frame into N struct comparisons.
            if (!state.hasFinal || !StatesEqual(state.lastFinal, result))
                WriteEvaluatedCard(card, result);
            state.lastFinal = result;
            state.hasFinal = true;
        }
    }

    static bool StatesEqual(ControlState a, ControlState b)
    {
        return Mathf.Approximately(a.length, b.length) &&
               Mathf.Approximately(a.width, b.width) &&
               Mathf.Approximately(a.segments, b.segments) &&
               Mathf.Approximately(a.bend, b.bend) &&
               Mathf.Approximately(a.twist, b.twist) &&
               Mathf.Approximately(a.depth, b.depth) &&
               Mathf.Approximately(a.x, b.x) &&
               Mathf.Approximately(a.y, b.y) &&
               Mathf.Approximately(a.z, b.z) &&
               Mathf.Approximately(a.uScale, b.uScale) &&
               Mathf.Approximately(a.vScale, b.vScale) &&
               Mathf.Approximately(a.uOffset, b.uOffset) &&
               Mathf.Approximately(a.vOffset, b.vOffset);
    }

    ControlState EffectForCard(HairCard card, List<PostAffector> list)
    {
        ControlState effect = new ControlState();
        foreach (PostAffector a in list)
        {
            float w = SpatialWeight(card, a) * Mathf.Clamp01(a.weight);
            if (w > .000001f) effect = Add(effect, Scale(a.delta, w));
        }
        return effect;
    }

    float SpatialWeight(HairCard card, PostAffector a)
    {
        Vector3 p = card.GetSpawnHitPoint();
        if (p == Vector3.zero) p = card.transform.position;
        float d = Vector3.Distance(p, a.center);
        float radius = Mathf.Max(.001f, a.radius);
        float outer = radius + Mathf.Max(0f, a.falloff);
        if (d <= radius) return 1f;
        if (a.falloff <= .000001f || d >= outer) return 0f;
        return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(outer, radius, d));
    }

    void WriteEvaluatedCard(HairCard card, ControlState s)
    {
        card.ApplyEvaluatedState(ToGroomState(s));
    }

    void WriteCanonicalOnly(HairCard card, ControlState s)
    {
        // While a POST is selected, preserve PREDETERMINED's per-card canonical rectangle.
        // This prevents the legacy POST baseline cache from writing an old Adjustable UV state
        // back into canonical data during active authoring.
        if (UsesPredeterminedUVs(card.groupId))
        {
            ControlState canonicalUV = ReadCanonical(card);
            CopyUV(ref s, canonicalUV);
        }
        card.SetCanonicalState(ToGroomState(s), false);
    }

    bool UsesPredeterminedUVs(int groupId)
    {
        if (predeterminedUVCacheFrame != Time.frameCount)
        {
            predeterminedUVCacheFrame = Time.frameCount;
            predeterminedUVByGroup.Clear();
        }

        if (predeterminedUVByGroup.TryGetValue(groupId, out bool cached)) return cached;

        if (uvRouting == null) uvRouting = FindFirstObjectByType<GroupPredeterminedUVController>();
        bool predetermined = false;
        if (uvRouting != null)
        {
            GroupSaveData probe = new GroupSaveData { groupId = groupId };
            uvRouting.PopulateGroupSave(probe);
            predetermined = probe.usePredeterminedUVs;
        }

        predeterminedUVByGroup[groupId] = predetermined;
        return predetermined;
    }

    static void CopyUV(ref ControlState target, ControlState source)
    {
        target.uScale = source.uScale;
        target.vScale = source.vScale;
        target.uOffset = source.uOffset;
        target.vOffset = source.vOffset;
    }

    HairCard.GroomState ToGroomState(ControlState s)
    {
        return new HairCard.GroomState
        {
            length = Mathf.Max(.0001f, s.length),
            width = Mathf.Max(.0005f, s.width),
            segments = Mathf.Clamp(Mathf.RoundToInt(s.segments), 4, 60),
            bend = s.bend,
            twist = s.twist,
            depth = Mathf.Max(0f, s.depth),
            x = s.x,
            y = s.y,
            z = s.z,
            uScale = s.uScale,
            vScale = s.vScale,
            uOffset = s.uOffset,
            vOffset = s.vOffset
        };
    }

    void EnsureRowsAndOrder()
    {
        RectTransform[] all = FindObjectsByType<RectTransform>(FindObjectsSortMode.None);
        foreach (RectTransform groupItem in all.Where(r => r.name.StartsWith("GroupItem_")))
        {
            if (!int.TryParse(groupItem.name.Substring("GroupItem_".Length), out int gid)) continue;
            Transform parent = groupItem.parent;
            if (parent == null) continue;

            List<PostAffector> list = groups.TryGetValue(gid, out List<PostAffector> found) ? found : null;
            int insert = groupItem.GetSiblingIndex() + 1;
            if (list == null) continue;

            int number = 1;
            foreach (PostAffector a in list)
            {
                string rowName = RowName(gid, a.id);
                Transform row = parent.Find(rowName);
                if (row == null) row = BuildRow(parent, a, number).transform;
                row.SetSiblingIndex(insert++);
                number++;
            }
        }
    }

    GameObject BuildRow(Transform parent, PostAffector a, int number)
    {
        GameObject row = new GameObject(RowName(a.groupId, a.id), typeof(RectTransform), typeof(Image), typeof(HorizontalLayoutGroup));
        row.transform.SetParent(parent, false);
        row.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 34f);
        row.GetComponent<Image>().color = a.id == activeId ? new Color(.18f, .24f, .34f, .98f) : new Color(.12f, .14f, .18f, .98f);
        HorizontalLayoutGroup layout = row.GetComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(6, 6, 4, 4);
        layout.spacing = 5f;
        layout.childControlWidth = false;
        layout.childControlHeight = true;

        GameObject select = AddButton(row.transform, "POST " + number, 72f);
        select.GetComponent<Button>().onClick.AddListener(() => SelectAffector(a));
        TextMeshProUGUI wt = AddText(row.transform, "WEIGHT", 10, 45f);
        wt.alignment = TextAlignmentOptions.Center;

        Slider slider = AddWeightSlider(row.transform, a.weight, 128f);
        TextMeshProUGUI value = AddText(row.transform, a.weight.ToString("F2"), 10, 30f);
        value.alignment = TextAlignmentOptions.Center;
        slider.onValueChanged.AddListener(v =>
        {
            a.weight = Mathf.Clamp01(v);
            value.text = a.weight.ToString("F2");
            if (a.id == activeId)
            {
                viewer.selectionStrength = a.weight;
                RenameLegacyStrengthToWeight();
            }
        });

        GameObject remove = AddButton(row.transform, "[-]", 34f);
        remove.GetComponent<Button>().onClick.AddListener(() => RemoveAffector(a));
        return row;
    }

    void SelectAffector(PostAffector a)
    {
        activeId = a.id;
        activeGroup = a.groupId;
        viewer.currentGroupId = a.groupId;
        SetField(hasSelectionField, true);
        SetField(hitPointField, a.center);
        SetField(hitNormalField, a.normal);
        viewer.brushRadius = a.radius;
        viewer.brushFalloffDistance = a.falloff;
        viewer.selectionStrength = a.weight;
        ApplyControls(Add(a.baseline, a.delta));
        RebuildGroupRows(a.groupId);
    }

    void RemoveAffector(PostAffector a)
    {
        if (groups.TryGetValue(a.groupId, out List<PostAffector> list))
        {
            list.RemoveAll(x => x.id == a.id);
            if (list.Count == 0) groups.Remove(a.groupId);
        }
        if (activeId == a.id)
        {
            activeId = -1;
            activeGroup = -1;
            SetField(hasSelectionField, false);
        }
        RebuildGroupRows(a.groupId);
        ApplyAll();
    }

    void RebuildGroupRows(int gid)
    {
        foreach (RectTransform r in FindObjectsByType<RectTransform>(FindObjectsSortMode.None)
            .Where(r => r.name.StartsWith("PostAffector_" + gid + "_")))
            Destroy(r.gameObject);
        nextUIScan = 0f;
    }

    void RenameLegacyStrengthToWeight()
    {
        GameObject row = strengthRowField?.GetValue(viewer) as GameObject;
        if (row == null) return;
        TextMeshProUGUI label = row.GetComponentInChildren<TextMeshProUGUI>(true);
        if (label != null) label.text = "WEIGHT: " + viewer.selectionStrength.ToString("F3");
        Slider slider = row.GetComponentInChildren<Slider>(true);
        if (slider != null && !Mathf.Approximately(slider.value, viewer.selectionStrength))
            slider.SetValueWithoutNotify(viewer.selectionStrength);
    }

    GameObject AddButton(Transform parent, string text, float width)
    {
        GameObject go = new GameObject(text, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(width, 25f);
        go.GetComponent<Image>().color = new Color(.20f, .25f, .32f);
        TextMeshProUGUI t = AddText(go.transform, text, 10, width);
        RectTransform tr = t.rectTransform;
        tr.anchorMin = Vector2.zero;
        tr.anchorMax = Vector2.one;
        tr.offsetMin = Vector2.zero;
        tr.offsetMax = Vector2.zero;
        t.raycastTarget = false;
        return go;
    }

    TextMeshProUGUI AddText(Transform parent, string text, int size, float width)
    {
        GameObject go = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(width, 25f);
        TextMeshProUGUI t = go.GetComponent<TextMeshProUGUI>();
        t.text = text;
        t.fontSize = size;
        t.color = Color.white;
        t.alignment = TextAlignmentOptions.Center;
        return t;
    }

    Slider AddWeightSlider(Transform parent, float value, float width)
    {
        GameObject go = new GameObject("WeightSlider", typeof(RectTransform), typeof(Slider));
        go.transform.SetParent(parent, false);
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(width, 24f);
        Slider s = go.GetComponent<Slider>();
        s.minValue = 0f;
        s.maxValue = 1f;
        s.value = value;

        GameObject bg = new GameObject("Background", typeof(RectTransform), typeof(Image));
        bg.transform.SetParent(go.transform, false);
        RectTransform br = bg.GetComponent<RectTransform>();
        br.anchorMin = new Vector2(0, .42f);
        br.anchorMax = new Vector2(1, .58f);
        br.offsetMin = br.offsetMax = Vector2.zero;
        bg.GetComponent<Image>().color = new Color(.24f, .24f, .24f);

        GameObject fillArea = new GameObject("Fill Area", typeof(RectTransform));
        fillArea.transform.SetParent(go.transform, false);
        RectTransform far = fillArea.GetComponent<RectTransform>();
        far.anchorMin = new Vector2(0, .35f);
        far.anchorMax = new Vector2(1, .65f);
        far.offsetMin = new Vector2(4, 0);
        far.offsetMax = new Vector2(-4, 0);

        GameObject fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
        fill.transform.SetParent(fillArea.transform, false);
        RectTransform fr = fill.GetComponent<RectTransform>();
        fr.anchorMin = Vector2.zero;
        fr.anchorMax = Vector2.one;
        fr.offsetMin = fr.offsetMax = Vector2.zero;
        fill.GetComponent<Image>().color = new Color(.28f, .58f, .95f);
        s.fillRect = fr;

        GameObject ha = new GameObject("Handle Slide Area", typeof(RectTransform));
        ha.transform.SetParent(go.transform, false);
        RectTransform har = ha.GetComponent<RectTransform>();
        har.anchorMin = Vector2.zero;
        har.anchorMax = Vector2.one;
        har.offsetMin = new Vector2(5, 0);
        har.offsetMax = new Vector2(-5, 0);

        GameObject h = new GameObject("Handle", typeof(RectTransform), typeof(Image));
        h.transform.SetParent(ha.transform, false);
        RectTransform hr = h.GetComponent<RectTransform>();
        hr.sizeDelta = new Vector2(9, 16);
        h.GetComponent<Image>().color = Color.white;
        s.handleRect = hr;
        return s;
    }

    string RowName(int gid, int id) => "PostAffector_" + gid + "_" + id;
    PostAffector GetActive() => groups.TryGetValue(activeGroup, out List<PostAffector> list) ? list.FirstOrDefault(a => a.id == activeId) : null;
    bool HasSelection() => hasSelectionField != null && hasSelectionField.GetValue(viewer) is bool b && b;
    Vector3 GetVector(FieldInfo f) => f != null && f.GetValue(viewer) is Vector3 v ? v : Vector3.zero;
    void SetField(FieldInfo f, object value) { if (f != null) f.SetValue(viewer, value); }

    ControlState ReadControls() => new ControlState
    {
        length = viewer.currentLength,
        width = viewer.currentWidth,
        segments = viewer.currentSegments,
        bend = viewer.currentBend,
        twist = viewer.currentTwist,
        depth = viewer.currentEmbedDepth,
        x = viewer.currentOffsetX,
        y = viewer.currentOffsetY,
        z = viewer.currentOffsetZ,
        uScale = viewer.currentUScale,
        vScale = viewer.currentVScale,
        uOffset = viewer.currentUOffset,
        vOffset = viewer.currentVOffset
    };

    ControlState ReadCanonical(HairCard c)
    {
        HairCard.GroomState s = c.GetCanonicalState();
        return new ControlState
        {
            length = s.length,
            width = s.width,
            segments = s.segments,
            bend = s.bend,
            twist = s.twist,
            depth = s.depth,
            x = s.x,
            y = s.y,
            z = s.z,
            uScale = s.uScale,
            vScale = s.vScale,
            uOffset = s.uOffset,
            vOffset = s.vOffset
        };
    }

    void ApplyControls(ControlState s)
    {
        viewer.currentLength = s.length;
        viewer.currentWidth = s.width;
        viewer.currentSegments = Mathf.RoundToInt(s.segments);
        viewer.currentBend = s.bend;
        viewer.currentTwist = s.twist;
        viewer.currentEmbedDepth = s.depth;
        viewer.currentOffsetX = s.x;
        viewer.currentOffsetY = s.y;
        viewer.currentOffsetZ = s.z;
        viewer.currentUScale = s.uScale;
        viewer.currentVScale = s.vScale;
        viewer.currentUOffset = s.uOffset;
        viewer.currentVOffset = s.vOffset;
    }

    static ControlState Add(ControlState a, ControlState b) => new ControlState
    {
        length = a.length + b.length, width = a.width + b.width, segments = a.segments + b.segments,
        bend = a.bend + b.bend, twist = a.twist + b.twist, depth = a.depth + b.depth,
        x = a.x + b.x, y = a.y + b.y, z = a.z + b.z,
        uScale = a.uScale + b.uScale, vScale = a.vScale + b.vScale,
        uOffset = a.uOffset + b.uOffset, vOffset = a.vOffset + b.vOffset
    };

    static ControlState Subtract(ControlState a, ControlState b) => new ControlState
    {
        length = a.length - b.length, width = a.width - b.width, segments = a.segments - b.segments,
        bend = a.bend - b.bend, twist = a.twist - b.twist, depth = a.depth - b.depth,
        x = a.x - b.x, y = a.y - b.y, z = a.z - b.z,
        uScale = a.uScale - b.uScale, vScale = a.vScale - b.vScale,
        uOffset = a.uOffset - b.uOffset, vOffset = a.vOffset - b.vOffset
    };

    static ControlState Scale(ControlState a, float s) => new ControlState
    {
        length = a.length * s, width = a.width * s, segments = a.segments * s,
        bend = a.bend * s, twist = a.twist * s, depth = a.depth * s,
        x = a.x * s, y = a.y * s, z = a.z * s,
        uScale = a.uScale * s, vScale = a.vScale * s,
        uOffset = a.uOffset * s, vOffset = a.vOffset * s
    };

    public List<PostAffectorSaveData> ExportGroup(int groupId)
    {
        List<PostAffectorSaveData> result = new List<PostAffectorSaveData>();
        if (!groups.TryGetValue(groupId, out List<PostAffector> list)) return result;
        foreach (PostAffector a in list)
        {
            result.Add(new PostAffectorSaveData
            {
                id = a.id,
                centerX = a.center.x, centerY = a.center.y, centerZ = a.center.z,
                normalX = a.normal.x, normalY = a.normal.y, normalZ = a.normal.z,
                radius = a.radius, falloff = a.falloff, weight = a.weight,
                baseline = ToSave(a.baseline), delta = ToSave(a.delta)
            });
        }
        return result;
    }

    public void ClearAll()
    {
        EnsureViewer();
        groups.Clear();
        cardStates.Clear();
        predeterminedUVByGroup.Clear();
        predeterminedUVCacheFrame = -1;
        uvRouting = null;
        activeId = -1;
        activeGroup = -1;
        nextId = 1;

        // A saved-project load can begin while a POST is selected. Clear the shared
        // selection hotspot as part of POST teardown so the radial marker cannot survive
        // into the newly loaded project/model.
        SetField(hasSelectionField, false);
        SetField(hitPointField, Vector3.zero);
        SetField(hitNormalField, Vector3.zero);

        foreach (RectTransform r in FindObjectsByType<RectTransform>(FindObjectsSortMode.None).Where(r => r.name.StartsWith("PostAffector_")))
            Destroy(r.gameObject);
        nextUIScan = 0f;
    }

    public void ImportGroup(int groupId, List<PostAffectorSaveData> data)
    {
        groups.Remove(groupId);
        if (data == null || data.Count == 0)
        {
            RebuildGroupRows(groupId);
            return;
        }

        List<PostAffector> list = new List<PostAffector>();
        foreach (PostAffectorSaveData d in data)
        {
            PostAffector a = new PostAffector
            {
                id = d.id,
                groupId = groupId,
                center = new Vector3(d.centerX, d.centerY, d.centerZ),
                normal = new Vector3(d.normalX, d.normalY, d.normalZ),
                radius = d.radius,
                falloff = d.falloff,
                weight = d.weight,
                baseline = FromSave(d.baseline),
                delta = FromSave(d.delta)
            };
            list.Add(a);
            nextId = Mathf.Max(nextId, a.id + 1);
        }
        groups[groupId] = list;

        // Format-v2 projects save canonical/upstream card state already. Do not subtract
        // POST effects during import: that old recovery path turns a valid base into
        // "base - POST", then the normal evaluator applies POST again and can create a
        // double-state handoff when several affectors are restored.
        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None).Where(c => c.groupId == groupId))
        {
            ControlState canonical = ReadCanonical(card);
            cardStates[card] = new CardState { baseState = canonical, lastFinal = canonical, hasFinal = false };
        }
        RebuildGroupRows(groupId);
    }

    static PostAffectorControlSaveData ToSave(ControlState s) => new PostAffectorControlSaveData
    {
        length = s.length, width = s.width, segments = s.segments, bend = s.bend, twist = s.twist,
        depth = s.depth, x = s.x, y = s.y, z = s.z,
        uScale = s.uScale, vScale = s.vScale, uOffset = s.uOffset, vOffset = s.vOffset
    };

    static ControlState FromSave(PostAffectorControlSaveData s) => s == null ? new ControlState() : new ControlState
    {
        length = s.length, width = s.width, segments = s.segments, bend = s.bend, twist = s.twist,
        depth = s.depth, x = s.x, y = s.y, z = s.z,
        uScale = s.uScale, vScale = s.vScale, uOffset = s.uOffset, vOffset = s.vOffset
    };
}
