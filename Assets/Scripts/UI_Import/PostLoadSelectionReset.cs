using System.Reflection;
using UnityEngine;

// Project/model replacement can begin while a POST hotspot is selected. Clear that transient
// editor selection before the modifier stack restores, otherwise the visualizer and authoring
// bridge can treat the old hotspot as active for the newly loaded project.
[DefaultExecutionOrder(3200)]
public class PostLoadSelectionReset : MonoBehaviour
{
    private HairProjectSaveData lastPending;
    private ModelViewer viewer;
    private FieldInfo loadedModelField;
    private GameObject lastLoadedModel;
    private bool trackedModel;

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
        if (viewer == null)
        {
            viewer = FindFirstObjectByType<ModelViewer>();
            if (viewer != null)
                loadedModelField = typeof(ModelViewer).GetField("loadedModel", BindingFlags.Instance | BindingFlags.NonPublic);
        }

        if (viewer != null && loadedModelField != null)
        {
            GameObject loaded = loadedModelField.GetValue(viewer) as GameObject;
            if (!trackedModel)
            {
                lastLoadedModel = loaded;
                trackedModel = true;
            }
            else if (loaded != lastLoadedModel)
            {
                lastLoadedModel = loaded;
                ClearTransientPostSelection();
            }
        }

        HairProjectSaveData pending = HairProjectSaveData.PendingModifierRestore;
        if (pending != null && pending != lastPending)
        {
            lastPending = pending;
            ClearTransientPostSelection();
        }
    }

    void ClearTransientPostSelection()
    {
        ModelViewer currentViewer = viewer != null ? viewer : FindFirstObjectByType<ModelViewer>();
        PostAffectorManager posts = FindFirstObjectByType<PostAffectorManager>();
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;

        if (currentViewer != null)
        {
            typeof(ModelViewer).GetField("hasSelectionHotspot", flags)?.SetValue(currentViewer, false);
            typeof(ModelViewer).GetField("selectionHitPoint", flags)?.SetValue(currentViewer, Vector3.zero);
            typeof(ModelViewer).GetField("selectionHitNormal", flags)?.SetValue(currentViewer, Vector3.zero);
        }

        if (posts != null)
        {
            typeof(PostAffectorManager).GetField("activeId", flags)?.SetValue(posts, -1);
            typeof(PostAffectorManager).GetField("activeGroup", flags)?.SetValue(posts, -1);
        }
    }
}
