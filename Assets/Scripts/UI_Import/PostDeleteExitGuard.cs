using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;

// Deleting the currently edited POST modifier must leave local/yellow edit mode completely.
[DefaultExecutionOrder(9200)]
public class PostDeleteExitGuard : MonoBehaviour
{
    private PostAffectorManager posts;
    private ModelViewer viewer;
    private GroomRootStateAuthority roots;

    private FieldInfo activeIdField;
    private FieldInfo activeGroupField;
    private MethodInfo clearSelectionMethod;

    private int previousActiveId = -1;
    private int previousActiveGroup = -1;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<PostDeleteExitGuard>() != null) return;
        GameObject go = new GameObject("PostDeleteExitGuard");
        DontDestroyOnLoad(go);
        go.AddComponent<PostDeleteExitGuard>();
    }

    void Update()
    {
        Resolve();
        if (posts == null || viewer == null || activeIdField == null) return;

        int activeId = (int)activeIdField.GetValue(posts);
        int activeGroup = activeGroupField != null ? (int)activeGroupField.GetValue(posts) : -1;

        // A live POST becoming inactive is the delete/deactivate transition that previously
        // left ModelViewer's local selection state behind.
        if (previousActiveId >= 0 && activeId < 0)
            FullyExitPostEdit(previousActiveGroup >= 0 ? previousActiveGroup : viewer.currentGroupId);

        previousActiveId = activeId;
        previousActiveGroup = activeGroup;
    }

    void Resolve()
    {
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        if (posts == null)
        {
            posts = FindFirstObjectByType<PostAffectorManager>();
            if (posts != null)
            {
                activeIdField = typeof(PostAffectorManager).GetField("activeId", flags);
                activeGroupField = typeof(PostAffectorManager).GetField("activeGroup", flags);
            }
        }

        if (viewer == null)
        {
            viewer = FindFirstObjectByType<ModelViewer>();
            if (viewer != null)
                clearSelectionMethod = typeof(ModelViewer).GetMethod("ClearSelectionHotspot", flags);
        }

        if (roots == null)
            roots = FindFirstObjectByType<GroomRootStateAuthority>();
    }

    void FullyExitPostEdit(int groupId)
    {
        // This is ModelViewer's complete local-edit exit: clears yellow mode, selection-mode
        // flags, falloff/weight rows and per-card selection weights.
        clearSelectionMethod?.Invoke(viewer, null);

        if (groupId >= 0)
        {
            viewer.currentGroupId = groupId;
            roots?.RestoreRootToViewer(groupId);
        }

        viewer.lastPlacedCard = null;

        if (EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(null);
    }
}
