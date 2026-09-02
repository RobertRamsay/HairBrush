using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class HairCard : MonoBehaviour
{
    [System.Serializable]
    public struct GroomState
    {
        public float length, width, bend, twist, depth;
        public int segments;
        public float x, y, z;
        public float uScale, vScale, uOffset, vOffset;
        public float curlFrequency, curlDiameter;
        public float waveAmplitude, waveFrequency, waveDirection;
        public float arch;
    }

    // A POST keeps its authored scalar delta, but its Bend/X/Y/Z contribution can have a
    // different root-to-tip profile from both the group root and the other POSTs. The manager's
    // scalar evaluator remains untouched; this transient list only carries profile provenance
    // into mesh generation so those additive deltas are shaped independently per row.
    public struct PostShapeProfileContribution
    {
        public int postId;
        public float bend, x, y, z;
    }

    // Native card cross-section: left edge / raised centre / right edge. The ridge height
    // follows card width so narrow and wide cards keep the same shallow convex profile.
    public const float CrossSectionRidgeRatio = 0.18f;

    // The arch value that leaves the cross-section exactly as it was before Arch existed.
    // Also where a brand-new card, a reset group and a legacy project all land.
    public const float ArchNeutral = 0.5f;
    // How many points a row of the cross-section has. Three under TENT, four under DIAMOND.
    //
    // Was a const, and every caller wrote `const int columns = HairCard.CrossSectionColumns;`
    // - which is exactly what a const is for and exactly what makes a second profile
    // impossible, since the value would be baked into each caller at compile time. It is a
    // property now, and HairCardSection owns the answer.
    public static int CrossSectionColumns
    {
        get { return HairCardSection.Columns; }
    }

    // Curl banking. Without this the coil only displaces the centreline while every
    // cross-section keeps pointing the same way, so a curled card cycles between
    // face-on and edge-on once per turn and reads as a twisting ribbon rather than
    // a coil. Rolling each section about the card's own length axis by the curl
    // angle keeps its relationship to the coil constant all the way to the tip -
    // the way an aircraft banks into a turn instead of staying wings-level.
    //
    // 1 = fully banked (section angle tracks the curl angle exactly).
    // 0 = the old unbanked behaviour.
    // Values above 1 over-rotate, which can be a useful stylisation.
    // This stacks with Twist; the twist slider still adds its own roll on top.
    // static readonly rather than const on purpose: as a const the compiler folds
    // every guard below into a constant and reports the other branch as unreachable.
    public static readonly float CurlBankAmount = 1f;

    // Bend used to rotate each cross-section by the authored bend/twist rotation and
    // nothing else. That rotation's forward axis is NOT the direction the bent spine
    // actually travels in - the spine point is rotation * (0,0,z), so as the bend
    // angle changes along the length the true tangent swings away from the section's
    // forward axis. Every section therefore sat at a slant to its own path, which
    // squashes anything with lateral extent: a curl coil gets projected into an
    // ellipse (down to about 54% of its width at a 90 degree bend), reading as a
    // flattened, skewed curl.
    //
    // With this on, the spine is left exactly where it was - bend shapes the card
    // the same way it always did - but each section is re-aimed by the minimal
    // rotation that puts its forward axis on the spine's real tangent, so the curl
    // keeps its round cross-section however hard the card is bent. Set false for the
    // old behaviour.
    // static readonly for the same reason as CurlBankAmount above.
    public static readonly bool BendFollowsPath = true;

    // Single source of truth for the coil. GenerateMesh and the clumped-card mesh
    // rebuild in ThreeColumnClumperMeshAuthority both call this, so the two cannot
    // drift apart again the way they did when curl was first added.
    //
    // curlOffset is the sideways displacement of the centreline at t. bankRotation
    // is the roll the flat cross-section should carry at t, about the card's own
    // length axis, and must be applied to the section BEFORE the offset: the roll
    // shapes the section, the offset moves it.
    // Below this, a row's three columns collapse onto one point: zero-area triangles, and
    // RecalculateNormals then produces garbage for that row, which reads on screen as a black
    // band at the tip. Curl guards its own degenerate case the same way (curlDiameter <= 0).
    public const float MinimumWidthMultiplier = .001f;

    // Minimum vertex rows per full cycle of a periodic modifier (Wave, Curl).
    //
    // A card only has `segments + 1` rows, and every periodic modifier is SAMPLED at those
    // rows - so the card's tessellation is a hard ceiling on the frequency it can express.
    // Push past it and the result is not "a tighter wave", it is an aliased one: the apparent
    // frequency folds back DOWN as the slider goes up, and at exactly segments/2 the samples
    // land on the zero crossings and the wave disappears altogether. At the default 12
    // segments that made frequency 6 render perfectly flat, and 7 through 10 render as 5, 4,
    // 3 and 2 - so the top half of the slider ran backwards and had a dead spot in the middle.
    //
    // Two samples per cycle is the Nyquist floor and still looks like a zigzag rather than a
    // wave, so the usable limit is a little above it.
    public const float MinimumRowsPerCycle = 2.5f;

    // Highest frequency this card's tessellation can actually render.
    // Raise Segments to unlock more: 12 segments allows 4.8, 24 allows 9.6, 32 allows 12.8.
    // Triangle winding for the three-column strip, in ONE place.
    //
    // GenerateMesh and BuildCleanMesh each used to lay these indices out themselves. That was
    // survivable while there was only one winding; with N- there are two, and two hand-written
    // copies of a winding rule is precisely how a clumped card ends up lit inside-out while an
    // unclumped one is not.
    //
    // flipWinding reverses each triangle, which is what actually inverts the surface normals -
    // RecalculateNormals derives them from winding, so there is nothing else to flip.
    // Kept as the name every builder already calls; the topology itself moved to
    // HairCardSection when the DIAMOND profile arrived, because a second winding pattern was
    // one hand-written copy too many. Under TENT it emits the same four triangles, in the same
    // order, that used to be written out here.
    // `vertices` lets each quad be split on its shorter diagonal - see HairCardSection for why a
    // fixed diagonal made a symmetric card render asymmetrically. Pass the positions that were
    // just written; omitting them falls back to a symmetric but shape-blind rule.
    public static void BuildStripTriangles(int segments, bool flipWinding, int[] triangles, Vector3[] vertices = null)
    {
        HairCardSection.BuildTriangles(segments, flipWinding, triangles, vertices);
    }

    public static float MaxRepresentableTurns(int segments)
    {
        return Mathf.Max(1f, segments / MinimumRowsPerCycle);
    }

    // Single source of truth for the card's cross-section at t.
    //
    // GenerateMesh and BOTH mesh reconstructions - ThreeColumnClumperMeshAuthority.
    // BuildCleanMesh and ModifierNeutralizeBeforeDeleteAuthority.WriteCleanThreeColumnMesh -
    // call this, so the three cannot drift apart. That is the same guarantee EvaluateCurl and
    // BuildSegmentFrames already give for the coil and the spine, and it exists for the same
    // reason: this project has twice shipped a feature into GenerateMesh only, and twice had
    // clumped cards silently render the pre-feature shape.
    //
    // Width was the third thing computed independently in three places, and the three did not
    // even agree: GenerateMesh used a raw `width * .5f` while both reconstructions used
    // `Mathf.Max(.0005f, width) * .5f`, so cards under 0.001 wide were already fractionally
    // wider once clumped. Folding it in here settles that too.
    public static void EvaluateCrossSection(HairCard card, float t, out float halfSpan, out float ridge)
    {
        halfSpan = 0f;
        ridge = 0f;
        if (card == null) return;

        // Root-only profile curve, same as Curl and Segment Density - see the channel enum.
        // GroomShapeCurveRegistry.Evaluate clamps every channel to 0..1, so this can only ever
        // narrow the card, never widen it past the Width slider.
        // Untouched profile means the multiplier is exactly 1, so the evaluation is pure
        // overhead - and this runs once per row per card per rebuild.
        float widthMultiplier = 1f;
        if (!GroomShapeCurveRegistry.IsFlatOne(card.groupId, GroomShapeCurveChannel.Width))
        {
            widthMultiplier = PostShapeCurveBridge.EvaluateRoot(card.groupId, GroomShapeCurveChannel.Width, t);
            widthMultiplier = Mathf.Max(MinimumWidthMultiplier, widthMultiplier);
        }

        halfSpan = Mathf.Max(.0005f, card.width) * .5f * card.flattenFactor * widthMultiplier;

        // Scaled by the SAME multiplier so the cross-section stays self-similar along the
        // length. The ridge is defined as a fixed ratio of width; leaving it un-tapered would
        // turn a narrowed tip into a tall thin spike rather than a smaller copy of the root.
        // Arch scales the ridge about its neutral point: 0.5 -> x1 (exactly the shape the tool
        // had before Arch existed), 0 -> flat ribbon, 1 -> double. Clamped at 0 only; a POST is
        // free to drive it past what the slider itself allows.
        float archScale = Mathf.Max(0f, card.arch) / ArchNeutral;
        ridge = card.GetCrossSectionRidgeHeight() * widthMultiplier * archScale;

        // N-: invert the arch. The cross-section is left edge / raised centre / right edge, so
        // negating the centre's height turns the shallow convex profile concave - the A / V
        // flip - and pairs with the reversed winding to give a properly mirrored surface
        // rather than a correct-looking shape lit from the wrong side.
        if (GroupNormalFlipAuthority.IsFlipped(card.groupId)) ridge = -ridge;
    }

    // Single source of truth for the WAVE, exactly as EvaluateCurl is for the coil.
    // GenerateMesh and both mesh reconstructions call this, so the three cannot drift.
    //
    // The wave is a PLANAR sinusoid: it displaces the cross-section along the card's own local
    // X - side to side within the flat plane of the card - with the phase advancing along the
    // length. That is what reads as waviness in silhouette, and it is deliberately different
    // from Curl, which displaces in both X and Y and sweeps around the length axis to make a
    // coil. The two stack cleanly because they act on different axes.
    //
    // To wave ACROSS the face of the card instead of side to side, move the amplitude from
    // the x component to the y component of waveOffset below. That is the whole change.
    //
    // sin(0) = 0, so the wave is exactly zero at the root and the card stays anchored to the
    // scalp however hard it is driven - the same property EvaluateCurl gets from cos(0)-1.
    public static void EvaluateWave(
        HairCard card,
        float t,
        out Vector3 waveOffset,
        bool mirrored = false)
    {
        waveOffset = Vector3.zero;
        if (card == null) return;

        // Both of these are exact "this contributes nothing" tests, so they cost two float
        // compares and save two AnimationCurve evaluations plus a sin() per row per card.
        if (card.waveAmplitude <= 0f) return;
        if (card.waveFrequency == 0f) return;

        int groupId = card.groupId;

        // Skip the curve read entirely when nobody has drawn on that profile. Evaluate would
        // return exactly 1 for a flat default curve, so multiplying by it is provably a no-op.
        float amplitude = card.waveAmplitude;
        if (!GroomShapeCurveRegistry.IsFlatOne(groupId, GroomShapeCurveChannel.WaveAmplitude))
            amplitude *= PostShapeCurveBridge.EvaluateRoot(groupId, GroomShapeCurveChannel.WaveAmplitude, t);

        float turns = card.waveFrequency;
        if (!GroomShapeCurveRegistry.IsFlatOne(groupId, GroomShapeCurveChannel.WaveFrequency))
            turns *= PostShapeCurveBridge.EvaluateRoot(groupId, GroomShapeCurveChannel.WaveFrequency, t);

        // Clamped to what the card's rows can carry. Without this the slider is not merely
        // capped at the top - it actively runs backwards past the halfway point and dies at
        // segments/2, which reads as "moving this does nothing".
        float maxTurns = MaxRepresentableTurns(card.segments);
        turns = Mathf.Clamp(turns, -maxTurns, maxTurns);

        float direction = Mathf.Clamp01(card.waveDirection);
        if (!GroomShapeCurveRegistry.IsFlatOne(groupId, GroomShapeCurveChannel.WaveDirection))
            direction *= PostShapeCurveBridge.EvaluateRoot(groupId, GroomShapeCurveChannel.WaveDirection, t);

        // 0 -> local X (side to side, in the card's flat plane), 1 -> local Y (up and down,
        // across its face). Unit length at every angle, so amplitude is honest throughout.
        float angle = direction * Mathf.PI * .5f;
        float axisX = Mathf.Cos(angle);
        float axisY = Mathf.Sin(angle);

        // A mirrored card is a reflection through local X, so ONLY the X component flips.
        // Negating the amplitude instead - which was right while the wave was X-only - would
        // now wrongly flip the up/down component too and break symmetry on any diagonal.
        if (mirrored) axisX = -axisX;

        float displacement = amplitude * Mathf.Sin(turns * t * Mathf.PI * 2f);
        waveOffset = new Vector3(axisX * displacement, axisY * displacement, 0f);
    }

    public static void EvaluateCurl(
        HairCard card,
        float t,
        out Vector3 curlOffset,
        out Quaternion bankRotation,
        bool mirrored = false)
    {
        curlOffset = Vector3.zero;
        bankRotation = Quaternion.identity;
        if (card == null) return;

        float curlFrequency = card.curlFrequency;
        float curlDiameter = card.curlDiameter;
        int groupId = card.groupId;

        if (curlFrequency == 0f) return;
        if (curlDiameter <= 0f) return;

        // Root-only profile curves (see GroomShapeCurveAuthority) - no per-POST override.
        float turns = curlFrequency;
        if (!GroomShapeCurveRegistry.IsFlatOne(groupId, GroomShapeCurveChannel.CurlFrequency))
            turns *= PostShapeCurveBridge.EvaluateRoot(groupId, GroomShapeCurveChannel.CurlFrequency, t);

        // Same tessellation ceiling as the wave - the coil is sampled at the same rows, so it
        // aliases the same way. This is almost certainly what "curl frequency does nothing"
        // was: past segments/2.5 the coil stops tightening and starts unwinding again.
        float maxTurns = MaxRepresentableTurns(card.segments);
        turns = Mathf.Clamp(turns, -maxTurns, maxTurns);

        float radius = curlDiameter * .5f;
        if (!GroomShapeCurveRegistry.IsFlatOne(groupId, GroomShapeCurveChannel.CurlDiameter))
            radius *= PostShapeCurveBridge.EvaluateRoot(groupId, GroomShapeCurveChannel.CurlDiameter, t);

        // A coil is handed, so a mirrored card's coil must wind the other way. Negating BOTH
        // the radius and the sweep is what makes the reflection exact: the offset becomes
        // (-r(cos a - 1), +r sin a, 0), which is precisely diag(-1,1,1) applied to the original,
        // and the bank rotation about local Z flips with it.
        //
        // Negating the angle alone would give the right handedness but leave the coil bulging
        // 180 degrees out of phase; negating curlDiameter on the card instead is not an option,
        // because the guard above rejects a non-positive diameter outright.
        if (mirrored)
        {
            radius = -radius;
            turns = -turns;
        }

        float angle = turns * t * Mathf.PI * 2f;

        // cos(0)-1 = 0 and sin(0) = 0, so this is exactly zero at the root (t=0),
        // keeping the coil continuous with the card's actual root position.
        curlOffset = new Vector3(radius * (Mathf.Cos(angle) - 1f), radius * Mathf.Sin(angle), 0f);

        if (CurlBankAmount == 0f) return;

        // THE SNAP FIX. The bank roll used to be computed from the sweep angle alone, with no
        // reference to the radius at all - so it went from nothing at diameter 0 to its FULL
        // value the instant the diameter guard above was cleared. At diameter 0.001 the coil
        // itself is half a thousandth of a unit wide and invisible, while every cross-section
        // was already rolling through the complete curl angle: at frequency 5 that is five full
        // turns of ribbon twist appearing out of nowhere. That is the pop.
        //
        // Banking exists to keep the coil's cross-section round as it sweeps. When the coil is
        // far narrower than the card itself there is no coil to keep round, so the roll should
        // not be there. Fading it in against the card's own half-width gives a scale-correct
        // ramp: by the time the coil radius matches the half-width the bank is at full strength
        // and behaves exactly as before, and everything below that eases in smoothly.
        float halfSpan;
        float ridge;
        EvaluateCrossSection(card, t, out halfSpan, out ridge);

        float bankFade = 1f;
        if (halfSpan > .000001f) bankFade = Mathf.Clamp01(Mathf.Abs(radius) / halfSpan);
        if (bankFade <= .0001f) return;

        bankRotation = Quaternion.AngleAxis(angle * Mathf.Rad2Deg * CurlBankAmount * bankFade, Vector3.forward);
    }

    // How finely the density curve is integrated. A handful of keyframes is smooth
    // enough that 64 trapezoids put every row within a ten-thousandth of the card's
    // length of its exact place.
    private const int SegmentDensitySamples = 64;

    // Reused between rebuilds. Mesh generation is main-thread only, so one shared
    // buffer avoids allocating this on every single card every frame.
    private static readonly float[] segmentDensityCumulative = new float[SegmentDensitySamples + 1];

    // Resolves where each segment row sits along the length from the SEGMENT DENSITY
    // curve, where Y is "how many segments per unit length here".
    //
    // Rows are placed at the points that cut the area under the curve into equal
    // shares, which is the inverse of its normalised cumulative area. Two useful
    // consequences fall straight out of that:
    //
    //   - Height alone means nothing, only shape. A curve flat at 0.2 and a curve
    //     flat at 1 both give perfectly even spacing, because the area accumulates
    //     at a constant rate either way.
    //   - Low on the left rising to high on the right puts few rows near the root
    //     and packs them toward the tip. High on the left does the opposite.
    //
    // The segment COUNT is untouched by any of this - that is the Segments slider
    // alone. The curve only decides where those rows land.
    //
    // Root and tip are pinned to exactly 0 and 1 so Length always produces the
    // expected span, and the cumulative area can only ever increase, so no curve
    // can fold the mesh back on itself.
    static void ResolveSegmentPositions(int groupId, int segments, float[] segmentT)
    {
        // A flat x1 density curve means evenly spaced rows by definition, so the whole
        // 64-sample cumulative integration below - and the search that walks it once per row -
        // reduces to a divide. This is the largest single saving of the lot: it is ~65 curve
        // evaluations per card per rebuild, paid on every groom whether or not anyone has ever
        // opened the Segment Density profile.
        if (GroomShapeCurveRegistry.IsFlatOne(groupId, GroomShapeCurveChannel.SegmentDensity))
        {
            for (int i = 0; i <= segments; i++) segmentT[i] = (float)i / segments;
            return;
        }

        float step = 1f / SegmentDensitySamples;
        float previousDensity = Mathf.Max(0f, PostShapeCurveBridge.EvaluateRoot(groupId, GroomShapeCurveChannel.SegmentDensity, 0f));
        segmentDensityCumulative[0] = 0f;

        for (int k = 1; k <= SegmentDensitySamples; k++)
        {
            float x = (float)k / SegmentDensitySamples;
            float density = Mathf.Max(0f, PostShapeCurveBridge.EvaluateRoot(groupId, GroomShapeCurveChannel.SegmentDensity, x));
            segmentDensityCumulative[k] = segmentDensityCumulative[k - 1] + (previousDensity + density) * .5f * step;
            previousDensity = density;
        }

        float total = segmentDensityCumulative[SegmentDensitySamples];

        // A curve that is zero everywhere says nothing about where rows belong.
        // Fall back to even spacing rather than collapsing the card onto its root.
        if (total <= 1e-6f)
        {
            for (int i = 0; i <= segments; i++) segmentT[i] = (float)i / segments;
            return;
        }

        // Targets rise with i, so the search cursor only ever moves forward.
        int cursor = 0;
        for (int i = 0; i <= segments; i++)
        {
            if (i == 0)
            {
                segmentT[i] = 0f;
                continue;
            }

            if (i == segments)
            {
                segmentT[i] = 1f;
                continue;
            }

            float target = total * i / segments;
            while (cursor < SegmentDensitySamples - 1 && segmentDensityCumulative[cursor + 1] < target) cursor++;

            float spanStart = segmentDensityCumulative[cursor];
            float spanEnd = segmentDensityCumulative[cursor + 1];
            float within = 0f;
            if (spanEnd > spanStart) within = Mathf.Clamp01((target - spanStart) / (spanEnd - spanStart));

            segmentT[i] = Mathf.Clamp01(((float)cursor + within) * step);
        }
    }

    // Resolves, for every segment row, where it sits along the length (see
    // ResolveSegmentPositions), where the spine sits there, and the frame its
    // cross-section should be placed in. Shared by GenerateMesh and both mesh
    // reconstructions so the three cannot drift.
    public static void BuildSegmentFrames(
        HairCard card,
        int segments,
        float length,
        float[] segmentT,
        Vector3[] segmentSpine,
        Quaternion[] segmentFrame)
    {
        if (card == null) return;
        if (segments < 1) return;

        ResolveSegmentPositions(card.groupId, segments, segmentT);

        for (int i = 0; i <= segments; i++)
        {
            float t = segmentT[i];
            segmentFrame[i] = card.GetLengthProfileRotation(t);
            segmentSpine[i] = segmentFrame[i] * new Vector3(0f, 0f, t * length);
        }

        if (!BendFollowsPath) return;

        // Second pass, because the tangent at a row needs its neighbours' spine
        // points. Central difference inside, one-sided at the ends.
        for (int i = 0; i <= segments; i++)
        {
            Vector3 tangent;
            if (i == 0)
            {
                tangent = segmentSpine[1] - segmentSpine[0];
            }
            else if (i == segments)
            {
                tangent = segmentSpine[segments] - segmentSpine[segments - 1];
            }
            else
            {
                tangent = segmentSpine[i + 1] - segmentSpine[i - 1];
            }

            // Duplicate t values (a flat stretch in the density curve) leave two
            // spine points on top of each other and no usable direction. Leave that
            // row on its authored rotation rather than inventing one.
            if (tangent.sqrMagnitude < 1e-12f) continue;

            Vector3 forward = segmentFrame[i] * Vector3.forward;
            Vector3 direction = tangent.normalized;

            // A near-180-degree flip has no well-defined minimal rotation - Unity
            // picks an arbitrary perpendicular axis and the section would cartwheel.
            // Leave those rows on their authored rotation.
            if (Vector3.Dot(forward, direction) < -.999f) continue;

            segmentFrame[i] = Quaternion.FromToRotation(forward, direction) * segmentFrame[i];
        }
    }

    [Header("Grooming Parameters")]
    public float width = 0.01f;
    public float length = 0.2f;
    [Range(1, 60)] public int segments = 12;

    [Header("Deformations")]
    public float bendAngle = 0f;
    public float twistAngle = 0f;
    public float flattenFactor = 1f;
    // Curl (spiral/coil): frequency = full turns from root to tip, diameter = coil width.
    // Applied after width, before bend, in the shape pipeline (see GenerateMesh).
    public float curlFrequency = 0f;
    public float curlDiameter = 0f;

    // WAVE: a planar sinusoid that snakes the card side to side within its own flat plane -
    // amplitude in local X, phase advancing with t along the length. Deliberately NOT a coil:
    // Curl already displaces in both X and Y and sweeps around the length axis, so the two
    // compose rather than duplicate. Zero amplitude is off, which is what every project saved
    // before this feature deserializes to.
    public float waveAmplitude = 0f;
    public float waveFrequency = 0f;

    // 0 = side to side across the card's flat plane (the old <> behaviour), 1 = up and down
    // across its face, anything between is a diagonal. Held as an ANGLE internally rather than
    // a lerp between two axis vectors: lerping (1,0,0) toward (0,1,0) passes through a vector
    // of length 0.707 at the midpoint, so a diagonal wave would visibly lose almost a third of
    // its amplitude. An angle keeps the axis unit length at every setting, so the Amplitude
    // slider means the same thing wherever this is parked.
    //
    // Defaults to 1 (up/down), NOT 0. A project saved by the first wave build has no
    // waveDirection key at all, so it deserializes to this initializer and comes back up/down
    // rather than side to side. Set the slider to 0 for the previous look.
    public float waveDirection = 1f;

    // ARCH: how pronounced the cross-section's convex profile is.
    //
    // 0.5 is neutral and reproduces exactly the shape the tool had before this existed - the
    // ridge sitting at CrossSectionRidgeRatio of the width. 0 flattens the card to a plain
    // ribbon, 1 doubles the arch. Centred rather than starting at zero on purpose, so the
    // slider has headroom in BOTH directions from the look you already had.
    //
    // Unlike Curl and Wave this is a ControlState channel rather than a root-only one, which
    // is what lets a POST affector drive it locally with spatial falloff.
    public float arch = ArchNeutral;

    [Header("UV Settings")]
    public float uScale = 1.0f;
    public float vScale = 1.0f;
    public float uOffset = 0.0f;
    public float vOffset = 0.0f;

    [Header("Grouping")]
    public int groupId = 0;

    // SYMMETRY.
    //
    // A mirrored card is a NORMAL card whose geometry is evaluated through a local-X mirror.
    // Nothing about the mirror is baked into its stored numbers: length, width, bend, twist,
    // the three angle offsets and the curl values are all stored exactly as its partner's.
    // The negation happens at evaluation time, here in HairCard.
    //
    // That is the whole point. ModelViewer's sliders push ABSOLUTE values to every card in the
    // group (ApplyGroupUpdate), so a card that merely had its twist negated at placement time
    // would be flattened back to its partner's value the first time any slider moved, and the
    // two sides would silently drift into being identical rather than mirrored. Because the
    // mirror is a property of the CARD and not of its numbers, group sliders, POSTs, clumpers
    // and variance all carry on working untouched and the pair stays symmetric forever.
    //
    // The maths: mirroring the card's local X axis is the conjugation v -> S v S with
    // S = diag(-1, 1, 1). Under it, rotations about local X keep their sign while rotations
    // about local Y and Z flip. So: offsetX and bendAngle are UNCHANGED, offsetY, offsetZ and
    // twistAngle are NEGATED, and the curl coil reverses its handedness.
    public bool mirrored = false;

    [Header("Selection State")]
    [Range(0f, 1f)] public float selectionWeight = 0f;

    private MeshFilter meshFilter;

    // The MeshRenderer, resolved once. HairIdleOverlayAuthority touches every card's renderer
    // when the translucent visualisation pass goes on or off, and at forty thousand cards a
    // GetComponent per card IS the cost of that transition. Named CardRenderer rather than
    // Renderer so it cannot shadow the UnityEngine.Renderer type inside this class.
    private MeshRenderer cardRenderer;

    public MeshRenderer CardRenderer
    {
        get
        {
            if (cardRenderer == null) cardRenderer = GetComponent<MeshRenderer>();
            return cardRenderer;
        }
    }

    // Owned by HairIdleOverlayAuthority: true while this card's MeshRenderer carries the
    // translucent overlay material in a second slot. Kept here rather than in a dictionary on
    // that authority so a destroyed card takes its own flag with it, and so the sweep can skip
    // an already-correct card without a single native call.
    [System.NonSerialized] public bool idleOverlayApplied;

    private Mesh mesh;
    private Vector3[] baseVertices;
    private Vector3 spawnHitPoint;
    private Vector3 surfaceNormal;

    // Where this card's RANDOMISATION is anchored, which is not always where the card is.
    //
    // Four hash sites key per-card randomness to the spawn point, two of them to the surface
    // normal as well, and all of them round to a ten-thousandth: group variance
    // (GroomVarianceController.SignedRandom), POST-local variance
    // (PostVarianceAffectorBridge.SignedRandom), POST coverage
    // (PostPredeterminedUVAuthority.StablePostThreshold) and the predetermined-UV pick in both
    // PostPredeterminedUVAuthority and GroupPredeterminedUVController. Move a root a tenth of a
    // millimetre and that card re-rolls its variance and its atlas rectangle.
    //
    // Nothing in ordinary use moves a placed root, so these track the spawn point exactly and the
    // behaviour is unchanged. They exist for the operations that DO move a whole groom - the
    // import rescale, and REMAP onto a different head - which freeze identity first and then move
    // the anchor, so the groom keeps the randomisation it was authored with.
    //
    // Initialised here rather than tested for later: an unfrozen card's identity IS its placement,
    // so there is no state where these are meaningfully absent.
    private Vector3 identityPoint = Vector3.zero;
    private Vector3 identityNormal = Vector3.up;
    private bool identityFrozen = false;

    // How much the card's LENGTHS have been scaled since identity was frozen.
    //
    // ClumperDeterministicLeaderAuthority.CardStableKey is a fifth deterministic site and the odd
    // one out: it quantises the authored length, width and embed depth alongside the point and
    // normal, so an operation that rescales a groom would re-pick every clump leader even with
    // the anchor identity held still. Dividing those three back out by this factor is what keeps
    // a rescale invisible. It stays 1 for a remap, which moves anchors without touching lengths,
    // and an ordinary shape edit still changes the key exactly as it always did.
    private float identityScale = 1f;
    private float currentEmbedDepth = 0.01f;
    private float storedOffsetX, storedOffsetY, storedOffsetZ;
    private float baseLength, baseWidth, baseBend, baseTwist, baseEmbedDepth;
    private float baseCurlFrequency, baseCurlDiameter;
    private float baseWaveAmplitude, baseWaveFrequency, baseWaveDirection;
    private float baseArch;
    private int baseSegments;
    private float baseOffsetX, baseOffsetY, baseOffsetZ;
    private Material cardMaterial;

    private GroomState canonicalState;
    private bool hasCanonicalState;
    private readonly List<PostShapeProfileContribution> postShapeProfileContributions = new List<PostShapeProfileContribution>();

    private bool clumpActive;
    private Vector3 clumpSurfacePoint;
    private Vector3 clumpSurfaceNormal;
    private float clumpStrength;
    private AnimationCurve clumpCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    // The native CLUMPER stage writes a derived mesh after the normal card mesh has been built.
    // Keep a compact signature of that normal source mesh. If another authority asks us to
    // regenerate exactly the same source while a CLUMPER override is active, do not overwrite
    // the already-clumped mesh. A genuine source change clears the override automatically.
    private int generatedMeshSignature;
    private bool externalClumpOverrideActive;
    private int externalClumpSourceSignature;

    // ---- Mesh input dirty-check ------------------------------------------------------
    //
    // GenerateMesh is a pure function of a known set of inputs, but it was being called 2-3
    // times per card per frame by authorities that re-assert state unconditionally, and it
    // had no idea whether anything had actually moved. Each call allocates six arrays,
    // evaluates ~144 AnimationCurve samples and re-uploads the mesh. In the default state -
    // no POSTs authored anywhere - that was the entire scene, rebuilt from scratch, every
    // frame, producing byte-identical geometry.
    //
    // WHY THIS IS NOT THE "FROZEN MESH" BUG AGAIN:
    //
    // The check is NOT inside GenerateMesh(). GenerateMesh() still writes unconditionally
    // every single time it is called. Only GenerateMeshIfInputsChanged() consults the hash,
    // and only the per-frame re-assertion paths (ApplyEvaluatedState, SetParameters) call it.
    // Every path that MUST write - ThreeColumnClumperMeshAuthority.RestoreRemovedGroups,
    // GroupClumperManager.RemoveClumper, GroomShapeCurveRegistry.RefreshGroup,
    // PostPredeterminedUVAuthority - calls GenerateMesh() directly and is completely unaffected.
    //
    // PostShapeCurveBridge used to be on that list and no longer is. Its per-frame pass attaches
    // POST profile provenance to every card inside the radius, which is a re-assertion rather
    // than a must-write: the values are identical frame after frame until the user moves
    // something. Rebuilding them unconditionally was most of the cost of having a POST on screen
    // at all, and the hash covers every input on that path.
    //
    // On top of that there are three independent escape hatches, any one of which forces a
    // real rebuild even when the hash matches: the foreignMeshWrite flag, the shared curve
    // epochs, and the meshFilter.sharedMesh identity check.
    private int lastMeshInputHash;
    private bool hasMeshInputHash;

    // Set whenever some other authority has written OUR Mesh behind our back. Sticky on
    // purpose: the card's own inputs do not change when the CLUMPER stamps its derived
    // geometry over the top, so without this the next guarded call would compare equal, skip,
    // and leave the foreign mesh on screen permanently with no error anywhere. Cleared only
    // by a real GenerateMesh pass, once our own geometry is genuinely back in the Mesh.
    private bool foreignMeshWrite;

    public void MarkForeignMeshWrite()
    {
        foreignMeshWrite = true;
    }

    public float GetEmbedDepth() { return currentEmbedDepth; }
    public float GetOffsetX() { return storedOffsetX; }
    public float GetOffsetY() { return storedOffsetY; }
    public float GetOffsetZ() { return storedOffsetZ; }
    public Vector3 GetSpawnHitPoint() { return spawnHitPoint; }
    public Vector3 GetSurfaceNormal() { return surfaceNormal; }

    // The pair every deterministic per-card hash reads. Identical to the spawn point and surface
    // normal unless something has frozen identity - see the fields.
    public Vector3 GetIdentityPoint() { return identityPoint; }
    public Vector3 GetIdentityNormal() { return identityNormal; }
    public bool HasFrozenIdentity() { return identityFrozen; }
    public float GetIdentityScale() { return identityScale; }

    // Pin randomisation to a point and normal, and stop it following the anchor.
    //
    // Call this BEFORE SetPlacementData when restoring or remapping a card: placement stamps
    // identity from itself while identity is unfrozen, so the other order loses the very values
    // this is preserving.
    public void SetIdentity(Vector3 point, Vector3 normal, float lengthScaleSinceFrozen)
    {
        identityPoint = point;
        identityNormal = normal;
        identityFrozen = true;
        if (lengthScaleSinceFrozen > .000001f)
        {
            identityScale = lengthScaleSinceFrozen;
        }
    }
    public float GetCrossSectionRidgeHeight() { return Mathf.Max(.0005f, width) * flattenFactor * CrossSectionRidgeRatio; }
    public int GetGeneratedMeshSignature() { return generatedMeshSignature; }

    // The Mesh this card owns and writes into. Any other authority that needs to write a
    // derived mesh for this card MUST go through here and never through MeshFilter.mesh.
    //
    // MeshFilter.mesh is Unity's INSTANTIATING getter: reading it duplicates the mesh and
    // leaves the duplicate on the filter. HairCard keeps its own reference to the original,
    // so a single read permanently divorces the two. After that, GenerateMesh keeps running,
    // keeps computing correct geometry, and keeps writing it into a mesh nobody renders -
    // the card is frozen on screen for the rest of the session with no error anywhere.
    //
    // This also self-heals: if the filter has already drifted onto a duplicate (a project
    // loaded by an older build, or any future stray MeshFilter.mesh read), point it back at
    // the mesh this card actually maintains.
    public Mesh GetLiveMesh()
    {
        if (mesh == null) return null;
        if (meshFilter == null) meshFilter = GetComponent<MeshFilter>();
        if (meshFilter != null && meshFilter.sharedMesh != mesh) meshFilter.sharedMesh = mesh;

        // Handing out a writable handle to our Mesh IS the foreign write, as far as the
        // dirty-check is concerned. Marking here rather than at each call site means a future
        // authority that writes this mesh cannot forget to declare it - which is the single
        // mistake that would produce a silently frozen card. HairCard's own code never comes
        // through here (it uses the `mesh` field directly), and the only cost of a read-only
        // caller tripping this is one extra rebuild, so it fails toward correctness.
        foreignMeshWrite = true;
        return mesh;
    }

    public void MarkExternalClumpOverride()
    {
        externalClumpOverrideActive = true;
        externalClumpSourceSignature = generatedMeshSignature;
        // The CLUMPER has just written its derived geometry into our Mesh. None of our own
        // inputs moved, so the dirty-check has to be told explicitly that what is on screen is
        // no longer what we last generated.
        foreignMeshWrite = true;
    }

    public void ClearExternalClumpOverride()
    {
        externalClumpOverrideActive = false;
    }

    // Whether this card is still rendering a CLUMPER-derived mesh.
    //
    // ThreeColumnClumperMeshAuthority needs this in its dirty-check. Its signature is built from
    // SOURCE state only, so it cannot see that a card has been re-generated back to clean
    // geometry and had its override dropped. When that happens on a frame whose source signature
    // is unchanged, the clumper skips and the card renders unclumped for that frame.
    public bool HasExternalClumpOverride()
    {
        return externalClumpOverrideActive;
    }

    public void ClearPostShapeProfileContributions()
    {
        postShapeProfileContributions.Clear();
    }

    public void AddPostShapeProfileContribution(int postId, float bend, float x, float y, float z)
    {
        if (Mathf.Abs(bend) + Mathf.Abs(x) + Mathf.Abs(y) + Mathf.Abs(z) <= .000001f) return;
        postShapeProfileContributions.Add(new PostShapeProfileContribution
        {
            postId = postId,
            bend = bend,
            x = x,
            y = y,
            z = z
        });
    }

    // Local per-row rotation which, after the GameObject's existing full X/Y/Z transform,
    // yields the requested root-to-tip angle profile. The scalar result is still the normal
    // canonical + POST evaluation. Each POST contribution then replaces only its share of the
    // group profile with that POST's own profile, preserving additive/spatial weighting exactly.
    public Quaternion GetLengthProfileRotation(float t)
    {
        t = Mathf.Clamp01(t);
        // A card with no bend, no twist, no angle offsets and no POST profile contributions
        // has an identity profile rotation at every t, and the four curve evaluations below
        // cannot change that - a multiplier only ever scales zero. Four evaluations per row
        // per card saved on every straight card in the scene.
        //
        // Deliberately an exact zero test on the SCALARS rather than a flat-curve test: these
        // four channels have per-POST overrides and route through the POST snapshot, so the
        // registry's flat-curve answer would not be the whole story. Zero times anything is.
        if (bendAngle == 0f && twistAngle == 0f
            && storedOffsetX == 0f && storedOffsetY == 0f && storedOffsetZ == 0f
            && postShapeProfileContributions.Count == 0)
            return Quaternion.identity;

        float bendMultiplier = PostShapeCurveBridge.EvaluateRoot(groupId, GroomShapeCurveChannel.Bend, t);
        float xMultiplier = PostShapeCurveBridge.EvaluateRoot(groupId, GroomShapeCurveChannel.X, t);
        float yMultiplier = PostShapeCurveBridge.EvaluateRoot(groupId, GroomShapeCurveChannel.Y, t);
        float zMultiplier = PostShapeCurveBridge.EvaluateRoot(groupId, GroomShapeCurveChannel.Z, t);

        float profiledBend = bendAngle * bendMultiplier;
        float profiledX = storedOffsetX * xMultiplier;
        float profiledY = storedOffsetY * yMultiplier;
        float profiledZ = storedOffsetZ * zMultiplier;

        foreach (PostShapeProfileContribution contribution in postShapeProfileContributions)
        {
            profiledBend += contribution.bend *
                (PostShapeCurveBridge.EvaluatePost(contribution.postId, GroomShapeCurveChannel.Bend, t) - bendMultiplier);
            profiledX += contribution.x *
                (PostShapeCurveBridge.EvaluatePost(contribution.postId, GroomShapeCurveChannel.X, t) - xMultiplier);
            profiledY += contribution.y *
                (PostShapeCurveBridge.EvaluatePost(contribution.postId, GroomShapeCurveChannel.Y, t) - yMultiplier);
            profiledZ += contribution.z *
                (PostShapeCurveBridge.EvaluatePost(contribution.postId, GroomShapeCurveChannel.Z, t) - zMultiplier);
            // No sign flip needed here: profiledY and profiledZ are handed to MirroredEuler
            // below, which negates the accumulated total. Flipping the contributions as well
            // would cancel it back out.
        }

        // Mirroring the whole profile chain, term by term. Every rotation here is built from
        // Euler triples whose X component is a rotation about local X (sign preserved under the
        // mirror) and whose Y and Z components rotate about local Y and Z (sign flipped). Twist
        // is a roll about local Z, so it flips too; bend is about local X, so it does not.
        //
        // Doing it per-term rather than conjugating the finished quaternion matters, because the
        // profile curves scale each component independently along the length - a mirror applied
        // after profiling would not be the profile of the mirror.
        Quaternion fullOffset = MirroredEuler(storedOffsetX, storedOffsetY, storedOffsetZ);
        Quaternion curvedOffset = MirroredEuler(profiledX, profiledY, profiledZ);

        float mirroredTwist = twistAngle;
        if (mirrored) mirroredTwist = -twistAngle;
        Quaternion bendAndTwist = Quaternion.Euler(profiledBend, 0f, mirroredTwist * t);

        return Quaternion.Inverse(fullOffset) * curvedOffset * bendAndTwist;
    }

    public GroomState GetCanonicalState()
    {
        if (!hasCanonicalState)
        {
            canonicalState = ReadRenderedState();
            hasCanonicalState = true;
        }
        return canonicalState;
    }

    public void SetCanonicalState(GroomState state, bool applyToRendered = false)
    {
        canonicalState = SanitizeState(state);
        hasCanonicalState = true;
        if (applyToRendered) ApplyEvaluatedState(canonicalState);
    }

    // regenerateMesh:false is for a caller that is about to write the card AGAIN in the same
    // breath - PostAffectorManager.PrepareCardForRootEdit, which puts the base back on the card
    // purely so the group edit landing immediately afterwards reads the base rather than
    // base + POST. Rebuilding the mesh for a state that exists for the length of one method call
    // is a full array allocation and ~144 curve samples per card, per card, per drag frame,
    // thrown away before anything can see it. Every other caller leaves it true.
    public void ApplyEvaluatedState(GroomState state, bool regenerateMesh = true)
    {
        state = SanitizeState(state);
        length = state.length;
        width = state.width;
        segments = state.segments;
        bendAngle = state.bend;
        twistAngle = state.twist;
        storedOffsetX = state.x;
        storedOffsetY = state.y;
        storedOffsetZ = state.z;
        currentEmbedDepth = state.depth;
        uScale = state.uScale;
        vScale = state.vScale;
        uOffset = state.uOffset;
        vOffset = state.vOffset;
        curlFrequency = state.curlFrequency;
        curlDiameter = state.curlDiameter;
        waveAmplitude = state.waveAmplitude;
        waveFrequency = state.waveFrequency;
        waveDirection = state.waveDirection;
        arch = state.arch;
        if (surfaceNormal != Vector3.zero) UpdateTransformOrientation(currentEmbedDepth);

        // Guarded. PostAffectorManager, PostFreeCanonicalAuthority and
        // PostVarianceAffectorBridge all re-assert state through here every single frame,
        // overwhelmingly with values identical to the ones already on the card. The field
        // writes and the transform update above still run unconditionally - only the mesh
        // rebuild is skipped, and only when nothing that feeds it has moved.
        if (regenerateMesh) GenerateMeshIfInputsChanged();
    }

    GroomState ReadRenderedState()
    {
        return new GroomState
        {
            length = length,
            width = width,
            segments = segments,
            bend = bendAngle,
            twist = twistAngle,
            depth = currentEmbedDepth,
            x = storedOffsetX,
            y = storedOffsetY,
            z = storedOffsetZ,
            uScale = uScale,
            vScale = vScale,
            uOffset = uOffset,
            vOffset = vOffset,
            curlFrequency = curlFrequency,
            curlDiameter = curlDiameter,
            waveAmplitude = waveAmplitude,
            waveFrequency = waveFrequency,
            waveDirection = waveDirection,
            arch = arch
        };
    }

    GroomState SanitizeState(GroomState state)
    {
        state.length = Mathf.Max(0.0001f, state.length);
        state.width = Mathf.Max(0.0005f, state.width);
        state.segments = Mathf.Clamp(state.segments, 1, 60);
        state.depth = Mathf.Max(0f, state.depth);
        state.curlDiameter = Mathf.Max(0f, state.curlDiameter);
        // Amplitude is a magnitude, so it clamps like curl diameter. Frequency stays signed -
        // a negative frequency simply runs the wave the other way, which is a usable result.
        state.waveAmplitude = Mathf.Max(0f, state.waveAmplitude);
        state.waveDirection = Mathf.Clamp01(state.waveDirection);
        // No upper bound - a POST may push arch past the slider's range, the same way it can
        // push Bend past 360. Negative is refused because an inverted arch is what the N- form
        // flip is for, and two controls fighting over one inversion makes neither predictable.
        state.arch = Mathf.Max(0f, state.arch);
        return state;
    }

    void CaptureCanonicalFromRendered()
    {
        canonicalState = SanitizeState(ReadRenderedState());
        hasCanonicalState = true;
    }

    public void SetClumpModifier(Vector3 surfacePoint, Vector3 normal, float strength, AnimationCurve curve)
    {
        clumpSurfacePoint = surfacePoint;
        clumpSurfaceNormal = normal.sqrMagnitude > 0f ? normal.normalized : surfaceNormal.normalized;
        clumpStrength = Mathf.Clamp01(strength);
        clumpCurve = curve ?? AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
        clumpActive = clumpStrength > 0.0001f;
        GenerateMesh();
    }

    public void ClearClumpModifier()
    {
        // PostFreeCanonicalAuthority calls this on every POST-free card every frame - which,
        // with no POSTs authored anywhere, is the whole scene - and it used to force a full
        // rebuild whether or not there was anything to clear. There virtually never is:
        // SetClumpModifier, the only thing that can set clumpActive, has no callers left in
        // the project. So this was a scene-wide mesh rebuild, every frame, to switch off
        // something that was already off.
        bool alreadyClear = !clumpActive && clumpStrength == 0f;
        clumpActive = false;
        clumpStrength = 0f;
        if (alreadyClear) return;
        GenerateMesh();
    }

    public void SetPlacementData(Vector3 hitPoint, Vector3 normal, float embedDepth, float offsetX, float offsetY, float offsetZ, int assignedGroupId)
    {
        spawnHitPoint = hitPoint;
        surfaceNormal = normal;

        // An ordinary placement is its own identity. A card whose identity has been frozen keeps
        // the one it was given, which is what lets a rescale or a remap move the anchor without
        // re-rolling the card's variance and predetermined UV rectangle.
        if (!identityFrozen)
        {
            identityPoint = hitPoint;
            identityNormal = normal;
        }

        currentEmbedDepth = embedDepth;
        storedOffsetX = offsetX;
        storedOffsetY = offsetY;
        storedOffsetZ = offsetZ;
        groupId = assignedGroupId;
        UpdateTransformOrientation(currentEmbedDepth);
        CaptureCanonicalFromRendered();
    }

    public void UpdateDepth(float embedDepth)
    {
        currentEmbedDepth = embedDepth;
        UpdateTransformOrientation(currentEmbedDepth);
        CaptureCanonicalFromRendered();
    }

    private void UpdateTransformOrientation(float embedDepth)
    {
        Vector3 wantPosition = spawnHitPoint - (surfaceNormal * embedDepth);
        Quaternion wantRotation = Quaternion.LookRotation(surfaceNormal) * MirroredEuler(storedOffsetX, storedOffsetY, storedOffsetZ);

        // WRITTEN ONLY WHEN IT CHANGED, and the guard is worth far more than the two compares
        // it costs.
        //
        // This is reached from ApplyEvaluatedState, which the POST authorities re-assert over
        // every card every frame - so with no POSTs at all, and nothing moving, forty thousand
        // cards were each having their position and rotation reassigned to the values they
        // already held, twice per frame. A transform write is not free even when the value is
        // identical: it dirties the transform, which forces Unity to recompute the world matrix
        // AND the MeshRenderer's world bounds, which invalidates the culling data the camera
        // then has to walk again. That is why it was felt hardest while merely NAVIGATING, when
        // by rights nothing should have been happening at all.
        //
        // Unity's == on Vector3 and Quaternion is APPROXIMATE, not bitwise - about ten microns
        // of distance and about a tenth of a degree of angle. That is the right behaviour here
        // and it cannot drift, because each frame compares against the freshly derived TARGET
        // rather than against the last thing written: a card is never more than one threshold
        // away from where it should be, and the moment the target moves further than that it is
        // written. An unchanged card reproduces its value bit for bit anyway, since it is the
        // same arithmetic over the same inputs - the tolerance only matters for a move so slow
        // it has not yet amounted to ten microns.
        if (transform.position != wantPosition) transform.position = wantPosition;

        // surfaceNormal is ALREADY the mirrored normal for a mirrored card - the mirror of the
        // placement is done once, at spawn. What is left to do here is the mirror of the card's
        // own body, which is the S-conjugation of the authored angle triple.
        //
        // This is exact, not an approximation. LookRotation(M n) == M * LookRotation(n) * S for
        // the world-X mirror M (both have forward M n, and world up is unchanged by M so both
        // derive the same up), and Euler(ox, -oy, -oz) == S * Euler(ox, oy, oz) * S. Composing
        // the two gives M * R * S, which is exactly the proper rotation whose local X axis is
        // the reflection of the original's, i.e. a true mirror rather than a rotation.
        if (transform.rotation != wantRotation) transform.rotation = wantRotation;
    }

    // Euler(x, y, z) for a normal card; Euler(x, -y, -z) for a mirrored one.
    private Quaternion MirroredEuler(float x, float y, float z)
    {
        if (!mirrored) return Quaternion.Euler(x, y, z);
        return Quaternion.Euler(x, -y, -z);
    }

    public void UpdateVisualHighlight()
    {
        Color finalColor = Color.Lerp(Color.yellow, Color.white, selectionWeight);
        if (selectionWeight <= .0001f)
        {
            RevertToSharedMaterialIfPossible();
            if (cardMaterial == null) return;
        }
        else
        {
            EnsurePerInstanceMaterial();
        }
        if (cardMaterial == null) return;
        if (cardMaterial.HasProperty("_BaseColor")) cardMaterial.SetColor("_BaseColor", finalColor);
        if (cardMaterial.HasProperty("_Color")) cardMaterial.SetColor("_Color", finalColor);
    }

    public void SetParameters(float newLength, float newWidth, int newSegments, float newBend, float newTwist, float offsetX, float offsetY, float offsetZ, float newEmbedDepth, float strengthMultiplier = 1f, float newUScale = 1f, float newVScale = 1f, float newUOffset = 0f, float newVOffset = 0f, float newCurlFrequency = 0f, float newCurlDiameter = 0f, float newWaveAmplitude = 0f, float newWaveFrequency = 0f, float newWaveDirection = 1f, float newArch = ArchNeutral)
    {
        if (selectionWeight > 0f)
        {
            float w = Mathf.Clamp01(selectionWeight * strengthMultiplier);
            length = Mathf.Max(0.0001f, Mathf.Lerp(baseLength, newLength, w));
            width = Mathf.Lerp(baseWidth, newWidth, w);
            segments = Mathf.RoundToInt(Mathf.Lerp(baseSegments, newSegments, w));
            bendAngle = Mathf.Lerp(baseBend, newBend, w);
            twistAngle = Mathf.Lerp(baseTwist, newTwist, w);
            storedOffsetX = Mathf.Lerp(baseOffsetX, offsetX, w);
            storedOffsetY = Mathf.Lerp(baseOffsetY, offsetY, w);
            storedOffsetZ = Mathf.Lerp(baseOffsetZ, offsetZ, w);
            currentEmbedDepth = Mathf.Lerp(baseEmbedDepth, newEmbedDepth, w);
            curlFrequency = Mathf.Lerp(baseCurlFrequency, newCurlFrequency, w);
            curlDiameter = Mathf.Lerp(baseCurlDiameter, newCurlDiameter, w);
            waveAmplitude = Mathf.Lerp(baseWaveAmplitude, newWaveAmplitude, w);
            waveFrequency = Mathf.Lerp(baseWaveFrequency, newWaveFrequency, w);
            waveDirection = Mathf.Lerp(baseWaveDirection, newWaveDirection, w);
            arch = Mathf.Lerp(baseArch, newArch, w);
        }
        else
        {
            length = Mathf.Max(0.0001f, newLength);
            width = newWidth;
            segments = newSegments;
            bendAngle = newBend;
            twistAngle = newTwist;
            storedOffsetX = offsetX;
            storedOffsetY = offsetY;
            storedOffsetZ = offsetZ;
            currentEmbedDepth = newEmbedDepth;
            curlFrequency = newCurlFrequency;
            curlDiameter = newCurlDiameter;
            waveAmplitude = newWaveAmplitude;
            waveFrequency = newWaveFrequency;
            waveDirection = newWaveDirection;
            arch = newArch;
        }
        uScale = newUScale;
        vScale = newVScale;
        uOffset = newUOffset;
        vOffset = newVOffset;
        if (surfaceNormal != Vector3.zero) UpdateTransformOrientation(currentEmbedDepth);
        CaptureCanonicalFromRendered();

        // Guarded. CaptureCanonicalFromRendered above still runs unconditionally, so the
        // canonical bookkeeping is untouched - only the rebuild is skipped, and only when the
        // incoming values are the ones the card already has.
        //
        // This is also what fixes SelectionLocalizedEditAuthority, which deliberately rewrites
        // every selected card's snapshot state EVERY LateUpdate as a restore mechanism against
        // lower-order authorities clobbering the group. That restore has to keep happening -
        // gating it on its own "changed" flags reintroduces the group-leak bug it exists to
        // prevent - but in the steady state it was rewriting values identical to what was
        // already there and paying a full mesh rebuild per card per frame for the privilege.
        GenerateMeshIfInputsChanged();
    }

    public void CaptureBaseState(float activeLength, float activeWidth, int activeSegments, float activeBend, float activeTwist, float activeDepth, float ox, float oy, float oz, float activeCurlFrequency = 0f, float activeCurlDiameter = 0f, float activeWaveAmplitude = 0f, float activeWaveFrequency = 0f, float activeWaveDirection = 1f, float activeArch = ArchNeutral)
    {
        baseLength = activeLength;
        baseWidth = activeWidth;
        baseSegments = activeSegments;
        baseBend = activeBend;
        baseTwist = activeTwist;
        baseEmbedDepth = activeDepth;
        baseOffsetX = ox;
        baseOffsetY = oy;
        baseOffsetZ = oz;
        baseCurlFrequency = activeCurlFrequency;
        baseCurlDiameter = activeCurlDiameter;
        baseWaveAmplitude = activeWaveAmplitude;
        baseWaveFrequency = activeWaveFrequency;
        baseWaveDirection = activeWaveDirection;
        baseArch = activeArch;
    }

    public void SetSelectionWeight(float weight) { selectionWeight = Mathf.Clamp01(weight); UpdateVisualHighlight(); }

    // The same assignment without the highlight pass, for a caller that is about to put the
    // weight straight back.
    //
    // UpdateVisualHighlight is not a cheap no-op at zero: it takes the revert branch, which
    // DESTROYS this card's material instance, and restoring the weight then allocates a new
    // one. A caller that drops the weight to zero purely to bypass SetParameters' internal
    // interpolation therefore destroys and re-creates a Unity Material for every card under
    // its brush, every frame - hundreds of native allocations a second to change nothing that
    // is ever drawn, since both writes land inside the same frame.
    public void SetSelectionWeightSilent(float weight)
    {
        selectionWeight = Mathf.Clamp01(weight);
    }

    // Bumped whenever a hair card enters or leaves the scene.
    //
    // Lets anything that needs to notice a membership change do it with one integer compare
    // instead of a full FindObjectsByType sweep plus LINQ filter every frame. Monotonic on
    // purpose: a count comparison would miss the case where one card is destroyed and another
    // created in the same frame, which happens routinely on re-brush and group reassign.
    private static int registryVersion;

    public static int RegistryVersion
    {
        get { return registryVersion; }
    }

    // EVERY CARD IN THE SCENE, maintained as they are born and die.
    //
    // registryVersion was already here and already had the right idea; what was missing was the
    // list itself, so a dozen authorities went on calling FindObjectsByType<HairCard> to get one.
    // That call walks Unity's whole object registry and allocates a fresh array every time. At
    // forty thousand cards that is a 320 KB allocation, and in the steady state - camera moving,
    // nothing edited - five of them happened per frame plus another seven per second on timers.
    // Roughly two megabytes of garbage per frame to re-derive a list that never changed.
    //
    // Readers get it as IReadOnlyList so nobody can quietly hold a mutable reference to the
    // scene's card list, and must still null-check: Unity defers Destroy to end of frame, so a
    // card removed this frame is null-but-present until OnDestroy runs.
    private static readonly List<HairCard> all = new List<HairCard>();

    // Where this card sits in `all`, so OnDestroy can pull it out without searching. -1 means
    // not registered - either never Awake'd or already removed.
    private int registryIndex = -1;

    // Bumped every time ANY card writes its mesh.
    //
    // The polygon readouts are the reason. Two of them existed - the panel counter and the group
    // headers - and each independently swept every card, fetched its MeshFilter and asked the
    // Mesh for its index count, seventeen times a second between them, to recompute a number
    // that only changes when geometry is rebuilt. While merely navigating, nothing is rebuilt
    // and both answers were already correct.
    //
    // Monotonic rather than a flag, for the same reason RegistryVersion is: a reader compares
    // the value it last saw and cannot miss a change that happened between its own scans.
    private static int meshGeneration;

    public static int MeshGeneration
    {
        get { return meshGeneration; }
    }

    public static IReadOnlyList<HairCard> All
    {
        get { return all; }
    }

    // Statics survive "Enter Play Mode -> Disable Domain Reload", which this project has on, so
    // without this the list starts the second Play session holding forty thousand destroyed
    // cards from the first.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetRegistry()
    {
        all.Clear();
        registryVersion = 0;
        meshGeneration = 0;
    }

    void Awake()
    {
        unchecked { registryVersion++; }
        registryIndex = all.Count;
        all.Add(this);
        meshFilter = GetComponent<MeshFilter>();
        mesh = new Mesh { name = "ProceduralHairCard" };
        meshFilter.mesh = mesh;

        // After the mesh exists, because MarkDynamic is one of the things it sets.
        ConfigureRenderer();

        SetupMaterial();
        GenerateMesh();
        UpdateVisualHighlight();
        CaptureCanonicalFromRendered();
    }

    // Per-renderer settings that are wrong by DEFAULT for forty thousand of anything.
    //
    // Done here rather than at the three spawn sites, so a fourth one cannot be added without
    // them. Costs one GetComponent at birth and nothing afterwards.
    //
    // LIGHT AND REFLECTION PROBES. Unity's default is BlendProbes, which makes it interpolate
    // probe data per renderer per frame on the CPU - forty thousand times, every frame, whether
    // or not the scene has any probes. This one does not have any: it is a runtime tool that
    // loads whatever head the user hands it, so there is nothing baked to interpolate and
    // BlendProbes falls back to the ambient probe regardless. Off produces the same pixels and
    // skips the work. The guide and ring previews already do exactly this for their own
    // renderers - see GuideCurvePreviewAuthority - so the pattern is the project's own.
    //
    // SHADOWS are deliberately NOT touched here. The hair shader has a real ShadowCaster pass
    // and forty thousand casters against two cascades is the single largest remaining cost in
    // the frame - but hair that neither casts onto the head nor receives from it is a visibly
    // different groom, and that is a decision about how the tool LOOKS. It is not mine to make
    // silently while being asked to make it faster.
    void ConfigureRenderer()
    {
        MeshRenderer mr = GetComponent<MeshRenderer>();
        if (mr == null) return;

        mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

        // The card's mesh is rewritten whenever a slider moves, a POST is dragged or a guide is
        // combed, which is the definition of dynamic. Tells Unity to keep it in memory it can
        // update cheaply rather than re-optimising the buffer on every write.
        if (mesh != null) mesh.MarkDynamic();
    }

    void OnValidate() { if (mesh != null) GenerateMesh(); }
    void OnDestroy()
    {
        unchecked { registryVersion++; }

        // Swap the last entry into this one's slot, in constant time, using the index the card
        // has been carrying since Awake.
        //
        // List.Remove would be a linear search plus a shift of everything after it, so deleting
        // a group of ten thousand would be ten thousand scans of a shrinking forty-thousand
        // entry list - quadratic, and felt as a hitch on exactly the operation that should be
        // instant. Holding the index makes it one array write.
        //
        // Order is not meaningful here and nothing may depend on it: FindObjectsByType, which
        // this replaces, never promised one either, and ThreeColumnClumperMeshAuthority sorts
        // explicitly where it genuinely needs stability.
        if (registryIndex >= 0 && registryIndex < all.Count && all[registryIndex] == this)
        {
            int last = all.Count - 1;
            HairCard moved = all[last];
            all[registryIndex] = moved;
            if (moved != null) moved.registryIndex = registryIndex;
            all.RemoveAt(last);
        }
        registryIndex = -1;

        if (cardMaterial != null) Destroy(cardMaterial);
    }
    public void ApplyDeformations() { GenerateMesh(); }

    // HairCard deliberately has NO Update().
    //
    // It used to have one, purely to poll the global 1/2 single-sided/double-sided hotkeys.
    // That is one managed Update callback plus four Input System lookups PER CARD PER FRAME,
    // for a keypress whose answer is identical for every card in the scene - pure overhead
    // that grew linearly with the groom and was paid even while the cards were hidden.
    // The hotkey now lives in exactly one scene-level listener,
    // HairCardSidednessHotkeyAuthority, which does the same broadcast on the one frame the
    // key is actually pressed.
    //
    // Behaviourally this changes nothing: GroupSidednessAuthority.ApplyAll() already
    // rewrote every card's sidedness from its GROUP's setting every 0.1s, so a per-card
    // hotkey write was being overwritten within a tenth of a second regardless.

    // Lets PostShapeCurveBridge tell "this card had no POST profile contributions last frame
    // and has none this frame" apart from "this card genuinely changed", so it can skip a
    // full mesh rebuild instead of rebuilding every card in the scene unconditionally.
    public int PostShapeProfileContributionCount
    {
        get { return postShapeProfileContributions.Count; }
    }

    // MaterialEditorManager.ApplyAssignments() is the single authoritative source for which
    // material every hair card should use by default (ModelViewer.hairCardMaterial), and it
    // now also guarantees that material stays double-sided and GPU-instancing-enabled. Cards
    // therefore just read it directly rather than maintaining an independent cached copy -
    // there is nothing left to keep in sync, so nothing here can go stale, and nothing here
    // competes with that other script over which material is "correct". Cards default to
    // sharing this one reference so the SRP batcher/GPU instancing can actually batch them; a
    // card only gets its own material instance - and only that card loses batching eligibility
    // - the moment it genuinely diverges: a non-zero selection-brush highlight, or an explicit
    // single-sided override. Both are small, usually-transient subsets of the full population.
    private static ModelViewer cachedViewer;
    private bool isDoubleSided = true;

    // Lets MaterialEditorManager's periodic global-material enforcement skip this card instead
    // of erasing a genuine, active divergence (selection highlight or single-sided override)
    // every time it runs.
    public bool HasDivergedMaterial() => cardMaterial != null || !isDoubleSided;

    static Material SharedMaterial()
    {
        if (cachedViewer == null) cachedViewer = FindFirstObjectByType<ModelViewer>();
        return cachedViewer != null ? cachedViewer.hairCardMaterial : null;
    }

    // One single-sided clone per source material, shared by every card that wants it.
    // Cull mode is material state and cannot be overridden per renderer, so a second
    // material is unavoidable - but a whole group set to SS still batches with every
    // other SS card rather than becoming hundreds of unique materials.
    private static readonly Dictionary<Material, Material> singleSidedVariants = new Dictionary<Material, Material>();

    // Now that _Cull is a real shader property, the single-sided clone is a genuine
    // second material and can drift from its source: change a texture or a tint on the
    // base material and the clone keeps the old one. Re-copying every property is cheap
    // (there is normally exactly one clone) and keeps the SS groups looking identical to
    // the DS groups in everything except which faces get drawn.
    public static void RefreshSingleSidedVariants()
    {
        foreach (KeyValuePair<Material, Material> pair in singleSidedVariants)
        {
            Material source = pair.Key;
            Material variant = pair.Value;
            if (source == null || variant == null) continue;

            variant.CopyPropertiesFromMaterial(source);
            if (variant.HasProperty("_Cull")) variant.SetFloat("_Cull", 2f);
            variant.DisableKeyword("_DOUBLESIDED_ON");
            variant.enableInstancing = true;
        }
    }

    static Material SingleSidedVariant(Material source)
    {
        if (source == null) return null;

        Material variant;
        if (singleSidedVariants.TryGetValue(source, out variant) && variant != null) return variant;

        variant = new Material(source) { name = source.name + "_SingleSided" };
        if (variant.HasProperty("_Cull")) variant.SetFloat("_Cull", 2f);
        variant.DisableKeyword("_DOUBLESIDED_ON");
        variant.enableInstancing = true;
        singleSidedVariants[source] = variant;
        return variant;
    }

    // What this card should be rendering with when it has no per-instance divergence.
    Material EffectiveSharedMaterial()
    {
        Material shared = SharedMaterial();
        if (shared == null) return null;
        if (isDoubleSided) return shared;
        return SingleSidedVariant(shared);
    }

    void SetupMaterial()
    {
        MeshRenderer mr = GetComponent<MeshRenderer>();
        if (mr == null) return;
        cardMaterial = null;
        isDoubleSided = true;

        isDoubleSided = !GroupSidednessAuthority.IsSingleSided(groupId);

        Material shared = EffectiveSharedMaterial();
        if (shared != null) { mr.sharedMaterial = shared; return; }

        // No material chosen yet (very early startup, before MaterialEditorManager has run) -
        // a plain per-card placeholder just so the card is visible. MaterialEditorManager
        // assigns the real shared material and enforces double-sided/instancing on it shortly
        // after this runs, at which point new cards pick it up directly and this one gets
        // corrected the next time ApplyAssignments sweeps all cards.
        Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        if (shader == null) return;
        Material fallback = new Material(shader) { name = "HairCardFallback" };
        if (fallback.HasProperty("_BaseColor")) fallback.SetColor("_BaseColor", Color.yellow);
        if (fallback.HasProperty("_Color")) fallback.SetColor("_Color", Color.yellow);
        if (fallback.HasProperty("_Cull")) fallback.SetFloat("_Cull", 0f);
        fallback.EnableKeyword("_DOUBLESIDED_ON");
        mr.sharedMaterial = fallback;
    }

    void EnsurePerInstanceMaterial()
    {
        if (cardMaterial != null) return;
        MeshRenderer mr = GetComponent<MeshRenderer>();
        if (mr == null) return;
        Material shared = EffectiveSharedMaterial() ?? mr.sharedMaterial;
        if (shared == null) return;
        cardMaterial = new Material(shared) { name = "HairCardInstance_" + GetInstanceID() };
        mr.sharedMaterial = cardMaterial;
    }

    // Called whenever this card's state returns to matching the shared default exactly, so it
    // rejoins the batchable pool instead of permanently keeping a now-unnecessary material.
    void RevertToSharedMaterialIfPossible()
    {
        if (cardMaterial == null) return;

        // A selection highlight is the only thing that still needs a material of this
        // card's own. Single-sidedness no longer does - it has a shared variant - so a
        // single-sided card rejoins the batchable pool here just like any other.
        if (selectionWeight > .0001f) return;

        MeshRenderer mr = GetComponent<MeshRenderer>();
        Material shared = EffectiveSharedMaterial();
        if (mr != null && shared != null) mr.sharedMaterial = shared;
        Destroy(cardMaterial);
        cardMaterial = null;
    }

    // Rendering only - no geometry or card state changes with this. Driven per group by
    // GroupSidednessAuthority's SS/DS toggle.
    public void SetDoubleSided(bool enabled)
    {
        isDoubleSided = enabled;

        // A card with a live selection highlight owns its own material anyway, so just set
        // the cull on that and leave the sharing alone until the highlight clears.
        if (cardMaterial != null)
        {
            if (cardMaterial.HasProperty("_Cull"))
            {
                float cull = 2f;
                if (enabled) cull = 0f;
                cardMaterial.SetFloat("_Cull", cull);
            }
            RevertToSharedMaterialIfPossible();
            return;
        }

        MeshRenderer mr = GetComponent<MeshRenderer>();
        if (mr == null) return;

        Material target = EffectiveSharedMaterial();
        if (target == null) return;
        if (mr.sharedMaterial != target) mr.sharedMaterial = target;
    }

    public void SetSegments(int newSegments)
    {
        segments = Mathf.Clamp(newSegments, 1, 60);
        CaptureCanonicalFromRendered();
        GenerateMesh();
    }

    // Every input GenerateMesh reads, folded into one int.
    //
    // Completeness is the whole safety argument. Anything read during a rebuild but missing
    // from here can change without moving the hash, and the card would then render stale
    // geometry silently. If you add a field GenerateMesh consults - or make it consult a new
    // external source - it MUST be added here in the same commit.
    //
    // Deliberately excluded because nothing in the GenerateMesh call tree reads them:
    // selectionWeight, currentEmbedDepth, spawnHitPoint, surfaceNormal, canonicalState,
    // isDoubleSided, cardMaterial and all base* fields.
    int ComputeMeshInputHash()
    {
        unchecked
        {
            int hash = 17;

            hash = hash * 31 + segments;
            hash = hash * 31 + length.GetHashCode();
            hash = hash * 31 + width.GetHashCode();
            hash = hash * 31 + flattenFactor.GetHashCode();
            hash = hash * 31 + bendAngle.GetHashCode();
            hash = hash * 31 + twistAngle.GetHashCode();
            hash = hash * 31 + storedOffsetX.GetHashCode();
            hash = hash * 31 + storedOffsetY.GetHashCode();
            hash = hash * 31 + storedOffsetZ.GetHashCode();
            hash = hash * 31 + curlFrequency.GetHashCode();
            hash = hash * 31 + curlDiameter.GetHashCode();
            hash = hash * 31 + waveAmplitude.GetHashCode();
            hash = hash * 31 + waveFrequency.GetHashCode();
            hash = hash * 31 + waveDirection.GetHashCode();
            hash = hash * 31 + arch.GetHashCode();
            hash = hash * 31 + uScale.GetHashCode();
            hash = hash * 31 + vScale.GetHashCode();
            hash = hash * 31 + uOffset.GetHashCode();
            hash = hash * 31 + vOffset.GetHashCode();

            // groupId routes every curve lookup in the tree, so a re-grouped card must rebuild
            // even when all of its own numbers are identical.
            hash = hash * 31 + groupId;

            // Flipping a card between mirrored and normal changes every vertex it produces.
            hash = hash * 31 + mirrored.GetHashCode();

            // POST profile provenance. The COUNT alone is not enough - a POST whose weight
            // changes rewrites bend/x/y/z with the count unchanged.
            hash = hash * 31 + postShapeProfileContributions.Count;
            foreach (PostShapeProfileContribution contribution in postShapeProfileContributions)
            {
                hash = hash * 31 + contribution.postId;
                hash = hash * 31 + contribution.bend.GetHashCode();
                hash = hash * 31 + contribution.x.GetHashCode();
                hash = hash * 31 + contribution.y.GetHashCode();
                hash = hash * 31 + contribution.z.GetHashCode();
            }

            // Clump displacement fields. Dead code today - SetClumpModifier has no callers -
            // but they are read in the vertex loop, so hashing them is cheap insurance against
            // the day something starts calling it again.
            hash = hash * 31 + clumpActive.GetHashCode();
            hash = hash * 31 + clumpStrength.GetHashCode();
            hash = hash * 31 + clumpSurfacePoint.GetHashCode();
            hash = hash * 31 + clumpSurfaceNormal.GetHashCode();

            // Read by the CLUMPER early-return at the bottom of GenerateMesh, and it decides
            // whether the mesh write happens at all - so it is a genuine input even though it
            // contributes no vertices. Note the hash is recorded BEFORE that flag is cleared,
            // which means the first guarded call after a clump release sees a changed hash and
            // rebuilds once. That is the direction we want to err in.
            //
            // GroupClumperManager.HasActiveClumper(groupId) is the other half of that guard
            // and is deliberately NOT hashed: it costs a dictionary lookup plus a LINQ Any per
            // call, which per card per frame would eat the saving. It does not need to be -
            // ThreeColumnClumperMeshAuthority filters clumpers by amount > .0001f before
            // building its group set, so a clumper zeroed in place drops out of that set and
            // RestoreRemovedGroups clears the override with a DIRECT GenerateMesh call.
            hash = hash * 31 + externalClumpOverrideActive.GetHashCode();

            // Curve data that lives OUTSIDE this card. Both registries can be edited in place -
            // dragging a curve key mutates the stored AnimationCurve object itself - so no
            // per-card field moves when they change. Hashing the keyframes directly is not an
            // option: AnimationCurve.keys allocates a fresh array on every access, which would
            // cost more than the rebuild this is here to avoid. A monotonic stamp is the cheap,
            // complete answer. Per-GROUP for the registry, so that editing one group's profile
            // does not dirty every card in the scene.
            // Per-GROUP form flip. Lives outside the card like the curve registries do, so it
            // has to be hashed or toggling the button would change nothing until some unrelated
            // edit happened to dirty the group.
            hash = hash * 31 + GroupNormalFlipAuthority.IsFlipped(groupId).GetHashCode();

            // The cross-section profile changes both the vertex count and every position, so
            // it has to be here or a card would keep whichever shape it happened to be built
            // with. This is also what makes the switch self-healing rather than dependent on
            // somebody remembering to rebuild: a card that missed HairCardSection's own sweep -
            // spawned a frame later, restored by a load, hidden inside a SOLO freeze - notices
            // by itself on its next re-assertion.
            hash = hash * 31 + (int)HairCardSection.Current;

            // The topology rule changes which triangles span the vertices, so a card that missed
            // the sweep heals itself the same way a profile change does.
            hash = hash * 31 + (int)HairCardSection.CurrentTopology;

            hash = hash * 31 + GroomShapeCurveRegistry.EpochFor(groupId);
            hash = hash * 31 + PostShapeCurveBridge.Epoch;

            return hash;
        }
    }

    // The guarded entry point. ONLY the per-frame re-assertion paths call this. Anything that
    // must write no matter what keeps calling GenerateMesh() directly.
    public void GenerateMeshIfInputsChanged()
    {
        if (mesh == null || segments < 1) return;

        if (hasMeshInputHash && !foreignMeshWrite && ComputeMeshInputHash() == lastMeshInputHash)
        {
            // One last check before trusting the skip: the filter must still be pointing at
            // the Mesh we maintain. A stray MeshFilter.mesh read anywhere else silently swaps
            // the filter onto a duplicate, and a card in that state has to rebuild so
            // GetLiveMesh can re-heal it - otherwise it freezes on screen for the session.
            if (meshFilter == null) meshFilter = GetComponent<MeshFilter>();
            if (meshFilter != null && meshFilter.sharedMesh == mesh) return;
        }

        GenerateMesh();
    }

    public void GenerateMesh()
    {
        if (mesh == null || segments < 1) return;

        int columns = CrossSectionColumns;
        int numVertices = (segments + 1) * columns;
        baseVertices = new Vector3[numVertices];
        Vector2[] uvs = new Vector2[numVertices];
        int[] triangles = new int[segments * HairCardSection.IndicesPerSegment];

        // Segment density remap, spine and section frames all resolved up front - the
        // frame at a row needs its neighbours' spine points, so it cannot be done
        // inline. See BuildSegmentFrames.
        float[] segmentT = new float[segments + 1];
        Vector3[] segmentSpine = new Vector3[segments + 1];
        Quaternion[] segmentFrame = new Quaternion[segments + 1];
        BuildSegmentFrames(this, segments, length, segmentT, segmentSpine, segmentFrame);

        for (int i = 0; i <= segments; i++)
        {
            float t = segmentT[i];
            float z = t * length;
            float baseULeft = uScale < 0f ? 1f : 0f;
            float baseURight = uScale < 0f ? 0f : 1f;
            float finalULeft = baseULeft * Mathf.Abs(uScale) + uOffset;
            float finalURight = baseURight * Mathf.Abs(uScale) + uOffset;

            float absVScale = Mathf.Abs(vScale);
            float baseV = (1f - t) * absVScale;
            if (vScale < 0f) baseV = absVScale - baseV;
            float finalV = baseV + vOffset;
            int index = i * columns;
            // Per row now - the Width profile curve makes both of these functions of t.
            float currentWidth;
            float ridgeHeight;
            EvaluateCrossSection(this, t, out currentWidth, out ridgeHeight);

            // Curl is resolved before the cross-section is built, because the section
            // has to be banked into the turn as it is laid down rather than rotated
            // afterwards - rotating later would swing any clump displacement around
            // with it.
            Vector3 curlOffset;
            Quaternion bankRotation;
            EvaluateCurl(this, t, out curlOffset, out bankRotation, mirrored);

            Vector3 waveOffset;
            EvaluateWave(this, t, out waveOffset, mirrored);

            Vector3 sectionOrigin = new Vector3(0f, 0f, z);

            // Everything that displaces the WHOLE section, accumulated before the section is
            // built rather than added to each point afterwards. The points themselves are
            // HairCardSection's business now - it is the only thing that knows whether there
            // are three of them or four.
            Vector3 sectionOffset = Vector3.zero;

            if (clumpActive && t > 0f)
            {
                float influence = Mathf.Clamp01(clumpStrength * clumpCurve.Evaluate(t));

                // This used to average the left and right points to find the undisplaced
                // centre. Those two are sectionOrigin plus and minus the same banked
                // half-span, so their midpoint IS sectionOrigin - identical arithmetic, and
                // it no longer needs the points to exist first.
                Vector3 worldAxisPoint = clumpSurfacePoint + clumpSurfaceNormal * (length * t);
                Vector3 targetCenter = transform.InverseTransformPoint(worldAxisPoint);
                sectionOffset += Vector3.Lerp(sectionOrigin, targetCenter, influence) - sectionOrigin;
            }

            // Curl (spiral/coil): displaces the whole cross-section outward from the straight
            // centerline, sweeping around the card's own length axis as t increases. Applied
            // after width (currentWidth above) and before Bend/X/Y/Z's rotation below, so a
            // curled card still gets bent/angled as a whole on top of its own coil shape.
            // The section is banked into this same sweep as it is built.
            sectionOffset += curlOffset;

            // Wave rides on top of curl. Both are displacements of the whole cross-section in
            // the card's own local space, applied after the section has been built and banked
            // and before the path-following frame places it, so a card can be curled AND wavy
            // without either shape fighting the other.
            sectionOffset += waveOffset;

            // The spine keeps exactly the position the authored bend/twist rotation
            // always put it at. Only the section's own lateral extent - width, ridge,
            // clump displacement, curl offset - is placed with the path-following
            // frame, so bend shapes the card as before while the curl stays round.
            Vector3 spinePoint = segmentSpine[i];
            Quaternion sectionFrame = segmentFrame[i];

            HairCardSection.WriteRow(
                baseVertices, uvs, index,
                sectionOrigin, bankRotation, sectionOffset,
                spinePoint, sectionFrame,
                currentWidth, ridgeHeight,
                finalULeft, finalURight, finalV);
        }

        // After the row loop, so the positions the diagonal choice reads are this rebuild's.
        BuildStripTriangles(segments, GroupNormalFlipAuthority.IsFlipped(groupId), triangles, baseVertices);

        int sourceSignature = ComputeGeneratedMeshSignature(baseVertices, uvs, segments);
        generatedMeshSignature = sourceSignature;

        // Record what this rebuild was produced from, for GenerateMeshIfInputsChanged.
        //
        // Recorded here rather than after the mesh write so it also covers the CLUMPER
        // early-return below: if the inputs have not moved and the clumper is still active,
        // re-deriving this identical source and discarding it again is pure waste. The paths
        // that END clumping (ThreeColumnClumperMeshAuthority.RestoreRemovedGroups,
        // GroupClumperManager.RemoveClumper) both call ClearExternalClumpOverride followed by
        // GenerateMesh DIRECTLY, so clean geometry always gets written when clumping releases.
        lastMeshInputHash = ComputeMeshInputHash();
        hasMeshInputHash = true;
        foreignMeshWrite = false;

        // POST/other authorities can still call GenerateMesh every frame. If they produced the
        // exact same source that the CLUMPER stage already consumed, keep the derived mesh in
        // place. Any actual change to source vertices/UVs/topology automatically falls through.
        // The HasActiveClumper check makes this guard self-limiting: it can only ever skip while
        // a clumper with amount > 0 genuinely exists on this group RIGHT NOW. Previously it
        // trusted externalClumpOverrideActive alone, and several lifecycle paths (removal,
        // load-time restore, pre-delete neutralize) each managed to leave that flag stuck true -
        // permanently freezing every subsequent mesh write for the whole group, from POST
        // editing and root sliders alike, any time clumping had ever touched the group.
        // GUIDE curves are the other thing that can legitimately own this mesh. The evaluator is
        // shared (ThreeColumnClumperMeshAuthority folds guides into its reconstruction), so the
        // override flag it sets means "a modifier owns this", not "a clumper owns this". Testing
        // only for a clumper made a guide-only group fail this guard every frame: the override
        // was dropped and clean geometry rewritten at order 5000, the evaluator saw its own
        // signature flip and re-derived at 5255, and the two alternated forever - a full
        // per-frame rebuild of every card in the group, which is exactly what this guard exists
        // to prevent. Both halves stay self-limiting: each only holds while a modifier with
        // amount > 0 genuinely exists on this group right now.
        if (externalClumpOverrideActive && sourceSignature == externalClumpSourceSignature &&
            (GroupClumperManager.HasActiveClumper(groupId) || GuideCurveManager.HasActiveGuide(groupId)))
            return;

        externalClumpOverrideActive = false;
        mesh.Clear();
        mesh.vertices = baseVertices;
        mesh.uv = uvs;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        // Bumped only where the mesh is ACTUALLY written - after every early return above, so a
        // rebuild that was skipped does not count as one. See MeshGeneration.
        unchecked { meshGeneration++; }
    }

    static int ComputeGeneratedMeshSignature(Vector3[] vertices, Vector2[] uvs, int segmentCount)
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + segmentCount;
            if (vertices != null)
            {
                hash = hash * 31 + vertices.Length;
                for (int i = 0; i < vertices.Length; i++)
                {
                    hash = hash * 31 + vertices[i].x.GetHashCode();
                    hash = hash * 31 + vertices[i].y.GetHashCode();
                    hash = hash * 31 + vertices[i].z.GetHashCode();
                }
            }
            if (uvs != null)
            {
                hash = hash * 31 + uvs.Length;
                for (int i = 0; i < uvs.Length; i++)
                {
                    hash = hash * 31 + uvs[i].x.GetHashCode();
                    hash = hash * 31 + uvs[i].y.GetHashCode();
                }
            }
            return hash;
        }
    }
}
