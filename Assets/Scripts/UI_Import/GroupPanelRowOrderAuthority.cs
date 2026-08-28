using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// Final authority for generated POST rows beneath each Hair Group header.
// Group-owned UV controls now live in the right grooming/modifier panel, so the left
// panel is navigation only: Group header -> POST rows.
[DefaultExecutionOrder(10000)]
public class GroupPanelRowOrderAuthority : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<GroupPanelRowOrderAuthority>() != null) return;
        GameObject go = new GameObject("GroupPanelRowOrderAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<GroupPanelRowOrderAuthority>();
    }

    // Throttle plus invalidation, rather than a plain interval.
    //
    // This used to run a full inactive-inclusive RectTransform scan EVERY frame and then a
    // name.StartsWith on every result. Transform.name is a native marshal that allocates a
    // fresh string per element per call, so the string garbage alone was the dominant cost -
    // and in a panel-heavy tool the inactive population is the larger one.
    //
    // A plain interval on its own would be wrong: row ordering is only ever WRONG in the
    // instant after a POST row is created, which is a user action, so a newly added row would
    // visibly jump into place up to a tenth of a second later, right where the user is
    // looking. Watching the panel's childCount is a single integer compare and catches
    // creation and deletion immediately, so the throttle only applies to the steady state,
    // where nothing has moved and there is nothing to reorder.
    //
    // Known gaps, all bounded by the interval below: a create and a delete in the SAME frame
    // net out to an unchanged count, and a rename that changes a row's prefix is invisible.
    // Both simply wait out the throttle.
    private const float ScanInterval = .1f;
    private int handledRebuildFrame = -1;
    private float nextScan;
    private Transform watchedPanel;
    private int lastPanelChildCount = -1;

    void LateUpdate()
    {
        bool structureChanged = false;
        if (watchedPanel != null && watchedPanel.childCount != lastPanelChildCount)
        {
            lastPanelChildCount = watchedPanel.childCount;
            structureChanged = true;
        }

        bool rebuilt = RuntimeUIRebuildSignal.TryConsume(ref handledRebuildFrame);
        if (!rebuilt && !structureChanged && Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + ScanInterval;

        RectTransform[] all = FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (RectTransform groupItem in all)
        {
            if (groupItem == null || !groupItem.name.StartsWith("GroupItem_", StringComparison.Ordinal)) continue;
            if (!int.TryParse(groupItem.name.Substring("GroupItem_".Length), out int groupId)) continue;

            Transform parent = groupItem.parent;
            if (parent == null) continue;

            // Latch the container the rows live in, so the childCount check above can notice a
            // POST row appear or disappear without needing another full scan to find out.
            watchedPanel = parent;
            lastPanelChildCount = parent.childCount;

            int insert = groupItem.GetSiblingIndex() + 1;
            string postPrefix = "PostAffector_" + groupId + "_";
            List<Transform> postRows = parent.Cast<Transform>()
                .Where(t => t != null && t.name.StartsWith(postPrefix, StringComparison.Ordinal))
                .OrderBy(t => ParsePostId(t.name, postPrefix))
                .ToList();

            foreach (Transform postRow in postRows)
                postRow.SetSiblingIndex(Mathf.Min(insert++, parent.childCount - 1));
        }
    }

    static int ParsePostId(string rowName, string prefix)
    {
        if (rowName != null && rowName.Length > prefix.Length &&
            int.TryParse(rowName.Substring(prefix.Length), out int id))
            return id;
        return int.MaxValue;
    }
}
