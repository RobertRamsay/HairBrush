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
        if (viewer == null || manager == null) return;

        // The old manager Update only maintained the large left-panel editor and guide refresh.
        // Disable that UI lifecycle; direct methods still work for save/load and regeneration.
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
        Transform lengthVariance = panel.Cast<Transform>().FirstOrDefault(t => t.name == "Length_VarianceRow");
        Transform lengthMain = panel.Find("Length_Row");
        Transform anchor = lengthVariance != null ? lengthVariance : lengthMain;
        int sibling = anchor != null ? anchor.GetSiblingIndex() + 1 : 0;

        clumpRow = BuildSliderRow(panel, "Clump_Row", "CLUMP", 0f, 1f, .0f, out clumpSlider, out clumpLabel);
        clumpRow.transform.SetSiblingIndex(sibling++);
        clumpSlider.onValueChanged.AddListener(v =>
        {
            ClumpLayerManager.ClumpLayer layer = GetLayer(viewer.currentGroupId);
            if (layer == null) return;
            layer.globalStrength = Mathf.Clamp01(v);
            layer.enabled = v > .0001f;
            if (layer.enabled && layer.points.Count == 0 && layer.pointCount > 0)
                RegenerateCurrent(false);
            ApplyGroup(viewer.currentGroupId);
            clumpLabel.text = "CLUMP: " + v.ToString("F2");
        });

        pointsRow = BuildPointsRow(panel);
        pointsRow.transform.SetSiblingIndex(sibling);
        SyncFromGroup();
    }

    GameObject BuildSliderRow(Transform parent, string name, string label, float min, float max, float value, out Slider slider, out TextMeshProUGUI text)
    {
        GameObject row = new GameObject(name, typeof(RectTransform), typeof(VerticalLayoutGroup));
        row.transform.SetParent(parent, false);
        row.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 38f);
        VerticalLayoutGroup layout = row.GetComponent<VerticalLayoutGroup>();
        layout.spacing = 1f;
        layout.childControlWidth = true;
        layout.childControlHeight = false;
        layout.childForceExpandHeight = false;

        text = MakeText(row.transform, label + ": " + value.ToString("F2"), 11, 16f, TextAlignmentOptions.Left);
        slider = MakeSlider(row.transform, min, max, value, 17f);
        return row;
    }

    GameObject BuildPointsRow(Transform parent)
    {
        GameObject row = new GameObject("ClumpPoints_Row", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        row.transform.SetParent(parent, false);
        row.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 27f);
        HorizontalLayoutGroup h = row.GetComponent<HorizontalLayoutGroup>();
        h.spacing = 5f;
        h.padding = new RectOffset(2, 2, 1, 1);
        h.childControlWidth = false;
        h.childControlHeight = true;
        h.childForceExpandWidth = false;

        pointsLabel = MakeText(row.transform, "POINTS 20", 10, 62f, TextAlignmentOptions.MidlineLeft);
        pointsSlider = MakeSlider(row.transform, 1f, 100f, 20f, 22f, 175f);
        pointsSlider.wholeNumbers = true;
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

        if (randomSeed && persistence != null)
            persistence.SetClumpSeed(groupId, Random.Range(0, 1000000));

        if (persistence != null)
            persistence.RegenerateSeeded(groupId);
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
        float weight = layer.enabled ? Mathf.Clamp01(layer.globalStrength) : 0f;
        clumpSlider.SetValueWithoutNotify(weight);
        pointsSlider.SetValueWithoutNotify(Mathf.Clamp(layer.pointCount, 1, 100));
        clumpLabel.text = "CLUMP: " + weight.ToString("F2");
        pointsLabel.text = "POINTS " + Mathf.Clamp(layer.pointCount, 1, 100);
    }

    ClumpLayerManager.ClumpLayer GetLayer(int groupId)
    {
        if (manager == null) return null;
        MethodInfo get = typeof(ClumpLayerManager).GetMethod("GetOrCreateLayer", BindingFlags.Instance | BindingFlags.NonPublic);
        return get?.Invoke(manager, new object[] { groupId }) as ClumpLayerManager.ClumpLayer;
    }

    public void ApplyGroup(int groupId)
    {
        ClumpLayerManager.ClumpLayer layer = GetLayer(groupId);
        if (layer == null) return;
        HairCard[] cards = FindObjectsByType<HairCard>(FindObjectsSortMode.None).Where(c => c.groupId == groupId).ToArray();
        float weight = layer.enabled ? Mathf.Clamp01(layer.globalStrength) : 0f;

        if (weight <= .0001f || layer.points.Count == 0)
        {
            foreach (HairCard card in cards) card.ClearClumpModifier();
            return;
        }

        foreach (HairCard card in cards)
        {
            Vector3 root = card.GetSpawnHitPoint();
            ClumpLayerManager.ClumpPoint nearest = layer.points
                .OrderBy(p => Vector3.SqrMagnitude(root - p.position))
                .First();
            card.SetClumpModifier(nearest.position, nearest.normal, weight, layer.curve);
        }
    }

    TextMeshProUGUI MakeText(Transform parent, string value, int size, float heightOrWidth, TextAlignmentOptions alignment)
    {
        GameObject go = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        LayoutElement le = go.GetComponent<LayoutElement>();
        le.preferredWidth = heightOrWidth;
        le.preferredHeight = 16f;
        TextMeshProUGUI t = go.GetComponent<TextMeshProUGUI>();
        t.text = value; t.fontSize = size; t.color = Color.white; t.alignment = alignment; t.raycastTarget = false;
        return t;
    }

    Slider MakeSlider(Transform parent, float min, float max, float value, float height, float width = -1f)
    {
        GameObject go = new GameObject("Slider", typeof(RectTransform), typeof(Slider), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        LayoutElement le = go.GetComponent<LayoutElement>();
        le.preferredHeight = height;
        if (width > 0f) le.preferredWidth = width; else le.flexibleWidth = 1f;
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
        RectTransform hr = hg.GetComponent<RectTransform>(); hr.sizeDelta = new Vector2(7f,11f); hg.GetComponent<Image>().color = Color.white; s.handleRect = hr;
        return s;
    }

    void MakeButton(Transform parent, string label, float width, UnityEngine.Events.UnityAction action)
    {
        GameObject go = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        LayoutElement le = go.GetComponent<LayoutElement>(); le.preferredWidth = width; le.preferredHeight = 24f;
        go.GetComponent<Image>().color = new Color(.18f,.30f,.20f);
        go.GetComponent<Button>().onClick.AddListener(action);
        TextMeshProUGUI t = MakeText(go.transform, label, 10, width, TextAlignmentOptions.Center);
        RectTransform tr = t.rectTransform; tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one; tr.offsetMin = Vector2.zero; tr.offsetMax = Vector2.zero;
    }
}
