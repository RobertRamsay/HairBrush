using System.Reflection;
using UnityEngine;

// Project deserialization can begin while a POST hotspot is selected. Clear that transient
// editor selection before the modifier stack restores, otherwise the visualizer and authoring
// bridge can treat the old hotspot as active for the newly loaded project.
[DefaultExecutionOrder(3200)]
public class PostLoadSelectionReset : MonoBehaviour
{
    private HairProjectSaveData lastPending;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<PostLoadSelectionReset>() != null) return;
        GameObject go = new GameObject("PostLoadSelectionReset");
        DontDestroyOnLoad(go);
        go.AddComponent<PostLoadSelectionReset>();
    }

    void Update()
    {
        HairProjectSaveData pending = HairProjectSaveData.PendingModifierRestore;
        if (pending == null || pending == lastPending) return;
        lastPending = pending;
        ClearTransientPostSelection();
    }

    void ClearTransientPostSelection()
    {
        ModelViewer viewer = FindFirstObjectByType<ModelViewer>();
        PostAffectorManager posts = FindFirstObjectByType<PostAffectorManager>();
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;

        if (viewer != null)
        {
            typeof(ModelViewer).GetField("hasSelectionHotspot", flags)?.SetValue(viewer, false);
            typeof(ModelViewer).GetField("selectionHitPoint", flags)?.SetValue(viewer, Vector3.zero);
            typeof(ModelViewer).GetField("selectionHitNormal", flags)?.SetValue(viewer, Vector3.zero);
        }

        if (posts != null)
        {
            typeof(PostAffectorManager).GetField("activeId", flags)?.SetValue(posts, -1);
            typeof(PostAffectorManager).GetField("activeGroup", flags)?.SetValue(posts, -1);
        }
    }
}
