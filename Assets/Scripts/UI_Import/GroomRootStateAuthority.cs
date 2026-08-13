using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

// Keeps the authored group-root controls separate from temporary POST authoring controls.
// Runs before ModelViewer so a normal card placement always sees the root values, never
// the last selected POST values. This is control-state only; card canonical data remains
// owned by HairCard/PostAffectorManager.
[DefaultExecutionOrder(-1100)]
public class GroomRootStateAuthority : MonoBehaviour
{
    public struct RootState
    {
        public float length, width, bend, twist, depth;
        public int segments;
        public float x, y, z;
        public float uScale, vScale, uOffset, vOffset;
    }

    private readonly Dictionary<int, RootState> roots = new();
    private ModelViewer viewer;
    private PostAffectorManager posts;
    private FieldInfo hasSelectionField;
    private FieldInfo loadedModelField;
    private GameObject lastLoadedModel;
    private bool wasSelected;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<GroomRootStateAuthority>() != null) return;
        GameObject go = new GameObject("GroomRootStateAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<GroomRootStateAuthority>();
    }

    void Update()
    {
        Resolve();
        if (viewer == null) return;

        GameObject loaded = loadedModelField?.GetValue(viewer) as GameObject;
        if (loaded != lastLoadedModel)
        {
            lastLoadedModel = loaded;
            roots.Clear();
            wasSelected = false;
            // LoadProject has already populated the saved root controls by the next Update.
            // New-model RESET may replace them later this frame; with no POST they will be
            // captured again on the following frame.
            roots[viewer.currentGroupId] = ReadViewer();
        }

        int groupId = viewer.currentGroupId;
        bool selected = HasSelection();
        bool hasPost = GroupHasPost(groupId);

        // A POST row can be removed or group-root can be selected after this component's
        // previous Update. On the next frame restore the preserved root before deciding
        // whether the now-unlocked controls should become authoritative again.
        if (wasSelected && !selected && roots.TryGetValue(groupId, out RootState exitedRoot))
            WriteViewer(exitedRoot);

        if (!roots.ContainsKey(groupId) && !selected)
            roots[groupId] = ReadViewer();

        if (selected)
        {
            wasSelected = true;
            return;
        }

        if (hasPost)
        {
            // POST controls temporarily borrow ModelViewer.current*. When POST authoring is
            // over, force those fields back to the preserved root before ModelViewer handles
            // placement or variance for this frame.
            RestoreRootToViewer(groupId);
        }
        else
        {
            // With no structural modifier the normal sliders are the authoritative root.
            roots[groupId] = ReadViewer();
        }

        wasSelected = false;
    }

    void Resolve()
    {
        if (viewer == null)
        {
            viewer = FindFirstObjectByType<ModelViewer>();
            if (viewer != null)
            {
                BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                hasSelectionField = typeof(ModelViewer).GetField("hasSelectionHotspot", flags);
                loadedModelField = typeof(ModelViewer).GetField("loadedModel", flags);
                lastLoadedModel = loadedModelField?.GetValue(viewer) as GameObject;
                roots[viewer.currentGroupId] = ReadViewer();
            }
        }
        if (posts == null) posts = FindFirstObjectByType<PostAffectorManager>();
    }

    bool HasSelection()
    {
        return hasSelectionField != null && viewer != null && hasSelectionField.GetValue(viewer) is bool b && b;
    }

    bool GroupHasPost(int groupId)
    {
        if (posts == null) return false;
        List<PostAffectorSaveData> items = posts.ExportGroup(groupId);
        return items != null && items.Count > 0;
    }

    RootState ReadViewer()
    {
        return new RootState
        {
            length = viewer.currentLength,
            width = viewer.currentWidth,
            segments = viewer.currentSegments,
            bend = viewer.currentBend,
            twist = viewer.currentTwist,
            depth = viewer.currentEmbedDepth,
            x = viewer.currentOffsetX,
            y = viewer.currentOffsetY,
            z = viewer.currentOffsetZ,
            uScale = viewer.currentUScale,
            vScale = viewer.currentVScale,
            uOffset = viewer.currentUOffset,
            vOffset = viewer.currentVOffset
        };
    }

    void WriteViewer(RootState s)
    {
        viewer.currentLength = s.length;
        viewer.currentWidth = s.width;
        viewer.currentSegments = s.segments;
        viewer.currentBend = s.bend;
        viewer.currentTwist = s.twist;
        viewer.currentEmbedDepth = s.depth;
        viewer.currentOffsetX = s.x;
        viewer.currentOffsetY = s.y;
        viewer.currentOffsetZ = s.z;
        viewer.currentUScale = s.uScale;
        viewer.currentVScale = s.vScale;
        viewer.currentUOffset = s.uOffset;
        viewer.currentVOffset = s.vOffset;
    }

    public bool TryGetRootState(int groupId, out RootState state)
    {
        Resolve();
        return roots.TryGetValue(groupId, out state);
    }

    public void RestoreRootToViewer(int groupId)
    {
        Resolve();
        if (viewer != null && roots.TryGetValue(groupId, out RootState state))
            WriteViewer(state);
    }

    public void ClearStoredRoots()
    {
        roots.Clear();
        wasSelected = false;
        if (viewer != null) roots[viewer.currentGroupId] = ReadViewer();
    }
}
