using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

// Current project-format modifier persistence only.
public class ModifierPersistenceBridge : MonoBehaviour
{
    private readonly Dictionary<int, int> clumpSeeds = new();
    private float nextScan;
    private ClumpLayerManager clumpManager;
    private GroomVarianceController variance;
    private PostAffectorManager postAffectors;
    private ClumpInlineGroomController inlineClump;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        GameObject go = new GameObject("ModifierPersistenceBridge");
        DontDestroyOnLoad(go);
        go.AddComponent<ModifierPersistenceBridge>();
    }

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + .2f;
        if (clumpManager == null) clumpManager = FindFirstObjectByType<ClumpLayerManager>();
        if (variance == null) variance = FindFirstObjectByType<GroomVarianceController>();
        if (postAffectors == null) postAffectors = FindFirstObjectByType<PostAffectorManager>();
        if (inlineClump == null) inlineClump = FindFirstObjectByType<ClumpInlineGroomController>();
        TryRestorePendingProject();
    }

    void TryRestorePendingProject()
    {
        HairProjectSaveData data = HairProjectSaveData.PendingModifierRestore;
        if (data == null || clumpManager == null || variance == null || postAffectors == null) return;
        int expected = data.hairCards != null ? data.hairCards.Count : 0;
        if (FindObjectsByType<HairCard>(FindObjectsSortMode.None).Length < expected) return;

        // Pre-v2 files stored already-modified visible cards and also stored the recipe
        // that produced them. Replaying that recipe double-applies variance/POST and can
        // make cards spin or expand. We intentionally dropped compatibility: legacy files
        // keep their saved visible card geometry, but their modifiers are not replayed.
        if (data.formatVersion < CanonicalProjectStateBridge.CurrentFormatVersion)
        {
            HairProjectSaveData.PendingModifierRestore = null;
            return;
        }

        HairProjectSaveData.PendingModifierRestore = null;
        variance.ClearSavedSettings();
        postAffectors.ClearAll();
        if (data.groups != null)
            foreach (GroupSaveData g in data.groups)
                RestoreGroup(g);
    }

    public int GetClumpSeed(int id) => clumpSeeds.TryGetValue(id, out int s) ? s : 0;
    public void SetClumpSeed(int id, int seed) { clumpSeeds[id] = seed; }

    public void RegenerateSeeded(int groupId)
    {
        if (clumpManager == null) return;
        MethodInfo get = typeof(ClumpLayerManager).GetMethod("GetOrCreateLayer", BindingFlags.Instance | BindingFlags.NonPublic);
        ClumpLayerManager.ClumpLayer layer = get?.Invoke(clumpManager, new object[] { groupId }) as ClumpLayerManager.ClumpLayer;
        if (layer == null) return;

        HairCard[] cards = FindObjectsByType<HairCard>(FindObjectsSortMode.None).Where(c => c.groupId == groupId).ToArray();
        layer.pointCount = Mathf.Clamp(layer.pointCount, 0, 100);
        layer.points.Clear();
        if (cards.Length == 0 || layer.pointCount == 0)
        {
            inlineClump?.ApplyGroup(groupId);
            return;
        }

        System.Random rng = new System.Random(GetClumpSeed(groupId));
        for (int i = 0; i < layer.pointCount; i++)
        {
            HairCard a = cards[rng.Next(cards.Length)];
            HairCard b = cards[rng.Next(cards.Length)];
            float blend = (float)rng.NextDouble();
            Vector3 p = Vector3.Lerp(a.GetSpawnHitPoint(), b.GetSpawnHitPoint(), blend);
            Vector3 n = Vector3.Slerp(a.GetSurfaceNormal().normalized, b.GetSurfaceNormal().normalized, blend).normalized;
            RaycastHit? hit = Physics.RaycastAll(p + n * .12f, -n, .30f)
                .Where(h => h.collider.GetComponent<HairCard>() == null)
                .OrderBy(h => h.distance)
                .Cast<RaycastHit?>()
                .FirstOrDefault();
            if (hit.HasValue) { p = hit.Value.point; n = hit.Value.normal.normalized; }
            layer.points.Add(new ClumpLayerManager.ClumpPoint { position = p, normal = n, strength = 1f });
        }
        inlineClump?.ApplyGroup(groupId);
    }

    public void PopulateGroupSave(GroupSaveData g)
    {
        if (variance != null) g.variances = variance.ExportGroupSettings(g.groupId);
        if (postAffectors != null) g.postAffectors = postAffectors.ExportGroup(g.groupId);
        if (clumpManager == null) return;

        MethodInfo get = typeof(ClumpLayerManager).GetMethod("GetOrCreateLayer", BindingFlags.Instance | BindingFlags.NonPublic);
        ClumpLayerManager.ClumpLayer l = get?.Invoke(clumpManager, new object[] { g.groupId }) as ClumpLayerManager.ClumpLayer;
        if (l == null) return;

        ClumpLayerSaveData d = new ClumpLayerSaveData
        {
            enabled = l.enabled,
            pointCount = l.pointCount,
            generationSeed = GetClumpSeed(g.groupId),
            globalStrength = l.globalStrength,
            debugMode = (int)l.debugMode,
            curveEarly = l.curve.Evaluate(.25f),
            curveMid = l.curve.Evaluate(.65f),
            curveTip = l.curve.Evaluate(1f)
        };
        foreach (ClumpLayerManager.ClumpPoint p in l.points)
            d.points.Add(new ClumpPointSaveData { posX = p.position.x, posY = p.position.y, posZ = p.position.z, normalX = p.normal.x, normalY = p.normal.y, normalZ = p.normal.z, strength = 1f });
        g.clump = d;
    }

    public void RestoreGroup(GroupSaveData g)
    {
        if (variance != null) variance.ImportGroupSettings(g.groupId, g.variances);
        if (postAffectors != null) postAffectors.ImportGroup(g.groupId, g.postAffectors);
        if (g.clump == null || clumpManager == null) return;

        MethodInfo get = typeof(ClumpLayerManager).GetMethod("GetOrCreateLayer", BindingFlags.Instance | BindingFlags.NonPublic);
        ClumpLayerManager.ClumpLayer l = get?.Invoke(clumpManager, new object[] { g.groupId }) as ClumpLayerManager.ClumpLayer;
        if (l == null) return;

        ClumpLayerSaveData d = g.clump;
        l.enabled = d.enabled;
        l.pointCount = Mathf.Clamp(d.pointCount, 1, 100);
        l.globalStrength = Mathf.Clamp01(d.globalStrength);
        l.debugMode = (ClumpLayerManager.DebugMode)d.debugMode;
        l.curve = new AnimationCurve(new Keyframe(0, 0), new Keyframe(.25f, d.curveEarly), new Keyframe(.65f, d.curveMid), new Keyframe(1, d.curveTip));
        l.points.Clear();
        if (d.points != null)
            foreach (ClumpPointSaveData p in d.points)
                l.points.Add(new ClumpLayerManager.ClumpPoint { position = new Vector3(p.posX, p.posY, p.posZ), normal = new Vector3(p.normalX, p.normalY, p.normalZ), strength = 1f });
        SetClumpSeed(g.groupId, d.generationSeed);
        inlineClump = inlineClump != null ? inlineClump : FindFirstObjectByType<ClumpInlineGroomController>();
        inlineClump?.ApplyGroup(g.groupId);
    }
}
