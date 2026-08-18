using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Group-level UV source selector.
// ADJUSTABLE uses the existing U/V Scale + Offset controls.
// PREDETERMINED maps each card to one authored Texture Editor UV rectangle, chosen
// deterministically from the group's inclusive rect-ID range and seed. Rectangle choices
// come from the material assigned to that group, so every material can own a different atlas.
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

    private readonly Dictionary<int, GroupUVSettings> settingsByGroup = new();
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

    private GameObject boundPanel;
    private GameObject modeRow;
    private Button modeButton;
    private TextMeshProUGUI modeButtonText;
    private TextMeshProUGUI rectStatusText;
    private GameObject predeterminedRow;
    private TMP_InputField minInput;
    private TMP_InputField maxInput;
    private TMP_InputField seedInput;
    private Button randomButton;
    private GameObject uScaleRow;
    private GameObject vScaleRow;
    private GameObject uOffsetRow;
    private GameObject vOffsetRow;

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
            nextUIScan = Time.unscaledTime + .06f;
            MaintainRightPanelUI();
            RemoveLegacyLeftRows();
        }

        if (Time.unscaledTime >= nextApplyScan)
        {
            nextApplyScan = Time.unscaledTime + .10f;
            ApplyPredeterminedAssignments();
        }
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
        nextUIScan = 0f;
        nextApplyScan = 0f;
        if (viewer != null) MaintainRightPanelUI();
    }

    void RestorePendingProject()
    {
        HairProjectSaveData pending = HairProjectSaveData.PendingGroupUVRestore;
        if (pending == null) return;

        // Rectangle definitions restore in the texture/material authority first. Do not apply
        // group ranges against a previous project's rectangle set during model-load handoff.
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
                NormalizeRange(group.groupId, settings);
            }
        }

        nextUIScan = 0f;
        nextApplyScan = 0f;
        ApplyPredeterminedAssignments();
    }

    void MaintainRightPanelUI()
    {
        if (viewer == null || viewer.groomingSliderPanelGO == null) return;

        if (boundPanel != viewer.groomingSliderPanelGO || modeRow == null)
            BindRightPanel(viewer.groomingSliderPanelGO);
        if (modeRow == null) return;

        int groupId = viewer.currentGroupId;
        GroupUVSettings settings = GetSettings(groupId);
        List<UVRectSaveData> rects = GetRectsForGroup(groupId);
        bool haveRects = rects.Count > 0;
        bool editingPost = IsEditingPost();

        modeButtonText.text = settings.predetermined ? "PREDETERMINED" : "ADJUSTABLE";
        rectStatusText.text = haveRects ? rects.Count + " UV RECTS" : "NO UV RECTS";

        modeButton.interactable = !editingPost && (haveRects || settings.predetermined);

        if (minInput != null)
        {
            if (minInput.text != settings.minId.ToString()) minInput.SetTextWithoutNotify(settings.minId.ToString());
            minInput.interactable = settings.predetermined && haveRects && !editingPost;
        }
        if (maxInput != null)
        {
            if (maxInput.text != settings.maxId.ToString()) maxInput.SetTextWithoutNotify(settings.maxId.ToString());
            maxInput.interactable = settings.predetermined && haveRects && !editingPost;
        }
        if (seedInput != null)
        {
            if (seedInput.text != settings.seed.ToString()) seedInput.SetTextWithoutNotify(settings.seed.ToString());
            seedInput.interactable = settings.predetermined && haveRects && !editingPost;
        }
        if (randomButton != null)
            randomButton.interactable = settings.predetermined && haveRects && !editingPost;

        SetRowActive(uScaleRow, !settings.predetermined);
        SetRowActive(vScaleRow, !settings.predetermined);
        SetRowActive(uOffsetRow, !settings.predetermined);
        SetRowActive(vOffsetRow, !settings.predetermined);
        if (predeterminedRow != null) predeterminedRow.SetActive(settings.predetermined);

        Image buttonImage = modeButton != null ? modeButton.GetComponent<Image>() : null;
        if (buttonImage != null)
            buttonImage.color = settings.predetermined
                ? new Color(.20f, .50f, .80f, 1f)
                : new Color(.25f, .25f, .25f, 1f);

        MaintainRightPanelOrder();
    }

    void BindRightPanel(GameObject panel)
    {
        boundPanel = panel;
        modeRow = null;
        modeButton = null;
        modeButtonText = null;
        rectStatusText = null;
        predeterminedRow = null;
        minInput = null;
        maxInput = null;
        seedInput = null;
        randomButton = null;

        if (panel == null) return;
        Transform root = panel.transform;
        uScaleRow = FindDirectOrDeep(root, "U Scale_Row")?.gameObject;
        vScaleRow = FindDirectOrDeep(root, "V Scale_Row")?.gameObject;
        uOffsetRow = FindDirectOrDeep(root, "U Offset_Row")?.gameObject;
        vOffsetRow = FindDirectOrDeep(root, "V Offset_Row")?.gameObject;
        if (uScaleRow == null || vScaleRow == null || uOffsetRow == null || vOffsetRow == null) return;

        Transform oldMode = root.Find("GroupUVMode_Row");
        if (oldMode != null) Destroy(oldMode.gameObject);
        Transform oldPred = root.Find("GroupUVPredetermined_Row");
        if (oldPred != null) Destroy(oldPred.gameObject);

        BuildModeRow(root);
        BuildPredeterminedRow(root);
        MaintainRightPanelOrder();
    }

    void BuildModeRow(Transform parent)
    {
        modeRow = new GameObject("GroupUVMode_Row", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        modeRow.transform.SetParent(parent, false);
        modeRow.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 38f);

        HorizontalLayoutGroup layout = modeRow.GetComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(0, 0, 3, 3);
        layout.spacing = 8f;
        layout.childControlWidth = false;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;

        TextMeshProUGUI label = AddText(modeRow.transform, "UV MODE", 14f, 92f, TextAlignmentOptions.MidlineLeft);
        label.fontStyle = FontStyles.Bold;

        GameObject buttonGO = AddButton(modeRow.transform, "ADJUSTABLE", 290f, 30f);
        buttonGO.name = "GroupUVModeButton";
        modeButton = buttonGO.GetComponent<Button>();
        modeButtonText = buttonGO.GetComponentInChildren<TextMeshProUGUI>(true);
        modeButton.onClick.AddListener(() => ToggleMode(viewer.currentGroupId));

        rectStatusText = AddText(modeRow.transform, "NO UV RECTS", 11f, 120f, TextAlignmentOptions.MidlineRight);
        rectStatusText.color = new Color(.72f, .78f, .86f, 1f);
    }

    void BuildPredeterminedRow(Transform parent)
    {
        predeterminedRow = new GameObject("GroupUVPredetermined_Row", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        predeterminedRow.transform.SetParent(parent, false);
        predeterminedRow.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 38f);

        HorizontalLayoutGroup layout = predeterminedRow.GetComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(0, 0, 3, 3);
        layout.spacing = 6f;
        layout.childControlWidth = false;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;

        TextMeshProUGUI rectLabel = AddText(predeterminedRow.transform, "UV RECTS", 13f, 88f, TextAlignmentOptions.MidlineLeft);
        rectLabel.fontStyle = FontStyles.Bold;
        minInput = AddIntInput(predeterminedRow.transform, "MIN", 64f);
        AddText(predeterminedRow.transform, "→", 13f, 22f, TextAlignmentOptions.Center);
        maxInput = AddIntInput(predeterminedRow.transform, "MAX", 64f);
        AddText(predeterminedRow.transform, "SEED", 11f, 48f, TextAlignmentOptions.Center);
        seedInput = AddIntInput(predeterminedRow.transform, "SEED", 105f);
        GameObject randomGO = AddButton(predeterminedRow.transform, "R", 42f, 30f);
        randomGO.name = "GroupUVRandomSeedButton";
        randomButton = randomGO.GetComponent<Button>();

        minInput.onEndEdit.AddListener(value => SetRangeValue(viewer.currentGroupId, true, value));
        maxInput.onEndEdit.AddListener(value => SetRangeValue(viewer.currentGroupId, false, value));
        seedInput.onEndEdit.AddListener(value => SetSeed(viewer.currentGroupId, value));
        randomButton.onClick.AddListener(() => RandomizeSeed(viewer.currentGroupId));
    }

    void MaintainRightPanelOrder()
    {
        if (modeRow == null || uScaleRow == null || modeRow.transform.parent != uScaleRow.transform.parent) return;
        Transform parent = modeRow.transform.parent;

        int uvStart = uScaleRow.transform.GetSiblingIndex();
        modeRow.transform.SetSiblingIndex(Mathf.Clamp(uvStart, 0, parent.childCount - 1));
        if (predeterminedRow != null)
            predeterminedRow.transform.SetSiblingIndex(Mathf.Min(modeRow.transform.GetSiblingIndex() + 1, parent.childCount - 1));

        if (!GetSettings(viewer.currentGroupId).predetermined)
        {
            int insert = modeRow.transform.GetSiblingIndex() + 1;
            uScaleRow.transform.SetSiblingIndex(Mathf.Min(insert++, parent.childCount - 1));
            vScaleRow.transform.SetSiblingIndex(Mathf.Min(insert++, parent.childCount - 1));
            uOffsetRow.transform.SetSiblingIndex(Mathf.Min(insert++, parent.childCount - 1));
            vOffsetRow.transform.SetSiblingIndex(Mathf.Min(insert, parent.childCount - 1));
        }
    }

    void RemoveLegacyLeftRows()
    {
        foreach (RectTransform row in FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (row == null || !row.name.StartsWith("GroupUV_", StringComparison.Ordinal)) continue;
            if (row.name == "GroupUVMode_Row" || row.name == "GroupUVPredetermined_Row") continue;
            Destroy(row.gameObject);
        }
    }

    void ToggleMode(int groupId)
    {
        if (IsEditingPost()) return;

        GroupUVSettings settings = GetSettings(groupId);
        if (!settings.predetermined)
        {
            List<UVRectSaveData> rects = GetRectsForGroup(groupId);
            if (rects.Count == 0) return;

            int min = rects.Min(r => r.id);
            int max = rects.Max(r => r.id);
            if (settings.minId <= 0 || settings.maxId <= 0 ||
                !rects.Any(r => r.id >= settings.minId && r.id <= settings.maxId) ||
                (settings.minId == 1 && settings.maxId == 1 && rects.Count > 1))
            {
                settings.minId = min;
                settings.maxId = max;
            }

            NormalizeRange(groupId, settings);
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
        MaintainRightPanelUI();
    }

    void SetRangeValue(int groupId, bool isMin, string value)
    {
        if (IsEditingPost()) return;
        GroupUVSettings settings = GetSettings(groupId);
        if (!int.TryParse(value, out int parsed)) parsed = isMin ? settings.minId : settings.maxId;
        if (isMin) settings.minId = parsed;
        else settings.maxId = parsed;
        NormalizeRange(groupId, settings);
        ClearAppliedForGroup(groupId);
        ForceApplyGroup(groupId);
        nextUIScan = 0f;
    }

    void SetSeed(int groupId, string value)
    {
        if (IsEditingPost()) return;
        GroupUVSettings settings = GetSettings(groupId);
        if (!int.TryParse(value, out int parsed)) parsed = 0;
        settings.seed = parsed;
        ClearAppliedForGroup(groupId);
        ForceApplyGroup(groupId);
        nextUIScan = 0f;
    }

    void RandomizeSeed(int groupId)
    {
        if (IsEditingPost()) return;
        GroupUVSettings settings = GetSettings(groupId);
        settings.seed = UnityEngine.Random.Range(0, 1000000);
        ClearAppliedForGroup(groupId);
        ForceApplyGroup(groupId);
        nextUIScan = 0f;
    }

    void NormalizeRange(int groupId, GroupUVSettings settings)
    {
        if (settings == null) return;
        List<UVRectSaveData> rects = GetRectsForGroup(groupId);
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
        foreach (KeyValuePair<int, GroupUVSettings> pair in settingsByGroup)
        {
            if (pair.Value == null || !pair.Value.predetermined) continue;
            List<UVRectSaveData> groupRects = GetRectsForGroup(pair.Key);
            if (groupRects.Count == 0) continue;
            ApplyGroup(pair.Key, pair.Value, groupRects);
        }

        HashSet<int> liveCardIds = new HashSet<int>(FindObjectsByType<HairCard>(FindObjectsSortMode.None)
            .Where(card => card != null).Select(card => card.GetInstanceID()));
        foreach (int dead in appliedSignatureByCard.Keys.Where(id => !liveCardIds.Contains(id)).ToArray())
            appliedSignatureByCard.Remove(dead);
    }

    void ForceApplyGroup(int groupId)
    {
        GroupUVSettings settings = GetSettings(groupId);
        if (!settings.predetermined) return;
        ApplyGroup(groupId, settings, GetRectsForGroup(groupId));
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

    List<UVRectSaveData> GetRectsForGroup(int groupId)
    {
        if (MaterialUVRectAuthority.TryGetRectsForGroup(groupId, out List<UVRectSaveData> materialRects))
            return materialRects.Where(rect => rect != null).OrderBy(rect => rect.id).ToList();

        // Compatibility fallback if the material authority is unavailable during an unusual
        // bootstrap frame. An empty material list is NOT allowed to fall back globally.
        if (workspace == null) workspace = FindFirstObjectByType<TextureUVRectWorkspace>();
        return workspace != null
            ? workspace.ExportDefinitions().Where(rect => rect != null).OrderBy(rect => rect.id).ToList()
            : new List<UVRectSaveData>();
    }

    bool GroupHasPost(int groupId)
    {
        if (posts == null) return false;
        List<PostAffectorSaveData> list = posts.ExportGroup(groupId);
        return list != null && list.Count > 0;
    }

    bool IsEditingPost()
    {
        if (posts == null || activePostIdField == null || hasSelectionField == null || viewer == null) return false;
        int activeId = activePostIdField.GetValue(posts) is int id ? id : -1;
        bool selected = hasSelectionField.GetValue(viewer) is bool b && b;
        return activeId >= 0 && selected;
    }

    static int StableCardHash(HairCard card, int groupId, int seed)
    {
        Vector3 p = card.GetSpawnHitPoint();
        if (p == Vector3.zero) p = card.transform.position;
        Vector3 n = card.GetSurfaceNormal();
        unchecked
        {
            uint hash = 2166136261u;
            Mix(ref hash, Mathf.RoundToInt(p.x * 10000f));
            Mix(ref hash, Mathf.RoundToInt(p.y * 10000f));
            Mix(ref hash, Mathf.RoundToInt(p.z * 10000f));
            Mix(ref hash, Mathf.RoundToInt(n.x * 10000f));
            Mix(ref hash, Mathf.RoundToInt(n.y * 10000f));
            Mix(ref hash, Mathf.RoundToInt(n.z * 10000f));
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

    static void SetRowActive(GameObject row, bool active)
    {
        if (row != null && row.activeSelf != active) row.SetActive(active);
    }

    static Transform FindDirectOrDeep(Transform root, string name)
    {
        if (root == null) return null;
        Transform direct = root.Find(name);
        if (direct != null) return direct;
        foreach (Transform child in root)
        {
            if (child.name == name) return child;
            Transform nested = FindDirectOrDeep(child, name);
            if (nested != null) return nested;
        }
        return null;
    }

    GameObject AddButton(Transform parent, string label, float width, float height)
    {
        GameObject go = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(width, height);
        go.GetComponent<Image>().color = new Color(.25f, .25f, .25f, 1f);

        GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textGO.transform.SetParent(go.transform, false);
        RectTransform rect = textGO.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        TextMeshProUGUI text = textGO.GetComponent<TextMeshProUGUI>();
        text.text = label;
        text.fontSize = 12f;
        text.fontStyle = FontStyles.Bold;
        text.alignment = TextAlignmentOptions.Center;
        text.color = Color.white;
        text.raycastTarget = false;
        return go;
    }

    TextMeshProUGUI AddText(Transform parent, string value, float fontSize, float width, TextAlignmentOptions alignment)
    {
        GameObject go = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(width, 30f);
        TextMeshProUGUI text = go.GetComponent<TextMeshProUGUI>();
        text.text = value;
        text.fontSize = fontSize;
        text.alignment = alignment;
        text.color = Color.white;
        text.raycastTarget = false;
        return text;
    }

    TMP_InputField AddIntInput(Transform parent, string placeholder, float width)
    {
        GameObject go = new GameObject(placeholder + "Input", typeof(RectTransform), typeof(Image), typeof(TMP_InputField));
        go.transform.SetParent(parent, false);
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(width, 30f);
        Image inputBg = go.GetComponent<Image>();
        if (UITheme.FineEdgeSprite != null)
        {
            inputBg.sprite = UITheme.FineEdgeSprite;
            inputBg.type = Image.Type.Sliced;
            inputBg.color = Color.white;
        }
        else
        {
            inputBg.color = new Color(.16f, .18f, .22f, 1f);
        }

        GameObject textArea = new GameObject("Text Area", typeof(RectTransform), typeof(RectMask2D));
        textArea.transform.SetParent(go.transform, false);
        RectTransform areaRect = textArea.GetComponent<RectTransform>();
        areaRect.anchorMin = Vector2.zero;
        areaRect.anchorMax = Vector2.one;
        areaRect.offsetMin = new Vector2(4f, 2f);
        areaRect.offsetMax = new Vector2(-4f, -2f);

        GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textGO.transform.SetParent(textArea.transform, false);
        RectTransform textRect = textGO.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;
        TextMeshProUGUI text = textGO.GetComponent<TextMeshProUGUI>();
        text.fontSize = 12f;
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
