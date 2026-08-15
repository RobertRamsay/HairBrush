using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

// GroupClumperManager's deformation is deterministic once its leader list is fixed, but
// its original leader generation starts from FindObjectsByType runtime order. Runtime
// object/instance order is not stable across a project reload, so the same saved seed can
// otherwise select slightly different leaders. This authority supplies leaders from a
// stable authored card ordering before GroupClumperManager.LateUpdate evaluates them.
[DefaultExecutionOrder(5190)]
public class ClumperDeterministicLeaderAuthority : MonoBehaviour
{
    private GroupClumperManager manager;
    private FieldInfo byGroupField;
    private MethodInfo computeTopologyHashMethod;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<ClumperDeterministicLeaderAuthority>() != null) return;
        GameObject go = new GameObject("ClumperDeterministicLeaderAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<ClumperDeterministicLeaderAuthority>();
    }

    void LateUpdate()
    {
        Resolve();
        if (manager == null || byGroupField == null || computeTopologyHashMethod == null) return;
        if (!(byGroupField.GetValue(manager) is IDictionary dict) || dict.Count == 0) return;

        HairCard[] allCards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        if (allCards.Length == 0) return;

        foreach (DictionaryEntry entry in dict)
        {
            if (!(entry.Key is int gid) || !(entry.Value is GroupClumperManager.GroupClumper clumper) || clumper == null) continue;

            HairCard[] cards = allCards.Where(c => c != null && c.groupId == gid).ToArray();
            if (cards.Length < 1) continue;

            int runtimeTopologyHash;
            try
            {
                runtimeTopologyHash = (int)computeTopologyHashMethod.Invoke(manager, new object[] { cards, clumper });
            }
            catch
            {
                continue;
            }

            bool invalid = clumper.leaders == null || clumper.leaders.Count == 0 ||
                           clumper.leaders.Any(l => l == null) ||
                           clumper.lastTopologyHash != runtimeTopologyHash;
            if (!invalid) continue;

            List<HairCard> ordered = cards.OrderBy(CardStableKey, StringComparer.Ordinal).ToList();
            List<HairCard> leaders = BuildStableLeaders(clumper, ordered);
            if (clumper.leaders == null) clumper.leaders = new List<HairCard>();
            else clumper.leaders.Clear();
            clumper.leaders.AddRange(leaders);

            // Match the manager's current runtime topology hash so its LateUpdate accepts
            // this stable leader list rather than immediately rebuilding it from runtime order.
            clumper.lastTopologyHash = runtimeTopologyHash;
        }
    }

    void Resolve()
    {
        if (manager != null) return;
        manager = FindFirstObjectByType<GroupClumperManager>();
        if (manager == null) return;

        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        byGroupField = typeof(GroupClumperManager).GetField("byGroup", flags);
        computeTopologyHashMethod = typeof(GroupClumperManager).GetMethod("ComputeTopologyHash", flags);
    }

    static List<HairCard> BuildStableLeaders(GroupClumperManager.GroupClumper clumper, List<HairCard> ordered)
    {
        List<HairCard> result = new List<HairCard>();
        if (ordered == null || ordered.Count == 0 || clumper == null) return result;

        int wanted = clumper.mode == GroupClumperManager.ClumpMode.Singular
            ? 1
            : Mathf.Clamp(clumper.count, 1, ordered.Count);

        if (clumper.mode == GroupClumperManager.ClumpMode.Singular)
        {
            HairCard leader = ordered
                .OrderBy(c => (RootWorld(c) - clumper.center).sqrMagnitude)
                .ThenBy(CardStableKey, StringComparer.Ordinal)
                .FirstOrDefault();
            if (leader != null) result.Add(leader);
            return result;
        }

        System.Random rng = new System.Random(clumper.seed);

        if (clumper.mode == GroupClumperManager.ClumpMode.DispersedEvenly)
        {
            int start = PositiveModulo(clumper.seed, ordered.Count);
            result.Add(ordered[start]);

            while (result.Count < wanted)
            {
                HairCard best = null;
                float bestScore = float.NegativeInfinity;
                string bestKey = null;

                foreach (HairCard candidate in ordered)
                {
                    if (result.Contains(candidate)) continue;
                    float nearestD2 = result.Min(l => (RootWorld(candidate) - RootWorld(l)).sqrMagnitude);
                    float jitter = (float)rng.NextDouble() * .000001f;
                    float score = nearestD2 + jitter;
                    string key = CardStableKey(candidate);

                    if (score > bestScore || (Mathf.Approximately(score, bestScore) && string.CompareOrdinal(key, bestKey) < 0))
                    {
                        bestScore = score;
                        best = candidate;
                        bestKey = key;
                    }
                }

                if (best == null) break;
                result.Add(best);
            }
            return result;
        }

        // FROM POINT: same seeded weighted selection as GroupClumperManager, but the pool
        // order is stable, so the RNG stream maps to the same authored cards after reload.
        List<HairCard> pool = new List<HairCard>(ordered);
        float outer = Mathf.Max(.001f, clumper.radius + clumper.falloff);
        while (result.Count < wanted && pool.Count > 0)
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
                if (pick <= acc)
                {
                    chosen = i;
                    break;
                }
            }

            result.Add(pool[chosen]);
            pool.RemoveAt(chosen);
        }

        return result;
    }

    static string CardStableKey(HairCard card)
    {
        if (card == null) return string.Empty;
        Vector3 p = card.GetSpawnHitPoint();
        Vector3 n = card.GetSurfaceNormal();
        HairCard.GroomState s = card.GetCanonicalState();

        // Quantized authored values intentionally mirror the stable hashing strategy used
        // by the variance systems. No instance ID, hierarchy index or creation order appears.
        return string.Join("|",
            Q(p.x), Q(p.y), Q(p.z),
            Q(n.x), Q(n.y), Q(n.z),
            card.groupId,
            Q(s.length), Q(s.width), s.segments,
            Q(s.bend), Q(s.twist), Q(s.depth),
            Q(s.x), Q(s.y), Q(s.z),
            Q(s.uScale), Q(s.vScale), Q(s.uOffset), Q(s.vOffset));
    }

    static int Q(float value) => Mathf.RoundToInt(value * 100000f);

    static Vector3 RootWorld(HairCard card)
    {
        Vector3 p = card.GetSpawnHitPoint();
        return p == Vector3.zero ? card.transform.position : p;
    }

    static int PositiveModulo(int value, int modulus)
    {
        if (modulus <= 0) return 0;
        long v = value;
        long m = modulus;
        long r = v % m;
        if (r < 0) r += m;
        return (int)r;
    }
}
