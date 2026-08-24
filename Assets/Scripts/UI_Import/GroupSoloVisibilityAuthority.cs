using System.Collections.Generic;
using UnityEngine;

// Single source of truth for SOLO.
//
// Two things used to be wrong with SOLO:
//
// 1. It was purely a RENDER switch. MeshRenderer.enabled = false stops the GPU drawing a
//    card, but every per-frame evaluator in the project kept sweeping those cards anyway -
//    PostAffectorManager rebuilt their meshes, PostShapeCurveBridge rebuilt them again,
//    PostFreeCanonicalAuthority rebuilt them a third time, and the clumper authority
//    re-hashed and re-evaluated their groups. Soloing one group out of ten therefore cost
//    almost exactly what showing all ten cost, because the ~3 full GenerateMesh() rebuilds
//    per card per frame carried on regardless of visibility.
//
//    A group that SOLO has hidden is now COMPUTATIONALLY FROZEN: every per-frame evaluator
//    skips it entirely and leaves its cards exactly as they were. Nothing is lost, because
//    canonical state is untouched - only the evaluated mesh goes stale, and the moment SOLO
//    releases the group the normal unconditional sweeps rebuild it on the very next frame.
//
// 2. Visibility had more than one owner. ModelViewer's group-flash coroutine blanket-enabled
//    EVERY renderer in the scene when it finished, and MenuSessionSafety.ResumeGroom did the
//    same - so clicking a second group while soloing quietly revealed everything. Renderer
//    enablement for hair cards is now written in exactly one place, ApplyVisibility(), which
//    always derives from the solo set. Anything that wants to change what is on screen calls
//    through here instead of touching renderers itself.
//
// SOLO is deliberately session-only. It is never written to a project file and never read
// back from one: loading a project comes up with everything visible and SOLO cleared.
public static class GroupSoloVisibilityAuthority
{
    // Initialised here so it is never null and nothing downstream has to test for existence.
    private static readonly HashSet<int> soloedGroups = new HashSet<int>();

    // Bumped on every change to the solo set. Evaluators that cache a per-group "nothing
    // changed" signature compare this and drop their cache when it moves, so a group coming
    // out of the freeze is guaranteed a fresh evaluation instead of resting on a signature
    // it happened to still match.
    private static int epoch = 0;

    // Statics survive "Enter Play Mode -> Disable Domain Reload". Without this, a SOLO left
    // on when you stopped play would still be live when you pressed play again - most of the
    // groom hidden AND frozen, with no lit button to explain why. Reset explicitly.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        soloedGroups.Clear();
        epoch = 0;
    }

    public static int Epoch
    {
        get { return epoch; }
    }

    public static bool AnySolo
    {
        get { return soloedGroups.Count > 0; }
    }

    // A copy, so a caller can Forget while walking it. UndoHistoryAuthority needs this to drop
    // a solo on a group that a step it just replayed no longer contains.
    public static List<int> SoloedGroups()
    {
        return new List<int>(soloedGroups);
    }

    public static bool IsSoloed(int groupId)
    {
        return soloedGroups.Contains(groupId);
    }

    public static bool IsGroupVisible(int groupId)
    {
        if (soloedGroups.Count == 0) return true;
        return soloedGroups.Contains(groupId);
    }

    // Hidden by SOLO == frozen. This is the single predicate every per-frame evaluator asks.
    public static bool IsGroupFrozen(int groupId)
    {
        if (soloedGroups.Count == 0) return false;
        return !soloedGroups.Contains(groupId);
    }

    public static bool IsCardFrozen(HairCard card)
    {
        if (card == null) return false;
        if (soloedGroups.Count == 0) return false;
        return !soloedGroups.Contains(card.groupId);
    }

    // Returns the group's new solo state so the caller can colour its button without
    // having to ask a second question.
    public static bool Toggle(int groupId)
    {
        bool nowSoloed = true;
        if (soloedGroups.Contains(groupId))
        {
            soloedGroups.Remove(groupId);
            nowSoloed = false;
        }
        else
        {
            soloedGroups.Add(groupId);
        }

        epoch++;
        ApplyVisibility();
        return nowSoloed;
    }

    // A deleted group must not keep a SOLO that no card can satisfy - that would leave the
    // whole scene hidden with no button left to switch it back off.
    public static void Forget(int groupId)
    {
        if (!soloedGroups.Remove(groupId)) return;
        epoch++;
        ApplyVisibility();
    }

    public static void ClearAll()
    {
        bool had = soloedGroups.Count > 0;
        soloedGroups.Clear();
        if (had) epoch++;
        ApplyVisibility();
    }

    // The ONLY place hair card renderer enablement is written. Call this instead of
    // blanket-enabling renderers after any operation that may have hidden some.
    public static void ApplyVisibility()
    {
        HairCard[] cards = Object.FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        foreach (HairCard card in cards)
        {
            if (card == null) continue;
            MeshRenderer renderer = card.GetComponent<MeshRenderer>();
            if (renderer == null) continue;
            renderer.enabled = IsGroupVisible(card.groupId);
        }
    }
}
