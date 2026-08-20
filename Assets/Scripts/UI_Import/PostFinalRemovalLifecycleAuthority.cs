using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

// POST teardown must be a complete lifecycle boundary. When a group loses its final POST,
// restore every existing card from PostAffectorManager's stored upstream baseState, write that
// state back to canonical + rendered mesh, remove the stale per-card POST cache entries, and
// clear POST selection ownership. This makes a later POST start from a genuinely fresh group
// state and does not require Ctrl-clicking empty space between modifier operations.
[DefaultExecutionOrder(3350)]
public class PostFinalRemovalLifecycleAuthority : MonoBehaviour
{
    private PostAffectorManager manager;
    private FieldInfo groupsField;
    private FieldInfo cardStatesField;
    private FieldInfo activeIdField;
    private FieldInfo activeGroupField;

    private ModelViewer viewer;
    private FieldInfo hasSelectionField;
    private FieldInfo hitPointField;
    private FieldInfo hitNormalField;

    private readonly HashSet<int> previousGroups = new HashSet<int>();
    private bool initialized;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<PostFinalRemovalLifecycleAuthority>() != null) return;
        GameObject go = new GameObject("PostFinalRemovalLifecycleAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<PostFinalRemovalLifecycleAuthority>();
    }

    void Update()
    {
        Resolve();
        if (manager == null || groupsField == null) return;

        var groups = groupsField.GetValue(manager) as Dictionary<int, List<PostAffectorManager.PostAffector>>;
        if (groups == null) return;

        if (!initialized)
        {
            Sync(groups);
            initialized = true;
            return;
        }

        List<int> removed = null;
        foreach (int gid in previousGroups)
        {
            if (groups.ContainsKey(gid)) continue;
            if (removed == null) removed = new List<int>();
            removed.Add(gid);
        }

        if (removed != null)
        {
            foreach (int gid in removed)
                RestoreAndClearFinalPost(gid);
        }

        Sync(groups);
    }

    void Resolve()
    {
        if (manager == null)
        {
            manager = FindFirstObjectByType<PostAffectorManager>();
            if (manager != null)
            {
                BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                System.Type t = typeof(PostAffectorManager);
                groupsField = t.GetField("groups", flags);
                cardStatesField = t.GetField("cardStates", flags);
                activeIdField = t.GetField("activeId", flags);
                activeGroupField = t.GetField("activeGroup", flags);
                initialized = false;
            }
        }

        if (viewer == null)
        {
            viewer = FindFirstObjectByType<ModelViewer>();
            if (viewer != null)
            {
                BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                System.Type t = typeof(ModelViewer);
                hasSelectionField = t.GetField("hasSelectionHotspot", flags);
                hitPointField = t.GetField("selectionHitPoint", flags);
                hitNormalField = t.GetField("selectionHitNormal", flags);
            }
        }
    }

    void RestoreAndClearFinalPost(int gid)
    {
        HairCard[] cards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        IDictionary states = cardStatesField != null ? cardStatesField.GetValue(manager) as IDictionary : null;
        List<HairCard> clear = new List<HairCard>();
        int restored = 0;

        foreach (HairCard card in cards)
        {
            if (card == null || card.groupId != gid) continue;

            bool didRestore = false;
            if (states != null && states.Contains(card))
            {
                object cardState = states[card];
                if (cardState != null)
                {
                    FieldInfo baseStateField = cardState.GetType().GetField("baseState", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (baseStateField != null && baseStateField.GetValue(cardState) is PostAffectorManager.ControlState baseState)
                    {
                        HairCard.GroomState upstream = ToGroomState(baseState);
                        card.SetCanonicalState(upstream, false);
                        card.ApplyEvaluatedState(upstream);
                        didRestore = true;
                    }
                }
                clear.Add(card);
            }

            if (!didRestore)
            {
                HairCard.GroomState upstream = card.GetCanonicalState();
                card.ApplyEvaluatedState(upstream);
            }

            card.SetSelectionWeight(0f);
            restored++;
        }

        if (states != null)
        {
            foreach (HairCard card in clear)
                states.Remove(card);
        }

        // Removing the final POST is also an explicit exit from POST authoring. A user should
        // never have to Ctrl-click elsewhere just to make the next POST editable.
        if (activeIdField != null) activeIdField.SetValue(manager, -1);
        if (activeGroupField != null) activeGroupField.SetValue(manager, -1);
        if (viewer != null)
        {
            if (hasSelectionField != null) hasSelectionField.SetValue(viewer, false);
            if (hitPointField != null) hitPointField.SetValue(viewer, Vector3.zero);
            if (hitNormalField != null) hitNormalField.SetValue(viewer, Vector3.zero);
            viewer.selectionStrength = 1f;
            // Same reasoning as the plain "click in empty space" exit path: without this the
            // sliders keep showing whatever this now-removed POST last had them at.
            viewer.SyncShapeSlidersToGroupRoot(gid);
        }

        Debug.Log("Final POST removed from group " + gid + ": restored " + restored + " HairCards to upstream state and cleared POST lifecycle cache.");
    }

    static HairCard.GroomState ToGroomState(PostAffectorManager.ControlState s)
    {
        return new HairCard.GroomState
        {
            length = Mathf.Max(.0001f, s.length),
            width = Mathf.Max(.0005f, s.width),
            segments = Mathf.Clamp(Mathf.RoundToInt(s.segments), 1, 60),
            bend = s.bend,
            twist = s.twist,
            depth = Mathf.Max(0f, s.depth),
            x = s.x,
            y = s.y,
            z = s.z,
            uScale = s.uScale,
            vScale = s.vScale,
            uOffset = s.uOffset,
            vOffset = s.vOffset,
            curlFrequency = s.curlFrequency,
            curlDiameter = Mathf.Max(0f, s.curlDiameter),
            waveAmplitude = Mathf.Max(0f, s.waveAmplitude),
            waveFrequency = s.waveFrequency
        };
    }

    void Sync(Dictionary<int, List<PostAffectorManager.PostAffector>> groups)
    {
        previousGroups.Clear();
        foreach (KeyValuePair<int, List<PostAffectorManager.PostAffector>> kv in groups)
        {
            if (kv.Value != null && kv.Value.Count > 0)
                previousGroups.Add(kv.Key);
        }
    }
}
