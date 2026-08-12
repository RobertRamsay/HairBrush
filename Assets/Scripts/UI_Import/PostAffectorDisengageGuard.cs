using System.Reflection;
using UnityEngine;

// Protects the single frame where Ctrl+clicking empty space disengages POST authoring.
//
// ModelViewer clears hasSelectionHotspot in its normal Update. On that same frame,
// PostAffectorManager would otherwise see editingPost=false and can mistake the
// previous frame's localized variance/clump result for new upstream groom data.
// That bakes the POST result into its base and then applies the POST again.
//
// Keep the hotspot logically alive only through the modifier evaluation for this
// one transition frame, then release the active POST after all modifier LateUpdates.
[DefaultExecutionOrder(3200)]
public class PostAffectorDisengageGuard : MonoBehaviour
{
    private ModelViewer viewer;
    private PostAffectorManager posts;

    private FieldInfo hasSelectionField;
    private FieldInfo activeIdField;
    private FieldInfo activeGroupField;

    private bool protectedDisengage;
    private bool previousSelection = false;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<PostAffectorDisengageGuard>() != null) return;
        GameObject go = new GameObject("PostAffectorDisengageGuard");
        DontDestroyOnLoad(go);
        go.AddComponent<PostAffectorDisengageGuard>();
    }

    void Update()
    {
        EnsureRefs();
        if (viewer == null || posts == null || hasSelectionField == null || activeIdField == null) return;

        bool selected = HasSelection();
        int activeId = (int)activeIdField.GetValue(posts);

        // ModelViewer has just cleared the hotspot, but the POST is still active until
        // PostAffectorManager gets its Update. Preserve editing state for exactly this
        // modifier evaluation so its upstream base cannot absorb last frame's output.
        if (previousSelection && !selected && activeId >= 0)
        {
            hasSelectionField.SetValue(viewer, true);
            protectedDisengage = true;
            selected = true;
        }

        previousSelection = selected;
    }

    // Runs after PostAffectorManager (3300) and PostVarianceAffectorBridge (3500)
    // LateUpdates, so the committed visual result has been evaluated exactly once.
    [DefaultExecutionOrder(3600)]
    private class ReleasePhase : MonoBehaviour { }

    void LateUpdate()
    {
        if (!protectedDisengage || viewer == null || posts == null) return;

        if (hasSelectionField != null) hasSelectionField.SetValue(viewer, false);
        if (activeIdField != null) activeIdField.SetValue(posts, -1);
        if (activeGroupField != null) activeGroupField.SetValue(posts, -1);

        protectedDisengage = false;
        previousSelection = false;
    }

    void EnsureRefs()
    {
        if (viewer == null) viewer = FindFirstObjectByType<ModelViewer>();
        if (posts == null) posts = FindFirstObjectByType<PostAffectorManager>();
        if (viewer == null || posts == null) return;

        if (hasSelectionField == null)
            hasSelectionField = typeof(ModelViewer).GetField("hasSelectionHotspot", BindingFlags.Instance | BindingFlags.NonPublic);
        if (activeIdField == null)
            activeIdField = typeof(PostAffectorManager).GetField("activeId", BindingFlags.Instance | BindingFlags.NonPublic);
        if (activeGroupField == null)
            activeGroupField = typeof(PostAffectorManager).GetField("activeGroup", BindingFlags.Instance | BindingFlags.NonPublic);
    }

    bool HasSelection()
    {
        return hasSelectionField != null && hasSelectionField.GetValue(viewer) is bool value && value;
    }
}
