using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

// ModelViewer's original Load Model file picker is EditorUtility-only. In a Windows player
// this authority replaces that startup button callback with the same load operation driven
// by RuntimeFileDialog. The Editor path is intentionally untouched. Load Project is deliberately
// NOT handled here any more - see RuntimeNavigationProjectIO.LoadProjectEnhanced for that.
[DefaultExecutionOrder(10000)]
public class RuntimeBuildLoadAuthority : MonoBehaviour
{
#if !UNITY_EDITOR
    private ModelViewer viewer;
    private Button boundModelButton;
    private float nextBindAttempt;

    private FieldInfo loadedModelField;
    private FieldInfo currentModelPathField;
    private FieldInfo allGroupIdsField;
    private FieldInfo groupNamesField;
    private FieldInfo groupUScalesField;
    private FieldInfo groupVScalesField;
    private FieldInfo groupUOffsetsField;
    private FieldInfo groupVOffsetsField;
    private FieldInfo activeSliderPanelField;
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
        allGroupIdsField = t.GetField("allGroupIds", flags);
        groupNamesField = t.GetField("groupNames", flags);
        groupUScalesField = t.GetField("groupUScales", flags);
        groupVScalesField = t.GetField("groupVScales", flags);
        groupUOffsetsField = t.GetField("groupUOffsets", flags);
        groupVOffsetsField = t.GetField("groupVOffsets", flags);
        activeSliderPanelField = t.GetField("activeSliderPanel", flags);
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

        // loadProjectButton is deliberately NOT bound here any more. RuntimeNavigationProjectIO's
        // LoadProjectEnhanced() is the more complete implementation - it also restores per-group
        // modifiers (clumpers, POST affectors) via ModifierPersistenceBridge, which this script's
        // own LoadProjectAtPath never did. Both scripts used to fight over this same button, and
        // this one happened to win by execution order, which meant projects only ever got the
        // more limited restore in a build. Leaving Load Model here alone since that path is
        // confirmed working and unrelated to the project-load gap.
    }

    void ChooseAndLoadModel()
    {
        string path = RuntimeFileDialog.OpenFile(
            "Select OBJ Model",
            "OBJ Models\0*.obj\0All Files\0*.*\0\0",
            "obj");
        if (string.IsNullOrEmpty(path)) return;

        try { LoadModelAtPath(path); }
        catch (Exception ex) { Debug.LogException(ex); }
    }

    void LoadModelAtPath(string path)
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
    }

    void CentreCameraOn(GameObject model)
    {
        MeshRenderer[] renderers = model.GetComponentsInChildren<MeshRenderer>();
        if (renderers.Length == 0 || viewer.cameraPivot == null) return;
        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
        viewer.cameraPivot.position = bounds.center;
    }
#endif
}
