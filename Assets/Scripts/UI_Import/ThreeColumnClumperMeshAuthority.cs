using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// Final native 3-column CLUMPER evaluator.
//
// Pipeline invariant:
//   canonical/group -> POST/local variance -> HairCard generated mesh -> CLUMPER final vertices.
//
// CLUMPER never owns or caches the authored HairCard shape. At the start of every LateUpdate
// it asks each affected HairCard to regenerate its current upstream mesh, snapshots that exact
// result for this frame, then layers every clumper point additively from the same clean source.
// HairCard still contains a retired internal clump path, so the clean regeneration explicitly
// clears that legacy state first; otherwise old clump vertices can be regenerated and then have
// the current CLUMPER applied on top, producing frame-to-frame accumulation.
[DefaultExecutionOrder(5255)]
public class ThreeColumnClumperMeshAuthority : MonoBehaviour
{
    private GroupClumperManager manager;

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
        if (clumpers.Count == 0) return;

        HairCard[] allCards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        if (allCards.Length == 0) return;

        HashSet<int> groups = new HashSet<int>(clumpers.Select(c => c.groupId));
        Dictionary<HairCard, Vector3[]> clean = new Dictionary<HairCard, Vector3[]>();
        Dictionary<HairCard, Vector3[]> working = new Dictionary<HairCard, Vector3[]>();

        foreach (HairCard card in allCards)
        {
            if (card == null || !groups.Contains(card.groupId)) continue;

            Vector3[] source = CaptureCurrentUpstreamVertices(card);
            if (source == null || source.Length < HairCard.CrossSectionColumns) continue;

            clean[card] = source;
            working[card] = (Vector3[])source.Clone();
        }

        foreach (GroupClumperManager.GroupClumper clumper in clumpers.OrderBy(c => c.id))
        {
            if (clumper == null || clumper.amount <= .0001f) continue;

            bool contiguous = SurfaceIslandScope.IsClumperContiguous(clumper.groupId);
            int scopeIsland = -1;
            if (contiguous && !SurfaceIslandScope.TryGetIslandAtWorldPoint(clumper.center, clumper.normal, out scopeIsland))
                continue;

            HairCard[] groupCards = allCards.Where(c =>
                c != null && c.groupId == clumper.groupId &&
                clean.ContainsKey(c) &&
                (!contiguous || SurfaceIslandScope.SameIsland(c, scopeIsland))).ToArray();
            if (groupCards.Length < 2) continue;

            List<HairCard> leaders = BuildLeaders(clumper, groupCards);
            if (leaders.Count == 0) continue;

            foreach (HairCard card in groupCards)
            {
                if (!clean.TryGetValue(card, out Vector3[] sourceClean) || !working.TryGetValue(card, out Vector3[] current)) continue;
                HairCard leader = FindAssignedLeader(card, leaders);
                if (leader == null || leader == card || !clean.TryGetValue(leader, out Vector3[] leaderClean)) continue;

                float influence = Mathf.Clamp01(clumper.amount * ZoneWeight(card, clumper));
                if (influence <= .0001f) continue;
                ApplyClumpAdditive(card, current, sourceClean, leader, leaderClean, influence);
            }
        }

        foreach (KeyValuePair<HairCard, Vector3[]> pair in working)
            WriteVertices(pair.Key, pair.Value);
    }

    // HairCard still exposes its original single-card clump modifier. The current GroupClumper
    // pipeline supersedes it, but stale legacy state can survive on a card. ClearClumpModifier()
    // both retires that state and regenerates the complete current HairCard mesh (topology, UVs,
    // POST-evaluated parameters and shape curves), giving this final pass a deterministic clean
    // source that can never contain last frame's CLUMPER result.
    static Vector3[] CaptureCurrentUpstreamVertices(HairCard card)
    {
        if (card == null) return null;
        card.ClearClumpModifier();

        MeshFilter mf = card.GetComponent<MeshFilter>();
        if (mf == null || mf.mesh == null || mf.mesh.vertexCount < HairCard.CrossSectionColumns)
            return null;

        return (Vector3[])mf.mesh.vertices.Clone();
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

    static void ApplyClumpAdditive(HairCard source, Vector3[] current, Vector3[] sourceClean, HairCard leader, Vector3[] leaderClean, float influence)
    {
        const int columns = HairCard.CrossSectionColumns;
        if (current == null || sourceClean == null || leaderClean == null || current.Length != sourceClean.Length) return;
        int rows = current.Length / columns;
        for (int row = 1; row < rows; row++)
        {
            float t = (float)row / (rows - 1);
            float w = influence * Mathf.SmoothStep(0f, 1f, t);
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

    static void WriteVertices(HairCard card, Vector3[] vertices)
    {
        MeshFilter mf = card.GetComponent<MeshFilter>();
        if (mf == null || mf.mesh == null || vertices == null || mf.mesh.vertexCount != vertices.Length) return;
        mf.mesh.vertices = vertices;
        mf.mesh.RecalculateNormals();
        mf.mesh.RecalculateBounds();
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
        float distance = Vector3.Distance(RootWorld(card), clumper.center);
        if (distance <= clumper.radius) return 1f;
        if (clumper.falloff <= .0001f) return 0f;
        float outer = clumper.radius + clumper.falloff;
        if (distance >= outer) return 0f;
        return Mathf.SmoothStep(1f, 0f, (distance - clumper.radius) / clumper.falloff);
    }

    static Vector3 RootWorld(HairCard card)
    {
        Vector3 p = card.GetSpawnHitPoint();
        return p == Vector3.zero ? card.transform.position : p;
    }
}
