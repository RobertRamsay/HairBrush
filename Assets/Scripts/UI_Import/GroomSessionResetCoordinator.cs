using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Central lifecycle reset for runtime-created groom/modifier state.
// New OBJ import is still a brand-new session, but the visible grooming RESET button is
// deliberately scoped to the thing currently being authored:
//   active POST -> neutralize only that POST's authored effect
//   no POSTs    -> reset only the current Hair Group's groom
// A group that contains POSTs but has no POST selected is locked, so RESET is disabled there.
[DefaultExecutionOrder(4900)]
public class GroomSessionResetCoordinator : MonoBehaviour
{
    private static readonly string[] VarianceChannels =
        { "Length", "Width", "Bend", "Twist", "AngleX", "AngleY", "AngleZ", "CurlFrequency", "CurlDiameter" };
    private static readonly string[] VarianceRows =
        { "Length_VarianceRow", "Width_VarianceRow", "Bend_VarianceRow", "Twist_VarianceRow", "Angle X_VarianceRow", "Angle Y_VarianceRow", "Angle Z_VarianceRow" };

    private ModelViewer viewer;
    private Button boundLoadButton;
    private Button boundLoadProjectButton;
    private Button boundResetButton;
    private GameObject lastKnownLoadedModel;
    private bool projectLoadJustCompleted;
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
        BindLoadProjectButton();
        BindResetButton();
        MaintainResetAvailability();

        GameObject currentLoaded = GetLoadedModel();
        if (currentLoaded != null && currentLoaded != lastKnownLoadedModel)
        {
            lastKnownLoadedModel = currentLoaded;
            if (!projectLoadJustCompleted)
                ResetEntireSessionForNewModel();
        }
        else if (currentLoaded == null)
        {
            lastKnownLoadedModel = null;
        }

        // One-shot marker only. A cancelled project dialog must not suppress the next OBJ reset.
        projectLoadJustCompleted = false;
    }

    void BindLoadButton()
    {
        if (viewer.loadButton == null || boundLoadButton == viewer.loadButton) return;
        boundLoadButton = viewer.loadButton;
    }

    void BindLoadProjectButton()
    {
        if (viewer.loadProjectButton == null || boundLoadProjectButton == viewer.loadProjectButton) return;
        boundLoadProjectButton = viewer.loadProjectButton;
        boundLoadProjectButton.onClick.AddListener(() => projectLoadJustCompleted = true);
    }

    void BindResetButton()
    {
        // Bind only the grooming-panel RESET. Texture/material editors can have their own
        // ResetButton names and must never inherit groom/session reset behaviour.
        if (viewer.groomingSliderPanelGO == null) return;
        Button reset = viewer.groomingSliderPanelGO.GetComponentsInChildren<Button>(true)
            .FirstOrDefault(b => b != null && b.gameObject.name == "ResetButton");
        if (reset == null || reset == boundResetButton) return;

        boundResetButton = reset;

        // ModelViewer originally wires ResetAllSliders here and the old coordinator added a
        // second whole-session listener. Own this one button explicitly so one click has one
        // scoped meaning and cannot also mutate the group underneath an active POST.
        boundResetButton.onClick.RemoveAllListeners();
        boundResetButton.onClick.AddListener(ResetCurrentAffector);
    }

    void MaintainResetAvailability()
    {
        if (boundResetButton == null) return;
        bool activePost = TryGetActivePost(out _);
        bool groupHasPost = GroupHasPost(viewer.currentGroupId);
        boundResetButton.interactable = activePost || !groupHasPost;
    }

    GameObject GetLoadedModel()
    {
        FieldInfo field = typeof(ModelViewer).GetField("loadedModel", BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(viewer) as GameObject;
    }

    public void ResetCurrentAffector()
    {
        if (viewer == null) return;

        if (TryGetActivePost(out PostAffectorManager.PostAffector activePost))
        {
            ResetActivePost(activePost);
            RefreshRuntimeUI();
            return;
        }

        // A group root that already contains POSTs is intentionally read-only. With no POST
        // selected there is no current editable affector, so RESET must not reach underneath
        // those downstream modifiers and alter the group behind their back.
        int groupId = viewer.currentGroupId;
        if (GroupHasPost(groupId)) return;

        ResetCurrentGroup(groupId);
        RefreshRuntimeUI();
    }

    // Compatibility entry point for any older caller. The UI no longer means "whole session".
    public void ResetCurrentGroomSession()
    {
        ResetCurrentAffector();
    }

    void ResetActivePost(PostAffectorManager.PostAffector post)
    {
        if (post == null) return;

        // A POST is an additive downstream affector. Its neutral/default authored state is a
        // zero delta from the baseline it captured when created, not the application's hard
        // group defaults. Preserve position, radius, falloff and weight; only its groom effect
        // is reset.
        post.delta = new PostAffectorManager.ControlState();
        WriteViewerControls(post.baseline);
        SyncCoreSliderUI(post.baseline);

        ResetPostLocalVariance(post.id);
        ResetPostLocalPredeterminedUV(post.id);

        PostAffectorManager manager = FindFirstObjectByType<PostAffectorManager>();
        InvokePrivate(manager, "ApplyAll");
    }

    void ResetCurrentGroup(int groupId)
    {
        GroomRootStateAuthority.RootState defaults = DefaultRootState();
        WriteViewerRoot(defaults);
        StoreRootState(groupId, defaults);
        StoreAdjustableUVDefaults(groupId);

        // Variance and shape curves are authored by the group root, so they are part of a
        // true group reset. UV routing mode/range itself is metadata and is intentionally
        // preserved; in ADJ mode the base values return to 1/1/0/0, while PRE keeps its range.
        GroomVarianceController variance = FindFirstObjectByType<GroomVarianceController>();
        if (variance != null)
            variance.ImportGroupSettings(groupId, ZeroVariance());

        GroomShapeCurveRegistry.Reset(groupId, GroomShapeCurveChannel.Bend);
        GroomShapeCurveRegistry.Reset(groupId, GroomShapeCurveChannel.X);
        GroomShapeCurveRegistry.Reset(groupId, GroomShapeCurveChannel.Y);
        GroomShapeCurveRegistry.Reset(groupId, GroomShapeCurveChannel.Z);
        GroomShapeCurveRegistry.Reset(groupId, GroomShapeCurveChannel.CurlFrequency);
        GroomShapeCurveRegistry.Reset(groupId, GroomShapeCurveChannel.CurlDiameter);
        GroomShapeCurveRegistry.Reset(groupId, GroomShapeCurveChannel.SegmentDensity);
        GroupSidednessAuthority.Forget(groupId);

        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
        {
            if (card == null || card.groupId != groupId) continue;
            card.SetSelectionWeight(0f);
            card.SetParameters(.2f, .01f, 12, 0f, 0f, 0f, 0f, 0f, .002f, 1f, 1f, 1f, 0f, 0f, 0f, 0f);
        }

        SyncCoreSliderUI(ToControlState(defaults));
        GroomShapeCurveRegistry.RefreshGroup(groupId);
        GroomShapeCurveEditor editor = FindFirstObjectByType<GroomShapeCurveEditor>();
        if (editor != null) editor.RefreshAll();
    }

    bool TryGetActivePost(out PostAffectorManager.PostAffector active)
    {
        active = null;
        PostAffectorManager manager = FindFirstObjectByType<PostAffectorManager>();
        if (manager == null || viewer == null) return false;

        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        FieldInfo selectedField = typeof(ModelViewer).GetField("hasSelectionHotspot", flags);
        bool selected = selectedField != null && selectedField.GetValue(viewer) is bool b && b;
        if (!selected) return false;

        FieldInfo activeIdField = typeof(PostAffectorManager).GetField("activeId", flags);
        FieldInfo activeGroupField = typeof(PostAffectorManager).GetField("activeGroup", flags);
        FieldInfo groupsField = typeof(PostAffectorManager).GetField("groups", flags);
        int activeId = activeIdField?.GetValue(manager) is int id ? id : -1;
        int activeGroup = activeGroupField?.GetValue(manager) is int gid ? gid : -1;
        if (activeId < 0 || activeGroup < 0) return false;

        Dictionary<int, List<PostAffectorManager.PostAffector>> groups =
            groupsField?.GetValue(manager) as Dictionary<int, List<PostAffectorManager.PostAffector>>;
        if (groups == null || !groups.TryGetValue(activeGroup, out List<PostAffectorManager.PostAffector> list) || list == null)
            return false;

        active = list.FirstOrDefault(p => p != null && p.id == activeId);
        return active != null;
    }

    bool GroupHasPost(int groupId)
    {
        PostAffectorManager manager = FindFirstObjectByType<PostAffectorManager>();
        if (manager == null) return false;
        List<PostAffectorSaveData> posts = manager.ExportGroup(groupId);
        return posts != null && posts.Count > 0;
    }

    void ResetPostLocalVariance(int postId)
    {
        PostVarianceAffectorBridge bridge = FindFirstObjectByType<PostVarianceAffectorBridge>();
        if (bridge != null)
        {
            FieldInfo field = typeof(PostVarianceAffectorBridge).GetField("localByPost", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.GetValue(bridge) is Dictionary<int, List<VarianceChannelSaveData>> local)
                local[postId] = ZeroVariance();
        }

        // The bridge reads the visible variance rows back into its POST record every Update,
        // so zero the UI without firing the normal group-variance listeners as part of the
        // same operation.
        if (viewer.groomingSliderPanelGO == null) return;
        Transform panel = viewer.groomingSliderPanelGO.transform;
        for (int i = 0; i < VarianceRows.Length; i++)
        {
            Transform row = panel.Find(VarianceRows[i]);
            if (row == null) continue;
            Slider slider = row.GetComponentInChildren<Slider>(true);
            TMP_InputField seed = row.GetComponentInChildren<TMP_InputField>(true);
            if (slider != null) slider.SetValueWithoutNotify(0f);
            if (seed != null) seed.SetTextWithoutNotify("0");

            TextMeshProUGUI label = row.GetComponentsInChildren<TextMeshProUGUI>(true)
                .FirstOrDefault(t => t != null && t.text.StartsWith("VAR", StringComparison.Ordinal));
            if (label != null)
            {
                bool linear = i == 0 || i == 1;
                label.text = linear ? "VAR ± 0.000" : "VAR ± 0.0°";
            }
        }
    }

    void ResetPostLocalPredeterminedUV(int postId)
    {
        PostPredeterminedUVAuthority authority = FindFirstObjectByType<PostPredeterminedUVAuthority>();
        if (authority == null) return;
        FieldInfo field = typeof(PostPredeterminedUVAuthority).GetField("byPost", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(authority) is IDictionary local)
            local.Remove(postId);
    }

    static List<VarianceChannelSaveData> ZeroVariance()
    {
        return VarianceChannels
            .Select(channel => new VarianceChannelSaveData { channel = channel, amount = 0f, seed = 0 })
            .ToList();
    }

    GroomRootStateAuthority.RootState DefaultRootState()
    {
        return new GroomRootStateAuthority.RootState
        {
            length = .2f,
            width = .01f,
            segments = 12,
            bend = 0f,
            twist = 0f,
            depth = .002f,
            x = 0f,
            y = 0f,
            z = 0f,
            uScale = 1f,
            vScale = 1f,
            uOffset = 0f,
            vOffset = 0f,
            curlFrequency = 0f,
            curlDiameter = 0f
        };
    }

    void WriteViewerRoot(GroomRootStateAuthority.RootState s)
    {
        viewer.currentLength = s.length;
        viewer.currentWidth = s.width;
        viewer.currentSegments = s.segments;
        viewer.currentBend = s.bend;
        viewer.currentTwist = s.twist;
        viewer.currentEmbedDepth = s.depth;
        viewer.currentOffsetX = s.x;
        viewer.currentOffsetY = s.y;
        viewer.currentOffsetZ = s.z;
        viewer.currentUScale = s.uScale;
        viewer.currentVScale = s.vScale;
        viewer.currentUOffset = s.uOffset;
        viewer.currentVOffset = s.vOffset;
        viewer.currentCurlFrequency = s.curlFrequency;
        viewer.currentCurlDiameter = s.curlDiameter;
    }

    void WriteViewerControls(PostAffectorManager.ControlState s)
    {
        viewer.currentLength = s.length;
        viewer.currentWidth = s.width;
        viewer.currentSegments = Mathf.RoundToInt(s.segments);
        viewer.currentBend = s.bend;
        viewer.currentTwist = s.twist;
        viewer.currentEmbedDepth = s.depth;
        viewer.currentOffsetX = s.x;
        viewer.currentOffsetY = s.y;
        viewer.currentOffsetZ = s.z;
        viewer.currentUScale = s.uScale;
        viewer.currentVScale = s.vScale;
        viewer.currentUOffset = s.uOffset;
        viewer.currentVOffset = s.vOffset;
        viewer.currentCurlFrequency = s.curlFrequency;
        viewer.currentCurlDiameter = s.curlDiameter;
    }

    static PostAffectorManager.ControlState ToControlState(GroomRootStateAuthority.RootState s)
    {
        return new PostAffectorManager.ControlState
        {
            length = s.length,
            width = s.width,
            segments = s.segments,
            bend = s.bend,
            twist = s.twist,
            depth = s.depth,
            x = s.x,
            y = s.y,
            z = s.z,
            uScale = s.uScale,
            vScale = s.vScale,
            uOffset = s.uOffset,
            vOffset = s.vOffset,
            curlFrequency = s.curlFrequency,
            curlDiameter = s.curlDiameter
        };
    }

    void StoreRootState(int groupId, GroomRootStateAuthority.RootState state)
    {
        GroomRootStateAuthority roots = FindFirstObjectByType<GroomRootStateAuthority>();
        if (roots == null) return;
        FieldInfo field = typeof(GroomRootStateAuthority).GetField("roots", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(roots) is Dictionary<int, GroomRootStateAuthority.RootState> states)
            states[groupId] = state;
    }

    void StoreAdjustableUVDefaults(int groupId)
    {
        SetFloatDictionaryValue("groupUScales", groupId, 1f);
        SetFloatDictionaryValue("groupVScales", groupId, 1f);
        SetFloatDictionaryValue("groupUOffsets", groupId, 0f);
        SetFloatDictionaryValue("groupVOffsets", groupId, 0f);
    }

    void SetFloatDictionaryValue(string fieldName, int groupId, float value)
    {
        Dictionary<int, float> dict = GetField<Dictionary<int, float>>(viewer, fieldName);
        if (dict != null) dict[groupId] = value;
    }

    void SyncCoreSliderUI(PostAffectorManager.ControlState s)
    {
        SetCoreSlider(new[] { "Length_Slider" }, "Length", s.length);
        SetCoreSlider(new[] { "Width_Slider" }, "Width", s.width);
        SetCoreSlider(new[] { "Segments_Slider" }, "Segments", s.segments);
        SetCoreSlider(new[] { "Bend Angle_Slider" }, "Bend Angle", s.bend);
        SetCoreSlider(new[] { "Twist Angle_Slider" }, "Twist Angle", s.twist);
        SetCoreSlider(new[] { "Embed Depth_Slider" }, "Embed Depth", s.depth);
        SetCoreSlider(new[] { "Angle X_Slider", "Offset X_Slider" }, "Angle X", s.x);
        SetCoreSlider(new[] { "Angle Y_Slider", "Offset Y_Slider" }, "Angle Y", s.y);
        SetCoreSlider(new[] { "Angle Z_Slider", "Offset Z_Slider" }, "Angle Z", s.z);
        SetCoreSlider(new[] { "U Scale_Slider" }, "U Scale", s.uScale);
        SetCoreSlider(new[] { "V Scale_Slider" }, "V Scale", s.vScale);
        SetCoreSlider(new[] { "U Offset_Slider" }, "U Offset", s.uOffset);
        SetCoreSlider(new[] { "V Offset_Slider" }, "V Offset", s.vOffset);
        // ControlState (POST-level) has no curl fields - curl lives only at the root, so these
        // read straight from the viewer instead, which WriteViewerRoot has already reset by the
        // time this runs. Without this the curl sliders would keep showing their pre-reset value
        // even though the underlying state was correctly zeroed.
        SetCoreSlider(new[] { "Curl Frequency_Slider" }, "Curl Frequency", viewer.currentCurlFrequency);
        SetCoreSlider(new[] { "Curl Diameter_Slider" }, "Curl Diameter", viewer.currentCurlDiameter);
    }

    void SetCoreSlider(string[] names, string labelPrefix, float value)
    {
        if (viewer.groomingSliderPanelGO == null) return;
        Slider slider = viewer.groomingSliderPanelGO.GetComponentsInChildren<Slider>(true)
            .FirstOrDefault(candidate => candidate != null && names.Contains(candidate.gameObject.name));
        if (slider == null) return;
        slider.SetValueWithoutNotify(value);

        Transform row = slider.transform.parent;
        TextMeshProUGUI label = row != null ? row.GetComponentInChildren<TextMeshProUGUI>(true) : null;
        if (label != null) label.text = labelPrefix + ": " + value.ToString("F3");
    }

    void ResetEntireSessionForNewModel()
    {
        ClearModifierManagers();
        ClearSelectionState();

        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
            if (card != null) Destroy(card.gameObject);

        ResetViewerGroupsToDefault();
        ResetViewerControlsToDefaults();
        ResetRootAuthority();
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

        HairProjectSaveData.PendingModifierRestore = null;
        CanonicalProjectStateBridge.PendingCanonicalRestore = null;

        foreach (RectTransform row in FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (row != null && row.name.StartsWith("PostAffector_", StringComparison.Ordinal))
                Destroy(row.gameObject);
        }

        // HairCard still knows how to clear the old deformation flag so a hot-reloaded
        // editor session cannot retain a stale clump result from an earlier build.
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
        if (names != null) { names.Clear(); names[0] = string.Empty; }

        Dictionary<int,bool> solo = GetField<Dictionary<int,bool>>(viewer, "groupSoloState");
        solo?.Clear();

        // groupSoloState is only a mirror now - GroupSoloVisibilityAuthority holds the real
        // solo set and owns renderer enablement, so clearing the mirror alone would leave
        // the session reset with a live SOLO and no lit button to switch it off.
        GroupSoloVisibilityAuthority.ClearAll();

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
        viewer.currentCurlFrequency = 0f;
        viewer.currentCurlDiameter = 0f;

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
            else if (n == "Curl Frequency_Slider" || n == "Curl Diameter_Slider") slider.SetValueWithoutNotify(0f);
            else if (n == "VarianceSlider") slider.SetValueWithoutNotify(0f);
            slider.interactable = true;
        }

        foreach (TMP_InputField input in viewer.groomingSliderPanelGO.GetComponentsInChildren<TMP_InputField>(true))
        {
            if (input == null) continue;
            if (input.gameObject.name == "SeedInput") input.SetTextWithoutNotify("0");
            input.interactable = true;
        }
    }

    void ResetRootAuthority()
    {
        GroomRootStateAuthority roots = FindFirstObjectByType<GroomRootStateAuthority>();
        roots?.ClearStoredRoots();
    }

    void CleanupDuplicateRuntimePanels()
    {
        GameObject keepGroom = viewer.groomingSliderPanelGO;
        foreach (RectTransform r in FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (r != null && r.name == "GroomingPanel" && r.gameObject != keepGroom)
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
