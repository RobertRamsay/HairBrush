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

        // ---- has anything happened that this pass could need to repair? -------------------
        //
        // This is a REPAIR sweep, not a simulation step. It exists to put a card back to its
        // authored state after something else disturbed it - a POST torn down, a clumper
        // released, a stray SetParameters from the legacy selection path. In a groom that has
        // never had a POST or a clumper in it there is nothing to repair, and it was running
        // ApplyEvaluatedState over all forty thousand cards every frame anyway to discover that.
        //
        // So the pass now watches the handful of things that can create work for it, and holds
        // off only once they have ALL been still for a while. The grace window is what makes
        // this safe: a teardown does not complete in one frame, so a single-frame edge test
        // would let the pass go back to sleep in the middle of the very thing it is for.
        int worldState = 17;
        unchecked
        {
            worldState = worldState * 31 + groupsWithPosts.Count;
            worldState = worldState * 31 + HairCard.RegistryVersion;
            worldState = worldState * 31 + GroupSoloVisibilityAuthority.Epoch;
            worldState = worldState * 31 + (cachedStates != null ? cachedStates.Count : 0);
        }

        if (worldState != lastWorldState)
        {
            lastWorldState = worldState;
            settledFrames = 0;
        }
        else if (settledFrames < SettleFrames)
        {
            settledFrames++;
        }

        // The bookkeeping half below still has to run every frame even when settled - it is a
        // dictionary probe per card and it is what the comment inside insists on. The GEOMETRY
        // half is the one that costs, and it is the one that gets to stop.
        bool repairGeometry = settledFrames < SettleFrames;

        // HairCard.All rather than FindObjectsByType: the same cards without a forty-thousand
        // entry array allocated every frame to hold them.
        IReadOnlyList<HairCard> allCards = HairCard.All;
        for (int i = 0; i < allCards.Count; i++)
        {
            HairCard card = allCards[i];
            if (card == null || groupsWithPosts.Contains(card.groupId)) continue;

            // Critical lifecycle rule: PostAffectorManager must not retain a CardState for a
            // group with no POSTs. Otherwise its normal LateUpdate recreates an old-strand
            // cache immediately after final-POST teardown, while newly-created strands start
            // clean. Clearing here (after POST evaluation) keeps both populations identical.
            //
            // Deliberately ABOVE the SOLO freeze below. This is bookkeeping, not geometry: it
            // costs a dictionary probe, not a mesh rebuild. Skipping it for hidden cards would
            // mean a group whose last POST was deleted while it was soloed out came back with
            // a stale baseState still cached, and got one frame evaluated from it - a visible
            // flicker on release, and the one place where "frozen loses nothing" would not
            // have been true.
            if (cachedStates != null && cachedStates.Contains(card))
                cachedStates.Remove(card);

            // Frozen by SOLO. The rest of this sweep ends in ApplyEvaluatedState ->
            // GenerateMesh for every POST-free card, which in the common case (no POSTs
            // anywhere) is the whole scene, every frame. Skipping the hidden groups costs
            // nothing in correctness: the card keeps its current mesh, its canonical state is
            // untouched, and this same sweep rebuilds it the frame SOLO lets it go.
            if (GroupSoloVisibilityAuthority.IsCardFrozen(card)) continue;

            // Nothing has changed for long enough that there is nothing left to repair. The
            // bookkeeping above still ran; this is the part that was rebuilding the world to
            // arrive back where it started.
            if (!repairGeometry) continue;

            // Match the known-good zero-CLUMPER refresh: remove any residual per-card clump
            // state, then explicitly rebuild from the authored upstream state.
            card.ClearClumpModifier();
            card.SetSelectionWeight(0f);
            card.ApplyEvaluatedState(card.GetCanonicalState());
        }
    }

    // How many consecutive unchanged frames before the geometry repair stands down. Generous on
    // purpose: it is paid once per change, not per frame, and the cost of being wrong is a card
    // left holding a modifier's geometry after the modifier is gone.
    private const int SettleFrames = 30;

    private int lastWorldState = -1;
    private int settledFrames;

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
