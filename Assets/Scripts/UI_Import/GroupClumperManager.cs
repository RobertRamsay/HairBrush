using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Owns CLUMPER data + UI. A group may contain any number of independent clump points.
// TAB + click creation / SPACE + click reposition is handled by GroupClumperInteractionAuthority.
// Final mesh deformation is owned by ThreeColumnClumperMeshAuthority.
[DefaultExecutionOrder(5200)]
public class GroupClumperManager : MonoBehaviour
{
    public enum ClumpMode { Singular = 0, DispersedEvenly = 1, FromPoint = 2 }

    [Serializable]
    public class GroupClumper
    {
        public int id;
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

    private readonly Dictionary<int, List<GroupClumper>> byGroup = new();
    private ModelViewer viewer;
    private MethodInfo createSliderMethod;

    // selectedGroup is retained because a few compatibility authorities still inspect it.
    private int selectedGroup = -1;
    private int selectedClumperId = -1;
    private int nextClumperId = 1;
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

        if (Time.unscaledTime >= nextUIScan)
        {
            nextUIScan = Time.unscaledTime + .10f;
            EnsureRows();
            MaintainControls();
        }
    }

    void Resolve()
    {
        if (viewer != null) return;
        viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer == null) return;
        createSliderMethod = typeof(ModelViewer).GetMethod("CreateSliderUI", BindingFlags.Instance | BindingFlags.NonPublic);
    }

    public GroupClumper CreateClumper(int groupId, Vector3 point, Vector3 normal)
    {
        if (!byGroup.TryGetValue(groupId, out List<GroupClumper> list))
        {
            list = new List<GroupClumper>();
            byGroup[groupId] = list;
        }

        GroupClumper clumper = new GroupClumper
        {
            id = nextClumperId++,
            groupId = groupId,
            center = point,
            normal = normal.sqrMagnitude > .000001f ? normal.normalized : Vector3.up,
            seed = UnityEngine.Random.Range(1, int.MaxValue)
        };
        list.Add(clumper);
        SelectClumper(groupId, clumper.id);
        return clumper;
    }

    public bool MoveSelectedClumper(int groupId, Vector3 point, Vector3 normal)
    {
        GroupClumper clumper = GetSelectedClumper();
        if (clumper == null || clumper.groupId != groupId) return false;
        clumper.center = point;
        clumper.normal = normal.sqrMagnitude > .000001f ? normal.normalized : Vector3.up;
        Invalidate(clumper);
        DestroyControls();
        RebuildRowsSoon();
        return true;
    }

    public GroupClumper GetSelectedClumper()
    {
        if (selectedClumperId < 0) return null;
        foreach (List<GroupClumper> list in byGroup.Values)
            foreach (GroupClumper clumper in list)
                if (clumper != null && clumper.id == selectedClumperId) return clumper;
        return null;
    }

    public List<GroupClumper> GetGroupClumpers(int groupId)
    {
        return byGroup.TryGetValue(groupId, out List<GroupClumper> list)
            ? list.Where(c => c != null).ToList()
            : new List<GroupClumper>();
    }

    public List<GroupClumper> GetAllClumpers()
    {
        return byGroup.Values.SelectMany(list => list).Where(c => c != null).ToList();
    }

    public bool HasClumpers(int groupId)
    {
        return byGroup.TryGetValue(groupId, out List<GroupClumper> list) && list.Any(c => c != null);
    }

    void EnsureRows()
    {
        RectTransform[] all = FindObjectsByType<RectTransform>(FindObjectsSortMode.None);
        foreach (RectTransform groupItem in all.Where(r => r != null && r.name.StartsWith("GroupItem_")))
        {
            if (!int.TryParse(groupItem.name.Substring("GroupItem_".Length), out int gid)) continue;
            Transform parent = groupItem.parent;
            if (parent == null) continue;

            List<GroupClumper> groupClumpers = GetGroupClumpers(gid);
            HashSet<string> wanted = new HashSet<string>(groupClumpers.Select(c => RowName(c)));

            for (int i = parent.childCount - 1; i >= 0; i--)
            {
                Transform child = parent.GetChild(i);
                if (!child.name.StartsWith("GroupClumper_" + gid + "_")) continue;
                if (!wanted.Contains(child.name)) Destroy(child.gameObject);
            }

            int insert = groupItem.GetSiblingIndex() + 1;
            while (insert < parent.childCount && parent.GetChild(insert).name.StartsWith("PostAffector_" + gid + "_")) insert++;

            foreach (GroupClumper clumper in groupClumpers.OrderBy(c => c.id))
            {
                Transform row = parent.Find(RowName(clumper));
                if (row == null) row = BuildRow(parent, clumper).transform;
                row.SetSiblingIndex(Mathf.Min(insert++, parent.childCount - 1));

                Image image = row.GetComponent<Image>();
                if (image != null)
                    image.color = selectedClumperId == clumper.id
                        ? new Color(.20f, .34f, .26f, .98f)
                        : new Color(.11f, .18f, .14f, .98f);
            }
        }
    }

    static string RowName(GroupClumper clumper)
    {
        return "GroupClumper_" + clumper.groupId + "_" + clumper.id;
    }

    GameObject BuildRow(Transform parent, GroupClumper clumper)
    {
        GameObject row = new GameObject(RowName(clumper), typeof(RectTransform), typeof(Image), typeof(HorizontalLayoutGroup));
        row.transform.SetParent(parent, false);
        row.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 34f);
        row.GetComponent<Image>().color = new Color(.11f, .18f, .14f, .98f);

        HorizontalLayoutGroup layout = row.GetComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(6, 6, 4, 4);
        layout.spacing = 5f;
        layout.childControlWidth = false;
        layout.childControlHeight = true;

        GameObject select = AddButton(row.transform, "CLUMP " + clumper.id, 118f);
        select.GetComponent<Button>().onClick.AddListener(() => SelectClumper(clumper.groupId, clumper.id));
        AddText(row.transform, ModeShort(clumper.mode), 10, 88f);
        GameObject neutral = AddButton(row.transform, "[-]", 34f);
        neutral.GetComponent<Button>().onClick.AddListener(() => RemoveClumper(clumper));
        return row;
    }

    void SelectClumper(int gid, int id)
    {
        GroupClumper clumper = FindClumper(id);
        if (clumper == null || clumper.groupId != gid) return;
        selectedGroup = gid;
        selectedClumperId = id;
        if (viewer != null) viewer.currentGroupId = gid;
        DestroyControls();
        RebuildRowsSoon();
    }

    GroupClumper FindClumper(int id)
    {
        foreach (List<GroupClumper> list in byGroup.Values)
            foreach (GroupClumper clumper in list)
                if (clumper != null && clumper.id == id) return clumper;
        return null;
    }

    void NeutralizeClumper(int id)
    {
        GroupClumper clumper = FindClumper(id);
        if (clumper == null) return;
        clumper.amount = 0f;
        Invalidate(clumper);
        if (selectedClumperId == id)
        {
            selectedClumperId = -1;
            selectedGroup = -1;
            DestroyControls();
        }
        RebuildRowsSoon();
    }

    // The "[-]" button previously only called NeutralizeClumper, which zeroes amount but never
    // actually removes the clumper from byGroup - it stayed there forever, permanently inert.
    // This is the actual second phase of the documented neutralize-then-delete pattern
    // (ModifierNeutralizeBeforeDeleteAuthority handles the neutralize-on-pointer-down half),
    // mirroring PostAffectorManager.RemoveAffector's true removal. ThreeColumnClumperMeshAuthority
    // already restores affected cards to their unclamped shape automatically once a clumper
    // disappears from byGroup, so no extra mesh-rebuild call is needed here.
    void RemoveClumper(GroupClumper clumper)
    {
        if (clumper == null) return;
        if (byGroup.TryGetValue(clumper.groupId, out List<GroupClumper> list))
        {
            list.RemoveAll(c => c != null && c.id == clumper.id);
            if (list.Count == 0) byGroup.Remove(clumper.groupId);
        }
        if (selectedClumperId == clumper.id)
        {
            selectedClumperId = -1;
            selectedGroup = -1;
            DestroyControls();
        }
        RebuildRowsSoon();
    }

    void MaintainControls()
    {
        GroupClumper clumper = GetSelectedClumper();
        if (clumper == null || viewer == null || viewer.groomingSliderPanelGO == null)
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

        AddHeader(controlsRoot.transform, "CLUMPER " + clumper.id);
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

        AddHint(controlsRoot.transform, "TAB + CLICK adds another   |   SPACE + CLICK moves this one");
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
        go.GetComponent<Image>().color = clumper.mode == mode ? new Color(.20f, .55f, .35f) : new Color(.20f, .25f, .32f);
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

    public void Invalidate(GroupClumper clumper)
    {
        if (clumper == null) return;
        clumper.lastTopologyHash = 0;
        if (clumper.leaders != null) clumper.leaders.Clear();
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