using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

// CLUMPER interaction semantics:
// TAB + click   = always create a new clumper point in the current group.
// SPACE + click = reposition the currently selected clumper point.
[DefaultExecutionOrder(5150)]
public class GroupClumperInteractionAuthority : MonoBehaviour
{
    private GroupClumperManager clumpers;
    private ModelViewer viewer;
    private int lastHandledFrame = -1;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<GroupClumperInteractionAuthority>() != null) return;
        GameObject go = new GameObject("GroupClumperInteractionAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<GroupClumperInteractionAuthority>();
    }

    void Update()
    {
        Resolve();
        if (clumpers == null || viewer == null || Mouse.current == null || Keyboard.current == null) return;
        if (!Mouse.current.leftButton.wasPressedThisFrame || lastHandledFrame == Time.frameCount) return;
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;

        bool tab = Keyboard.current.tabKey.isPressed;
        bool space = Keyboard.current.spaceKey.isPressed;
        if (!tab && !space) return;
        if (!TryRaycastModel(out RaycastHit hit)) return;

        int gid = viewer.currentGroupId;
        if (tab)
        {
            clumpers.CreateClumper(gid, hit.point, hit.normal);
            lastHandledFrame = Time.frameCount;
            return;
        }

        if (space && clumpers.MoveSelectedClumper(gid, hit.point, hit.normal))
            lastHandledFrame = Time.frameCount;
    }

    void Resolve()
    {
        if (clumpers == null) clumpers = FindFirstObjectByType<GroupClumperManager>();
        if (viewer == null) viewer = FindFirstObjectByType<ModelViewer>();
    }

    bool TryRaycastModel(out RaycastHit hit)
    {
        hit = default;
        if (viewer == null || viewer.mainCamera == null) return false;
        Ray ray = viewer.mainCamera.ScreenPointToRay(Mouse.current.position.ReadValue());
        return Physics.Raycast(ray, out hit);
    }

    void OnDrawGizmos()
    {
        Resolve();
        if (clumpers == null) return;
        GroupClumperManager.GroupClumper clumper = clumpers.GetSelectedClumper();
        if (clumper == null) return;
        Gizmos.color = new Color(.15f, 1f, .45f, 1f);
        float r = Mathf.Max(.003f, clumper.radius * .12f);
        Gizmos.DrawSphere(clumper.center, r);
        Gizmos.DrawLine(clumper.center, clumper.center + clumper.normal * Mathf.Max(.03f, r * 5f));
    }
}
