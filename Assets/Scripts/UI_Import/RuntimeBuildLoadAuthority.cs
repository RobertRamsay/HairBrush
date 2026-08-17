using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

// ModelViewer's original file pickers are EditorUtility-only. In a Windows player this
// authority replaces those two startup button callbacks with the same load operations driven
// by RuntimeFileDialog. The Editor path is intentionally untouched.
[DefaultExecutionOrder(10000)]
public class RuntimeBuildLoadAuthority : MonoBehaviour
{
#if !UNITY_EDITOR
    private ModelViewer viewer;
    private Button boundModelButton;
    private Button boundProjectButton;
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

        if (viewer.loadProjectButton != null && boundProjectButton != viewer.loadProjectButton)
        {
            boundProjectButton = viewer.loadProjectButton;
            boundProjectButton.onClick.RemoveAllListeners();
            boundProjectButton.onClick.AddListener(ChooseAndLoadProject);
        }
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

    void ChooseAndLoadProject()
    {
        string path = RuntimeFileDialog.OpenFile(
            "Open Hair Project",
            "HairBrush Projects\0*.json\0All Files\0*.*\0\0",
            "json");
        if (string.IsNullOrEmpty(path)) return;

        try { LoadProjectAtPath(path); }
        catch (Exception ex) { Debug.LogException(ex); }
    }

    void LoadProjectAtPath(string path)
    {
        string json = File.ReadAllText(path);
        HairProjectSaveData saveData = JsonUtility.FromJson<HairProjectSaveData>(json);
        if (saveData == null)
        {
            Debug.LogError("HairBrush: project file could not be read: " + path);
            return;
        }

        if (!string.IsNullOrEmpty(saveData.modelPath))
        {
            currentModelPathField?.SetValue(viewer, saveData.modelPath);
            GameObject oldModel = loadedModelField?.GetValue(viewer) as GameObject;
            if (oldModel != null) Destroy(oldModel);

            GameObject model = CustomOBJImporter.Load(saveData.modelPath);
            loadedModelField?.SetValue(viewer, model);
            if (model != null)
            {
                model.transform.position = Vector3.zero;
                model.transform.eulerAngles = new Vector3(0f, 180f, 0f);
                CentreCameraOn(model);
            }
        }

        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
            if (card != null) Destroy(card.gameObject);

        viewer.currentLength = saveData.sliderLength;
        viewer.currentWidth = saveData.sliderWidth;
        viewer.currentSegments = saveData.sliderSegments;
        viewer.currentBend = saveData.sliderBend;
        viewer.currentTwist = saveData.sliderTwist;
        viewer.currentEmbedDepth = saveData.sliderEmbedDepth;
        viewer.currentOffsetX = saveData.sliderOffsetX;
        viewer.currentOffsetY = saveData.sliderOffsetY;
        viewer.currentOffsetZ = saveData.sliderOffsetZ;
        viewer.currentUScale = saveData.sliderUScale != 0f ? saveData.sliderUScale : 1f;
        viewer.currentVScale = saveData.sliderVScale != 0f ? saveData.sliderVScale : 1f;
        viewer.currentUOffset = saveData.sliderUOffset;
        viewer.currentVOffset = saveData.sliderVOffset;

        HashSet<int> allGroupIds = allGroupIdsField?.GetValue(viewer) as HashSet<int>;
        Dictionary<int, string> groupNames = groupNamesField?.GetValue(viewer) as Dictionary<int, string>;
        Dictionary<int, float> groupUScales = groupUScalesField?.GetValue(viewer) as Dictionary<int, float>;
        Dictionary<int, float> groupVScales = groupVScalesField?.GetValue(viewer) as Dictionary<int, float>;
        Dictionary<int, float> groupUOffsets = groupUOffsetsField?.GetValue(viewer) as Dictionary<int, float>;
        Dictionary<int, float> groupVOffsets = groupVOffsetsField?.GetValue(viewer) as Dictionary<int, float>;

        allGroupIds?.Clear();
        groupNames?.Clear();
        groupUScales?.Clear();
        groupVScales?.Clear();
        groupUOffsets?.Clear();
        groupVOffsets?.Clear();

        if (saveData.groups != null)
        {
            foreach (GroupSaveData g in saveData.groups)
            {
                if (g == null) continue;
                allGroupIds?.Add(g.groupId);
                if (groupNames != null) groupNames[g.groupId] = g.groupName;
                if (groupUScales != null) groupUScales[g.groupId] = g.uScale != 0f ? g.uScale : 1f;
                if (groupVScales != null) groupVScales[g.groupId] = g.vScale != 0f ? g.vScale : 1f;
                if (groupUOffsets != null) groupUOffsets[g.groupId] = g.uOffset;
                if (groupVOffsets != null) groupVOffsets[g.groupId] = g.vOffset;
            }
        }

        if (allGroupIds != null && allGroupIds.Count == 0)
        {
            allGroupIds.Add(0);
            if (groupNames != null) groupNames[0] = "Group 0 (Default)";
            if (groupUScales != null) groupUScales[0] = 1f;
            if (groupVScales != null) groupVScales[0] = 1f;
            if (groupUOffsets != null) groupUOffsets[0] = 0f;
            if (groupVOffsets != null) groupVOffsets[0] = 0f;
        }

        if (saveData.hairCards != null)
        {
            foreach (HairCardSaveData cData in saveData.hairCards)
            {
                if (cData == null) continue;
                GameObject cardGO = new GameObject("HairCard_Strip", typeof(MeshFilter), typeof(MeshRenderer), typeof(HairCard));
                HairCard card = cardGO.GetComponent<HairCard>();
                card.transform.position = new Vector3(cData.posX, cData.posY, cData.posZ);
                card.transform.rotation = new Quaternion(cData.rotX, cData.rotY, cData.rotZ, cData.rotW);
                card.groupId = cData.groupId;
                float u = cData.uScale != 0f ? cData.uScale : 1f;
                float v = cData.vScale != 0f ? cData.vScale : 1f;
                card.SetParameters(cData.length, cData.width, cData.segments, cData.bendAngle, cData.twistAngle,
                    cData.offsetX, cData.offsetY, cData.offsetZ, cData.embedDepth, 1f, u, v, cData.uOffset, cData.vOffset);
                MeshRenderer renderer = cardGO.GetComponent<MeshRenderer>();
                if (viewer.hairCardMaterial != null) renderer.sharedMaterial = viewer.hairCardMaterial;
            }
        }

        if (viewer.uiContainer != null) viewer.uiContainer.SetActive(false);
        viewer.OnModelLoaded();
        if (activeSliderPanelField?.GetValue(viewer) == null) viewer.BuildRuntimeGroomingUI();
        buildGroupManagementMethod?.Invoke(viewer, null);
        isGroomingModeField?.SetValue(viewer, true);
        Debug.Log("Project loaded successfully from: " + path);
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
