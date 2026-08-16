using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

// POST data belongs to the lifetime of its Hair Group. Numeric group IDs are reused,
// so deleting a group must remove both its POST records and cached per-card POST state.
// Also owns the creation-time POST radius default so POST does not inherit the much
// larger general brush radius from ModelViewer.
[DefaultExecutionOrder(3310)]
public class PostGroupLifetimeAuthority : MonoBehaviour
{
    private const float DefaultPostRadius = .05f;

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
    private int lastNormalizedPostId = -1;

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
        ApplyNewPostRadiusDefault();
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

    void ApplyNewPostRadiusDefault()
    {
        if (lastCreatedFrameField == null || activeIdField == null || activeGroupField == null || groupsField == null)
            return;

        int createdFrame = lastCreatedFrameField.GetValue(posts) is int frame ? frame : -1;
        if (createdFrame != Time.frameCount) return;

        int activeId = activeIdField.GetValue(posts) is int id ? id : -1;
        int activeGroup = activeGroupField.GetValue(posts) is int gid ? gid : -1;
        if (activeId < 0 || activeGroup < 0 || activeId == lastNormalizedPostId) return;

        IDictionary groups = groupsField.GetValue(posts) as IDictionary;
        if (groups == null || !groups.Contains(activeGroup)) return;
        if (!(groups[activeGroup] is IEnumerable list)) return;

        foreach (object item in list)
        {
            PostAffectorManager.PostAffector post = item as PostAffectorManager.PostAffector;
            if (post == null || post.id != activeId) continue;

            post.radius = DefaultPostRadius;
            viewer.brushRadius = DefaultPostRadius;
            lastNormalizedPostId = activeId;
            break;
        }
    }
}
