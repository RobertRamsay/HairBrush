using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// POST affectors are downstream structural modifiers, so the group root is read-only
// while one exists. Variance remains part of the groom UI but is locked at the same time.
// CLUMPER is itself a downstream modifier and must stay editable even when POSTs exist.
[DefaultExecutionOrder(5000)]
public class ModifierCoreLock : MonoBehaviour
{
    private ModelViewer viewer;
    private PostAffectorManager postManager;
    private FieldInfo hasSelectionField;
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
        bool locked = post && !editingPost;

        // Runtime rows can be rebuilt, so always re-apply instead of caching interactable state.
        ApplyLock(locked, post);
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
        catch
        {
            return false;
        }
    }

    void ApplyLock(bool locked, bool post)
    {
        if (boundPanel == null) return;

        foreach (Slider slider in boundPanel.GetComponentsInChildren<Slider>(true))
        {
            if (slider == null || IsInsideClumper(slider.transform)) continue;
            slider.interactable = !locked;
        }

        foreach (TMP_InputField input in boundPanel.GetComponentsInChildren<TMP_InputField>(true))
        {
            if (input == null || IsInsideClumper(input.transform)) continue;
            input.interactable = !locked;
        }

        foreach (Button button in boundPanel.GetComponentsInChildren<Button>(true))
        {
            if (button == null || IsInsideClumper(button.transform) || !IsVarianceButton(button)) continue;
            button.interactable = !locked;
        }

        foreach (Transform child in boundPanel.transform)
        {
            if (child == null || child.name == "ClumperScrollHost" || child.name == "ClumperControls") continue;
            bool editableRow = child.name.EndsWith("_Row", StringComparison.Ordinal) ||
                               child.name.EndsWith("_VarianceRow", StringComparison.Ordinal);
            if (!editableRow) continue;
            CanvasGroup cg = child.GetComponent<CanvasGroup>();
            if (cg == null) cg = child.gameObject.AddComponent<CanvasGroup>();
            cg.alpha = locked ? 0.48f : 1f;
        }

        EnsureNotice();
        if (lockNotice == null) return;
        lockNotice.SetActive(locked && !IsClumperVisible());
        if (!locked || IsClumperVisible()) return;

        TextMeshProUGUI text = lockNotice.GetComponent<TextMeshProUGUI>();
        if (text != null)
            text.text = post ? "GROOM LOCKED — active: POST" : "GROOM LOCKED";
    }

    bool IsInsideClumper(Transform t)
    {
        while (t != null && t != boundPanel.transform)
        {
            if (t.name == "ClumperControls" || t.name == "ClumperScrollHost") return true;
            t = t.parent;
        }
        return false;
    }

    bool IsClumperVisible()
    {
        if (boundPanel == null) return false;
        Transform host = boundPanel.transform.Find("ClumperScrollHost");
        return host != null && host.gameObject.activeInHierarchy;
    }

    bool IsVarianceButton(Button button)
    {
        Transform t = button.transform;
        while (t != null && t != boundPanel.transform)
        {
            if (t.name.EndsWith("_VarianceRow", StringComparison.Ordinal)) return true;
            t = t.parent;
        }
        return false;
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
