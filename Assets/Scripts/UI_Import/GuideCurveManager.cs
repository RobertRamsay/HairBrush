using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// V017 GUIDE curves.
//
// A guide is a three-point curve - contact, mid, end - that neighbouring cards adopt the SHAPE
// of. It is not a clump: every affected card keeps its own root and takes on the guide's
// curvature, fanning out in parallel. GUIDE means direction, CLUMPER means gathering, and the
// two compose - ThreeColumnClumperMeshAuthority applies guides to its clean reconstruction
// first, so the clumper then gathers strands that are already guided.
//
// Two decisions are worth knowing before reading the rest.
//
// MID AND END ARE STORED IN THE CONTACT FRAME, not in world space. That is what makes
// "SPACE+click repositions it but keeps its form" free: move the contact, rebuild the frame from
// the new surface normal, and the offsets ride along. It also keeps a guide sane when it is
// moved across a curved surface, where a world-space curve would end up buried or floating.
//
// CARDS SAMPLE THE GUIDE BY ABSOLUTE ARC LENGTH, not by normalised t. A card of length Lc reads
// the guide from arc length 0 to Lc: shorter than the guide and it follows the first part and
// never sees the rest; longer and it runs out of the end along the exit tangent, straight.
// Normalised t - which is right for the clumper, where both sides are cards - would stretch or
// squash the guide per card, so a long card would exaggerate the curve instead of straightening
// out of it, and two cards of different length under one guide would not share a shape.
[DefaultExecutionOrder(5240)]
public class GuideCurveManager : MonoBehaviour
{
    // Matches the CLUMPER creation defaults. A guide's zone is the same kind of thing and
    // starting them at the same size makes the two directly comparable on the model.
    // Two nodes plus the contact is the original three point guide and the floor. Twenty plus
    // the contact is the ceiling: a guide is as long as the hair it steers, so past about that
    // the points are closer together than the cards are and the extra control buys nothing but
    // a curve that is harder to aim at.
    public const int MinGuideNodes = 2;
    public const int MaxGuideNodes = 20;

    // How far above the surface plane a node has to stay, in world units.
    //
    // A guide pointing into the model is meaningless - the cards would be driven through the
    // scalp - so no node may sit below the tangent plane at the contact. Lives here rather than
    // in GuideCurveHandleAuthority, which is where it used to be, because MoveGuideRoot has to
    // apply the same floor and two copies of a limit like this drift.
    public const float MinNodeHeight = .002f;

    public const float DefaultGuideRadius = .04f;
    public const float DefaultGuideFalloff = .04f;

    // Guide colour is carried as a HUE and nothing else. Saturation and value are fixed, so
    // every guide reads as the same KIND of thing however it is coloured - a recolour tells two
    // overlapping guides apart, it does not let one be drawn muddy brown or washed-out grey.
    //
    // .7485 is not a round number on purpose: it is the exact hue of the purple every guide has
    // been drawn in since guides existed. Color.HSVToRGB(.7485, .55, 1) returns (.72, .45, 1),
    // which is the constant it replaces, so a project saved before this existed - and a guide
    // created after it and never touched - looks exactly as it always did.
    public const float DefaultGuideHue = .7485f;
    public const float GuideSaturation = .55f;
    public const float GuideValue = 1f;

    // The one place a guide's hue becomes a colour. Alpha is the caller's business: the curve,
    // the inner zone ring and the outer zone ring are the same colour at three strengths.
    public static Color CurveColor(GuideCurve guide, float alpha)
    {
        float hue = DefaultGuideHue;
        if (guide != null) hue = Mathf.Repeat(guide.hue, 1f);

        Color rgb = Color.HSVToRGB(hue, GuideSaturation, GuideValue);
        rgb.a = alpha;
        return rgb;
    }

    // How many points the arc-length table holds. The curve is a quadratic, so it is smooth
    // between samples; this only has to be fine enough that the cumulative-length approximation
    // does not visibly shorten the curve.
    private const int PathSamples = 32;
    private const int SamplesPerSpan = 16;
    private const int MaxPathSamples = 321;

    [Serializable]
    public class GuideCurve
    {
        public int id;
        public int groupId;

        // World. The point on the model the guide grows out of.
        public Vector3 contact;
        public Vector3 normal = Vector3.up;

        // The contact frame, CARRIED rather than re-derived from the normal each time.
        //
        // Deriving it would be simpler and is wrong: any construction has to pick a reference
        // axis, and every choice of reference has a discontinuity somewhere on the sphere. A
        // guide SPACE+clicked across that seam would have its stored shape snap through ~180
        // degrees about its own axis - the precise opposite of "keeps its form". Carrying the
        // frame and rotating it by the minimal old-normal-to-new-normal rotation has no seam at
        // all, so a guide dragged from the side of a head to the crown keeps its shape exactly.
        public Quaternion frame = Quaternion.identity;

        // The points the curve passes through, in that frame, ordered root to tip. Local +Y is
        // the surface normal, so a guide whose offsets are all on the Y axis stands straight out
        // of the surface. The contact is NOT in this list - it is always the first point of the
        // curve, and it moves through MoveGuideRoot rather than SetNode because it has to stay ON
        // the model and supplies the normal this whole frame is carried by.
        //
        // Two entries is a guide as it has always been: a mid and an end. Extra points are
        // inserted between them with CTRL+SHIFT+click; the TIP can never be removed, and neither can the
        // removal that would take the list below two, so the shape always keeps a root, something
        // in the middle and a tip. See RemoveNode for why the FIRST node is deliberately not
        // protected as well.
        public List<Vector3> nodesLocal = new List<Vector3> { Vector3.up, Vector3.up };

        // Starts at zero, exactly like a new clumper: a modifier that changes nothing until it
        // is asked to. Dropping a guide onto the model should never move hair by itself.
        [Range(0f, 1f)] public float amount = 0f;
        public float radius = DefaultGuideRadius;
        public float falloff = DefaultGuideFalloff;

        // What colour this guide draws in. See DefaultGuideHue - initialised to the purple every
        // guide used to be, so an untouched guide is indistinguishable from one made before
        // guides could be recoloured.
        [Range(0f, 1f)] public float hue = DefaultGuideHue;
    }

    private readonly Dictionary<int, List<GuideCurve>> byGroup = new Dictionary<int, List<GuideCurve>>();

    private ModelViewer viewer;
    private MethodInfo createSliderMethod;
    private GameObject controlsRoot;
    private int selectedGuideId = -1;
    private int selectedGroup = -1;
    private int nextGuideId = 1;
    private float nextUIScan;
    private int lastHandledFrame = -1;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<GuideCurveManager>() != null) return;
        GameObject go = new GameObject("GuideCurveManager");
        DontDestroyOnLoad(go);
        go.AddComponent<GuideCurveManager>();
    }

    void Awake()
    {
        instance = this;
        byGroup.Clear();
        viewer = null;
        createSliderMethod = null;
        controlsRoot = null;
        selectedGuideId = -1;
        selectedGroup = -1;
        nextGuideId = 1;
        nextUIScan = 0f;
        lastHandledFrame = -1;
    }

    void Update()
    {
        Resolve();
        if (viewer == null) return;

        HandleSpaceReposition();
        MaintainSelectionExit();

        if (Time.unscaledTime < nextUIScan) return;
        nextUIScan = Time.unscaledTime + .10f;
        EnsureRows();
        MaintainControls();
    }

    void Resolve()
    {
        if (viewer != null) return;
        viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer == null) return;
        createSliderMethod = typeof(ModelViewer).GetMethod("CreateSliderUI",
            BindingFlags.Instance | BindingFlags.NonPublic);
    }

    // ---------------------------------------------------------------------------------
    // Contact frame and curve evaluation
    // ---------------------------------------------------------------------------------

    // Builds a frame from scratch. Only ever called ONCE per guide, at creation - after that
    // the guide carries its own frame and repositioning rotates it. See GuideCurve.frame.
    public static Quaternion BuildInitialFrame(Vector3 normal)
    {
        Vector3 n = Vector3.up;
        if (normal.sqrMagnitude > .000001f) n = normal.normalized;

        Vector3 reference = Vector3.up;
        if (Mathf.Abs(Vector3.Dot(n, Vector3.up)) > .95f) reference = Vector3.right;

        Vector3 tangent = Vector3.Cross(reference, n);
        if (tangent.sqrMagnitude < .00000001f) tangent = Vector3.Cross(Vector3.forward, n);
        return Quaternion.LookRotation(tangent.normalized, n);
    }

    public static int NodeCount(GuideCurve guide)
    {
        if (guide == null || guide.nodesLocal == null) return 0;
        return guide.nodesLocal.Count;
    }

    public static Vector3 WorldNode(GuideCurve guide, int index)
    {
        if (guide == null || guide.nodesLocal == null) return Vector3.zero;
        if (index < 0 || index >= guide.nodesLocal.Count) return Vector3.zero;
        return guide.contact + guide.frame * guide.nodesLocal[index];
    }

    public static void SetNode(GuideCurve guide, int index, Vector3 local)
    {
        if (guide == null || guide.nodesLocal == null) return;
        if (index < 0 || index >= guide.nodesLocal.Count) return;
        guide.nodesLocal[index] = local;
    }

    // Every point of the curve, contact first, in world space. Allocated per call, so callers
    // that run per frame build it once and hand it around rather than asking twice.
    public static Vector3[] WorldPoints(GuideCurve guide)
    {
        int count = NodeCount(guide);
        if (count == 0) return new Vector3[0];

        Vector3[] points = new Vector3[count + 1];
        points[0] = guide.contact;
        for (int i = 0; i < count; i++) points[i + 1] = WorldNode(guide, i);
        return points;
    }

    // ------------------------------------------------------------------ adding and removing

    // Inserts a point, already in the guide's frame, into the span it belongs to. Returns the
    // index of the new node, or -1 if the guide is already at its ceiling.
    //
    // Takes a LOCAL offset rather than a world point on purpose: the caller has to clamp it above
    // the contact plane first, and handing this a world position would put the one writer that
    // skips that clamp right here.
    public static int InsertNode(GuideCurve guide, int spanIndex, Vector3 local)
    {
        if (guide == null || guide.nodesLocal == null) return -1;
        if (guide.nodesLocal.Count >= MaxGuideNodes) return -1;

        // spanIndex counts spans of the drawn curve: span 0 runs contact to node 0, span 1 runs
        // node 0 to node 1, and so on. A point in span i becomes node i.
        //
        // Clamped BELOW the last node, not to the count. Inserting at the count appends past the
        // tip and makes the new point the tip, which is the one thing RemoveNode is written to
        // guarantee cannot happen - and an invariant guarded at one end only is not an invariant.
        int index = Mathf.Clamp(spanIndex, 0, guide.nodesLocal.Count - 1);
        guide.nodesLocal.Insert(index, local);
        return index;
    }

    // Only the TIP is permanent, plus the two-node floor. Making the FIRST node permanent as well
    // sounds right and is not: a point inserted in the first span becomes index 0, so the node
    // the user had been shaping would be demoted to removable and the one they had just added
    // would be the protected one. Guarding the last node and the count gives the same guarantee
    // that matters - a root, something between, and a tip - without depending on which index a
    // point happens to hold this minute.
    public static bool RemoveNode(GuideCurve guide, int index)
    {
        if (guide == null || guide.nodesLocal == null) return false;
        if (guide.nodesLocal.Count <= MinGuideNodes) return false;
        if (index < 0 || index >= guide.nodesLocal.Count - 1) return false;

        guide.nodesLocal.RemoveAt(index);
        return true;
    }

    // Is anything at all being shaped right now. Read by ModelViewer, which stands its right
    // button's camera gesture down while CTRL+SHIFT is held so CTRL+SHIFT plus right can mean
    // "remove this point".
    public static bool AnyGuideSelected
    {
        get
        {
            if (instance == null) return false;
            return instance.GetSelectedGuide() != null;
        }
    }

    // World point back into the guide's own frame. This is what a drag handle writes through.
    public static Vector3 ToLocal(GuideCurve guide, Vector3 world)
    {
        if (guide == null) return Vector3.zero;
        return Quaternion.Inverse(guide.frame) * (world - guide.contact);
    }

    // The quadratic that passes through all THREE points, at t = 0, 0.5 and 1.
    //
    // A quadratic Bezier would be the obvious choice and is wrong here: its middle point is an
    // off-curve handle, so dragging "mid" would not put the curve where the handle is. These are
    // three points ON the guide, so the curve has to interpolate them. This is the Lagrange
    // basis for those three nodes, which is the unique quadratic through them.
    public static Vector3 Evaluate(Vector3 p0, Vector3 p1, Vector3 p2, float t)
    {
        float a = 2f * t * t - 3f * t + 1f;
        float b = -4f * t * t + 4f * t;
        float c = 2f * t * t - t;
        return p0 * a + p1 * b + p2 * c;
    }

    public static Vector3 EvaluateTangent(Vector3 p0, Vector3 p1, Vector3 p2, float t)
    {
        float a = 4f * t - 3f;
        float b = -8f * t + 4f;
        float c = 4f * t - 1f;
        return p0 * a + p1 * b + p2 * c;
    }

    // The whole curve, for any number of points, as one parameter from 0 at the contact to 1 at
    // the tip.
    //
    // THREE points keep the quadratic above, exactly. That is not tidiness, it is every guide
    // ever authored: a spline through three points is a different curve from the parabola
    // through them, so routing the three point case through the general path would quietly
    // reshape every guide in every saved project the moment it loaded.
    //
    // FOUR or more use a Catmull-Rom spline, which interpolates every point it is given - the
    // same property the quadratic was chosen for. The end tangents are taken from the doubled
    // end points, so the curve leaves the contact and arrives at the tip along the chords, which
    // is what makes an added point feel like it bends the curve rather than moving it.
    public static Vector3 EvaluatePoints(Vector3[] points, float t)
    {
        if (points == null || points.Length == 0) return Vector3.zero;
        if (points.Length == 1) return points[0];
        if (points.Length == 2) return Vector3.Lerp(points[0], points[1], Mathf.Clamp01(t));
        if (points.Length == 3) return Evaluate(points[0], points[1], points[2], t);

        int spans = points.Length - 1;
        float scaled = Mathf.Clamp01(t) * spans;
        int span = Mathf.Min((int)scaled, spans - 1);
        float u = scaled - span;

        Vector3 a = points[Mathf.Max(span - 1, 0)];
        Vector3 b = points[span];
        Vector3 c = points[span + 1];
        Vector3 d = points[Mathf.Min(span + 2, points.Length - 1)];
        return CatmullRom(a, b, c, d, u);
    }

    static Vector3 CatmullRom(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float u)
    {
        float u2 = u * u;
        float u3 = u2 * u;
        return .5f * ((2f * b) +
                      (-a + c) * u +
                      (2f * a - 5f * b + 4f * c - d) * u2 +
                      (-a + 3f * b - 3f * c + d) * u3);
    }

    // A guide flattened into a world-space polyline with a cumulative arc-length table, so a
    // card can ask "where is the guide 7cm along its own length" in one lookup. Built once per
    // guide per evaluation pass, never per card.
    public class GuidePath
    {
        public Vector3[] points;
        public float[] cumulative;
        public float totalLength;
        public Vector3 exitTangent;
        public Vector3 origin;

        public Vector3 SampleByLength(float s)
        {
            if (points == null || points.Length == 0) return origin;
            if (s <= 0f) return points[0];

            // PAST THE END: keep going in the direction the guide was last travelling. This is
            // the whole answer to "a card longer than the guide" - it follows the shape while
            // the guide has shape to give, then runs straight out of the end rather than
            // stopping dead at the tip or looping back on itself.
            if (s >= totalLength) return points[points.Length - 1] + exitTangent * (s - totalLength);

            for (int i = 1; i < points.Length; i++)
            {
                if (cumulative[i] < s) continue;
                float span = cumulative[i] - cumulative[i - 1];
                if (span <= .0000001f) return points[i];
                float f = (s - cumulative[i - 1]) / span;
                return Vector3.Lerp(points[i - 1], points[i], f);
            }
            return points[points.Length - 1];
        }
    }

    public static GuidePath BuildPath(GuideCurve guide)
    {
        if (guide == null) return null;

        Vector3[] control = WorldPoints(guide);
        if (control.Length < 2) return null;

        // Sampled per span rather than at a fixed count. Thirty two points was ample for one
        // parabola and would visibly facet a twenty point curve.
        int samples = Mathf.Clamp(SamplesPerSpan * (control.Length - 1) + 1, PathSamples, MaxPathSamples);

        GuidePath path = new GuidePath();
        path.points = new Vector3[samples];
        path.cumulative = new float[samples];
        path.origin = control[0];

        for (int i = 0; i < samples; i++)
        {
            float t = (float)i / (samples - 1);
            path.points[i] = EvaluatePoints(control, t);
            if (i == 0)
            {
                path.cumulative[i] = 0f;
                continue;
            }
            path.cumulative[i] = path.cumulative[i - 1] + Vector3.Distance(path.points[i], path.points[i - 1]);
        }

        path.totalLength = path.cumulative[samples - 1];

        // The analytic derivative at the end for the three point case, and the last chord for
        // everything else - which is also the fallback when the points are collinear and close
        // enough together that the derivative degenerates.
        Vector3 tangent = Vector3.zero;
        if (control.Length == 3) tangent = EvaluateTangent(control[0], control[1], control[2], 1f);
        if (tangent.sqrMagnitude < .00000001f) tangent = path.points[samples - 1] - path.points[samples - 2];
        if (tangent.sqrMagnitude < .00000001f) tangent = guide.normal;
        if (tangent.sqrMagnitude < .00000001f) tangent = Vector3.up;
        path.exitTangent = tangent.normalized;
        return path;
    }

    // ---------------------------------------------------------------------------------
    // Data access, used by ThreeColumnClumperMeshAuthority
    // ---------------------------------------------------------------------------------

    // Static probes, mirroring GroupClumperManager.HasActiveClumper. HairCard's mesh-override
    // guard needs the group answer without holding a reference, and the mesh evaluator needs the
    // scene answer cheaply enough to ask every frame before allocating anything.
    private static GuideCurveManager instance;

    public static bool HasActiveGuide(int groupId)
    {
        if (instance == null) instance = FindFirstObjectByType<GuideCurveManager>();
        if (instance == null) return false;

        List<GuideCurve> list;
        if (!instance.byGroup.TryGetValue(groupId, out list) || list == null) return false;
        foreach (GuideCurve guide in list)
        {
            if (guide != null && guide.amount > .0001f) return true;
        }
        return false;
    }

    // Allocation-free. The evaluator calls this every LateUpdate, and the overwhelmingly common
    // answer in a project with no guides is "no" - which must not cost a LINQ chain to establish.
    public bool HasAnyActiveGuide()
    {
        foreach (List<GuideCurve> list in byGroup.Values)
        {
            if (list == null) continue;
            foreach (GuideCurve guide in list)
            {
                if (guide != null && guide.amount > .0001f) return true;
            }
        }
        return false;
    }

    // Any guide at all, active or not, as opposed to HasActiveGuide which asks whether one is
    // combing anything. Written for the viewport preview, which drew every guide in the group;
    // the preview now draws only the selected one and asks GetSelectedGuide instead, so this has
    // no caller. Kept because it is the natural form of the question and costs four lines.
    public bool HasAnyGuideInGroup(int groupId)
    {
        List<GuideCurve> list;
        if (!byGroup.TryGetValue(groupId, out list) || list == null) return false;
        foreach (GuideCurve guide in list)
        {
            if (guide != null) return true;
        }
        return false;
    }

    public List<GuideCurve> GetGroupGuides(int groupId)
    {
        List<GuideCurve> found;
        if (!byGroup.TryGetValue(groupId, out found)) return new List<GuideCurve>();
        return found.Where(g => g != null).ToList();
    }

    public List<GuideCurve> GetAllGuides()
    {
        return byGroup.Values.SelectMany(list => list).Where(g => g != null).ToList();
    }

    public GuideCurve GetSelectedGuide()
    {
        if (selectedGuideId < 0) return null;
        return FindGuide(selectedGuideId);
    }

    // The same lookup, reachable from outside. GuideRowSwatch holds an ID rather than a guide
    // because a project load replaces every guide object wholesale, and a captured reference
    // would then point at one the manager no longer owns.
    public GuideCurve FindGuidePublic(int id)
    {
        return FindGuide(id);
    }

    GuideCurve FindGuide(int id)
    {
        foreach (List<GuideCurve> list in byGroup.Values)
        {
            foreach (GuideCurve guide in list)
            {
                if (guide != null && guide.id == id) return guide;
            }
        }
        return null;
    }

    // ---------------------------------------------------------------------------------
    // Creation, repositioning, removal
    // ---------------------------------------------------------------------------------

    public GuideCurve CreateGuide(int groupId, Vector3 point, Vector3 normal)
    {
        List<GuideCurve> list;
        if (!byGroup.TryGetValue(groupId, out list))
        {
            list = new List<GuideCurve>();
            byGroup[groupId] = list;
        }

        Vector3 safeNormal = Vector3.up;
        if (normal.sqrMagnitude > .000001f) safeNormal = normal.normalized;

        // Born straight out of the surface, at roughly the length of the hair it will guide, so
        // the first thing raising Amount does is comb the neighbours straight - an unambiguous
        // read on whether the deformation is reaching the cards at all. Shape comes after.
        float reach = .1f;
        if (viewer != null) reach = Mathf.Max(.02f, viewer.currentLength);

        GuideCurve guide = new GuideCurve
        {
            id = nextGuideId++,
            groupId = groupId,
            contact = point,
            normal = safeNormal,
            frame = BuildInitialFrame(safeNormal),
            nodesLocal = new List<Vector3>
            {
                new Vector3(0f, reach * .5f, 0f),
                new Vector3(0f, reach, 0f)
            },
            amount = 0f,
            radius = DefaultGuideRadius,
            falloff = DefaultGuideFalloff
        };

        list.Add(guide);
        SelectGuide(groupId, guide.id);
        return guide;
    }

    // Keeps every node untouched, which IS the "keeps its general form" requirement -
    // they are frame-relative, so re-seating the contact and its normal carries the whole shape
    // to the new spot and re-aims it along the new surface.
    public bool MoveSelectedGuide(int groupId, Vector3 point, Vector3 normal)
    {
        GuideCurve guide = GetSelectedGuide();
        if (guide == null || guide.groupId != groupId) return false;

        guide.contact = point;
        TransportFrame(guide, normal);
        return true;
    }

    // Plants the ROOT somewhere else and lets the curve re-aim, instead of carrying the whole
    // guide across rigidly the way MoveSelectedGuide above does.
    //
    // The two are the guide's two editing gestures and they are deliberately opposites. SPACE and
    // click says "this shape, somewhere else" - contact and nodes travel together, the form is
    // preserved, and it is what you reach for once a guide is shaped the way you want it.
    // Dragging the root ring says "this tip, from somewhere else" - every other point stays where
    // it is in the world and the curve swings to reach them from the new base, which is the only
    // way to change the direction hair leaves the scalp in without moving every point by hand.
    //
    // That second one matters more than it used to. The blend now hands the guide the direction
    // at the root as well as further up, so where the root sits and which way it launches is the
    // single biggest thing about a guide - and until this it was the one part you could only set
    // by clicking somewhere else and starting again.
    //
    // preservedWorld holds where the nodes should END UP, in world space, and the caller captures
    // it ONCE when the drag begins rather than letting this read the guide's current positions.
    //
    // That distinction is the whole reason this takes an array. A root drag calls in every frame,
    // and the height clamp below is one-way: a node pushed up to clear a brow stays up, and next
    // frame that pushed-up position is what "where it was" would mean. Read live, the shape
    // ratchets - drag across a curve and back and the guide comes home taller than it left. Read
    // from a snapshot of the moment the handle was grabbed, every frame recomputes from the same
    // truth, the clamp only ever applies to the surface actually underneath, and dragging back
    // restores the guide exactly.
    public bool MoveGuideRoot(GuideCurve guide, Vector3 point, Vector3 normal,
        Vector3[] preservedWorld, int preservedCount)
    {
        if (guide == null || preservedWorld == null) return false;

        int count = Mathf.Min(NodeCount(guide), Mathf.Min(preservedCount, preservedWorld.Length));

        guide.contact = point;
        TransportFrame(guide, normal);

        for (int i = 0; i < count; i++)
        {
            Vector3 local = ToLocal(guide, preservedWorld[i]);

            // The one thing that cannot be preserved. A point that was comfortably above the old
            // surface can be under the new one - drag the root round a brow or into a concavity
            // and the tangent plane tilts out from under the shape - and a node below it is hair
            // driven back into the head. So world position is kept exactly wherever it can be,
            // and this is where it gives.
            if (local.y < MinNodeHeight) local.y = MinNodeHeight;

            SetNode(guide, i, local);
        }
        return true;
    }

    // Minimal rotation from the old normal to the new one, applied to the frame the guide is
    // already carrying. No reference axis is involved, so there is no seam to cross.
    static void TransportFrame(GuideCurve guide, Vector3 normal)
    {
        if (guide == null || normal.sqrMagnitude <= .000001f) return;

        Vector3 newNormal = normal.normalized;

        // FromToRotation has no defined axis for an exact 180-degree flip - Unity picks an
        // arbitrary perpendicular, so a guide moved to the precise antipode would roll by an
        // unpredictable amount. Naming the axis ourselves makes that one point continuous
        // with its neighbourhood instead of a coin toss.
        Quaternion transport;
        if (Vector3.Dot(guide.normal, newNormal) < -.9999f)
        {
            transport = Quaternion.AngleAxis(180f, guide.frame * Vector3.forward);
        }
        else
        {
            transport = Quaternion.FromToRotation(guide.normal, newNormal);
        }

        guide.frame = transport * guide.frame;
        guide.normal = newNormal;
    }

    // Cleared from GroupClumperManager.SelectClumper, so picking a clumper drops the guide.
    // The reverse happens in SelectGuide below. Without both halves the panel stacks two
    // modifiers' controls at once and SPACE+click becomes ambiguous - the guide row says it is
    // selected while the clumper is the thing that actually moves.
    public void ClearSelection()
    {
        if (selectedGuideId < 0 && selectedGroup < 0) return;
        selectedGuideId = -1;
        selectedGroup = -1;
        DestroyControls();
        nextUIScan = 0f;
    }

    public void SelectGuide(int groupId, int id)
    {
        GuideCurve guide = FindGuide(id);
        if (guide == null || guide.groupId != groupId) return;

        GroupClumperManager clumpers = FindFirstObjectByType<GroupClumperManager>();
        if (clumpers != null) clumpers.ClearSelection();
        ReleasePostSelection();

        selectedGroup = groupId;
        selectedGuideId = id;
        if (viewer != null) viewer.currentGroupId = groupId;
        DestroyControls();
        nextUIScan = 0f;
    }

    public void RemoveGuide(GuideCurve guide)
    {
        if (guide == null) return;

        // Neutralize before removing, the same two-phase pattern the CLUMPER uses. The mesh
        // evaluator releases a group the moment nothing in it is active, so zeroing first gives
        // it a clean frame to restore the cards on before the record disappears.
        guide.amount = 0f;

        List<GuideCurve> list;
        if (byGroup.TryGetValue(guide.groupId, out list))
        {
            list.Remove(guide);
            if (list.Count == 0) byGroup.Remove(guide.groupId);
        }

        if (selectedGuideId == guide.id)
        {
            selectedGuideId = -1;
            selectedGroup = -1;
            DestroyControls();
        }
        nextUIScan = 0f;
    }

    public void ClearAll()
    {
        byGroup.Clear();
        selectedGuideId = -1;
        selectedGroup = -1;
        nextGuideId = 1;
        DestroyControls();
        nextUIScan = 0f;
    }

    // Holds the id allocator above a set of ids that are not in byGroup yet but are about to be.
    // GuideCurvePersistenceBridge empties the manager the moment a project file is parsed, which
    // restarts numbering at 1, and then takes several frames to install the saved guides. A guide
    // placed by hand in that gap would otherwise be handed an id the incoming set already owns.
    public void ReserveGuideIdsAbove(int highestId)
    {
        if (highestId < nextGuideId) return;
        nextGuideId = highestId + 1;
    }

    // Wholesale replacement, used by GuideCurvePersistenceBridge when a project is loaded.
    //
    // A public entry point rather than the bridge reflecting into byGroup: this manager is
    // part of the same feature, so the two can simply agree on a method. The list is taken as
    // a flat set because each guide already carries its own groupId.
    //
    // Nothing is selected afterwards, matching how a restored POST or CLUMPER arrives - the
    // project opens on the group root with every modifier present and none being edited.
    public void ReplaceAll(List<GuideCurve> restored)
    {
        byGroup.Clear();
        selectedGuideId = -1;
        selectedGroup = -1;
        DestroyControls();

        int highest = 0;
        if (restored != null)
        {
            foreach (GuideCurve guide in restored)
            {
                if (guide == null) continue;

                List<GuideCurve> list;
                if (!byGroup.TryGetValue(guide.groupId, out list))
                {
                    list = new List<GuideCurve>();
                    byGroup[guide.groupId] = list;
                }
                list.Add(guide);

                if (guide.id > highest) highest = guide.id;
            }
        }

        // Above every restored id, or a guide created afterwards would collide with one on
        // disk and FindGuide would return whichever it reached first.
        nextGuideId = highest + 1;
        if (nextGuideId < 1) nextGuideId = 1;

        // Rows are rebuilt from byGroup by EnsureRows; bring that scan forward to this frame
        // so the restored guides appear in the panel immediately rather than up to 0.1s later.
        nextUIScan = 0f;
    }

    // SPACE + click moves the selected guide, mirroring the clumper gesture.
    //
    // Guarded on a guide actually being the selected modifier, because SPACE+click is shared
    // property: GroupClumperInteractionAuthority repositions the selected clumper with it, and
    // PostSpaceRepositionAuthority and PostAffectorSurfaceMoveUX both move the active POST. Each
    // of those no-ops when its own modifier is not selected; this does the same, and stands down
    // outright if a clumper is selected, so one click can never move two things.
    void HandleSpaceReposition()
    {
        if (Keyboard.current == null || Mouse.current == null) return;
        // ALT is reserved for the camera. Under MAYA-NAV, ALT+SPACE (or ALT+TAB) plus a click is
        // an awkward chord rather than an impossible one, and it would both tumble the view and
        // fire this gesture. True whenever ALT is held, in either mode.
        if (MayaNavigationAuthority.AltReserved) return;

        if (!Keyboard.current.spaceKey.isPressed) return;
        if (!Mouse.current.leftButton.wasPressedThisFrame) return;
        if (lastHandledFrame == Time.frameCount) return;
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;

        GuideCurve guide = GetSelectedGuide();
        if (guide == null) return;

        // SPACE+click is shared property. Selection is now mutually exclusive with the CLUMPER,
        // but a POST can be active at the same time as a guide is selected, and
        // PostSpaceRepositionAuthority/PostAffectorSurfaceMoveUX will both move it on this same
        // click. Standing down for an active POST means one click moves one thing.
        GroupClumperManager clumpers = FindFirstObjectByType<GroupClumperManager>();
        if (clumpers != null && clumpers.GetSelectedClumper() != null) return;
        if (IsPostActiveOnGroup(guide.groupId)) return;

        if (viewer == null || viewer.mainCamera == null) return;
        Ray ray = viewer.mainCamera.ScreenPointToRay(Mouse.current.position.ReadValue());
        RaycastHit hit;
        if (!Physics.Raycast(ray, out hit)) return;

        lastHandledFrame = Time.frameCount;
        MoveSelectedGuide(guide.groupId, hit.point, hit.normal);
    }

    // Selecting a guide releases any active POST, the same way ClumperPostOwnershipAuthority
    // releases one when a clumper is selected, and for the same reason. Without it the exclusion
    // graph had one unpaired edge: SelectAffector cleared the guide, but SelectGuide left the
    // POST active - so the guide panel opened advertising "SPACE + CLICK moves this guide" while
    // HandleSpaceReposition stood down for the POST and the POST moved instead, silently. Both
    // halves of the POST's state go, because ModelViewer's hotspot and PostAffectorManager's
    // activeId are two halves of one selection and stranding either is unrecoverable.
    void ReleasePostSelection()
    {
        // Deliberately NOT a hand-rolled field clear. PostAffectorManager.ReleasePostSelection is
        // documented as the single atomic way to leave POST editing, and it also resets
        // orphanHotspotFrames and hasPanelControls - state a reflection-based copy silently left
        // behind. ModelViewer.ClearSelectionHotspot is the other half, and skipping it was worse
        // than it looks: it is what zeroes selectionWeight on every brushed card and tears down
        // the POST's brush rows. Leaving weights non-zero with the hotspot false is unrecoverable
        // - HasLiveSelection returns early and never reaches its own self-heal, and every later
        // group-root write takes HairCard.SetParameters' selectionWeight > 0 branch and lerps
        // toward a stale base. Both are cleared, in that order, mirroring
        // ClumperPostOwnershipAuthority.
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;

        if (viewer != null)
        {
            MethodInfo clearHotspot = typeof(ModelViewer).GetMethod("ClearSelectionHotspot", flags);
            if (clearHotspot != null) clearHotspot.Invoke(viewer, null);

            FieldInfo selectionModeField = typeof(ModelViewer).GetField("isSelectionMode", flags);
            if (selectionModeField != null) selectionModeField.SetValue(viewer, false);
        }

        PostAffectorManager posts = FindFirstObjectByType<PostAffectorManager>();
        if (posts != null) posts.ReleasePostSelection();
    }

    // A selected guide takes over the whole right-hand panel - ClumperControlsScrollFix hides
    // every groom row behind the modifier host - so there MUST always be a way back out. There
    // was not: none of the universal exit guards know about guides. ModifierEmptySpaceExitAuthority
    // classifies only POST and clumper, ClumperSelectionExitAuthority returns early unless a POST
    // is active, and NewGroupRootSelectionAuthority is clumper-only. The result was a guide that
    // could only be dismissed by deleting it or by selecting a clumper, while its panel followed
    // the user to other groups and kept editing the original group's guide behind their back.
    //
    // Two exits here, plus the DONE button in the controls: switching group, and ESC.
    void MaintainSelectionExit()
    {
        if (selectedGuideId < 0) return;

        if (viewer != null && viewer.currentGroupId != selectedGroup)
        {
            ClearSelection();
            return;
        }

        // ESC is not consumed by whoever reads it first, so an armed +GUIDE/+POST placement and
        // this both see the same press. Cancelling a placement must not also close the panel of
        // the guide being tuned - the placement owns ESC while it is armed.
        if (GroupAddButtonPlacementAuthority.ArmedKind != GroupAddButtonPlacementAuthority.AddKind.None) return;
        if (Keyboard.current == null) return;
        if (!Keyboard.current.escapeKey.wasPressedThisFrame) return;
        ClearSelection();
    }

    // Scoped to the guide's OWN group. A POST active somewhere else in the scene is not a reason
    // this guide cannot be moved - the POST's own SPACE handlers only act on their group, so
    // standing down scene-wide would leave the guide unmovable for no reason.
    static bool IsPostActiveOnGroup(int groupId)
    {
        PostAffectorManager posts = FindFirstObjectByType<PostAffectorManager>();
        if (posts == null) return false;

        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        FieldInfo idField = typeof(PostAffectorManager).GetField("activeId", flags);
        FieldInfo groupField = typeof(PostAffectorManager).GetField("activeGroup", flags);
        if (idField == null || groupField == null) return false;

        object idValue = idField.GetValue(posts);
        object groupValue = groupField.GetValue(posts);
        if (!(idValue is int id) || id < 0) return false;
        if (!(groupValue is int group)) return false;
        return group == groupId;
    }

    // ---------------------------------------------------------------------------------
    // Left panel rows
    // ---------------------------------------------------------------------------------

    static string RowName(GuideCurve guide)
    {
        return "GuideCurve_" + guide.groupId + "_" + guide.id;
    }

    void EnsureRows()
    {
        RectTransform[] all = FindObjectsByType<RectTransform>(FindObjectsSortMode.None);
        HashSet<int> liveGroups = new HashSet<int>();

        foreach (RectTransform groupItem in all)
        {
            if (groupItem == null) continue;
            if (!groupItem.name.StartsWith("GroupItem_", StringComparison.Ordinal)) continue;

            int gid;
            if (!int.TryParse(groupItem.name.Substring("GroupItem_".Length), out gid)) continue;
            Transform parent = groupItem.parent;
            if (parent == null) continue;

            liveGroups.Add(gid);
            List<GuideCurve> guides = GetGroupGuides(gid);
            HashSet<string> wanted = new HashSet<string>(guides.Select(g => RowName(g)));

            for (int i = parent.childCount - 1; i >= 0; i--)
            {
                Transform child = parent.GetChild(i);
                if (!child.name.StartsWith("GuideCurve_" + gid + "_", StringComparison.Ordinal)) continue;
                if (!wanted.Contains(child.name)) Destroy(child.gameObject);
            }

            // After this group's POST rows and its CLUMPER rows, same convention those two
            // already follow, so each group's block reads header -> POST -> CLUMP -> GUIDE.
            int insert = groupItem.GetSiblingIndex() + 1;
            while (insert < parent.childCount)
            {
                string childName = parent.GetChild(insert).name;
                bool ownedByThisGroup =
                    childName.StartsWith("PostAffector_" + gid + "_", StringComparison.Ordinal) ||
                    childName.StartsWith("GroupClumper_" + gid + "_", StringComparison.Ordinal);
                if (!ownedByThisGroup) break;
                insert++;
            }

            foreach (GuideCurve guide in guides.OrderBy(g => g.id))
            {
                Transform row = parent.Find(RowName(guide));
                if (row == null) row = BuildRow(parent, guide).transform;
                row.SetSiblingIndex(Mathf.Min(insert++, parent.childCount - 1));

                Image image = row.GetComponent<Image>();
                if (image == null) continue;
                if (selectedGuideId == guide.id) image.color = new Color(.30f, .24f, .40f, .98f);
                else image.color = new Color(.18f, .14f, .24f, .98f);
            }
        }

        PurgeDeletedGroups(liveGroups);
    }

    // ModelViewer.GetNextAvailableGroupId hands deleted ids straight back out, so a guide left
    // behind by a deleted group would silently reattach itself to whatever new group inherits
    // the number - and, worse, keep deforming cards in it. The empty check matters: the panel
    // legitimately has no group rows before a model is loaded, and reading that as "every group
    // was deleted" would wipe every guide on the way in.
    void PurgeDeletedGroups(HashSet<int> liveGroups)
    {
        if (liveGroups == null || liveGroups.Count == 0) return;

        foreach (int gid in byGroup.Keys.Where(g => !liveGroups.Contains(g)).ToArray())
        {
            List<GuideCurve> dead;
            if (byGroup.TryGetValue(gid, out dead) && dead != null)
            {
                foreach (GuideCurve guide in dead)
                {
                    if (guide == null) continue;
                    if (selectedGuideId != guide.id) continue;
                    selectedGuideId = -1;
                    selectedGroup = -1;
                    DestroyControls();
                }
            }
            byGroup.Remove(gid);
        }
    }

    GameObject BuildRow(Transform parent, GuideCurve guide)
    {
        GameObject row = new GameObject(RowName(guide), typeof(RectTransform), typeof(Image), typeof(HorizontalLayoutGroup));
        row.transform.SetParent(parent, false);
        row.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 34f);
        row.GetComponent<Image>().color = new Color(.18f, .14f, .24f, .98f);

        HorizontalLayoutGroup layout = row.GetComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(6, 6, 4, 4);
        layout.spacing = 5f;
        layout.childControlWidth = false;
        layout.childControlHeight = true;

        int capturedGroup = guide.groupId;
        int capturedId = guide.id;

        GameObject select = AddButton(row.transform, "GUIDE " + guide.id, 118f);
        select.GetComponent<Button>().onClick.AddListener(delegate { SelectGuide(capturedGroup, capturedId); });

        // The swatch replaces a "CURVE" label that said the same thing about every row. With
        // several guides on one group the useful thing to show here is which one is which, and
        // the colour is the only per-guide thing there is room for.
        //
        // A plain Image, no Button: the hue is set from the slider in the right panel, where it
        // sits with the guide's other properties. Making the swatch itself cycle a palette was
        // the alternative and gives up the full range for a shorter path to eight fixed colours.
        // No LayoutElement: this row's HorizontalLayoutGroup has childControlWidth false, so it
        // reads each child's own rect and ignores LayoutElement entirely - the AddButton siblings
        // carry none either.
        GameObject swatchGO = new GameObject("ColourSwatch", typeof(RectTransform), typeof(Image), typeof(GuideRowSwatch));
        swatchGO.transform.SetParent(row.transform, false);
        swatchGO.GetComponent<RectTransform>().sizeDelta = new Vector2(88f, 18f);
        Image swatchImage = swatchGO.GetComponent<Image>();
        swatchImage.raycastTarget = false;

        // Painted here as well as by the component, or the row flashes white for the one frame
        // between building it and GuideRowSwatch's first Update.
        swatchImage.color = CurveColor(guide, 1f);
        swatchGO.GetComponent<GuideRowSwatch>().Bind(this, capturedId);

        GameObject remove = AddButton(row.transform, "DEL", 40f);
        remove.GetComponent<Button>().onClick.AddListener(delegate { RemoveGuide(FindGuide(capturedId)); });
        return row;
    }

    // ---------------------------------------------------------------------------------
    // Right panel controls
    // ---------------------------------------------------------------------------------

    void MaintainControls()
    {
        GuideCurve guide = GetSelectedGuide();
        if (guide == null || viewer == null || viewer.groomingSliderPanelGO == null)
        {
            DestroyControls();
            return;
        }
        if (controlsRoot != null) return;

        controlsRoot = new GameObject("GuideControls", typeof(RectTransform), typeof(VerticalLayoutGroup));
        controlsRoot.transform.SetParent(viewer.groomingSliderPanelGO.transform, false);
        VerticalLayoutGroup layout = controlsRoot.GetComponent<VerticalLayoutGroup>();
        layout.spacing = 6f;
        layout.childControlWidth = true;
        layout.childControlHeight = false;

        AddHeader(controlsRoot.transform, "GUIDE " + guide.id);
        AddSlider(controlsRoot.transform, "Guide Amount", 0f, 1f, guide.amount, v => guide.amount = v);
        AddSlider(controlsRoot.transform, "Radius", .001f, .25f, guide.radius, v => guide.radius = v);
        AddSlider(controlsRoot.transform, "Falloff", 0f, .25f, guide.falloff, v => guide.falloff = v);

        // Colour, as a hue. One slider rather than three because saturation and value are fixed -
        // see DefaultGuideHue - so there is exactly one degree of freedom to expose. The row's
        // swatch follows it live, and the curve and its rings repaint the same frame.
        AddSlider(controlsRoot.transform, "Colour", 0f, 1f, guide.hue, v => guide.hue = v);

        GameObject done = AddButton(controlsRoot.transform, "DONE", 120f);
        done.GetComponent<Button>().onClick.AddListener(ClearSelection);

        AddHint(controlsRoot.transform, "Drag the handles to shape the curve");
        AddHint(controlsRoot.transform, "Drag the ROOT ring to re-aim it from a new spot");
        AddHint(controlsRoot.transform, "CTRL + SHIFT + CLICK on the curve adds a point, up to " +
                                        (MaxGuideNodes + 1));
        AddHint(controlsRoot.transform, "CTRL + SHIFT + RIGHT CLICK a point removes it (not the tip)");
        AddHint(controlsRoot.transform, "SPACE + CLICK moves this guide, keeping its shape");
        AddHint(controlsRoot.transform, "Card placing is OFF while a guide is selected");
        AddHint(controlsRoot.transform, "Colour tells overlapping guides apart - it is saved");
        AddHint(controlsRoot.transform, "DONE, ESC, empty space or another group closes this");
    }

    void DestroyControls()
    {
        if (controlsRoot != null) Destroy(controlsRoot);
        controlsRoot = null;
    }

    void AddSlider(Transform parent, string label, float min, float max, float value, UnityEngine.Events.UnityAction<float> changed)
    {
        if (createSliderMethod == null) return;
        object[] args = { parent, label, min, max, value, changed, null, 44f, 15 };
        createSliderMethod.Invoke(viewer, args);
    }

    void AddHeader(Transform parent, string text)
    {
        TextMeshProUGUI tmp = AddText(parent, text, 16, 0f);
        tmp.fontStyle = FontStyles.Bold;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.rectTransform.sizeDelta = new Vector2(0, 24);
    }

    void AddHint(Transform parent, string text)
    {
        TextMeshProUGUI tmp = AddText(parent, text, 11, 0f);
        tmp.color = new Color(.80f, .76f, .88f);
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.rectTransform.sizeDelta = new Vector2(0, 22);
    }

    GameObject AddButton(Transform parent, string text, float width)
    {
        GameObject go = new GameObject(text, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(width, 25f);
        go.GetComponent<Image>().color = new Color(.26f, .22f, .34f);
        TextMeshProUGUI t = AddText(go.transform, text, 10, width);
        RectTransform tr = t.rectTransform;
        tr.anchorMin = Vector2.zero;
        tr.anchorMax = Vector2.one;
        tr.offsetMin = Vector2.zero;
        tr.offsetMax = Vector2.zero;
        return go;
    }

    TextMeshProUGUI AddText(Transform parent, string text, int size, float width)
    {
        GameObject go = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(width, 22f);
        TextMeshProUGUI tmp = go.GetComponent<TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = size;
        tmp.color = new Color(.90f, .88f, .95f);
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.textWrappingMode = TextWrappingModes.NoWrap;
        return tmp;
    }
}

// ------------------------------------------------------------------------------------
// The deformation itself.
//
// Deliberately a static helper rather than an authority: it is called from inside
// ThreeColumnClumperMeshAuthority's clean reconstruction, so guides and clumps share ONE mesh
// write, one dirty-check and one override lifecycle. Two authorities both calling GetLiveMesh()
// and MarkExternalClumpOverride() would fight, and the loser's work would vanish on alternate
// frames - which is the failure mode already documented in
// claude/clumper-post-frozen-mesh-root-cause.md.
// ------------------------------------------------------------------------------------
public static class GuideDeformation
{
    public class ActiveGuide
    {
        public GuideCurveManager.GuideCurve curve;
        public GuideCurveManager.GuidePath path;
    }

    // NO island scoping. A guide's reach is its zone - radius plus falloff - and nothing else.
    //
    // Island scoping was written and then taken back out, and the reason is worth keeping. The
    // clumper's version is opt-in behind a CONTIGUOUS flag whose only working control lives
    // inside ClumperControls, which a guide-only group can never open - so a guide had to either
    // hard-code the scoping on or read a flag it could not set, and both were worse than going
    // without.
    //
    // Reading the flag meant deleting a group's last clumper silently expanded every guide in it
    // to the whole group, because ReleaseEditOwnership clears the flag - hair jumping with
    // nothing in the UI having changed. Hard-coding it on put
    // SurfaceIslandScope.TryGetIslandAtWorldPoint in charge of whether a guide worked at all: a
    // 25mm probe on a model auto-scaled to roughly 0.33 units wide easily spans two nearby
    // shells, so a guide near a hairline could resolve to the skull rather than the scalp cap and
    // reject every card - rings drawn, Amount at 1.0, nothing happening, nothing to diagnose it
    // by. And an unresolvable contact left the group re-evaluating every card every frame,
    // forever, because the frame could never be cached.
    //
    // A guide that reaches slightly across an island boundary is visible, and fixable by pulling
    // the zone in. A guide that silently does nothing is neither. If this wants scoping later it
    // wants its own toggle in the guide panel, not a borrowed one.
    public static List<ActiveGuide> Resolve(List<GuideCurveManager.GuideCurve> guides)
    {
        List<ActiveGuide> result = new List<ActiveGuide>();
        if (guides == null) return result;

        foreach (GuideCurveManager.GuideCurve guide in guides)
        {
            if (guide == null) continue;
            if (guide.amount <= .0001f) continue;

            GuideCurveManager.GuidePath path = GuideCurveManager.BuildPath(guide);
            if (path == null) continue;

            // A guide with no length has no shape to give, and it is not harmless. Every arc
            // position past zero takes SampleByLength's past-the-end branch, so the whole zone
            // resolves to root plus exitTangent times arc: every card in reach combed dead
            // straight along one direction, at full length, at full strength. BuildPath always
            // finds SOME exit tangent - last chord, then the contact normal, then up - so there
            // is no degenerate case to notice it by, just a zone of spikes.
            //
            // It takes the contact and every node landing on each other to reach, which the
            // height clamp makes unlikely, but a groom full of spikes is a bad way to find out.
            if (path.totalLength <= .00001f) continue;

            result.Add(new ActiveGuide { curve = guide, path = path });
        }
        return result;
    }

    // Grown on demand and reused. Apply runs per card per evaluation, and a fresh float[] and
    // Vector3[] each time is real garbage on a groom with thousands of cards - the same cost
    // ThreeColumnClumperMeshAuthority's signature comment describes hunting down elsewhere.
    private static float[] arcScratch = new float[0];
    private static Vector3[] worldScratch = new Vector3[0];
    private static float[] weightScratch = new float[0];
    private static Vector3[] blendScratch = new Vector3[0];
    private static Vector3[] outScratch = new Vector3[0];

    // How far along the card the guide reaches full strength, as a fraction of its length, and
    // the fewest segments that fraction is allowed to be worth. See RampAlong.
    private const float RampKnee = .35f;
    private const int KneeMinSegments = 2;

    // Displaces one card's spine and vertices onto the guides' SHAPE.
    //
    // Both arrays are rebuilt together, and that matters: the spine is what the clumper then
    // reads for its own leader/follower anchors, so guiding it here is what makes the two
    // modifiers compose rather than overwrite each other. Leaving the spine behind would have
    // the clumper gathering strands toward where they used to be.
    //
    // Three passes, and they are separable on purpose. The first decides WHERE the line runs -
    // a ramped position blend from the card's own shape onto the guide's. The second decides
    // where the card sits ALONG that line, at its own segment lengths, so being guided never
    // changes how long the hair is. The third holds the root out of the scalp and carries the
    // cross-sections onto the new spine.
    public static void Apply(HairCard card, List<ActiveGuide> guides, Vector3[] spine, Vector3[] vertices)
    {
        if (card == null || guides == null || guides.Count == 0) return;
        if (spine == null || spine.Length < 2 || vertices == null) return;

        int columns = HairCard.CrossSectionColumns;
        int rows = spine.Length;
        if (vertices.Length < rows * columns) return;

        if (arcScratch.Length < rows) arcScratch = new float[rows];
        if (worldScratch.Length < rows) worldScratch = new Vector3[rows];
        if (blendScratch.Length < rows) blendScratch = new Vector3[rows];
        if (outScratch.Length < rows) outScratch = new Vector3[rows];

        // Arc length along the card's OWN spine, measured in WORLD space.
        //
        // The spine is local and the guide path is world, so measuring the local distance would
        // silently read the guide at the wrong position the moment a card sits under any
        // non-unit scale. Cards are unparented today and nothing writes localScale on them, so
        // this is belt and braces - but it is one TransformPoint per row against a whole class
        // of bug that would present as "the guide works but is the wrong size".
        //
        // Using the spine rather than card.length also means a BENT card reads the guide at the
        // distance it has actually travelled, not its nominal length. Not a curled or waved one,
        // though the comment here used to say so: curl and wave are cross-section offsets added
        // on top of the spine, never into it, so a tight coil and a straight card of the same
        // bend read the guide at identical arc positions.
        for (int i = 0; i < rows; i++) worldScratch[i] = card.transform.TransformPoint(spine[i]);

        arcScratch[0] = 0f;
        for (int i = 1; i < rows; i++)
        {
            arcScratch[i] = arcScratch[i - 1] + Vector3.Distance(worldScratch[i], worldScratch[i - 1]);
        }

        Vector3 rootWorld = worldScratch[0];
        float totalArc = arcScratch[rows - 1];

        // Where the ramp reaches full strength, in world arc length. See RampAlong for why it
        // is a knee at all; the Max is what holds it open for at least two segments.
        //
        // A card can have as few as one segment and a groom routinely has cards at four or five,
        // and on a four row card row 1 already sits a third of the way up - past the knee, at
        // full guide strength, with nothing tethered. Measured on a four row card, a knee at a
        // flat 35% turned the root 13.6 degrees off its own direction where a sixty row card
        // under the same guide turned 6.7; holding the knee open for at least two segments
        // brings it to 7.0. The tether stops depending on Segment count, which is a setting
        // about silhouette and should not quietly be a setting about roots.
        float knee = Mathf.Max(RampKnee * totalArc, arcScratch[Mathf.Min(KneeMinSegments, rows - 1)]);

        // Resolved ONCE per card, not once per row. ZoneWeight is card-and-guide constant - it
        // measures the card's spawn point against the guide's contact - so evaluating it per
        // spine row was up to sixty redundant evaluations per card per frame, on a groom that is
        // already rebuilding meshes.
        if (weightScratch.Length < guides.Count) weightScratch = new float[guides.Count];

        float strongest = 0f;
        for (int g = 0; g < guides.Count; g++)
        {
            ActiveGuide active = guides[g];
            float w = Mathf.Clamp01(active.curve.amount) * ZoneWeight(card, active.curve);
            weightScratch[g] = w;
            if (w > strongest) strongest = w;
        }

        if (strongest <= .0001f) return;

        // ------------------------------------------------------------------ where the line runs
        //
        // POSITIONS, not directions. The card is moved ONTO the guide's shape rather than merely
        // pointed the same way, and that distinction is the whole of this method's history.
        //
        // Blending tangents and stepping the card's own segment lengths along them preserves
        // length perfectly and produces a card that mimics the guide and never touches it:
        // measured against the curve the guide describes, the strand sat an average of
        // thirty-six millimetres away from it along its whole length. It copied the gesture and
        // went somewhere else. What a guide has to do is be the line the hair lies on.
        //
        // Measured the same way on the spine this actually writes - after the resample and the
        // lift below, not on the blend - a card of four segments or more at full Amount now sits
        // within two tenths of a millimetre of the guide past the knee, and most of them within
        // a twentieth. A one segment card cannot follow a curve and does not pretend to.
        //
        // Row 0 is skipped: at arc length zero the target IS the root - SampleByLength(0) returns
        // origin exactly - so it could not move anyway, and hair that slides out of the scalp
        // when a modifier is raised is the one artifact nobody forgives.
        blendScratch[0] = spine[0];

        for (int i = 1; i < rows; i++)
        {
            Vector3 weightedTarget = Vector3.zero;
            float weightSum = 0f;

            for (int g = 0; g < guides.Count; g++)
            {
                float w = weightScratch[g];
                if (w <= .0001f) continue;
                ActiveGuide active = guides[g];

                // SHAPE, NOT POSITION. The card keeps its own root and adopts the guide's
                // curvature relative to the guide's own root, so a zone of cards combs parallel
                // instead of collapsing onto one line. Collapsing onto a line is clumping, and
                // the CLUMPER already does it - keeping them separate is what lets a groom be
                // guided and clumped at once instead of having to choose.
                Vector3 targetWorld = rootWorld + (active.path.SampleByLength(arcScratch[i]) - active.path.origin);
                weightedTarget += card.transform.InverseTransformPoint(targetWorld) * w;
                weightSum += w;
            }

            if (weightSum <= .0001f)
            {
                blendScratch[i] = spine[i];
                continue;
            }

            // WHERE to go is the weighted average of the guides' targets. HOW FAR is the
            // STRONGEST single weight, not their sum.
            //
            // Summing conflates "how many guides reach this card" with "how committed they are":
            // two copies of the same guide at Amount 0.5 would snap a card fully onto a shape
            // that neither of them asked to be more than half-followed, so raising a second
            // guide's Amount would silently double the first's effect. Taking the max keeps
            // Amount meaning the same thing whether one guide overlaps a card or five do, and
            // still reduces to plain Amount for the single-guide case.
            Vector3 blendedTarget = weightedTarget / weightSum;

            blendScratch[i] = Vector3.Lerp(spine[i], blendedTarget, strongest * RampAlong(arcScratch[i], knee));
        }

        // -------------------------------------------------------------- and how far along it is
        //
        // Walked back out at the card's OWN segment lengths, which is what stops a guide changing
        // how long the hair is.
        //
        // The blended line is not the card's length and cannot be relied on to be either longer
        // or shorter than it. Past the knee it is the guide's chord over the card's arc
        // intervals, so it comes up SHORT wherever the guide is more curved than the card is;
        // through the knee it crosses from one curve to the other, and that transverse travel
        // ADDS length. Measured across three thousand random card and guide pairs the raw blend
        // ran from a third short to well over long, both visible - hair that grows or shrinks
        // when a guide is raised is not a trade worth making for anything.
        //
        // So the blend decides the LINE and this decides where the card sits along it: the
        // strand lies on the guide and reaches exactly as far as its own length allows, which is
        // a shorter way along the guide than the guide itself goes. Correct, and the only answer
        // that leaves Length meaning what it says on the slider.
        ResampleToOwnLengths(blendScratch, spine, outScratch, rows);

        // The FIRST segment, and only the first, is held above the scalp it grows out of.
        //
        // A guide cannot point down at its own root - its nodes are clamped above the contact
        // plane. It can point down at somebody else's: ZoneWeight measures distance to the
        // contact and knows nothing about normals, and with the radius slider reaching .25 on a
        // head about .33 across, a card at full weight can sit on a part of the scalp facing a
        // very different way.
        //
        // AFTER the resample, not before it. Lifting a vertex of the blended line and then
        // walking that line does not hold: the walk consumes the lifted segment at the card's
        // own segment length rather than the blend's, runs on into the next segment - which was
        // never lifted - and the root ends up back under the surface. Across four thousand
        // random cards, lifting before the resample left thirty-one roots breached, the worst of
        // them pointing almost straight into the mesh; lifting after left none.
        //
        // The rest of the card is carried by the same offset rather than left where it was, so
        // the only thing that changes is the angle the strand leaves at. Bending just the root
        // segment would put a kink at row 1 that no guide asked for, and because the tail moves
        // rigidly every segment keeps the exact length the resample just gave it.
        Vector3 firstSegment = outScratch[1] - outScratch[0];
        float firstLength = firstSegment.magnitude;
        if (firstLength > .0000001f)
        {
            Vector3 ownFirst = spine[1] - spine[0];
            if (ownFirst.sqrMagnitude > .0000000001f)
            {
                Vector3 lifted = LiftAboveSurface(card, firstSegment / firstLength, ownFirst.normalized);
                Vector3 shift = outScratch[0] + lifted * firstLength - outScratch[1];
                for (int i = 1; i < rows; i++) outScratch[i] += shift;
            }
        }

        // ------------------------------------------------------------------------ write it back
        Vector3 previousOriginal = spine[0];
        Vector3 previousMoved = outScratch[0];
        // Local +Z, because that is the axis BuildSegmentFrames grows a card along. Only ever
        // read if row 1 sits exactly on row 0, where the turn works out to identity either way -
        // but a seed that is the right axis costs nothing and stays right if the two degeneracy
        // thresholds below are ever allowed to disagree.
        Vector3 previousDirection = Vector3.forward;

        for (int i = 1; i < rows; i++)
        {
            // Saved before spine[i] is overwritten: the NEXT row measures its own segment against
            // this one, and the cross-section offsets are measured from it too.
            Vector3 original = spine[i];

            Vector3 ownSegment = original - previousOriginal;
            Vector3 ownDirection = previousDirection;
            if (ownSegment.sqrMagnitude > .0000000001f) ownDirection = ownSegment.normalized;

            Vector3 movedSegment = outScratch[i] - previousMoved;
            Vector3 movedDirection = ownDirection;
            if (movedSegment.sqrMagnitude > .0000000001f) movedDirection = movedSegment.normalized;

            spine[i] = outScratch[i];

            int index = i * columns;

            // The cross-section turns with the segment it sits on.
            //
            // Without this the ribbon keeps the facing it was built with while its spine points
            // somewhere else, so a card combed across its own width axis shears into a sliver and
            // RecalculateNormals lights it from the wrong side.
            //
            // FromToRotation, so the ribbon is carried by the shortest rotation onto the new
            // tangent and picks up no roll of its own. Roll along the card is TWIST's job, and it
            // is already baked into the offsets this rotates. Built per row from that row's own
            // direction, never composed down the chain, so sixty segments accumulate exactly as
            // much roll as two do: none.
            Quaternion turn;
            if (Vector3.Dot(ownDirection, movedDirection) > -.9999f)
            {
                turn = Quaternion.FromToRotation(ownDirection, movedDirection);
            }
            else
            {
                // Doubled back on itself, where FromToRotation has no shortest answer to give -
                // every axis perpendicular to the segment is equally correct, so Unity picks one
                // and the cross-section cartwheels a different way each frame as the angle
                // wanders across the reversal. HairCard and the guide handle drawing both refuse
                // this same case; here the cross-section's own offset - half-width, plus whatever
                // curl and wave have added to it - is the natural axis, since turning about it
                // flips the card over in its own plane rather than edge-on.
                Vector3 across = vertices[index] - original;
                across -= ownDirection * Vector3.Dot(across, ownDirection);
                if (across.sqrMagnitude < .0000000001f) across = Vector3.Cross(ownDirection, Vector3.up);
                if (across.sqrMagnitude < .0000000001f) across = Vector3.Cross(ownDirection, Vector3.right);
                turn = Quaternion.AngleAxis(180f, across.normalized);
            }

            vertices[index] = spine[i] + turn * (vertices[index] - original);
            vertices[index + 1] = spine[i] + turn * (vertices[index + 1] - original);
            vertices[index + 2] = spine[i] + turn * (vertices[index + 2] - original);

            // The root row's own cross-section, turned by the first segment's rotation. Its spine
            // point stays exactly where it was - only the facing changes - so the scalp anchor
            // holds while the ribbon leaves it without a kink.
            if (i == 1)
            {
                vertices[0] = spine[0] + turn * (vertices[0] - spine[0]);
                vertices[1] = spine[0] + turn * (vertices[1] - spine[0]);
                vertices[2] = spine[0] + turn * (vertices[2] - spine[0]);
            }

            previousOriginal = original;
            previousMoved = outScratch[i];
            previousDirection = movedDirection;
        }
    }

    // How much of the guide a row this far along the card takes: 0 at the root, 1 at the knee.
    //
    // THREE arrangements have stood here and the differences matter. A smoothstep over the WHOLE
    // length, on a position lerp whose target already converges on the root as O(t), compounded
    // into a t-cubed falloff: a fifth of the way up a card the guide had two percent of the say
    // it had at the tip, and guides read as combing only the top half of a groom. No ramp at all
    // handed the base the guide's exact WORLD direction, and the scalp is curved, so that one
    // direction is a different angle to every card's own scalp - nearly thirty degrees of spread
    // across a zone, some cards standing up and others lying flat from a guide that meant one
    // thing. A plain linear ramp tethers the base properly but never arrives: the strand is still
    // eighteen millimetres off the guide at the far end of the card.
    //
    // A knee gets all three. Zero at the root, so a card leaves along its own direction exactly
    // as it would with no guide at all - planted, and uniform across the zone whatever the scalp
    // is doing under it. Full by a third of the way up, so the rest of the strand lies ON the
    // guide rather than near it - fractions of a millimetre past the knee, where the tangent
    // blend this replaces sat at thirty-six and a half. Apply's own comment has the numbers.
    //
    // In ARC LENGTH, not row index. Segment Density decides where rows sit, so a root-heavy curve
    // packs half of them into the bottom fifth of the card, and a row-index ramp would be at full
    // strength a fifth of the way up - handing the base the whole guide again, under exactly the
    // setting somebody reaches for when they want a smooth root.
    static float RampAlong(float arc, float knee)
    {
        if (knee <= .000001f) return 1f;
        float k = Mathf.Clamp01(arc / knee);
        return k * k * (3f - 2f * k);
    }

    // Lays down `rows` points along `path` whose consecutive STRAIGHT-LINE distances are exactly
    // `lengthSource`'s, so a guided card follows the blended line without its length changing.
    //
    // A bead threaded on a wire, pulled taut: each point is where the wire first leaves a sphere
    // of the next segment's length centred on the point before it. Straight-line distance and
    // not distance along the wire, because the mesh is built from chords and a chord across a
    // bend is shorter than the bend - measuring along the wire and stepping straight took six
    // percent off a short card over a hard S curve, and stepping the walked directions instead
    // fixed the length but let the error accumulate forward: across four thousand random cards
    // the strand strayed eleven millimetres from the line at the ninety-ninth percentile, where
    // this stays inside a quarter of one.
    static void ResampleToOwnLengths(Vector3[] path, Vector3[] lengthSource, Vector3[] into, int rows)
    {
        // Where the wire continues once it reaches the end, taken from the last stretch of it
        // that HAS a direction. Scanning back rather than assuming the final segment does: a
        // guide whose last sampled span is flat leaves path[rows-1] sitting on path[rows-2], and
        // an unusable run-out direction would strand every remaining row on one point - a card
        // visibly cut short, with a degenerate tip, and nothing in the UI to explain it.
        //
        // The run-out is not the exception it looks like. Past the knee the blended line is the
        // guide's CHORDS over the card's arc intervals, so any guide that curves harder than the
        // card runs the wire out before the card is finished. Carrying straight on is also what
        // SampleByLength does at the end of a guide, so a card longer than the line it follows
        // behaves the same whichever of the two runs out first.
        Vector3 runOut = Vector3.zero;
        for (int j = rows - 2; j >= 0; j--)
        {
            Vector3 span = path[rows - 1] - path[j];
            if (span.sqrMagnitude > .0000000001f)
            {
                runOut = span.normalized;
                break;
            }
        }

        into[0] = path[0];

        Vector3 cursor = path[0];
        int segment = 1;

        for (int i = 1; i < rows; i++)
        {
            float radius = Vector3.Distance(lengthSource[i], lengthSource[i - 1]);
            Vector3 centre = into[i - 1];

            // The cursor sits ON the previous point, so every crossing test starts from inside
            // the sphere and the FIRST exit is the answer. Not the only one - the blended line
            // is a per-row lerp toward independently sampled targets and can double back through
            // the sphere as often as it likes - but a straight segment leaving an inside point
            // crosses a sphere once, so stopping at the first segment that ends outside is right
            // whatever the wire does afterwards.
            bool placed = false;
            while (segment < rows)
            {
                float t = SphereExit(cursor, path[segment], centre, radius);
                if (t >= 0f)
                {
                    cursor += (path[segment] - cursor) * t;
                    placed = true;
                    break;
                }

                cursor = path[segment];
                segment++;
            }

            if (!placed)
            {
                Vector3 direction = runOut;

                // No part of the wire has a direction: the whole blended line has collapsed to a
                // point. Following it would stack every row of the card on its root - one hair
                // turning into one vertex, which is a far worse answer than ignoring the guide.
                // The card walks out along its OWN shape instead, so a guide that degenerates
                // does nothing rather than something catastrophic.
                if (direction.sqrMagnitude < .0000000001f)
                {
                    Vector3 own = lengthSource[i] - lengthSource[i - 1];
                    if (own.sqrMagnitude > .0000000001f) direction = own.normalized;
                }

                // The ray is cut to the furthest the crossing can possibly be, which is what
                // makes this exact rather than a guess. The walk only ever steps over a segment
                // that ENDS inside the sphere, so wherever it gave up it is still inside, and a
                // ray leaving a point inside a sphere clears it within the distance to the
                // centre plus the radius.
                //
                // Sizing the ray by the card's total length instead - which read as obviously
                // generous - was wrong: the reach is measured from the cursor, not the centre,
                // and one segment carrying most of a card's length needs nearly twice its own
                // radius. Fuzzing found it on about one card in a hundred built from a Segment
                // Density curve with a flat region, and the row it missed jumped eight tenths
                // of the card's length. Lengths still came out right, so it would have shipped
                // as an unexplained kink.
                float reach = Vector3.Distance(cursor, centre) + radius;
                float t = SphereExit(cursor, cursor + direction * reach, centre, radius);
                if (t >= 0f) cursor += direction * (reach * t);
                else cursor = centre + direction * radius;
            }

            into[i] = cursor;
        }
    }

    // Where the segment a->b leaves the sphere of radius `radius` about `centre`, as a fraction
    // of the segment, or -1 if it does not. The far root, since the walk always starts inside.
    static float SphereExit(Vector3 a, Vector3 b, Vector3 centre, float radius)
    {
        Vector3 d = b - a;
        float dd = Vector3.Dot(d, d);
        if (dd <= .0000000001f) return -1f;

        Vector3 offset = a - centre;
        float half = Vector3.Dot(offset, d);
        float outside = Vector3.Dot(offset, offset) - radius * radius;

        float discriminant = half * half - dd * outside;
        if (discriminant < 0f) return -1f;

        float t = (-half + Mathf.Sqrt(discriminant)) / dd;

        // No tolerance either side, and none is wanted. A crossing that rounds a hair past this
        // segment's end is not lost by refusing it: the walk steps to that end, and the next
        // segment starts a hair INSIDE the sphere and hands back a crossing a hair along itself -
        // the same point, still at exactly `radius`. Widening the test instead, and clamping,
        // would place the point on the shared vertex rather than at the radius, and that is a
        // length error rather than a rounding one. Small - a hundredth of a percent of a card -
        // but Length is a slider somebody typed a number into.
        //
        // The near end cannot be crossed at all: `a` is always inside, so `outside` is negative
        // and the far root is always positive. It is tested because a bound that only holds by
        // argument is worth one comparison.
        if (t < 0f || t > 1f) return -1f;
        return t;
    }

    // Keeps a direction on the outside of the surface the card is planted in.
    //
    // A shallow floor rather than the plane itself. Exactly along the surface is still a strand
    // lying flat on the scalp with its whole length z-fighting the mesh, and the guide handles
    // use the same .002 idea for the same reason - so the root is asked to clear the surface by
    // a couple of degrees rather than merely not to breach it.
    //
    // The rotation is the minimum one that gets there: the direction is pushed along the normal
    // and re-normalized, which slides it up the cone rather than swinging it to some unrelated
    // heading. A card whose normal is unknown - nothing has planted it yet - is left alone.
    static Vector3 LiftAboveSurface(HairCard card, Vector3 localDirection, Vector3 ownDirection)
    {
        Vector3 normal = card.GetSurfaceNormal();
        if (normal.sqrMagnitude < .000001f) return localDirection;

        // Both sides in the card's own space, which is the space the spine is built in.
        Vector3 localNormal = card.transform.InverseTransformDirection(normal.normalized).normalized;

        // The floor is whichever is LOWER: the two degrees a guide may not push a root below, or
        // wherever the card's own unguided direction already sat. This stops the guide driving a
        // root into the scalp without ever raising one the card itself laid flat - a card bent
        // hard enough to graze its own surface is doing that on purpose, and snapping it up the
        // instant a guide came within range would be a jump with no cause the user could see.
        const float MinRise = .035f;
        float floor = Mathf.Min(MinRise, Vector3.Dot(ownDirection, localNormal));

        float rise = Vector3.Dot(localDirection, localNormal);
        if (rise >= floor) return localDirection;

        Vector3 lifted = localDirection + localNormal * (floor - rise);
        if (lifted.sqrMagnitude < .000001f) return localNormal;
        return lifted.normalized;
    }

    // Same shape as ThreeColumnClumperMeshAuthority.ZoneWeight, measured from the card's spawn
    // point so a card is in or out of a guide's zone by where it was planted, not by wherever
    // some earlier modifier has since pushed its tip.
    static float ZoneWeight(HairCard card, GuideCurveManager.GuideCurve guide)
    {
        Vector3 root = card.GetSpawnHitPoint();
        if (root == Vector3.zero) root = card.transform.position;

        float d = Vector3.Distance(root, guide.contact);
        float radius = Mathf.Max(.001f, guide.radius);
        float outer = radius + Mathf.Max(0f, guide.falloff);
        if (d <= radius) return 1f;
        if (guide.falloff <= .000001f || d >= outer) return 0f;
        return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(outer, radius, d));
    }
}

// The colour chip on a guide's row in the left panel.
//
// Repaints itself rather than being repainted by EnsureRows, because the hue slider moves while
// the row is already built and EnsureRows only touches a row's background when the SELECTION
// changes. A row rebuilt for any other reason would otherwise carry the colour it was born with
// until something unrelated happened.
//
// Holds the guide's ID and looks the guide up, rather than holding the guide itself: guides are
// replaced wholesale by a project load (GuideCurveManager.ReplaceAll), so a captured reference
// would be to an object that is no longer in the manager and the chip would freeze.
public class GuideRowSwatch : MonoBehaviour
{
    private GuideCurveManager manager;
    private int guideId = -1;
    private Image image;
    private float lastHue = float.MinValue;

    public void Bind(GuideCurveManager owner, int id)
    {
        manager = owner;
        guideId = id;
        image = GetComponent<Image>();
        lastHue = float.MinValue;
    }

    private void Update()
    {
        if (manager == null || image == null) return;

        GuideCurveManager.GuideCurve guide = manager.FindGuidePublic(guideId);
        if (guide == null) return;

        // Only write when it actually moved. This runs every frame for every guide row on screen,
        // and assigning Image.color dirties the canvas whether or not the value differs.
        if (Mathf.Approximately(guide.hue, lastHue)) return;
        lastHue = guide.hue;

        image.color = GuideCurveManager.CurveColor(guide, 1f);
    }
}
