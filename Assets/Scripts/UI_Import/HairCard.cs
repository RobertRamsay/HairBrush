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
    public const int CrossSectionColumns = 3;

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
    public static void EvaluateCurl(
        int groupId,
        float curlFrequency,
        float curlDiameter,
        float t,
        out Vector3 curlOffset,
        out Quaternion bankRotation)
    {
        curlOffset = Vector3.zero;
        bankRotation = Quaternion.identity;

        if (curlFrequency == 0f) return;
        if (curlDiameter <= 0f) return;

        // Root-only profile curves (see GroomShapeCurveAuthority) - no per-POST override.
        float freqMultiplier = PostShapeCurveBridge.EvaluateRoot(groupId, GroomShapeCurveChannel.CurlFrequency, t);
        float diameterMultiplier = PostShapeCurveBridge.EvaluateRoot(groupId, GroomShapeCurveChannel.CurlDiameter, t);
        float turns = curlFrequency * freqMultiplier;
        float radius = curlDiameter * diameterMultiplier * .5f;
        float angle = turns * t * Mathf.PI * 2f;

        // cos(0)-1 = 0 and sin(0) = 0, so this is exactly zero at the root (t=0),
        // keeping the coil continuous with the card's actual root position.
        curlOffset = new Vector3(radius * (Mathf.Cos(angle) - 1f), radius * Mathf.Sin(angle), 0f);

        if (CurlBankAmount == 0f) return;
        bankRotation = Quaternion.AngleAxis(angle * Mathf.Rad2Deg * CurlBankAmount, Vector3.forward);
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

    [Header("UV Settings")]
    public float uScale = 1.0f;
    public float vScale = 1.0f;
    public float uOffset = 0.0f;
    public float vOffset = 0.0f;

    [Header("Grouping")]
    public int groupId = 0;

    [Header("Selection State")]
    [Range(0f, 1f)] public float selectionWeight = 0f;

    private MeshFilter meshFilter;
    private Mesh mesh;
    private Vector3[] baseVertices;
    private Vector3 spawnHitPoint;
    private Vector3 surfaceNormal;
    private float currentEmbedDepth = 0.01f;
    private float storedOffsetX, storedOffsetY, storedOffsetZ;
    private float baseLength, baseWidth, baseBend, baseTwist, baseEmbedDepth;
    private float baseCurlFrequency, baseCurlDiameter;
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
    // PostShapeCurveBridge, PostPredeterminedUVAuthority - calls GenerateMesh() directly and
    // is completely unaffected.
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
        }

        Quaternion fullOffset = Quaternion.Euler(storedOffsetX, storedOffsetY, storedOffsetZ);
        Quaternion curvedOffset = Quaternion.Euler(profiledX, profiledY, profiledZ);
        Quaternion bendAndTwist = Quaternion.Euler(profiledBend, 0f, twistAngle * t);

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

    public void ApplyEvaluatedState(GroomState state)
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
        if (surfaceNormal != Vector3.zero) UpdateTransformOrientation(currentEmbedDepth);

        // Guarded. PostAffectorManager, PostFreeCanonicalAuthority and
        // PostVarianceAffectorBridge all re-assert state through here every single frame,
        // overwhelmingly with values identical to the ones already on the card. The field
        // writes and the transform update above still run unconditionally - only the mesh
        // rebuild is skipped, and only when nothing that feeds it has moved.
        GenerateMeshIfInputsChanged();
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
            curlDiameter = curlDiameter
        };
    }

    GroomState SanitizeState(GroomState state)
    {
        state.length = Mathf.Max(0.0001f, state.length);
        state.width = Mathf.Max(0.0005f, state.width);
        state.segments = Mathf.Clamp(state.segments, 1, 60);
        state.depth = Mathf.Max(0f, state.depth);
        state.curlDiameter = Mathf.Max(0f, state.curlDiameter);
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
        transform.position = spawnHitPoint - (surfaceNormal * embedDepth);
        transform.rotation = Quaternion.LookRotation(surfaceNormal) * Quaternion.Euler(storedOffsetX, storedOffsetY, storedOffsetZ);
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

    public void SetParameters(float newLength, float newWidth, int newSegments, float newBend, float newTwist, float offsetX, float offsetY, float offsetZ, float newEmbedDepth, float strengthMultiplier = 1f, float newUScale = 1f, float newVScale = 1f, float newUOffset = 0f, float newVOffset = 0f, float newCurlFrequency = 0f, float newCurlDiameter = 0f)
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

    public void CaptureBaseState(float activeLength, float activeWidth, int activeSegments, float activeBend, float activeTwist, float activeDepth, float ox, float oy, float oz, float activeCurlFrequency = 0f, float activeCurlDiameter = 0f)
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
    }

    public void SetSelectionWeight(float weight) { selectionWeight = Mathf.Clamp01(weight); UpdateVisualHighlight(); }

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

    void Awake()
    {
        unchecked { registryVersion++; }
        meshFilter = GetComponent<MeshFilter>();
        mesh = new Mesh { name = "ProceduralHairCard" };
        meshFilter.mesh = mesh;
        SetupMaterial();
        GenerateMesh();
        UpdateVisualHighlight();
        CaptureCanonicalFromRendered();
    }

    void OnValidate() { if (mesh != null) GenerateMesh(); }
    void OnDestroy()
    {
        unchecked { registryVersion++; }
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
            hash = hash * 31 + uScale.GetHashCode();
            hash = hash * 31 + vScale.GetHashCode();
            hash = hash * 31 + uOffset.GetHashCode();
            hash = hash * 31 + vOffset.GetHashCode();

            // groupId routes every curve lookup in the tree, so a re-grouped card must rebuild
            // even when all of its own numbers are identical.
            hash = hash * 31 + groupId;

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

        const int columns = CrossSectionColumns;
        int numVertices = (segments + 1) * columns;
        baseVertices = new Vector3[numVertices];
        Vector2[] uvs = new Vector2[numVertices];
        int[] triangles = new int[segments * 12];
        float halfWidth = width * 0.5f;
        float ridgeHeight = GetCrossSectionRidgeHeight();

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
            float finalUCenter = (finalULeft + finalURight) * .5f;

            float absVScale = Mathf.Abs(vScale);
            float baseV = (1f - t) * absVScale;
            if (vScale < 0f) baseV = absVScale - baseV;
            float finalV = baseV + vOffset;
            int index = i * columns;
            float currentWidth = halfWidth * flattenFactor;

            // Curl is resolved before the cross-section is built, because the section
            // has to be banked into the turn as it is laid down rather than rotated
            // afterwards - rotating later would swing any clump displacement around
            // with it.
            Vector3 curlOffset;
            Quaternion bankRotation;
            EvaluateCurl(groupId, curlFrequency, curlDiameter, t, out curlOffset, out bankRotation);

            Vector3 sectionOrigin = new Vector3(0f, 0f, z);
            Vector3 left = sectionOrigin + bankRotation * new Vector3(-currentWidth, 0f, 0f);
            Vector3 center = sectionOrigin + bankRotation * new Vector3(0f, ridgeHeight, 0f);
            Vector3 right = sectionOrigin + bankRotation * new Vector3(currentWidth, 0f, 0f);

            if (clumpActive && t > 0f)
            {
                float influence = Mathf.Clamp01(clumpStrength * clumpCurve.Evaluate(t));
                Vector3 straightCenter = (left + right) * 0.5f;
                Vector3 worldAxisPoint = clumpSurfacePoint + clumpSurfaceNormal * (length * t);
                Vector3 targetCenter = transform.InverseTransformPoint(worldAxisPoint);
                Vector3 movedCenter = Vector3.Lerp(straightCenter, targetCenter, influence);
                Vector3 delta = movedCenter - straightCenter;
                left += delta;
                center += delta;
                right += delta;
            }

            // Curl (spiral/coil): displaces the whole cross-section outward from the straight
            // centerline, sweeping around the card's own length axis as t increases. Applied
            // after width (currentWidth above) and before Bend/X/Y/Z's rotation below, so a
            // curled card still gets bent/angled as a whole on top of its own coil shape.
            // The section was already banked into this same sweep when it was built.
            left += curlOffset;
            center += curlOffset;
            right += curlOffset;

            // The spine keeps exactly the position the authored bend/twist rotation
            // always put it at. Only the section's own lateral extent - width, ridge,
            // clump displacement, curl offset - is placed with the path-following
            // frame, so bend shapes the card as before while the curl stays round.
            Vector3 spinePoint = segmentSpine[i];
            Quaternion sectionFrame = segmentFrame[i];

            baseVertices[index] = spinePoint + sectionFrame * (left - sectionOrigin);
            baseVertices[index + 1] = spinePoint + sectionFrame * (center - sectionOrigin);
            baseVertices[index + 2] = spinePoint + sectionFrame * (right - sectionOrigin);
            uvs[index] = new Vector2(finalULeft, finalV);
            uvs[index + 1] = new Vector2(finalUCenter, finalV);
            uvs[index + 2] = new Vector2(finalURight, finalV);
        }

        int triIndex = 0;
        for (int i = 0; i < segments; i++)
        {
            int row = i * columns;
            int next = row + columns;

            // Left half of the convex strip.
            triangles[triIndex++] = row;
            triangles[triIndex++] = next;
            triangles[triIndex++] = row + 1;
            triangles[triIndex++] = row + 1;
            triangles[triIndex++] = next;
            triangles[triIndex++] = next + 1;

            // Right half.
            triangles[triIndex++] = row + 1;
            triangles[triIndex++] = next + 1;
            triangles[triIndex++] = row + 2;
            triangles[triIndex++] = row + 2;
            triangles[triIndex++] = next + 1;
            triangles[triIndex++] = next + 2;
        }

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
        if (externalClumpOverrideActive && sourceSignature == externalClumpSourceSignature &&
            GroupClumperManager.HasActiveClumper(groupId))
            return;

        externalClumpOverrideActive = false;
        mesh.Clear();
        mesh.vertices = baseVertices;
        mesh.uv = uvs;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
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
