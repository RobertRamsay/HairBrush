using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

// CLUMPER data belongs to the lifetime of its Hair Group. A group ID may be reused after
// deletion, so both edit selection AND the stored clumper records must be purged when a
// live group disappears. Otherwise a newly-created Group 0 can inherit deleted Group 0's
// clump points/settings simply because the numeric ID was recycled.
[DefaultExecutionOrder(5265)]
public class ClumperDeletedGroupExitAuthority : MonoBehaviour
{
    private GroupClumperManager manager;
    private ModelViewer viewer;
    private FieldInfo selectedGroupField;
    private FieldInfo selectedClumperIdField;
    private FieldInfo byGroupField;
    private FieldInfo allGroupIdsField;
    private MethodInfo destroyControlsMethod;

    private readonly HashSet<int> previousLiveGroups = new HashSet<int>();
    private bool initialized;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<ClumperDeletedGroupExitAuthority>() != null) return;
        GameObject go = new GameObject("ClumperDeletedGroupExitAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<ClumperDeletedGroupExitAuthority>();
    }

    void Update()
    {
        Resolve();
        if (manager == null || viewer == null) return;

        HashSet<int> liveGroups = ReadLiveGroups();
        if (!initialized)
        {
            previousLiveGroups.Clear();
            foreach (int gid in liveGroups) previousLiveGroups.Add(gid);
            initialized = true;
        }
        else
        {
            foreach (int oldGid in previousLiveGroups)
            {
                if (!liveGroups.Contains(oldGid))
                    PurgeDeletedGroup(oldGid);
            }

            previousLiveGroups.Clear();
            foreach (int gid in liveGroups) previousLiveGroups.Add(gid);
        }

        // Also fail safe if selection somehow points at a group that is already gone.
        int selectedGroup = selectedGroupField != null && selectedGroupField.GetValue(manager) is int g ? g : -1;
        int selectedClumper = selectedClumperIdField != null && selectedClumperIdField.GetValue(manager) is int c ? c : -1;
        if ((selectedGroup >= 0 && !liveGroups.Contains(selectedGroup)) ||
            (selectedGroup < 0 && selectedClumper >= 0))
            ExitClumper();
    }

    void Resolve()
    {
        if (manager == null)
        {
            manager = FindFirstObjectByType<GroupClumperManager>();
            if (manager != null)
            {
                BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic;
                selectedGroupField = typeof(GroupClumperManager).GetField("selectedGroup", f);
                selectedClumperIdField = typeof(GroupClumperManager).GetField("selectedClumperId", f);
                byGroupField = typeof(GroupClumperManager).GetField("byGroup", f);
                destroyControlsMethod = typeof(GroupClumperManager).GetMethod("DestroyControls", f);
            }
        }

        if (viewer == null)
        {
            viewer = FindFirstObjectByType<ModelViewer>();
            if (viewer != null)
                allGroupIdsField = typeof(ModelViewer).GetField("allGroupIds", BindingFlags.Instance | BindingFlags.NonPublic);
        }
    }

    HashSet<int> ReadLiveGroups()
    {
        HashSet<int> live = new HashSet<int>();
        object raw = allGroupIdsField?.GetValue(viewer);
        if (raw is IEnumerable enumerable)
        {
            foreach (object value in enumerable)
                if (value is int id) live.Add(id);
            return live;
        }

        // Fallback if ModelViewer's internal group collection changes in future.
        foreach (RectTransform rect in FindObjectsByType<RectTransform>(FindObjectsSortMode.None))
        {
            if (rect == null || !rect.name.StartsWith("GroupItem_")) continue;
            if (int.TryParse(rect.name.Substring("GroupItem_".Length), out int gid)) live.Add(gid);
        }
        return live;
    }

    void PurgeDeletedGroup(int gid)
    {
        // The important part: remove the actual modifier records, not just UI selection.
        // IDictionary lets us remove the Dictionary<int,List<GroupClumper>> entry without
        // depending on its private generic field type through reflection.
        IDictionary clumpersByGroup = byGroupField?.GetValue(manager) as IDictionary;
        if (clumpersByGroup != null && clumpersByGroup.Contains(gid))
            clumpersByGroup.Remove(gid);

        // Scope is group-lifetime metadata too. Reset it before this numeric group ID can
        // be recycled by ModelViewer's GetNextAvailableGroupId().
        SurfaceIslandScope.SetClumperContiguous(gid, false);

        int selectedGroup = selectedGroupField != null && selectedGroupField.GetValue(manager) is int g ? g : -1;
        if (selectedGroup == gid) ExitClumper();
    }

    void ExitClumper()
    {
        selectedGroupField?.SetValue(manager, -1);
        selectedClumperIdField?.SetValue(manager, -1);
        destroyControlsMethod?.Invoke(manager, null);

        GameObject host = GameObject.Find("ClumperScrollHost");
        if (host != null) Destroy(host);
    }
}
