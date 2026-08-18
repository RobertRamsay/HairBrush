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
    private enum Channel { Length, Width, Bend, Twist, AngleX, AngleY, AngleZ }

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
        nextInstallAttempt = 0f;
    }

    void TryInstall()
    {
        if (viewer.groomingSliderPanelGO == null) return;
        Transform panel = viewer.groomingSliderPanelGO.transform;

        var definitions = new[]
        {
            (Channel.Length,  "Length_Row",      "Length_Row",      "Length",      0.5f),
            (Channel.Width,   "Width_Row",       "Width_Row",       "Width",       0.05f),
            (Channel.Bend,    "Bend Angle_Row",  "Bend Angle_Row",  "Bend Angle",  360f),
            (Channel.Twist,   "Twist Angle_Row", "Twist Angle_Row", "Twist Angle", 360f),
            (Channel.AngleX,  "Angle X_Row",     "Offset X_Row",    "Angle X",     360f),
            (Channel.AngleY,  "Angle Y_Row",     "Offset Y_Row",    "Angle Y",     360f),
            (Channel.AngleZ,  "Angle Z_Row",     "Offset Z_Row",    "Angle Z",     360f)
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
        foreach (Transform child in panel.Cast<Transform>().ToArray())
        {
            if (child != null && child.name.Contains("_Variance"))
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
        GameObject rowGO = new GameObject(key + "_VarianceRow", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        rowGO.transform.SetParent(panel, false);
        rowGO.transform.SetSiblingIndex(mainRow.GetSiblingIndex() + 1);
        rowGO.GetComponent<RectTransform>().sizeDelta = new Vector2(0, 22);

        HorizontalLayoutGroup layout = rowGO.GetComponent<HorizontalLayoutGroup>();
        layout.spacing = 5;
        layout.padding = new RectOffset(4, 2, 2, 2);
        layout.childControlHeight = true;
        layout.childControlWidth = false;
        layout.childForceExpandHeight = false;
        layout.childForceExpandWidth = false;

        TextMeshProUGUI valueText = AddText(rowGO.transform, "VAR ± 0.000", 11, 82);
        valueText.alignment = TextAlignmentOptions.MidlineLeft;
        Slider varianceSlider = AddCompactSlider(rowGO.transform, 0, maxVariance, 0, 150);

        // Second line, its own row below the first: seed + reroll. Previously all of this lived
        // on one ~500px-wide line (label + slider + "SEED" + input + button); the panel this
        // lives in is roughly 300px, so everything past the variance slider was being pushed off
        // the edge and clipped - invisible, not missing. Splitting onto its own line keeps every
        // control within the panel's actual width instead of chasing a single line that can't fit.
        GameObject seedRowGO = new GameObject(key + "_VarianceSeedRow", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        seedRowGO.transform.SetParent(panel, false);
        seedRowGO.transform.SetSiblingIndex(mainRow.GetSiblingIndex() + 2);
        seedRowGO.GetComponent<RectTransform>().sizeDelta = new Vector2(0, 22);

        HorizontalLayoutGroup seedLayout = seedRowGO.GetComponent<HorizontalLayoutGroup>();
        seedLayout.spacing = 5;
        seedLayout.padding = new RectOffset(4, 2, 2, 2);
        seedLayout.childControlHeight = true;
        seedLayout.childControlWidth = false;
        seedLayout.childForceExpandHeight = false;
        seedLayout.childForceExpandWidth = false;

        TextMeshProUGUI seedLabel = AddText(seedRowGO.transform, "SEED", 10, 38);
        seedLabel.alignment = TextAlignmentOptions.Center;
        TMP_InputField seedInput = AddSeedField(seedRowGO.transform, 78);
        GameObject randomButton = AddButton(seedRowGO.transform, "R", 24);

        // Thin divider between this channel's block and the next, so each setting's variance
        // controls read as one clearly separated group rather than running into each other.
        GameObject dividerGO = new GameObject(key + "_VarianceDivider", typeof(RectTransform), typeof(LayoutElement), typeof(Image));
        dividerGO.transform.SetParent(panel, false);
        dividerGO.transform.SetSiblingIndex(mainRow.GetSiblingIndex() + 3);
        LayoutElement dividerLayout = dividerGO.GetComponent<LayoutElement>();
        dividerLayout.minHeight = 6f;
        dividerLayout.preferredHeight = 6f;
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
            VarianceSetting s = GetSetting(viewer.currentGroupId, channel);
            s.amount = v;
            valueText.text = "VAR ± " + FormatVariance(channel, v);
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
        LayoutElement dividerLayout = dividerGO.GetComponent<LayoutElement>();
        dividerLayout.minHeight = 6f;
        dividerLayout.preferredHeight = 6f;
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
            p.Value.slider.SetValueWithoutNotify(s.amount);
            p.Value.seedInput.SetTextWithoutNotify(s.seed.ToString());
            p.Value.valueText.text = "VAR ± " + FormatVariance(p.Key, s.amount);
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
            case Channel.Length: state.length = Mathf.Max(.0005f, varied); break;
            case Channel.Width: state.width = Mathf.Max(.0005f, varied); break;
            case Channel.Bend: state.bend = varied; break;
            case Channel.Twist: state.twist = varied; break;
            case Channel.AngleX: state.x = varied; break;
            case Channel.AngleY: state.y = varied; break;
            case Channel.AngleZ: state.z = varied; break;
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
        Vector3 p = card.GetSpawnHitPoint();
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
    string ChannelLabel(Channel c) => c switch { Channel.Length => "Length", Channel.Width => "Width", Channel.Bend => "Bend", Channel.Twist => "Twist", Channel.AngleX => "Angle X", Channel.AngleY => "Angle Y", Channel.AngleZ => "Angle Z", _ => c.ToString() };
    string FormatVariance(Channel c, float v) => c == Channel.Length || c == Channel.Width ? v.ToString("F3") : v.ToString("F1") + "°";

    TextMeshProUGUI AddText(Transform p, string text, int size, float width)
    {
        GameObject go = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(p, false);
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(width, 24);
        TextMeshProUGUI t = go.GetComponent<TextMeshProUGUI>();
        t.text = text; t.fontSize = size; t.color = new Color(.86f, .86f, .86f); t.alignment = TextAlignmentOptions.Center;
        return t;
    }

    Slider AddCompactSlider(Transform p, float min, float max, float value, float width)
    {
        GameObject go = new GameObject("VarianceSlider", typeof(RectTransform), typeof(Slider));
        go.transform.SetParent(p, false); go.GetComponent<RectTransform>().sizeDelta = new Vector2(width, 22);
        Slider s = go.GetComponent<Slider>(); s.minValue = min; s.maxValue = max; s.value = value;

        GameObject bg = new GameObject("Background", typeof(RectTransform), typeof(Image)); bg.transform.SetParent(go.transform, false);
        RectTransform br = bg.GetComponent<RectTransform>(); br.anchorMin = new Vector2(0, .4f); br.anchorMax = new Vector2(1, .6f); br.sizeDelta = Vector2.zero; bg.GetComponent<Image>().color = new Color(.28f, .28f, .28f);
        GameObject fa = new GameObject("Fill Area", typeof(RectTransform)); fa.transform.SetParent(go.transform, false);
        RectTransform far = fa.GetComponent<RectTransform>(); far.anchorMin = new Vector2(0, .35f); far.anchorMax = new Vector2(1, .65f); far.offsetMin = new Vector2(4, 0); far.offsetMax = new Vector2(-4, 0);
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
        go.transform.SetParent(p, false); go.GetComponent<RectTransform>().sizeDelta = new Vector2(width, 24); go.GetComponent<Image>().color = new Color(.12f, .12f, .12f);
        GameObject tg = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI)); tg.transform.SetParent(go.transform, false);
        RectTransform tr = tg.GetComponent<RectTransform>(); tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one; tr.offsetMin = new Vector2(5, 1); tr.offsetMax = new Vector2(-5, -1);
        TextMeshProUGUI text = tg.GetComponent<TextMeshProUGUI>(); text.fontSize = 11; text.color = Color.white; text.alignment = TextAlignmentOptions.Center;
        TMP_InputField input = go.GetComponent<TMP_InputField>(); input.textComponent = text; input.contentType = TMP_InputField.ContentType.IntegerNumber; input.text = "0";
        return input;
    }

    GameObject AddButton(Transform p, string label, float width)
    {
        GameObject go = new GameObject(label + "Button", typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(p, false);
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(width, 24);

        // Brighter than the standard muted button tint and sized closer to the seed field's own
        // 24px height, rather than leaving it at the default width/tint every other button gets
        // from the global reskin pass - this one's a reroll action and should read as distinct.
        Image image = go.GetComponent<Image>();
        Button button = go.GetComponent<Button>();
        if (UITheme.ButtonNormalSprite != null)
        {
            image.sprite = UITheme.ButtonNormalSprite;
            image.type = Image.Type.Sliced;
            image.color = new Color(.45f, .95f, 1f, 1f);
            button.transition = Selectable.Transition.SpriteSwap;
            SpriteState state = button.spriteState;
            state.highlightedSprite = UITheme.ButtonHoverSprite;
            state.pressedSprite = UITheme.ButtonClickSprite;
            button.spriteState = state;
        }
        else
        {
            image.color = new Color(.27f, .34f, .20f);
        }

        TextMeshProUGUI text = AddText(go.transform, label, 12, width); RectTransform tr = text.rectTransform; tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one; tr.offsetMin = Vector2.zero; tr.offsetMax = Vector2.zero;
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
        ModelViewer viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer == null) return;
        GroomVarianceController controller = viewer.GetComponent<GroomVarianceController>();
        if (controller == null) controller = viewer.gameObject.AddComponent<GroomVarianceController>();
        if (boundViewer != viewer)
        {
            boundViewer = viewer;
            controller.Init(viewer);
        }
    }
}
