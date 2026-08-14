using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

[DefaultExecutionOrder(9100)]
public class MaterialEditorBootstrap : MonoBehaviour
{
    private ModelViewer viewer;
    private MaterialEditorManager editor;
    private bool initialised;
    private float nextScan;
    private bool workspaceStateKnown;
    private bool lastWorkspaceVisible;
    private Transform lastWorkspaceCanvas;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Spawn()
    {
        if (FindFirstObjectByType<MaterialEditorBootstrap>() != null) return;
        GameObject go = new GameObject("MaterialEditorBootstrap");
        DontDestroyOnLoad(go);
        go.AddComponent<MaterialEditorBootstrap>();
    }

    private void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + .15f;

        if (viewer == null) viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer == null) return;

        if (!initialised) InitialiseEditor();
        if (editor == null) return;

        GameObject texturePanel = FindNamed("TextureEditorPanel");
        bool textureEditorOpen = texturePanel != null && texturePanel.activeInHierarchy;

        Transform canvas = null;
        if (texturePanel != null) canvas = texturePanel.transform.root;
        else if (viewer.groomingSliderPanelGO != null) canvas = viewer.groomingSliderPanelGO.transform.root;

        if (canvas == null) return;

        // SetWorkspaceVisible refreshes the material/property rows. Calling it on every
        // polling pass destroys and recreates the LOAD buttons under the pointer, which
        // makes them flicker and causes clicks to be lost. Only notify the editor when
        // visibility or the owning canvas actually changes.
        if (!workspaceStateKnown || textureEditorOpen != lastWorkspaceVisible || canvas != lastWorkspaceCanvas)
        {
            editor.SetWorkspaceVisible(textureEditorOpen, canvas);
            workspaceStateKnown = true;
            lastWorkspaceVisible = textureEditorOpen;
            lastWorkspaceCanvas = canvas;
        }
    }

    private void InitialiseEditor()
    {
        editor = viewer.GetComponent<MaterialEditorManager>();
        if (editor == null) editor = viewer.gameObject.AddComponent<MaterialEditorManager>();

        Material template = null;
#if UNITY_EDITOR
        template = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/HairCard_dithSdr.mat");
#endif
        if (template == null) template = viewer.hairCardMaterial;

        editor.Init(viewer, template);
        initialised = true;
        workspaceStateKnown = false;
    }

    private static GameObject FindNamed(string name)
    {
        foreach (Transform t in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (t != null && t.name == name) return t.gameObject;
        return null;
    }
}
