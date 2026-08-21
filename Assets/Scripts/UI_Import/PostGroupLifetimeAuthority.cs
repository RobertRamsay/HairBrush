using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

// POST data belongs to the lifetime of its Hair Group. Numeric group IDs are reused,
// so deleting a group must remove both its POST records and cached per-card POST state.
// Also owns the creation-time POST radius and falloff defaults so POST does not inherit the
// much larger general brush radius from ModelViewer.
[DefaultExecutionOrder(3310)]
public class PostGroupLifetimeAuthority : MonoBehaviour
{
    // THE creation defaults for a POST. Every new POST starts here, whatever the Radius and
    // Falloff sliders were left on.
    //
    // Public, and referenced rather than copied, because these numbers previously existed as
    // four independent literals - here, in SelectionBrushScaleTuning, and twice in
    // PostAffectorUXFix - which had already drifted to three different values (.05, .03/.05,
    // .05). The pre-click ring is drawn from one of them and the created POST stamped from
    // another, so a drift means the ring you aim with is not the POST you get.
    public const float DefaultPostRadius = .025f;
    public const float DefaultPostFalloff = .04f;

    private PostAffectorManager posts;
    private ModelViewer viewer;

    private FieldInfo groupsField;
    private FieldInfo cardStatesField;
    private FieldInfo activeIdField;
    private FieldInfo activeGroupField;
    private FieldInfo lastCreatedFrameField;

    private FieldInfo allGroupIdsField;
    private FieldInfo hasSelectionField;
    private FieldInfo isSelectionModeField;
    private FieldInfo hitPointField;
    private FieldInfo hitNormalField;

    private readonly HashSet<int> previousLiveGroups = new HashSet<int>();
    private bool initialized;
    // Keyed on the FRAME, not on the POST id.
    //
    // An id-based guard is unsafe here: PostAffectorManager.ClearAll resets nextId to 1 on
    // every project load and on session RESET, so POST ids are reused. After creating one POST
    // and then loading a project, the next POST created is id 1 again - equal to the remembered
    // id - and the whole method returned early, silently leaving that POST on whatever the
    // sliders happened to hold. The frame stamp cannot collide: this method only ever acts on
    // the creation frame, and PostAffectorManager.lastCreatedFrame already forbids two POSTs
    // in one frame. It only has to make the Update and LateUpdate calls idempotent.
    private int lastNormalizedFrame = -1;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<PostGroupLifetimeAuthority>() != null) return;
        GameObject go = new GameObject("PostGroupLifetimeAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<PostGroupLifetimeAuthority>();
    }

    void Update()
    {
        Resolve();
        if (posts == null || viewer == null) return;

        PurgeDeletedGroups();
        ApplyNewPostShapeDefaults();
    }

    // Also in LateUpdate, because not every POST is created during Update any more.
    // GroupAddButtonPlacementAuthority (the +POST button) places in LateUpdate, so its
    // PostAffectorManager.lastCreatedFrame stamp lands after this component's Update has
    // already run - and the frame after, the createdFrame == Time.frameCount test fails and
    // the POST is never normalised at all. A button POST would silently keep whatever the
    // sliders happened to hold while a Ctrl+click POST got the default.
    //
    // Safe to run twice in one frame: lastNormalizedFrame makes the second call a no-op.
    void LateUpdate()
    {
        Resolve();
        if (posts == null || viewer == null) return;
        ApplyNewPostShapeDefaults();
    }

    void Resolve()
    {
        if (posts == null)
        {
            posts = FindFirstObjectByType<PostAffectorManager>();
            if (posts != null)
            {
                BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic;
                System.Type t = typeof(PostAffectorManager);
                groupsField = t.GetField("groups", f);
                cardStatesField = t.GetField("cardStates", f);
                activeIdField = t.GetField("activeId", f);
                activeGroupField = t.GetField("activeGroup", f);
                lastCreatedFrameField = t.GetField("lastCreatedFrame", f);
            }
        }

        if (viewer == null)
        {
            viewer = FindFirstObjectByType<ModelViewer>();
            if (viewer != null)
            {
                BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic;
                System.Type t = typeof(ModelViewer);
                allGroupIdsField = t.GetField("allGroupIds", f);
                hasSelectionField = t.GetField("hasSelectionHotspot", f);
                isSelectionModeField = t.GetField("isSelectionMode", f);
                hitPointField = t.GetField("selectionHitPoint", f);
                hitNormalField = t.GetField("selectionHitNormal", f);
            }
        }
    }

    void PurgeDeletedGroups()
    {
        HashSet<int> liveGroups = ReadLiveGroups();
        if (!initialized)
        {
            previousLiveGroups.Clear();
            foreach (int gid in liveGroups) previousLiveGroups.Add(gid);
            initialized = true;
            return;
        }

        foreach (int oldGid in previousLiveGroups)
        {
            if (!liveGroups.Contains(oldGid))
                PurgeDeletedGroup(oldGid);
        }

        previousLiveGroups.Clear();
        foreach (int gid in liveGroups) previousLiveGroups.Add(gid);
    }

    HashSet<int> ReadLiveGroups()
    {
        HashSet<int> live = new HashSet<int>();
        object raw = allGroupIdsField?.GetValue(viewer);
        if (raw is IEnumerable enumerable)
        {
            foreach (object value in enumerable)
                if (value is int id) live.Add(id);
            return live;
        }

        foreach (RectTransform rect in FindObjectsByType<RectTransform>(FindObjectsSortMode.None))
        {
            if (rect == null || !rect.name.StartsWith("GroupItem_")) continue;
            if (int.TryParse(rect.name.Substring("GroupItem_".Length), out int gid)) live.Add(gid);
        }
        return live;
    }

    void PurgeDeletedGroup(int gid)
    {
        IDictionary groups = groupsField?.GetValue(posts) as IDictionary;
        if (groups != null && groups.Contains(gid))
            groups.Remove(gid);

        // Remove cached POST state for cards from the deleted group as well. Keeping these
        // caches around is another route by which a recycled group ID can inherit stale state.
        IDictionary cardStates = cardStatesField?.GetValue(posts) as IDictionary;
        if (cardStates != null)
        {
            List<object> remove = new List<object>();
            foreach (DictionaryEntry entry in cardStates)
            {
                if (!(entry.Key is HairCard card)) continue;
                if (card == null || card.groupId == gid) remove.Add(entry.Key);
            }
            foreach (object key in remove) cardStates.Remove(key);
        }

        int activeGroup = activeGroupField != null && activeGroupField.GetValue(posts) is int a ? a : -1;
        if (activeGroup == gid)
            ExitPostEditing();

        foreach (RectTransform row in FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (row != null && row.name.StartsWith("PostAffector_" + gid + "_"))
                Destroy(row.gameObject);
        }
    }

    void ExitPostEditing()
    {
        activeIdField?.SetValue(posts, -1);
        activeGroupField?.SetValue(posts, -1);
        hasSelectionField?.SetValue(viewer, false);
        isSelectionModeField?.SetValue(viewer, false);
        hitPointField?.SetValue(viewer, Vector3.zero);
        hitNormalField?.SetValue(viewer, Vector3.zero);
        viewer.selectionStrength = 0f;
    }

    void ApplyNewPostShapeDefaults()
    {
        if (lastCreatedFrameField == null || activeIdField == null || activeGroupField == null || groupsField == null)
            return;

        int createdFrame = lastCreatedFrameField.GetValue(posts) is int frame ? frame : -1;
        if (createdFrame != Time.frameCount) return;

        int activeId = activeIdField.GetValue(posts) is int id ? id : -1;
        int activeGroup = activeGroupField.GetValue(posts) is int gid ? gid : -1;
        if (activeId < 0 || activeGroup < 0 || lastNormalizedFrame == Time.frameCount) return;

        IDictionary groups = groupsField.GetValue(posts) as IDictionary;
        if (groups == null || !groups.Contains(activeGroup)) return;
        if (!(groups[activeGroup] is IEnumerable list)) return;

        foreach (object item in list)
        {
            PostAffectorManager.PostAffector post = item as PostAffectorManager.PostAffector;
            if (post == null || post.id != activeId) continue;

            post.radius = DefaultPostRadius;
            post.falloff = DefaultPostFalloff;

            // The viewer fields are written too, not just the POST record: they are what the
            // Radius/Falloff sliders read back and what SelectionBrushVisualizer draws the
            // ring from, so leaving them on the old slider values would show a ring that does
            // not match the POST that was just created underneath it.
            viewer.brushRadius = DefaultPostRadius;
            viewer.brushFalloffDistance = DefaultPostFalloff;

            lastNormalizedFrame = Time.frameCount;
            break;
        }
    }
}
