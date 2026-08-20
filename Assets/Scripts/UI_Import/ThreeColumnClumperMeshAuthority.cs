using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// Final native 3-column CLUMPER evaluator.
//
// The expensive mesh rebuild is dirty-driven. Each CLUMPER group keeps a lightweight signature
// of modifier settings + card source meshes/transforms. If that signature has not changed, the
// already-derived clumped mesh is left untouched. HairCard cooperates by preserving an active
// external CLUMPER override when another authority regenerates an identical clean source mesh.
[DefaultExecutionOrder(5255)]
public class ThreeColumnClumperMeshAuthority : MonoBehaviour
{
    private sealed class CleanMeshData
    {
        public Vector3[] vertices;
        public Vector2[] uvs;
        public int[] triangles;
    }

    private GroupClumperManager manager;
    private readonly Dictionary<int, int> lastGroupSignature = new Dictionary<int, int>();

    // Last island each clumper (by id) successfully resolved to, so a transient raycast miss
    // reuses the previous answer instead of silently dropping that clumper for the frame.
    private readonly Dictionary<int, int> lastResolvedIsland = new Dictionary<int, int>();
    private readonly HashSet<int> overriddenGroups = new HashSet<int>();

    // Initialised to -1 rather than 0 so the very first LateUpdate always disagrees with the
    // authority's starting epoch and clears the (empty) cache once, instead of the two
    // silently agreeing before anything has been evaluated at all.
    private int lastSoloEpoch = -1;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<ThreeColumnClumperMeshAuthority>() != null) return;
        GameObject go = new GameObject("ThreeColumnClumperMeshAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<ThreeColumnClumperMeshAuthority>();
    }

    void LateUpdate()
    {
        if (manager == null) manager = FindFirstObjectByType<GroupClumperManager>();
        if (manager == null) return;

        // A group that was frozen by SOLO skipped its evaluations entirely, so the signature
        // cached against it describes a state that may no longer be true. Dropping the whole
        // cache the moment the solo set changes guarantees every group gets one honest
        // re-evaluation on the way back in, rather than resting on a stale "clean" verdict.
        if (lastSoloEpoch != GroupSoloVisibilityAuthority.Epoch)
        {
            lastSoloEpoch = GroupSoloVisibilityAuthority.Epoch;
            lastGroupSignature.Clear();
        }

        List<GroupClumperManager.GroupClumper> clumpers = manager.GetAllClumpers();
        HairCard[] allCards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);

        // Amount == 0 means this clumper no longer owns the generated mesh. Treat zeroed
        // clumpers exactly like removed clumpers here rather than keeping their group alive
        // until the manager's deferred-delete/UI lifecycle finishes. This makes authority
        // release a property of the evaluator itself, so normal HairCard GenerateMesh calls
        // regain control immediately when clumping is neutralised.
        List<GroupClumperManager.GroupClumper> ordered = clumpers
            .Where(c => c != null && c.amount > .0001f)
            .OrderBy(c => c.id)
            .ToList();
        HashSet<int> groups = new HashSet<int>(ordered.Select(c => c.groupId));

        if (ordered.Count == 0)
        {
            RestoreRemovedGroups(allCards, groups);
            lastGroupSignature.Clear();
            return;
        }

        RestoreRemovedGroups(allCards, groups);

        foreach (int groupId in groups)
        {
            // Frozen by SOLO. Skipping BEFORE the per-group LINQ filter and the
            // ComputeGroupSignature sort matters: the dirty-check itself is O(N log N) per
            // group per frame, so a hidden group was costing real time even on the frames
            // where it turned out to be clean and nothing was evaluated.
            //
            // Deliberately placed after RestoreRemovedGroups above, which must keep running
            // for every group so a clumper removed while soloing still releases its mesh.
            if (GroupSoloVisibilityAuthority.IsGroupFrozen(groupId)) continue;

            List<GroupClumperManager.GroupClumper> groupClumpers = ordered
                .Where(c => c.groupId == groupId)
                .ToList();
            HairCard[] groupCards = allCards
                .Where(c => c != null && c.groupId == groupId)
                .ToArray();

            int signature = ComputeGroupSignature(groupId, groupClumpers, groupCards);
            if (lastGroupSignature.TryGetValue(groupId, out int previous) && previous == signature)
                continue;

            // Only cache the signature when the evaluation was trustworthy. A clumper whose
            // island scope could not be resolved this frame contributes nothing, which writes
            // the clean unclumped mesh - and caching that result would LATCH it: the signature
            // never changes again on its own, so the group rests unclumped until some unrelated
            // edit dirties it. Leaving the cache untouched makes the next frame retry instead.
            //
            // Cache the signature computed AFTER the evaluation, not the one computed before it.
            // EvaluateGroup re-marks every card's clump override, so the post-evaluation signature
            // describes the state the group was actually left in. Caching the pre-evaluation value
            // stored a signature the group no longer had, which meant the very next frame always
            // looked dirty - and, worse, a frame where POST had dropped the overrides could hash
            // back to the cached value and be skipped.
            bool trustworthy = EvaluateGroup(groupId, groupClumpers, groupCards);
            if (trustworthy) lastGroupSignature[groupId] = ComputeGroupSignature(groupId, groupClumpers, groupCards);
            else lastGroupSignature.Remove(groupId);
        }

        foreach (int stale in lastGroupSignature.Keys.Where(g => !groups.Contains(g)).ToArray())
            lastGroupSignature.Remove(stale);
    }

    void RestoreRemovedGroups(HairCard[] allCards, HashSet<int> currentGroups)
    {
        foreach (int oldGroup in overriddenGroups.Where(g => !currentGroups.Contains(g)).ToArray())
        {
            foreach (HairCard card in allCards)
            {
                if (card == null || card.groupId != oldGroup) continue;
                card.ClearExternalClumpOverride();
                card.GenerateMesh();
            }
            overriddenGroups.Remove(oldGroup);
            lastGroupSignature.Remove(oldGroup);
        }
    }

    // Returns false when this evaluation must not be cached - see the caller. That happens when
    // a clumper's island scope could not be resolved, which is a transient physics probe result,
    // not a real change in the group.
    bool EvaluateGroup(int groupId, List<GroupClumperManager.GroupClumper> clumpers, HairCard[] groupCards)
    {
        if (groupCards == null || groupCards.Length == 0)
        {
            overriddenGroups.Remove(groupId);
            return true;
        }

        bool scopeResolved = true;

        Dictionary<HairCard, CleanMeshData> clean = new Dictionary<HairCard, CleanMeshData>();
        Dictionary<HairCard, Vector3[]> working = new Dictionary<HairCard, Vector3[]>();

        foreach (HairCard card in groupCards)
        {
            if (card == null) continue;
            CleanMeshData source = BuildCleanMesh(card);
            if (source == null || source.vertices == null) continue;
            clean[card] = source;
            working[card] = (Vector3[])source.vertices.Clone();
        }

        bool anyActive = false;
        foreach (GroupClumperManager.GroupClumper clumper in clumpers)
        {
            if (clumper == null || clumper.amount <= .0001f) continue;
            anyActive = true;

            // SurfaceIslandScope.TryGetIslandAtWorldPoint is a Physics.Raycast over a .025 probe.
            // A single miss used to drop this clumper's whole contribution for the frame, writing
            // the clean unclumped mesh. Remember the last island this clumper resolved to and
            // reuse it on a miss - the clumper has not moved, so the previous answer is still the
            // right one. Only when there is no previous answer at all do we skip, and then the
            // result is reported as untrustworthy so the caller retries instead of latching.
            bool contiguous = SurfaceIslandScope.IsClumperContiguous(clumper.groupId);
            int scopeIsland = -1;
            if (contiguous)
            {
                if (SurfaceIslandScope.TryGetIslandAtWorldPoint(clumper.center, clumper.normal, out scopeIsland))
                {
                    lastResolvedIsland[clumper.id] = scopeIsland;
                }
                else if (lastResolvedIsland.TryGetValue(clumper.id, out int cachedIsland))
                {
                    scopeIsland = cachedIsland;
                }
                else
                {
                    scopeResolved = false;
                    continue;
                }
            }

            // Order MUST be deterministic before BuildLeaders. groupCards comes from
            // FindObjectsByType, whose ordering Unity does not guarantee and which can differ
            // between frames. BuildLeaders indexes straight into this array for the non-Singular
            // modes - `cards[seed % cards.Length]` for DispersedEvenly, and the weighted pool
            // for FromPoint - so an unstable order silently picks DIFFERENT leaders on different
            // frames. Same clumper, same settings, different clump: whole bands of cards swap
            // between leaders and appear to pop in and out. Sorting by instance id makes leader
            // selection a pure function of the clumper's own seed, as it was always meant to be.
            HairCard[] scopedCards = groupCards.Where(c =>
                c != null && clean.ContainsKey(c) &&
                (!contiguous || SurfaceIslandScope.SameIsland(c, scopeIsland)))
                .OrderBy(c => c.GetInstanceID())
                .ToArray();
            if (scopedCards.Length < 2) continue;

            List<HairCard> leaders = BuildLeaders(clumper, scopedCards);
            if (leaders.Count == 0) continue;

            foreach (HairCard card in scopedCards)
            {
                if (!clean.TryGetValue(card, out CleanMeshData sourceData) ||
                    !working.TryGetValue(card, out Vector3[] current)) continue;

                HairCard leader = FindAssignedLeader(card, leaders);
                if (leader == null || leader == card ||
                    !clean.TryGetValue(leader, out CleanMeshData leaderData)) continue;

                float influence = Mathf.Clamp01(clumper.amount * ZoneWeight(card, clumper));
                if (influence <= .0001f) continue;
                ApplyClumpAdditive(card, current, sourceData.vertices, leader, leaderData.vertices, influence);
            }
        }

        foreach (KeyValuePair<HairCard, Vector3[]> pair in working)
        {
            if (!clean.TryGetValue(pair.Key, out CleanMeshData source)) continue;

            pair.Key.ClearExternalClumpOverride();
            WriteFullMesh(pair.Key, source, pair.Value);
            if (anyActive) pair.Key.MarkExternalClumpOverride();
        }

        if (anyActive) overriddenGroups.Add(groupId);
        else overriddenGroups.Remove(groupId);

        return scopeResolved;
    }

    static int ComputeGroupSignature(int groupId, List<GroupClumperManager.GroupClumper> clumpers, HairCard[] cards)
    {
        unchecked
        {
            int hash = 17;
            hash = Mix(hash, groupId);
            hash = Mix(hash, SurfaceIslandScope.IsClumperContiguous(groupId) ? 1 : 0);
            hash = Mix(hash, clumpers != null ? clumpers.Count : 0);

            if (clumpers != null)
            {
                foreach (GroupClumperManager.GroupClumper c in clumpers)
                {
                    if (c == null) continue;
                    hash = Mix(hash, c.id);
                    hash = Mix(hash, (int)c.mode);
                    hash = Mix(hash, c.amount.GetHashCode());
                    hash = Mix(hash, c.count);
                    hash = Mix(hash, c.seed);
                    hash = Mix(hash, c.radius.GetHashCode());
                    hash = Mix(hash, c.falloff.GetHashCode());
                    hash = Mix(hash, c.center.x.GetHashCode());
                    hash = Mix(hash, c.center.y.GetHashCode());
                    hash = Mix(hash, c.center.z.GetHashCode());
                    hash = Mix(hash, c.normal.x.GetHashCode());
                    hash = Mix(hash, c.normal.y.GetHashCode());
                    hash = Mix(hash, c.normal.z.GetHashCode());
                }
            }

            hash = Mix(hash, cards != null ? cards.Length : 0);
            if (cards != null)
            {
                // Order-independent accumulation instead of sorting the array first.
                //
                // This ran TWICE per dirty group per frame (once to test, once to re-cache),
                // and each OrderBy is an O(N log N) quicksort that allocates a buffer, a key
                // array and an index map - just to make the result stable. Summing an
                // independent per-card sub-hash is stable for free.
                //
                // Safe because this signature is only ever compared against the previously
                // cached value for the SAME group in the SAME process. It is never persisted,
                // never compared across groups and never used as an index, so it has to be
                // deterministic, not canonical.
                //
                // Sum rather than XOR: XOR cancels identical pairs, and while GetInstanceID
                // makes exact sub-hash collisions very unlikely, sum degrades more gracefully.
                //
                // DO NOT apply this to the OrderBy in EvaluateGroup - that one is load-bearing.
                // BuildLeaders indexes straight into the sorted array, so an unstable order
                // there picks different leaders on different frames and whole bands of cards
                // visibly pop between clumps.
                int cardAccumulator = 0;
                foreach (HairCard card in cards)
                {
                    if (card == null) continue;
                    int cardHash = 17;
                    cardHash = Mix(cardHash, card.GetInstanceID());
                    cardHash = Mix(cardHash, card.GetGeneratedMeshSignature());

                    // Whether the card is still rendering OUR mesh is part of what makes this
                    // group clean, and it is not derivable from the source state.
                    //
                    // PostAffectorManager (order 3300) and PostVarianceAffectorBridge (3500) can
                    // both write the same card in one frame. The first write changes the card's
                    // source signature, which drops the clump override and rebuilds clean
                    // geometry; the second write restores the ORIGINAL source signature. By the
                    // time this authority runs at 5255 the source hash is byte-identical to the
                    // cached one, so the group looked clean and was skipped - leaving every
                    // POST-covered card rendering unclumped until the next slider tick dirtied
                    // it again. That is the two-state mesh flip-flop: 875 overrides held one
                    // frame, 404 the next, with an unchanged group signature across both.
                    int overrideHeld = 0;
                    if (card.HasExternalClumpOverride()) overrideHeld = 1;
                    cardHash = Mix(cardHash, overrideHeld);

                    Vector3 root = RootWorld(card);
                    cardHash = Mix(cardHash, root.x.GetHashCode());
                    cardHash = Mix(cardHash, root.y.GetHashCode());
                    cardHash = Mix(cardHash, root.z.GetHashCode());

                    Vector3 p = card.transform.position;
                    Quaternion q = card.transform.rotation;
                    cardHash = Mix(cardHash, p.x.GetHashCode());
                    cardHash = Mix(cardHash, p.y.GetHashCode());
                    cardHash = Mix(cardHash, p.z.GetHashCode());
                    cardHash = Mix(cardHash, q.x.GetHashCode());
                    cardHash = Mix(cardHash, q.y.GetHashCode());
                    cardHash = Mix(cardHash, q.z.GetHashCode());
                    cardHash = Mix(cardHash, q.w.GetHashCode());

                    unchecked { cardAccumulator += cardHash; }
                }

                hash = Mix(hash, cardAccumulator);
            }
            return hash;
        }
    }

    static int Mix(int hash, int value)
    {
        unchecked { return hash * 31 + value; }
    }

    static List<HairCard> BuildLeaders(GroupClumperManager.GroupClumper clumper, HairCard[] cards)
    {
        List<HairCard> leaders = new List<HairCard>();
        if (cards == null || cards.Length == 0) return leaders;
        int wanted = clumper.mode == GroupClumperManager.ClumpMode.Singular ? 1 : Mathf.Clamp(clumper.count, 1, cards.Length);
        System.Random rng = new System.Random(clumper.seed);

        if (clumper.mode == GroupClumperManager.ClumpMode.Singular)
        {
            leaders.Add(cards.OrderBy(c => (RootWorld(c) - clumper.center).sqrMagnitude).First());
            return leaders;
        }

        if (clumper.mode == GroupClumperManager.ClumpMode.DispersedEvenly)
        {
            HairCard first = cards[Mathf.Abs(clumper.seed) % cards.Length];
            leaders.Add(first);
            while (leaders.Count < wanted)
            {
                HairCard best = null;
                float bestScore = float.NegativeInfinity;
                foreach (HairCard candidate in cards)
                {
                    if (leaders.Contains(candidate)) continue;
                    float nearestD2 = leaders.Min(l => (RootWorld(candidate) - RootWorld(l)).sqrMagnitude);
                    float score = nearestD2 + (float)rng.NextDouble() * .000001f;
                    if (score > bestScore) { bestScore = score; best = candidate; }
                }
                if (best == null) break;
                leaders.Add(best);
            }
            return leaders;
        }

        List<HairCard> pool = cards.ToList();
        float outer = Mathf.Max(.001f, clumper.radius + clumper.falloff);
        while (leaders.Count < wanted && pool.Count > 0)
        {
            float total = 0f;
            float[] weights = new float[pool.Count];
            for (int i = 0; i < pool.Count; i++)
            {
                float d = Vector3.Distance(RootWorld(pool[i]), clumper.center);
                float normalized = Mathf.Clamp01(d / outer);
                float w = Mathf.Pow(1f - normalized, 2f) + .015f;
                weights[i] = w;
                total += w;
            }
            double pick = rng.NextDouble() * total;
            float acc = 0f;
            int chosen = pool.Count - 1;
            for (int i = 0; i < pool.Count; i++)
            {
                acc += weights[i];
                if (pick <= acc) { chosen = i; break; }
            }
            leaders.Add(pool[chosen]);
            pool.RemoveAt(chosen);
        }
        return leaders;
    }

    static CleanMeshData BuildCleanMesh(HairCard card)
    {
        if (card == null) return null;

        const int columns = HairCard.CrossSectionColumns;
        int segments = Mathf.Clamp(card.segments, 1, 60);
        int vertexCount = (segments + 1) * columns;
        Vector3[] vertices = new Vector3[vertexCount];
        Vector2[] uvs = new Vector2[vertexCount];
        int[] triangles = new int[segments * 12];


        // Segment density remap, spine and path-following section frames come straight
        // from HairCard, so this "clean" reconstruction cannot drift from GenerateMesh.
        // It once had its own copy of the loop, which is how a clumped card silently
        // lost its curl shape and ignored its density profile entirely.
        float cardLength = Mathf.Max(.0001f, card.length);
        float[] segmentT = new float[segments + 1];
        Vector3[] segmentSpine = new Vector3[segments + 1];
        Quaternion[] segmentFrame = new Quaternion[segments + 1];
        HairCard.BuildSegmentFrames(card, segments, cardLength, segmentT, segmentSpine, segmentFrame);

        for (int i = 0; i <= segments; i++)
        {
            float t = segmentT[i];
            float z = t * cardLength;
            // Shared with GenerateMesh so a Width taper reaches clumped cards too. Computed
            // per row because the Width profile curve is a function of t.
            float span;
            float ridge;
            HairCard.EvaluateCrossSection(card, t, out span, out ridge);
            int index = i * columns;

            // HairCard.EvaluateCurl is the shared coil definition, so this rebuild keeps
            // both the offset and the bank roll identical to GenerateMesh. The bank
            // shapes the section, then the offset moves it.
            Vector3 curlOffset;
            Quaternion bankRotation;
            HairCard.EvaluateCurl(card.groupId, card.curlFrequency, card.curlDiameter, t, out curlOffset, out bankRotation, card.mirrored);

            // Shared with GenerateMesh so a wavy card stays wavy once clumped or neutralised.
            // Skipping this is how curl and segment density each silently reverted here before.
            Vector3 waveOffset;
            HairCard.EvaluateWave(card.groupId, card.waveAmplitude, card.waveFrequency, t, out waveOffset, card.mirrored);

            Vector3 sectionOrigin = new Vector3(0f, 0f, z);
            Vector3 left = sectionOrigin + bankRotation * new Vector3(-span, 0f, 0f) + curlOffset + waveOffset;
            Vector3 center = sectionOrigin + bankRotation * new Vector3(0f, ridge, 0f) + curlOffset + waveOffset;
            Vector3 right = sectionOrigin + bankRotation * new Vector3(span, 0f, 0f) + curlOffset + waveOffset;

            Vector3 spinePoint = segmentSpine[i];
            Quaternion sectionFrame = segmentFrame[i];
            vertices[index] = spinePoint + sectionFrame * (left - sectionOrigin);
            vertices[index + 1] = spinePoint + sectionFrame * (center - sectionOrigin);
            vertices[index + 2] = spinePoint + sectionFrame * (right - sectionOrigin);

            float baseULeft = card.uScale < 0f ? 1f : 0f;
            float baseURight = card.uScale < 0f ? 0f : 1f;
            float finalULeft = baseULeft * Mathf.Abs(card.uScale) + card.uOffset;
            float finalURight = baseURight * Mathf.Abs(card.uScale) + card.uOffset;
            float finalUCenter = (finalULeft + finalURight) * .5f;

            float absVScale = Mathf.Abs(card.vScale);
            float baseV = (1f - t) * absVScale;
            if (card.vScale < 0f) baseV = absVScale - baseV;
            float finalV = baseV + card.vOffset;

            uvs[index] = new Vector2(finalULeft, finalV);
            uvs[index + 1] = new Vector2(finalUCenter, finalV);
            uvs[index + 2] = new Vector2(finalURight, finalV);
        }

        int triIndex = 0;
        for (int i = 0; i < segments; i++)
        {
            int row = i * columns;
            int next = row + columns;

            triangles[triIndex++] = row;
            triangles[triIndex++] = next;
            triangles[triIndex++] = row + 1;
            triangles[triIndex++] = row + 1;
            triangles[triIndex++] = next;
            triangles[triIndex++] = next + 1;

            triangles[triIndex++] = row + 1;
            triangles[triIndex++] = next + 1;
            triangles[triIndex++] = row + 2;
            triangles[triIndex++] = row + 2;
            triangles[triIndex++] = next + 1;
            triangles[triIndex++] = next + 2;
        }

        return new CleanMeshData
        {
            vertices = vertices,
            uvs = uvs,
            triangles = triangles
        };
    }

    static void ApplyClumpAdditive(HairCard source, Vector3[] current, Vector3[] sourceClean, HairCard leader, Vector3[] leaderClean, float influence)
    {
        const int columns = HairCard.CrossSectionColumns;
        if (current == null || sourceClean == null || leaderClean == null || current.Length != sourceClean.Length) return;
        int rows = current.Length / columns;
        for (int row = 1; row < rows; row++)
        {
            float t = (float)row / (rows - 1);
            float along = t * t * (3f - 2f * t);
            float w = Mathf.Clamp01(influence * along);
            if (w <= .0001f) continue;

            int index = row * columns;
            Vector3 ownCenter = (sourceClean[index] + sourceClean[index + 2]) * .5f;
            Vector3 leaderWorld = SampleCentreWorld(leader, leaderClean, t);
            Vector3 leaderLocal = source.transform.InverseTransformPoint(leaderWorld);
            Vector3 delta = (leaderLocal - ownCenter) * w;
            current[index] += delta;
            current[index + 1] += delta;
            current[index + 2] += delta;
        }
    }

    static void WriteFullMesh(HairCard card, CleanMeshData source, Vector3[] vertices)
    {
        // HairCard.GetLiveMesh(), never MeshFilter.mesh. The MeshFilter getter INSTANTIATES:
        // it duplicates the mesh and leaves the duplicate on the filter while HairCard goes on
        // writing into the original. One evaluation pass here was enough to divorce every card
        // in the group permanently - CLUMPER kept painting the rendered duplicate, GenerateMesh
        // kept painting the orphan, and from then on no POST edit, slider move or clumper
        // removal could ever change what was on screen again.
        if (card == null) return;
        Mesh mesh = card.GetLiveMesh();
        if (mesh == null || source == null || vertices == null) return;

        mesh.Clear();
        mesh.vertices = vertices;
        mesh.uv = source.uvs;
        mesh.triangles = source.triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
    }

    static Vector3 SampleCentreWorld(HairCard card, Vector3[] vertices, float t)
    {
        const int columns = HairCard.CrossSectionColumns;
        int rows = vertices.Length / columns;
        if (rows <= 0) return card.transform.position;
        float rowF = Mathf.Clamp01(t) * (rows - 1);
        int a = Mathf.Clamp(Mathf.FloorToInt(rowF), 0, rows - 1);
        int b = Mathf.Min(a + 1, rows - 1);
        float f = rowF - a;
        Vector3 ca = (vertices[a * columns] + vertices[a * columns + 2]) * .5f;
        Vector3 cb = (vertices[b * columns] + vertices[b * columns + 2]) * .5f;
        return card.transform.TransformPoint(Vector3.Lerp(ca, cb, f));
    }

    static HairCard FindAssignedLeader(HairCard card, List<HairCard> leaders)
    {
        Vector3 p = RootWorld(card);
        HairCard best = null;
        float bestD2 = float.PositiveInfinity;
        foreach (HairCard leader in leaders)
        {
            if (leader == null) continue;
            float d2 = (RootWorld(leader) - p).sqrMagnitude;
            if (d2 < bestD2) { bestD2 = d2; best = leader; }
        }
        return best;
    }

    static float ZoneWeight(HairCard card, GroupClumperManager.GroupClumper clumper)
    {
        if (clumper.mode == GroupClumperManager.ClumpMode.DispersedEvenly) return 1f;
        float d = Vector3.Distance(RootWorld(card), clumper.center);
        float radius = Mathf.Max(.001f, clumper.radius);
        float outer = radius + Mathf.Max(0f, clumper.falloff);
        if (d <= radius) return 1f;
        if (clumper.falloff <= .000001f || d >= outer) return 0f;
        return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(outer, radius, d));
    }

    static Vector3 RootWorld(HairCard card)
    {
        Vector3 p = card.GetSpawnHitPoint();
        return p == Vector3.zero ? card.transform.position : p;
    }
}
