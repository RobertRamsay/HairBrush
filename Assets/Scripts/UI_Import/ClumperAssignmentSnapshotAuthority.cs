using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

// A CLUMPER must never be allowed to become the new baseline for a group.
// Freeze two per-card snapshots ONCE, when the group first gains a CLUMPER:
//   PRE_POST  = canonical/source state before any POST evaluation.
//   POST      = currently rendered/evaluated state after POSTs, before CLUMPER mesh deformation.
// Repositioning or editing the CLUMPER never refreshes these snapshots.
// On removal, restore POST if POST modifiers still exist; otherwise restore PRE_POST.
[DefaultExecutionOrder(5210)]
public class ClumperAssignmentSnapshotAuthority : MonoBehaviour
{
    private sealed class FrozenGroup
    {
        public readonly Dictionary<HairCard, HairCard.GroomState> prePost = new Dictionary<HairCard, HairCard.GroomState>();
        public readonly Dictionary<HairCard, HairCard.GroomState> post = new Dictionary<HairCard, HairCard.GroomState>();
    }

    private readonly Dictionary<int, FrozenGroup> frozen = new Dictionary<int, FrozenGroup>();

    private GroupClumperManager clumperManager;
    private FieldInfo clumpGroupsField;
    private PostAffectorManager postManager;
    private FieldInfo postGroupsField;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<ClumperAssignmentSnapshotAuthority>() != null) return;
        GameObject go = new GameObject("ClumperAssignmentSnapshotAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<ClumperAssignmentSnapshotAuthority>();
    }

    void Update()
    {
        Resolve();
        if (clumperManager == null || clumpGroupsField == null) return;

        var active = clumpGroupsField.GetValue(clumperManager) as Dictionary<int, GroupClumperManager.GroupClumper>;
        if (active == null) return;

        // Freeze a group exactly once, on the first frame the CLUMPER exists.
        foreach (int gid in active.Keys)
        {
            if (!frozen.ContainsKey(gid)) CaptureAssignment(gid);
        }

        // Restore immediately when the manager no longer contains that group's CLUMPER.
        if (frozen.Count == 0) return;
        List<int> removed = null;
        foreach (int gid in frozen.Keys)
        {
            if (active.ContainsKey(gid)) continue;
            if (removed == null) removed = new List<int>();
            removed.Add(gid);
        }

        if (removed == null) return;
        foreach (int gid in removed)
        {
            RestoreAssignment(gid);
            frozen.Remove(gid);
        }
    }

    void Resolve()
    {
        if (clumperManager == null)
        {
            clumperManager = FindFirstObjectByType<GroupClumperManager>();
            if (clumperManager != null)
                clumpGroupsField = typeof(GroupClumperManager).GetField("byGroup", BindingFlags.Instance | BindingFlags.NonPublic);
        }

        if (postManager == null)
        {
            postManager = FindFirstObjectByType<PostAffectorManager>();
            if (postManager != null)
                postGroupsField = typeof(PostAffectorManager).GetField("groups", BindingFlags.Instance | BindingFlags.NonPublic);
        }
    }

    void CaptureAssignment(int gid)
    {
        FrozenGroup snapshot = new FrozenGroup();
        HairCard[] cards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);

        foreach (HairCard card in cards)
        {
            if (card == null || card.groupId != gid) continue;

            // PRE_POST is the true authored source and must remain frozen for the lifetime
            // of this CLUMPER.
            snapshot.prePost[card] = card.GetCanonicalState();

            // At order 5210 the card still contains the POST-evaluated parameter state from
            // the previous completed frame, while CLUMPER deformation itself only lives in
            // mesh vertices. Reading parameters here therefore reconstructs the exact
            // pre-clump displayed form without carrying clumped vertices into the snapshot.
            snapshot.post[card] = ReadRendered(card);
        }

        frozen[gid] = snapshot;
        Debug.Log("CLUMPER assigned to group " + gid + ": froze PRE_POST and POST states for " + snapshot.prePost.Count + " HairCards.");
    }

    void RestoreAssignment(int gid)
    {
        if (!frozen.TryGetValue(gid, out FrozenGroup snapshot)) return;

        bool keepPosts = HasPostModifiers(gid);
        HairCard[] cards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        int restored = 0;

        foreach (HairCard card in cards)
        {
            if (card == null || card.groupId != gid) continue;

            HairCard.GroomState state;
            if (keepPosts)
            {
                state = snapshot.post.TryGetValue(card, out HairCard.GroomState savedPost)
                    ? savedPost
                    : ReadRendered(card);
                // POST is an evaluated layer. Never bake it back into canonical/source.
                card.ApplyEvaluatedState(state);
            }
            else
            {
                state = snapshot.prePost.TryGetValue(card, out HairCard.GroomState savedSource)
                    ? savedSource
                    : card.GetCanonicalState();
                card.SetCanonicalState(state, false);
                card.ApplyEvaluatedState(state);
            }

            // Belt-and-braces: no old card-level clump state or final-pass vertices survive.
            card.ClearClumpModifier();
            card.GenerateMesh();
            restored++;
        }

        Debug.Log("CLUMPER removed from group " + gid + ": restored " + restored + " HairCards from frozen " + (keepPosts ? "POST" : "PRE_POST") + " snapshot.");
    }

    bool HasPostModifiers(int gid)
    {
        Resolve();
        if (postManager == null || postGroupsField == null) return false;

        var groups = postGroupsField.GetValue(postManager) as Dictionary<int, List<PostAffectorManager.PostAffector>>;
        return groups != null && groups.TryGetValue(gid, out List<PostAffectorManager.PostAffector> list) && list != null && list.Count > 0;
    }

    static HairCard.GroomState ReadRendered(HairCard card)
    {
        return new HairCard.GroomState
        {
            length = card.length,
            width = card.width,
            segments = Mathf.Clamp(card.segments, 1, 36),
            bend = card.bendAngle,
            twist = card.twistAngle,
            depth = card.GetEmbedDepth(),
            x = card.GetOffsetX(),
            y = card.GetOffsetY(),
            z = card.GetOffsetZ(),
            uScale = card.uScale,
            vScale = card.vScale,
            uOffset = card.uOffset,
            vOffset = card.vOffset
        };
    }
}
