using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Bridges the existing group variance UI into POST authoring without making
// GroomVarianceController or PostAffectorManager own each other's state.
// While a POST is active, VAR +/- and SEED edit localized variance for that POST.
// Outside POST authoring, the same rows remain the normal group variance controls.
[DefaultExecutionOrder(3500)]
public class PostVarianceAffectorBridge : MonoBehaviour
{
    private static readonly string[] Channels = { "Length", "Bend", "Twist", "AngleX", "AngleY", "AngleZ" };
    private static readonly string[] RowNames = { "Length_VarianceRow", "Bend_VarianceRow", "Twist_VarianceRow", "Angle X_VarianceRow", "Angle Y_VarianceRow", "Angle Z_VarianceRow" };

    private readonly Dictionary<int, List<VarianceChannelSaveData>> localByPost = new();
    private readonly Dictionary<int, List<VarianceChannelSaveData>> groupBase = new();

    private PostAffectorManager posts;
    private GroomVarianceController variance;
    private ModelViewer viewer;
    private FieldInfo activeIdField;
    private FieldInfo activeGroupField;
    private FieldInfo groupsField;
    private int lastActiveId = int.MinValue;
    private int lastActiveGroup = int.MinValue;
    private HairProjectSaveData cachedPending;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<PostVarianceAffectorBridge>() != null) return;
        GameObject go = new GameObject("PostVarianceAffectorBridge");
        DontDestroyOnLoad(go);
        go.AddComponent<PostVarianceAffectorBridge>();
    }

    void Update()
    {
        EnsureRefs();
        if (posts == null || variance == null || viewer == null) return;

        RestorePendingLocalVariance();
        PostAffectorManager.PostAffector active = GetActive();
        NormalizeActiveAngleDelta(active);

        int activeId = active != null ? active.id : -1;
        int activeGroup = active != null ? active.groupId : -1;

        if (activeId != lastActiveId || activeGroup != lastActiveGroup)
        {
            if (active != null)
            {
                // Capture the real group variance before replacing the visible rows
                // with this POST's localized variance values.
                groupBase[active.groupId] = Clone(variance.ExportGroupSettings(active.groupId));
                if (!localByPost.ContainsKey(active.id)) localByPost[active.id] = ZeroLocal();
                WriteRows(localByPost[active.id]);
            }
            else if (lastActiveGroup >= 0)
            {
                if (groupBase.TryGetValue(lastActiveGroup, out List<VarianceChannelSaveData> baseData))
                    WriteRows(baseData);
            }

            lastActiveId = activeId;
            lastActiveGroup = activeGroup;
        }

        if (active == null)
        {
            // Keep the latest normal group state ready for the next POST selection.
            groupBase[viewer.currentGroupId] = Clone(variance.ExportGroupSettings(viewer.currentGroupId));
            return;
        }

        if (!groupBase.TryGetValue(active.groupId, out List<VarianceChannelSaveData> groupSettings))
        {
            groupSettings = Clone(variance.ExportGroupSettings(active.groupId));
            groupBase[active.groupId] = groupSettings;
        }

        List<VarianceChannelSaveData> localUI = ReadRows();
        localByPost[active.id] = localUI;

        // The normal variance listeners fire first and temporarily write a POST UI
        // edit into the whole group's variance state. Put the group settings back,
        // then restore the localized values to the visible controls without notify.
        List<VarianceChannelSaveData> currentGroup = variance.ExportGroupSettings(active.groupId);
        if (!Equivalent(currentGroup, groupSettings))
        {
            variance.ImportGroupSettings(active.groupId, Clone(groupSettings));
            WriteRows(localUI);
        }
    }

    void LateUpdate()
    {
        EnsureRefs();
        if (posts == null || viewer == null) return;

        Dictionary<int, List<PostAffectorManager.PostAffector>> groups = GetGroups();
        if (groups == null || groups.Count == 0) return;

        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
        {
            if (!groups.TryGetValue(card.groupId, out List<PostAffectorManager.PostAffector> list)) continue;

            float dLength = 0f, dBend = 0f, dTwist = 0f, dX = 0f, dY = 0f, dZ = 0f;
            foreach (PostAffectorManager.PostAffector a in list)
            {
                if (!localByPost.TryGetValue(a.id, out List<VarianceChannelSaveData> local)) continue;
                float spatial = SpatialWeight(card, a) * Mathf.Clamp01(a.weight);
                if (spatial <= .000001f) continue;

                dLength += RandomDelta(card, a, local, "Length") * spatial;
                dBend += RandomDelta(card, a, local, "Bend") * spatial;
                dTwist += RandomDelta(card, a, local, "Twist") * spatial;
                dX += RandomDelta(card, a, local, "AngleX") * spatial;
                dY += RandomDelta(card, a, local, "AngleY") * spatial;
                dZ += RandomDelta(card, a, local, "AngleZ") * spatial;
            }

            if (Mathf.Abs(dLength) + Mathf.Abs(dBend) + Mathf.Abs(dTwist) + Mathf.Abs(dX) + Mathf.Abs(dY) + Mathf.Abs(dZ) <= .000001f)
                continue;

            float oldSelection = card.selectionWeight;
            card.SetSelectionWeight(0f);
            card.SetParameters(
                Mathf.Max(.0005f, card.length + dLength), card.width, card.segments,
                card.bendAngle + dBend, card.twistAngle + dTwist,
                NormalizeAngle(card.GetOffsetX() + dX), NormalizeAngle(card.GetOffsetY() + dY), NormalizeAngle(card.GetOffsetZ() + dZ),
                card.GetEmbedDepth(), 1f, card.uScale, card.vScale, card.uOffset, card.vOffset);
            card.SetSelectionWeight(oldSelection);
        }
    }

    void EnsureRefs()
    {
        if (viewer == null) viewer = FindFirstObjectByType<ModelViewer>();
        if (variance == null) variance = FindFirstObjectByType<GroomVarianceController>();
        if (posts != null) return;
        posts = FindFirstObjectByType<PostAffectorManager>();
        if (posts == null) return;
        BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic;
        activeIdField = typeof(PostAffectorManager).GetField("activeId", f);
        activeGroupField = typeof(PostAffectorManager).GetField("activeGroup", f);
        groupsField = typeof(PostAffectorManager).GetField("groups", f);
    }

    void RestorePendingLocalVariance()
    {
        HairProjectSaveData pending = HairProjectSaveData.PendingModifierRestore;
        if (pending == null || pending == cachedPending) return;
        cachedPending = pending;
        localByPost.Clear();
        if (pending.groups == null) return;
        foreach (GroupSaveData g in pending.groups)
        {
            if (g.postAffectors == null) continue;
            foreach (PostAffectorSaveData p in g.postAffectors)
                localByPost[p.id] = p.localVariances != null && p.localVariances.Count > 0 ? Clone(p.localVariances) : ZeroLocal();
        }
    }

    void NormalizeActiveAngleDelta(PostAffectorManager.PostAffector active)
    {
        if (active == null || viewer == null) return;
        PostAffectorManager.ControlState d = active.delta;
        d.x = Mathf.DeltaAngle(active.baseline.x, viewer.currentOffsetX);
        d.y = Mathf.DeltaAngle(active.baseline.y, viewer.currentOffsetY);
        d.z = Mathf.DeltaAngle(active.baseline.z, viewer.currentOffsetZ);
        active.delta = d;
    }

    float SpatialWeight(HairCard card, PostAffectorManager.PostAffector a)
    {
        Vector3 p = card.GetSpawnHitPoint();
        if (p == Vector3.zero) p = card.transform.position;
        float d = Vector3.Distance(p, a.center);
        float radius = Mathf.Max(.001f, a.radius);
        float outer = radius + Mathf.Max(0f, a.falloff);
        if (d <= radius) return 1f;
        if (a.falloff <= .000001f || d >= outer) return 0f;
        return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(outer, radius, d));
    }

    float RandomDelta(HairCard card, PostAffectorManager.PostAffector a, List<VarianceChannelSaveData> data, string channel)
    {
        VarianceChannelSaveData s = data.FirstOrDefault(x => x != null && x.channel == channel);
        if (s == null || s.amount <= 0f) return 0f;
        return SignedRandom(card, channel, s.seed, a.groupId, a.id) * s.amount;
    }

    float SignedRandom(HairCard card, string channel, int seed, int groupId, int postId)
    {
        Vector3 p = card.GetSpawnHitPoint();
        unchecked
        {
            uint h = 2166136261u;
            Mix(ref h, Mathf.RoundToInt(p.x * 10000f));
            Mix(ref h, Mathf.RoundToInt(p.y * 10000f));
            Mix(ref h, Mathf.RoundToInt(p.z * 10000f));
            Mix(ref h, groupId);
            Mix(ref h, postId * 3571);
            Mix(ref h, ChannelIndex(channel) * 7919);
            Mix(ref h, seed);
            h ^= h >> 16; h *= 0x7feb352du; h ^= h >> 15; h *= 0x846ca68bu; h ^= h >> 16;
            return (h & 0x00FFFFFFu) / 16777215f * 2f - 1f;
        }
    }

    static void Mix(ref uint h, int v) { unchecked { h ^= (uint)v; h *= 16777619u; } }
    static int ChannelIndex(string c) => Array.IndexOf(Channels, c);
    static float NormalizeAngle(float a) => Mathf.DeltaAngle(0f, a);

    List<VarianceChannelSaveData> ReadRows()
    {
        List<VarianceChannelSaveData> result = new();
        if (viewer == null || viewer.groomingSliderPanelGO == null) return ZeroLocal();
        Transform panel = viewer.groomingSliderPanelGO.transform;
        for (int i = 0; i < Channels.Length; i++)
        {
            Transform row = panel.Find(RowNames[i]);
            Slider slider = row != null ? row.GetComponentInChildren<Slider>(true) : null;
            TMP_InputField seed = row != null ? row.GetComponentInChildren<TMP_InputField>(true) : null;
            int parsed = 0;
            if (seed != null) int.TryParse(seed.text, out parsed);
            result.Add(new VarianceChannelSaveData { channel = Channels[i], amount = slider != null ? slider.value : 0f, seed = parsed });
        }
        return result;
    }

    void WriteRows(List<VarianceChannelSaveData> data)
    {
        if (viewer == null || viewer.groomingSliderPanelGO == null || data == null) return;
        Transform panel = viewer.groomingSliderPanelGO.transform;
        for (int i = 0; i < Channels.Length; i++)
        {
            VarianceChannelSaveData v = data.FirstOrDefault(x => x != null && x.channel == Channels[i]);
            Transform row = panel.Find(RowNames[i]);
            if (row == null || v == null) continue;
            Slider slider = row.GetComponentInChildren<Slider>(true);
            TMP_InputField seed = row.GetComponentInChildren<TMP_InputField>(true);
            TextMeshProUGUI label = row.GetComponentsInChildren<TextMeshProUGUI>(true).FirstOrDefault(t => t.gameObject.name == "Text" || t.text.StartsWith("VAR"));
            if (slider != null) slider.SetValueWithoutNotify(v.amount);
            if (seed != null) seed.SetTextWithoutNotify(v.seed.ToString());
            if (label != null) label.text = "VAR ± " + (Channels[i] == "Length" ? v.amount.ToString("F3") : v.amount.ToString("F1") + "°");
        }
    }

    static List<VarianceChannelSaveData> ZeroLocal()
    {
        return Channels.Select(c => new VarianceChannelSaveData { channel = c, amount = 0f, seed = 0 }).ToList();
    }

    static List<VarianceChannelSaveData> Clone(List<VarianceChannelSaveData> src)
    {
        if (src == null) return ZeroLocal();
        return src.Select(v => new VarianceChannelSaveData { channel = v.channel, amount = v.amount, seed = v.seed }).ToList();
    }

    static bool Equivalent(List<VarianceChannelSaveData> a, List<VarianceChannelSaveData> b)
    {
        if (a == null || b == null || a.Count != b.Count) return false;
        foreach (string c in Channels)
        {
            VarianceChannelSaveData x = a.FirstOrDefault(v => v.channel == c);
            VarianceChannelSaveData y = b.FirstOrDefault(v => v.channel == c);
            if (x == null || y == null || !Mathf.Approximately(x.amount, y.amount) || x.seed != y.seed) return false;
        }
        return true;
    }

    Dictionary<int, List<PostAffectorManager.PostAffector>> GetGroups()
    {
        return groupsField?.GetValue(posts) as Dictionary<int, List<PostAffectorManager.PostAffector>>;
    }

    PostAffectorManager.PostAffector GetActive()
    {
        if (posts == null || activeIdField == null || activeGroupField == null) return null;
        int id = (int)activeIdField.GetValue(posts);
        int group = (int)activeGroupField.GetValue(posts);
        if (id < 0 || group < 0) return null;
        Dictionary<int, List<PostAffectorManager.PostAffector>> groups = GetGroups();
        return groups != null && groups.TryGetValue(group, out List<PostAffectorManager.PostAffector> list)
            ? list.FirstOrDefault(a => a.id == id) : null;
    }

    public void PopulateSave(List<PostAffectorSaveData> data)
    {
        if (data == null) return;
        foreach (PostAffectorSaveData p in data)
            p.localVariances = localByPost.TryGetValue(p.id, out List<VarianceChannelSaveData> local) ? Clone(local) : ZeroLocal();
    }
}
