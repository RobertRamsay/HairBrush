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
    private PostAffectorManager postManager;
    private readonly Dictionary<int, int> lastGroupSignature = new Dictionary<int, int>();
    private readonly HashSet<int> overriddenGroups = new HashSet<int>();

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
            List<GroupClumperManager.GroupClumper> groupClumpers = ordered
                .Where(c => c.groupId == groupId)
                .ToList();
            HairCard[] groupCards = allCards
                .Where(c => c != null && c.groupId == groupId)
                .ToArray();

            int signature = ComputeGroupSignature(groupId, groupClumpers, groupCards);
            if (lastGroupSignature.TryGetValue(groupId, out int previous) && previous == signature)
                continue;

            EvaluateGroup(groupId, groupClumpers, groupCards);
            lastGroupSignature[groupId] = signature;
        }

        foreach (int stale in lastGroupSignature.Keys.Where(g => !groups.Contains(g)).ToArray())
            lastGroupSignature.Remove(stale);
    }

    void RestoreRemovedGroups(HairCard[] allCards, HashSet<int> currentGroups)
    {
        foreach (int oldGroup in overriddenGroups.Where(g => !currentGroups.Contains(g)).ToArray())
        {
            HairCard[] groupCards = allCards
                .Where(card => card != null && card.groupId == oldGroup)
                .ToArray();

            // Release CLUMPER ownership first. Reapply POST afterwards so the final visible mesh
            // is canonical + POST, not whichever derived clump/clean mesh happened to be last.
            foreach (HairCard card in groupCards)
                card.ClearExternalClumpOverride();

            if (postManager == null) postManager = FindFirstObjectByType<PostAffectorManager>();
            if (postManager != null)
            {
                postManager.ReapplyGroup(oldGroup);
            }
            else
            {
                foreach (HairCard card in groupCards)
                    card.GenerateMesh();
            }

            overriddenGroups.Remove(oldGroup);
            lastGroupSignature.Remove(oldGroup);
        }
    }

    void EvaluateGroup(int groupId, List<GroupClumperManager.GroupClumper> clumpers, HairCard[] groupCards)
    {
        if (groupCards == null || groupCards.Length == 0)
        {
            overriddenGroups.Remove(groupId);
            return;
        }

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

            bool contiguous = SurfaceIslandScope.IsClumperContiguous(clumper.groupId);
            int scopeIsland = -1;
            if (contiguous && !SurfaceIslandScope.TryGetIslandAtWorldPoint(clumper.center, clumper.normal, out scopeIsland))
                continue;

            HairCard[] scopedCards = groupCards.Where(c =>
                c != null && clean.ContainsKey(c) &&
                (!contiguous || SurfaceIslandScope.SameIsland(c, scopeIsland))).ToArray();
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
                foreach (HairCard card in cards.OrderBy(c => c != null ? c.GetInstanceID() : 0))
                {
                    if (card == null) continue;
                    hash = Mix(hash, card.GetInstanceID());
                    hash = Mix(hash, card.GetGeneratedMeshSignature());

                    Vector3 root = RootWorld(card);
                    hash = Mix(hash, root.x.GetHashCode());
                    hash = Mix(hash, root.y.GetHashCode());
                    hash = Mix(hash, root.z.GetHashCode());

                    Vector3 p = card.transform.position;
                    Quaternion q = card.transform.rotation;
                    hash = Mix(hash, p.x.GetHashCode());
                    hash = Mix(hash, p.y.GetHashCode());
                    hash = Mix(hash, p.z.GetHashCode());
                    hash = Mix(hash, q.x.GetHashCode());
                    hash = Mix(hash, q.y.GetHashCode());
                    hash = Mix(hash, q.z.GetHashCode());
                    hash = Mix(hash, q.w.GetHashCode());
                }
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

        float halfWidth = Mathf.Max(.0005f, card.width) * .5f;
        float ridge = card.GetCrossSectionRidgeHeight();

        // Mirrors HairCard.GenerateMesh's own segment-density remap and curl offset exactly -
        // this "clean" reconstruction predates both features and was never updated to include
        // them, so a clumped card silently lost its curl shape and always used uniform segment
        // spacing regardless of its actual density profile. Root and tip stay pinned to exactly
        // 0 and 1 for the same reason GenerateMesh pins them: so Length always produces the
        // expected span even if the density curve doesn't touch its own corners.
        float previousSegmentT = 0f;

        for (int i = 0; i <= segments; i++)
        {
            float t;
            if (i == 0) t = 0f;
            else if (i == segments) t = 1f;
            else
            {
                float u = (float)i / segments;
                t = Mathf.Max(previousSegmentT, PostShapeCurveBridge.EvaluateRoot(card.groupId, GroomShapeCurveChannel.SegmentDensity, u));
            }
            previousSegmentT = t;
            float z = t * Mathf.Max(.0001f, card.length);
            float span = halfWidth * card.flattenFactor;
            int index = i * columns;

            Vector3 left = new Vector3(-span, 0f, z);
            Vector3 center = new Vector3(0f, ridge, z);
            Vector3 right = new Vector3(span, 0f, z);

            if (card.curlFrequency != 0f && card.curlDiameter > 0f)
            {
                float freqMultiplier = PostShapeCurveBridge.EvaluateRoot(card.groupId, GroomShapeCurveChannel.CurlFrequency, t);
                float diameterMultiplier = PostShapeCurveBridge.EvaluateRoot(card.groupId, GroomShapeCurveChannel.CurlDiameter, t);
                float turns = card.curlFrequency * freqMultiplier;
                float radius = card.curlDiameter * diameterMultiplier * .5f;
                float angle = turns * t * Mathf.PI * 2f;
                Vector3 curlOffset = new Vector3(radius * (Mathf.Cos(angle) - 1f), radius * Mathf.Sin(angle), 0f);
                left += curlOffset;
                center += curlOffset;
                right += curlOffset;
            }

            Quaternion authored = card.GetLengthProfileRotation(t);
            vertices[index] = authored * left;
            vertices[index + 1] = authored * center;
            vertices[index + 2] = authored * right;

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
        MeshFilter mf = card != null ? card.GetComponent<MeshFilter>() : null;
        if (mf == null || mf.mesh == null || source == null || vertices == null) return;

        Mesh mesh = mf.mesh;
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
