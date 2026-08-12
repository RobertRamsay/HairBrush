using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

// Central lifecycle reset for runtime-created groom/modifier state.
// New OBJ import = brand-new session. RESET = clear all groom/modifier settings for current model.
[DefaultExecutionOrder(4900)]
public class GroomSessionResetCoordinator : MonoBehaviour
{
    private ModelViewer viewer;
    private Button boundLoadButton;
    private Button boundResetButton;
    private GameObject lastKnownLoadedModel;
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<GroomSessionResetCoordinator>() != null) return;
        GameObject go = new GameObject("GroomSessionResetCoordinator");
        DontDestroyOnLoad(go);
        go.AddComponent<GroomSessionResetCoordinator>();
    }

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + 0.08f;

        if (viewer == null)
        {
            viewer = FindFirstObjectByType<ModelViewer>();
            lastKnownLoadedModel = GetLoadedModel();
        }
        if (viewer == null) return;

        BindLoadButton();
        BindResetButton();

        GameObject currentLoaded = GetLoadedModel();
        if (currentLoaded != null && currentLoaded != lastKnownLoadedModel)
        {
            lastKnownLoadedModel = currentLoaded;
            ResetEntireSessionForNewModel();
        }
        else if (currentLoaded == null)
        {
            lastKnownLoadedModel = null;
        }
    }

    void BindLoadButton()
    {
        if (viewer.loadButton == null || boundLoadButton == viewer.loadButton) return;
        boundLoadButton = viewer.loadButton;
        // No reset listener is required here: actual loaded-model instance change is authoritative.
        // This means cancelling the native file dialog leaves the current session untouched.
    }

    void BindResetButton()
    {
        Button reset = FindObjectsByType<Button>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .LastOrDefault(b => b != null && b.gameObject.name == "ResetButton");
        if (reset == null || reset == boundResetButton) return;
        boundResetButton = reset;
        boundResetButton.onClick.AddListener(ResetCurrentGroomSession);
    }

    GameObject GetLoadedModel()
    {
        FieldInfo field = typeof(ModelViewer).GetField("loadedModel", BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(viewer) as GameObject;
    }

    public void ResetCurrentGroomSession()
    {
        if (viewer == null) return;
        ClearModifierManagers();
        ClearSelectionState();
        ResetViewerControlsToDefaults();
        ResetAllCardGroomToDefaults();
        RefreshRuntimeUI();
    }

    void ResetEntireSessionForNewModel()
    {
        ClearModifierManagers();
        ClearSelectionState();

        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
            if (card != null) Destroy(card.gameObject);

        ResetViewerGroupsToDefault();
        ResetViewerControlsToDefaults();
        CleanupDuplicateRuntimePanels();
        RefreshRuntimeUI();
    }

    void ClearModifierManagers()
    {
        GroomVarianceController variance = FindFirstObjectByType<GroomVarianceController>();
        if (variance != null) variance.ClearSavedSettings();

        PostAffectorManager post = FindFirstObjectByType<PostAffectorManager>();
        if (post != null)
        {
            ClearDictionaryField(post, "groups");
            ClearDictionaryField(post, "cardStates");
            SetField(post, "activeId", -1);
            SetField(post, "activeGroup", -1);
            SetField(post, "nextId", 1);
            SetField(post, "nextUIScan", 0f);
        }

        ClumpLayerManager clump = FindFirstObjectByType<ClumpLayerManager>();
        if (clump != null)
        {
            ClearDictionaryField(clump, "layers");
            ClearDictionaryField(clump, "expandedGroups");
            SetField(clump, "visualGroupId", -1);
            SetField(clump, "nextUIScanTime", 0f);
        }

        HairProjectSaveData.PendingModifierRestore = null;
        CanonicalProjectStateBridge.PendingCanonicalRestore = null;

        foreach (RectTransform row in FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (row == null) continue;
            if (row.name.StartsWith("PostAffector_", StringComparison.Ordinal) ||
                row.name.StartsWith("ClumpModifier_", StringComparison.Ordinal))
                Destroy(row.gameObject);
        }

        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
            if (card != null) card.ClearClumpModifier();
    }

    void ClearSelectionState()
    {
        SetField(viewer, "hasSelectionHotspot", false);
        SetField(viewer, "isSelectionMode", false);
        SetField(viewer, "lastPlacedCard", null);
        viewer.selectionStrength = 0.25f;
        viewer.brushRadius = 0.2f;
        viewer.brushFalloffDistance = 0.05f;
    }

    void ResetViewerGroupsToDefault()
    {
        HashSet<int> ids = GetField<HashSet<int>>(viewer, "allGroupIds");
        if (ids != null) { ids.Clear(); ids.Add(0); }

        Dictionary<int,string> names = GetField<Dictionary<int,string>>(viewer, "groupNames");
        if (names != null) { names.Clear(); names[0] = "Group 0 (Default)"; }

        Dictionary<int,bool> solo = GetField<Dictionary<int,bool>>(viewer, "groupSoloState");
        solo?.Clear();

        ResetFloatDictionary("groupUScales", 1f);
        ResetFloatDictionary("groupVScales", 1f);
        ResetFloatDictionary("groupUOffsets", 0f);
        ResetFloatDictionary("groupVOffsets", 0f);
        viewer.currentGroupId = 0;
    }

    void ResetFloatDictionary(string fieldName, float value)
    {
        Dictionary<int,float> dict = GetField<Dictionary<int,float>>(viewer, fieldName);
        if (dict == null) return;
        dict.Clear();
        dict[0] = value;
    }

    void ResetViewerControlsToDefaults()
    {
        viewer.currentLength = 0.2f;
        viewer.currentWidth = 0.01f;
        viewer.currentSegments = 12;
        viewer.currentBend = 0f;
        viewer.currentTwist = 0f;
        viewer.currentEmbedDepth = 0.002f;
        viewer.currentOffsetX = 0f;
        viewer.currentOffsetY = 0f;
        viewer.currentOffsetZ = 0f;
        viewer.currentUScale = 1f;
        viewer.currentVScale = 1f;
        viewer.currentUOffset = 0f;
        viewer.currentVOffset = 0f;

        if (viewer.groomingSliderPanelGO == null) return;

        foreach (Slider slider in viewer.groomingSliderPanelGO.GetComponentsInChildren<Slider>(true))
        {
            if (slider == null) continue;
            string n = slider.gameObject.name;
            if (n == "Length_Slider") slider.SetValueWithoutNotify(.2f);
            else if (n == "Width_Slider") slider.SetValueWithoutNotify(.01f);
            else if (n == "Segments_Slider") slider.SetValueWithoutNotify(12f);
            else if (n == "Bend Angle_Slider") slider.SetValueWithoutNotify(0f);
            else if (n == "Twist Angle_Slider") slider.SetValueWithoutNotify(0f);
            else if (n == "Embed Depth_Slider") slider.SetValueWithoutNotify(.002f);
            else if (n == "Offset X_Slider" || n == "Offset Y_Slider" || n == "Offset Z_Slider" ||
                     n == "Angle X_Slider" || n == "Angle Y_Slider" || n == "Angle Z_Slider") slider.SetValueWithoutNotify(0f);
            else if (n == "U Scale_Slider" || n == "V Scale_Slider") slider.SetValueWithoutNotify(1f);
            else if (n == "U Offset_Slider" || n == "V Offset_Slider") slider.SetValueWithoutNotify(0f);
            else if (n == "VarianceSlider") slider.SetValueWithoutNotify(0f);
            slider.interactable = true;
        }

        foreach (TMPro.TMP_InputField input in viewer.groomingSliderPanelGO.GetComponentsInChildren<TMPro.TMP_InputField>(true))
        {
            if (input == null) continue;
            if (input.gameObject.name == "SeedInput") input.SetTextWithoutNotify("0");
            input.interactable = true;
        }
    }

    void ResetAllCardGroomToDefaults()
    {
        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
        {
            if (card == null) continue;
            card.SetSelectionWeight(0f);
            card.ClearClumpModifier();
            card.SetParameters(.2f, .01f, 12, 0f, 0f, 0f, 0f, 0f, .002f, 1f, 1f, 1f, 0f, 0f);
        }
    }

    void CleanupDuplicateRuntimePanels()
    {
        GameObject keepGroom = viewer.groomingSliderPanelGO;
        foreach (RectTransform r in FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (r == null) continue;
            if (r.name == "GroomingPanel" && r.gameObject != keepGroom)
                Destroy(r.gameObject);
        }

        Transform liveContent = GetField<Transform>(viewer, "groupListContentTransform");
        GameObject keepGroupPanel = null;
        if (liveContent != null)
        {
            Transform p = liveContent;
            while (p != null && p.name != "GroupManagerPanel") p = p.parent;
            if (p != null) keepGroupPanel = p.gameObject;
        }

        foreach (RectTransform r in FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (r != null && r.name == "GroupManagerPanel" && r.gameObject != keepGroupPanel)
                Destroy(r.gameObject);
        }
    }

    void RefreshRuntimeUI()
    {
        InvokePrivate(viewer, "RefreshGroupListUI");
        PostAffectorManager post = FindFirstObjectByType<PostAffectorManager>();
        if (post != null) SetField(post, "nextUIScan", 0f);
        ClumpLayerManager clump = FindFirstObjectByType<ClumpLayerManager>();
        if (clump != null) SetField(clump, "nextUIScanTime", 0f);
    }

    static void ClearDictionaryField(object owner, string fieldName)
    {
        if (owner == null) return;
        FieldInfo f = owner.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (f?.GetValue(owner) is IDictionary dict) dict.Clear();
    }

    static T GetField<T>(object owner, string fieldName) where T : class
    {
        if (owner == null) return null;
        FieldInfo f = owner.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        return f?.GetValue(owner) as T;
    }

    static void SetField(object owner, string fieldName, object value)
    {
        if (owner == null) return;
        FieldInfo f = owner.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (f != null) f.SetValue(owner, value);
    }

    static void InvokePrivate(object owner, string methodName)
    {
        if (owner == null) return;
        MethodInfo m = owner.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        m?.Invoke(owner, null);
    }
}
