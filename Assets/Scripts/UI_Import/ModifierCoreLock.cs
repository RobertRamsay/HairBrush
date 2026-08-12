using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// The authored group core is deliberately immutable once downstream modifiers exist.
// Lock state is derived from live modifier activity and the UI explains the active source.
[DefaultExecutionOrder(5000)]
public class ModifierCoreLock : MonoBehaviour
{
    private static readonly string[] CoreRowNames =
    {
        "Length_Row", "Width_Row", "Segments_Row", "Bend Angle_Row", "Twist Angle_Row",
        "Embed Depth_Row", "Offset X_Row", "Offset Y_Row", "Offset Z_Row",
        "Angle X_Row", "Angle Y_Row", "Angle Z_Row", "U Scale_Row", "V Scale_Row",
        "U Offset_Row", "V Offset_Row"
    };

    private ModelViewer viewer;
    private PostAffectorManager postManager;
    private GroomVarianceController varianceManager;
    private ClumpLayerManager clumpManager;
    private FieldInfo hasSelectionField;
    private FieldInfo clumpLayersField;
    private GameObject boundPanel;
    private GameObject lockNotice;
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<ModifierCoreLock>() != null) return;
        GameObject go = new GameObject("ModifierCoreLock");
        DontDestroyOnLoad(go);
        go.AddComponent<ModifierCoreLock>();
    }

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + 0.08f;

        ResolveReferences();
        if (viewer == null || viewer.groomingSliderPanelGO == null) return;

        if (boundPanel != viewer.groomingSliderPanelGO)
        {
            boundPanel = viewer.groomingSliderPanelGO;
            EnsureNotice();
        }

        int groupId = viewer.currentGroupId;
        bool editingPost = IsLocalizedPostEditing();
        bool post = GroupHasPost(groupId);
        bool variance = GroupHasVariance(groupId);
        bool clump = GroupHasEnabledClump(groupId);
        bool locked = (post || variance || clump) && !editingPost;

        ApplyLock(locked, post, variance, clump);
    }

    void ResolveReferences()
    {
        if (viewer == null)
        {
            viewer = FindFirstObjectByType<ModelViewer>();
            if (viewer != null)
                hasSelectionField = typeof(ModelViewer).GetField("hasSelectionHotspot", BindingFlags.Instance | BindingFlags.NonPublic);
        }
        if (postManager == null) postManager = FindFirstObjectByType<PostAffectorManager>();
        if (varianceManager == null) varianceManager = FindFirstObjectByType<GroomVarianceController>();
        if (clumpManager == null)
        {
            clumpManager = FindFirstObjectByType<ClumpLayerManager>();
            if (clumpManager != null)
                clumpLayersField = typeof(ClumpLayerManager).GetField("layers", BindingFlags.Instance | BindingFlags.NonPublic);
        }
    }

    bool IsLocalizedPostEditing()
    {
        return viewer != null && hasSelectionField != null && hasSelectionField.GetValue(viewer) is bool active && active;
    }

    bool GroupHasPost(int groupId)
    {
        if (postManager == null) return false;
        try
        {
            List<PostAffectorSaveData> items = postManager.ExportGroup(groupId);
            if (items == null || items.Count == 0) return false;
            RectTransform[] rows = FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            return rows.Any(r => r != null && r.name.StartsWith("PostAffector_" + groupId + "_", StringComparison.Ordinal));
        }
        catch { return false; }
    }

    bool GroupHasVariance(int groupId)
    {
        if (varianceManager == null) return false;
        try
        {
            // For the currently visible group, trust the live variance sliders first. This
            // avoids stale stored amounts keeping the core locked after UI-side reset/removal.
            if (viewer != null && viewer.currentGroupId == groupId && boundPanel != null)
            {
                Slider[] live = boundPanel.GetComponentsInChildren<Slider>(true)
                    .Where(s => s != null && s.gameObject.name == "VarianceSlider")
                    .ToArray();
                if (live.Length > 0)
                    return live.Any(s => Mathf.Abs(s.value) > 0.000001f);
            }

            List<VarianceChannelSaveData> settings = varianceManager.ExportGroupSettings(groupId);
            return settings != null && settings.Any(s => s != null && Mathf.Abs(s.amount) > 0.000001f);
        }
        catch { return false; }
    }

    bool GroupHasEnabledClump(int groupId)
    {
        if (clumpManager == null || clumpLayersField == null) return false;
        try
        {
            object raw = clumpLayersField.GetValue(clumpManager);
            if (raw is IDictionary dictionary && dictionary.Contains(groupId))
            {
                object layer = dictionary[groupId];
                if (layer == null) return false;
                FieldInfo enabledField = layer.GetType().GetField("enabled", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return enabledField != null && enabledField.GetValue(layer) is bool enabled && enabled;
            }
        }
        catch { }
        return false;
    }

    void ApplyLock(bool locked, bool post, bool variance, bool clump)
    {
        if (boundPanel == null) return;

        foreach (string rowName in CoreRowNames)
        {
            Transform row = boundPanel.transform.Find(rowName);
            if (row == null) continue;
            foreach (Slider slider in row.GetComponentsInChildren<Slider>(true))
                slider.interactable = !locked;

            CanvasGroup cg = row.GetComponent<CanvasGroup>();
            if (cg == null) cg = row.gameObject.AddComponent<CanvasGroup>();
            cg.alpha = locked ? 0.48f : 1f;
        }

        EnsureNotice();
        if (lockNotice == null) return;
        lockNotice.SetActive(locked);
        if (!locked) return;

        TextMeshProUGUI text = lockNotice.GetComponent<TextMeshProUGUI>();
        if (text == null) return;
        List<string> sources = new();
        if (post) sources.Add("POST");
        if (variance) sources.Add("VARIANCE");
        if (clump) sources.Add("CLUMP");
        text.text = "CORE LOCKED — active: " + string.Join(" + ", sources);
    }

    void EnsureNotice()
    {
        if (boundPanel == null) return;
        if (lockNotice != null && lockNotice.transform.parent == boundPanel.transform) return;

        Transform existing = boundPanel.transform.Find("CoreLockedNotice");
        if (existing != null)
        {
            lockNotice = existing.gameObject;
            return;
        }

        lockNotice = new GameObject("CoreLockedNotice", typeof(RectTransform), typeof(LayoutElement), typeof(TextMeshProUGUI));
        lockNotice.transform.SetParent(boundPanel.transform, false);
        RectTransform rt = lockNotice.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(0f, 34f);
        LayoutElement le = lockNotice.GetComponent<LayoutElement>();
        le.preferredHeight = 34f;
        le.minHeight = 34f;

        TextMeshProUGUI text = lockNotice.GetComponent<TextMeshProUGUI>();
        text.text = "CORE LOCKED";
        text.fontSize = 13f;
        text.fontStyle = FontStyles.Bold;
        text.alignment = TextAlignmentOptions.Center;
        text.color = new Color(1f, 0.72f, 0.28f, 1f);
        text.raycastTarget = false;

        int targetIndex = Mathf.Min(2, boundPanel.transform.childCount - 1);
        lockNotice.transform.SetSiblingIndex(targetIndex);
        lockNotice.SetActive(false);
    }
}
