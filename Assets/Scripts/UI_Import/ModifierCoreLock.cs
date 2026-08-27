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
// Predetermined UV routing is group metadata, not groom geometry, so its controls also
// stay editable at the group root and must not oscillate against this lock.
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
            if (slider == null || IsInsideClumper(slider.transform) || IsInsideUVRouting(slider.transform)) continue;
            slider.interactable = !locked;
        }

        foreach (TMP_InputField input in boundPanel.GetComponentsInChildren<TMP_InputField>(true))
        {
            if (input == null || IsInsideClumper(input.transform) || IsInsideUVRouting(input.transform)) continue;
            input.interactable = !locked;
        }

        foreach (Button button in boundPanel.GetComponentsInChildren<Button>(true))
        {
            if (button == null || IsInsideClumper(button.transform) || IsInsideUVRouting(button.transform) || !IsVarianceButton(button)) continue;
            button.interactable = !locked;
        }

        foreach (Transform child in boundPanel.transform)
        {
            // GroupUVFlip_Row sets its own alpha instead of taking this one, because it needs a
            // different answer in each of the two UV modes and this loop only has one to give.
            // In PREDETERMINED that button is UV routing and stays live and bright like the rows
            // either side of it; in ADJUSTABLE it negates V Scale, which is groom geometry, so it
            // locks and dims with the sliders. GroupPredeterminedUVController.MaintainFlipRow
            // reads the ADJUSTABLE half of that straight off the V Scale slider the loop above
            // has just set, and applies the matching alpha - so the two agree without either
            // having to work the other's answer out again.
            //
            // Note it is NOT on IsInsideUVRouting, and must not be: that list exempts a control
            // in both modes, which would leave the ADJUSTABLE flip editable under the lock.
            if (child == null || child.name == "ClumperScrollHost" || child.name == "ClumperControls" ||
                child.name == "GroupUVMode_Row" || child.name == "GroupUVPredetermined_Row" ||
                child.name == "GroupUVFlip_Row") continue;
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

    bool IsInsideUVRouting(Transform t)
    {
        while (t != null && t != boundPanel.transform)
        {
            if (t.name == "GroupUVMode_Row" || t.name == "GroupUVPredetermined_Row" || t.name == "UVRectRangeSlider")
                return true;
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
