using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// The group root is EDITABLE with POSTs in place. This class no longer locks anything - it
// keeps the panel live and says what is going on.
//
// It used to make the whole groom panel read-only whenever the current group had a POST, for a
// real reason: ModelViewer's group edit path reads each card's rendered state, which for a card
// under a POST is base + that POST's contribution, and writes it back as the new base. Editing
// the root with a POST live therefore baked the POST into the base and then evaluated it again
// on top - doubling it on every channel, every slider tick. Locking the root was the only thing
// standing between the user and that.
//
// PostAffectorManager.PrepareCardForRootEdit fixes it at the source: the root edit now reads the
// base rather than base + POST, and the POSTs are re-applied over the result in the same frame.
// So the root can move and the POSTs ride on top of wherever it lands, which is what a group
// base value is supposed to mean.
//
// What is left here:
//   - a pass that keeps the rows interactable and undimmed, so no stale lock state survives a
//     panel rebuild or a project saved by a build that did lock them
//   - the notice, reworded from a refusal into an explanation
//
// Authoring a POST is unchanged: Ctrl+click into one and the panel edits that POST's delta,
// exactly as before.
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

        // Runtime rows can be rebuilt, so always re-apply instead of caching interactable state.
        // The notice is for the group root only - while a POST is being authored the panel is
        // that POST's, and saying anything about the root there would be wrong.
        ApplyLock(post && !editingPost);
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

    // Everything here now sets the SAME value every pass - live, undimmed. It is kept as a pass
    // rather than deleted because the state it is clearing is not always its own: a panel rebuilt
    // from a project saved by a locking build, or any authority that dims a row and dies before
    // putting it back, would otherwise leave a permanently dead slider with nothing to revive it.
    void ApplyLock(bool post)
    {
        if (boundPanel == null) return;

        foreach (Slider slider in boundPanel.GetComponentsInChildren<Slider>(true))
        {
            if (slider == null || IsInsideClumper(slider.transform) || IsInsideUVRouting(slider.transform)) continue;
            slider.interactable = true;
        }

        foreach (TMP_InputField input in boundPanel.GetComponentsInChildren<TMP_InputField>(true))
        {
            if (input == null || IsInsideClumper(input.transform) || IsInsideUVRouting(input.transform)) continue;
            input.interactable = true;
        }

        foreach (Button button in boundPanel.GetComponentsInChildren<Button>(true))
        {
            if (button == null || IsInsideClumper(button.transform) || IsInsideUVRouting(button.transform) || !IsVarianceButton(button)) continue;
            button.interactable = true;
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
            // in both modes, and this loop is what would put a stale dim on it back to 1.
            if (child == null || child.name == "ClumperScrollHost" || child.name == "ClumperControls" ||
                child.name == "GroupUVMode_Row" || child.name == "GroupUVPredetermined_Row" ||
                child.name == "GroupUVFlip_Row") continue;
            bool editableRow = child.name.EndsWith("_Row", StringComparison.Ordinal) ||
                               child.name.EndsWith("_VarianceRow", StringComparison.Ordinal);
            if (!editableRow) continue;
            // Only ever added, never dimmed. A CanvasGroup at alpha 1 costs nothing and is what
            // an older project's 0.48 gets corrected to.
            CanvasGroup cg = child.GetComponent<CanvasGroup>();
            if (cg == null) cg = child.gameObject.AddComponent<CanvasGroup>();
            cg.alpha = 1f;
        }

        EnsureNotice();
        if (lockNotice == null) return;

        // Shown while the group has POSTs, hidden during a clumper session - the clumper hides
        // the rest of the panel anyway, so a line about the root would be talking about controls
        // that are not on screen.
        bool show = post && !IsClumperVisible();
        lockNotice.SetActive(show);
        if (!show) return;

        TextMeshProUGUI text = lockNotice.GetComponent<TextMeshProUGUI>();
        if (text != null) text.text = "POSTS LIVE — these are the group base values, POSTs ride on top";
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
        text.text = "POSTS LIVE";
        text.fontSize = 12f;
        text.fontStyle = FontStyles.Bold;
        text.alignment = TextAlignmentOptions.Center;
        // Was amber, which is this project's "you cannot do that". Nothing is being refused
        // any more, so it reads as an ordinary panel label.
        text.color = new Color(.62f, .74f, .86f, 1f);
        text.raycastTarget = false;

        int targetIndex = Mathf.Min(2, boundPanel.transform.childCount - 1);
        lockNotice.transform.SetSiblingIndex(targetIndex);
        lockNotice.SetActive(false);
    }
}
