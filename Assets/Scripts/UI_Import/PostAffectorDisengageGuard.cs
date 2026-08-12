using System.Reflection;
using UnityEngine;

// Protects the single frame where Ctrl+clicking empty space disengages POST authoring.
// ModelViewer clears hasSelectionHotspot before the modifier stack runs. Without this
// guard, PostAffectorManager can mistake last frame's localized output for upstream
// groom data and bake/reapply it.
[DefaultExecutionOrder(3200)]
public class PostAffectorDisengageGuard : MonoBehaviour
{
    internal static bool releasePending;
    internal static ModelViewer pendingViewer;
    internal static PostAffectorManager pendingPosts;
    internal static FieldInfo pendingSelectionField;
    internal static FieldInfo pendingActiveIdField;
    internal static FieldInfo pendingActiveGroupField;

    private ModelViewer viewer;
    private PostAffectorManager posts;
    private FieldInfo hasSelectionField;
    private FieldInfo activeIdField;
    private FieldInfo activeGroupField;
    private bool previousSelection;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<PostAffectorDisengageGuard>() != null) return;
        GameObject go = new GameObject("PostAffectorDisengageGuard");
        DontDestroyOnLoad(go);
        go.AddComponent<PostAffectorDisengageGuard>();
        go.AddComponent<PostAffectorDisengageRelease>();
    }

    void Update()
    {
        EnsureRefs();
        if (viewer == null || posts == null || hasSelectionField == null || activeIdField == null) return;

        bool selected = HasSelection();
        int activeId = (int)activeIdField.GetValue(posts);

        if (previousSelection && !selected && activeId >= 0)
        {
            // Keep POST logically selected through this frame's modifier evaluation.
            hasSelectionField.SetValue(viewer, true);
            releasePending = true;
            pendingViewer = viewer;
            pendingPosts = posts;
            pendingSelectionField = hasSelectionField;
            pendingActiveIdField = activeIdField;
            pendingActiveGroupField = activeGroupField;
            selected = true;
        }

        previousSelection = selected;
    }

    void EnsureRefs()
    {
        if (viewer == null) viewer = FindFirstObjectByType<ModelViewer>();
        if (posts == null) posts = FindFirstObjectByType<PostAffectorManager>();
        if (viewer == null || posts == null) return;

        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        if (hasSelectionField == null) hasSelectionField = typeof(ModelViewer).GetField("hasSelectionHotspot", flags);
        if (activeIdField == null) activeIdField = typeof(PostAffectorManager).GetField("activeId", flags);
        if (activeGroupField == null) activeGroupField = typeof(PostAffectorManager).GetField("activeGroup", flags);
    }

    bool HasSelection()
    {
        return hasSelectionField != null && hasSelectionField.GetValue(viewer) is bool value && value;
    }

    internal void MarkReleased()
    {
        previousSelection = false;
    }
}

// Release happens after PostAffectorManager (3300) and PostVarianceAffectorBridge (3500)
// have both completed LateUpdate, so the stored POST result is evaluated exactly once.
[DefaultExecutionOrder(3600)]
public class PostAffectorDisengageRelease : MonoBehaviour
{
    void LateUpdate()
    {
        if (!PostAffectorDisengageGuard.releasePending) return;

        ModelViewer viewer = PostAffectorDisengageGuard.pendingViewer;
        PostAffectorManager posts = PostAffectorDisengageGuard.pendingPosts;

        if (viewer != null && PostAffectorDisengageGuard.pendingSelectionField != null)
            PostAffectorDisengageGuard.pendingSelectionField.SetValue(viewer, false);
        if (posts != null && PostAffectorDisengageGuard.pendingActiveIdField != null)
            PostAffectorDisengageGuard.pendingActiveIdField.SetValue(posts, -1);
        if (posts != null && PostAffectorDisengageGuard.pendingActiveGroupField != null)
            PostAffectorDisengageGuard.pendingActiveGroupField.SetValue(posts, -1);

        PostAffectorDisengageGuard guard = FindFirstObjectByType<PostAffectorDisengageGuard>();
        if (guard != null) guard.MarkReleased();

        PostAffectorDisengageGuard.releasePending = false;
        PostAffectorDisengageGuard.pendingViewer = null;
        PostAffectorDisengageGuard.pendingPosts = null;
    }
}
