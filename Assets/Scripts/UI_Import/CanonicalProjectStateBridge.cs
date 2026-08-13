using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

// Current-format save/load contract:
// HairCardSaveData stores the card state immediately upstream of persistent POST
// affectors. Group variance is already represented in that upstream card state;
// POST deltas and POST-local variance are stored separately and evaluated once after load.
[DefaultExecutionOrder(3900)]
public class CanonicalProjectStateBridge : MonoBehaviour
{
    public const int CurrentFormatVersion = 3;
    public static HairProjectSaveData PendingCanonicalRestore;

    private HairProjectSaveData pending;
    private int settleFrames;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<CanonicalProjectStateBridge>() != null) return;
        GameObject go = new GameObject("CanonicalProjectStateBridge");
        DontDestroyOnLoad(go);
        go.AddComponent<CanonicalProjectStateBridge>();
    }

    public static void CanonicalizeForSave(HairProjectSaveData data)
    {
        if (data == null) return;
        data.formatVersion = CurrentFormatVersion;
        if (data.hairCards == null || data.hairCards.Count == 0) return;

        PostAffectorManager posts = FindFirstObjectByType<PostAffectorManager>();
        if (posts == null) return;

        FieldInfo statesField = typeof(PostAffectorManager).GetField("cardStates", BindingFlags.Instance | BindingFlags.NonPublic);
        IDictionary states = statesField?.GetValue(posts) as IDictionary;
        if (states == null) return;

        HairCard[] cards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        HashSet<HairCard> used = new HashSet<HairCard>();
        foreach (HairCardSaveData saved in data.hairCards)
        {
            if (saved == null) continue;
            Vector3 hit = new Vector3(saved.hitX, saved.hitY, saved.hitZ);
            HairCard card = cards.Where(c => c != null && c.groupId == saved.groupId && !used.Contains(c))
                .OrderBy(c => (c.GetSpawnHitPoint() - hit).sqrMagnitude).FirstOrDefault();
            if (card == null) continue;
            used.Add(card);

            object state = states[card];
            if (state == null) continue;
            FieldInfo baseField = state.GetType().GetField("baseState", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (baseField?.GetValue(state) is PostAffectorManager.ControlState b)
                WriteControl(saved, b);
        }
    }

    void Update()
    {
        if (PendingCanonicalRestore != null && PendingCanonicalRestore.formatVersion >= CurrentFormatVersion)
        {
            pending = PendingCanonicalRestore;
            PendingCanonicalRestore = null;
            settleFrames = 0;
        }
        if (pending == null) return;

        int expected = pending.hairCards != null ? pending.hairCards.Count : 0;
        if (FindObjectsByType<HairCard>(FindObjectsSortMode.None).Length < expected) return;
        if (HairProjectSaveData.PendingModifierRestore != null) return;

        GroomVarianceController variance = FindFirstObjectByType<GroomVarianceController>();
        if (variance == null) return;
        FieldInfo installedField = typeof(GroomVarianceController).GetField("installed", BindingFlags.Instance | BindingFlags.NonPublic);
        if (installedField != null && installedField.GetValue(variance) is bool installed && !installed) return;

        // Give all normal restore/UI callbacks one full frame to finish before the
        // canonical state becomes authoritative.
        if (++settleFrames < 2) return;

        RestoreCanonicalState(pending);
        pending = null;
    }

    void RestoreCanonicalState(HairProjectSaveData data)
    {
        PostAffectorManager posts = FindFirstObjectByType<PostAffectorManager>();
        if (posts == null || data.hairCards == null) return;

        FieldInfo statesField = typeof(PostAffectorManager).GetField("cardStates", BindingFlags.Instance | BindingFlags.NonPublic);
        IDictionary states = statesField?.GetValue(posts) as IDictionary;
        if (states == null) return;

        HairCard[] cards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        HashSet<HairCard> used = new HashSet<HairCard>();

        foreach (HairCardSaveData saved in data.hairCards)
        {
            if (saved == null) continue;
            Vector3 hit = new Vector3(saved.hitX, saved.hitY, saved.hitZ);
            HairCard card = cards.Where(c => c != null && c.groupId == saved.groupId && !used.Contains(c))
                .OrderBy(c => (c.GetSpawnHitPoint() - hit).sqrMagnitude).FirstOrDefault();
            if (card == null) continue;
            used.Add(card);

            PostAffectorManager.ControlState canonical = ReadControl(saved);
            float oldSelection = card.selectionWeight;
            card.SetSelectionWeight(0f);
            card.SetParameters(
                Mathf.Max(.0005f, canonical.length), Mathf.Max(.0005f, canonical.width), Mathf.Clamp(Mathf.RoundToInt(canonical.segments), 4, 36),
                canonical.bend, canonical.twist, canonical.x, canonical.y, canonical.z,
                Mathf.Max(0f, canonical.depth), 1f,
                canonical.uScale, canonical.vScale, canonical.uOffset, canonical.vOffset);
            card.SetSelectionWeight(oldSelection);

            object state = states[card];
            if (state == null)
            {
                Type stateType = typeof(PostAffectorManager).GetNestedType("CardState", BindingFlags.NonPublic);
                if (stateType == null) continue;
                state = Activator.CreateInstance(stateType);
                states[card] = state;
            }
            Type t = state.GetType();
            t.GetField("baseState", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(state, canonical);
            t.GetField("lastFinal", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(state, canonical);
            t.GetField("hasFinal", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(state, false);
        }

        RestorePostLocalVariance(data);
    }

    void RestorePostLocalVariance(HairProjectSaveData data)
    {
        PostVarianceAffectorBridge bridge = FindFirstObjectByType<PostVarianceAffectorBridge>();
        if (bridge == null || data.groups == null) return;
        FieldInfo field = typeof(PostVarianceAffectorBridge).GetField("localByPost", BindingFlags.Instance | BindingFlags.NonPublic);
        IDictionary dict = field?.GetValue(bridge) as IDictionary;
        if (dict == null) return;
        dict.Clear();
        foreach (GroupSaveData g in data.groups)
            if (g?.postAffectors != null)
                foreach (PostAffectorSaveData p in g.postAffectors)
                    dict[p.id] = CloneVariance(p.localVariances);
    }

    static List<VarianceChannelSaveData> CloneVariance(List<VarianceChannelSaveData> src)
    {
        if (src == null) return new List<VarianceChannelSaveData>();
        return src.Where(v => v != null).Select(v => new VarianceChannelSaveData { channel = v.channel, amount = v.amount, seed = v.seed }).ToList();
    }

    static PostAffectorManager.ControlState ReadControl(HairCardSaveData s) => new PostAffectorManager.ControlState
    {
        length=s.length, width=s.width, segments=s.segments, bend=s.bendAngle, twist=s.twistAngle, depth=s.embedDepth,
        x=s.offsetX, y=s.offsetY, z=s.offsetZ, uScale=s.uScale, vScale=s.vScale, uOffset=s.uOffset, vOffset=s.vOffset
    };

    static void WriteControl(HairCardSaveData s, PostAffectorManager.ControlState b)
    {
        s.length=b.length; s.width=b.width; s.segments=Mathf.RoundToInt(b.segments); s.bendAngle=b.bend; s.twistAngle=b.twist; s.embedDepth=b.depth;
        s.offsetX=b.x; s.offsetY=b.y; s.offsetZ=b.z; s.uScale=b.uScale; s.vScale=b.vScale; s.uOffset=b.uOffset; s.vOffset=b.vOffset;
    }
}