using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public enum GroomShapeCurveChannel
{
    Bend,
    X,
    Y,
    Z,
    // Curl (spiral/coil) magnitude profiles. Unlike Bend/X/Y/Z these have no per-POST override -
    // see PostShapeCurveBridge.EvaluateRoot, which routes these two straight to the group
    // registry rather than through the POST-editing snapshot mechanism.
    CurlFrequency,
    CurlDiameter,
    // Segment density: NOT a magnitude multiplier like every channel above - this one is a
    // 0..1 -> 0..1 REMAP of where segments actually sit along the card's length (see
    // HairCard.GenerateMesh). Root-only, same reasoning as Curl: mesh topology isn't a
    // per-POST concept.
    SegmentDensity,
    // Width taper: a 0..1 multiplier on the card's authored width, sampled per row. Because
    // GroomShapeCurveRegistry.Evaluate clamps every channel to 0..1, this can only ever NARROW
    // a card, never widen it past the Width slider - which is what a taper wants, and it keeps
    // the slider meaning "maximum width" rather than "width somewhere along the card".
    //
    // Root-only, exactly like Curl and Segment Density: see PostShapeCurveBridge.EvaluateRoot,
    // whose per-POST snapshot CurveSet has fields for Bend/X/Y/Z ONLY.
    Width,
    // Wave amplitude / frequency profiles. Root-only, same as Curl and Segment Density.
    WaveAmplitude,
    WaveFrequency,
    // Wave direction blend: 0 = side to side across the card's flat plane, 1 = up and down
    // across its face, anything between is a diagonal. Root-only like the rest of the tail.
    WaveDirection
}

// Canonical group-root length profiles for shape angles. The slider remains the authored
// magnitude; these curves are a normalized 0..1 multiplier from card root (t=0) to tip (t=1).
// Bend defaults to the legacy t^2 progression. X/Y/Z default to 1 so existing cards retain
// their exact whole-card orientation until a curve is deliberately edited.
public static class GroomShapeCurveRegistry
{
    private sealed class CurveSet
    {
        public AnimationCurve bend = CreateDefault(GroomShapeCurveChannel.Bend);
        public AnimationCurve x = CreateDefault(GroomShapeCurveChannel.X);
        public AnimationCurve y = CreateDefault(GroomShapeCurveChannel.Y);
        public AnimationCurve z = CreateDefault(GroomShapeCurveChannel.Z);
        public AnimationCurve curlFrequency = CreateDefault(GroomShapeCurveChannel.CurlFrequency);
        public AnimationCurve curlDiameter = CreateDefault(GroomShapeCurveChannel.CurlDiameter);
        public AnimationCurve segmentDensity = CreateDefault(GroomShapeCurveChannel.SegmentDensity);
        public AnimationCurve widthProfile = CreateDefault(GroomShapeCurveChannel.Width);
        public AnimationCurve waveAmplitude = CreateDefault(GroomShapeCurveChannel.WaveAmplitude);
        public AnimationCurve waveFrequency = CreateDefault(GroomShapeCurveChannel.WaveFrequency);
        public AnimationCurve waveDirection = CreateDefault(GroomShapeCurveChannel.WaveDirection);
    }

    private static readonly Dictionary<int, CurveSet> byGroup = new Dictionary<int, CurveSet>();

    // Monotonic change stamps for HairCard's mesh-input dirty-check.
    //
    // Curve data is the one mesh input that does NOT live on the card. Worse, the editor
    // mutates the stored AnimationCurve objects IN PLACE - GroomShapeCurveEditor.DragKey calls
    // GetCurve(...) and then MoveKey on the returned object - so neither the dictionary
    // reference nor anything on the card ever changes. Without a stamp, a card whose numbers
    // are unchanged would compare equal and refuse to rebuild while the user drags a curve
    // point, and the shape would simply stop responding.
    //
    // Hashing the keyframes is not an option: AnimationCurve.keys allocates a fresh Keyframe[]
    // on every access, which would cost more per frame than the rebuild being avoided.
    //
    // Kept PER GROUP so that editing one group's profile does not dirty every card in the
    // scene - a curve drag would otherwise rebuild the whole groom every frame for the
    // duration of the drag, which is exactly the cost the dirty-check exists to remove.
    // globalEpoch covers the wholesale operations that have no single group.
    private static readonly Dictionary<int, int> epochByGroup = new Dictionary<int, int>();
    private static int globalEpoch;

    // "Is this curve still the flat x1 default?" - cached, because the answer is needed per
    // row per card per frame and the test itself walks keyframes.
    //
    // This is what lets the mesh builders skip work that provably cannot change anything: a
    // flat x1 Segment Density curve means even row spacing, so the 64-sample integration it
    // normally runs can be replaced by a divide; a flat Width curve means no taper; a flat
    // Wave curve means the multiplier is 1 and the AnimationCurve.Evaluate calls are pure
    // overhead. In a groom where nobody has drawn a profile - the overwhelmingly common case -
    // that removes every curve evaluation from the rebuild.
    //
    // Keyed on the per-group epoch, so drawing on a curve invalidates it for that group only.
    private static readonly Dictionary<int, int> flatCacheEpoch = new Dictionary<int, int>();
    private static readonly Dictionary<int, bool[]> flatCacheValue = new Dictionary<int, bool[]>();

    public static bool IsFlatOne(int groupId, GroomShapeCurveChannel channel)
    {
        int epoch = EpochFor(groupId);

        int cachedEpoch;
        bool[] flags;
        bool haveEpoch = flatCacheEpoch.TryGetValue(groupId, out cachedEpoch);
        bool haveFlags = flatCacheValue.TryGetValue(groupId, out flags);

        if (!haveEpoch || !haveFlags || cachedEpoch != epoch || flags == null)
        {
            flags = new bool[Enum.GetValues(typeof(GroomShapeCurveChannel)).Length];
            for (int i = 0; i < flags.Length; i++) flags[i] = ComputeIsFlatOne(groupId, (GroomShapeCurveChannel)i);
            flatCacheEpoch[groupId] = epoch;
            flatCacheValue[groupId] = flags;
        }

        int index = (int)channel;
        if (index < 0 || index >= flags.Length) return false;
        return flags[index];
    }

    static bool ComputeIsFlatOne(int groupId, GroomShapeCurveChannel channel)
    {
        AnimationCurve curve = GetCurve(groupId, channel);
        if (curve == null) return false;

        // keys allocates, which is exactly why this result is cached rather than recomputed.
        Keyframe[] keys = curve.keys;
        if (keys == null || keys.Length == 0) return false;

        for (int i = 0; i < keys.Length; i++)
        {
            if (!Mathf.Approximately(keys[i].value, 1f)) return false;

            // A key whose value is 1 can still bulge between neighbours if it carries a
            // tangent, so a non-flat tangent disqualifies the curve even at the right height.
            if (!Mathf.Approximately(keys[i].inTangent, 0f)) return false;
            if (!Mathf.Approximately(keys[i].outTangent, 0f)) return false;
        }
        return true;
    }

    public static int EpochFor(int groupId)
    {
        int groupEpoch = 0;
        epochByGroup.TryGetValue(groupId, out groupEpoch);
        unchecked { return globalEpoch * 397 + groupEpoch; }
    }

    // Bumped from BOTH the setters below AND RefreshGroup. RefreshGroup alone is not enough:
    // ClearAll is called from GroomShapeCurveAuthority.CheckModelLifecycle with no refresh
    // afterwards. The setters alone are not enough either: the editor's in-place AddKey /
    // MoveKey / RemoveKey never touch a setter, and RefreshGroup is the only thing all three
    // of them call. Bumping in both places covers every mutation path with no gaps.
    public static void BumpEpoch(int groupId)
    {
        int current = 0;
        epochByGroup.TryGetValue(groupId, out current);
        unchecked { epochByGroup[groupId] = current + 1; }
    }

    public static AnimationCurve GetCurve(int groupId, GroomShapeCurveChannel channel)
    {
        CurveSet set = GetSet(groupId);
        switch (channel)
        {
            case GroomShapeCurveChannel.Bend: return set.bend;
            case GroomShapeCurveChannel.X: return set.x;
            case GroomShapeCurveChannel.Y: return set.y;
            case GroomShapeCurveChannel.Z: return set.z;
            case GroomShapeCurveChannel.CurlFrequency: return set.curlFrequency;
            case GroomShapeCurveChannel.CurlDiameter: return set.curlDiameter;
            case GroomShapeCurveChannel.Width: return set.widthProfile;
            case GroomShapeCurveChannel.WaveAmplitude: return set.waveAmplitude;
            case GroomShapeCurveChannel.WaveFrequency: return set.waveFrequency;
            case GroomShapeCurveChannel.WaveDirection: return set.waveDirection;
            // Was `default: return set.segmentDensity;`. A channel with no case of its own
            // silently read and wrote the SEGMENT DENSITY curve - so editing Width would have
            // re-spaced the rows and editing Segment Density would have tapered the card, with
            // no error anywhere. Named explicitly so the next channel added fails loudly.
            case GroomShapeCurveChannel.SegmentDensity: return set.segmentDensity;
            default: return set.segmentDensity;
        }
    }

    public static float Evaluate(int groupId, GroomShapeCurveChannel channel, float t)
    {
        AnimationCurve curve = GetCurve(groupId, channel);
        return Mathf.Clamp01(curve.Evaluate(Mathf.Clamp01(t)));
    }

    public static void SetCurve(int groupId, GroomShapeCurveChannel channel, AnimationCurve curve)
    {
        CurveSet set = GetSet(groupId);
        AnimationCurve clean = SanitizeCurve(channel, curve);
        switch (channel)
        {
            case GroomShapeCurveChannel.Bend: set.bend = clean; break;
            case GroomShapeCurveChannel.X: set.x = clean; break;
            case GroomShapeCurveChannel.Y: set.y = clean; break;
            case GroomShapeCurveChannel.Z: set.z = clean; break;
            case GroomShapeCurveChannel.CurlFrequency: set.curlFrequency = clean; break;
            case GroomShapeCurveChannel.CurlDiameter: set.curlDiameter = clean; break;
            case GroomShapeCurveChannel.SegmentDensity: set.segmentDensity = clean; break;
            // This switch has no default, so a missing case is a silently DROPPED write.
            case GroomShapeCurveChannel.Width: set.widthProfile = clean; break;
            case GroomShapeCurveChannel.WaveAmplitude: set.waveAmplitude = clean; break;
            case GroomShapeCurveChannel.WaveFrequency: set.waveFrequency = clean; break;
            case GroomShapeCurveChannel.WaveDirection: set.waveDirection = clean; break;
        }
        BumpEpoch(groupId);
    }

    public static void Reset(int groupId, GroomShapeCurveChannel channel)
    {
        SetCurve(groupId, channel, CreateDefault(channel));
    }

    public static void ClearAll()
    {
        byGroup.Clear();
        epochByGroup.Clear();
        // CheckModelLifecycle calls this with no RefreshGroup afterwards, so every group's
        // curves silently revert to defaults. Harmless today because a model reload rebuilds
        // the cards anyway - but the dirty-check would otherwise treat those cards as clean.
        unchecked { globalEpoch++; }
    }

    public static List<GroomCurveKeySaveData> Export(int groupId, GroomShapeCurveChannel channel)
    {
        List<GroomCurveKeySaveData> result = new List<GroomCurveKeySaveData>();
        foreach (Keyframe key in GetCurve(groupId, channel).keys)
        {
            result.Add(new GroomCurveKeySaveData
            {
                time = key.time,
                value = key.value,
                inTangent = key.inTangent,
                outTangent = key.outTangent
            });
        }
        return result;
    }

    public static void Import(int groupId, GroomShapeCurveChannel channel, List<GroomCurveKeySaveData> saved)
    {
        if (saved == null || saved.Count < 2)
        {
            Reset(groupId, channel);
            return;
        }

        List<Keyframe> keys = new List<Keyframe>();
        foreach (GroomCurveKeySaveData item in saved)
        {
            if (item == null) continue;
            keys.Add(new Keyframe(
                Mathf.Clamp01(item.time),
                Mathf.Clamp01(item.value),
                Finite(item.inTangent) ? item.inTangent : 0f,
                Finite(item.outTangent) ? item.outTangent : 0f));
        }

        // SEGMENT DENSITY used to be a position remap whose default was the identity
        // diagonal y=t. Y is now segments-per-unit-length, where that same diagonal
        // means "no rows at the root, densest at the tip". A project saved before the
        // change with an untouched curve would therefore load looking quite different,
        // so the legacy default is recognised and replaced with the new flat default.
        // Anything actually authored is left exactly as it was drawn and reinterpreted.
        if (channel == GroomShapeCurveChannel.SegmentDensity && IsLegacyIdentityRemap(keys))
        {
            Reset(groupId, channel);
            return;
        }

        SetCurve(groupId, channel, new AnimationCurve(keys.ToArray()));
    }

    static bool IsLegacyIdentityRemap(List<Keyframe> keys)
    {
        if (keys == null || keys.Count != 2) return false;
        if (Mathf.Abs(keys[0].time) > .0001f || Mathf.Abs(keys[0].value) > .0001f) return false;
        if (Mathf.Abs(keys[1].time - 1f) > .0001f || Mathf.Abs(keys[1].value - 1f) > .0001f) return false;
        return true;
    }

    public static void RefreshGroup(int groupId)
    {
        // The catch-all for in-place curve edits. GroomShapeCurveEditor's AddKey / DragKey /
        // RemoveKey mutate the stored AnimationCurve directly and then call this, so this is
        // the only point at which those edits become observable to anything else.
        BumpEpoch(groupId);

        foreach (HairCard card in UnityEngine.Object.FindObjectsByType<HairCard>(FindObjectsSortMode.None))
            if (card != null && card.groupId == groupId)
                card.GenerateMesh();
    }

    public static AnimationCurve CreateDefault(GroomShapeCurveChannel channel)
    {
        AnimationCurve curve;
        if (channel == GroomShapeCurveChannel.Bend)
        {
            // Piecewise Hermite keys/tangents reproduce y=t^2 exactly.
            curve = new AnimationCurve(
                new Keyframe(0f, 0f, 0f, 0f),
                new Keyframe(.5f, .25f, 1f, 1f),
                new Keyframe(1f, 1f, 2f, 2f));
        }
        else
        {
            curve = new AnimationCurve(
                new Keyframe(0f, 1f, 0f, 0f),
                new Keyframe(1f, 1f, 0f, 0f));
        }
        curve.preWrapMode = WrapMode.ClampForever;
        curve.postWrapMode = WrapMode.ClampForever;
        return curve;
    }

    private static CurveSet GetSet(int groupId)
    {
        if (!byGroup.TryGetValue(groupId, out CurveSet set) || set == null)
        {
            set = new CurveSet();
            byGroup[groupId] = set;
        }
        return set;
    }

    private static AnimationCurve SanitizeCurve(GroomShapeCurveChannel channel, AnimationCurve source)
    {
        if (source == null || source.length < 2) return CreateDefault(channel);

        List<Keyframe> sorted = source.keys
            .Select(k => new Keyframe(
                Mathf.Clamp01(k.time),
                Mathf.Clamp01(k.value),
                Finite(k.inTangent) ? k.inTangent : 0f,
                Finite(k.outTangent) ? k.outTangent : 0f))
            .OrderBy(k => k.time)
            .ToList();

        List<Keyframe> unique = new List<Keyframe>();
        foreach (Keyframe key in sorted)
        {
            if (unique.Count > 0 && Mathf.Abs(unique[unique.Count - 1].time - key.time) < .0001f)
                unique[unique.Count - 1] = key;
            else
                unique.Add(key);
        }

        if (unique.Count == 0) return CreateDefault(channel);
        if (unique[0].time > .0001f)
            unique.Insert(0, new Keyframe(0f, Mathf.Clamp01(source.Evaluate(0f))));
        else
        {
            Keyframe first = unique[0];
            first.time = 0f;
            unique[0] = first;
        }

        int lastIndex = unique.Count - 1;
        if (unique[lastIndex].time < .9999f)
            unique.Add(new Keyframe(1f, Mathf.Clamp01(source.Evaluate(1f))));
        else
        {
            Keyframe last = unique[lastIndex];
            last.time = 1f;
            unique[lastIndex] = last;
        }

        AnimationCurve result = new AnimationCurve(unique.ToArray());
        result.preWrapMode = WrapMode.ClampForever;
        result.postWrapMode = WrapMode.ClampForever;
        return result;
    }

    private static bool Finite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
}

[DefaultExecutionOrder(5210)]
public class GroomShapeCurveAuthority : MonoBehaviour
{
    private static HairProjectSaveData pendingRestore;
    private static int pendingRestoreFrames;

    private ModelViewer viewer;
    private GameObject boundPanel;
    private FieldInfo loadedModelField;
    private GameObject lastLoadedModel;
    private float nextScan;
    private GameObject popup;
    private GroomShapeCurveEditor popupEditor;
    private int popupGroup = -1;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<GroomShapeCurveAuthority>() != null) return;
        GameObject go = new GameObject("GroomShapeCurveAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<GroomShapeCurveAuthority>();
    }

    public static void Capture(HairProjectSaveData data)
    {
        if (data == null || data.groups == null) return;
        foreach (GroupSaveData group in data.groups)
        {
            if (group == null) continue;
            group.bendCurve = GroomShapeCurveRegistry.Export(group.groupId, GroomShapeCurveChannel.Bend);
            group.xAngleCurve = GroomShapeCurveRegistry.Export(group.groupId, GroomShapeCurveChannel.X);
            group.yAngleCurve = GroomShapeCurveRegistry.Export(group.groupId, GroomShapeCurveChannel.Y);
            group.zAngleCurve = GroomShapeCurveRegistry.Export(group.groupId, GroomShapeCurveChannel.Z);
            group.curlFrequencyCurve = GroomShapeCurveRegistry.Export(group.groupId, GroomShapeCurveChannel.CurlFrequency);
            group.curlDiameterCurve = GroomShapeCurveRegistry.Export(group.groupId, GroomShapeCurveChannel.CurlDiameter);
            group.segmentDensityCurve = GroomShapeCurveRegistry.Export(group.groupId, GroomShapeCurveChannel.SegmentDensity);
        group.widthCurve = GroomShapeCurveRegistry.Export(group.groupId, GroomShapeCurveChannel.Width);
        group.waveAmplitudeCurve = GroomShapeCurveRegistry.Export(group.groupId, GroomShapeCurveChannel.WaveAmplitude);
        group.waveFrequencyCurve = GroomShapeCurveRegistry.Export(group.groupId, GroomShapeCurveChannel.WaveFrequency);
        group.waveDirectionCurve = GroomShapeCurveRegistry.Export(group.groupId, GroomShapeCurveChannel.WaveDirection);
        }
    }

    public static void QueueRestore(HairProjectSaveData data)
    {
        pendingRestore = data;
        pendingRestoreFrames = 0;
    }

    void Update()
    {
        ResolveViewer();
        if (viewer == null) return;

        CheckModelLifecycle();
        TryRestorePending();

        if (popup != null && popupGroup != viewer.currentGroupId)
            ClosePopup();

        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + .10f;

        if (viewer.groomingSliderPanelGO == null) return;
        if (boundPanel != viewer.groomingSliderPanelGO)
        {
            boundPanel = viewer.groomingSliderPanelGO;
            ClosePopup();
        }

        EnsureCurveRow("Bend Angle_Row", "BEND PROFILE", GroomShapeCurveChannel.Bend);
        EnsureCurveRow("Offset X_Row", "X ANGLE PROFILE", GroomShapeCurveChannel.X);
        EnsureCurveRow("Offset Y_Row", "Y ANGLE PROFILE", GroomShapeCurveChannel.Y);
        EnsureCurveRow("Offset Z_Row", "Z ANGLE PROFILE", GroomShapeCurveChannel.Z);
        EnsureCurveRow("Curl Frequency_Row", "CURL FREQUENCY PROFILE", GroomShapeCurveChannel.CurlFrequency);
        EnsureCurveRow("Curl Diameter_Row", "CURL DIAMETER PROFILE", GroomShapeCurveChannel.CurlDiameter);
        EnsureCurveRow("Segments_Row", "SEGMENT DENSITY PROFILE", GroomShapeCurveChannel.SegmentDensity);
        // Anchored to the Width slider's own row: ModelViewer builds slider rows as
        // labelText + "_Row", and the width slider's label is "Width".
        EnsureCurveRow("Width_Row", "WIDTH PROFILE", GroomShapeCurveChannel.Width);
        EnsureCurveRow("Wave Amplitude_Row", "WAVE AMPLITUDE PROFILE", GroomShapeCurveChannel.WaveAmplitude);
        EnsureCurveRow("Wave Frequency_Row", "WAVE FREQUENCY PROFILE", GroomShapeCurveChannel.WaveFrequency);
        EnsureCurveRow("Wave Direction_Row", "WAVE DIRECTION PROFILE", GroomShapeCurveChannel.WaveDirection);
    }

    private void ResolveViewer()
    {
        if (viewer != null) return;
        viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer == null) return;
        loadedModelField = typeof(ModelViewer).GetField("loadedModel", BindingFlags.Instance | BindingFlags.NonPublic);
        lastLoadedModel = loadedModelField?.GetValue(viewer) as GameObject;
    }

    private void CheckModelLifecycle()
    {
        if (loadedModelField == null || viewer == null) return;
        GameObject loaded = loadedModelField.GetValue(viewer) as GameObject;
        if (loaded == lastLoadedModel) return;

        lastLoadedModel = loaded;
        GroomShapeCurveRegistry.ClearAll();
        ClosePopup();
        pendingRestoreFrames = 0;
    }

    private void TryRestorePending()
    {
        if (pendingRestore == null) return;

        int expectedCards = pendingRestore.hairCards != null ? pendingRestore.hairCards.Count : 0;
        int actualCards = FindObjectsByType<HairCard>(FindObjectsSortMode.None).Length;
        if (actualCards < expectedCards) return;
        if (++pendingRestoreFrames < 2) return;

        HairProjectSaveData restore = pendingRestore;
        pendingRestore = null;
        pendingRestoreFrames = 0;
        GroomShapeCurveRegistry.ClearAll();

        if (restore.groups != null)
        {
            foreach (GroupSaveData group in restore.groups)
            {
                if (group == null) continue;
                GroomShapeCurveRegistry.Import(group.groupId, GroomShapeCurveChannel.Bend, group.bendCurve);
                GroomShapeCurveRegistry.Import(group.groupId, GroomShapeCurveChannel.X, group.xAngleCurve);
                GroomShapeCurveRegistry.Import(group.groupId, GroomShapeCurveChannel.Y, group.yAngleCurve);
                GroomShapeCurveRegistry.Import(group.groupId, GroomShapeCurveChannel.Z, group.zAngleCurve);
                GroomShapeCurveRegistry.Import(group.groupId, GroomShapeCurveChannel.CurlFrequency, group.curlFrequencyCurve);
                GroomShapeCurveRegistry.Import(group.groupId, GroomShapeCurveChannel.CurlDiameter, group.curlDiameterCurve);
                GroomShapeCurveRegistry.Import(group.groupId, GroomShapeCurveChannel.SegmentDensity, group.segmentDensityCurve);
            GroomShapeCurveRegistry.Import(group.groupId, GroomShapeCurveChannel.Width, group.widthCurve);
            GroomShapeCurveRegistry.Import(group.groupId, GroomShapeCurveChannel.WaveAmplitude, group.waveAmplitudeCurve);
            GroomShapeCurveRegistry.Import(group.groupId, GroomShapeCurveChannel.WaveFrequency, group.waveFrequencyCurve);
            GroomShapeCurveRegistry.Import(group.groupId, GroomShapeCurveChannel.WaveDirection, group.waveDirectionCurve);
                GroomShapeCurveRegistry.RefreshGroup(group.groupId);
            }
        }
    }

    private void EnsureCurveRow(string targetRowName, string label, GroomShapeCurveChannel channel)
    {
        if (boundPanel == null) return;
        Transform target = boundPanel.transform.Find(targetRowName);
        if (target == null) return;

        string rowName = "ShapeCurve_" + channel + "_Row";
        Transform existing = boundPanel.transform.Find(rowName);
        GameObject row;
        if (existing == null)
            row = BuildCurveRow(rowName, label, channel);
        else
            row = existing.gameObject;

        row.SetActive(target.gameObject.activeSelf);
        int desired = Mathf.Min(target.GetSiblingIndex() + 1, boundPanel.transform.childCount - 1);
        if (row.transform.GetSiblingIndex() != desired)
            row.transform.SetSiblingIndex(desired);
    }

    private GameObject BuildCurveRow(string rowName, string label, GroomShapeCurveChannel channel)
    {
        GameObject row = new GameObject(rowName, typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        row.transform.SetParent(boundPanel.transform, false);
        row.GetComponent<LayoutElement>().preferredHeight = 27f;

        HorizontalLayoutGroup layout = row.GetComponent<HorizontalLayoutGroup>();
        layout.spacing = 6f;
        layout.padding = new RectOffset(135, 0, 0, 0);
        layout.childControlWidth = false;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;

        AddRowLabel(row.transform, label, 150f);
        AddButton(row.transform, "EDIT CURVE", 112f, () => OpenEditor(channel));
        AddButton(row.transform, "RESET", 68f, () =>
        {
            int gid = viewer != null ? viewer.currentGroupId : 0;
            GroomShapeCurveRegistry.Reset(gid, channel);
            GroomShapeCurveRegistry.RefreshGroup(gid);
            if (popupEditor != null && popupGroup == gid && popupEditor.Channel == channel)
                popupEditor.RefreshAll();
        });
        return row;
    }

    private void OpenEditor(GroomShapeCurveChannel channel)
    {
        if (viewer == null || boundPanel == null) return;

        bool sameEditorAlreadyOpen = popup != null && popupEditor != null
            && popupGroup == viewer.currentGroupId && popupEditor.Channel == channel;
        if (sameEditorAlreadyOpen)
        {
            ClosePopup();
            return;
        }

        ClosePopup();

        Canvas canvas = boundPanel.GetComponentInParent<Canvas>();
        if (canvas == null) return;

        popupGroup = viewer.currentGroupId;
        popup = new GameObject("GroomShapeCurveEditor", typeof(RectTransform), typeof(Image));
        popup.transform.SetParent(canvas.transform, false);
        popup.transform.SetAsLastSibling();

        RectTransform root = popup.GetComponent<RectTransform>();
        root.anchorMin = new Vector2(.5f, .5f);
        root.anchorMax = new Vector2(.5f, .5f);
        root.pivot = new Vector2(.5f, .5f);
        root.sizeDelta = new Vector2(670f, 455f);
        root.anchoredPosition = Vector2.zero;
        popup.GetComponent<Image>().color = new Color(.105f, .115f, .13f, .985f);

        AddPopupText(root, "Title", ChannelTitle(channel) + "  •  LENGTH CURVE", 18f,
            new Vector2(.05f, .90f), new Vector2(.95f, .98f), TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
        AddPopupText(root, "Hint", "ROOT 0 → TIP 1    |    curve value = 0–1 multiplier    |    click graph to add • drag points • right-click point to remove", 11f,
            new Vector2(.05f, .835f), new Vector2(.95f, .90f), TextAlignmentOptions.MidlineLeft, FontStyles.Normal);

        GameObject graphGO = new GameObject("Graph", typeof(RectTransform), typeof(Image), typeof(GroomCurveGraphInput));
        graphGO.transform.SetParent(root, false);
        RectTransform graph = graphGO.GetComponent<RectTransform>();
        graph.anchorMin = new Vector2(.07f, .18f);
        graph.anchorMax = new Vector2(.93f, .82f);
        graph.offsetMin = Vector2.zero;
        graph.offsetMax = Vector2.zero;
        graphGO.GetComponent<Image>().color = new Color(.055f, .06f, .07f, 1f);

        Transform gridRoot = CreateStretchChild(graph, "Grid").transform;
        Transform lineRoot = CreateStretchChild(graph, "CurveLine").transform;
        Transform pointRoot = CreateStretchChild(graph, "Points").transform;
        BuildGrid(gridRoot);

        AddPopupText(root, "Zero", "0", 10f, new Vector2(.035f, .16f), new Vector2(.065f, .21f), TextAlignmentOptions.Center, FontStyles.Normal);
        AddPopupText(root, "One", "1", 10f, new Vector2(.035f, .79f), new Vector2(.065f, .84f), TextAlignmentOptions.Center, FontStyles.Normal);
        AddPopupText(root, "RootLabel", "ROOT", 10f, new Vector2(.06f, .115f), new Vector2(.14f, .17f), TextAlignmentOptions.Center, FontStyles.Bold);
        AddPopupText(root, "TipLabel", "TIP", 10f, new Vector2(.86f, .115f), new Vector2(.94f, .17f), TextAlignmentOptions.Center, FontStyles.Bold);

        popupEditor = popup.AddComponent<GroomShapeCurveEditor>();
        popupEditor.Bind(this, popupGroup, channel, graph, lineRoot, pointRoot);
        graphGO.GetComponent<GroomCurveGraphInput>().Bind(popupEditor);

        AddPopupButton(root, "RESET", new Vector2(.41f, .060f), new Vector2(.59f, .080f), popupEditor.ResetDefault);

        Canvas.ForceUpdateCanvases();
        popupEditor.RefreshAll();
    }

    public void ClosePopup()
    {
        if (popup != null) Destroy(popup);
        popup = null;
        popupEditor = null;
        popupGroup = -1;
    }

    private static string ChannelTitle(GroomShapeCurveChannel channel)
    {
        switch (channel)
        {
            case GroomShapeCurveChannel.Bend: return "BEND";
            case GroomShapeCurveChannel.X: return "X ANGLE";
            case GroomShapeCurveChannel.Y: return "Y ANGLE";
            case GroomShapeCurveChannel.Z: return "Z ANGLE";
            case GroomShapeCurveChannel.CurlFrequency: return "CURL FREQUENCY";
            case GroomShapeCurveChannel.CurlDiameter: return "CURL DIAMETER";
            case GroomShapeCurveChannel.Width: return "WIDTH";
            case GroomShapeCurveChannel.WaveAmplitude: return "WAVE AMPLITUDE";
            case GroomShapeCurveChannel.WaveFrequency: return "WAVE FREQUENCY";
            case GroomShapeCurveChannel.WaveDirection: return "WAVE DIRECTION";
            default: return "SEGMENT DENSITY";
        }
    }

    private static void AddRowLabel(Transform parent, string label, float width)
    {
        GameObject go = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        go.GetComponent<LayoutElement>().preferredWidth = width;
        go.GetComponent<LayoutElement>().preferredHeight = 25f;
        TextMeshProUGUI text = go.GetComponent<TextMeshProUGUI>();
        text.text = label;
        text.fontSize = 10f;
        text.color = new Color(.78f, .82f, .87f, 1f);
        text.alignment = TextAlignmentOptions.MidlineLeft;
        text.raycastTarget = false;
    }

    private static void AddButton(Transform parent, string label, float width, UnityEngine.Events.UnityAction action)
    {
        GameObject go = new GameObject(label + "Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        LayoutElement le = go.GetComponent<LayoutElement>();
        le.preferredWidth = width;
        le.minWidth = width;
        le.preferredHeight = 25f;
        le.minHeight = 25f;
        go.GetComponent<Image>().color = new Color(.20f, .42f, .67f, 1f);
        go.GetComponent<Button>().onClick.AddListener(action);
        AddButtonText(go.transform, label, 10f);
    }

    private static GameObject CreateStretchChild(RectTransform parent, string name)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        RectTransform rect = go.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        return go;
    }

    private static void BuildGrid(Transform root)
    {
        for (int i = 0; i <= 4; i++)
        {
            float n = i / 4f;
            CreateGridLine(root, true, n);
            CreateGridLine(root, false, n);
        }
    }

    private static void CreateGridLine(Transform parent, bool vertical, float normalized)
    {
        GameObject go = new GameObject(vertical ? "VGrid" : "HGrid", typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        RectTransform rect = go.GetComponent<RectTransform>();
        if (vertical)
        {
            rect.anchorMin = new Vector2(normalized, 0f);
            rect.anchorMax = new Vector2(normalized, 1f);
            rect.sizeDelta = new Vector2(1f, 0f);
        }
        else
        {
            rect.anchorMin = new Vector2(0f, normalized);
            rect.anchorMax = new Vector2(1f, normalized);
            rect.sizeDelta = new Vector2(0f, 1f);
        }
        go.GetComponent<Image>().color = new Color(.23f, .25f, .29f, .55f);
        go.GetComponent<Image>().raycastTarget = false;
    }

    private static TextMeshProUGUI AddPopupText(RectTransform parent, string name, string content, float size,
        Vector2 anchorMin, Vector2 anchorMax, TextAlignmentOptions align, FontStyles style)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        RectTransform rect = go.GetComponent<RectTransform>();
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        TextMeshProUGUI text = go.GetComponent<TextMeshProUGUI>();
        text.text = content;
        text.fontSize = size;
        text.fontStyle = style;
        text.alignment = align;
        text.color = Color.white;
        text.raycastTarget = false;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        return text;
    }

    private static void AddPopupButton(RectTransform parent, string label, Vector2 anchorMin, Vector2 anchorMax, UnityEngine.Events.UnityAction action)
    {
        GameObject go = new GameObject(label + "Button", typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        RectTransform rect = go.GetComponent<RectTransform>();
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        go.GetComponent<Image>().color = new Color(.20f, .42f, .67f, 1f);
        go.GetComponent<Button>().onClick.AddListener(action);
        AddButtonText(go.transform, label, 11f);
    }

    private static void AddButtonText(Transform parent, string label, float size)
    {
        GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textGO.transform.SetParent(parent, false);
        RectTransform rect = textGO.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        TextMeshProUGUI text = textGO.GetComponent<TextMeshProUGUI>();
        text.text = label;
        text.fontSize = size;
        text.fontStyle = FontStyles.Bold;
        text.alignment = TextAlignmentOptions.Center;
        text.color = Color.white;
        text.raycastTarget = false;
    }
}

public class GroomShapeCurveEditor : MonoBehaviour
{
    private const int SampleCount = 64;
    private readonly List<RectTransform> lineSegments = new List<RectTransform>();
    private readonly List<GroomCurvePointHandle> pointHandles = new List<GroomCurvePointHandle>();

    private GroomShapeCurveAuthority owner;
    private int groupId;
    private GroomShapeCurveChannel channel;
    private RectTransform graph;
    private Transform lineRoot;
    private Transform pointRoot;

    public GroomShapeCurveChannel Channel => channel;

    public void Bind(GroomShapeCurveAuthority authority, int gid, GroomShapeCurveChannel curveChannel,
        RectTransform graphRect, Transform lines, Transform points)
    {
        owner = authority;
        groupId = gid;
        channel = curveChannel;
        graph = graphRect;
        lineRoot = lines;
        pointRoot = points;
    }

    public void RefreshAll()
    {
        if (graph == null) return;
        Canvas.ForceUpdateCanvases();
        EnsureLines();
        EnsurePoints();
        RefreshLines();
        RefreshPoints();
    }

    public void ResetDefault()
    {
        GroomShapeCurveRegistry.Reset(groupId, channel);
        GroomShapeCurveRegistry.RefreshGroup(groupId);
        RefreshAll();
    }

    public void AddKey(Vector2 normalized)
    {
        AnimationCurve curve = GroomShapeCurveRegistry.GetCurve(groupId, channel);
        float time = Mathf.Clamp(normalized.x, .005f, .995f);
        float value = Mathf.Clamp01(normalized.y);

        foreach (Keyframe existing in curve.keys)
            if (Mathf.Abs(existing.time - time) < .012f)
                return;

        curve.AddKey(new Keyframe(time, value));
        Smooth(curve);
        GroomShapeCurveRegistry.RefreshGroup(groupId);
        RefreshAll();
    }

    public void DragKey(GroomCurvePointHandle handle, int index, Vector2 normalized)
    {
        AnimationCurve curve = GroomShapeCurveRegistry.GetCurve(groupId, channel);
        Keyframe[] keys = curve.keys;
        if (index < 0 || index >= keys.Length) return;

        float time;
        if (index == 0) time = 0f;
        else if (index == keys.Length - 1) time = 1f;
        else time = Mathf.Clamp(normalized.x, keys[index - 1].time + .005f, keys[index + 1].time - .005f);
        float value = Mathf.Clamp01(normalized.y);

        Keyframe moved = keys[index];
        moved.time = time;
        moved.value = value;
        int newIndex = curve.MoveKey(index, moved);
        Smooth(curve);
        handle.SetKeyIndex(newIndex);
        GroomShapeCurveRegistry.RefreshGroup(groupId);
        RefreshLines();
        RefreshPoints();
    }

    public void RemoveKey(int index)
    {
        AnimationCurve curve = GroomShapeCurveRegistry.GetCurve(groupId, channel);
        if (index <= 0 || index >= curve.length - 1) return;
        curve.RemoveKey(index);
        Smooth(curve);
        GroomShapeCurveRegistry.RefreshGroup(groupId);
        RefreshAll();
    }

    public bool ScreenToNormalized(PointerEventData eventData, out Vector2 normalized)
    {
        normalized = Vector2.zero;
        if (graph == null || eventData == null) return false;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(graph, eventData.position, eventData.pressEventCamera, out Vector2 local))
            return false;
        Rect rect = graph.rect;
        if (rect.width <= .001f || rect.height <= .001f) return false;
        normalized.x = Mathf.Clamp01((local.x - rect.xMin) / rect.width);
        normalized.y = Mathf.Clamp01((local.y - rect.yMin) / rect.height);
        return true;
    }

    private void EnsureLines()
    {
        if (lineRoot == null) return;
        int wanted = SampleCount - 1;
        while (lineSegments.Count < wanted)
        {
            GameObject go = new GameObject("Segment", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(lineRoot, false);
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(.5f, .5f);
            rect.anchorMax = new Vector2(.5f, .5f);
            rect.pivot = new Vector2(.5f, .5f);
            Image image = go.GetComponent<Image>();
            image.color = new Color(.22f, .72f, 1f, 1f);
            image.raycastTarget = false;
            lineSegments.Add(rect);
        }
    }

    private void EnsurePoints()
    {
        if (pointRoot == null) return;
        int wanted = GroomShapeCurveRegistry.GetCurve(groupId, channel).length;
        while (pointHandles.Count < wanted)
        {
            GameObject go = new GameObject("CurvePoint", typeof(RectTransform), typeof(Image), typeof(GroomCurvePointHandle));
            go.transform.SetParent(pointRoot, false);
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(.5f, .5f);
            rect.anchorMax = new Vector2(.5f, .5f);
            rect.pivot = new Vector2(.5f, .5f);
            rect.sizeDelta = new Vector2(14f, 14f);
            go.GetComponent<Image>().color = new Color(1f, .78f, .24f, 1f);
            GroomCurvePointHandle handle = go.GetComponent<GroomCurvePointHandle>();
            handle.Bind(this, pointHandles.Count);
            pointHandles.Add(handle);
        }
        while (pointHandles.Count > wanted)
        {
            int last = pointHandles.Count - 1;
            if (pointHandles[last] != null) Destroy(pointHandles[last].gameObject);
            pointHandles.RemoveAt(last);
        }
        for (int i = 0; i < pointHandles.Count; i++)
            if (pointHandles[i] != null) pointHandles[i].Bind(this, i);
    }

    private void RefreshLines()
    {
        if (graph == null) return;
        EnsureLines();
        Rect rect = graph.rect;
        AnimationCurve curve = GroomShapeCurveRegistry.GetCurve(groupId, channel);
        for (int i = 0; i < lineSegments.Count; i++)
        {
            float t0 = i / (float)(SampleCount - 1);
            float t1 = (i + 1) / (float)(SampleCount - 1);
            Vector2 a = GraphPoint(rect, t0, Mathf.Clamp01(curve.Evaluate(t0)));
            Vector2 b = GraphPoint(rect, t1, Mathf.Clamp01(curve.Evaluate(t1)));
            Vector2 delta = b - a;
            RectTransform line = lineSegments[i];
            if (line == null) continue;
            line.anchoredPosition = (a + b) * .5f;
            line.sizeDelta = new Vector2(delta.magnitude + 1f, 2.5f);
            line.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
        }
    }

    private void RefreshPoints()
    {
        if (graph == null) return;
        EnsurePoints();
        Rect rect = graph.rect;
        Keyframe[] keys = GroomShapeCurveRegistry.GetCurve(groupId, channel).keys;
        for (int i = 0; i < pointHandles.Count && i < keys.Length; i++)
        {
            GroomCurvePointHandle handle = pointHandles[i];
            if (handle == null) continue;
            handle.SetKeyIndex(i);
            handle.GetComponent<RectTransform>().anchoredPosition = GraphPoint(rect, keys[i].time, keys[i].value);
        }
    }

    private static Vector2 GraphPoint(Rect rect, float x, float y)
    {
        return new Vector2(rect.xMin + Mathf.Clamp01(x) * rect.width, rect.yMin + Mathf.Clamp01(y) * rect.height);
    }

    private static void Smooth(AnimationCurve curve)
    {
        if (curve == null) return;
        for (int i = 0; i < curve.length; i++)
            curve.SmoothTangents(i, 0f);
        curve.preWrapMode = WrapMode.ClampForever;
        curve.postWrapMode = WrapMode.ClampForever;
    }
}

public class GroomCurveGraphInput : MonoBehaviour, IPointerClickHandler
{
    private GroomShapeCurveEditor editor;

    public void Bind(GroomShapeCurveEditor curveEditor)
    {
        editor = curveEditor;
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        if (editor == null || eventData == null || eventData.button != PointerEventData.InputButton.Left) return;
        if (editor.ScreenToNormalized(eventData, out Vector2 normalized))
            editor.AddKey(normalized);
    }
}

public class GroomCurvePointHandle : MonoBehaviour, IDragHandler, IPointerDownHandler, IPointerClickHandler
{
    private GroomShapeCurveEditor editor;
    private int keyIndex;

    public void Bind(GroomShapeCurveEditor curveEditor, int index)
    {
        editor = curveEditor;
        keyIndex = index;
    }

    public void SetKeyIndex(int index)
    {
        keyIndex = index;
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (editor == null) return;
        if (editor.ScreenToNormalized(eventData, out Vector2 normalized))
            editor.DragKey(this, keyIndex, normalized);
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        if (editor == null || eventData == null) return;
        if (eventData.button == PointerEventData.InputButton.Right)
        {
            editor.RemoveKey(keyIndex);
            eventData.Use();
        }
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        // Consume point clicks so they never bubble to the graph and accidentally create
        // a second key underneath the handle the user was trying to select/drag.
        if (eventData != null) eventData.Use();
    }
}
