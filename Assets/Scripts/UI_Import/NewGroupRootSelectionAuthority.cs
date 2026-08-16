using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;

// A freshly-created Hair Group always starts in root/group authoring context.
// This covers every creation path (the + GROUP button and stroke/dialog group creation):
// leave POST/CLUMPER editing first, then select the new group's normal root controls.
[DefaultExecutionOrder(5270)]
public class NewGroupRootSelectionAuthority : MonoBehaviour
{
    private ModelViewer viewer;
    private PostAffectorManager posts;
    private GroupClumperManager clumpers;

    private FieldInfo allGroupIdsField;
    private MethodInfo selectGroupMethod;
    private MethodInfo clearSelectionMethod;

    private FieldInfo postActiveIdField;
    private FieldInfo postActiveGroupField;

    private FieldInfo clumperSelectedGroupField;
    private FieldInfo clumperSelectedIdField;
    private MethodInfo clumperDestroyControlsMethod;

    private readonly HashSet<int> previousGroups = new HashSet<int>();
    private bool initialized;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<NewGroupRootSelectionAuthority>() != null) return;
        GameObject go = new GameObject("NewGroupRootSelectionAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<NewGroupRootSelectionAuthority>();
    }

    void Update()
    {
        Resolve();
        if (viewer == null || allGroupIdsField == null) return;

        HashSet<int> current = ReadGroups();
        if (!initialized)
        {
            ReplaceSnapshot(current);
            initialized = true;
            return;
        }

        int newGroup = -1;
        foreach (int gid in current)
        {
            if (previousGroups.Contains(gid)) continue;
            // If more than one group appears in the same frame, favour ModelViewer's
            // current group because that is the group the creation path just selected.
            if (gid == viewer.currentGroupId)
            {
                newGroup = gid;
                break;
            }
            if (newGroup < 0) newGroup = gid;
        }

        ReplaceSnapshot(current);
        if (newGroup < 0) return;

        EnterFreshGroupRoot(newGroup);
    }

    void Resolve()
    {
        if (viewer == null)
        {
            viewer = FindFirstObjectByType<ModelViewer>();
            if (viewer != null)
            {
                BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic;
                allGroupIdsField = typeof(ModelViewer).GetField("allGroupIds", f);
                selectGroupMethod = typeof(ModelViewer).GetMethod("SelectGroup", f);
                clearSelectionMethod = typeof(ModelViewer).GetMethod("ClearSelectionHotspot", f);
            }
        }

        if (posts == null)
        {
            posts = FindFirstObjectByType<PostAffectorManager>();
            if (posts != null)
            {
                BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic;
                postActiveIdField = typeof(PostAffectorManager).GetField("activeId", f);
                postActiveGroupField = typeof(PostAffectorManager).GetField("activeGroup", f);
            }
        }

        if (clumpers == null)
        {
            clumpers = FindFirstObjectByType<GroupClumperManager>();
            if (clumpers != null)
            {
                BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic;
                clumperSelectedGroupField = typeof(GroupClumperManager).GetField("selectedGroup", f);
                clumperSelectedIdField = typeof(GroupClumperManager).GetField("selectedClumperId", f);
                clumperDestroyControlsMethod = typeof(GroupClumperManager).GetMethod("DestroyControls", f);
            }
        }
    }

    HashSet<int> ReadGroups()
    {
        HashSet<int> result = new HashSet<int>();
        object raw = allGroupIdsField?.GetValue(viewer);
        if (raw is IEnumerable<int> groups)
        {
            foreach (int gid in groups) result.Add(gid);
        }
        return result;
    }

    void ReplaceSnapshot(HashSet<int> current)
    {
        previousGroups.Clear();
        foreach (int gid in current) previousGroups.Add(gid);
    }

    void EnterFreshGroupRoot(int gid)
    {
        // Release ModelViewer's localized selection/hotspot first so normal group sliders
        // cannot inherit a POST-local edit context.
        clearSelectionMethod?.Invoke(viewer, null);
        viewer.selectionStrength = 0f;

        // POST records remain intact on their owning old groups; only editor ownership exits.
        postActiveIdField?.SetValue(posts, -1);
        postActiveGroupField?.SetValue(posts, -1);

        // Likewise, leave CLUMPER editing without deleting/neutralizing any existing clumper.
        clumperSelectedGroupField?.SetValue(clumpers, -1);
        clumperSelectedIdField?.SetValue(clumpers, -1);
        clumperDestroyControlsMethod?.Invoke(clumpers, null);

        GameObject host = GameObject.Find("ClumperScrollHost");
        if (host != null) Destroy(host);

        // Use ModelViewer's own group-selection path so UV/base values, group highlight and
        // all standard group controls are refreshed exactly as if the root had been clicked.
        selectGroupMethod?.Invoke(viewer, new object[] { gid });

        if (EventSystem.current != null)
        {
            GameObject row = GameObject.Find("GroupItem_" + gid);
            Transform label = row != null ? row.transform.Find("LabelButton") : null;
            EventSystem.current.SetSelectedGameObject(label != null ? label.gameObject : null);
        }
    }
}
