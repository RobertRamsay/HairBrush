using System.Collections.Generic;
using UnityEngine;

// The landmark pairs a REMAP is solved from, and where the automatic ones come from.
//
// Everything here is plain data and arithmetic - no scene, no rendering, no input. That is
// deliberate: the sampler is the one part of the marker phase with a right answer, so it is kept
// somewhere it can be run and checked without Unity.

// Where a session is in the flow. The order is the order of the phase bar's buttons, and the
// phase decides which markers accept a click - matching ten automatic points and pinning six ear
// slots are different jobs and mixing them is how a user places number 12 by accident.
public enum RemapPhase
{
    AutoMarkers,
    EarMarkers,
    // Coverage satisfied, ready to solve. The solve itself is not built yet.
    Ready
}

public enum RemapMarkerKind
{
    // Placed automatically on the source by sampling the groom's own anchors, then matched by
    // hand on the target.
    Auto,
    // Placed by hand on BOTH models. The ear region is the highest-curvature, most variable part
    // of the correspondence and the one place the sampler cannot be trusted to cover: a groom
    // with no hair behind the ears puts no anchors there, and the warp then interpolates through
    // the region as though it were smooth.
    Ear,
    // The same argument, applied to the lower face - added only when the groom actually reaches
    // it. A beard's roots run along the jawline, under the chin and down the neck, and those are
    // the parts of two heads that agree least: chins differ in length, jaws in width and angle,
    // and the automatic sampler treats all three as ordinary surface. Without pinned landmarks
    // the warp interpolates the whole lower face from markers around it, which is how a scalp
    // lands perfectly and a beard sprays.
    Jaw
}

public class RemapMarker
{
    public int id;
    public string label = string.Empty;
    public string description = string.Empty;
    public RemapMarkerKind kind = RemapMarkerKind.Auto;

    // Ear slots only. Drives the one-shot MIRROR and the left/right agreement check.
    public bool isLeftSide;
    public bool isRightSide;

    public bool sourcePlaced;
    public Vector3 sourcePoint = Vector3.zero;
    public Vector3 sourceNormal = Vector3.up;

    public bool targetPlaced;
    public Vector3 targetPoint = Vector3.zero;
    public Vector3 targetNormal = Vector3.up;

    // True while the target placement is the machine's guess rather than the user's answer.
    //
    // The estimate is good enough to adjust and nowhere near good enough to trust: it assumes the
    // two heads are proportionally similar, which is exactly the assumption the whole REMAP exists
    // because it is false. So an estimated marker is drawn faded, and the moment it is dragged it
    // becomes a real placement and draws solid. That difference is the user's checklist - what is
    // still faded is what has not been looked at.
    public bool targetIsEstimate;

    public bool Paired
    {
        get
        {
            if (!sourcePlaced) return false;
            if (!targetPlaced) return false;
            return true;
        }
    }
}

public static class RemapMarkerSet
{
    // Thirty rather than ten. Ten spreads well over a scalp, but a groom that also carries a
    // beard is two regions with a face between them, and the farthest-point pass spends its first
    // handful of picks on the outer extremes of the whole cloud - which left the jaw and chin
    // under-constrained, so the warp there was interpolating across a gap instead of following
    // markers. The solve is a (N+4) square system either way; thirty is 34x34, still nothing.
    //
    // They are estimated onto the new head automatically now, so the cost of raising this is
    // review rather than placement.
    public const int AutoMarkerCount = 30;

    // Named rather than free points. A triangle of three per ear pins the region's position,
    // scale AND rotation, where a single crease point pins position only - and named slots are
    // what make the left/right agreement check possible at all, because a slot knows which side
    // it is supposed to be on.
    //
    // All three are on the HEAD behind the ear, not on the ear itself. A marker clicked on the
    // pinna, against a model whose ear geometry differs, drags scalp anchors onto the ear, which
    // is the exact failure the ear markers exist to prevent.
    public static List<RemapMarker> BuildEarMarkers(int startingId)
    {
        List<RemapMarker> markers = new List<RemapMarker>();
        string[] slots = new string[] { "HELIX ROOT", "CREASE", "LOBE" };
        string[] hints = new string[]
        {
            "top of the ear attachment, on the head",
            "behind the ear, mid height, in the crease",
            "below the lobe attachment, on the head"
        };

        int id = startingId;
        for (int side = 0; side < 2; side++)
        {
            bool left = side == 0;
            string sideLabel = "R";
            if (left) sideLabel = "L";

            for (int i = 0; i < slots.Length; i++)
            {
                RemapMarker marker = new RemapMarker();
                marker.id = id;
                marker.kind = RemapMarkerKind.Ear;
                marker.isLeftSide = left;
                marker.isRightSide = !left;
                marker.label = id.ToString();
                marker.description = sideLabel + " " + slots[i] + " - " + hints[i];
                markers.Add(marker);
                id++;
            }
        }
        return markers;
    }

    // The lower-face landmarks, for grooms that reach the jaw. Same reasoning as the ear slots,
    // same shape: named positions rather than free points, so both heads are answering the same
    // question and the left/right check has something to compare.
    public static List<RemapMarker> BuildJawMarkers(int startingId)
    {
        List<RemapMarker> markers = new List<RemapMarker>();
        string[] slots = new string[] { "CHIN TIP", "UNDER CHIN", "L JAW ANGLE", "R JAW ANGLE" };
        string[] hints = new string[]
        {
            "front point of the chin",
            "underneath the chin, on the soft edge",
            "left corner of the jaw, below the ear",
            "right corner of the jaw, below the ear"
        };

        int id = startingId;
        for (int i = 0; i < slots.Length; i++)
        {
            RemapMarker marker = new RemapMarker();
            marker.id = id;
            marker.kind = RemapMarkerKind.Jaw;
            marker.isLeftSide = i == 2;
            marker.isRightSide = i == 3;
            marker.label = id.ToString();
            marker.description = slots[i] + " - " + hints[i];
            markers.Add(marker);
            id++;
        }
        return markers;
    }

    // Farthest-point sampling over the groom's own anchors.
    //
    // Sampled from the ANCHORS rather than from anatomy, because a thin plate spline is accurate
    // inside the convex hull of its markers and degrades outside it. Taking the markers from the
    // cloud that is about to move gives a hull that provably contains every point being moved;
    // an anatomical set would spend the accuracy budget where there is no hair.
    //
    // Farthest-point rather than k-means: it is deterministic, has no cluster count to tune, and
    // takes the extremes first, which is exactly what hull coverage wants. K-means centroids give
    // more even density and worse hull coverage, which is the wrong trade here.
    //
    // Seeded from the anchor nearest the centroid so the result does not depend on the order the
    // scene happened to enumerate cards in.
    public static List<int> FarthestPointSample(List<Vector3> anchors, int count)
    {
        List<int> chosen = new List<int>();
        if (anchors == null || anchors.Count == 0 || count <= 0) return chosen;

        int wanted = count;
        if (wanted > anchors.Count) wanted = anchors.Count;

        Vector3 centroid = Vector3.zero;
        for (int i = 0; i < anchors.Count; i++) centroid += anchors[i];
        centroid = centroid / anchors.Count;

        int seed = 0;
        float bestToCentroid = float.MaxValue;
        for (int i = 0; i < anchors.Count; i++)
        {
            float d = (anchors[i] - centroid).sqrMagnitude;
            if (d < bestToCentroid)
            {
                bestToCentroid = d;
                seed = i;
            }
        }
        chosen.Add(seed);

        // Running nearest-chosen distance per anchor, updated against the one marker just added
        // rather than rescanned - the difference between O(n*k) and O(n*k*k), which matters on a
        // dense groom.
        float[] nearest = new float[anchors.Count];
        for (int i = 0; i < anchors.Count; i++) nearest[i] = (anchors[i] - anchors[seed]).sqrMagnitude;

        while (chosen.Count < wanted)
        {
            int pick = -1;
            float bestDistance = -1f;
            for (int i = 0; i < anchors.Count; i++)
            {
                if (nearest[i] > bestDistance)
                {
                    bestDistance = nearest[i];
                    pick = i;
                }
            }
            // Every remaining anchor is coincident with one already chosen; there is nothing
            // further to find and padding the set with duplicates would only make the solve
            // ill-conditioned.
            if (pick < 0 || bestDistance <= .000001f) break;

            chosen.Add(pick);
            for (int i = 0; i < anchors.Count; i++)
            {
                float d = (anchors[i] - anchors[pick]).sqrMagnitude;
                if (d < nearest[i]) nearest[i] = d;
            }
        }

        return chosen;
    }

    // Two markers a fraction of a millimetre apart make the spline system ill-conditioned, and
    // the symptom is a wild warp rather than a diagnostic. Reported as a pair so the caller can
    // name them.
    public static bool TryFindTooClose(List<RemapMarker> markers, float minimumSeparation, out int firstId, out int secondId)
    {
        firstId = -1;
        secondId = -1;
        if (markers == null) return false;

        float limit = minimumSeparation * minimumSeparation;
        for (int i = 0; i < markers.Count; i++)
        {
            if (markers[i] == null || !markers[i].sourcePlaced) continue;
            for (int j = i + 1; j < markers.Count; j++)
            {
                if (markers[j] == null || !markers[j].sourcePlaced) continue;
                if ((markers[i].sourcePoint - markers[j].sourcePoint).sqrMagnitude >= limit) continue;
                firstId = markers[i].id;
                secondId = markers[j].id;
                return true;
            }
        }
        return false;
    }

    // A swapped left/right pair folds the groom inside out and the spline raises nothing at all,
    // because a fold is a perfectly good answer to the question it was asked. Named slots make it
    // checkable: a marker that sits one side of the midline on the source and the other side on
    // the target has been mismatched.
    //
    // Compared in each model's OWN local space, since the two heads sit at different world X.
    public static bool TryFindSideMismatch(
        List<RemapMarker> markers,
        Transform sourceRoot,
        Transform targetRoot,
        out int mismatchedId)
    {
        mismatchedId = -1;
        if (markers == null || sourceRoot == null || targetRoot == null) return false;

        foreach (RemapMarker marker in markers)
        {
            if (marker == null || !marker.Paired) continue;
            float sourceX = sourceRoot.InverseTransformPoint(marker.sourcePoint).x;
            float targetX = targetRoot.InverseTransformPoint(marker.targetPoint).x;

            // Near the midline either sign is legitimate - a nape-centre or crown-centre marker
            // is its own mirror - so only a decisive disagreement counts.
            if (Mathf.Abs(sourceX) < .01f) continue;
            if (Mathf.Abs(targetX) < .01f) continue;
            if (sourceX * targetX >= 0f) continue;

            mismatchedId = marker.id;
            return true;
        }
        return false;
    }

    public static bool InteractiveInPhase(RemapMarker marker, RemapPhase phase)
    {
        if (marker == null) return false;
        if (phase == RemapPhase.AutoMarkers) return marker.kind == RemapMarkerKind.Auto;
        if (phase == RemapPhase.EarMarkers)
        {
            if (marker.kind == RemapMarkerKind.Ear) return true;
            return marker.kind == RemapMarkerKind.Jaw;
        }
        return true;
    }

    // The next marker awaiting a placement on the given side, in marker order.
    //
    // Shared by the view (which highlights it and is where a click on bare surface lands) and by
    // the phase bar (which names it). Two copies of "what am I placing next" is exactly the sort
    // of thing that drifts into the highlight and the instruction disagreeing.
    public static int NextUnplaced(List<RemapMarker> markers, RemapPhase phase, bool isTarget)
    {
        if (markers == null) return -1;
        for (int i = 0; i < markers.Count; i++)
        {
            RemapMarker marker = markers[i];
            if (!InteractiveInPhase(marker, phase)) continue;
            if (isTarget && !marker.targetPlaced) return i;
            if (!isTarget && !marker.sourcePlaced) return i;
        }
        return -1;
    }

    // The next placement wanted, alternating between the two heads marker by marker.
    //
    // Asking for every source placement first and every target placement afterwards is what let a
    // user put all six ear markers on one head and watch a pair counter sit at zero the whole
    // time. Finishing each marker before starting the next means the count moves on every second
    // click, and no marker can be six placements deep on one side with nothing opposite it.
    public static bool NextPending(List<RemapMarker> markers, RemapPhase phase, out int index, out bool isTarget)
    {
        index = -1;
        isTarget = false;
        if (markers == null) return false;

        for (int i = 0; i < markers.Count; i++)
        {
            RemapMarker marker = markers[i];
            if (!InteractiveInPhase(marker, phase)) continue;
            if (!marker.sourcePlaced)
            {
                index = i;
                isTarget = false;
                return true;
            }
            if (!marker.targetPlaced)
            {
                index = i;
                isTarget = true;
                return true;
            }
        }
        return false;
    }

    // How many are placed on ONE side, which is not the same question as how many are paired.
    //
    // A marker needs both halves before it is worth anything to the solve, so the headline count
    // is pairs - but a user who has just put three markers on the new head and sees 0/6 has been
    // told their work did not register. Both numbers have to be visible or the honest count reads
    // as a bug.
    public static void PhaseSideProgress(List<RemapMarker> markers, RemapPhase phase, bool isTarget, out int done, out int total)
    {
        done = 0;
        total = 0;
        if (markers == null) return;
        foreach (RemapMarker marker in markers)
        {
            if (!InteractiveInPhase(marker, phase)) continue;
            total++;
            if (isTarget && marker.targetPlaced) done++;
            if (!isTarget && marker.sourcePlaced) done++;
        }
    }

    // How many of the markers this phase is responsible for are done, as a pair.
    public static void PhaseProgress(List<RemapMarker> markers, RemapPhase phase, out int done, out int total)
    {
        done = 0;
        total = 0;
        if (markers == null) return;
        foreach (RemapMarker marker in markers)
        {
            if (!InteractiveInPhase(marker, phase)) continue;
            total++;
            if (marker.Paired) done++;
        }
    }

    public static int CountPaired(List<RemapMarker> markers)
    {
        int total = 0;
        if (markers == null) return 0;
        foreach (RemapMarker marker in markers)
        {
            if (marker != null && marker.Paired) total++;
        }
        return total;
    }

    // The gate on PROCESS is coverage, not a count: the automatic set matched, plus at least one
    // pinned pair behind each ear. "Ten markers" is satisfiable by ten points on the crown.
    public static bool CoverageSatisfied(List<RemapMarker> markers, out string reason)
    {
        reason = string.Empty;
        if (markers == null || markers.Count == 0)
        {
            reason = "no markers";
            return false;
        }

        int autoUnpaired = 0;
        int leftEar = 0;
        int rightEar = 0;
        int jawTotal = 0;
        int jawPaired = 0;
        foreach (RemapMarker marker in markers)
        {
            if (marker == null) continue;
            if (marker.kind == RemapMarkerKind.Auto && !marker.Paired) autoUnpaired++;
            if (marker.kind == RemapMarkerKind.Jaw)
            {
                jawTotal++;
                if (marker.Paired) jawPaired++;
            }
            if (marker.kind != RemapMarkerKind.Ear || !marker.Paired) continue;
            if (marker.isLeftSide) leftEar++;
            if (marker.isRightSide) rightEar++;
        }

        if (autoUnpaired > 0)
        {
            reason = autoUnpaired + " automatic marker(s) still unmatched on the new head";
            return false;
        }
        if (leftEar == 0)
        {
            reason = "the left ear has no pinned marker";
            return false;
        }
        if (rightEar == 0)
        {
            reason = "the right ear has no pinned marker";
            return false;
        }
        // Only demanded when the set contains them at all, which is only when the groom reaches
        // the lower face. A scalp-only groom is never asked to point at a chin.
        if (jawTotal > 0 && jawPaired < jawTotal)
        {
            reason = (jawTotal - jawPaired) + " jaw marker(s) still unpinned";
            return false;
        }
        return true;
    }
}
