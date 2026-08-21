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
    // APPEND ONLY, for the same reason GroomVarianceController.Channel is append only:
    // SignedRandom mixes ChannelIndex(channel) into the per-card hash, so a channel's position
    // in this array is part of the persisted random stream. The six curl/wave/arch channels are
    // added at the END, leaving the original seven on indices 0..6 - every project saved before
    // this change re-loads with an identical scatter.
    //
    // These must stay in step with GroomVarianceController.Channel: a name here that does not
    // parse there is silently ignored on restore. Names, not ordinals, are what save data uses.
    private static readonly string[] Channels = { "Length", "Width", "Bend", "Twist", "AngleX", "AngleY", "AngleZ", "CurlFrequency", "CurlDiameter", "WaveAmplitude", "WaveFrequency", "WaveDirection", "Arch" };

    // Parallel to Channels. The row names are the LABELS GroomVarianceController.BuildVarianceRow
    // generates ("Curl Frequency_VarianceRow"), not the enum spellings - hence the spaces.
    private static readonly string[] RowNames = { "Length_VarianceRow", "Width_VarianceRow", "Bend_VarianceRow", "Twist_VarianceRow", "Angle X_VarianceRow", "Angle Y_VarianceRow", "Angle Z_VarianceRow", "Curl Frequency_VarianceRow", "Curl Diameter_VarianceRow", "Wave Amplitude_VarianceRow", "Wave Frequency_VarianceRow", "Wave Direction_VarianceRow", "Arch_VarianceRow" };

    // The channels whose VAR amount is an ANGLE, for the row label suffix. Matches
    // GroomVarianceController.FormatVariance exactly.
    private static readonly string[] AngleChannels = { "Bend", "Twist", "AngleX", "AngleY", "AngleZ" };

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

        // Normal variance listeners fire before this bridge. Restore the real group variance,
        // then put the localized POST variance values back into the visible controls.
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

        Dictionary<int, ResolvedLocal> resolved = ResolveLocalSettings();

        // PostAffectorManager has already evaluated canonical -> POST for this frame.
        // Local variance is a final evaluated-only layer. Never call SetParameters here:
        // that is an authored/root write and would feed the variance back into canonical,
        // causing the same delta to accumulate again on every frame.
        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
        {
            // Frozen by SOLO - and this one is not merely an optimisation, it is required for
            // correctness. Unlike its siblings, this bridge ACCUMULATES: it reads the card's
            // CURRENT values and adds a delta on top (card.length + dLength, and so on). That
            // is only idempotent because PostAffectorManager, at execution order 3300, resets
            // those fields from canonical + POST every frame before this runs at 3500.
            //
            // Freeze the resetter and not the accumulator and a hidden group's length, width,
            // bend and twist grow without bound for as long as SOLO keeps it hidden - and,
            // because the fields really do change every frame, those invisible cards would be
            // the only ones in the scene still paying a full mesh rebuild per frame. Exactly
            // backwards. So this gate goes in alongside the others.
            if (GroupSoloVisibilityAuthority.IsCardFrozen(card)) continue;

            if (!groups.TryGetValue(card.groupId, out List<PostAffectorManager.PostAffector> list)) continue;

            float dLength = 0f, dWidth = 0f, dBend = 0f, dTwist = 0f, dX = 0f, dY = 0f, dZ = 0f;
            float dCurlFrequency = 0f, dCurlDiameter = 0f;
            float dWaveAmplitude = 0f, dWaveFrequency = 0f, dWaveDirection = 0f, dArch = 0f;

            foreach (PostAffectorManager.PostAffector a in list)
            {
                if (!resolved.TryGetValue(a.id, out ResolvedLocal local)) continue;
                float spatial = SpatialWeight(card, a) * Mathf.Clamp01(a.weight);
                if (spatial <= .000001f) continue;

                dLength += RandomDelta(card, a, local, ChannelLength) * spatial;
                dWidth += RandomDelta(card, a, local, ChannelWidth) * spatial;
                dBend += RandomDelta(card, a, local, ChannelBend) * spatial;
                dTwist += RandomDelta(card, a, local, ChannelTwist) * spatial;
                dX += RandomDelta(card, a, local, ChannelAngleX) * spatial;
                dY += RandomDelta(card, a, local, ChannelAngleY) * spatial;
                dZ += RandomDelta(card, a, local, ChannelAngleZ) * spatial;
                dCurlFrequency += RandomDelta(card, a, local, ChannelCurlFrequency) * spatial;
                dCurlDiameter += RandomDelta(card, a, local, ChannelCurlDiameter) * spatial;
                dWaveAmplitude += RandomDelta(card, a, local, ChannelWaveAmplitude) * spatial;
                dWaveFrequency += RandomDelta(card, a, local, ChannelWaveFrequency) * spatial;
                dWaveDirection += RandomDelta(card, a, local, ChannelWaveDirection) * spatial;
                dArch += RandomDelta(card, a, local, ChannelArch) * spatial;
            }

            float moved = Mathf.Abs(dLength) + Mathf.Abs(dWidth) + Mathf.Abs(dBend) + Mathf.Abs(dTwist) +
                          Mathf.Abs(dX) + Mathf.Abs(dY) + Mathf.Abs(dZ) +
                          Mathf.Abs(dCurlFrequency) + Mathf.Abs(dCurlDiameter) +
                          Mathf.Abs(dWaveAmplitude) + Mathf.Abs(dWaveFrequency) +
                          Mathf.Abs(dWaveDirection) + Mathf.Abs(dArch);
            if (moved <= .000001f) continue;

            HairCard.GroomState evaluated = new HairCard.GroomState
            {
                length = Mathf.Max(.0001f, card.length + dLength),
                width = Mathf.Max(.0005f, card.width + dWidth),
                segments = card.segments,
                bend = card.bendAngle + dBend,
                twist = card.twistAngle + dTwist,
                depth = card.GetEmbedDepth(),
                x = NormalizeAngle(card.GetOffsetX() + dX),
                y = NormalizeAngle(card.GetOffsetY() + dY),
                z = NormalizeAngle(card.GetOffsetZ() + dZ),
                uScale = card.uScale,
                vScale = card.vScale,
                uOffset = card.uOffset,
                vOffset = card.vOffset,

                // These six used to be pass-through only - the card's own value, copied
                // straight back. (They were once missing from this initialiser entirely, which
                // defaulted them to 0f and silently flattened the curl of any card a variance
                // POST touched, since ApplyEvaluatedState writes EVERY field.)
                //
                // They now carry a POST-local delta like the other seven. Clamps mirror
                // HairCard.SanitizeState exactly: diameter and amplitude are magnitudes,
                // direction is a 0..1 blend, arch refuses to invert - that is what the N- form
                // flip is for. Both frequencies stay signed, because a negative frequency simply
                // runs the curl or wave the other way, which is a usable result.
                curlFrequency = card.curlFrequency + dCurlFrequency,
                curlDiameter = Mathf.Max(0f, card.curlDiameter + dCurlDiameter),
                waveAmplitude = Mathf.Max(0f, card.waveAmplitude + dWaveAmplitude),
                waveFrequency = card.waveFrequency + dWaveFrequency,
                waveDirection = Mathf.Clamp01(card.waveDirection + dWaveDirection),
                arch = Mathf.Max(0f, card.arch + dArch)
            };
            card.ApplyEvaluatedState(evaluated);
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
                localByPost[p.id] = p.localVariances != null && p.localVariances.Count > 0 ? NormalizeChannels(Clone(p.localVariances)) : ZeroLocal();
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

    float RandomDelta(HairCard card, PostAffectorManager.PostAffector a, ResolvedLocal local, int channelIndex)
    {
        float amount = local.amounts[channelIndex];
        if (amount <= 0f) return 0f;
        return SignedRandom(card, channelIndex, local.seeds[channelIndex], a.groupId, a.id) * amount;
    }

    // Takes the channel INDEX rather than its name, and the index is the same number the old
    // name-based form fed into the hash via ChannelIndex - so every existing scatter is
    // reproduced bit for bit. What goes away is the lookup: this used to be a LINQ
    // FirstOrDefault with a capturing lambda, evaluated once per channel, per POST, per card,
    // per frame, for an answer that does not vary across the card loop at all.
    float SignedRandom(HairCard card, int channelIndex, int seed, int groupId, int postId)
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
            Mix(ref h, channelIndex * 7919);
            Mix(ref h, seed);
            h ^= h >> 16; h *= 0x7feb352du; h ^= h >> 15; h *= 0x846ca68bu; h ^= h >> 16;
            return (h & 0x00FFFFFFu) / 16777215f * 2f - 1f;
        }
    }

    static void Mix(ref uint h, int v) { unchecked { h ^= (uint)v; h *= 16777619u; } }
    static float NormalizeAngle(float a) => Mathf.DeltaAngle(0f, a);

    // Channel indices, named. These are the SAME ordinals as the position in Channels, and they
    // are part of the persisted random stream - see the APPEND ONLY note on that array.
    private const int ChannelLength = 0;
    private const int ChannelWidth = 1;
    private const int ChannelBend = 2;
    private const int ChannelTwist = 3;
    private const int ChannelAngleX = 4;
    private const int ChannelAngleY = 5;
    private const int ChannelAngleZ = 6;
    private const int ChannelCurlFrequency = 7;
    private const int ChannelCurlDiameter = 8;
    private const int ChannelWaveAmplitude = 9;
    private const int ChannelWaveFrequency = 10;
    private const int ChannelWaveDirection = 11;
    private const int ChannelArch = 12;

    // One POST's thirteen amounts and seeds, flattened for indexed access.
    private class ResolvedLocal
    {
        public float[] amounts;
        public int[] seeds;
    }

    // Resolved once per frame, before the card loop, because none of it varies per card.
    Dictionary<int, ResolvedLocal> ResolveLocalSettings()
    {
        Dictionary<int, ResolvedLocal> result = new Dictionary<int, ResolvedLocal>();
        foreach (KeyValuePair<int, List<VarianceChannelSaveData>> entry in localByPost)
        {
            ResolvedLocal resolvedLocal = new ResolvedLocal();
            resolvedLocal.amounts = new float[Channels.Length];
            resolvedLocal.seeds = new int[Channels.Length];

            if (entry.Value != null)
            {
                foreach (VarianceChannelSaveData v in entry.Value)
                {
                    if (v == null) continue;
                    int index = Array.IndexOf(Channels, v.channel);
                    if (index < 0) continue;
                    resolvedLocal.amounts[index] = v.amount;
                    resolvedLocal.seeds[index] = v.seed;
                }
            }

            result[entry.Key] = resolvedLocal;
        }
        return result;
    }

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

        // Normalized to the full channel set FIRST, so a short list writes an explicit zero
        // into the rows it does not mention instead of skipping them.
        //
        // That skip was harmless while only seven channels were owned, and is not any more.
        // Every project saved before this change carries a seven-entry localVariances list, and
        // CanonicalProjectStateBridge restores it verbatim rather than through NormalizeChannels.
        // Selecting a POST from such a project left the six curl/wave/arch rows displaying the
        // GROUP amounts that ImportGroupSettings had just written into them - and the very next
        // ReadRows captured those group amounts as POST-local variance, quietly doubling the
        // group's curl scatter with a second independent draw and saving it back as authored.
        List<VarianceChannelSaveData> normalized = NormalizeChannels(data);

        Transform panel = viewer.groomingSliderPanelGO.transform;
        for (int i = 0; i < Channels.Length; i++)
        {
            VarianceChannelSaveData v = normalized.FirstOrDefault(x => x != null && x.channel == Channels[i]);
            Transform row = panel.Find(RowNames[i]);
            if (row == null || v == null) continue;
            Slider slider = row.GetComponentInChildren<Slider>(true);
            TMP_InputField seed = row.GetComponentInChildren<TMP_InputField>(true);
            TextMeshProUGUI label = row.GetComponentsInChildren<TextMeshProUGUI>(true).FirstOrDefault(t => t.gameObject.name == "Text" || t.text.StartsWith("VAR"));
            if (slider != null) slider.SetValueWithoutNotify(v.amount);
            if (seed != null) seed.SetTextWithoutNotify(v.seed.ToString());
            if (label != null)
            {
                // Was "everything except Length and Width is an angle", which held only while
                // this bridge covered seven channels. Curl Frequency, Curl Diameter, Wave
                // Amplitude, Wave Frequency and Arch are not angles, and would have picked up
                // a degree sign and lost two decimal places under the old test.
                bool isAngle = Array.IndexOf(AngleChannels, Channels[i]) >= 0;
                if (isAngle)
                {
                    label.text = "VAR ± " + v.amount.ToString("F1") + "°";
                }
                else
                {
                    label.text = "VAR ± " + v.amount.ToString("F3");
                }
            }
        }
    }

    static List<VarianceChannelSaveData> ZeroLocal()
    {
        return Channels.Select(c => new VarianceChannelSaveData { channel = c, amount = 0f, seed = 0 }).ToList();
    }

    static List<VarianceChannelSaveData> NormalizeChannels(List<VarianceChannelSaveData> src)
    {
        List<VarianceChannelSaveData> result = ZeroLocal();
        if (src == null) return result;
        foreach (VarianceChannelSaveData item in src)
        {
            if (item == null) continue;
            VarianceChannelSaveData target = result.FirstOrDefault(x => x.channel == item.channel);
            if (target == null) continue;
            target.amount = item.amount;
            target.seed = item.seed;
        }
        return result;
    }

    static List<VarianceChannelSaveData> Clone(List<VarianceChannelSaveData> src)
    {
        if (src == null) return ZeroLocal();
        return src.Select(v => new VarianceChannelSaveData { channel = v.channel, amount = v.amount, seed = v.seed }).ToList();
    }

    static bool Equivalent(List<VarianceChannelSaveData> a, List<VarianceChannelSaveData> b)
    {
        if (a == null || b == null) return false;
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
            p.localVariances = localByPost.TryGetValue(p.id, out List<VarianceChannelSaveData> local) ? NormalizeChannels(Clone(local)) : ZeroLocal();
    }
}
