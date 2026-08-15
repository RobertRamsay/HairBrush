using System.Collections.Generic;
using UnityEngine;

// Explicit modifier-layer snapshots:
// 1) SOURCE is captured at the very start of Update, before ModelViewer/POST authoring can
//    temporarily write evaluated controls back onto HairCard.
// 2) PRE_CLUMP is captured in LateUpdate after POST evaluation (order 3300) and before the
//    CLUMPER final mesh passes (5200+).
// Removal code can therefore restore the correct layer instead of guessing from the current
// live mesh or a canonical state that may have been touched later in the frame.
public static class ModifierEvaluationSnapshots
{
    static readonly Dictionary<HairCard, HairCard.GroomState> source = new Dictionary<HairCard, HairCard.GroomState>();
    static readonly Dictionary<HairCard, HairCard.GroomState> preClump = new Dictionary<HairCard, HairCard.GroomState>();

    public static void CaptureSource()
    {
        HairCard[] cards = Object.FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        foreach (HairCard card in cards)
        {
            if (card == null) continue;
            source[card] = card.GetCanonicalState();
        }
        Prune(source);
    }

    public static void CapturePreClump()
    {
        HairCard[] cards = Object.FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        foreach (HairCard card in cards)
        {
            if (card == null) continue;
            preClump[card] = ReadRendered(card);
        }
        Prune(preClump);
    }

    public static void RestoreSourceGroup(int groupId)
    {
        HairCard[] cards = Object.FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        foreach (HairCard card in cards)
        {
            if (card == null || card.groupId != groupId) continue;
            HairCard.GroomState state = source.TryGetValue(card, out HairCard.GroomState saved)
                ? saved
                : card.GetCanonicalState();
            card.SetCanonicalState(state, false);
            card.ApplyEvaluatedState(state);
            card.SetSelectionWeight(0f);
        }
    }

    public static void RestorePreClumpGroup(int groupId)
    {
        HairCard[] cards = Object.FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        foreach (HairCard card in cards)
        {
            if (card == null || card.groupId != groupId) continue;
            HairCard.GroomState state = preClump.TryGetValue(card, out HairCard.GroomState saved)
                ? saved
                : card.GetCanonicalState();
            // PRE_CLUMP is evaluated/display state (normally SOURCE + POST), so do not write
            // it into canonical/source. Only restore the rendered layer.
            card.ApplyEvaluatedState(state);
            card.SetSelectionWeight(0f);
        }
    }

    static HairCard.GroomState ReadRendered(HairCard card)
    {
        return new HairCard.GroomState
        {
            length = card.length,
            width = card.width,
            segments = Mathf.Clamp(card.segments, 1, 36),
            bend = card.bendAngle,
            twist = card.twistAngle,
            depth = card.GetEmbedDepth(),
            x = card.GetOffsetX(),
            y = card.GetOffsetY(),
            z = card.GetOffsetZ(),
            uScale = card.uScale,
            vScale = card.vScale,
            uOffset = card.uOffset,
            vOffset = card.vOffset
        };
    }

    static void Prune(Dictionary<HairCard, HairCard.GroomState> states)
    {
        List<HairCard> dead = null;
        foreach (HairCard card in states.Keys)
        {
            if (card != null) continue;
            if (dead == null) dead = new List<HairCard>();
            dead.Add(card);
        }
        if (dead == null) return;
        foreach (HairCard card in dead) states.Remove(card);
    }
}

[DefaultExecutionOrder(-10000)]
public class ModifierSourceSnapshotAuthority : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<ModifierSourceSnapshotAuthority>() != null) return;
        GameObject go = new GameObject("ModifierSourceSnapshotAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<ModifierSourceSnapshotAuthority>();
    }

    void Update()
    {
        ModifierEvaluationSnapshots.CaptureSource();
    }
}

[DefaultExecutionOrder(5100)]
public class PreClumpSnapshotAuthority : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<PreClumpSnapshotAuthority>() != null) return;
        GameObject go = new GameObject("PreClumpSnapshotAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<PreClumpSnapshotAuthority>();
    }

    void LateUpdate()
    {
        ModifierEvaluationSnapshots.CapturePreClump();
    }
}
