using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

// Keeps the authored group-root controls separate from temporary POST/CLUMPER authoring controls.
// Runs before ModelViewer so a normal card placement always sees the root values, never
// the last selected modifier values. This is control-state only; card canonical data remains
// owned by HairCard/PostAffectorManager/GroupClumperManager.
[DefaultExecutionOrder(-1100)]
public class GroomRootStateAuthority : MonoBehaviour
{
    public struct RootState
    {
        public float length, width, bend, twist, depth;
        public int segments;
        public float x, y, z;
        public float uScale, vScale, uOffset, vOffset;
        public float curlFrequency, curlDiameter;
        public float waveAmplitude, waveFrequency, waveDirection;
        public float arch;
    }

    private readonly Dictionary<int, RootState> roots = new();
    private ModelViewer viewer;
    private PostAffectorManager posts;
    private GroupClumperManager clumpers;
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
            // New-model RESET may replace them later this frame; with no modifier selected they
            // will be captured again on the following frame.
            roots[viewer.currentGroupId] = ReadViewer();
        }

        int groupId = viewer.currentGroupId;
        bool selected = HasSelection() || IsClumperSelectedForGroup(groupId);
        bool hasModifier = GroupHasPost(groupId) || GroupHasClumper(groupId);

        // A POST/CLUMPER row can be removed or group-root can be selected after this component's
        // previous Update. On the next frame restore the preserved root before deciding
        // whether the now-unlocked controls should become authoritative again.
        if (wasSelected && !selected && roots.TryGetValue(groupId, out RootState exitedRoot))
            WriteViewer(exitedRoot);

        if (!roots.ContainsKey(groupId) && !selected)
            roots[groupId] = ReadViewer();

        if (selected)
        {
            // Modifier controls are never allowed to redefine the group's authored root/base.
            wasSelected = true;
            return;
        }

        if (hasModifier)
        {
            // POST/CLUMPER controls may temporarily borrow or coexist with ModelViewer.current*.
            // When modifier authoring is over, force those fields back to the preserved root
            // before ModelViewer handles placement or variance for this frame. This means
            // adding more hairs to an existing modified group starts from its original base.
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
        if (clumpers == null) clumpers = FindFirstObjectByType<GroupClumperManager>();
    }

    bool HasSelection()
    {
        return hasSelectionField != null && viewer != null && hasSelectionField.GetValue(viewer) is bool b && b;
    }

    bool IsClumperSelectedForGroup(int groupId)
    {
        if (clumpers == null) return false;
        GroupClumperManager.GroupClumper selected = clumpers.GetSelectedClumper();
        return selected != null && selected.groupId == groupId;
    }

    bool GroupHasPost(int groupId)
    {
        if (posts == null) return false;
        List<PostAffectorSaveData> items = posts.ExportGroup(groupId);
        return items != null && items.Count > 0;
    }

    bool GroupHasClumper(int groupId)
    {
        return clumpers != null && clumpers.HasClumpers(groupId);
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
            vOffset = viewer.currentVOffset,
            curlFrequency = viewer.currentCurlFrequency,
            curlDiameter = viewer.currentCurlDiameter,
            waveAmplitude = viewer.currentWaveAmplitude,
            waveFrequency = viewer.currentWaveFrequency,
            waveDirection = viewer.currentWaveDirection,
            arch = viewer.currentArch
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
        viewer.currentCurlFrequency = s.curlFrequency;
        viewer.currentCurlDiameter = s.curlDiameter;
        viewer.currentWaveAmplitude = s.waveAmplitude;
        viewer.currentWaveFrequency = s.waveFrequency;
        viewer.currentWaveDirection = s.waveDirection;
        viewer.currentArch = s.arch;
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

    // Drops every stored root WITHOUT re-capturing the viewer's current slider values.
    //
    // ClearStoredRoots above deliberately re-seeds from the viewer, which is right when
    // the session is being reset to a known state. Project load is the opposite case:
    // the sliders still hold the previous session's values at that moment, so re-seeding
    // would hand the loaded groups a root belonging to whatever was on screen before.
    // Forgetting outright lets each group fall back to sampling its own loaded cards.
    public void ForgetStoredRoots()
    {
        roots.Clear();
        wasSelected = false;
    }
}
