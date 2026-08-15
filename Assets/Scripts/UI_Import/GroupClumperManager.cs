using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// One relationship-style CLUMPER modifier per group.
// Creation/reposition: hold TAB and click the model, mirroring Ctrl+Click POST creation.
// Evaluation order is intentionally after POSTs. Clumper never writes HairCard canonical
// state or selection weights; it only produces the final display mesh from the current
// evaluated HairCard parameters.
[DefaultExecutionOrder(5200)]
public class GroupClumperManager : MonoBehaviour
{
    public enum ClumpMode { Singular = 0, DispersedEvenly = 1, FromPoint = 2 }

    [Serializable]
    public class GroupClumper
    {
        public int groupId;
        public Vector3 center;
        public Vector3 normal = Vector3.up;
        public ClumpMode mode = ClumpMode.Singular;
        [Range(0f, 1f)] public float amount = 0f;
        [Range(1, 24)] public int count = 6;
        public int seed = 1;
        public float radius = .05f;
        public float falloff = .05f;
        [NonSerialized] public List<HairCard> leaders = new();
        [NonSerialized] public int lastTopologyHash;
    }

    private readonly Dictionary<int, GroupClumper> byGroup = new();

    private ModelViewer viewer;
    private FieldInfo hitPointField;
    private FieldInfo hitNormalField;
    private FieldInfo hasSelectionField;
    private MethodInfo createSliderMethod;

    private int selectedGroup = -1;
    private int lastTabClickFrame = -1;
    private float nextUIScan;
    private GameObject controlsRoot;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<GroupClumperManager>() != null) return;
        GameObject go = new GameObject("GroupClumperManager");
        DontDestroyOnLoad(go);
        go.AddComponent<GroupClumperManager>();
    }

    void Update()
    {
        Resolve();
        if (viewer == null) return;

        DetectTabClick();

        if (Time.unscaledTime >= nextUIScan)
        {
            nextUIScan = Time.unscaledTime + .12f;
            EnsureRows();
            MaintainControls();
        }
    }

    void LateUpdate()
    {
        if (byGroup.Count == 0) return;
        HairCard[] cards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        if (cards.Length == 0) return;

        // Build a clean immutable pose directly from the CURRENT evaluated card parameters.
        // This avoids feeding previously clumped mesh vertices back into the next evaluation.
        Dictionary<HairCard, Vector3[]> clean = new();
        foreach (HairCard card in cards)
        {
            if (card == null || !byGroup.TryGetValue(card.groupId, out GroupClumper c) || c.amount <= .0001f) continue;
            clean[card] = BuildCleanVertices(card);
        }

        foreach (GroupClumper clumper in byGroup.Values)
        {
            if (clumper == null || clumper.amount <= .0001f) continue;
            HairCard[] groupCards = cards.Where(c => c != null && c.groupId == clumper.groupId).ToArray();
            if (groupCards.Length < 2) continue;

            int topologyHash = ComputeTopologyHash(groupCards, clumper);
            if (clumper.leaders == null) clumper.leaders = new List<HairCard>();
            if (topologyHash != clumper.lastTopologyHash || clumper.leaders.Count == 0 || clumper.leaders.Any(l => l == null))
            {
                RebuildLeaders(clumper, groupCards);
                clumper.lastTopologyHash = topologyHash;
            }

            foreach (HairCard card in groupCards)
            {
                if (!clean.TryGetValue(card, out Vector3[] sourceClean)) continue;
                HairCard leader = FindAssignedLeader(card, clumper.leaders);
                if (leader == null || leader == card || !clean.TryGetValue(leader, out Vector3[] leaderClean)) continue;

                float zone = ZoneWeight(card, clumper);
                float influence = Mathf.Clamp01(clumper.amount * zone);
                if (influence <= .0001f) continue;
                ApplyClump(card, sourceClean, leader, leaderClean, influence);
            }
        }
    }

    void Resolve()
    {
        if (viewer != null) return;
        viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer == null) return;

        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Type t = typeof(ModelViewer);
        hitPointField = t.GetField("selectionHitPoint", flags);
        hitNormalField = t.GetField("selectionHitNormal", flags);
        hasSelectionField = t.GetField("hasSelectionHotspot", flags);
        createSliderMethod = t.GetMethod("CreateSliderUI", flags);
    }

    void DetectTabClick()
    {
        if (Mouse.current == null || Keyboard.current == null) return;
        if (!Keyboard.current.tabKey.isPressed || !Mouse.current.leftButton.wasPressedThisFrame) return;
        if (lastTabClickFrame == Time.frameCount) return;
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;
        if (!HasSelection()) return;

        lastTabClickFrame = Time.frameCount;
        int gid = viewer.currentGroupId;
        Vector3 point = GetVector(hitPointField);
        Vector3 normal = GetVector(hitNormalField);

        if (!byGroup.TryGetValue(gid, out GroupClumper clumper))
        {
            clumper = new GroupClumper { groupId = gid };
            byGroup[gid] = clumper;
        }

        // A later TAB-click on the same group repositions the one allowed CLUMPER.
        clumper.center = point;
        clumper.normal = normal.sqrMagnitude > .000001f ? normal.normalized : Vector3.up;
        clumper.lastTopologyHash = 0;
        clumper.leaders.Clear();
        selectedGroup = gid;
        RebuildRowsSoon();
    }

    bool HasSelection()
    {
        return hasSelectionField != null && hasSelectionField.GetValue(viewer) is bool b && b;
    }

    Vector3 GetVector(FieldInfo field)
    {
        return field != null && field.GetValue(viewer) is Vector3 v ? v : Vector3.zero;
    }

    void EnsureRows()
    {
        RectTransform[] all = FindObjectsByType<RectTransform>(FindObjectsSortMode.None);
        foreach (RectTransform groupItem in all.Where(r => r != null && r.name.StartsWith("GroupItem_")))
        {
            if (!int.TryParse(groupItem.name.Substring("GroupItem_".Length), out int gid)) continue;
            Transform parent = groupItem.parent;
            if (parent == null) continue;

            string rowName = "GroupClumper_" + gid;
            Transform row = parent.Find(rowName);
            bool exists = byGroup.ContainsKey(gid);

            if (!exists)
            {
                if (row != null) Destroy(row.gameObject);
                continue;
            }

            if (row == null)
                row = BuildRow(parent, byGroup[gid]).transform;

            // Always after all POST rows belonging to this group.
            int insert = groupItem.GetSiblingIndex() + 1;
            while (insert < parent.childCount && parent.GetChild(insert).name.StartsWith("PostAffector_" + gid + "_")) insert++;
            row.SetSiblingIndex(Mathf.Min(insert, parent.childCount - 1));

            Image image = row.GetComponent<Image>();
            if (image != null)
                image.color = selectedGroup == gid ? new Color(.20f, .34f, .26f, .98f) : new Color(.11f, .18f, .14f, .98f);
        }
    }

    GameObject BuildRow(Transform parent, GroupClumper clumper)
    {
        GameObject row = new GameObject("GroupClumper_" + clumper.groupId, typeof(RectTransform), typeof(Image), typeof(HorizontalLayoutGroup));
        row.transform.SetParent(parent, false);
        row.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 34f);
        row.GetComponent<Image>().color = new Color(.11f, .18f, .14f, .98f);

        HorizontalLayoutGroup layout = row.GetComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(6, 6, 4, 4);
        layout.spacing = 5f;
        layout.childControlWidth = false;
        layout.childControlHeight = true;

        GameObject select = AddButton(row.transform, "CLUMPER", 118f);
        select.GetComponent<Button>().onClick.AddListener(() => SelectClumper(clumper.groupId));
        AddText(row.transform, ModeShort(clumper.mode), 10, 88f);
        GameObject remove = AddButton(row.transform, "[-]", 34f);
        remove.GetComponent<Button>().onClick.AddListener(() => RemoveClumper(clumper.groupId));
        return row;
    }

    void SelectClumper(int gid)
    {
        if (!byGroup.ContainsKey(gid)) return;
        selectedGroup = gid;
        viewer.currentGroupId = gid;
        DestroyControls();
        RebuildRowsSoon();
    }

    void RemoveClumper(int gid)
    {
        // A CLUMPER is a permanent stage once introduced to a group. Removing that stage
        // invalidates later group/POST authoring, so "[-]" now neutralizes its canonical
        // influence while keeping the modifier record and evaluation pipeline alive.
        if (!byGroup.TryGetValue(gid, out GroupClumper clumper) || clumper == null) return;
        clumper.amount = 0f;
        Invalidate(clumper);
        if (selectedGroup == gid) selectedGroup = -1;
        DestroyControls();
        RebuildRowsSoon();
    }

    void MaintainControls()
    {
        if (selectedGroup < 0 || !byGroup.TryGetValue(selectedGroup, out GroupClumper clumper) || viewer.groomingSliderPanelGO == null)
        {
            DestroyControls();
            return;
        }

        if (controlsRoot != null) return;
        controlsRoot = new GameObject("ClumperControls", typeof(RectTransform), typeof(VerticalLayoutGroup));
        controlsRoot.transform.SetParent(viewer.groomingSliderPanelGO.transform, false);
        VerticalLayoutGroup layout = controlsRoot.GetComponent<VerticalLayoutGroup>();
        layout.spacing = 6f;
        layout.childControlWidth = true;
        layout.childControlHeight = false;

        AddHeader(controlsRoot.transform, "CLUMPER");
        AddModeRow(controlsRoot.transform, clumper);
        AddSlider(controlsRoot.transform, "Clump Amount", 0f, 1f, clumper.amount, v => clumper.amount = v);

        if (clumper.mode != ClumpMode.Singular)
        {
            AddSlider(controlsRoot.transform, "Clumps", 1f, 24f, clumper.count, v => { clumper.count = Mathf.RoundToInt(v); Invalidate(clumper); });
            AddSeedRow(controlsRoot.transform, clumper);
        }

        if (clumper.mode != ClumpMode.DispersedEvenly)
        {
            AddSlider(controlsRoot.transform, "Radius", .001f, .25f, clumper.radius, v => { clumper.radius = v; Invalidate(clumper); });
            AddSlider(controlsRoot.transform, "Falloff", 0f, .25f, clumper.falloff, v => { clumper.falloff = v; Invalidate(clumper); });
        }

        AddHint(controlsRoot.transform, "TAB + CLICK repositions this clumper");
    }

    void AddModeRow(Transform parent, GroupClumper clumper)
    {
        GameObject row = new GameObject("ModeRow", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        row.transform.SetParent(parent, false);
        row.GetComponent<RectTransform>().sizeDelta = new Vector2(0, 34);
        HorizontalLayoutGroup h = row.GetComponent<HorizontalLayoutGroup>();
        h.spacing = 4f;
        h.childControlWidth = false;
        h.childControlHeight = true;

        AddModeButton(row.transform, "SINGLE", ClumpMode.Singular, clumper);
        AddModeButton(row.transform, "EVEN", ClumpMode.DispersedEvenly, clumper);
        AddModeButton(row.transform, "POINT", ClumpMode.FromPoint, clumper);
    }

    void AddModeButton(Transform parent, string label, ClumpMode mode, GroupClumper clumper)
    {
        GameObject go = AddButton(parent, label, 82f);
        Image image = go.GetComponent<Image>();
        image.color = clumper.mode == mode ? new Color(.20f, .55f, .35f) : new Color(.20f, .25f, .32f);
        go.GetComponent<Button>().onClick.AddListener(() =>
        {
            clumper.mode = mode;
            Invalidate(clumper);
            DestroyControls();
            RebuildRowsSoon();
        });
    }

    void AddSeedRow(Transform parent, GroupClumper clumper)
    {
        GameObject row = new GameObject("SeedRow", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        row.transform.SetParent(parent, false);
        row.GetComponent<RectTransform>().sizeDelta = new Vector2(0, 32);
        HorizontalLayoutGroup h = row.GetComponent<HorizontalLayoutGroup>();
        h.spacing = 6f;
        h.childControlWidth = false;
        h.childControlHeight = true;

        TextMeshProUGUI label = AddText(row.transform, "Seed: " + clumper.seed, 13, 170f);
        GameObject regen = AddButton(row.transform, "R", 42f);
        regen.GetComponent<Button>().onClick.AddListener(() =>
        {
            unchecked { clumper.seed = clumper.seed * 1664525 + 1013904223; }
            label.text = "Seed: " + clumper.seed;
            Invalidate(clumper);
        });
    }

    void AddSlider(Transform parent, string label, float min, float max, float value, UnityEngine.Events.UnityAction<float> changed)
    {
        if (createSliderMethod == null) return;
        object[] args = { parent, label, min, max, value, changed, null, 44f, 15 };
        createSliderMethod.Invoke(viewer, args);
    }

    void AddHeader(Transform parent, string text)
    {
        TextMeshProUGUI tmp = AddText(parent, text, 16, 0f);
        tmp.fontStyle = FontStyles.Bold;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.rectTransform.sizeDelta = new Vector2(0, 24);
    }

    void AddHint(Transform parent, string text)
    {
        TextMeshProUGUI tmp = AddText(parent, text, 11, 0f);
        tmp.color = new Color(.75f, .8f, .75f);
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.rectTransform.sizeDelta = new Vector2(0, 22);
    }

    void DestroyControls()
    {
        if (controlsRoot != null) Destroy(controlsRoot);
        controlsRoot = null;
    }

    void RebuildRowsSoon() { nextUIScan = 0f; }

    void Invalidate(GroupClumper clumper)
    {
        clumper.lastTopologyHash = 0;
        if (clumper.leaders != null) clumper.leaders.Clear();
    }

    void RebuildLeaders(GroupClumper clumper, HairCard[] cards)
    {
        clumper.leaders.Clear();
        if (cards.Length == 0) return;

        int wanted = clumper.mode == ClumpMode.Singular ? 1 : Mathf.Clamp(clumper.count, 1, cards.Length);
        System.Random rng = new System.Random(clumper.seed);

        if (clumper.mode == ClumpMode.Singular)
        {
            clumper.leaders.Add(cards.OrderBy(c => (RootWorld(c) - clumper.center).sqrMagnitude).First());
            return;
        }

        if (clumper.mode == ClumpMode.DispersedEvenly)
        {
            HairCard first = cards[Mathf.Abs(clumper.seed) % cards.Length];
            clumper.leaders.Add(first);
            while (clumper.leaders.Count < wanted)
            {
                HairCard best = null;
                float bestScore = float.NegativeInfinity;
                foreach (HairCard candidate in cards)
                {
                    if (clumper.leaders.Contains(candidate)) continue;
                    float nearestD2 = clumper.leaders.Min(l => (RootWorld(candidate) - RootWorld(l)).sqrMagnitude);
                    float jitter = (float)rng.NextDouble() * .000001f;
                    float score = nearestD2 + jitter;
                    if (score > bestScore) { bestScore = score; best = candidate; }
                }
                if (best == null) break;
                clumper.leaders.Add(best);
            }
            return;
        }

        // From Point: seeded weighted selection, deliberately denser near the click centre.
        List<HairCard> pool = cards.ToList();
        float outer = Mathf.Max(.001f, clumper.radius + clumper.falloff);
        while (clumper.leaders.Count < wanted && pool.Count > 0)
        {
            float total = 0f;
            float[] weights = new float[pool.Count];
            for (int i = 0; i < pool.Count; i++)
            {
                float d = Vector3.Distance(RootWorld(pool[i]), clumper.center);
                float normalized = Mathf.Clamp01(d / outer);
                float w = Mathf.Pow(1f - normalized, 2f) + .015f;
                weights[i] = w;
                total += w;
            }

            double pick = rng.NextDouble() * total;
            float acc = 0f;
            int chosen = pool.Count - 1;
            for (int i = 0; i < pool.Count; i++)
            {
                acc += weights[i];
                if (pick <= acc) { chosen = i; break; }
            }
            clumper.leaders.Add(pool[chosen]);
            pool.RemoveAt(chosen);
        }
    }

    HairCard FindAssignedLeader(HairCard card, List<HairCard> leaders)
    {
        if (leaders == null || leaders.Count == 0) return null;
        Vector3 p = RootWorld(card);
        HairCard best = null;
        float bestD2 = float.PositiveInfinity;
        foreach (HairCard leader in leaders)
        {
            if (leader == null) continue;
            float d2 = (RootWorld(leader) - p).sqrMagnitude;
            if (d2 < bestD2) { bestD2 = d2; best = leader; }
        }
        return best;
    }

    float ZoneWeight(HairCard card, GroupClumper clumper)
    {
        if (clumper.mode == ClumpMode.DispersedEvenly) return 1f;
        float d = Vector3.Distance(RootWorld(card), clumper.center);
        float radius = Mathf.Max(.001f, clumper.radius);
        float outer = radius + Mathf.Max(0f, clumper.falloff);
        if (d <= radius) return 1f;
        if (clumper.falloff <= .000001f || d >= outer) return 0f;
        return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(outer, radius, d));
    }

    int ComputeTopologyHash(HairCard[] cards, GroupClumper clumper)
    {
        unchecked
        {
            int h = 17;
            h = h * 31 + (int)clumper.mode;
            h = h * 31 + clumper.count;
            h = h * 31 + clumper.seed;
            h = h * 31 + Mathf.RoundToInt(clumper.center.x * 10000f);
            h = h * 31 + Mathf.RoundToInt(clumper.center.y * 10000f);
            h = h * 31 + Mathf.RoundToInt(clumper.center.z * 10000f);
            h = h * 31 + cards.Length;
            foreach (HairCard c in cards.OrderBy(c => c.GetInstanceID())) h = h * 31 + c.GetInstanceID();
            return h;
        }
    }

    static Vector3[] BuildCleanVertices(HairCard card)
    {
        int segments = Mathf.Clamp(card.segments, 4, 36);
        Vector3[] vertices = new Vector3[(segments + 1) * 2];
        float segmentHeight = Mathf.Max(.001f, card.length) / segments;
        float halfWidth = Mathf.Max(.0005f, card.width) * .5f;
        for (int i = 0; i <= segments; i++)
        {
            float t = (float)i / segments;
            float z = i * segmentHeight;
            float span = halfWidth * card.flattenFactor;
            Vector3 left = new Vector3(-span, 0f, z);
            Vector3 right = new Vector3(span, 0f, z);
            Quaternion authored = Quaternion.Euler(card.bendAngle * (t * t), 0f, card.twistAngle * t);
            vertices[i * 2] = authored * left;
            vertices[i * 2 + 1] = authored * right;
        }
        return vertices;
    }

    static void ApplyClump(HairCard source, Vector3[] sourceClean, HairCard leader, Vector3[] leaderClean, float influence)
    {
        MeshFilter mf = source.GetComponent<MeshFilter>();
        if (mf == null || mf.mesh == null || sourceClean == null || leaderClean == null) return;
        if (mf.mesh.vertexCount != sourceClean.Length) return;

        Vector3[] vertices = (Vector3[])sourceClean.Clone();
        int rows = vertices.Length / 2;
        for (int row = 1; row < rows; row++)
        {
            float t = (float)row / (rows - 1);
            float along = t * t * (3f - 2f * t);
            float w = Mathf.Clamp01(influence * along);
            if (w <= .0001f) continue;

            int li = row * 2;
            int ri = li + 1;
            Vector3 left = sourceClean[li];
            Vector3 right = sourceClean[ri];
            Vector3 ownCenter = (left + right) * .5f;
            Vector3 halfSpan = (right - left) * .5f;
            Vector3 leaderWorld = SampleCentreWorld(leader, leaderClean, t);
            Vector3 leaderLocal = source.transform.InverseTransformPoint(leaderWorld);
            Vector3 center = Vector3.Lerp(ownCenter, leaderLocal, w);
            vertices[li] = center - halfSpan;
            vertices[ri] = center + halfSpan;
        }

        mf.mesh.vertices = vertices;
        mf.mesh.RecalculateNormals();
        mf.mesh.RecalculateBounds();
    }

    static Vector3 SampleCentreWorld(HairCard card, Vector3[] vertices, float t)
    {
        int rows = vertices.Length / 2;
        if (rows <= 0) return card.transform.position;
        float rowF = Mathf.Clamp01(t) * (rows - 1);
        int a = Mathf.Clamp(Mathf.FloorToInt(rowF), 0, rows - 1);
        int b = Mathf.Min(a + 1, rows - 1);
        float f = rowF - a;
        Vector3 ca = (vertices[a * 2] + vertices[a * 2 + 1]) * .5f;
        Vector3 cb = (vertices[b * 2] + vertices[b * 2 + 1]) * .5f;
        return card.transform.TransformPoint(Vector3.Lerp(ca, cb, f));
    }

    static Vector3 RootWorld(HairCard card)
    {
        Vector3 p = card.GetSpawnHitPoint();
        return p == Vector3.zero ? card.transform.position : p;
    }

    static string ModeShort(ClumpMode mode)
    {
        switch (mode)
        {
            case ClumpMode.Singular: return "SINGLE";
            case ClumpMode.DispersedEvenly: return "EVEN";
            default: return "POINT";
        }
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
        t.raycastTarget = false;
        return t;
    }
}