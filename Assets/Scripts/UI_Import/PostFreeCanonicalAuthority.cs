using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

// Once a group has zero POST affectors, no POST-local cache should be allowed to keep
// re-applying stale evaluated values. PostAffectorManager currently visits every HairCard
// in LateUpdate and can recreate CardState entries even for groups that have no POSTs, so
// this authority runs afterwards and makes POST-free an explicit lifecycle state:
//   - remove the card from POST's per-card cache;
//   - clear any residual card-level clump deformation;
//   - render directly from canonical authored state;
//   - clear selection weight.
// This deliberately mirrors the hard refresh that a zero-strength CLUMPER performs. The
// next POST therefore has to capture a genuinely fresh baseState for every existing card,
// exactly as it does for a newly-created strand.
[DefaultExecutionOrder(5000)]
public class PostFreeCanonicalAuthority : MonoBehaviour
{
    private PostAffectorManager manager;
    private FieldInfo groupsField;
    private FieldInfo cardStatesField;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<PostFreeCanonicalAuthority>() != null) return;
        GameObject go = new GameObject("PostFreeCanonicalAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<PostFreeCanonicalAuthority>();
    }

    void LateUpdate()
    {
        Resolve();
        if (manager == null || groupsField == null) return;

        IDictionary groups = groupsField.GetValue(manager) as IDictionary;
        IDictionary cachedStates = cardStatesField != null ? cardStatesField.GetValue(manager) as IDictionary : null;
        HashSet<int> groupsWithPosts = new HashSet<int>();
        if (groups != null)
        {
            foreach (DictionaryEntry entry in groups)
            {
                if (!(entry.Key is int gid)) continue;
                int count = entry.Value is ICollection collection ? collection.Count : 0;
                if (count > 0) groupsWithPosts.Add(gid);
            }
        }

        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
        {
            if (card == null || groupsWithPosts.Contains(card.groupId)) continue;

            // Frozen by SOLO. This sweep ends in ApplyEvaluatedState -> GenerateMesh for
            // every POST-free card, which in the common case (no POSTs anywhere) is the
            // whole scene, every frame. Skipping the hidden groups costs nothing in
            // correctness: the card keeps its current mesh, its canonical state is
            // untouched, and this same sweep rebuilds it the frame SOLO lets it go.
            if (GroupSoloVisibilityAuthority.IsCardFrozen(card)) continue;

            // Critical lifecycle rule: PostAffectorManager must not retain a CardState for a
            // group with no POSTs. Otherwise its normal LateUpdate recreates an old-strand
            // cache immediately after final-POST teardown, while newly-created strands start
            // clean. Clearing here (after POST evaluation) keeps both populations identical.
            if (cachedStates != null && cachedStates.Contains(card))
                cachedStates.Remove(card);

            // Match the known-good zero-CLUMPER refresh: remove any residual per-card clump
            // state, then explicitly rebuild from the authored upstream state.
            card.ClearClumpModifier();
            card.SetSelectionWeight(0f);
            card.ApplyEvaluatedState(card.GetCanonicalState());
        }
    }

    void Resolve()
    {
        if (manager != null) return;
        manager = FindFirstObjectByType<PostAffectorManager>();
        if (manager != null)
        {
            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            groupsField = typeof(PostAffectorManager).GetField("groups", flags);
            cardStatesField = typeof(PostAffectorManager).GetField("cardStates", flags);
        }
    }
}
