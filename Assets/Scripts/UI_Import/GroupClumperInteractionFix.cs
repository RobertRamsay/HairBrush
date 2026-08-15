using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

// CLUMPER placement must not depend on ModelViewer's POST/local-selection hotspot.
// TAB + click performs its own model raycast, creates/repositions the one clumper for
// the active group, selects it, and opens its controls immediately.
[DefaultExecutionOrder(5150)]
public class GroupClumperInteractionFix : MonoBehaviour
{
    private ModelViewer viewer;
    private GroupClumperManager manager;
    private FieldInfo byGroupField;
    private FieldInfo selectedGroupField;
    private MethodInfo destroyControlsMethod;
    private MethodInfo maintainControlsMethod;
    private MethodInfo ensureRowsMethod;
    private int handledFrame = -1;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<GroupClumperInteractionFix>() != null) return;
        GameObject go = new GameObject("GroupClumperInteractionFix");
        DontDestroyOnLoad(go);
        go.AddComponent<GroupClumperInteractionFix>();
    }

    void Update()
    {
        Resolve();
        if (viewer == null || manager == null || Mouse.current == null || Keyboard.current == null) return;
        if (!Keyboard.current.tabKey.isPressed || !Mouse.current.leftButton.wasPressedThisFrame) return;
        if (handledFrame == Time.frameCount) return;
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;
        if (viewer.mainCamera == null) return;

        Ray ray = viewer.mainCamera.ScreenPointToRay(Mouse.current.position.ReadValue());
        if (!Physics.Raycast(ray, out RaycastHit hit)) return;

        handledFrame = Time.frameCount;
        int gid = viewer.currentGroupId;
        Dictionary<int, GroupClumperManager.GroupClumper> byGroup =
            byGroupField?.GetValue(manager) as Dictionary<int, GroupClumperManager.GroupClumper>;
        if (byGroup == null) return;

        if (!byGroup.TryGetValue(gid, out GroupClumperManager.GroupClumper clumper) || clumper == null)
        {
            clumper = new GroupClumperManager.GroupClumper { groupId = gid };
            byGroup[gid] = clumper;
        }

        clumper.center = hit.point;
        clumper.normal = hit.normal.sqrMagnitude > 0.000001f ? hit.normal.normalized : Vector3.up;
        clumper.lastTopologyHash = 0;
        if (clumper.leaders == null) clumper.leaders = new List<HairCard>();
        else clumper.leaders.Clear();

        selectedGroupField?.SetValue(manager, gid);
        viewer.currentGroupId = gid;

        // Do not wait for the periodic UI scan: open the CLUMPER editor now.
        destroyControlsMethod?.Invoke(manager, null);
        ensureRowsMethod?.Invoke(manager, null);
        maintainControlsMethod?.Invoke(manager, null);
    }

    void Resolve()
    {
        if (viewer == null) viewer = FindFirstObjectByType<ModelViewer>();
        if (manager == null)
        {
            manager = FindFirstObjectByType<GroupClumperManager>();
            if (manager != null)
            {
                BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                System.Type t = typeof(GroupClumperManager);
                byGroupField = t.GetField("byGroup", flags);
                selectedGroupField = t.GetField("selectedGroup", flags);
                destroyControlsMethod = t.GetMethod("DestroyControls", flags);
                maintainControlsMethod = t.GetMethod("MaintainControls", flags);
                ensureRowsMethod = t.GetMethod("EnsureRows", flags);
            }
        }
    }
}
