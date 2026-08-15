using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

// Owns CLUMPER interaction semantics without borrowing POST's hotspot state.
// TAB + click  : create/select the one clumper for the current group.
// SPACE + click: reposition the selected clumper.
// Removing a clumper restores the upstream Group -> POST result immediately.
[DefaultExecutionOrder(5150)]
public class GroupClumperInteractionAuthority : MonoBehaviour
{
    private GroupClumperManager clumpers;
    private ModelViewer viewer;
    private PostAffectorManager posts;

    private FieldInfo byGroupField;
    private FieldInfo selectedGroupField;
    private FieldInfo lastTabClickFrameField;
    private MethodInfo destroyControlsMethod;
    private MethodInfo rebuildRowsSoonMethod;
    private MethodInfo postApplyAllMethod;
    private FieldInfo postActiveIdField;
    private FieldInfo postActiveGroupField;
    private FieldInfo viewerHasSelectionField;
    private MethodInfo clearSelectionMethod;

    private readonly HashSet<int> previousGroups = new HashSet<int>();
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
        if (clumpers == null || viewer == null) return;

        WatchForRemovedClumpers();

        if (Mouse.current == null || Keyboard.current == null) return;
        if (!Mouse.current.leftButton.wasPressedThisFrame) return;
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;
        if (lastHandledFrame == Time.frameCount) return;

        bool tab = Keyboard.current.tabKey.isPressed;
        bool space = Keyboard.current.spaceKey.isPressed;
        if (!tab && !space) return;

        IDictionary dict = Groups();
        if (dict == null) return;

        int gid = viewer.currentGroupId;
        if (space)
        {
            int selected = SelectedGroup();
            if (selected < 0 || selected != gid || !dict.Contains(gid)) return;
        }

        if (!TryRaycastModel(out RaycastHit hit)) return;

        lastHandledFrame = Time.frameCount;
        if (tab) CreateOrMove(dict, gid, hit.point, hit.normal, true);
        else CreateOrMove(dict, gid, hit.point, hit.normal, false);
    }

    void LateUpdate()
    {
        // Keep the old first-pass TAB handler from also consuming the same click when a
        // POST hotspot happened to be active. We handled it independently above.
        if (lastHandledFrame == Time.frameCount && lastTabClickFrameField != null)
            lastTabClickFrameField.SetValue(clumpers, Time.frameCount);
    }

    void Resolve()
    {
        if (clumpers == null)
        {
            clumpers = FindFirstObjectByType<GroupClumperManager>();
            if (clumpers != null)
            {
                BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic;
                Type t = typeof(GroupClumperManager);
                byGroupField = t.GetField("byGroup", f);
                selectedGroupField = t.GetField("selectedGroup", f);
                lastTabClickFrameField = t.GetField("lastTabClickFrame", f);
                destroyControlsMethod = t.GetMethod("DestroyControls", f);
                rebuildRowsSoonMethod = t.GetMethod("RebuildRowsSoon", f);
            }
        }

        if (viewer == null)
        {
            viewer = FindFirstObjectByType<ModelViewer>();
            if (viewer != null)
            {
                BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic;
                viewerHasSelectionField = typeof(ModelViewer).GetField("hasSelectionHotspot", f);
                clearSelectionMethod = typeof(ModelViewer).GetMethod("ClearSelectionHotspot", f);
            }
        }

        if (posts == null)
        {
            posts = FindFirstObjectByType<PostAffectorManager>();
            if (posts != null)
            {
                BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic;
                Type t = typeof(PostAffectorManager);
                postApplyAllMethod = t.GetMethod("ApplyAll", f);
                postActiveIdField = t.GetField("activeId", f);
                postActiveGroupField = t.GetField("activeGroup", f);
            }
        }
    }

    IDictionary Groups()
    {
        return byGroupField?.GetValue(clumpers) as IDictionary;
    }

    int SelectedGroup()
    {
        return selectedGroupField != null && selectedGroupField.GetValue(clumpers) is int gid ? gid : -1;
    }

    bool TryRaycastModel(out RaycastHit hit)
    {
        hit = default;
        if (viewer.mainCamera == null) return false;
        Ray ray = viewer.mainCamera.ScreenPointToRay(Mouse.current.position.ReadValue());
        return Physics.Raycast(ray, out hit);
    }

    void CreateOrMove(IDictionary dict, int gid, Vector3 point, Vector3 normal, bool createIfMissing)
    {
        object raw = dict.Contains(gid) ? dict[gid] : null;
        GroupClumperManager.GroupClumper clumper = raw as GroupClumperManager.GroupClumper;
        if (clumper == null)
        {
            if (!createIfMissing) return;
            clumper = new GroupClumperManager.GroupClumper { groupId = gid };
            dict[gid] = clumper;
        }

        ExitPostEditing();

        clumper.center = point;
        clumper.normal = normal.sqrMagnitude > .000001f ? normal.normalized : Vector3.up;
        clumper.lastTopologyHash = 0;
        if (clumper.leaders == null) clumper.leaders = new List<HairCard>();
        else clumper.leaders.Clear();

        viewer.currentGroupId = gid;
        selectedGroupField?.SetValue(clumpers, gid);
        destroyControlsMethod?.Invoke(clumpers, null);
        rebuildRowsSoonMethod?.Invoke(clumpers, null);
    }

    void ExitPostEditing()
    {
        // CLUMPER has its own marker/edit state. Do not leave the yellow POST hotspot active
        // while manipulating it or the two marker concepts become visually ambiguous.
        clearSelectionMethod?.Invoke(viewer, null);
        postActiveIdField?.SetValue(posts, -1);
        postActiveGroupField?.SetValue(posts, -1);
        if (viewerHasSelectionField != null) viewerHasSelectionField.SetValue(viewer, false);
    }

    void WatchForRemovedClumpers()
    {
        IDictionary dict = Groups();
        if (dict == null) return;

        HashSet<int> current = new HashSet<int>();
        foreach (DictionaryEntry e in dict)
            if (e.Key is int gid) current.Add(gid);

        foreach (int removed in previousGroups.Where(g => !current.Contains(g)).ToArray())
            RestoreUpstream(removed);

        previousGroups.Clear();
        foreach (int gid in current) previousGroups.Add(gid);
    }

    void RestoreUpstream(int gid)
    {
        // POST manager is the authoritative upstream evaluator. Reapplying it once erases
        // the mesh-only clumper deformation and leaves Group -> POST exactly as authored.
        if (posts != null && postApplyAllMethod != null)
        {
            postApplyAllMethod.Invoke(posts, null);
            return;
        }

        // Safe fallback when there is no POST manager yet.
        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
            if (card != null && card.groupId == gid) card.GenerateMesh();
    }

    void OnDrawGizmos()
    {
        Resolve();
        IDictionary dict = Groups();
        if (dict == null) return;
        int selected = SelectedGroup();
        if (selected < 0 || !dict.Contains(selected)) return;

        GroupClumperManager.GroupClumper clumper = dict[selected] as GroupClumperManager.GroupClumper;
        if (clumper == null) return;

        Gizmos.color = new Color(.15f, 1f, .45f, 1f);
        float r = Mathf.Max(.003f, clumper.radius * .12f);
        Gizmos.DrawSphere(clumper.center, r);
        Gizmos.DrawLine(clumper.center, clumper.center + clumper.normal * Mathf.Max(.03f, r * 5f));
    }
}
