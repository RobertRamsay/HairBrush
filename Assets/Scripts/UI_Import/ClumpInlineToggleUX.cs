using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Adds an explicit ON/OFF control to the compact inline clump row.
// This is the escape hatch from a CLUMP-locked groom: disabling clump preserves
// its settings/points, removes its effect, and lets the root groom become editable.
[DefaultExecutionOrder(4700)]
public class ClumpInlineToggleUX : MonoBehaviour
{
    private ModelViewer viewer;
    private ClumpLayerManager manager;
    private ClumpInlineGroomController inlineClump;
    private ModifierPersistenceBridge persistence;
    private GameObject boundPanel;
    private Button toggleButton;
    private TextMeshProUGUI toggleText;
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<ClumpInlineToggleUX>() != null) return;
        GameObject go = new GameObject("ClumpInlineToggleUX");
        DontDestroyOnLoad(go);
        go.AddComponent<ClumpInlineToggleUX>();
    }

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + 0.08f;

        ResolveReferences();
        if (viewer == null || manager == null || viewer.groomingSliderPanelGO == null) return;

        if (boundPanel != viewer.groomingSliderPanelGO)
        {
            boundPanel = viewer.groomingSliderPanelGO;
            toggleButton = null;
            toggleText = null;
        }

        EnsureToggle();
        SyncToggle();
    }

    void ResolveReferences()
    {
        if (viewer == null) viewer = FindFirstObjectByType<ModelViewer>();
        if (manager == null) manager = FindFirstObjectByType<ClumpLayerManager>();
        if (inlineClump == null) inlineClump = FindFirstObjectByType<ClumpInlineGroomController>();
        if (persistence == null) persistence = FindFirstObjectByType<ModifierPersistenceBridge>();
    }

    void EnsureToggle()
    {
        if (boundPanel == null) return;
        Transform row = boundPanel.transform.Find("ClumpPoints_Row");
        if (row == null) return;

        Transform existing = row.Find("ClumpToggleButton");
        if (existing != null)
        {
            toggleButton = existing.GetComponent<Button>();
            toggleText = existing.GetComponentInChildren<TextMeshProUGUI>(true);
            return;
        }

        GameObject go = new GameObject("ClumpToggleButton", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        go.transform.SetParent(row, false);
        RectTransform rt = go.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(44f, 24f);
        LayoutElement le = go.GetComponent<LayoutElement>();
        le.preferredWidth = 44f;
        le.minWidth = 44f;
        le.flexibleWidth = 0f;
        le.preferredHeight = 24f;

        toggleButton = go.GetComponent<Button>();
        toggleButton.onClick.AddListener(ToggleCurrentGroup);

        GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textGO.transform.SetParent(go.transform, false);
        RectTransform tr = textGO.GetComponent<RectTransform>();
        tr.anchorMin = Vector2.zero;
        tr.anchorMax = Vector2.one;
        tr.offsetMin = Vector2.zero;
        tr.offsetMax = Vector2.zero;
        toggleText = textGO.GetComponent<TextMeshProUGUI>();
        toggleText.fontSize = 10f;
        toggleText.fontStyle = FontStyles.Bold;
        toggleText.alignment = TextAlignmentOptions.Center;
        toggleText.color = Color.white;
        toggleText.raycastTarget = false;

        Transform regen = row.Cast<Transform>().FirstOrDefault(t => t.name == "REGEN");
        if (regen != null) go.transform.SetSiblingIndex(regen.GetSiblingIndex());
    }

    void ToggleCurrentGroup()
    {
        if (viewer == null || manager == null) return;
        ClumpLayerManager.ClumpLayer layer = GetLayer(viewer.currentGroupId);
        if (layer == null) return;

        layer.enabled = !layer.enabled;
        if (layer.enabled && layer.points.Count == 0 && layer.pointCount > 0)
        {
            if (persistence != null)
                persistence.RegenerateSeeded(viewer.currentGroupId);
            else
            {
                MethodInfo regen = typeof(ClumpLayerManager).GetMethod("Regenerate", BindingFlags.Instance | BindingFlags.NonPublic);
                regen?.Invoke(manager, new object[] { layer });
            }
        }

        if (inlineClump != null)
            inlineClump.ApplyGroup(viewer.currentGroupId);
        else
        {
            MethodInfo apply = typeof(ClumpLayerManager).GetMethod("ApplyLayer", BindingFlags.Instance | BindingFlags.NonPublic);
            apply?.Invoke(manager, new object[] { layer });
        }

        SyncToggle();
    }

    void SyncToggle()
    {
        if (toggleButton == null || toggleText == null || viewer == null) return;
        ClumpLayerManager.ClumpLayer layer = GetLayer(viewer.currentGroupId);
        bool enabled = layer != null && layer.enabled;
        toggleText.text = enabled ? "ON" : "OFF";
        Image image = toggleButton.GetComponent<Image>();
        if (image != null)
            image.color = enabled ? new Color(.18f, .48f, .22f) : new Color(.28f, .28f, .28f);

        // The toggle itself must always remain usable so a CLUMP lock can be released.
        toggleButton.interactable = true;
    }

    ClumpLayerManager.ClumpLayer GetLayer(int groupId)
    {
        if (manager == null) return null;
        MethodInfo get = typeof(ClumpLayerManager).GetMethod("GetOrCreateLayer", BindingFlags.Instance | BindingFlags.NonPublic);
        return get?.Invoke(manager, new object[] { groupId }) as ClumpLayerManager.ClumpLayer;
    }
}
