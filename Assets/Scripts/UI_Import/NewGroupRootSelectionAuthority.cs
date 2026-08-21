using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;

// A freshly-created Hair Group always starts in root/group authoring context.
// This covers every creation path (the + GROUP button and stroke/dialog group creation):
// leave POST/CLUMPER editing first, purge any recycled group-ID state, then select the
// new group's normal root controls. Empty + GROUP groups also reset every groom slider.
[DefaultExecutionOrder(5270)]
public class NewGroupRootSelectionAuthority : MonoBehaviour
{
    private ModelViewer viewer;
    private PostAffectorManager posts;
    private GroupClumperManager clumpers;
    private GroomRootStateAuthority rootState;
    private GroomVarianceController variance;
    private GroupPredeterminedUVController uvRouting;
    private GroomShapeCurveAuthority curves;

    private FieldInfo allGroupIdsField;
    private MethodInfo selectGroupMethod;
    private MethodInfo clearSelectionMethod;
    private MethodInfo resetAllSlidersMethod;

    private FieldInfo postActiveIdField;
    private FieldInfo postActiveGroupField;
    private FieldInfo postGroupsField;

    private FieldInfo clumperSelectedGroupField;
    private FieldInfo clumperSelectedIdField;
    private FieldInfo clumperGroupsField;
    private MethodInfo clumperDestroyControlsMethod;

    private FieldInfo rootStatesField;
    private FieldInfo varianceSettingsField;
    private FieldInfo varianceLastGroupField;
    private FieldInfo uvSettingsField;
    private MethodInfo closeCurvePopupMethod;

    private readonly HashSet<int> previousGroups = new HashSet<int>();
    private bool initialized;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<NewGroupRootSelectionAuthority>() != null) return;
        GameObject go = new GameObject("NewGroupRootSelectionAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<NewGroupRootSelectionAuthority>();
    }

    void Update()
    {
        Resolve();
        if (viewer == null || allGroupIdsField == null) return;

        HashSet<int> current = ReadGroups();
        if (!initialized)
        {
            ReplaceSnapshot(current);
            initialized = true;
            return;
        }

        int newGroup = -1;
        foreach (int gid in current)
        {
            if (previousGroups.Contains(gid)) continue;
            // If more than one group appears in the same frame, favour ModelViewer's
            // current group because that is the group the creation path just selected.
            if (gid == viewer.currentGroupId)
            {
                newGroup = gid;
                break;
            }
            if (newGroup < 0) newGroup = gid;
        }

        ReplaceSnapshot(current);
        if (newGroup < 0) return;

        EnterFreshGroupRoot(newGroup);
    }

    void Resolve()
    {
        if (viewer == null)
        {
            viewer = FindFirstObjectByType<ModelViewer>();
            if (viewer != null)
            {
                BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic;
                allGroupIdsField = typeof(ModelViewer).GetField("allGroupIds", f);
                selectGroupMethod = typeof(ModelViewer).GetMethod("SelectGroup", f);
                clearSelectionMethod = typeof(ModelViewer).GetMethod("ClearSelectionHotspot", f);
                resetAllSlidersMethod = typeof(ModelViewer).GetMethod("ResetAllSliders", f);
            }
        }

        if (posts == null)
        {
            posts = FindFirstObjectByType<PostAffectorManager>();
            if (posts != null)
            {
                BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic;
                postActiveIdField = typeof(PostAffectorManager).GetField("activeId", f);
                postActiveGroupField = typeof(PostAffectorManager).GetField("activeGroup", f);
                postGroupsField = typeof(PostAffectorManager).GetField("groups", f);
            }
        }

        if (clumpers == null)
        {
            clumpers = FindFirstObjectByType<GroupClumperManager>();
            if (clumpers != null)
            {
                BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic;
                clumperSelectedGroupField = typeof(GroupClumperManager).GetField("selectedGroup", f);
                clumperSelectedIdField = typeof(GroupClumperManager).GetField("selectedClumperId", f);
                clumperGroupsField = typeof(GroupClumperManager).GetField("byGroup", f);
                clumperDestroyControlsMethod = typeof(GroupClumperManager).GetMethod("DestroyControls", f);
            }
        }

        if (rootState == null)
        {
            rootState = FindFirstObjectByType<GroomRootStateAuthority>();
            if (rootState != null)
                rootStatesField = typeof(GroomRootStateAuthority).GetField("roots", BindingFlags.Instance | BindingFlags.NonPublic);
        }

        if (variance == null)
        {
            variance = FindFirstObjectByType<GroomVarianceController>();
            if (variance != null)
            {
                BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic;
                varianceSettingsField = typeof(GroomVarianceController).GetField("groupSettings", f);
                varianceLastGroupField = typeof(GroomVarianceController).GetField("lastGroupId", f);
            }
        }

        if (uvRouting == null)
        {
            uvRouting = FindFirstObjectByType<GroupPredeterminedUVController>();
            if (uvRouting != null)
                uvSettingsField = typeof(GroupPredeterminedUVController).GetField("settingsByGroup", BindingFlags.Instance | BindingFlags.NonPublic);
        }

        if (curves == null)
        {
            curves = FindFirstObjectByType<GroomShapeCurveAuthority>();
            if (curves != null)
                closeCurvePopupMethod = typeof(GroomShapeCurveAuthority).GetMethod("ClosePopup", BindingFlags.Instance | BindingFlags.NonPublic);
        }
    }

    HashSet<int> ReadGroups()
    {
        HashSet<int> result = new HashSet<int>();
        object raw = allGroupIdsField?.GetValue(viewer);
        if (raw is IEnumerable<int> groups)
        {
            foreach (int gid in groups) result.Add(gid);
        }
        return result;
    }

    void ReplaceSnapshot(HashSet<int> current)
    {
        previousGroups.Clear();
        foreach (int gid in current) previousGroups.Add(gid);
    }

    void EnterFreshGroupRoot(int gid)
    {
        // Stroke-created groups already contain the cards that were just authored. They still
        // need recycled metadata purged, but resetting the scalar sliders would rewrite those
        // new cards. Empty + GROUP groups should instead receive the exact factory defaults.
        bool hasCards = FindObjectsByType<HairCard>(FindObjectsSortMode.None)
            .Any(card => card != null && card.groupId == gid);

        // Release ModelViewer's localized selection/hotspot first so normal group sliders
        // cannot inherit a POST-local edit context.
        clearSelectionMethod?.Invoke(viewer, null);
        viewer.selectionStrength = 0f;

        // Leave modifier editing before clearing any recycled records for this numeric ID.
        postActiveIdField?.SetValue(posts, -1);
        postActiveGroupField?.SetValue(posts, -1);
        clumperSelectedGroupField?.SetValue(clumpers, -1);
        clumperSelectedIdField?.SetValue(clumpers, -1);
        clumperDestroyControlsMethod?.Invoke(clumpers, null);

        GameObject host = GameObject.Find("ClumperScrollHost");
        if (host != null) Destroy(host);

        ResetStoredGroupState(gid);

        // Use ModelViewer's own group-selection path so UV/base values, group highlight and
        // all standard group controls are refreshed exactly as if the root had been clicked.
        selectGroupMethod?.Invoke(viewer, new object[] { gid });

        if (!hasCards)
        {
            // ResetAllSliders ends by touching lastPlacedCard, so detach the previous group's
            // last-card pointer before invoking it. With an empty new group its slider callbacks
            // are then harmless and update both the backing values and visible labels cleanly.
            viewer.lastPlacedCard = null;
            resetAllSlidersMethod?.Invoke(viewer, null);
            selectGroupMethod?.Invoke(viewer, new object[] { gid });
        }

        if (EventSystem.current != null)
        {
            GameObject row = GameObject.Find("GroupItem_" + gid);
            Transform label = row != null ? row.transform.Find("LabelButton") : null;
            EventSystem.current.SetSelectedGameObject(label != null ? label.gameObject : null);
        }
    }

    void ResetStoredGroupState(int gid)
    {
        // A numeric group ID may have existed earlier in this session. Remove every known
        // group-keyed cache so reusing that number behaves exactly like a never-used group.
        closeCurvePopupMethod?.Invoke(curves, null);
        RemoveGroupEntry(rootStatesField, rootState, gid);
        RemoveGroupEntry(varianceSettingsField, variance, gid);
        RemoveGroupEntry(uvSettingsField, uvRouting, gid);
        RemoveGroupEntry(postGroupsField, posts, gid);
        RemoveGroupEntry(clumperGroupsField, clumpers, gid);

        // Force the variance UI to re-read the now-default setting on its next Update.
        varianceLastGroupField?.SetValue(variance, int.MinValue);

        SurfaceIslandScope.SetClumperContiguous(gid, false);

        GroomShapeCurveRegistry.Reset(gid, GroomShapeCurveChannel.Bend);
        GroomShapeCurveRegistry.Reset(gid, GroomShapeCurveChannel.X);
        GroomShapeCurveRegistry.Reset(gid, GroomShapeCurveChannel.Y);
        GroomShapeCurveRegistry.Reset(gid, GroomShapeCurveChannel.Z);
        GroomShapeCurveRegistry.Reset(gid, GroomShapeCurveChannel.CurlFrequency);
        GroomShapeCurveRegistry.Reset(gid, GroomShapeCurveChannel.CurlDiameter);
        GroomShapeCurveRegistry.Reset(gid, GroomShapeCurveChannel.WaveAmplitude);
        GroomShapeCurveRegistry.Reset(gid, GroomShapeCurveChannel.WaveFrequency);
        GroomShapeCurveRegistry.Reset(gid, GroomShapeCurveChannel.WaveDirection);
        GroomShapeCurveRegistry.Reset(gid, GroomShapeCurveChannel.SegmentDensity);
        // A recycled group id must not inherit the deleted group's width taper - same rule as
        // the SS/DS Forget below.
        GroomShapeCurveRegistry.Reset(gid, GroomShapeCurveChannel.Width);

        // A reused group id must not inherit the previous group's SS/DS rendering choice.
        GroupSidednessAuthority.Forget(gid);
        GroupNormalFlipAuthority.Forget(gid);
    }

    static void RemoveGroupEntry(FieldInfo field, object owner, int gid)
    {
        IDictionary dictionary = field?.GetValue(owner) as IDictionary;
        if (dictionary != null && dictionary.Contains(gid))
            dictionary.Remove(gid);
    }
}
