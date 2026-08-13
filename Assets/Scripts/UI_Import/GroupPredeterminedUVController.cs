using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Group-level UV source selector.
// ADJUSTABLE keeps the existing U/V Scale + Offset root controls.
// PREDETERMINED maps each card to one authored Texture Editor UV rectangle, chosen
// deterministically from the group's inclusive rect-ID range and seed. The chosen rect
// becomes canonical/base UV state, so POST-local UV edits may still operate downstream.
[DefaultExecutionOrder(6000)]
public class GroupPredeterminedUVController : MonoBehaviour
{
    [Serializable]
    private class GroupUVSettings
    {
        public bool predetermined;
        public int minId = 1;
        public int maxId = 1;
        public int seed;
    }

    private class RowUI
    {
        public GameObject root;
        public Button modeButton;
        public TextMeshProUGUI modeText;
        public TMP_InputField minInput;
        public TMP_InputField maxInput;
        public TMP_InputField seedInput;
        public Button randomButton;
    }

    private readonly Dictionary<int, GroupUVSettings> settingsByGroup = new();
    private readonly Dictionary<int, RowUI> rowsByGroup = new();
    private readonly Dictionary<int, int> appliedSignatureByCard = new();

    private ModelViewer viewer;
    private TextureUVRectWorkspace workspace;
    private PostAffectorManager posts;
    private FieldInfo activePostIdField;
    private FieldInfo hasSelectionField;
    private FieldInfo groupUScalesField;
    private FieldInfo groupVScalesField;
    private FieldInfo groupUOffsetsField;
    private FieldInfo groupVOffsetsField;

    private float nextUIScan;
    private float nextApplyScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<GroupPredeterminedUVController>() != null) return;
        GameObject go = new GameObject("GroupPredeterminedUVController");
        DontDestroyOnLoad(go);
        go.AddComponent<GroupPredeterminedUVController>();
    }

    void Update()
    {
        Resolve();
        if (viewer == null) return;

        RestorePendingProject();

        if (Time.unscaledTime >= nextUIScan)
        {
            nextUIScan = Time.unscaledTime + .08f;
            MaintainGroupRows();
        }

        if (Time.unscaledTime >= nextApplyScan)
        {
            nextApplyScan = Time.unscaledTime + .10f;
            ApplyPredeterminedAssignments();
        }

        MaintainRootUVLock();
    }

    void Resolve()
    {
        if (viewer == null)
        {
            viewer = FindFirstObjectByType<ModelViewer>();
            if (viewer != null)
            {
                BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                Type type = typeof(ModelViewer);
                hasSelectionField = type.GetField("hasSelectionHotspot", flags);
                groupUScalesField = type.GetField("groupUScales", flags);
                groupVScalesField = type.GetField("groupVScales", flags);
                groupUOffsetsField = type.GetField("groupUOffsets", flags);
                groupVOffsetsField = type.GetField("groupVOffsets", flags);
            }
        }

        if (workspace == null) workspace = FindFirstObjectByType<TextureUVRectWorkspace>();

        if (posts == null)
        {
            posts = FindFirstObjectByType<PostAffectorManager>();
            if (posts != null)
                activePostIdField = typeof(PostAffectorManager).GetField("activeId", BindingFlags.Instance | BindingFlags.NonPublic);
        }
    }

    GroupUVSettings GetSettings(int groupId)
    {
        if (!settingsByGroup.TryGetValue(groupId, out GroupUVSettings settings))
        {
            settings = new GroupUVSettings();
            settingsByGroup[groupId] = settings;
        }
        return settings;
    }

    public void PopulateGroupSave(GroupSaveData group)
    {
        if (group == null) return;
        GroupUVSettings settings = GetSettings(group.groupId);
        group.usePredeterminedUVs = settings.predetermined;
        group.uvRectMinId = settings.minId;
        group.uvRectMaxId = settings.maxId;
        group.uvRectSeed = settings.seed;
    }

    public void ClearAllSettings()
    {
        settingsByGroup.Clear();
        appliedSignatureByCard.Clear();

        foreach (RowUI row in rowsByGroup.Values)
            if (row != null && row.root != null) Destroy(row.root);
        rowsByGroup.Clear();
        nextUIScan = 0f;
        nextApplyScan = 0f;
    }

    void RestorePendingProject()
    {
        HairProjectSaveData pending = HairProjectSaveData.PendingGroupUVRestore;
        if (pending == null) return;

        // Texture rectangle definitions restore in the dedicated texture workspace later in
        // execution order. Wait until that handoff has completed before applying any ranges,
        // otherwise a newly loaded project could briefly use the previous project's rectangles.
        if (HairProjectSaveData.PendingUVRectRestore != null) return;

        int expectedCards = pending.hairCards != null ? pending.hairCards.Count : 0;
        if (FindObjectsByType<HairCard>(FindObjectsSortMode.None).Length < expectedCards) return;

        HairProjectSaveData.PendingGroupUVRestore = null;
        settingsByGroup.Clear();
        appliedSignatureByCard.Clear();

        if (pending.groups != null)
        {
            foreach (GroupSaveData group in pending.groups)
            {
                if (group == null) continue;
                GroupUVSettings settings = GetSettings(group.groupId);
                settings.predetermined = group.usePredeterminedUVs;
                settings.minId = group.uvRectMinId > 0 ? group.uvRectMinId : 1;
                settings.maxId = group.uvRectMaxId > 0 ? group.uvRectMaxId : settings.minId;
                settings.seed = group.uvRectSeed;
                NormalizeRange(settings);
            }
        }

        nextUIScan = 0f;
        nextApplyScan = 0f;
        ApplyPredeterminedAssignments();
    }

    void MaintainGroupRows()
    {
        RectTransform[] all = FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        HashSet<int> liveGroups = new();

        foreach (RectTransform groupItem in all)
        {
            if (groupItem == null || !groupItem.name.StartsWith("GroupItem_", StringComparison.Ordinal)) continue;
            if (!int.TryParse(groupItem.name.Substring("GroupItem_".Length), out int groupId)) continue;
            Transform parent = groupItem.parent;
            if (parent == null) continue;

            liveGroups.Add(groupId);
            RowUI row = EnsureRow(parent, groupItem, groupId);
            if (row == null || row.root == null) continue;

            // Keep group-owned UV state immediately under the group header. POST rows are
            // reordered earlier in the frame and naturally shift down after this insertion.
            int targetIndex = Mathf.Min(groupItem.GetSiblingIndex() + 1, parent.childCount - 1);
            if (row.root.transform.GetSiblingIndex() != targetIndex)
                row.root.transform.SetSiblingIndex(targetIndex);

            SyncRow(groupId, row);
        }

        foreach (int groupId in rowsByGroup.Keys.Where(id => !liveGroups.Contains(id)).ToArray())
        {
            RowUI row = rowsByGroup[groupId];
            if (row != null && row.root != null) Destroy(row.root);
            rowsByGroup.Remove(groupId);
        }
    }

    RowUI EnsureRow(Transform parent, RectTransform groupItem, int groupId)
    {
        if (rowsByGroup.TryGetValue(groupId, out RowUI cached) && cached != null && cached.root != null)
            return cached;

        Transform existing = parent.Find("GroupUV_" + groupId);
        if (existing != null) Destroy(existing.gameObject);

        GameObject root = new GameObject("GroupUV_" + groupId, typeof(RectTransform), typeof(Image), typeof(HorizontalLayoutGroup));
        root.transform.SetParent(parent, false);
        root.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 30f);
        root.GetComponent<Image>().color = new Color(.10f, .12f, .16f, .96f);

        HorizontalLayoutGroup layout = root.GetComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(5, 5, 3, 3);
        layout.spacing = 4f;
        layout.childControlWidth = false;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;

        GameObject modeGO = AddButton(root.transform, "UV: ADJ", 76f);
        Button modeButton = modeGO.GetComponent<Button>();
        TextMeshProUGUI modeText = modeGO.GetComponentInChildren<TextMeshProUGUI>(true);

        TMP_InputField minInput = AddIntInput(root.transform, "MIN", 30f);
        AddText(root.transform, "→", 10f, 10f);
        TMP_InputField maxInput = AddIntInput(root.transform, "MAX", 30f);
        AddText(root.transform, "S", 9f, 10f);
        TMP_InputField seedInput = AddIntInput(root.transform, "SEED", 47f);
        GameObject randomGO = AddButton(root.transform, "R", 22f);
        Button randomButton = randomGO.GetComponent<Button>();

        int capturedGroup = groupId;
        modeButton.onClick.AddListener(() => ToggleMode(capturedGroup));
        minInput.onEndEdit.AddListener(value => SetRangeValue(capturedGroup, true, value));
        maxInput.onEndEdit.AddListener(value => SetRangeValue(capturedGroup, false, value));
        seedInput.onEndEdit.AddListener(value => SetSeed(capturedGroup, value));
        randomButton.onClick.AddListener(() => RandomizeSeed(capturedGroup));

        RowUI row = new RowUI
        {
            root = root,
            modeButton = modeButton,
            modeText = modeText,
            minInput = minInput,
            maxInput = maxInput,
            seedInput = seedInput,
            randomButton = randomButton
        };
        rowsByGroup[groupId] = row;
        return row;
    }

    void SyncRow(int groupId, RowUI row)
    {
        GroupUVSettings settings = GetSettings(groupId);
        bool haveRects = GetAllRects().Count > 0;

        if (row.modeText != null)
            row.modeText.text = settings.predetermined ? "UV: PRE" : "UV: ADJ";
        if (row.modeButton != null)
            row.modeButton.interactable = haveRects || settings.predetermined;

        bool inputsEnabled = settings.predetermined && haveRects;
        if (row.minInput != null)
        {
            if (row.minInput.text != settings.minId.ToString()) row.minInput.SetTextWithoutNotify(settings.minId.ToString());
            row.minInput.interactable = inputsEnabled;
        }
        if (row.maxInput != null)
        {
            if (row.maxInput.text != settings.maxId.ToString()) row.maxInput.SetTextWithoutNotify(settings.maxId.ToString());
            row.maxInput.interactable = inputsEnabled;
        }
        if (row.seedInput != null)
        {
            if (row.seedInput.text != settings.seed.ToString()) row.seedInput.SetTextWithoutNotify(settings.seed.ToString());
            row.seedInput.interactable = inputsEnabled;
        }
        if (row.randomButton != null) row.randomButton.interactable = inputsEnabled;

        Image image = row.root != null ? row.root.GetComponent<Image>() : null;
        if (image != null)
            image.color = settings.predetermined ? new Color(.11f, .20f, .27f, .98f) : new Color(.10f, .12f, .16f, .96f);
    }

    void ToggleMode(int groupId)
    {
        GroupUVSettings settings = GetSettings(groupId);
        if (!settings.predetermined)
        {
            List<UVRectSaveData> rects = GetAllRects();
            if (rects.Count == 0) return;

            int min = rects.Min(r => r.id);
            int max = rects.Max(r => r.id);
            if (settings.minId <= 0 || settings.maxId <= 0 || !rects.Any(r => r.id >= settings.minId && r.id <= settings.maxId))
            {
                settings.minId = min;
                settings.maxId = max;
            }
            else if (settings.minId == 1 && settings.maxId == 1 && rects.Count > 1)
            {
                // First activation should naturally use the complete authored set.
                settings.minId = min;
                settings.maxId = max;
            }

            NormalizeRange(settings);
            settings.predetermined = true;
            ClearAppliedForGroup(groupId);
            ApplyGroup(groupId, settings, rects);
        }
        else
        {
            settings.predetermined = false;
            RestoreAdjustableUV(groupId);
            ClearAppliedForGroup(groupId);
        }

        nextUIScan = 0f;
        nextApplyScan = 0f;
    }

    void SetRangeValue(int groupId, bool isMin, string value)
    {
        GroupUVSettings settings = GetSettings(groupId);
        if (!int.TryParse(value, out int parsed)) parsed = isMin ? settings.minId : settings.maxId;
        if (isMin) settings.minId = parsed;
        else settings.maxId = parsed;
        NormalizeRange(settings);
        ClearAppliedForGroup(groupId);
        ForceApplyGroup(groupId);
    }

    void SetSeed(int groupId, string value)
    {
        GroupUVSettings settings = GetSettings(groupId);
        if (!int.TryParse(value, out int parsed)) parsed = 0;
        settings.seed = parsed;
        ClearAppliedForGroup(groupId);
        ForceApplyGroup(groupId);
    }

    void RandomizeSeed(int groupId)
    {
        GroupUVSettings settings = GetSettings(groupId);
        settings.seed = UnityEngine.Random.Range(0, 1000000);
        ClearAppliedForGroup(groupId);
        ForceApplyGroup(groupId);
    }

    void NormalizeRange(GroupUVSettings settings)
    {
        if (settings == null) return;
        List<UVRectSaveData> rects = GetAllRects();
        if (rects.Count == 0)
        {
            settings.minId = Mathf.Max(1, settings.minId);
            settings.maxId = Mathf.Max(settings.minId, settings.maxId);
            return;
        }

        int availableMin = rects.Min(r => r.id);
        int availableMax = rects.Max(r => r.id);
        settings.minId = Mathf.Clamp(settings.minId, availableMin, availableMax);
        settings.maxId = Mathf.Clamp(settings.maxId, availableMin, availableMax);
        if (settings.minId > settings.maxId)
        {
            int swap = settings.minId;
            settings.minId = settings.maxId;
            settings.maxId = swap;
        }
    }

    void ApplyPredeterminedAssignments()
    {
        List<UVRectSaveData> allRects = GetAllRects();
        if (allRects.Count == 0) return;

        foreach (KeyValuePair<int, GroupUVSettings> pair in settingsByGroup)
        {
            if (pair.Value == null || !pair.Value.predetermined) continue;
            ApplyGroup(pair.Key, pair.Value, allRects);
        }

        HashSet<int> liveCardIds = new(FindObjectsByType<HairCard>(FindObjectsSortMode.None)
            .Where(card => card != null).Select(card => card.GetInstanceID()));
        foreach (int dead in appliedSignatureByCard.Keys.Where(id => !liveCardIds.Contains(id)).ToArray())
            appliedSignatureByCard.Remove(dead);
    }

    void ForceApplyGroup(int groupId)
    {
        GroupUVSettings settings = GetSettings(groupId);
        if (!settings.predetermined) return;
        ApplyGroup(groupId, settings, GetAllRects());
        nextApplyScan = Time.unscaledTime + .10f;
    }

    void ApplyGroup(int groupId, GroupUVSettings settings, List<UVRectSaveData> allRects)
    {
        if (settings == null || !settings.predetermined || allRects == null || allRects.Count == 0) return;

        List<UVRectSaveData> allowed = allRects
            .Where(rect => rect != null && rect.id >= settings.minId && rect.id <= settings.maxId)
            .OrderBy(rect => rect.id)
            .ToList();
        if (allowed.Count == 0) return;

        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
        {
            if (card == null || card.groupId != groupId) continue;

            int pick = PositiveMod(StableCardHash(card, groupId, settings.seed), allowed.Count);
            UVRectSaveData rect = allowed[pick];
            int signature = RectSignature(rect, settings.seed, allowed.Count);
            int cardId = card.GetInstanceID();
            if (appliedSignatureByCard.TryGetValue(cardId, out int previous) && previous == signature) continue;

            HairCard.GroomState canonical = card.GetCanonicalState();
            canonical.uScale = Mathf.Max(.000001f, rect.uMax - rect.uMin);
            canonical.vScale = Mathf.Max(.000001f, rect.vMax - rect.vMin);
            canonical.uOffset = rect.uMin;
            canonical.vOffset = rect.vMin;
            card.SetCanonicalState(canonical, true);
            appliedSignatureByCard[cardId] = signature;
        }
    }

    void RestoreAdjustableUV(int groupId)
    {
        float uScale = GroupFloat(groupUScalesField, groupId, 1f);
        float vScale = GroupFloat(groupVScalesField, groupId, 1f);
        float uOffset = GroupFloat(groupUOffsetsField, groupId, 0f);
        float vOffset = GroupFloat(groupVOffsetsField, groupId, 0f);

        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
        {
            if (card == null || card.groupId != groupId) continue;
            HairCard.GroomState canonical = card.GetCanonicalState();
            canonical.uScale = uScale;
            canonical.vScale = vScale;
            canonical.uOffset = uOffset;
            canonical.vOffset = vOffset;
            card.SetCanonicalState(canonical, true);
        }
    }

    float GroupFloat(FieldInfo field, int groupId, float fallback)
    {
        Dictionary<int, float> values = field?.GetValue(viewer) as Dictionary<int, float>;
        if (values != null && values.TryGetValue(groupId, out float value)) return value;
        if (viewer != null && viewer.currentGroupId == groupId)
        {
            if (field == groupUScalesField) return viewer.currentUScale;
            if (field == groupVScalesField) return viewer.currentVScale;
            if (field == groupUOffsetsField) return viewer.currentUOffset;
            if (field == groupVOffsetsField) return viewer.currentVOffset;
        }
        return fallback;
    }

    void ClearAppliedForGroup(int groupId)
    {
        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
            if (card != null && card.groupId == groupId)
                appliedSignatureByCard.Remove(card.GetInstanceID());
    }

    List<UVRectSaveData> GetAllRects()
    {
        if (workspace == null) workspace = FindFirstObjectByType<TextureUVRectWorkspace>();
        return workspace != null
            ? workspace.ExportDefinitions().Where(rect => rect != null).OrderBy(rect => rect.id).ToList()
            : new List<UVRectSaveData>();
    }

    void MaintainRootUVLock()
    {
        if (viewer == null || viewer.groomingSliderPanelGO == null) return;

        GroupUVSettings settings = GetSettings(viewer.currentGroupId);
        bool editingPost = IsEditingPost();
        bool predeterminedRoot = settings.predetermined && !editingPost;
        bool canEditOtherwise = !GroupHasPost(viewer.currentGroupId) || editingPost;

        foreach (Slider slider in viewer.groomingSliderPanelGO.GetComponentsInChildren<Slider>(true))
        {
            if (slider == null) continue;
            string name = slider.gameObject.name;
            if (name != "U Scale_Slider" && name != "V Scale_Slider" &&
                name != "U Offset_Slider" && name != "V Offset_Slider") continue;

            if (predeterminedRoot) slider.interactable = false;
            else if (canEditOtherwise) slider.interactable = true;
        }
    }

    bool IsEditingPost()
    {
        if (posts == null || activePostIdField == null || hasSelectionField == null || viewer == null) return false;
        int activeId = activePostIdField.GetValue(posts) is int id ? id : -1;
        bool selected = hasSelectionField.GetValue(viewer) is bool b && b;
        return activeId >= 0 && selected;
    }

    bool GroupHasPost(int groupId)
    {
        if (posts == null) return false;
        List<PostAffectorSaveData> list = posts.ExportGroup(groupId);
        return list != null && list.Count > 0;
    }

    static int StableCardHash(HairCard card, int groupId, int seed)
    {
        Vector3 p = card.transform.position;
        Quaternion q = card.transform.rotation;
        unchecked
        {
            uint hash = 2166136261u;
            Mix(ref hash, Mathf.RoundToInt(p.x * 10000f));
            Mix(ref hash, Mathf.RoundToInt(p.y * 10000f));
            Mix(ref hash, Mathf.RoundToInt(p.z * 10000f));
            Mix(ref hash, Mathf.RoundToInt(q.x * 10000f));
            Mix(ref hash, Mathf.RoundToInt(q.y * 10000f));
            Mix(ref hash, Mathf.RoundToInt(q.z * 10000f));
            Mix(ref hash, Mathf.RoundToInt(q.w * 10000f));
            Mix(ref hash, groupId);
            Mix(ref hash, seed);
            return (int)(hash & 0x7fffffff);
        }
    }

    static void Mix(ref uint hash, int value)
    {
        unchecked
        {
            hash ^= (uint)value;
            hash *= 16777619u;
        }
    }

    static int RectSignature(UVRectSaveData rect, int seed, int allowedCount)
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + rect.id;
            hash = hash * 31 + seed;
            hash = hash * 31 + allowedCount;
            hash = hash * 31 + Mathf.RoundToInt(rect.uMin * 100000f);
            hash = hash * 31 + Mathf.RoundToInt(rect.uMax * 100000f);
            hash = hash * 31 + Mathf.RoundToInt(rect.vMin * 100000f);
            hash = hash * 31 + Mathf.RoundToInt(rect.vMax * 100000f);
            return hash;
        }
    }

    static int PositiveMod(int value, int modulus)
    {
        if (modulus <= 0) return 0;
        int result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    GameObject AddButton(Transform parent, string label, float width)
    {
        GameObject go = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(width, 24f);
        go.GetComponent<Image>().color = new Color(.20f, .27f, .35f, 1f);

        GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textGO.transform.SetParent(go.transform, false);
        RectTransform rect = textGO.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        TextMeshProUGUI text = textGO.GetComponent<TextMeshProUGUI>();
        text.text = label;
        text.fontSize = 9f;
        text.fontStyle = FontStyles.Bold;
        text.alignment = TextAlignmentOptions.Center;
        text.color = Color.white;
        text.raycastTarget = false;
        return go;
    }

    TextMeshProUGUI AddText(Transform parent, string value, float fontSize, float width)
    {
        GameObject go = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(width, 24f);
        TextMeshProUGUI text = go.GetComponent<TextMeshProUGUI>();
        text.text = value;
        text.fontSize = fontSize;
        text.alignment = TextAlignmentOptions.Center;
        text.color = new Color(.82f, .86f, .90f, 1f);
        text.raycastTarget = false;
        return text;
    }

    TMP_InputField AddIntInput(Transform parent, string placeholder, float width)
    {
        GameObject go = new GameObject(placeholder + "Input", typeof(RectTransform), typeof(Image), typeof(TMP_InputField));
        go.transform.SetParent(parent, false);
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(width, 24f);
        go.GetComponent<Image>().color = new Color(.16f, .18f, .22f, 1f);

        GameObject textArea = new GameObject("Text Area", typeof(RectTransform), typeof(RectMask2D));
        textArea.transform.SetParent(go.transform, false);
        RectTransform areaRect = textArea.GetComponent<RectTransform>();
        areaRect.anchorMin = Vector2.zero;
        areaRect.anchorMax = Vector2.one;
        areaRect.offsetMin = new Vector2(2f, 1f);
        areaRect.offsetMax = new Vector2(-2f, -1f);

        GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textGO.transform.SetParent(textArea.transform, false);
        RectTransform textRect = textGO.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;
        TextMeshProUGUI text = textGO.GetComponent<TextMeshProUGUI>();
        text.fontSize = 9f;
        text.alignment = TextAlignmentOptions.Center;
        text.color = Color.white;

        TMP_InputField input = go.GetComponent<TMP_InputField>();
        input.textViewport = areaRect;
        input.textComponent = text;
        input.contentType = TMP_InputField.ContentType.IntegerNumber;
        input.lineType = TMP_InputField.LineType.SingleLine;
        return input;
    }
}
