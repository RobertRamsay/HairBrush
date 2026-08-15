using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

// GroupClumperManager historically rebuilt a 2-column clean strip internally. HairCard now
// has a native 3-column convex cross-section (left / raised centre / right), so this final
// mesh authority evaluates the same CLUMPER relationship against that topology and preserves
// the ridge. It runs after GroupClumperManager; the legacy 2-column apply safely no-ops when
// its vertex count does not match the new HairCard mesh.
[DefaultExecutionOrder(5255)]
public class ThreeColumnClumperMeshAuthority : MonoBehaviour
{
    private GroupClumperManager manager;
    private FieldInfo byGroupField;

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
        Resolve();
        if (manager == null || byGroupField == null) return;

        var byGroup = byGroupField.GetValue(manager) as Dictionary<int, GroupClumperManager.GroupClumper>;
        if (byGroup == null || byGroup.Count == 0) return;

        HairCard[] allCards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        if (allCards.Length == 0) return;

        Dictionary<HairCard, Vector3[]> clean = new Dictionary<HairCard, Vector3[]>();
        foreach (HairCard card in allCards)
        {
            if (card == null || !byGroup.TryGetValue(card.groupId, out var clumper) || clumper == null || clumper.amount <= .0001f)
                continue;
            clean[card] = BuildCleanVertices(card);
        }

        foreach (var clumper in byGroup.Values)
        {
            if (clumper == null || clumper.amount <= .0001f || clumper.leaders == null || clumper.leaders.Count == 0)
                continue;

            HairCard[] groupCards = allCards.Where(c => c != null && c.groupId == clumper.groupId).ToArray();
            foreach (HairCard card in groupCards)
            {
                if (!clean.TryGetValue(card, out Vector3[] sourceClean)) continue;
                HairCard leader = FindAssignedLeader(card, clumper.leaders);
                if (leader == null || leader == card || !clean.TryGetValue(leader, out Vector3[] leaderClean)) continue;

                float influence = Mathf.Clamp01(clumper.amount * ZoneWeight(card, clumper));
                if (influence <= .0001f) continue;
                ApplyClump(card, sourceClean, leader, leaderClean, influence);
            }
        }
    }

    void Resolve()
    {
        if (manager != null) return;
        manager = FindFirstObjectByType<GroupClumperManager>();
        if (manager == null) return;
        byGroupField = typeof(GroupClumperManager).GetField("byGroup", BindingFlags.Instance | BindingFlags.NonPublic);
    }

    static Vector3[] BuildCleanVertices(HairCard card)
    {
        const int columns = HairCard.CrossSectionColumns;
        int segments = Mathf.Clamp(card.segments, 1, 36);
        Vector3[] vertices = new Vector3[(segments + 1) * columns];
        float segmentHeight = Mathf.Max(.001f, card.length) / segments;
        float halfWidth = Mathf.Max(.0005f, card.width) * .5f;
        float ridge = card.GetCrossSectionRidgeHeight();

        for (int i = 0; i <= segments; i++)
        {
            float t = (float)i / segments;
            float z = i * segmentHeight;
            float span = halfWidth * card.flattenFactor;
            Quaternion authored = Quaternion.Euler(card.bendAngle * (t * t), 0f, card.twistAngle * t);
            int index = i * columns;
            vertices[index] = authored * new Vector3(-span, 0f, z);
            vertices[index + 1] = authored * new Vector3(0f, ridge, z);
            vertices[index + 2] = authored * new Vector3(span, 0f, z);
        }
        return vertices;
    }

    static void ApplyClump(HairCard source, Vector3[] sourceClean, HairCard leader, Vector3[] leaderClean, float influence)
    {
        const int columns = HairCard.CrossSectionColumns;
        MeshFilter mf = source.GetComponent<MeshFilter>();
        if (mf == null || mf.mesh == null || sourceClean == null || leaderClean == null) return;
        if (mf.mesh.vertexCount != sourceClean.Length || sourceClean.Length % columns != 0) return;

        Vector3[] vertices = (Vector3[])sourceClean.Clone();
        int rows = vertices.Length / columns;
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
            Vector3 targetCenter = Vector3.Lerp(ownCenter, leaderLocal, w);
            Vector3 delta = targetCenter - ownCenter;

            // Move the whole row as one cross-section. Width and centre ridge are preserved.
            vertices[index] = sourceClean[index] + delta;
            vertices[index + 1] = sourceClean[index + 1] + delta;
            vertices[index + 2] = sourceClean[index + 2] + delta;
        }

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
            if (d2 < bestD2)
            {
                bestD2 = d2;
                best = leader;
            }
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
