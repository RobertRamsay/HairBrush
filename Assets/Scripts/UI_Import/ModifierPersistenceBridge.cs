using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class ModifierPersistenceBridge : MonoBehaviour
{
    private readonly Dictionary<int, int> clumpSeeds = new();
    private float nextScan;
    private ClumpLayerManager clumpManager;
    private GroomVarianceController variance;
    private PostAffectorManager postAffectors;

    private struct CardState
    {
        public HairCard card;
        public float length, width, bend, twist, embed, ox, oy, oz, uScale, vScale, uOffset, vOffset;
        public int segments;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        var go = new GameObject("ModifierPersistenceBridge");
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
        SetNewLayerDefaultTo20();
        InstallClumpRandomButtons();
        TryRestorePendingProject();
    }

    void TryRestorePendingProject()
    {
        var data = HairProjectSaveData.PendingModifierRestore;
        if (data == null || clumpManager == null || variance == null || postAffectors == null) return;
        int expected = data.hairCards != null ? data.hairCards.Count : 0;
        if (FindObjectsByType<HairCard>(FindObjectsSortMode.None).Length < expected) return;

        HairProjectSaveData.PendingModifierRestore = null;
        variance.ClearSavedSettings();
        if (data.groups != null)
            foreach (var g in data.groups) RestoreGroup(g);
    }

    void SetNewLayerDefaultTo20()
    {
        if (clumpManager == null) return;
        FieldInfo f = typeof(ClumpLayerManager).GetField("layers", BindingFlags.Instance | BindingFlags.NonPublic);
        IDictionary d = f?.GetValue(clumpManager) as IDictionary;
        if (d == null) return;
        foreach (DictionaryEntry e in d)
        {
            var l = e.Value as ClumpLayerManager.ClumpLayer;
            if (l != null && l.pointCount == 100 && l.points.Count == 0) l.pointCount = 20;
        }
    }

    void InstallClumpRandomButtons()
    {
        foreach (RectTransform modifier in FindObjectsByType<RectTransform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).Where(r => r.name.StartsWith("ClumpModifier_")))
        {
            if (!int.TryParse(modifier.name.Substring("ClumpModifier_".Length), out int groupId)) continue;
            Transform regen = modifier.Cast<Transform>().FirstOrDefault(t => t.name == "REGENERATE POINTS");
            if (regen == null || modifier.Find("RANDOMIZE CLUMP POINTS") != null) continue;

            GameObject go = new GameObject("RANDOMIZE CLUMP POINTS", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
            go.transform.SetParent(modifier, false);
            go.transform.SetSiblingIndex(regen.GetSiblingIndex() + 1);
            go.GetComponent<RectTransform>().sizeDelta = new Vector2(0, 24);
            go.GetComponent<LayoutElement>().preferredHeight = 24;
            go.GetComponent<Image>().color = new Color(.27f, .34f, .20f);

            var text = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI)).GetComponent<TextMeshProUGUI>();
            text.transform.SetParent(go.transform, false);
            text.rectTransform.anchorMin = Vector2.zero;
            text.rectTransform.anchorMax = Vector2.one;
            text.rectTransform.offsetMin = Vector2.zero;
            text.rectTransform.offsetMax = Vector2.zero;
            text.text = "R  RANDOMIZE POINTS";
            text.fontSize = 11;
            text.alignment = TextAlignmentOptions.Center;
            text.color = Color.white;
            text.raycastTarget = false;

            int id = groupId;
            go.GetComponent<Button>().onClick.AddListener(() =>
            {
                clumpSeeds[id] = UnityEngine.Random.Range(0, 1000000);
                RegenerateSeeded(id);
            });
        }
    }

    public int GetClumpSeed(int id) => clumpSeeds.TryGetValue(id, out int s) ? s : 0;
    public void SetClumpSeed(int id, int seed) { clumpSeeds[id] = seed; }

    public void RegenerateSeeded(int groupId)
    {
        if (clumpManager == null) return;
        MethodInfo get = typeof(ClumpLayerManager).GetMethod("GetOrCreateLayer", BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo apply = typeof(ClumpLayerManager).GetMethod("ApplyLayer", BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo refresh = typeof(ClumpLayerManager).GetMethod("RefreshGuideVisuals", BindingFlags.Instance | BindingFlags.NonPublic);
        var layer = get?.Invoke(clumpManager, new object[] { groupId }) as ClumpLayerManager.ClumpLayer;
        if (layer == null) return;

        HairCard[] cards = FindObjectsByType<HairCard>(FindObjectsSortMode.None).Where(c => c.groupId == groupId).ToArray();
        layer.pointCount = Mathf.Clamp(layer.pointCount, 0, 100);
        layer.points.Clear();
        if (cards.Length == 0 || layer.pointCount == 0)
        {
            apply?.Invoke(clumpManager, new object[] { layer });
            return;
        }

        System.Random rng = new System.Random(GetClumpSeed(groupId));
        for (int i = 0; i < layer.pointCount; i++)
        {
            HairCard a = cards[rng.Next(cards.Length)], b = cards[rng.Next(cards.Length)];
            float blend = (float)rng.NextDouble();
            Vector3 p = Vector3.Lerp(a.GetSpawnHitPoint(), b.GetSpawnHitPoint(), blend);
            Vector3 n = Vector3.Slerp(a.GetSurfaceNormal().normalized, b.GetSurfaceNormal().normalized, blend).normalized;
            RaycastHit? hit = Physics.RaycastAll(p + n * .12f, -n, .30f)
                .Where(h => h.collider.GetComponent<HairCard>() == null)
                .OrderBy(h => h.distance)
                .Cast<RaycastHit?>()
                .FirstOrDefault();
            if (hit.HasValue) { p = hit.Value.point; n = hit.Value.normal.normalized; }
            layer.points.Add(new ClumpLayerManager.ClumpPoint { position = p, normal = n, strength = 0 });
        }
        apply?.Invoke(clumpManager, new object[] { layer });
        refresh?.Invoke(clumpManager, new object[] { layer });
    }

    public void PopulateGroupSave(GroupSaveData g)
    {
        if (variance != null) g.variances = variance.ExportGroupSettings(g.groupId);
        if (postAffectors != null) g.postAffectors = postAffectors.ExportGroup(g.groupId);
        if (clumpManager == null) return;

        MethodInfo get = typeof(ClumpLayerManager).GetMethod("GetOrCreateLayer", BindingFlags.Instance | BindingFlags.NonPublic);
        var l = get?.Invoke(clumpManager, new object[] { g.groupId }) as ClumpLayerManager.ClumpLayer;
        if (l == null) return;

        var d = new ClumpLayerSaveData
        {
            enabled = l.enabled,
            pointCount = l.pointCount,
            generationSeed = GetClumpSeed(g.groupId),
            globalStrength = l.globalStrength,
            brushRadius = l.brushRadius,
            brushStrength = l.brushStrength,
            brushFalloff = l.brushFalloff,
            brushValue = l.brushValue,
            debugMode = (int)l.debugMode,
            curveEarly = l.curve.Evaluate(.25f),
            curveMid = l.curve.Evaluate(.65f),
            curveTip = l.curve.Evaluate(1)
        };
        foreach (var p in l.points)
            d.points.Add(new ClumpPointSaveData { posX = p.position.x, posY = p.position.y, posZ = p.position.z, normalX = p.normal.x, normalY = p.normal.y, normalZ = p.normal.z, strength = p.strength });
        g.clump = d;
    }

    public void RestoreGroup(GroupSaveData g)
    {
        // HairCardSaveData stores the saved visible card state. Restore modifier controls,
        // then put that exact upstream state back before POST and CLUMP evaluate.
        List<CardState> savedCards = CaptureGroupCardState(g.groupId);
        if (variance != null) variance.ImportGroupSettings(g.groupId, g.variances);
        RestoreGroupCardState(savedCards);

        if (postAffectors != null) postAffectors.ImportGroup(g.groupId, g.postAffectors);

        if (g.clump == null || clumpManager == null) return;
        MethodInfo get = typeof(ClumpLayerManager).GetMethod("GetOrCreateLayer", BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo apply = typeof(ClumpLayerManager).GetMethod("ApplyLayer", BindingFlags.Instance | BindingFlags.NonPublic);
        var l = get?.Invoke(clumpManager, new object[] { g.groupId }) as ClumpLayerManager.ClumpLayer;
        if (l == null) return;

        var d = g.clump;
        l.enabled = d.enabled;
        l.pointCount = d.pointCount;
        l.globalStrength = d.globalStrength;
        l.brushRadius = d.brushRadius;
        l.brushStrength = d.brushStrength;
        l.brushFalloff = d.brushFalloff;
        l.brushValue = d.brushValue;
        l.debugMode = (ClumpLayerManager.DebugMode)d.debugMode;
        l.curve = new AnimationCurve(new Keyframe(0, 0), new Keyframe(.25f, d.curveEarly), new Keyframe(.65f, d.curveMid), new Keyframe(1, d.curveTip));
        l.points.Clear();
        if (d.points != null)
            foreach (var p in d.points)
                l.points.Add(new ClumpLayerManager.ClumpPoint { position = new Vector3(p.posX, p.posY, p.posZ), normal = new Vector3(p.normalX, p.normalY, p.normalZ), strength = p.strength });
        SetClumpSeed(g.groupId, d.generationSeed);
        apply?.Invoke(clumpManager, new object[] { l });
    }

    List<CardState> CaptureGroupCardState(int groupId)
    {
        List<CardState> result = new();
        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None).Where(c => c.groupId == groupId))
        {
            result.Add(new CardState
            {
                card = card,
                length = card.length,
                width = card.width,
                segments = card.segments,
                bend = card.bendAngle,
                twist = card.twistAngle,
                embed = card.GetEmbedDepth(),
                ox = card.GetOffsetX(),
                oy = card.GetOffsetY(),
                oz = card.GetOffsetZ(),
                uScale = card.uScale,
                vScale = card.vScale,
                uOffset = card.uOffset,
                vOffset = card.vOffset
            });
        }
        return result;
    }

    void RestoreGroupCardState(List<CardState> states)
    {
        foreach (CardState s in states)
        {
            if (s.card == null) continue;
            s.card.SetParameters(s.length, s.width, s.segments, s.bend, s.twist, s.ox, s.oy, s.oz, s.embed, 1f, s.uScale, s.vScale, s.uOffset, s.vOffset);
        }
    }
}
