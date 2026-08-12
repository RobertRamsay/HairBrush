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
    private enum Channel { Length, Bend, Twist, AngleX, AngleY, AngleZ }

    [Serializable] private class VarianceSetting { public float amount; public int seed; }
    private class VarianceRow { public Slider slider; public TextMeshProUGUI valueText; public TMP_InputField seedInput; }

    private readonly Dictionary<int, Dictionary<Channel, VarianceSetting>> groupSettings = new();
    private readonly Dictionary<Channel, VarianceRow> rows = new();
    private readonly Dictionary<Channel, Slider> mainSliders = new();
    private readonly Dictionary<Channel, TextMeshProUGUI> mainLabels = new();

    private ModelViewer viewer;
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
            lastCardCount = CountCards(lastGroupId);
        }

        int count = CountCards(viewer.currentGroupId);
        if (count != lastCardCount)
        {
            lastCardCount = count;
            ApplyAllVarianceForGroup(viewer.currentGroupId);
        }
    }

    void ResetUIBindings()
    {
        installed = false;
        installedPanel = null;
        rows.Clear();
        mainSliders.Clear();
        mainLabels.Clear();
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
            (Channel.Length,  "Length_Row",      "Length_Row",      "Length",     0.5f),
            (Channel.Bend,    "Bend Angle_Row",  "Bend Angle_Row",  "Bend Angle", 360f),
            (Channel.Twist,   "Twist Angle_Row", "Twist Angle_Row", "Twist Angle",360f),
            (Channel.AngleX,  "Angle X_Row",     "Offset X_Row",    "Angle X",    360f),
            (Channel.AngleY,  "Angle Y_Row",     "Offset Y_Row",    "Angle Y",    360f),
            (Channel.AngleZ,  "Angle Z_Row",     "Offset Z_Row",    "Angle Z",    360f)
        };

        Dictionary<Channel, Transform> mainRows = new();
        foreach (var d in definitions)
        {
            Transform row = panel.Find(d.Item2) ?? panel.Find(d.Item3);
            if (row == null || row.GetComponentInChildren<Slider>(true) == null) return;
            mainRows[d.Item1] = row;
        }

        // Clean up any generated rows left by an older buggy install before creating one canonical set.
        foreach (Transform child in panel.Cast<Transform>().ToArray())
        {
            if (child != null && child.name.EndsWith("_VarianceRow", StringComparison.Ordinal))
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

        installed = true;
        installedPanel = viewer.groomingSliderPanelGO;
        lastGroupId = viewer.currentGroupId;
        lastCardCount = CountCards(lastGroupId);
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
        GameObject rowGO = new GameObject(key + "_VarianceRow", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        rowGO.transform.SetParent(panel, false);
        rowGO.transform.SetSiblingIndex(mainRow.GetSiblingIndex() + 1);
        rowGO.GetComponent<RectTransform>().sizeDelta = new Vector2(0, 28);

        HorizontalLayoutGroup layout = rowGO.GetComponent<HorizontalLayoutGroup>();
        layout.spacing = 5;
        layout.padding = new RectOffset(4, 2, 2, 2);
        layout.childControlHeight = true;
        layout.childControlWidth = false;
        layout.childForceExpandHeight = false;
        layout.childForceExpandWidth = false;

        TextMeshProUGUI valueText = AddText(rowGO.transform, "VAR ± 0.000", 11, 82);
        valueText.alignment = TextAlignmentOptions.MidlineLeft;
        Slider varianceSlider = AddCompactSlider(rowGO.transform, 0, maxVariance, 0, 245);
        TextMeshProUGUI seedLabel = AddText(rowGO.transform, "SEED", 10, 38);
        seedLabel.alignment = TextAlignmentOptions.Center;
        TMP_InputField seedInput = AddSeedField(rowGO.transform, 78);
        GameObject randomButton = AddButton(rowGO.transform, "R", 30);

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

    void ApplyAllVarianceForGroup(int groupId)
    {
        foreach (Channel c in Enum.GetValues(typeof(Channel)))
            if (GetSetting(groupId, c).amount > 0f)
                ApplyChannel(c, groupId);
    }

    void ApplyChannel(Channel c, int groupId)
    {
        if (viewer == null) return;
        VarianceSetting s = GetSetting(groupId, c);
        float baseValue = MainValue(c);

        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None).Where(x => x.groupId == groupId))
        {
            float varied = baseValue + SignedRandom(card, c, s.seed, groupId) * s.amount;
            float l = card.length, bend = card.bendAngle, twist = card.twistAngle;
            float x = card.GetOffsetX(), y = card.GetOffsetY(), z = card.GetOffsetZ();

            switch (c)
            {
                case Channel.Length: l = Mathf.Max(.0005f, varied); break;
                case Channel.Bend: bend = varied; break;
                case Channel.Twist: twist = varied; break;
                case Channel.AngleX: x = varied; break;
                case Channel.AngleY: y = varied; break;
                case Channel.AngleZ: z = varied; break;
            }

            card.SetParameters(l, card.width, card.segments, bend, twist, x, y, z, card.GetEmbedDepth(), 1f, card.uScale, card.vScale, card.uOffset, card.vOffset);
        }
    }

    float MainValue(Channel c) => c switch
    {
        Channel.Length => viewer.currentLength,
        Channel.Bend => viewer.currentBend,
        Channel.Twist => viewer.currentTwist,
        Channel.AngleX => viewer.currentOffsetX,
        Channel.AngleY => viewer.currentOffsetY,
        Channel.AngleZ => viewer.currentOffsetZ,
        _ => 0f
    };

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
    string ChannelLabel(Channel c) => c switch { Channel.Length => "Length", Channel.Bend => "Bend", Channel.Twist => "Twist", Channel.AngleX => "Angle X", Channel.AngleY => "Angle Y", Channel.AngleZ => "Angle Z", _ => c.ToString() };
    string FormatVariance(Channel c, float v) => c == Channel.Length ? v.ToString("F3") : v.ToString("F1") + "°";

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
        go.transform.SetParent(p, false); go.GetComponent<RectTransform>().sizeDelta = new Vector2(width, 24); go.GetComponent<Image>().color = new Color(.27f, .34f, .20f);
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
