using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// Final native 3-column CLUMPER evaluator.
//
// Every affected card is rebuilt from its current authored/evaluated parameters each frame:
// vertices + UVs + topology are created deterministically, active clumpers deform only those
// clean vertices, then the complete mesh is replaced. No previous CLUMPER output is ever read.
// This makes amount=0 a true restore and prevents topology/UV changes from wedging the stage.
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
        Dictionary<HairCard, CleanMeshData> clean = new Dictionary<HairCard, CleanMeshData>();
        Dictionary<HairCard, Vector3[]> working = new Dictionary<HairCard, Vector3[]>();

        foreach (HairCard card in allCards)
        {
            if (card == null || !groups.Contains(card.groupId)) continue;
            CleanMeshData source = BuildCleanMesh(card);
            if (source == null || source.vertices == null) continue;
            clean[card] = source;
            working[card] = (Vector3[])source.vertices.Clone();
        }

        foreach (GroupClumperManager.GroupClumper clumper in clumpers.OrderBy(c => c.id))
        {
            if (clumper == null || clumper.amount <= .0001f) continue;

            bool contiguous = SurfaceIslandScope.IsClumperContiguous(clumper.groupId);
            int scopeIsland = -1;
            if (contiguous && !SurfaceIslandScope.TryGetIslandAtWorldPoint(clumper.center, clumper.normal, out scopeIsland))
                continue;

            HairCard[] groupCards = allCards.Where(c =>
                c != null && c.groupId == clumper.groupId && clean.ContainsKey(c) &&
                (!contiguous || SurfaceIslandScope.SameIsland(c, scopeIsland))).ToArray();
            if (groupCards.Length < 2) continue;

            List<HairCard> leaders = BuildLeaders(clumper, groupCards);
            if (leaders.Count == 0) continue;

            foreach (HairCard card in groupCards)
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
            if (clean.TryGetValue(pair.Key, out CleanMeshData source))
                WriteFullMesh(pair.Key, source, pair.Value);
        }
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
        int segments = Mathf.Clamp(card.segments, 1, 36);
        int vertexCount = (segments + 1) * columns;
        Vector3[] vertices = new Vector3[vertexCount];
        Vector2[] uvs = new Vector2[vertexCount];
        int[] triangles = new int[segments * 12];

        float segmentHeight = Mathf.Max(.001f, card.length) / segments;
        float halfWidth = Mathf.Max(.0005f, card.width) * .5f;
        float ridge = card.GetCrossSectionRidgeHeight();

        for (int i = 0; i <= segments; i++)
        {
            float t = (float)i / segments;
            float z = i * segmentHeight;
            float span = halfWidth * card.flattenFactor;
            Quaternion authored = card.GetLengthProfileRotation(t);
            int index = i * columns;

            vertices[index] = authored * new Vector3(-span, 0f, z);
            vertices[index + 1] = authored * new Vector3(0f, ridge, z);
            vertices[index + 2] = authored * new Vector3(span, 0f, z);

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
