using System;
using System.Collections;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

#if UNITY_EDITOR
using UnityEditor;
#endif

// ModelViewer's original Load Model picker is EditorUtility-only. This authority owns the
// startup Load Model button in both Editor play mode and Windows builds so import behaviour is
// identical: load/display the OBJ with HairBrush's grey material first, then offer an optional
// albedo on the following frame. Load Project remains owned by RuntimeNavigationProjectIO.
[DefaultExecutionOrder(10000)]
public class RuntimeBuildLoadAuthority : MonoBehaviour
{
    private ModelViewer viewer;
    private Button boundModelButton;
    private float nextBindAttempt;

    private FieldInfo loadedModelField;
    private FieldInfo currentModelPathField;
    private FieldInfo isGroomingModeField;
    private MethodInfo buildGroupManagementMethod;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<RuntimeBuildLoadAuthority>() != null) return;
        GameObject go = new GameObject("RuntimeBuildLoadAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<RuntimeBuildLoadAuthority>();
    }

    void Update()
    {
        if (Time.unscaledTime < nextBindAttempt) return;
        nextBindAttempt = Time.unscaledTime + .25f;
        Resolve();
        BindButtons();
    }

    void Resolve()
    {
        if (viewer != null) return;
        viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer == null) return;

        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Type t = typeof(ModelViewer);
        loadedModelField = t.GetField("loadedModel", flags);
        currentModelPathField = t.GetField("currentModelPath", flags);
        isGroomingModeField = t.GetField("isGroomingMode", flags);
        buildGroupManagementMethod = t.GetMethod("BuildGroupManagementUI", flags);
    }

    void BindButtons()
    {
        if (viewer == null) return;

        if (viewer.loadButton != null && boundModelButton != viewer.loadButton)
        {
            boundModelButton = viewer.loadButton;
            boundModelButton.onClick.RemoveAllListeners();
            boundModelButton.onClick.AddListener(ChooseAndLoadModel);
        }

        // loadProjectButton is deliberately not bound here. RuntimeNavigationProjectIO owns the
        // complete project restore path, including per-group modifiers.
    }

    void ChooseAndLoadModel()
    {
        string path;
#if UNITY_EDITOR
        path = EditorUtility.OpenFilePanel("Select OBJ Model", "", "obj");
#else
        path = RuntimeFileDialog.OpenFile(
            "Select OBJ Model",
            "OBJ Models\0*.obj\0All Files\0*.*\0\0",
            "obj");
#endif
        if (string.IsNullOrEmpty(path)) return;

        try { LoadModelAtPath(path); }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            StatusToast.Show("HairBrush could not import that OBJ model.", true);
        }
    }

    public void LoadModelAtPath(string path)
    {
        currentModelPathField?.SetValue(viewer, path);
        GameObject oldModel = loadedModelField?.GetValue(viewer) as GameObject;
        if (oldModel != null) Destroy(oldModel);

        GameObject model = CustomOBJImporter.Load(path);
        loadedModelField?.SetValue(viewer, model);
        if (model == null) return;

        model.transform.position = Vector3.zero;
        model.transform.eulerAngles = new Vector3(0f, 180f, 0f);
        CentreCameraOn(model);

        if (viewer.uiContainer != null) viewer.uiContainer.SetActive(false);
        viewer.OnModelLoaded();
        viewer.BuildRuntimeGroomingUI();
        buildGroupManagementMethod?.Invoke(viewer, null);
        isGroomingModeField?.SetValue(viewer, true);

        // The model is fully installed with its grey material before this coroutine starts.
        // Waiting one frame lets the user actually see the successful import before a second
        // native dialog appears.
        StartCoroutine(PromptForOptionalAlbedo(model));
    }

    IEnumerator PromptForOptionalAlbedo(GameObject model)
    {
        yield return null;
        if (model == null) yield break;

        bool wantsAlbedo;
#if UNITY_EDITOR
        wantsAlbedo = EditorUtility.DisplayDialog(
            "Optional Albedo",
            "Would you like to add an albedo texture to this head?\n\nChoose Albedo will apply a PNG or JPEG to the imported head. Skip keeps the HairBrush grey material.",
            "Choose Albedo",
            "Skip");
#else
        wantsAlbedo = RuntimeFileDialog.ConfirmOptionalAlbedo();
#endif

        if (!wantsAlbedo)
        {
            StatusToast.Show("Head imported with the default grey material.");
            yield break;
        }

        if (!ImportedHeadAppearance.HasUsableUV0(model))
        {
            StatusToast.Show("This mesh has no UV coordinates, so an albedo cannot be displayed. Keeping the grey head material.", true);
            yield break;
        }

        string texturePath;
#if UNITY_EDITOR
        texturePath = EditorUtility.OpenFilePanelWithFilters(
            "Select Albedo Texture",
            "",
            new[] { "Image files", "png,jpg,jpeg", "All files", "*" });
#else
        texturePath = RuntimeFileDialog.OpenFile(
            "Select Albedo Texture",
            "Image Files\0*.png;*.jpg;*.jpeg\0PNG\0*.png\0JPEG\0*.jpg;*.jpeg\0All Files\0*.*\0\0");
#endif

        // Cancelling the texture picker is an intentional Skip, not an import error.
        if (string.IsNullOrEmpty(texturePath))
        {
            StatusToast.Show("No albedo selected; using the default grey material.");
            yield break;
        }

        ImportedHeadAppearance.TryApplyAlbedo(model, texturePath);
    }

    void CentreCameraOn(GameObject model)
    {
        Renderer[] renderers = model.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0 || viewer.cameraPivot == null) return;
        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
        viewer.cameraPivot.position = bounds.center;
    }
}
