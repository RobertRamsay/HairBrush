using System.Reflection;
using UnityEngine;

// CLUMPER and POST are mutually-exclusive edit contexts.
// Whenever a clumper point is selected, tear down POST's hotspot/visualizer state immediately.
[DefaultExecutionOrder(5190)]
public class ClumperPostOwnershipAuthority : MonoBehaviour
{
    private GroupClumperManager clumpers;
    private PostAffectorManager posts;
    private ModelViewer viewer;

    private FieldInfo postActiveIdField;
    private FieldInfo postActiveGroupField;
    private FieldInfo viewerHasSelectionField;
    private FieldInfo viewerSelectionModeField;
    private MethodInfo clearSelectionMethod;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<ClumperPostOwnershipAuthority>() != null) return;
        GameObject go = new GameObject("ClumperPostOwnershipAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<ClumperPostOwnershipAuthority>();
    }

    void Update()
    {
        Resolve();
        if (clumpers == null || viewer == null) return;
        if (clumpers.GetSelectedClumper() == null) return;

        // Clear the ModelViewer hotspot first: this is what the POST radius/falloff
        // visualizers key from, and also removes any local POST selection weights.
        bool hasSelection = viewerHasSelectionField != null &&
            viewerHasSelectionField.GetValue(viewer) is bool selected && selected;
        bool selectionMode = viewerSelectionModeField != null &&
            viewerSelectionModeField.GetValue(viewer) is bool mode && mode;

        if (hasSelection || selectionMode)
            clearSelectionMethod?.Invoke(viewer, null);

        // POST's own active IDs are separate from ModelViewer's hotspot state.
        // Clear both so no POST authority can recreate the circle on the next frame.
        if (posts != null)
        {
            if (postActiveIdField != null && postActiveIdField.GetValue(posts) is int activeId && activeId >= 0)
                postActiveIdField.SetValue(posts, -1);
            if (postActiveGroupField != null && postActiveGroupField.GetValue(posts) is int activeGroup && activeGroup >= 0)
                postActiveGroupField.SetValue(posts, -1);
        }

        if (viewerHasSelectionField != null) viewerHasSelectionField.SetValue(viewer, false);
        if (viewerSelectionModeField != null) viewerSelectionModeField.SetValue(viewer, false);
    }

    void Resolve()
    {
        if (clumpers == null) clumpers = FindFirstObjectByType<GroupClumperManager>();

        if (viewer == null)
        {
            viewer = FindFirstObjectByType<ModelViewer>();
            if (viewer != null)
            {
                BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                viewerHasSelectionField = typeof(ModelViewer).GetField("hasSelectionHotspot", flags);
                viewerSelectionModeField = typeof(ModelViewer).GetField("isSelectionMode", flags);
                clearSelectionMethod = typeof(ModelViewer).GetMethod("ClearSelectionHotspot", flags);
            }
        }

        if (posts == null)
        {
            posts = FindFirstObjectByType<PostAffectorManager>();
            if (posts != null)
            {
                BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                postActiveIdField = typeof(PostAffectorManager).GetField("activeId", flags);
                postActiveGroupField = typeof(PostAffectorManager).GetField("activeGroup", flags);
            }
        }
    }
}
