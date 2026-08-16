using System.Collections;
using System.Reflection;
using UnityEngine;

// A selected CLUMPER must never survive deletion/reset of its owning Hair Group.
// This watches the authoritative ModelViewer group collection so every deletion path
// (UI delete, project reset/load, future tooling) tears down CLUMPER editing immediately.
[DefaultExecutionOrder(5265)]
public class ClumperDeletedGroupExitAuthority : MonoBehaviour
{
    private GroupClumperManager manager;
    private ModelViewer viewer;
    private FieldInfo selectedGroupField;
    private FieldInfo selectedClumperIdField;
    private FieldInfo allGroupIdsField;
    private MethodInfo destroyControlsMethod;

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
        if (manager == null || viewer == null || selectedGroupField == null || selectedClumperIdField == null) return;

        int gid = selectedGroupField.GetValue(manager) is int g ? g : -1;
        int clumperId = selectedClumperIdField.GetValue(manager) is int c ? c : -1;
        if (gid < 0 && clumperId < 0) return;

        if (gid >= 0 && GroupStillExists(gid)) return;
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

    bool GroupStillExists(int gid)
    {
        object raw = allGroupIdsField?.GetValue(viewer);
        if (raw is IEnumerable enumerable)
        {
            foreach (object value in enumerable)
                if (value is int id && id == gid) return true;
            return false;
        }

        // Fallback to the visible group root if the internal collection ever changes type.
        return GameObject.Find("GroupItem_" + gid) != null;
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
