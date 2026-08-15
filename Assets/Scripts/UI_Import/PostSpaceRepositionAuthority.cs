using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

// SPACE+Click repositions the currently selected POST. CLUMPER owns the same gesture when
// CLUMPER editing is active, so these two modifier types never fight over the click.
[DefaultExecutionOrder(5100)]
public class PostSpaceRepositionAuthority : MonoBehaviour
{
    private PostAffectorManager posts;
    private GroupClumperManager clumpers;
    private ModelViewer viewer;

    private FieldInfo groupsField;
    private FieldInfo activeIdField;
    private FieldInfo activeGroupField;
    private FieldInfo hitPointField;
    private FieldInfo hitNormalField;
    private FieldInfo selectedClumperGroupField;
    private MethodInfo recomputeWeightsMethod;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<PostSpaceRepositionAuthority>() != null) return;
        GameObject go = new GameObject("PostSpaceRepositionAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<PostSpaceRepositionAuthority>();
    }

    void Update()
    {
        Resolve();
        if (posts == null || viewer == null || Mouse.current == null || Keyboard.current == null) return;
        if (!Keyboard.current.spaceKey.isPressed || !Mouse.current.leftButton.wasPressedThisFrame) return;
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;

        // If a CLUMPER is selected, SPACE belongs to the clumper authority instead.
        if (selectedClumperGroupField != null && clumpers != null &&
            selectedClumperGroupField.GetValue(clumpers) is int selectedClumper && selectedClumper >= 0)
            return;

        int activeId = activeIdField != null && activeIdField.GetValue(posts) is int id ? id : -1;
        int activeGroup = activeGroupField != null && activeGroupField.GetValue(posts) is int gid ? gid : -1;
        if (activeId < 0 || activeGroup < 0 || viewer.mainCamera == null) return;

        Ray ray = viewer.mainCamera.ScreenPointToRay(Mouse.current.position.ReadValue());
        if (!Physics.Raycast(ray, out RaycastHit hit)) return;

        PostAffectorManager.PostAffector active = FindAffector(activeGroup, activeId);
        if (active == null) return;

        active.center = hit.point;
        active.normal = hit.normal.sqrMagnitude > .000001f ? hit.normal.normalized : Vector3.up;
        hitPointField?.SetValue(viewer, active.center);
        hitNormalField?.SetValue(viewer, active.normal);
        viewer.currentGroupId = activeGroup;
        recomputeWeightsMethod?.Invoke(viewer, new object[] { active.center });
    }

    PostAffectorManager.PostAffector FindAffector(int gid, int id)
    {
        object raw = groupsField?.GetValue(posts);
        if (!(raw is IDictionary dict) || !dict.Contains(gid)) return null;
        if (!(dict[gid] is IEnumerable list)) return null;
        foreach (object item in list)
            if (item is PostAffectorManager.PostAffector a && a.id == id) return a;
        return null;
    }

    void Resolve()
    {
        BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic;

        if (posts == null)
        {
            posts = FindFirstObjectByType<PostAffectorManager>();
            if (posts != null)
            {
                groupsField = typeof(PostAffectorManager).GetField("groups", f);
                activeIdField = typeof(PostAffectorManager).GetField("activeId", f);
                activeGroupField = typeof(PostAffectorManager).GetField("activeGroup", f);
            }
        }

        if (viewer == null)
        {
            viewer = FindFirstObjectByType<ModelViewer>();
            if (viewer != null)
            {
                hitPointField = typeof(ModelViewer).GetField("selectionHitPoint", f);
                hitNormalField = typeof(ModelViewer).GetField("selectionHitNormal", f);
                recomputeWeightsMethod = typeof(ModelViewer).GetMethod("RecomputeSelectionWeights", f);
            }
        }

        if (clumpers == null)
        {
            clumpers = FindFirstObjectByType<GroupClumperManager>();
            if (clumpers != null)
                selectedClumperGroupField = typeof(GroupClumperManager).GetField("selectedGroup", f);
        }
    }
}
