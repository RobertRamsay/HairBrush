using System.Reflection;
using UnityEngine;

// PostAffectorManager owns POST selection, while ModelViewer owns the legacy localized-selection
// state and its UI. When POST editing ends, make sure both systems leave localized mode together.
// This is especially important when the active/final POST is deleted: zero POSTs must mean full
// group authoring again, not a hidden selection hotspot with no POST row.
[DefaultExecutionOrder(3450)]
public class PostSelectionExitAuthority : MonoBehaviour
{
    private PostAffectorManager postManager;
    private ModelViewer viewer;
    private FieldInfo activeIdField;
    private MethodInfo clearSelectionMethod;
    private bool initialized;
    private int previousActiveId = -1;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<PostSelectionExitAuthority>() != null) return;
        GameObject go = new GameObject("PostSelectionExitAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<PostSelectionExitAuthority>();
    }

    void Update()
    {
        Resolve();
        if (postManager == null || viewer == null || activeIdField == null || clearSelectionMethod == null)
            return;

        int activeId = (int)activeIdField.GetValue(postManager);
        if (!initialized)
        {
            previousActiveId = activeId;
            initialized = true;
            return;
        }

        // A real POST edit session just ended. Use ModelViewer's own teardown path so
        // isSelectionMode, hotspot state, yellow panel, falloff/weight rows and per-card
        // selection weights all return to normal group-authoring state together.
        if (previousActiveId >= 0 && activeId < 0)
            clearSelectionMethod.Invoke(viewer, null);

        previousActiveId = activeId;
    }

    void Resolve()
    {
        if (postManager == null)
        {
            postManager = FindFirstObjectByType<PostAffectorManager>();
            if (postManager != null)
                activeIdField = typeof(PostAffectorManager).GetField("activeId", BindingFlags.Instance | BindingFlags.NonPublic);
        }

        if (viewer == null)
        {
            viewer = FindFirstObjectByType<ModelViewer>();
            if (viewer != null)
                clearSelectionMethod = typeof(ModelViewer).GetMethod("ClearSelectionHotspot", BindingFlags.Instance | BindingFlags.NonPublic);
        }
    }
}
