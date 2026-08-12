using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Structural modifiers lock the whole groom editing surface for the active group.
// That includes variance sliders: while POST or CLUMP is active, no groom slider may change.
[DefaultExecutionOrder(5000)]
public class ModifierCoreLock : MonoBehaviour
{
    private ModelViewer viewer;
    private PostAffectorManager postManager;
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
            lockNotice = null;
            EnsureNotice();
        }

        int groupId = viewer.currentGroupId;
        bool editingPost = IsLocalizedPostEditing();
        bool post = GroupHasPost(groupId);
        bool clump = GroupHasEnabledClump(groupId);
        bool locked = (post || clump) && !editingPost;

        ApplyLock(locked, post, clump);
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
            return items != null && items.Count > 0;
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

    void ApplyLock(bool locked, bool post, bool clump)
    {
        if (boundPanel == null) return;

        // Lock every slider in the groom panel, including all VAR ± controls.
        // POST editing is the only exception because it deliberately reuses the shared groom sliders.
        foreach (Slider slider in boundPanel.GetComponentsInChildren<Slider>(true))
            if (slider != null) slider.interactable = !locked;

        // Seed fields and randomize buttons are also variation controls, so prevent them changing too.
        foreach (TMPro.TMP_InputField input in boundPanel.GetComponentsInChildren<TMPro.TMP_InputField>(true))
            if (input != null) input.interactable = !locked;

        foreach (Transform child in boundPanel.transform)
        {
            if (child == null) continue;
            bool editableRow = child.name.EndsWith("_Row", StringComparison.Ordinal) ||
                               child.name.EndsWith("_VarianceRow", StringComparison.Ordinal);
            if (!editableRow) continue;
            CanvasGroup cg = child.GetComponent<CanvasGroup>();
            if (cg == null) cg = child.gameObject.AddComponent<CanvasGroup>();
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
        if (clump) sources.Add("CLUMP");
        text.text = "GROOM LOCKED — active: " + string.Join(" + ", sources);
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
        text.text = "GROOM LOCKED";
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
