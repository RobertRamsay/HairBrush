using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Adds deterministic +/- per-card variation underneath selected grooming controls.
// This deliberately sits beside ModelViewer rather than expanding that already-large class.
public class GroomVarianceController : MonoBehaviour
{
    private enum Channel { Length, Bend, Twist, AngleX, AngleY, AngleZ }

    [Serializable]
    private class VarianceSetting
    {
        public float amount;
        public int seed;
    }

    private class VarianceRow
    {
        public Channel channel;
        public Slider slider;
        public TextMeshProUGUI valueText;
        public TMP_InputField seedInput;
    }

    private readonly Dictionary<int, Dictionary<Channel, VarianceSetting>> groupSettings = new();
    private readonly Dictionary<Channel, VarianceRow> rows = new();
    private readonly Dictionary<Channel, Slider> mainSliders = new();

    private ModelViewer viewer;
    private bool installed;
    private int lastGroupId = int.MinValue;
    private int lastCardCount = -1;
    private float nextInstallAttempt;

    public void Init(ModelViewer owner)
    {
        viewer = owner;
    }

    void Update()
    {
        if (viewer == null) viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer == null) return;

        if (!installed && Time.unscaledTime >= nextInstallAttempt)
        {
            nextInstallAttempt = Time.unscaledTime + 0.25f;
            TryInstall();
        }

        if (!installed) return;

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
            ApplyAllActiveVariance();
        }
    }

    void TryInstall()
    {
        if (viewer.groomingSliderPanelGO == null) return;
        Transform panel = viewer.groomingSliderPanelGO.transform;

        var definitions = new[]
        {
            (Channel.Length, "Length_Row", "Length", 0.5f),
            (Channel.Bend, "Bend Angle_Row", "Bend Angle", 360f),
            (Channel.Twist, "Twist Angle_Row", "Twist Angle", 360f),
            (Channel.AngleX, "Offset X_Row", "Angle X", 360f),
            (Channel.AngleY, "Offset Y_Row", "Angle Y", 360f),
            (Channel.AngleZ, "Offset Z_Row", "Angle Z", 360f),
        };

        foreach (var d in definitions)
        {
            Transform mainRow = panel.Find(d.Item2);
            if (mainRow == null) return;
            Slider main = mainRow.GetComponentInChildren<Slider>(true);
            if (main == null) return;
        }

        foreach (var d in definitions)
        {
            Transform mainRow = panel.Find(d.Item2);
            Slider main = mainRow.GetComponentInChildren<Slider>(true);
            mainSliders[d.Item1] = main;

            if (d.Item1 == Channel.AngleX || d.Item1 == Channel.AngleY || d.Item1 == Channel.AngleZ)
                RenameMainControl(mainRow, d.Item3);

            VarianceRow row = BuildVarianceRow(panel, mainRow, d.Item1, d.Item4);
            rows[d.Item1] = row;

            Channel captured = d.Item1;
            // ModelViewer's listener was registered first, so this runs after the main value
            // has updated and reapplies the deterministic per-card spread around that value.
            main.onValueChanged.AddListener(_ => ApplyChannel(captured));
        }

        installed = true;
        lastGroupId = viewer.currentGroupId;
        lastCardCount = CountCards(lastGroupId);
        SyncRowsForGroup(lastGroupId);
    }

    void RenameMainControl(Transform row, string newLabel)
    {
        row.name = newLabel + "_Row";
        TextMeshProUGUI text = row.GetComponentInChildren<TextMeshProUGUI>(true);
        Slider slider = row.GetComponentInChildren<Slider>(true);
        if (text != null && slider != null)
            text.text = newLabel + ": " + slider.value.ToString("F3");
        if (text != null) text.gameObject.name = newLabel + "_Text";
        if (slider != null) slider.gameObject.name = newLabel + "_Slider";
    }

    VarianceRow BuildVarianceRow(Transform panel, Transform mainRow, Channel channel, float maxVariance)
    {
        string key = ChannelLabel(channel);
        GameObject rowGO = new GameObject(key + "_VarianceRow", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        rowGO.transform.SetParent(panel, false);
        rowGO.transform.SetSiblingIndex(mainRow.GetSiblingIndex() + 1);
        rowGO.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 28f);

        HorizontalLayoutGroup layout = rowGO.GetComponent<HorizontalLayoutGroup>();
        layout.spacing = 5f;
        layout.padding = new RectOffset(4, 2, 2, 2);
        layout.childControlHeight = true;
        layout.childControlWidth = false;
        layout.childForceExpandHeight = false;
        layout.childForceExpandWidth = false;

        TextMeshProUGUI valueText = AddText(rowGO.transform, "VAR ± 0.000", 11, 82f);
        valueText.alignment = TextAlignmentOptions.MidlineLeft;

        Slider varianceSlider = AddCompactSlider(rowGO.transform, 0f, maxVariance, 0f, 245f);
        TextMeshProUGUI seedLabel = AddText(rowGO.transform, "SEED", 10, 38f);
        seedLabel.alignment = TextAlignmentOptions.Center;
        TMP_InputField seedInput = AddSeedField(rowGO.transform, 78f);
        GameObject randomButton = AddButton(rowGO.transform, "R", 30f);

        VarianceRow result = new VarianceRow
        {
            channel = channel,
            slider = varianceSlider,
            valueText = valueText,
            seedInput = seedInput
        };

        varianceSlider.onValueChanged.AddListener(v =>
        {
            VarianceSetting setting = GetSetting(viewer.currentGroupId, channel);
            setting.amount = v;
            valueText.text = "VAR ± " + FormatVariance(channel, v);
            ApplyChannel(channel);
        });

        seedInput.onEndEdit.AddListener(value =>
        {
            VarianceSetting setting = GetSetting(viewer.currentGroupId, channel);
            if (!int.TryParse(value, out int parsed)) parsed = 0;
            setting.seed = parsed;
            seedInput.SetTextWithoutNotify(parsed.ToString());
            ApplyChannel(channel);
        });

        randomButton.GetComponent<Button>().onClick.AddListener(() =>
        {
            VarianceSetting setting = GetSetting(viewer.currentGroupId, channel);
            setting.seed = UnityEngine.Random.Range(1, int.MaxValue);
            seedInput.SetTextWithoutNotify(setting.seed.ToString());
            ApplyChannel(channel);
        });

        return result;
    }

    void SyncRowsForGroup(int groupId)
    {
        foreach (var pair in rows)
        {
            VarianceSetting setting = GetSetting(groupId, pair.Key);
            pair.Value.slider.SetValueWithoutNotify(setting.amount);
            pair.Value.seedInput.SetTextWithoutNotify(setting.seed.ToString());
            pair.Value.valueText.text = "VAR ± " + FormatVariance(pair.Key, setting.amount);
        }
    }

    VarianceSetting GetSetting(int groupId, Channel channel)
    {
        if (!groupSettings.TryGetValue(groupId, out Dictionary<Channel, VarianceSetting> byChannel))
        {
            byChannel = new Dictionary<Channel, VarianceSetting>();
            groupSettings[groupId] = byChannel;
        }

        if (!byChannel.TryGetValue(channel, out VarianceSetting setting))
        {
            setting = new VarianceSetting { amount = 0f, seed = 0 };
            byChannel[channel] = setting;
        }
        return setting;
    }

    void ApplyAllActiveVariance()
    {
        foreach (Channel channel in Enum.GetValues(typeof(Channel)))
        {
            if (GetSetting(viewer.currentGroupId, channel).amount > 0f)
                ApplyChannel(channel);
        }
    }

    void ApplyChannel(Channel channel)
    {
        if (viewer == null) return;
        VarianceSetting setting = GetSetting(viewer.currentGroupId, channel);
        float baseValue = MainValue(channel);

        HairCard[] cards = FindObjectsByType<HairCard>(FindObjectsSortMode.None)
            .Where(c => c.groupId == viewer.currentGroupId)
            .ToArray();

        foreach (HairCard card in cards)
        {
            float signed = SignedRandom(card, channel, setting.seed);
            float varied = baseValue + signed * setting.amount;

            float length = card.length;
            float bend = card.bendAngle;
            float twist = card.twistAngle;
            float angleX = card.GetOffsetX();
            float angleY = card.GetOffsetY();
            float angleZ = card.GetOffsetZ();

            switch (channel)
            {
                case Channel.Length: length = Mathf.Max(0.0005f, varied); break;
                case Channel.Bend: bend = varied; break;
                case Channel.Twist: twist = varied; break;
                case Channel.AngleX: angleX = varied; break;
                case Channel.AngleY: angleY = varied; break;
                case Channel.AngleZ: angleZ = varied; break;
            }

            card.SetParameters(
                length, card.width, card.segments, bend, twist,
                angleX, angleY, angleZ, card.GetEmbedDepth(), 1f,
                card.uScale, card.vScale, card.uOffset, card.vOffset);
        }
    }

    float MainValue(Channel channel)
    {
        return channel switch
        {
            Channel.Length => viewer.currentLength,
            Channel.Bend => viewer.currentBend,
            Channel.Twist => viewer.currentTwist,
            Channel.AngleX => viewer.currentOffsetX,
            Channel.AngleY => viewer.currentOffsetY,
            Channel.AngleZ => viewer.currentOffsetZ,
            _ => 0f
        };
    }

    float SignedRandom(HairCard card, Channel channel, int seed)
    {
        Vector3 p = card.GetSpawnHitPoint();
        int qx = Mathf.RoundToInt(p.x * 10000f);
        int qy = Mathf.RoundToInt(p.y * 10000f);
        int qz = Mathf.RoundToInt(p.z * 10000f);

        unchecked
        {
            uint h = 2166136261u;
            Mix(ref h, qx);
            Mix(ref h, qy);
            Mix(ref h, qz);
            Mix(ref h, viewer.currentGroupId);
            Mix(ref h, (int)channel * 7919);
            Mix(ref h, seed);
            h ^= h >> 16;
            h *= 0x7feb352du;
            h ^= h >> 15;
            h *= 0x846ca68bu;
            h ^= h >> 16;
            float zeroToOne = (h & 0x00FFFFFFu) / 16777215f;
            return zeroToOne * 2f - 1f;
        }
    }

    static void Mix(ref uint h, int value)
    {
        unchecked
        {
            h ^= (uint)value;
            h *= 16777619u;
        }
    }

    int CountCards(int groupId)
    {
        return FindObjectsByType<HairCard>(FindObjectsSortMode.None).Count(c => c.groupId == groupId);
    }

    string ChannelLabel(Channel channel)
    {
        return channel switch
        {
            Channel.Length => "Length",
            Channel.Bend => "Bend",
            Channel.Twist => "Twist",
            Channel.AngleX => "Angle X",
            Channel.AngleY => "Angle Y",
            Channel.AngleZ => "Angle Z",
            _ => channel.ToString()
        };
    }

    string FormatVariance(Channel channel, float value)
    {
        return channel == Channel.Length ? value.ToString("F3") : value.ToString("F1") + "°";
    }

    TextMeshProUGUI AddText(Transform parent, string text, int size, float width)
    {
        GameObject go = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(width, 24f);
        TextMeshProUGUI tmp = go.GetComponent<TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = size;
        tmp.color = new Color(0.86f, 0.86f, 0.86f);
        tmp.alignment = TextAlignmentOptions.Center;
        return tmp;
    }

    Slider AddCompactSlider(Transform parent, float min, float max, float value, float width)
    {
        GameObject go = new GameObject("VarianceSlider", typeof(RectTransform), typeof(Slider));
        go.transform.SetParent(parent, false);
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(width, 22f);
        Slider slider = go.GetComponent<Slider>();
        slider.minValue = min;
        slider.maxValue = max;
        slider.value = value;

        GameObject bg = new GameObject("Background", typeof(RectTransform), typeof(Image));
        bg.transform.SetParent(go.transform, false);
        RectTransform br = bg.GetComponent<RectTransform>();
        br.anchorMin = new Vector2(0f, 0.4f); br.anchorMax = new Vector2(1f, 0.6f); br.sizeDelta = Vector2.zero;
        bg.GetComponent<Image>().color = new Color(0.28f, 0.28f, 0.28f);

        GameObject fillArea = new GameObject("Fill Area", typeof(RectTransform));
        fillArea.transform.SetParent(go.transform, false);
        RectTransform far = fillArea.GetComponent<RectTransform>();
        far.anchorMin = new Vector2(0f, 0.35f); far.anchorMax = new Vector2(1f, 0.65f); far.offsetMin = new Vector2(4f, 0f); far.offsetMax = new Vector2(-4f, 0f);

        GameObject fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
        fill.transform.SetParent(fillArea.transform, false);
        RectTransform fr = fill.GetComponent<RectTransform>();
        fr.anchorMin = Vector2.zero; fr.anchorMax = Vector2.one; fr.offsetMin = Vector2.zero; fr.offsetMax = Vector2.zero;
        fill.GetComponent<Image>().color = new Color(0.55f, 0.45f, 0.15f);
        slider.fillRect = fr;

        GameObject handleArea = new GameObject("Handle Slide Area", typeof(RectTransform));
        handleArea.transform.SetParent(go.transform, false);
        RectTransform har = handleArea.GetComponent<RectTransform>();
        har.anchorMin = Vector2.zero; har.anchorMax = Vector2.one; har.offsetMin = new Vector2(6f, 0f); har.offsetMax = new Vector2(-6f, 0f);

        GameObject handle = new GameObject("Handle", typeof(RectTransform), typeof(Image));
        handle.transform.SetParent(handleArea.transform, false);
        RectTransform hr = handle.GetComponent<RectTransform>();
        hr.sizeDelta = new Vector2(10f, 16f);
        handle.GetComponent<Image>().color = Color.white;
        slider.handleRect = hr;
        return slider;
    }

    TMP_InputField AddSeedField(Transform parent, float width)
    {
        GameObject go = new GameObject("SeedInput", typeof(RectTransform), typeof(Image), typeof(TMP_InputField));
        go.transform.SetParent(parent, false);
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(width, 24f);
        go.GetComponent<Image>().color = new Color(0.12f, 0.12f, 0.12f);

        GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textGO.transform.SetParent(go.transform, false);
        RectTransform tr = textGO.GetComponent<RectTransform>();
        tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one; tr.offsetMin = new Vector2(5f, 1f); tr.offsetMax = new Vector2(-5f, -1f);
        TextMeshProUGUI text = textGO.GetComponent<TextMeshProUGUI>();
        text.fontSize = 11; text.color = Color.white; text.alignment = TextAlignmentOptions.Center;

        TMP_InputField input = go.GetComponent<TMP_InputField>();
        input.textComponent = text;
        input.contentType = TMP_InputField.ContentType.IntegerNumber;
        input.text = "0";
        return input;
    }

    GameObject AddButton(Transform parent, string label, float width)
    {
        GameObject go = new GameObject(label + "Button", typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(width, 24f);
        go.GetComponent<Image>().color = new Color(0.27f, 0.34f, 0.20f);

        TextMeshProUGUI text = AddText(go.transform, label, 12, width);
        RectTransform tr = text.rectTransform;
        tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one; tr.offsetMin = Vector2.zero; tr.offsetMax = Vector2.zero;
        return go;
    }
}

[DefaultExecutionOrder(-950)]
public class GroomVarianceBootstrap : MonoBehaviour
{
    private bool installed;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        GameObject go = new GameObject("GroomVarianceBootstrap");
        DontDestroyOnLoad(go);
        go.AddComponent<GroomVarianceBootstrap>();
    }

    void Update()
    {
        if (installed) return;
        ModelViewer viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer == null || viewer.groomingSliderPanelGO == null) return;
        GroomVarianceController controller = viewer.GetComponent<GroomVarianceController>();
        if (controller == null) controller = viewer.gameObject.AddComponent<GroomVarianceController>();
        controller.Init(viewer);
        installed = true;
    }
}
