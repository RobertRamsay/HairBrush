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

        // PREDETERMINED only - see GroupSaveData.uvFlipV for why there are two levels of flip.
        // XORed with each rectangle's own flipV, so turning a group over does not reach into
        // strips that other groups are sharing.
        public bool flipV;
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
    private GameObject flipRow;
    private Button flipButton;
    private TextMeshProUGUI flipButtonText;
    private Image flipButtonImage;
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
        group.uvFlipV = settings.flipV;
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
                settings.flipV = group.uvFlipV;

                // Ordered and floored, but NOT clamped to the rectangles that happen to exist
                // right now. A range can legitimately be saved pointing outside the current set -
                // a rect was deleted, AUTO replaced the set, the group moved to a smaller atlas -
                // and ApplyGroup already falls back to a clamped view of it for as long as that
                // is true, without destroying it. Clamping here would write that fallback into
                // the settings and then persist it on the next save, so restoring the rectangles
                // could never restore the range that went with them.
                SanitizeRange(settings);
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
        MaintainFlipRow(settings, rects, haveRects, editingPost);

        Image buttonImage = modeButton != null ? modeButton.GetComponent<Image>() : null;
        if (buttonImage != null)
            buttonImage.color = settings.predetermined
                ? new Color(.20f, .50f, .80f, 1f)
                : new Color(.25f, .25f, .25f, 1f);

        MaintainRightPanelOrder();
    }

    // The FLIP V button's whole visible state: shown or not, on or off, live or dead.
    // rects is handed in rather than fetched. GetRectsForGroup is not a getter: it runs a
    // FindFirstObjectByType, a material-generation sync and a full Clone of the atlas, and
    // MaintainRightPanelUI has already paid for all of that a few lines above. Asking again
    // would double it, sixteen times a second, forever, for a label.
    void MaintainFlipRow(GroupUVSettings settings, List<UVRectSaveData> rects, bool haveRects, bool editingPost)
    {
        if (flipRow == null) return;

        // Hidden while a POST is selected. In that state the panel's sliders are editing the
        // POST, not the group, and a button that says FLIP V while everything around it means
        // something else is worse than no button.
        //
        // Hidden from HERE, on this authority's own IsEditingPost, rather than by the arrangement
        // that hides the PREDETERMINED row - that one is PostPredeterminedUVUIAuthority reaching
        // in and calling SetActive(false) on a row it does not own, under a narrower predicate
        // that also requires the POST's group to resolve and to be in PREDETERMINED. Two owners
        // for one row's visibility is how the row ends up flickering between them.
        bool visible = !editingPost;
        if (flipRow.activeSelf != visible) flipRow.SetActive(visible);
        if (!visible) return;

        bool on = FlipIsOn(settings);

        string label = "FLIP V: OFF";
        if (on) label = "FLIP V: ON";

        if (settings.predetermined)
        {
            // How many of the strips this group can actually draw are already turned over in the
            // texture editor. Without it the button is the only flip you can see, and a group
            // whose sheet is half one way and half the other looks like a bug rather than like
            // three strips waiting to be marked.
            int flipped = CountFlippedInRange(settings, rects, out int total);
            if (flipped > 0) label += "  (" + flipped + "/" + total + " STRIPS FLIPPED)";
        }
        if (flipButtonText != null && flipButtonText.text != label) flipButtonText.text = label;

        if (flipButtonImage != null)
        {
            Color colour = new Color(.25f, .25f, .25f, 1f);
            if (on) colour = new Color(.20f, .58f, .45f, 1f);
            if (flipButtonImage.color != colour) flipButtonImage.color = colour;
        }

        if (flipButton == null) return;

        bool live;
        if (settings.predetermined)
        {
            // Same rule as the rest of the PREDETERMINED controls, and it stays live under GROOM
            // LOCKED for the same reason they do: which part of a texture a card samples is group
            // metadata, not groom geometry.
            //
            // Note HOW it stays live, because it is not the way the neighbouring rows manage it.
            // They are on ModifierCoreLock's IsInsideUVRouting list; this row is NOT, and cannot
            // be, because that list would also exempt it in ADJUSTABLE where it must lock. It
            // survives instead because that authority's button pass only touches buttons under a
            // *_VarianceRow. So this line is the only thing deciding it, in both modes - if a
            // slider or an input field is ever added to this row, it will NOT get the same
            // treatment, and IsInsideUVRouting will lock it in both modes rather than one.
            live = haveRects;
        }
        else
        {
            // ADJUSTABLE, where this button IS the V Scale slider. Read the lock off that slider
            // rather than working it out again, so the two cannot disagree about a state
            // ModifierCoreLock owns. In this mode the flip IS groom geometry, so it locks.
            Slider vScale = null;
            if (vScaleRow != null) vScale = vScaleRow.GetComponentInChildren<Slider>(true);

            live = true;
            if (vScale != null) live = vScale.interactable;
        }
        flipButton.interactable = live;

        // The row's own dim, because ModifierCoreLock cannot do it for us. Its dim pass gives a
        // row one alpha, and this row needs two: full brightness in PREDETERMINED, where it stays
        // usable under the lock alongside the UV RECTS row, and the same 0.48 as its neighbours
        // in ADJUSTABLE, where it locks with them. That is why the row is on that pass's skip
        // list - so the alpha is ours to set here, from the same `live` the button just took.
        // Left alone it would have been a bright, dead, full-width button in a dimmed column.
        CanvasGroup group = flipRow.GetComponent<CanvasGroup>();
        if (group == null) group = flipRow.AddComponent<CanvasGroup>();
        float alpha = 1f;
        if (!live) alpha = .48f;
        if (!Mathf.Approximately(group.alpha, alpha)) group.alpha = alpha;
    }

    // What the button reads as ON, per mode. PREDETERMINED has its own flag; ADJUSTABLE has only
    // ever had the sign of V Scale.
    bool FlipIsOn(GroupUVSettings settings)
    {
        if (settings.predetermined) return settings.flipV;
        return CurrentGroupVScale() < 0f;
    }

    // Counted over ResolveRange, the same range ApplyGroup draws from, so the number on the
    // button is the number of strips this group can actually land on - including when the
    // authored range has fallen outside the live rectangles and ApplyGroup is running on its
    // clamped fallback. Filtering by minId/maxId here would report 0/0 in exactly that case.
    //
    // A plain loop rather than BuildAllowed's list, because this runs on the 16Hz panel scan
    // and BuildAllowed allocates a Where, an OrderBy and a List every time it is called.
    static int CountFlippedInRange(GroupUVSettings settings, List<UVRectSaveData> rects, out int total)
    {
        total = 0;
        if (!ResolveRange(settings, rects, out int lo, out int hi)) return 0;

        int flipped = 0;
        foreach (UVRectSaveData rect in rects)
        {
            if (rect == null || rect.id < lo || rect.id > hi) continue;
            total++;
            if (rect.flipV) flipped++;
        }
        return flipped;
    }

    float CurrentGroupVScale()
    {
        return GroupFloat(groupVScalesField, viewer.currentGroupId, 1f);
    }

    void ToggleFlipV(int groupId)
    {
        if (IsEditingPost()) return;
        GroupUVSettings settings = GetSettings(groupId);

        if (settings.predetermined)
        {
            settings.flipV = !settings.flipV;
            ClearAppliedForGroup(groupId);
            ForceApplyGroup(groupId);
        }
        else
        {
            // ADJUSTABLE: negate the group's V Scale, which is the flip this mode has always had.
            //
            // Refused for any group but the current one, and that is not defensiveness. There is
            // no per-group setter for V Scale: ModelViewer.OnSliderVScaleChanged writes
            // currentVScale and groupVScales[currentGroupId], and ApplyGroupUpdate walks
            // currentGroupId's cards. Running it for another id would edit the wrong group while
            // reporting success - so the parameter has to be honoured by refusing, rather than
            // by quietly meaning something else here than it means in the branch above.
            if (viewer == null || viewer.currentGroupId != groupId) return;
            //
            // The zero case is not a no-op. V Scale's range is -1..1 and a card at 0 draws a
            // single line of the texture, so -0 would leave the button looking dead. Falling to
            // -1 is the same choice HairCardSaveData.FlipLegacyAbsoluteV makes for exactly the
            // same reason, and lands on the mirrored form of the default.
            //
            // It is the one input this button does not round-trip: 0 goes to -1 and comes back
            // as +1, so a group parked at 0 cannot be returned to 0 with the button. Restoring a
            // degenerate value is not worth a second special case - the slider is right there.
            float current = CurrentGroupVScale();
            float flipped = -current;
            if (Mathf.Approximately(current, 0f)) flipped = -1f;

            viewer.OnSliderVScaleChanged(flipped);

            // OnSliderVScaleChanged writes the value and the cards but not the slider widget -
            // it is normally CALLED BY the widget. Without this the handle stays where it was
            // and the next drag snaps the hair back from wherever it was left.
            PushGroomSliders();
        }

        nextUIScan = 0f;
        MaintainRightPanelUI();
    }

    // ModelViewer.PushAllGroomSliders is private and there is no public equivalent. Reflected
    // rather than duplicated: re-implementing it here would be a second list of every slider in
    // the panel, silently going stale the first time one was added.
    void PushGroomSliders()
    {
        if (viewer == null) return;
        MethodInfo push = typeof(ModelViewer).GetMethod("PushAllGroomSliders",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (push != null) push.Invoke(viewer, null);
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
        flipRow = null;
        flipButton = null;
        flipButtonText = null;
        flipButtonImage = null;

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
        Transform oldFlip = root.Find("GroupUVFlip_Row");
        if (oldFlip != null) Destroy(oldFlip.gameObject);

        BuildModeRow(root);
        BuildPredeterminedRow(root);
        BuildFlipRow(root);
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

    // FLIP V, in BOTH modes, and it means the same thing in both: this group's hair is coming
    // out root-end-first against the texture.
    //
    // What it writes is not the same in both, and it cannot be. In PREDETERMINED there is a real
    // group flag to turn over (settings.flipV). In ADJUSTABLE there has never been one, because
    // the sign of the group's own V Scale has always been the flip - so there the button negates
    // that slider rather than inventing a second store that would then have to be reconciled
    // with it. See ToggleFlipV.
    //
    // Named "GroupUVFlip_Row" and NOT "GroupUV_Flip_Row": RemoveLegacyLeftRows destroys every
    // RectTransform whose name starts with "GroupUV_" bar two whitelisted ones, so the
    // underscore would delete this row on the next scan after it was built.
    void BuildFlipRow(Transform parent)
    {
        flipRow = new GameObject("GroupUVFlip_Row", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        flipRow.transform.SetParent(parent, false);
        flipRow.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 34f);

        HorizontalLayoutGroup layout = flipRow.GetComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(0, 0, 2, 2);
        layout.spacing = 0f;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        GameObject buttonGO = AddButton(flipRow.transform, "FLIP V: OFF", 0f, 28f);
        buttonGO.name = "GroupUVFlipButton";
        flipButton = buttonGO.GetComponent<Button>();
        flipButtonText = buttonGO.GetComponentInChildren<TextMeshProUGUI>(true);
        flipButtonImage = buttonGO.GetComponent<Image>();

        // The strip count makes this the longest label in the panel, and PanelTypographyScale
        // takes the 12 AddButton set up to 14 the first frame it sees it.
        //
        // Belt and braces rather than the mechanism: CompactRightPanelAuthority already forces
        // NoWrap and autosizing down to 9pt on every button label under GroomingPanel, so the
        // count shrinks to fit rather than wrapping or clipping. This says the same thing
        // locally, so that a row read on its own does not look like it is relying on TMP's
        // wrapping default - which would put a second line half outside a 28px button.
        if (flipButtonText != null)
        {
            flipButtonText.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
            flipButtonText.overflowMode = TMPro.TextOverflowModes.Ellipsis;
        }

        LayoutElement element = buttonGO.AddComponent<LayoutElement>();
        element.minHeight = 28f;
        element.preferredHeight = 28f;
        element.flexibleWidth = 1f;

        flipButton.onClick.AddListener(() => ToggleFlipV(viewer.currentGroupId));
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
            vOffsetRow.transform.SetSiblingIndex(Mathf.Min(insert++, parent.childCount - 1));

            // Under the four sliders rather than between V Scale and U Offset. It belongs with
            // them, but breaking up U Scale / V Scale / U Offset / V Offset would cost more in
            // scannability than the adjacency is worth.
            if (flipRow != null)
                flipRow.transform.SetSiblingIndex(Mathf.Min(insert, parent.childCount - 1));
        }
        else if (flipRow != null && predeterminedRow != null)
        {
            flipRow.transform.SetSiblingIndex(
                Mathf.Min(predeterminedRow.transform.GetSiblingIndex() + 1, parent.childCount - 1));
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

    // Ordering and the floor only. Everything NormalizeRange does that depends on which
    // rectangles exist at this moment is left out on purpose - see the call in
    // RestorePendingProject for why that distinction matters.
    static void SanitizeRange(GroupUVSettings settings)
    {
        if (settings == null) return;
        settings.minId = Mathf.Max(1, settings.minId);
        settings.maxId = Mathf.Max(1, settings.maxId);
        if (settings.minId > settings.maxId)
        {
            int swap = settings.minId;
            settings.minId = settings.maxId;
            settings.maxId = swap;
        }
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

    // The rect-id range a group is ACTUALLY drawing from right now: its authored range, or the
    // clamped view of it explained below when that range currently selects nothing. False when
    // there is nothing to draw from at all.
    //
    // Split out so that ApplyGroup and the FLIP V button's "n/N STRIPS FLIPPED" count agree on
    // which strips are in play. Worked out separately, the count went quiet in exactly the case
    // it is most wanted - a range pointing outside the live set, where ApplyGroup is quietly
    // running on a fallback the panel knew nothing about.
    //
    // Deliberately allocation-free, and that is not premature: the panel asks this 16 times a
    // second for a label, forever, and it is the reason the count does not simply reuse
    // BuildAllowed's list.
    static bool ResolveRange(GroupUVSettings settings, List<UVRectSaveData> allRects, out int lo, out int hi)
    {
        lo = 0;
        hi = -1;
        if (settings == null || allRects == null || allRects.Count == 0) return false;

        int lowest = int.MaxValue;
        int highest = int.MinValue;
        int live = 0;
        int inRange = 0;

        foreach (UVRectSaveData rect in allRects)
        {
            if (rect == null) continue;
            live++;
            if (rect.id < lowest) lowest = rect.id;
            if (rect.id > highest) highest = rect.id;
            if (rect.id >= settings.minId && rect.id <= settings.maxId) inRange++;
        }
        if (live == 0) return false;

        lo = settings.minId;
        hi = settings.maxId;
        if (inRange > 0) return true;

        // The rectangle set can shrink underneath a stored range at any time: a rect deleted with
        // a right click, AUTO replacing five hand-drawn rects with three of its own, a group
        // reassigned to a material with a smaller atlas. When it does, a range like 4-5 selects
        // nothing, and this used to return without touching a card - leaving every card in the
        // group pointing at rectangles that no longer exist, while the panel still read as a
        // healthy PREDETERMINED group with no way to tell.
        //
        // Clamped for THIS pass only, and deliberately never written back to settings. Whatever
        // took the rectangles away can put them back - UNDO LAST, undo, switching to the material
        // they came from - and the authored range has to still be there when it does. A clamp
        // stored in settings would have thrown it away a tenth of a second after it went out of
        // range, permanently, in response to something the user was about to reverse.
        lo = Mathf.Clamp(settings.minId, lowest, highest);
        hi = Mathf.Clamp(settings.maxId, lowest, highest);
        if (lo > hi)
        {
            int swap = lo;
            lo = hi;
            hi = swap;
        }
        return true;
    }

    static List<UVRectSaveData> BuildAllowed(GroupUVSettings settings, List<UVRectSaveData> allRects)
    {
        if (!ResolveRange(settings, allRects, out int lo, out int hi)) return new List<UVRectSaveData>();

        return allRects
            .Where(rect => rect != null && rect.id >= lo && rect.id <= hi)
            .OrderBy(rect => rect.id)
            .ToList();
    }

    void ApplyGroup(int groupId, GroupUVSettings settings, List<UVRectSaveData> allRects)
    {
        if (settings == null || !settings.predetermined || allRects == null || allRects.Count == 0) return;

        List<UVRectSaveData> allowed = BuildAllowed(settings, allRects);
        if (allowed.Count == 0) return;

        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
        {
            if (card == null || card.groupId != groupId) continue;

            int pick = PositiveMod(StableCardHash(card, groupId, settings.seed), allowed.Count);
            UVRectSaveData rect = allowed[pick];
            int signature = RectSignature(rect, settings.seed, allowed.Count, settings.flipV);
            int cardId = card.GetInstanceID();
            if (appliedSignatureByCard.TryGetValue(cardId, out int previous) && previous == signature) continue;

            HairCard.GroomState canonical = card.GetCanonicalState();
            canonical.uScale = Mathf.Max(.000001f, rect.uMax - rect.uMin);
            canonical.vScale = SignedVScale(rect, settings.flipV);
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

    // The one place a rectangle plus the two flips become a card's V ramp, so the group path and
    // the POST override cannot drift apart. PostPredeterminedUVAuthority calls this too.
    //
    // The sign IS the flip. HairCard.GenerateMesh reads a negative vScale as "run the ramp the
    // other way" (see the baseV lines there): positive puts the root at vMax and the tip at vMin,
    // negative puts the root at vMin and the tip at vMax. Magnitude is the rectangle's height
    // either way, and vOffset stays vMin either way, so nothing but the direction changes.
    //
    // Clamped away from zero for the same reason the old expression was: a rectangle that has
    // been squashed to nothing must not produce a degenerate UV span, and Mathf.Max cannot be
    // applied after the negation without throwing the sign away.
    public static float SignedVScale(UVRectSaveData rect, bool groupFlip)
    {
        if (rect == null) return 1f;
        float span = Mathf.Max(.000001f, rect.vMax - rect.vMin);

        // XOR, not OR. A strip already marked as root-at-the-bottom in the texture editor and a
        // group told to turn over cancel each other out, which is the only answer that lets the
        // two controls be used together rather than one having to know about the other.
        if (rect.flipV != groupFlip) return -span;
        return span;
    }

    static int RectSignature(UVRectSaveData rect, int seed, int allowedCount, bool groupFlip)
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

            // Both flips. This hash is what stops a card being rewritten every tenth of a second,
            // so anything that changes the UVs a card ends up with has to be in it - a flip moves
            // no edge, so without these two lines toggling one would repaint the button and leave
            // the hair exactly as it was.
            int rectFlipBit = 0;
            if (rect.flipV) rectFlipBit = 1;
            hash = hash * 31 + rectFlipBit;

            int groupFlipBit = 0;
            if (groupFlip) groupFlipBit = 1;
            hash = hash * 31 + groupFlipBit;
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
