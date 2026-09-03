using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Deterministic +/- per-card variation underneath selected grooming controls.
// Owns its own runtime UI lifecycle: one panel instance, one variance row per channel.
public class GroomVarianceController : MonoBehaviour
{
    // APPEND ONLY. SignedRandom mixes (int)c into the deterministic per-card hash, so the
    // ordinal of every channel is part of the persisted random stream. Inserting a channel
    // anywhere but the end silently re-randomises every card in every project ever saved.
    private enum Channel { Length, Width, Bend, Twist, AngleX, AngleY, AngleZ, CurlFrequency, CurlDiameter, WaveAmplitude, WaveFrequency, WaveDirection, Arch }

    // THE VARIANCE ROW'S WIDTH BUDGET, in one place because it has to add up.
    //
    // CompactRightPanelAuthority narrows this panel to the left panel's width - 300px by default -
    // and then compacts each row's children to fit. It can only compact a child that carries a
    // LayoutElement, and none of the children here do: this row sizes them by raw RectTransform
    // with childControlWidth off. So nothing rescues these numbers at runtime and they have to
    // fit on their own.
    //
    //   300 panel - 20 root padding            = 280 inner
    //   280 - 8 row padding (GroomVarianceSeedUIFix re-asserts 4 left / 4 right every 0.1s)
    //       - 10 spacing (5 between three children)                        = 262 for children
    //
    // Caption + slider + value = 260. Two to spare.
    private const float CaptionWidth = 42f;
    private const float SliderWidth = 168f;
    private const float ValueWidth = 50f;

    // The name of the label carrying the variance NUMBER. Public because two other authorities
    // write it - see BuildVarianceRow.
    public const string ValueLabelName = "VarianceValue";

    [Serializable] private class VarianceSetting { public float amount; public int seed; }
    private class VarianceRow { public Slider slider; public TextMeshProUGUI valueText; public TMP_InputField seedInput; }

    private readonly Dictionary<int, Dictionary<Channel, VarianceSetting>> groupSettings = new();
    private readonly Dictionary<Channel, VarianceRow> rows = new();
    private readonly Dictionary<Channel, Slider> mainSliders = new();
    private readonly Dictionary<Channel, TextMeshProUGUI> mainLabels = new();
    private readonly HashSet<int> knownCardIds = new();

    private ModelViewer viewer;
    private GroomRootStateAuthority rootAuthority;
    private bool installed;
    private GameObject installedPanel;
    private int lastGroupId = int.MinValue;
    private int lastCardCount = -1;

    // -1 can never equal HairCard.RegistryVersion on the first pass, so the tracker below
    // always runs once before it is allowed to start skipping.
    private int lastSeenCardRegistryVersion = -1;
    private float nextInstallAttempt;

    public void Init(ModelViewer owner) { viewer = owner; }

    public List<VarianceChannelSaveData> ExportGroupSettings(int groupId)
    {
        List<VarianceChannelSaveData> result = new();
        foreach (Channel channel in Enum.GetValues(typeof(Channel)))
        {
            VarianceSetting s = GetSetting(groupId, channel);
            result.Add(new VarianceChannelSaveData { channel = channel.ToString(), amount = s.amount, seed = s.seed });
        }
        return result;
    }

    public void ImportGroupSettings(int groupId, List<VarianceChannelSaveData> data)
    {
        if (data != null)
        {
            foreach (VarianceChannelSaveData item in data)
            {
                if (item == null || !Enum.TryParse(item.channel, out Channel channel)) continue;
                VarianceSetting s = GetSetting(groupId, channel);
                s.amount = Mathf.Max(0f, item.amount);
                s.seed = item.seed;
            }
        }

        if (viewer != null && viewer.currentGroupId == groupId && installed)
            SyncRowsForGroup(groupId);

        ApplyAllVarianceForGroup(groupId);
        if (viewer != null && viewer.currentGroupId == groupId)
            SyncKnownCards(groupId);
    }

    public void ClearSavedSettings()
    {
        groupSettings.Clear();
        if (viewer != null && installed) SyncRowsForGroup(viewer.currentGroupId);
    }

    void Update()
    {
        if (viewer == null) viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer == null) return;
        if (rootAuthority == null) rootAuthority = FindFirstObjectByType<GroomRootStateAuthority>();

        GameObject currentPanel = viewer.groomingSliderPanelGO;
        if (installed && installedPanel != currentPanel)
            ResetUIBindings();

        if (!installed && currentPanel != null && Time.unscaledTime >= nextInstallAttempt)
        {
            nextInstallAttempt = Time.unscaledTime + 0.2f;
            TryInstall();
        }

        if (!installed) return;

        MaintainAngleLabels();
        if (viewer.currentGroupId != lastGroupId)
        {
            lastGroupId = viewer.currentGroupId;
            SyncRowsForGroup(lastGroupId);
            SyncKnownCards(lastGroupId);
        }

        TrackCardMembershipAndApplyNewCards(viewer.currentGroupId);
    }

    void ResetUIBindings()
    {
        installed = false;
        installedPanel = null;
        rows.Clear();
        mainSliders.Clear();
        mainLabels.Clear();
        knownCardIds.Clear();
        lastGroupId = int.MinValue;
        lastCardCount = -1;
        // Reset alongside lastCardCount, so a torn-down and reinstalled controller is forced
        // to re-examine membership rather than trusting a version stamp from the old session.
        lastSeenCardRegistryVersion = -1;
        nextInstallAttempt = 0f;
    }

    void TryInstall()
    {
        if (viewer.groomingSliderPanelGO == null) return;
        Transform panel = viewer.groomingSliderPanelGO.transform;

        var definitions = new[]
        {
            (Channel.Length,  "Length_Row",      "Length_Row",      "Length",      GroomLengthCurve.MaxVariance),
            (Channel.Width,   "Width_Row",       "Width_Row",       "Width",       0.05f),
            (Channel.Bend,    "Bend Angle_Row",  "Bend Angle_Row",  "Bend Angle",  360f),
            (Channel.Twist,   "Twist Angle_Row", "Twist Angle_Row", "Twist Angle", 360f),
            (Channel.AngleX,  "Angle X_Row",     "Offset X_Row",    "Angle X",     360f),
            (Channel.AngleY,  "Angle Y_Row",     "Offset Y_Row",    "Angle Y",     360f),
            (Channel.AngleZ,  "Angle Z_Row",     "Offset Z_Row",    "Angle Z",     360f),
            (Channel.CurlFrequency, "Curl Frequency_Row", "Curl Frequency_Row", "Curl Frequency", 5f),
            (Channel.CurlDiameter,  "Curl Diameter_Row",  "Curl Diameter_Row",  "Curl Diameter",  0.05f),
            (Channel.WaveAmplitude, "Wave Amplitude_Row", "Wave Amplitude_Row", "Wave Amplitude", 0.05f),
            (Channel.WaveFrequency, "Wave Frequency_Row", "Wave Frequency_Row", "Wave Frequency", 5f),
            (Channel.WaveDirection, "Wave Direction_Row", "Wave Direction_Row", "Wave Direction", 0.5f),
            (Channel.Arch,          "Arch_Row",           "Arch_Row",           "Arch",           0.5f)
        };

        Dictionary<Channel, Transform> mainRows = new();
        foreach (var d in definitions)
        {
            Transform row = panel.Find(d.Item2) ?? panel.Find(d.Item3);
            if (row == null || row.GetComponentInChildren<Slider>(true) == null) return;
            mainRows[d.Item1] = row;
        }

        // Clean up any generated rows left by an older install before creating one canonical set.
        // Matches on "_Variance" as a substring rather than a specific suffix so it catches every
        // row type this builds (main row, seed row, divider) - narrower matching here is exactly
        // what let an earlier divider-insertion feature elsewhere in this project leak orphaned
        // objects across reinstalls.
        // Condemned before destroyed, for the same reason ModelViewer's group list is: Destroy
        // does not take effect until the end of the frame, so the old rows would otherwise still
        // be alive, still rendered and still findable BY NAME while the replacements below are
        // built - one frame of a panel holding two of everything.
        foreach (Transform child in panel.Cast<Transform>().ToArray())
        {
            if (child == null || !child.name.Contains("_Variance")) continue;

            child.gameObject.SetActive(false);
            child.gameObject.name = "Discarded_" + child.gameObject.name;
            Destroy(child.gameObject);
        }

        rows.Clear();
        mainSliders.Clear();
        mainLabels.Clear();

        foreach (var d in definitions)
        {
            Transform mainRow = mainRows[d.Item1];
            Slider main = mainRow.GetComponentInChildren<Slider>(true);
            TextMeshProUGUI label = mainRow.GetComponentInChildren<TextMeshProUGUI>(true);
            mainSliders[d.Item1] = main;
            if (label != null) mainLabels[d.Item1] = label;

            if (d.Item1 >= Channel.AngleX) RenameMainControl(mainRow, d.Item4);

            VarianceRow row = BuildVarianceRow(panel, mainRow, d.Item1, d.Item5);
            rows[d.Item1] = row;

            Channel captured = d.Item1;
            main.onValueChanged.AddListener(_ =>
            {
                if (GetSetting(viewer.currentGroupId, captured).amount > 0f)
                    ApplyChannel(captured, viewer.currentGroupId);
                MaintainMainLabel(captured);
            });
        }

        // A few sliders (Segments, Embed Depth) don't get their own variance/seed block, but
        // should still get the same divider every other slider row now has for consistency.
        AddPlainDividerAfter(panel, "Segments_Row");
        AddPlainDividerAfter(panel, "Embed Depth_Row");

        installed = true;
        installedPanel = viewer.groomingSliderPanelGO;
        lastGroupId = viewer.currentGroupId;
        SyncKnownCards(lastGroupId);
        SyncRowsForGroup(lastGroupId);
        MaintainAngleLabels();
        ApplyAllVarianceForGroup(lastGroupId);
    }

    void RenameMainControl(Transform row, string newLabel)
    {
        row.name = newLabel + "_Row";
        TextMeshProUGUI text = row.GetComponentInChildren<TextMeshProUGUI>(true);
        Slider slider = row.GetComponentInChildren<Slider>(true);
        if (text != null && slider != null) text.text = newLabel + ": " + slider.value.ToString("F3");
        if (text != null) text.gameObject.name = newLabel + "_Text";
        if (slider != null) slider.gameObject.name = newLabel + "_Slider";
    }

    void MaintainAngleLabels()
    {
        MaintainMainLabel(Channel.AngleX);
        MaintainMainLabel(Channel.AngleY);
        MaintainMainLabel(Channel.AngleZ);
    }

    void MaintainMainLabel(Channel c)
    {
        if (!mainLabels.TryGetValue(c, out TextMeshProUGUI label) || label == null) return;
        if (!mainSliders.TryGetValue(c, out Slider slider) || slider == null) return;
        string name = c == Channel.AngleX ? "Angle X" : c == Channel.AngleY ? "Angle Y" : c == Channel.AngleZ ? "Angle Z" : null;
        if (name != null) label.text = name + ": " + slider.value.ToString("F3");
    }

    VarianceRow BuildVarianceRow(Transform panel, Transform mainRow, Channel channel, float maxVariance)
    {
        string key = ChannelLabel(channel);

        // First line: the variance amount itself, under the main slider it modifies.
        GameObject rowGO = new GameObject(key + "_VarianceRow", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        rowGO.transform.SetParent(panel, false);
        rowGO.transform.SetSiblingIndex(mainRow.GetSiblingIndex() + 1);
        rowGO.GetComponent<RectTransform>().sizeDelta = new Vector2(0, 20);
        LayoutElement rowLayoutElement = rowGO.GetComponent<LayoutElement>();
        rowLayoutElement.preferredHeight = 20f;
        rowLayoutElement.minHeight = 20f;

        HorizontalLayoutGroup layout = rowGO.GetComponent<HorizontalLayoutGroup>();
        layout.spacing = 5;
        layout.padding = new RectOffset(4, 2, 0, 0);
        layout.childControlHeight = true;
        layout.childControlWidth = false;
        layout.childForceExpandHeight = false;
        layout.childForceExpandWidth = false;

        // "VAR ±" on the LEFT, the number on the RIGHT of the slider - the shape every other
        // slider in this panel reads in.
        //
        // It was one label, "VAR ± 0.021", crammed into 82px on the left of the slider. That
        // width was measured against the 11pt it is built at, and it is not laid out at 11pt:
        // PanelTypographyScale bumps every label in this panel built at 13pt or under by two
        // points, so the string is drawn at 13, no longer fits, and TMP wraps it onto a second
        // line. A 20px row has no room for a second line, so it spilled DOWN over the SEED row
        // underneath - which is the overlapping mush of "0.021" and "SEE" in the panel.
        //
        // Every label in this row is NoWrap for that reason. The widths here are the only thing
        // keeping them on one line, and a label that cannot wrap fails visibly and locally
        // rather than quietly landing on top of the row below.
        TextMeshProUGUI captionText = AddText(rowGO.transform, "VAR ±", 11, CaptionWidth, "VarianceCaption");
        captionText.alignment = TextAlignmentOptions.MidlineLeft;
        captionText.textWrappingMode = TextWrappingModes.NoWrap;

        Slider varianceSlider = AddCompactSlider(rowGO.transform, 0, maxVariance, 0, SliderWidth);

        // A CURVED CHANNEL carries a 0-1 parameter rather than the amount itself, so its domain
        // is 1 whatever the channel's maximum is. Zero still means zero in both, which is what
        // lets every reset in the tool go on writing a plain 0 into it. The row is already named
        // by this point, which is how the curve recognises the slider - see GroomLengthCurve.
        if (GroomLengthCurve.IsLengthVarianceSlider(varianceSlider)) varianceSlider.maxValue = 1f;

        // Named, not just positioned. GroomSessionResetCoordinator and PostVarianceAffectorBridge
        // both reach into these rows to write this number, and both used to find it as "the first
        // Text child" / "the first label starting with VAR" - neither of which survives there
        // being two labels in the row. They look it up by this name now.
        TextMeshProUGUI valueText = AddText(rowGO.transform, "0.000", 11, ValueWidth, ValueLabelName);
        valueText.alignment = TextAlignmentOptions.MidlineLeft;
        valueText.textWrappingMode = TextWrappingModes.NoWrap;

        // Second line, its own row below the first: seed + reroll. Previously all of this lived
        // on one ~500px-wide line (label + slider + "SEED" + input + button); the panel this
        // lives in is roughly 300px, so everything past the variance slider was being pushed off
        // the edge and clipped - invisible, not missing. Splitting onto its own line keeps every
        // control within the panel's actual width instead of chasing a single line that can't fit.
        GameObject seedRowGO = new GameObject(key + "_VarianceSeedRow", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        seedRowGO.transform.SetParent(panel, false);
        seedRowGO.transform.SetSiblingIndex(mainRow.GetSiblingIndex() + 2);
        seedRowGO.GetComponent<RectTransform>().sizeDelta = new Vector2(0, 24);
        LayoutElement seedRowLayoutElement = seedRowGO.GetComponent<LayoutElement>();
        seedRowLayoutElement.preferredHeight = 24f;
        seedRowLayoutElement.minHeight = 24f;

        HorizontalLayoutGroup seedLayout = seedRowGO.GetComponent<HorizontalLayoutGroup>();
        seedLayout.spacing = 5;
        seedLayout.padding = new RectOffset(4, 2, 0, 0);
        seedLayout.childControlHeight = true;
        seedLayout.childControlWidth = false;
        seedLayout.childForceExpandHeight = false;
        seedLayout.childForceExpandWidth = false;

        // 44, not 38. Same cause as the row above: 38 was measured at the 10pt this is built
        // with, PanelTypographyScale draws it at 12, and "SEED" then wrapped to "SEE" / "D" -
        // the stray D sitting under the variance number in the panel.
        TextMeshProUGUI seedLabel = AddText(seedRowGO.transform, "SEED", 10, 44);
        seedLabel.alignment = TextAlignmentOptions.Center;
        seedLabel.textWrappingMode = TextWrappingModes.NoWrap;
        TMP_InputField seedInput = AddSeedField(seedRowGO.transform, 78);
        GameObject randomButton = AddButton(seedRowGO.transform, "RANDOMIZE", 92, 19);

        // Thin divider between this channel's block and the next, so each setting's variance
        // controls read as one clearly separated group rather than running into each other.
        GameObject dividerGO = new GameObject(key + "_VarianceDivider", typeof(RectTransform), typeof(LayoutElement), typeof(Image));
        dividerGO.transform.SetParent(panel, false);
        dividerGO.transform.SetSiblingIndex(mainRow.GetSiblingIndex() + 3);
        // The panel's VerticalLayoutGroup runs childControlHeight = false, meaning it sizes
        // children from their raw RectTransform, NOT from LayoutElement.preferredHeight - and a
        // fresh RectTransform defaults to 100x100. Not setting sizeDelta here is what made every
        // divider silently occupy 100px of layout space (the huge gaps), while the scroll bound
        // calculation read the 4px preferred height instead - undercounting total content by
        // ~100px per divider and cutting the scroll range short around Embed Depth.
        dividerGO.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 8f);
        LayoutElement dividerLayout = dividerGO.GetComponent<LayoutElement>();
        dividerLayout.minHeight = 8f;
        dividerLayout.preferredHeight = 8f;
        dividerLayout.flexibleWidth = 1f;
        Image dividerImage = dividerGO.GetComponent<Image>();
        dividerImage.raycastTarget = false;
        if (UITheme.DividerSprite != null)
        {
            dividerImage.sprite = UITheme.DividerSprite;
            dividerImage.type = Image.Type.Sliced;
            dividerImage.color = Color.white;
        }
        else
        {
            dividerImage.color = new Color(1f, 1f, 1f, .12f);
        }

        varianceSlider.onValueChanged.AddListener(v =>
        {
            // ValueOf, not v. On a curved channel v is the parameter and the amount is derived;
            // on every other channel the two are the same number and this costs a name compare.
            float amount = GroomLengthCurve.ValueOf(varianceSlider);

            VarianceSetting s = GetSetting(viewer.currentGroupId, channel);
            s.amount = amount;
            valueText.text = FormatVariance(channel, amount);
            ApplyChannel(channel, viewer.currentGroupId);
        });

        seedInput.onEndEdit.AddListener(value =>
        {
            VarianceSetting s = GetSetting(viewer.currentGroupId, channel);
            if (!int.TryParse(value, out int parsed)) parsed = 0;
            s.seed = parsed;
            seedInput.SetTextWithoutNotify(parsed.ToString());
            if (s.amount > 0f) ApplyChannel(channel, viewer.currentGroupId);
        });

        randomButton.GetComponent<Button>().onClick.AddListener(() =>
        {
            VarianceSetting s = GetSetting(viewer.currentGroupId, channel);
            s.seed = UnityEngine.Random.Range(0, 1000000);
            seedInput.SetTextWithoutNotify(s.seed.ToString());
            if (s.amount > 0f) ApplyChannel(channel, viewer.currentGroupId);
        });

        return new VarianceRow { slider = varianceSlider, valueText = valueText, seedInput = seedInput };
    }

    // Plain divider for slider rows that don't have their own variance/seed block (Segments,
    // Embed Depth) - same visual separator, named so the existing "_Variance" cleanup filter
    // still catches and clears it on reinstall.
    void AddPlainDividerAfter(Transform panel, string rowName)
    {
        Transform row = panel.Find(rowName);
        if (row == null) return;

        GameObject dividerGO = new GameObject(rowName + "_VarianceDivider", typeof(RectTransform), typeof(LayoutElement), typeof(Image));
        dividerGO.transform.SetParent(panel, false);
        dividerGO.transform.SetSiblingIndex(row.GetSiblingIndex() + 1);
        // Same childControlHeight=false sizing rule as the main divider above: sizeDelta is what
        // the panel layout actually uses, so it must be set explicitly or the default 100px wins.
        dividerGO.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 8f);
        LayoutElement dividerLayout = dividerGO.GetComponent<LayoutElement>();
        dividerLayout.minHeight = 8f;
        dividerLayout.preferredHeight = 8f;
        dividerLayout.flexibleWidth = 1f;
        Image dividerImage = dividerGO.GetComponent<Image>();
        dividerImage.raycastTarget = false;
        if (UITheme.DividerSprite != null)
        {
            dividerImage.sprite = UITheme.DividerSprite;
            dividerImage.type = Image.Type.Sliced;
            dividerImage.color = Color.white;
        }
        else
        {
            dividerImage.color = new Color(1f, 1f, 1f, .12f);
        }
    }

    void SyncRowsForGroup(int id)
    {
        foreach (var p in rows)
        {
            if (p.Value == null || p.Value.slider == null || p.Value.seedInput == null || p.Value.valueText == null) continue;
            VarianceSetting s = GetSetting(id, p.Key);
            p.Value.slider.SetValueWithoutNotify(GroomLengthCurve.ToSliderFor(p.Value.slider, s.amount));
            p.Value.seedInput.SetTextWithoutNotify(s.seed.ToString());
            p.Value.valueText.text = FormatVariance(p.Key, s.amount);
        }
    }

    VarianceSetting GetSetting(int id, Channel c)
    {
        if (!groupSettings.TryGetValue(id, out Dictionary<Channel, VarianceSetting> dict))
        {
            dict = new Dictionary<Channel, VarianceSetting>();
            groupSettings[id] = dict;
        }
        if (!dict.TryGetValue(c, out VarianceSetting s))
        {
            s = new VarianceSetting { amount = 0f, seed = 0 };
            dict[c] = s;
        }
        return s;
    }

    void TrackCardMembershipAndApplyNewCards(int groupId)
    {
        // Cheap gate first. This method is called unconditionally every frame, and it used to
        // pay a full scene scan, a LINQ filter, an array allocation and an N-element Any()
        // probe BEFORE reaching its own early-out - which, in the steady state, is every
        // single frame. No card has been created or destroyed since the last look means
        // membership cannot have changed, and one integer compare settles it.
        //
        // Deliberately a monotonic version rather than a count compare: destroy one card and
        // create another in the same frame - a re-brush, a group reassign - and the count is
        // unchanged while membership differs, so the new cards would silently never receive
        // their variance and would render flat next to their neighbours. Note also that
        // Destroy() is deferred to end of frame, so the Awake and OnDestroy of a swap can land
        // in different frames; a version counter handles that, a count compare does not.
        //
        // groupId is still re-checked below, because a card can change group without any card
        // entering or leaving the scene - see ModelViewer's shift-drag promotion. That path
        // goes through SelectGroup, which resets this tracker.
        if (HairCard.RegistryVersion == lastSeenCardRegistryVersion) return;
        lastSeenCardRegistryVersion = HairCard.RegistryVersion;

        HairCard[] cards = FindObjectsByType<HairCard>(FindObjectsSortMode.None).Where(c => c.groupId == groupId).ToArray();
        bool membershipChanged = cards.Length != lastCardCount || cards.Any(c => !knownCardIds.Contains(c.GetInstanceID()));
        if (!membershipChanged) return;

        // Card count changes are not groom edits. Only seed variance onto genuinely new cards;
        // never touch the canonical state of cards that were already in the group.
        foreach (HairCard card in cards)
            if (!knownCardIds.Contains(card.GetInstanceID()))
                ApplyAllVarianceForCard(card, groupId);

        knownCardIds.Clear();
        foreach (HairCard card in cards) knownCardIds.Add(card.GetInstanceID());
        lastCardCount = cards.Length;
    }

    void SyncKnownCards(int groupId)
    {
        knownCardIds.Clear();
        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None).Where(c => c.groupId == groupId))
            knownCardIds.Add(card.GetInstanceID());
        lastCardCount = knownCardIds.Count;
    }

    void ApplyAllVarianceForGroup(int groupId)
    {
        foreach (Channel c in Enum.GetValues(typeof(Channel)))
            if (GetSetting(groupId, c).amount > 0f)
                ApplyChannel(c, groupId);
    }

    void ApplyAllVarianceForCard(HairCard card, int groupId)
    {
        if (card == null) return;
        foreach (Channel c in Enum.GetValues(typeof(Channel)))
        {
            VarianceSetting s = GetSetting(groupId, c);
            if (s.amount > 0f)
                ApplyChannelToCard(card, c, groupId, MainValue(c, groupId), s);
        }
    }

    void ApplyChannel(Channel c, int groupId)
    {
        if (viewer == null) return;
        VarianceSetting s = GetSetting(groupId, c);
        float baseValue = MainValue(c, groupId);

        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None).Where(x => x.groupId == groupId))
            ApplyChannelToCard(card, c, groupId, baseValue, s);
    }

    void ApplyChannelToCard(HairCard card, Channel c, int groupId, float baseValue, VarianceSetting setting)
    {
        if (card == null) return;

        float varied = baseValue + SignedRandom(card, c, setting.seed, groupId) * setting.amount;
        HairCard.GroomState state = card.GetCanonicalState();

        switch (c)
        {
            case Channel.Length: state.length = Mathf.Max(.0001f, varied); break;
            case Channel.Width: state.width = Mathf.Max(.0005f, varied); break;
            case Channel.Bend: state.bend = varied; break;
            case Channel.Twist: state.twist = varied; break;
            case Channel.AngleX: state.x = varied; break;
            case Channel.AngleY: state.y = varied; break;
            case Channel.AngleZ: state.z = varied; break;
            case Channel.CurlFrequency: state.curlFrequency = varied; break;
            case Channel.CurlDiameter: state.curlDiameter = Mathf.Max(0f, varied); break;
            case Channel.WaveAmplitude: state.waveAmplitude = Mathf.Max(0f, varied); break;
            case Channel.WaveFrequency: state.waveFrequency = varied; break;
            case Channel.WaveDirection: state.waveDirection = Mathf.Clamp01(varied); break;
            case Channel.Arch: state.arch = Mathf.Max(0f, varied); break;
        }

        // Variance is upstream authored state. Write that canonical channel directly instead
        // of reading other channels from the POST-evaluated/rendered card and accidentally
        // baking downstream effects back into the base.
        card.SetCanonicalState(state, true);
    }

    float MainValue(Channel c, int groupId)
    {
        if (rootAuthority == null) rootAuthority = FindFirstObjectByType<GroomRootStateAuthority>();
        if (rootAuthority != null && rootAuthority.TryGetRootState(groupId, out GroomRootStateAuthority.RootState root))
        {
            return c switch
            {
                Channel.Length => root.length,
                Channel.Width => root.width,
                Channel.Bend => root.bend,
                Channel.Twist => root.twist,
                Channel.AngleX => root.x,
                Channel.AngleY => root.y,
                Channel.AngleZ => root.z,
                Channel.CurlFrequency => root.curlFrequency,
                Channel.CurlDiameter => root.curlDiameter,
                Channel.WaveAmplitude => root.waveAmplitude,
                Channel.WaveFrequency => root.waveFrequency,
                Channel.WaveDirection => root.waveDirection,
                Channel.Arch => root.arch,
                _ => 0f
            };
        }

        if (viewer != null && viewer.currentGroupId == groupId)
        {
            return c switch
            {
                Channel.Length => viewer.currentLength,
                Channel.Width => viewer.currentWidth,
                Channel.Bend => viewer.currentBend,
                Channel.Twist => viewer.currentTwist,
                Channel.AngleX => viewer.currentOffsetX,
                Channel.AngleY => viewer.currentOffsetY,
                Channel.AngleZ => viewer.currentOffsetZ,
                Channel.CurlFrequency => viewer.currentCurlFrequency,
                Channel.CurlDiameter => viewer.currentCurlDiameter,
                Channel.WaveAmplitude => viewer.currentWaveAmplitude,
                Channel.WaveFrequency => viewer.currentWaveFrequency,
                Channel.WaveDirection => viewer.currentWaveDirection,
                Channel.Arch => viewer.currentArch,
                _ => 0f
            };
        }

        // Fallback for an off-screen group: recover the deterministic root channel from one
        // canonical card by removing that card's saved variance contribution.
        HairCard sample = FindObjectsByType<HairCard>(FindObjectsSortMode.None).FirstOrDefault(x => x.groupId == groupId);
        if (sample != null)
        {
            HairCard.GroomState state = sample.GetCanonicalState();
            float value = c switch
            {
                Channel.Length => state.length,
                Channel.Width => state.width,
                Channel.Bend => state.bend,
                Channel.Twist => state.twist,
                Channel.AngleX => state.x,
                Channel.AngleY => state.y,
                Channel.AngleZ => state.z,
                Channel.CurlFrequency => state.curlFrequency,
                Channel.CurlDiameter => state.curlDiameter,
                Channel.WaveAmplitude => state.waveAmplitude,
                Channel.WaveFrequency => state.waveFrequency,
                Channel.WaveDirection => state.waveDirection,
                Channel.Arch => state.arch,
                _ => 0f
            };
            VarianceSetting s = GetSetting(groupId, c);
            if (s.amount > 0f)
                value -= SignedRandom(sample, c, s.seed, groupId) * s.amount;
            return value;
        }

        return 0f;
    }

    float SignedRandom(HairCard card, Channel c, int seed, int groupId)
    {
        // Identity, not placement. The two are the same value for every card placed normally;
        // they differ only after an operation that moved the whole groom (import rescale, REMAP),
        // and there this is what keeps the scatter the user authored instead of re-rolling it.
        Vector3 p = card.GetIdentityPoint();
        unchecked
        {
            uint h = 2166136261u;
            Mix(ref h, Mathf.RoundToInt(p.x * 10000));
            Mix(ref h, Mathf.RoundToInt(p.y * 10000));
            Mix(ref h, Mathf.RoundToInt(p.z * 10000));
            Mix(ref h, groupId);
            Mix(ref h, (int)c * 7919);
            Mix(ref h, seed);
            h ^= h >> 16; h *= 0x7feb352du; h ^= h >> 15; h *= 0x846ca68bu; h ^= h >> 16;
            return (h & 0x00FFFFFFu) / 16777215f * 2f - 1f;
        }
    }

    static void Mix(ref uint h, int v) { unchecked { h ^= (uint)v; h *= 16777619u; } }
    int CountCards(int id) => FindObjectsByType<HairCard>(FindObjectsSortMode.None).Count(c => c.groupId == id);
    string ChannelLabel(Channel c) => c switch { Channel.Length => "Length", Channel.Width => "Width", Channel.Bend => "Bend", Channel.Twist => "Twist", Channel.AngleX => "Angle X", Channel.AngleY => "Angle Y", Channel.AngleZ => "Angle Z", Channel.CurlFrequency => "Curl Frequency", Channel.CurlDiameter => "Curl Diameter", Channel.WaveAmplitude => "Wave Amplitude", Channel.WaveFrequency => "Wave Frequency", Channel.WaveDirection => "Wave Direction", Channel.Arch => "Arch", _ => c.ToString() };
    // Angular channels (Bend/Twist/AngleX-Z) show a degree symbol; everything else (including
    // Curl Frequency, a turn count, and Curl Diameter, a length-scale magnitude) is plain decimal.
    string FormatVariance(Channel c, float v) => c == Channel.Bend || c == Channel.Twist || c == Channel.AngleX || c == Channel.AngleY || c == Channel.AngleZ ? v.ToString("F1") + "°" : v.ToString("F3");

    TextMeshProUGUI AddText(Transform p, string text, int size, float width, string objectName = "Text")
    {
        GameObject go = new GameObject(objectName, typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(p, false);
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(width, 24);
        TextMeshProUGUI t = go.GetComponent<TextMeshProUGUI>();
        t.text = text; t.fontSize = size; t.color = new Color(.86f, .86f, .86f); t.alignment = TextAlignmentOptions.Center;
        return t;
    }

    // How tall the variance track line is, in pixels. Deliberately thin: this control sits under
    // a full-size slider and must not compete with it.
    private const float TrackHeight = 3f;

    Slider AddCompactSlider(Transform p, float min, float max, float value, float width)
    {
        GameObject go = new GameObject("VarianceSlider", typeof(RectTransform), typeof(Slider));
        go.transform.SetParent(p, false); go.GetComponent<RectTransform>().sizeDelta = new Vector2(width, 22);
        Slider s = go.GetComponent<Slider>(); s.minValue = min; s.maxValue = max; s.value = value;

        // THE TRACK. A 3px line the notch visibly rides along, rather than a proportion of the
        // row's height. The old version anchored 0.4-0.6 of a row whose height the layout group
        // controls, which is both unpredictable and, at this row's size, near enough invisible
        // against the panel - the notch read as a lone white tick floating in space.
        //
        // Anchored across the full width at the vertical CENTRE, with the height carried by the
        // offsets: with anchorMin.y and anchorMax.y equal, offsetMin/offsetMax are the distance
        // above and below that centre line, so this is exactly TrackHeight tall whatever the row
        // does around it.
        GameObject bg = new GameObject("Background", typeof(RectTransform), typeof(Image)); bg.transform.SetParent(go.transform, false);
        RectTransform br = bg.GetComponent<RectTransform>();
        br.anchorMin = new Vector2(0f, .5f); br.anchorMax = new Vector2(1f, .5f); br.pivot = new Vector2(.5f, .5f);
        br.offsetMin = new Vector2(0f, -TrackHeight * .5f); br.offsetMax = new Vector2(0f, TrackHeight * .5f);
        bg.GetComponent<Image>().color = new Color(.42f, .42f, .46f);

        GameObject fa = new GameObject("Fill Area", typeof(RectTransform)); fa.transform.SetParent(go.transform, false);
        RectTransform far = fa.GetComponent<RectTransform>();
        far.anchorMin = new Vector2(0f, .5f); far.anchorMax = new Vector2(1f, .5f); far.pivot = new Vector2(.5f, .5f);
        far.offsetMin = new Vector2(4f, -TrackHeight * .5f); far.offsetMax = new Vector2(-4f, TrackHeight * .5f);
        GameObject fill = new GameObject("Fill", typeof(RectTransform), typeof(Image)); fill.transform.SetParent(fa.transform, false);
        RectTransform fr = fill.GetComponent<RectTransform>(); fr.anchorMin = Vector2.zero; fr.anchorMax = Vector2.one; fr.offsetMin = Vector2.zero; fr.offsetMax = Vector2.zero; fill.GetComponent<Image>().color = new Color(.55f, .45f, .15f); s.fillRect = fr;
        GameObject ha = new GameObject("Handle Slide Area", typeof(RectTransform)); ha.transform.SetParent(go.transform, false);
        RectTransform har = ha.GetComponent<RectTransform>(); har.anchorMin = Vector2.zero; har.anchorMax = Vector2.one; har.offsetMin = new Vector2(6, 0); har.offsetMax = new Vector2(-6, 0);
        GameObject h = new GameObject("Handle", typeof(RectTransform), typeof(Image)); h.transform.SetParent(ha.transform, false); RectTransform hr = h.GetComponent<RectTransform>(); hr.sizeDelta = new Vector2(10, 16); h.GetComponent<Image>().color = Color.white; s.handleRect = hr;
        return s;
    }

    TMP_InputField AddSeedField(Transform p, float width)
    {
        GameObject go = new GameObject("SeedInput", typeof(RectTransform), typeof(Image), typeof(TMP_InputField));
        go.transform.SetParent(p, false); go.GetComponent<RectTransform>().sizeDelta = new Vector2(width, 24);
        Image seedBg = go.GetComponent<Image>();
        if (UITheme.FineEdgeSprite != null)
        {
            seedBg.sprite = UITheme.FineEdgeSprite;
            seedBg.type = Image.Type.Sliced;
            seedBg.color = Color.white;
        }
        else
        {
            seedBg.color = new Color(.12f, .12f, .12f);
        }
        GameObject tg = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI)); tg.transform.SetParent(go.transform, false);
        RectTransform tr = tg.GetComponent<RectTransform>(); tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one; tr.offsetMin = new Vector2(5, 1); tr.offsetMax = new Vector2(-5, -1);
        TextMeshProUGUI text = tg.GetComponent<TextMeshProUGUI>(); text.fontSize = 11; text.color = Color.white; text.alignment = TextAlignmentOptions.Center;
        TMP_InputField input = go.GetComponent<TMP_InputField>(); input.textComponent = text; input.contentType = TMP_InputField.ContentType.IntegerNumber; input.text = "0";
        return input;
    }

    GameObject AddButton(Transform p, string label, float width, float height = 24f)
    {
        GameObject go = new GameObject(label + "Button", typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(p, false);
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(width, height);

        TextMeshProUGUI text = AddText(go.transform, label, 11, width); RectTransform tr = text.rectTransform; tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one; tr.offsetMin = Vector2.zero; tr.offsetMax = Vector2.zero;

        // One shared style definition for all reroll buttons - see UITheme.StyleRerollButton.
        UITheme.StyleRerollButton(go.GetComponent<Button>());
        return go;
    }
}

[DefaultExecutionOrder(-950)]
public class GroomVarianceBootstrap : MonoBehaviour
{
    private ModelViewer boundViewer;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        GameObject go = new GameObject("GroomVarianceBootstrap");
        DontDestroyOnLoad(go);
        go.AddComponent<GroomVarianceBootstrap>();
    }

    void Update()
    {
        // Once bound, there is nothing left to do. This ran a scene type-scan AND a
        // GetComponent every frame for the entire session to re-discover a binding that was
        // established on the first frame and never changes afterwards.
        //
        // The null test is Unity's overloaded ==, so a destroyed viewer still compares equal
        // to null and rebinding resumes. (Nothing in the project destroys or replaces
        // ModelViewer - there is no SceneManager use anywhere and no OnDestroy on it - but
        // leaning on the Unity null check costs nothing and keeps the rebind path honest.)
        if (boundViewer != null) return;

        ModelViewer viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer == null) return;
        GroomVarianceController controller = viewer.GetComponent<GroomVarianceController>();
        if (controller == null) controller = viewer.gameObject.AddComponent<GroomVarianceController>();
        boundViewer = viewer;
        controller.Init(viewer);
    }
}
