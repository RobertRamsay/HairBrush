using System.Collections.Generic;
using UnityEngine;

// COPY and PASTE for a guide's shape, from any guide onto any other, across groups.
//
// WHAT TRAVELS: the node positions, the per-node rolls, and the zone - Amount, Radius, Falloff.
// A pasted guide combs the same way, as hard, over the same reach.
//
// WHAT DOES NOT: the COLOUR, and that omission is deliberate rather than an oversight. Hue is how
// you tell two overlapping guides apart on a head, and a paste is most useful precisely when you
// are building several guides that do the same thing in different places - so carrying the colour
// across would make every one of them identical at the moment there are most of them to
// distinguish. The target keeps the colour it had.
//
// Nor the CONTACT or the FRAME. Those are where the guide is and which way the surface faces
// under it, and pasting them would move the target guide, which is not what a paste means. The
// nodes are stored in the guide's own contact frame, so dropping the same offsets onto a
// different root reproduces the shape relative to that root - the same property that lets
// SPACE + click carry a guide across the head with its form intact.
//
// SESSION-ONLY, and it survives opening another project on purpose. The clip holds plain numbers
// with no reference to a guide, a group or a model that could go stale, so carrying a comb shape
// from one file into the next is useful rather than surprising. Same reasoning as
// GroupParameterClipboardAuthority, which this is modelled on.
public static class GuideClipboardAuthority
{
    private sealed class Clip
    {
        public List<Vector3> nodes = new List<Vector3>();
        public List<float> rolls = new List<float>();
        public float amount;
        public float radius;
        public float falloff;

        // Only so the toast can say what was copied. Never applied.
        public int sourceId;
    }

    private static Clip clip;

    // Statics survive "Enter Play Mode -> Disable Domain Reload", so without this the clipboard
    // starts the next Play session holding the last one's guide.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        clip = null;
    }

    public static bool HasClip
    {
        get { return clip != null; }
    }

    public static int CopiedFromId
    {
        get { return clip != null ? clip.sourceId : -1; }
    }

    public static bool Copy(GuideCurveManager.GuideCurve guide)
    {
        if (guide == null || guide.nodesLocal == null) return false;

        GuideCurveManager.NormaliseRoll(guide);

        Clip next = new Clip
        {
            amount = guide.amount,
            radius = guide.radius,
            falloff = guide.falloff,
            sourceId = guide.id
        };

        // COPIED, not referenced. The source guide goes on being edited after this and a clip
        // holding its live lists would follow every drag - so a paste would deliver whatever the
        // source looks like NOW rather than what it looked like when COPY was pressed.
        for (int i = 0; i < guide.nodesLocal.Count; i++)
        {
            next.nodes.Add(guide.nodesLocal[i]);
            next.rolls.Add(i < guide.nodeRoll.Count ? guide.nodeRoll[i] : 0f);
        }

        if (next.nodes.Count < GuideCurveManager.MinGuideNodes) return false;

        clip = next;
        return true;
    }

    public static bool Paste(GuideCurveManager.GuideCurve guide)
    {
        if (guide == null || clip == null) return false;
        if (clip.nodes.Count < GuideCurveManager.MinGuideNodes) return false;

        // Fresh lists rather than the clip's own, so pasting the same clip onto a second guide
        // does not hand both of them the same List to edit.
        List<Vector3> nodes = new List<Vector3>();
        List<float> rolls = new List<float>();

        // The ceiling is enforced here as well as at the insert gesture. A clip taken from a
        // twenty-point guide is exactly at it, so nothing normally trims - but a future change
        // that lowered MaxGuideNodes would otherwise paste a guide the editor cannot represent.
        int count = Mathf.Min(clip.nodes.Count, GuideCurveManager.MaxGuideNodes);
        for (int i = 0; i < count; i++)
        {
            nodes.Add(clip.nodes[i]);
            rolls.Add(i < clip.rolls.Count ? clip.rolls[i] : 0f);
        }

        // Trimming must never lose the TIP - it is the point that decides how far the guide
        // reaches, and a guide silently shortened by a paste would be the hardest kind of change
        // to account for. Same rule the project loader trims by.
        if (clip.nodes.Count > count)
        {
            nodes[count - 1] = clip.nodes[clip.nodes.Count - 1];
            rolls[count - 1] = clip.rolls[clip.rolls.Count - 1];
        }

        guide.nodesLocal = nodes;
        guide.nodeRoll = rolls;
        guide.amount = Mathf.Clamp01(clip.amount);
        guide.radius = Mathf.Max(.001f, clip.radius);
        guide.falloff = Mathf.Max(0f, clip.falloff);

        // Nothing to tell the evaluator by hand: node positions, rolls, amount, radius and
        // falloff are every one of them folded into ThreeColumnClumperMeshAuthority's group
        // signature, so the group dirties itself on the next LateUpdate.
        return true;
    }
}
