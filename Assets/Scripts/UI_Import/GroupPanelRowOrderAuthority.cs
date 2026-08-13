using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// Final authority for generated rows beneath each Hair Group header.
// Several runtime features maintain their own rows at different execution orders; without
// one final ordering pass the UV row and POST rows can repeatedly compete for the same
// sibling index and visibly jump. Keep the contract deterministic:
// Group header -> group UV row -> POST rows.
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

    void LateUpdate()
    {
        RectTransform[] all = FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (RectTransform groupItem in all)
        {
            if (groupItem == null || !groupItem.name.StartsWith("GroupItem_", StringComparison.Ordinal)) continue;
            if (!int.TryParse(groupItem.name.Substring("GroupItem_".Length), out int groupId)) continue;

            Transform parent = groupItem.parent;
            if (parent == null) continue;

            int insert = groupItem.GetSiblingIndex() + 1;

            Transform uvRow = parent.Find("GroupUV_" + groupId);
            if (uvRow != null)
                uvRow.SetSiblingIndex(Mathf.Min(insert++, parent.childCount - 1));

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
