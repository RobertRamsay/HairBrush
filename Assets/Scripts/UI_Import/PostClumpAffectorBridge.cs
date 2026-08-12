using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

// Keeps CLUMP in the same authoring model as the other POST controls without
// coupling the post-affector implementation directly to the clump manager.
[DefaultExecutionOrder(2450)]
public class PostClumpAffectorBridge : MonoBehaviour
{
    private class LocalClumpState
    {
        public float baseline;
        public float delta;
    }

    private readonly Dictionary<int, LocalClumpState> states = new();
    private PostAffectorManager posts;
    private ClumpInlineGroomController clump;
    private ModelViewer viewer;
    private FieldInfo activeIdField;
    private FieldInfo activeGroupField;
    private FieldInfo groupsField;
    private bool displayDirty;
    private HairProjectSaveData cachedPending;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<PostClumpAffectorBridge>() != null) return;
        GameObject go = new GameObject("PostClumpAffectorBridge");
        DontDestroyOnLoad(go);
        go.AddComponent<PostClumpAffectorBridge>();
    }

    void Update()
    {
        if (posts == null)
        {
            posts = FindFirstObjectByType<PostAffectorManager>();
            if (posts != null)
            {
                BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic;
                activeIdField = typeof(PostAffectorManager).GetField("activeId", f);
                activeGroupField = typeof(PostAffectorManager).GetField("activeGroup", f);
                groupsField = typeof(PostAffectorManager).GetField("groups", f);
            }
        }
        if (clump == null) clump = FindFirstObjectByType<ClumpInlineGroomController>();
        if (viewer == null) viewer = FindFirstObjectByType<ModelViewer>();

        HairProjectSaveData pending = HairProjectSaveData.PendingModifierRestore;
        if (pending != null && pending != cachedPending)
        {
            cachedPending = pending;
            states.Clear();
            if (pending.groups != null)
                foreach (GroupSaveData g in pending.groups)
                    if (g.postAffectors != null)
                        foreach (PostAffectorSaveData p in g.postAffectors)
                            states[p.id] = new LocalClumpState { baseline = p.clumpBaseline, delta = p.clumpDelta };
            displayDirty = true;
        }
    }

    public bool TryAuthorActive(float target)
    {
        PostAffectorManager.PostAffector active = GetActive();
        if (active == null || clump == null) return false;
        int id = active.id;
        if (!states.TryGetValue(id, out LocalClumpState s))
        {
            float baseWeight = clump.GetBaseGroupWeight(active.groupId);
            s = new LocalClumpState { baseline = baseWeight, delta = 0f };
            states[id] = s;
        }
        s.delta = Mathf.Clamp01(target) - s.baseline;
        displayDirty = true;
        clump.ApplyGroup(active.groupId);
        return true;
    }

    public float GetDisplayedWeight(int groupId, float baseWeight)
    {
        PostAffectorManager.PostAffector active = GetActive();
        if (active == null || active.groupId != groupId) return baseWeight;
        if (!states.TryGetValue(active.id, out LocalClumpState s)) return baseWeight;
        return Mathf.Clamp01(s.baseline + s.delta);
    }

    public float EvaluateWeight(HairCard card, float baseWeight)
    {
        float result = baseWeight;
        Dictionary<int, List<PostAffectorManager.PostAffector>> groups = GetGroups();
        if (groups == null || !groups.TryGetValue(card.groupId, out List<PostAffectorManager.PostAffector> list))
            return Mathf.Clamp01(result);

        Vector3 p = card.GetSpawnHitPoint();
        if (p == Vector3.zero) p = card.transform.position;
        foreach (PostAffectorManager.PostAffector a in list)
        {
            if (!states.TryGetValue(a.id, out LocalClumpState s) || Mathf.Abs(s.delta) < .000001f) continue;
            float d = Vector3.Distance(p, a.center);
            float radius = Mathf.Max(.001f, a.radius);
            float outer = radius + Mathf.Max(0f, a.falloff);
            float spatial;
            if (d <= radius) spatial = 1f;
            else if (a.falloff <= .000001f || d >= outer) spatial = 0f;
            else spatial = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(outer, radius, d));
            result += s.delta * spatial * Mathf.Clamp01(a.weight);
        }
        return Mathf.Clamp01(result);
    }

    public void PopulateSave(List<PostAffectorSaveData> data)
    {
        if (data == null) return;
        foreach (PostAffectorSaveData p in data)
        {
            if (states.TryGetValue(p.id, out LocalClumpState s))
            {
                p.clumpBaseline = s.baseline;
                p.clumpDelta = s.delta;
            }
        }
    }

    public bool ConsumeDisplayDirty()
    {
        bool dirty = displayDirty;
        displayDirty = false;
        return dirty;
    }

    PostAffectorManager.PostAffector GetActive()
    {
        if (posts == null || activeIdField == null || activeGroupField == null) return null;
        int id = (int)activeIdField.GetValue(posts);
        int group = (int)activeGroupField.GetValue(posts);
        if (id < 0 || group < 0) return null;
        Dictionary<int, List<PostAffectorManager.PostAffector>> groups = GetGroups();
        return groups != null && groups.TryGetValue(group, out List<PostAffectorManager.PostAffector> list)
            ? list.FirstOrDefault(a => a.id == id) : null;
    }

    Dictionary<int, List<PostAffectorManager.PostAffector>> GetGroups()
    {
        return groupsField?.GetValue(posts) as Dictionary<int, List<PostAffectorManager.PostAffector>>;
    }
}
