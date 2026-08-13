using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

// Keeps authored UV rectangles and group assignment state aligned with the existing session rules
// without coupling the feature to the central groom reset coordinator.
[DefaultExecutionOrder(6100)]
public class GroupPredeterminedUVLifecycle : MonoBehaviour
{
    private ModelViewer viewer;
    private GroupPredeterminedUVController controller;
    private TextureUVRectWorkspace workspace;
    private FieldInfo loadedModelField;
    private GameObject lastLoadedModel;
    private Button boundResetButton;
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<GroupPredeterminedUVLifecycle>() != null) return;
        GameObject go = new GameObject("GroupPredeterminedUVLifecycle");
        DontDestroyOnLoad(go);
        go.AddComponent<GroupPredeterminedUVLifecycle>();
    }

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + .08f;

        Resolve();
        if (viewer == null || controller == null) return;

        BindResetButton();
        DetectModelSwap();
    }

    void Resolve()
    {
        if (viewer == null)
        {
            viewer = FindFirstObjectByType<ModelViewer>();
            if (viewer != null)
            {
                loadedModelField = typeof(ModelViewer).GetField("loadedModel", BindingFlags.Instance | BindingFlags.NonPublic);
                lastLoadedModel = loadedModelField?.GetValue(viewer) as GameObject;
            }
        }
        if (controller == null) controller = FindFirstObjectByType<GroupPredeterminedUVController>();
        if (workspace == null) workspace = FindFirstObjectByType<TextureUVRectWorkspace>();
    }

    void BindResetButton()
    {
        Button reset = FindObjectsByType<Button>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .LastOrDefault(button => button != null && button.gameObject.name == "ResetButton");
        if (reset == null || reset == boundResetButton) return;

        boundResetButton = reset;
        boundResetButton.onClick.AddListener(() =>
        {
            controller?.ClearAllSettings();
            HairProjectSaveData.PendingGroupUVRestore = null;
            // Groom RESET intentionally keeps authored UV rectangle definitions.
        });
    }

    void DetectModelSwap()
    {
        if (loadedModelField == null) return;
        GameObject current = loadedModelField.GetValue(viewer) as GameObject;
        if (current == null)
        {
            lastLoadedModel = null;
            return;
        }
        if (lastLoadedModel == null)
        {
            lastLoadedModel = current;
            return;
        }
        if (current == lastLoadedModel) return;

        lastLoadedModel = current;

        bool projectRestorePending = HairProjectSaveData.PendingModifierRestore != null ||
                                     HairProjectSaveData.PendingUVRectRestore != null ||
                                     HairProjectSaveData.PendingGroupUVRestore != null ||
                                     CanonicalProjectStateBridge.PendingCanonicalRestore != null;
        if (projectRestorePending) return;

        // New OBJ = new session. Unlike Groom RESET, this clears the texture-space definitions too.
        controller.ClearAllSettings();
        workspace?.ClearDefinitions();
        HairProjectSaveData.PendingGroupUVRestore = null;
        HairProjectSaveData.PendingUVRectRestore = null;
    }
}
