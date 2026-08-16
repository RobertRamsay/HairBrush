using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Repairs the ownership handoff between POST editing and the group-root UV router.
// Clicking any control inside a GroupItem exits POST editing, so GroupPredeterminedUVController
// can immediately own ADJ/PRE switching again instead of seeing a stale POST selection flag.
[DefaultExecutionOrder(6095)]
public class GroupUVRootPostExitAuthority : MonoBehaviour
{
    private ModelViewer viewer;
    private PostAffectorManager posts;
    private FieldInfo activeIdField;
    private FieldInfo activeGroupField;
    private FieldInfo hasSelectionField;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<GroupUVRootPostExitAuthority>() != null) return;
        GameObject go = new GameObject("GroupUVRootPostExitAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<GroupUVRootPostExitAuthority>();
    }

    void Update()
    {
        Resolve();
        if (viewer == null || posts == null || EventSystem.current == null) return;

        int activeId = activeIdField != null && activeIdField.GetValue(posts) is int id ? id : -1;
        if (activeId < 0) return;

        GameObject selected = EventSystem.current.currentSelectedGameObject;
        if (selected == null || !IsInsideGroupRoot(selected.transform)) return;

        if (activeIdField != null) activeIdField.SetValue(posts, -1);
        if (activeGroupField != null) activeGroupField.SetValue(posts, -1);
        if (hasSelectionField != null) hasSelectionField.SetValue(viewer, false);
    }

    void Resolve()
    {
        if (viewer == null)
        {
            viewer = FindFirstObjectByType<ModelViewer>();
            if (viewer != null)
                hasSelectionField = typeof(ModelViewer).GetField("hasSelectionHotspot", BindingFlags.Instance | BindingFlags.NonPublic);
        }

        if (posts == null)
        {
            posts = FindFirstObjectByType<PostAffectorManager>();
            if (posts != null)
            {
                BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                activeIdField = typeof(PostAffectorManager).GetField("activeId", flags);
                activeGroupField = typeof(PostAffectorManager).GetField("activeGroup", flags);
            }
        }
    }

    static bool IsInsideGroupRoot(Transform transform)
    {
        for (Transform current = transform; current != null; current = current.parent)
        {
            if (current.name.StartsWith("PostAffector_", StringComparison.Ordinal) ||
                current.name.StartsWith("GroupClumper_", StringComparison.Ordinal))
                return false;
            if (current.name.StartsWith("GroupItem_", StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}

// POST + PRE uses a local inclusive rectangle range. The first implementation exposed that
// range as tiny integer text boxes; this authority replaces them with normal whole-number
// sliders so the range can be groomed interactively like the rest of HairBrush.
[DefaultExecutionOrder(6120)]
public class PostPredeterminedUVSliderUXAuthority : MonoBehaviour
{
    private PostPredeterminedUVAuthority authority;
    private PostPredeterminedUVUIAuthority legacyUI;
    private TextureUVRectWorkspace workspace;
    private ModelViewer viewer;

    private GameObject minRow;
    private GameObject maxRow;
    private GameObject seedRow;
    private Slider minSlider;
    private Slider maxSlider;
    private TextMeshProUGUI minValue;
    private TextMeshProUGUI maxValue;
    private TMP_InputField seedInput;
    private Transform hiddenGroupRow;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<PostPredeterminedUVSliderUXAuthority>() != null) return;
        GameObject go = new GameObject("PostPredeterminedUVSliderUXAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<PostPredeterminedUVSliderUXAuthority>();
    }

    void Update()
    {
        Resolve();
        DisableLegacyUI();

        if (authority == null || viewer == null || viewer.groomingSliderPanelGO == null)
        {
            DestroyRows();
            return;
        }

        if (!authority.TryGetActiveContext(out _, out _, out int minId, out int maxId, out int seed))
        {
            DestroyRows();
            return;
        }

        List<UVRectSaveData> rects = GetRects();
        if (rects.Count == 0)
        {
            DestroyRows();
            return;
        }

        int availableMin = rects.Min(rect => rect.id);
        int availableMax = rects.Max(rect => rect.id);
        minId = Mathf.Clamp(minId, availableMin, availableMax);
        maxId = Mathf.Clamp(maxId, availableMin, availableMax);
        if (minId > maxId)
        {
            int swap = minId;
            minId = maxId;
            maxId = swap;
        }

        Transform panel = viewer.groomingSliderPanelGO.transform;
        Transform groupRow = FindDirectOrDeep(panel, "GroupUVPredetermined_Row");
        if (groupRow != null)
        {
            hiddenGroupRow = groupRow;
            if (groupRow.gameObject.activeSelf) groupRow.gameObject.SetActive(false);
        }

        if (minRow == null || minRow.transform.parent != panel)
            BuildRows(panel);
        if (minRow == null || maxRow == null || seedRow == null) return;

        PlaceRows(panel);
        SyncSlider(minSlider, availableMin, maxId, minId);
        SyncSlider(maxSlider, minId, availableMax, maxId);
        if (minValue != null) minValue.text = minId.ToString();
        if (maxValue != null) maxValue.text = maxId.ToString();
        if (seedInput != null && !seedInput.isFocused && seedInput.text != seed.ToString())
            seedInput.SetTextWithoutNotify(seed.ToString());
    }

    void Resolve()
    {
        if (authority == null) authority = FindFirstObjectByType<PostPredeterminedUVAuthority>();
        if (workspace == null) workspace = FindFirstObjectByType<TextureUVRectWorkspace>();
        if (viewer == null) viewer = FindFirstObjectByType<ModelViewer>();
        if (legacyUI == null) legacyUI = FindFirstObjectByType<PostPredeterminedUVUIAuthority>();
    }

    void DisableLegacyUI()
    {
        if (legacyUI != null && legacyUI.enabled) legacyUI.enabled = false;
        GameObject oldRow = GameObject.Find("PostPredeterminedUV_Row");
        if (oldRow != null) Destroy(oldRow);
    }

    List<UVRectSaveData> GetRects()
    {
        if (workspace == null) workspace = FindFirstObjectByType<TextureUVRectWorkspace>();
        return workspace != null
            ? workspace.ExportDefinitions().Where(rect => rect != null).OrderBy(rect => rect.id).ToList()
            : new List<UVRectSaveData>();
    }

    void BuildRows(Transform parent)
    {
        DestroyRows(false);

        minRow = BuildSliderRow(parent, "POST PRE MIN", out minSlider, out minValue);
        maxRow = BuildSliderRow(parent, "POST PRE MAX", out maxSlider, out maxValue);
        seedRow = BuildSeedRow(parent);

        minSlider.onValueChanged.AddListener(value =>
        {
            if (authority == null) return;
            int rounded = Mathf.RoundToInt(value);
            authority.SetActiveRange(true, rounded.ToString());
            if (minValue != null) minValue.text = rounded.ToString();
        });

        maxSlider.onValueChanged.AddListener(value =>
        {
            if (authority == null) return;
            int rounded = Mathf.RoundToInt(value);
            authority.SetActiveRange(false, rounded.ToString());
            if (maxValue != null) maxValue.text = rounded.ToString();
        });
    }

    GameObject BuildSliderRow(Transform parent, string label, out Slider slider, out TextMeshProUGUI valueText)
    {
        GameObject row = new GameObject(label.Replace(' ', '_') + "_Row", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        row.transform.SetParent(parent, false);
        row.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 38f);

        HorizontalLayoutGroup layout = row.GetComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(0, 0, 3, 3);
        layout.spacing = 8f;
        layout.childControlWidth = false;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;

        TextMeshProUGUI text = AddText(row.transform, label, 12f, 112f, TextAlignmentOptions.MidlineLeft);
        text.fontStyle = FontStyles.Bold;
        slider = AddSlider(row.transform, 300f);
        valueText = AddText(row.transform, "1", 12f, 48f, TextAlignmentOptions.Center);
        return row;
    }

    GameObject BuildSeedRow(Transform parent)
    {
        GameObject row = new GameObject("POST_PRE_SEED_Row", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        row.transform.SetParent(parent, false);
        row.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 38f);

        HorizontalLayoutGroup layout = row.GetComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(0, 0, 3, 3);
        layout.spacing = 8f;
        layout.childControlWidth = false;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;

        TextMeshProUGUI label = AddText(row.transform, "POST PRE SEED", 12f, 112f, TextAlignmentOptions.MidlineLeft);
        label.fontStyle = FontStyles.Bold;
        seedInput = AddIntInput(row.transform, 280f);
        GameObject random = AddButton(row.transform, "R", 48f, 30f);
        seedInput.onEndEdit.AddListener(value => authority?.SetActiveSeed(value));
        random.GetComponent<Button>().onClick.AddListener(() => authority?.RandomizeActiveSeed());
        return row;
    }

    void PlaceRows(Transform panel)
    {
        int insert = panel.childCount - 1;
        Transform modeRow = FindDirectOrDeep(panel, "GroupUVMode_Row");
        if (modeRow != null) insert = modeRow.GetSiblingIndex() + 1;
        else if (hiddenGroupRow != null && hiddenGroupRow.parent == panel) insert = hiddenGroupRow.GetSiblingIndex();

        minRow.transform.SetSiblingIndex(Mathf.Min(insert++, panel.childCount - 1));
        maxRow.transform.SetSiblingIndex(Mathf.Min(insert++, panel.childCount - 1));
        seedRow.transform.SetSiblingIndex(Mathf.Min(insert, panel.childCount - 1));
    }

    static void SyncSlider(Slider slider, int min, int max, int value)
    {
        if (slider == null) return;
        if (max < min) max = min;
        slider.minValue = min;
        slider.maxValue = max;
        slider.wholeNumbers = true;
        float clamped = Mathf.Clamp(value, min, max);
        if (!Mathf.Approximately(slider.value, clamped)) slider.SetValueWithoutNotify(clamped);
    }

    void DestroyRows(bool restoreGroupRow = true)
    {
        if (minRow != null) Destroy(minRow);
        if (maxRow != null) Destroy(maxRow);
        if (seedRow != null) Destroy(seedRow);
        minRow = null;
        maxRow = null;
        seedRow = null;
        minSlider = null;
        maxSlider = null;
        minValue = null;
        maxValue = null;
        seedInput = null;

        if (restoreGroupRow && hiddenGroupRow != null)
            hiddenGroupRow.gameObject.SetActive(true);
        hiddenGroupRow = null;
    }

    static Slider AddSlider(Transform parent, float width)
    {
        GameObject go = new GameObject("Slider", typeof(RectTransform), typeof(Slider));
        go.transform.SetParent(parent, false);
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(width, 30f);
        Slider slider = go.GetComponent<Slider>();
        slider.wholeNumbers = true;

        GameObject background = new GameObject("Background", typeof(RectTransform), typeof(Image));
        background.transform.SetParent(go.transform, false);
        RectTransform bg = background.GetComponent<RectTransform>();
        bg.anchorMin = new Vector2(0f, .42f);
        bg.anchorMax = new Vector2(1f, .58f);
        bg.offsetMin = Vector2.zero;
        bg.offsetMax = Vector2.zero;
        background.GetComponent<Image>().color = new Color(.24f, .24f, .24f, 1f);

        GameObject fillArea = new GameObject("Fill Area", typeof(RectTransform));
        fillArea.transform.SetParent(go.transform, false);
        RectTransform fa = fillArea.GetComponent<RectTransform>();
        fa.anchorMin = new Vector2(0f, .35f);
        fa.anchorMax = new Vector2(1f, .65f);
        fa.offsetMin = new Vector2(5f, 0f);
        fa.offsetMax = new Vector2(-5f, 0f);

        GameObject fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
        fill.transform.SetParent(fillArea.transform, false);
        RectTransform fr = fill.GetComponent<RectTransform>();
        fr.anchorMin = Vector2.zero;
        fr.anchorMax = Vector2.one;
        fr.offsetMin = Vector2.zero;
        fr.offsetMax = Vector2.zero;
        fill.GetComponent<Image>().color = new Color(.28f, .58f, .95f, 1f);
        slider.fillRect = fr;

        GameObject handleArea = new GameObject("Handle Slide Area", typeof(RectTransform));
        handleArea.transform.SetParent(go.transform, false);
        RectTransform ha = handleArea.GetComponent<RectTransform>();
        ha.anchorMin = Vector2.zero;
        ha.anchorMax = Vector2.one;
        ha.offsetMin = new Vector2(5f, 0f);
        ha.offsetMax = new Vector2(-5f, 0f);

        GameObject handle = new GameObject("Handle", typeof(RectTransform), typeof(Image));
        handle.transform.SetParent(handleArea.transform, false);
        RectTransform hr = handle.GetComponent<RectTransform>();
        hr.sizeDelta = new Vector2(10f, 18f);
        handle.GetComponent<Image>().color = Color.white;
        slider.handleRect = hr;
        slider.targetGraphic = handle.GetComponent<Image>();
        return slider;
    }

    static TextMeshProUGUI AddText(Transform parent, string value, float fontSize, float width, TextAlignmentOptions alignment)
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

    static TMP_InputField AddIntInput(Transform parent, float width)
    {
        GameObject go = new GameObject("SeedInput", typeof(RectTransform), typeof(Image), typeof(TMP_InputField));
        go.transform.SetParent(parent, false);
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(width, 30f);
        go.GetComponent<Image>().color = new Color(.16f, .18f, .22f, 1f);

        GameObject textArea = new GameObject("Text Area", typeof(RectTransform), typeof(RectMask2D));
        textArea.transform.SetParent(go.transform, false);
        RectTransform area = textArea.GetComponent<RectTransform>();
        area.anchorMin = Vector2.zero;
        area.anchorMax = Vector2.one;
        area.offsetMin = new Vector2(5f, 2f);
        area.offsetMax = new Vector2(-5f, -2f);

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
        input.textViewport = area;
        input.textComponent = text;
        input.contentType = TMP_InputField.ContentType.IntegerNumber;
        input.lineType = TMP_InputField.LineType.SingleLine;
        return input;
    }

    static GameObject AddButton(Transform parent, string label, float width, float height)
    {
        GameObject go = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(width, height);
        go.GetComponent<Image>().color = new Color(.20f, .30f, .42f, 1f);

        TextMeshProUGUI text = AddText(go.transform, label, 12f, width, TextAlignmentOptions.Center);
        RectTransform rect = text.rectTransform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        text.fontStyle = FontStyles.Bold;
        return go;
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
}
