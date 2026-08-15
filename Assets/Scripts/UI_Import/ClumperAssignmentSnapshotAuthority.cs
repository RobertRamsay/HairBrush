using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

// Freeze the exact per-card group state immediately before GroupClumperManager's LateUpdate
// deforms mesh vertices. This is intentionally a one-shot assignment snapshot: changing
// amount/radius/mode/repositioning never refreshes it.
//
// PRE_POST = canonical/source GroomState at CLUMPER assignment.
// POST     = evaluated GroomState + exact mesh vertices at CLUMPER assignment, after POSTs
//            have evaluated but before CLUMPER writes its final mesh pass.
//
// On removal: keep POST snapshot when POSTs still exist; otherwise restore PRE_POST.
[DefaultExecutionOrder(5190)]
public class ClumperAssignmentSnapshotAuthority : MonoBehaviour
{
    private sealed class FrozenCard
    {
        public HairCard.GroomState prePost;
        public HairCard.GroomState post;
        public Vector3[] postVertices;
    }

    private sealed class FrozenGroup
    {
        public readonly Dictionary<HairCard, FrozenCard> cards = new Dictionary<HairCard, FrozenCard>();
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

    void LateUpdate()
    {
        Resolve();
        if (clumperManager == null || clumpGroupsField == null) return;

        var active = clumpGroupsField.GetValue(clumperManager) as Dictionary<int, GroupClumperManager.GroupClumper>;
        if (active == null) return;

        // GroupClumperManager.Update has already created/removed modifiers by this point,
        // while its LateUpdate (order 5200) has not yet deformed this frame's meshes.
        foreach (int gid in active.Keys)
        {
            if (!frozen.ContainsKey(gid)) CaptureAssignment(gid);
        }

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

            MeshFilter mf = card.GetComponent<MeshFilter>();
            Vector3[] vertices = mf != null && mf.mesh != null ? mf.mesh.vertices : null;

            snapshot.cards[card] = new FrozenCard
            {
                prePost = card.GetCanonicalState(),
                post = ReadRendered(card),
                postVertices = vertices != null ? (Vector3[])vertices.Clone() : null
            };
        }

        frozen[gid] = snapshot;
        Debug.Log("CLUMPER assigned to group " + gid + ": froze exact pre-clump mesh for " + snapshot.cards.Count + " HairCards.");
    }

    void RestoreAssignment(int gid)
    {
        if (!frozen.TryGetValue(gid, out FrozenGroup snapshot)) return;

        bool keepPosts = HasPostModifiers(gid);
        HairCard[] cards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        int restored = 0;
        int exactMeshes = 0;

        foreach (HairCard card in cards)
        {
            if (card == null || card.groupId != gid) continue;
            if (!snapshot.cards.TryGetValue(card, out FrozenCard saved)) continue;

            // Remove CLUMPER-owned card state first. GroupClumperManager has already removed
            // the group modifier from its dictionary, so its later LateUpdate cannot reapply it.
            card.ClearClumpModifier();

            if (keepPosts)
            {
                // Restore evaluated parameters, then restore the exact mesh that was visible
                // at assignment time. This bypasses any reconstruction ambiguity entirely.
                card.ApplyEvaluatedState(saved.post);
                MeshFilter mf = card.GetComponent<MeshFilter>();
                if (mf != null && mf.mesh != null && saved.postVertices != null && mf.mesh.vertexCount == saved.postVertices.Length)
                {
                    mf.mesh.vertices = (Vector3[])saved.postVertices.Clone();
                    mf.mesh.RecalculateNormals();
                    mf.mesh.RecalculateBounds();
                    exactMeshes++;
                }
            }
            else
            {
                // No POSTs: return to the authored state that existed before any modifiers.
                card.SetCanonicalState(saved.prePost, false);
                card.ApplyEvaluatedState(saved.prePost);
            }

            card.SetSelectionWeight(0f);
            restored++;
        }

        Debug.Log("CLUMPER removed from group " + gid + ": restored " + restored + " HairCards; exact mesh restores=" + exactMeshes + "; layer=" + (keepPosts ? "POST" : "PRE_POST") + ".");
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