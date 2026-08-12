using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Simplified current-version clump UI. Clump is a normal groom parameter:
// Length -> Clump -> Bend/Twist -> Angle transform.
// Generated points define clusters; every card uses its nearest attractor.
[DefaultExecutionOrder(2500)]
public class ClumpInlineGroomController : MonoBehaviour
{
    private ModelViewer viewer;
    private ClumpLayerManager manager;
    private ModifierPersistenceBridge persistence;
    private PostClumpAffectorBridge postClump;
    private GameObject installedPanel;
    private GameObject clumpRow;
    private GameObject pointsRow;
    private Slider clumpSlider;
    private Slider pointsSlider;
    private TextMeshProUGUI clumpLabel;
    private TextMeshProUGUI pointsLabel;
    private int shownGroup = int.MinValue;
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<ClumpInlineGroomController>() != null) return;
        GameObject go = new GameObject("ClumpInlineGroomController");
        DontDestroyOnLoad(go);
        go.AddComponent<ClumpInlineGroomController>();
    }

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + .15f;

        if (viewer == null) viewer = FindFirstObjectByType<ModelViewer>();
        if (manager == null) manager = FindFirstObjectByType<ClumpLayerManager>();
        if (persistence == null) persistence = FindFirstObjectByType<ModifierPersistenceBridge>();
        if (postClump == null) postClump = FindFirstObjectByType<PostClumpAffectorBridge>();
        if (viewer == null || manager == null) return;

        if (manager.enabled) manager.enabled = false;
        RemoveLegacyClumpPanels();

        GameObject panel = viewer.groomingSliderPanelGO;
        if (panel == null) return;
        if (installedPanel != panel || clumpRow == null)
            Install(panel);

        if (shownGroup != viewer.currentGroupId)
        {
            shownGroup = viewer.currentGroupId;
            SyncFromGroup();
            ApplyGroup(shownGroup);
        }
        else if (postClump != null && postClump.ConsumeDisplayDirty())
        {
            SyncFromGroup();
            ApplyGroup(shownGroup);
        }
    }

    void RemoveLegacyClumpPanels()
    {
        foreach (RectTransform r in FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Where(r => r != null && r.name.StartsWith("ClumpModifier_")))
            Destroy(r.gameObject);
    }

    void Install(GameObject panelGO)
    {
        installedPanel = panelGO;
        shownGroup = int.MinValue;
        if (clumpRow != null) Destroy(clumpRow);
        if (pointsRow != null) Destroy(pointsRow);

        Transform panel = panelGO.transform;
        Transform widthRow = panel.Find("Width_Row");
        Transform lengthVariance = panel.Cast<Transform>().FirstOrDefault(t => t.name == "Length_VarianceRow");
        Transform lengthMain = panel.Find("Length_Row");
        Transform anchor = lengthVariance != null ? lengthVariance : lengthMain;
        int sibling = anchor != null ? anchor.GetSiblingIndex() + 1 : (widthRow != null ? widthRow.GetSiblingIndex() : 0);

        clumpRow = BuildNativeSliderRow(panel, widthRow, out clumpSlider, out clumpLabel);
        clumpRow.transform.SetSiblingIndex(sibling++);
        clumpSlider.onValueChanged.AddListener(v =>
        {
            float value = Mathf.Clamp01(v);
            if (postClump != null && postClump.TryAuthorActive(value))
            {
                clumpLabel.text = "CLUMP: " + value.ToString("F3");
                ApplyGroup(viewer.currentGroupId);
                return;
            }

            ClumpLayerManager.ClumpLayer layer = GetLayer(viewer.currentGroupId);
            if (layer == null) return;
            layer.globalStrength = value;
            layer.enabled = value > .0001f;
            if (layer.enabled && layer.points.Count == 0 && layer.pointCount > 0)
                RegenerateCurrent(false);
            ApplyGroup(viewer.currentGroupId);
            clumpLabel.text = "CLUMP: " + value.ToString("F3");
        });

        pointsRow = BuildPointsRow(panel);
        pointsRow.transform.SetSiblingIndex(sibling);
        LayoutRebuilder.ForceRebuildLayoutImmediate(panel as RectTransform);
        SyncFromGroup();
    }

    GameObject BuildNativeSliderRow(Transform parent, Transform templateRow, out Slider slider, out TextMeshProUGUI label)
    {
        GameObject row;
        if (templateRow != null)
        {
            row = Instantiate(templateRow.gameObject, parent, false);
            row.name = "Clump_Row";
            slider = row.GetComponentInChildren<Slider>(true);
            label = row.GetComponentInChildren<TextMeshProUGUI>(true);
            if (slider != null)
            {
                slider.onValueChanged.RemoveAllListeners();
                slider.minValue = 0f;
                slider.maxValue = 1f;
                slider.wholeNumbers = false;
                slider.SetValueWithoutNotify(0f);
                if (slider.handleRect != null)
                {
                    // Match the compact native groom handle rather than the old oversized custom one.
                    RectTransform templateHandle = templateRow.GetComponentInChildren<Slider>(true)?.handleRect;
                    if (templateHandle != null) slider.handleRect.sizeDelta = templateHandle.sizeDelta;
                }
            }
            if (label != null) label.text = "CLUMP: 0.000";
            return row;
        }

        row = new GameObject("Clump_Row", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(LayoutElement));
        row.transform.SetParent(parent, false);
        row.GetComponent<LayoutElement>().preferredHeight = 42f;
        VerticalLayoutGroup layout = row.GetComponent<VerticalLayoutGroup>();
        layout.spacing = 1f; layout.childControlWidth = true; layout.childControlHeight = false;
        label = MakeText(row.transform, "CLUMP: 0.000", 11, 16f, TextAlignmentOptions.Left);
        slider = MakeSlider(row.transform, 0f, 1f, 0f, 16f);
        if (slider.handleRect != null) slider.handleRect.sizeDelta = new Vector2(6f, 10f);
        return row;
    }

    GameObject BuildPointsRow(Transform parent)
    {
        GameObject row = new GameObject("ClumpPoints_Row", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        row.transform.SetParent(parent, false);
        row.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 27f);
        row.GetComponent<LayoutElement>().preferredHeight = 27f;
        HorizontalLayoutGroup h = row.GetComponent<HorizontalLayoutGroup>();
        h.spacing = 5f; h.padding = new RectOffset(2, 2, 1, 1); h.childControlWidth = false; h.childControlHeight = true; h.childForceExpandWidth = false;

        pointsLabel = MakeText(row.transform, "POINTS 20", 10, 62f, TextAlignmentOptions.MidlineLeft);
        pointsSlider = MakeSlider(row.transform, 1f, 100f, 20f, 22f, 175f);
        pointsSlider.wholeNumbers = true;
        if (pointsSlider.handleRect != null) pointsSlider.handleRect.sizeDelta = new Vector2(6f, 10f);
        pointsSlider.onValueChanged.AddListener(v =>
        {
            ClumpLayerManager.ClumpLayer layer = GetLayer(viewer.currentGroupId);
            if (layer == null) return;
            layer.pointCount = Mathf.RoundToInt(v);
            pointsLabel.text = "POINTS " + layer.pointCount;
        });

        MakeButton(row.transform, "REGEN", 58f, () => RegenerateCurrent(false));
        MakeButton(row.transform, "R", 28f, () => RegenerateCurrent(true));
        return row;
    }

    void RegenerateCurrent(bool randomSeed)
    {
        int groupId = viewer.currentGroupId;
        ClumpLayerManager.ClumpLayer layer = GetLayer(groupId);
        if (layer == null) return;
        if (randomSeed && persistence != null) persistence.SetClumpSeed(groupId, Random.Range(0, 1000000));
        if (persistence != null) persistence.RegenerateSeeded(groupId);
        else
        {
            MethodInfo regen = typeof(ClumpLayerManager).GetMethod("Regenerate", BindingFlags.Instance | BindingFlags.NonPublic);
            regen?.Invoke(manager, new object[] { layer });
        }
        ApplyGroup(groupId);
    }

    void SyncFromGroup()
    {
        if (viewer == null || clumpSlider == null || pointsSlider == null) return;
        ClumpLayerManager.ClumpLayer layer = GetLayer(viewer.currentGroupId);
        if (layer == null) return;
        float baseWeight = layer.enabled ? Mathf.Clamp01(layer.globalStrength) : 0f;
        float displayWeight = postClump != null ? postClump.GetDisplayedWeight(viewer.currentGroupId, baseWeight) : baseWeight;
        clumpSlider.SetValueWithoutNotify(displayWeight);
        pointsSlider.SetValueWithoutNotify(Mathf.Clamp(layer.pointCount, 1, 100));
        clumpLabel.text = "CLUMP: " + displayWeight.ToString("F3");
        pointsLabel.text = "POINTS " + Mathf.Clamp(layer.pointCount, 1, 100);
    }

    ClumpLayerManager.ClumpLayer GetLayer(int groupId)
    {
        if (manager == null) return null;
        MethodInfo get = typeof(ClumpLayerManager).GetMethod("GetOrCreateLayer", BindingFlags.Instance | BindingFlags.NonPublic);
        return get?.Invoke(manager, new object[] { groupId }) as ClumpLayerManager.ClumpLayer;
    }

    public float GetBaseGroupWeight(int groupId)
    {
        ClumpLayerManager.ClumpLayer layer = GetLayer(groupId);
        return layer != null && layer.enabled ? Mathf.Clamp01(layer.globalStrength) : 0f;
    }

    public void ApplyGroup(int groupId)
    {
        ClumpLayerManager.ClumpLayer layer = GetLayer(groupId);
        if (layer == null) return;
        HairCard[] cards = FindObjectsByType<HairCard>(FindObjectsSortMode.None).Where(c => c.groupId == groupId).ToArray();
        float baseWeight = layer.enabled ? Mathf.Clamp01(layer.globalStrength) : 0f;

        if (layer.points.Count == 0)
        {
            foreach (HairCard card in cards) card.ClearClumpModifier();
            return;
        }

        foreach (HairCard card in cards)
        {
            float weight = postClump != null ? postClump.EvaluateWeight(card, baseWeight) : baseWeight;
            if (weight <= .0001f) { card.ClearClumpModifier(); continue; }
            Vector3 root = card.GetSpawnHitPoint();
            ClumpLayerManager.ClumpPoint nearest = layer.points.OrderBy(p => Vector3.SqrMagnitude(root - p.position)).First();
            card.SetClumpModifier(nearest.position, nearest.normal, weight, layer.curve);
        }
    }

    TextMeshProUGUI MakeText(Transform parent, string value, int size, float heightOrWidth, TextAlignmentOptions alignment)
    {
        GameObject go = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        LayoutElement le = go.GetComponent<LayoutElement>(); le.preferredWidth = heightOrWidth; le.preferredHeight = 16f;
        TextMeshProUGUI t = go.GetComponent<TextMeshProUGUI>(); t.text = value; t.fontSize = size; t.color = Color.white; t.alignment = alignment; t.raycastTarget = false;
        return t;
    }

    Slider MakeSlider(Transform parent, float min, float max, float value, float height, float width = -1f)
    {
        GameObject go = new GameObject("Slider", typeof(RectTransform), typeof(Slider), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        LayoutElement le = go.GetComponent<LayoutElement>(); le.preferredHeight = height; if (width > 0f) le.preferredWidth = width; else le.flexibleWidth = 1f;
        Slider s = go.GetComponent<Slider>(); s.minValue = min; s.maxValue = max; s.value = value;
        GameObject bg = new GameObject("Background", typeof(RectTransform), typeof(Image)); bg.transform.SetParent(go.transform, false);
        RectTransform br = bg.GetComponent<RectTransform>(); br.anchorMin = new Vector2(0f,.43f); br.anchorMax = new Vector2(1f,.57f); br.offsetMin = Vector2.zero; br.offsetMax = Vector2.zero; bg.GetComponent<Image>().color = new Color(.28f,.28f,.28f);
        GameObject fa = new GameObject("Fill Area", typeof(RectTransform)); fa.transform.SetParent(go.transform, false);
        RectTransform far = fa.GetComponent<RectTransform>(); far.anchorMin = new Vector2(0f,.38f); far.anchorMax = new Vector2(1f,.62f); far.offsetMin = new Vector2(4f,0f); far.offsetMax = new Vector2(-4f,0f);
        GameObject fill = new GameObject("Fill", typeof(RectTransform), typeof(Image)); fill.transform.SetParent(fa.transform, false);
        RectTransform fr = fill.GetComponent<RectTransform>(); fr.anchorMin = Vector2.zero; fr.anchorMax = Vector2.one; fr.offsetMin = Vector2.zero; fr.offsetMax = Vector2.zero; fill.GetComponent<Image>().color = new Color(.2f,.7f,.3f); s.fillRect = fr;
        GameObject ha = new GameObject("Handle Slide Area", typeof(RectTransform)); ha.transform.SetParent(go.transform, false);
        RectTransform har = ha.GetComponent<RectTransform>(); har.anchorMin = Vector2.zero; har.anchorMax = Vector2.one; har.offsetMin = new Vector2(5f,0f); har.offsetMax = new Vector2(-5f,0f);
        GameObject hg = new GameObject("Handle", typeof(RectTransform), typeof(Image)); hg.transform.SetParent(ha.transform, false);
        RectTransform hr = hg.GetComponent<RectTransform>(); hr.sizeDelta = new Vector2(6f,10f); hg.GetComponent<Image>().color = Color.white; s.handleRect = hr;
        return s;
    }

    void MakeButton(Transform parent, string label, float width, UnityEngine.Events.UnityAction action)
    {
        GameObject go = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        LayoutElement le = go.GetComponent<LayoutElement>(); le.preferredWidth = width; le.preferredHeight = 24f;
        go.GetComponent<Image>().color = new Color(.18f,.30f,.20f); go.GetComponent<Button>().onClick.AddListener(action);
        TextMeshProUGUI t = MakeText(go.transform, label, 10, width, TextAlignmentOptions.Center);
        RectTransform tr = t.rectTransform; tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one; tr.offsetMin = Vector2.zero; tr.offsetMax = Vector2.zero;
    }
}
