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
    public const float DefaultGuideRadius = .04f;
    public const float DefaultGuideFalloff = .04f;

    // How many points the arc-length table holds. The curve is a quadratic, so it is smooth
    // between samples; this only has to be fine enough that the cumulative-length approximation
    // does not visibly shorten the curve.
    private const int PathSamples = 32;

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

        // Offsets in that frame. Local +Y is the surface normal, so a guide with both offsets on
        // the Y axis stands straight out of the surface.
        public Vector3 midLocal = Vector3.up;
        public Vector3 endLocal = Vector3.up;

        // Starts at zero, exactly like a new clumper: a modifier that changes nothing until it
        // is asked to. Dropping a guide onto the model should never move hair by itself.
        [Range(0f, 1f)] public float amount = 0f;
        public float radius = DefaultGuideRadius;
        public float falloff = DefaultGuideFalloff;
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

    public static Vector3 WorldMid(GuideCurve guide)
    {
        if (guide == null) return Vector3.zero;
        return guide.contact + guide.frame * guide.midLocal;
    }

    public static Vector3 WorldEnd(GuideCurve guide)
    {
        if (guide == null) return Vector3.zero;
        return guide.contact + guide.frame * guide.endLocal;
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

        Vector3 p0 = guide.contact;
        Vector3 p1 = WorldMid(guide);
        Vector3 p2 = WorldEnd(guide);

        GuidePath path = new GuidePath();
        path.points = new Vector3[PathSamples];
        path.cumulative = new float[PathSamples];
        path.origin = p0;

        for (int i = 0; i < PathSamples; i++)
        {
            float t = (float)i / (PathSamples - 1);
            path.points[i] = Evaluate(p0, p1, p2, t);
            if (i == 0)
            {
                path.cumulative[i] = 0f;
                continue;
            }
            path.cumulative[i] = path.cumulative[i - 1] + Vector3.Distance(path.points[i], path.points[i - 1]);
        }

        path.totalLength = path.cumulative[PathSamples - 1];

        // The analytic derivative at the end, falling back to the last chord when the three
        // points are collinear-and-coincident enough that the derivative degenerates.
        Vector3 tangent = EvaluateTangent(p0, p1, p2, 1f);
        if (tangent.sqrMagnitude < .00000001f) tangent = path.points[PathSamples - 1] - path.points[PathSamples - 2];
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

    // Any guide at all, active or not - the preview draws inactive guides too, so it needs a
    // different question from HasActiveGuide.
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
            midLocal = new Vector3(0f, reach * .5f, 0f),
            endLocal = new Vector3(0f, reach, 0f),
            amount = 0f,
            radius = DefaultGuideRadius,
            falloff = DefaultGuideFalloff
        };

        list.Add(guide);
        SelectGuide(groupId, guide.id);
        return guide;
    }

    // Keeps midLocal/endLocal untouched, which IS the "keeps its general form" requirement -
    // they are frame-relative, so re-seating the contact and its normal carries the whole shape
    // to the new spot and re-aims it along the new surface.
    public bool MoveSelectedGuide(int groupId, Vector3 point, Vector3 normal)
    {
        GuideCurve guide = GetSelectedGuide();
        if (guide == null || guide.groupId != groupId) return false;

        guide.contact = point;

        // Minimal rotation from the old normal to the new one, applied to the frame the guide is
        // already carrying. No reference axis is involved, so there is no seam to cross.
        if (normal.sqrMagnitude > .000001f)
        {
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
        return true;
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

        AddText(row.transform, "CURVE", 10, 88f);

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

        GameObject done = AddButton(controlsRoot.transform, "DONE", 120f);
        done.GetComponent<Button>().onClick.AddListener(ClearSelection);

        AddHint(controlsRoot.transform, "Drag the AMBER and BLUE handles to shape the curve");
        AddHint(controlsRoot.transform, "SPACE + CLICK moves this guide, keeping its shape");
        AddHint(controlsRoot.transform, "Card placing is OFF while a guide is selected");
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

    // Displaces one card's spine and vertices toward the guides' SHAPE.
    //
    // Both arrays are moved by the same delta, and that matters: the spine is what the clumper
    // then reads for its own leader/follower anchors, so guiding it here is what makes the two
    // modifiers compose rather than overwrite each other. Leaving the spine behind would have
    // the clumper gathering strands toward where they used to be.
    public static void Apply(HairCard card, List<ActiveGuide> guides, Vector3[] spine, Vector3[] vertices)
    {
        if (card == null || guides == null || guides.Count == 0) return;
        if (spine == null || spine.Length < 2 || vertices == null) return;

        int columns = HairCard.CrossSectionColumns;
        int rows = spine.Length;
        if (vertices.Length < rows * columns) return;

        if (arcScratch.Length < rows) arcScratch = new float[rows];
        if (worldScratch.Length < rows) worldScratch = new Vector3[rows];

        // Arc length along the card's OWN spine, measured in WORLD space.
        //
        // The spine is local and the guide path is world, so measuring the local distance would
        // silently read the guide at the wrong position the moment a card sits under any
        // non-unit scale. Cards are unparented today and nothing writes localScale on them, so
        // this is belt and braces - but it is one TransformPoint per row against a whole class
        // of bug that would present as "the guide works but is the wrong size".
        //
        // Using the spine rather than card.length also means a curled or waved card reads the
        // guide at the distance it has actually travelled, not its nominal length.
        for (int i = 0; i < rows; i++) worldScratch[i] = card.transform.TransformPoint(spine[i]);

        arcScratch[0] = 0f;
        for (int i = 1; i < rows; i++)
        {
            arcScratch[i] = arcScratch[i - 1] + Vector3.Distance(worldScratch[i], worldScratch[i - 1]);
        }

        Vector3 rootWorld = worldScratch[0];

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

        // Row 0 is the root and never moves - the same anchoring the clumper uses. Hair that
        // slides out of the scalp when a modifier is raised is the one artifact nobody forgives.
        for (int i = 1; i < rows; i++)
        {
            float t = (float)i / (rows - 1);
            float along = t * t * (3f - 2f * t);

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

            if (weightSum <= .0001f) continue;

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
            float influence = strongest * along;
            if (influence <= .0001f) continue;
            Vector3 delta = (blendedTarget - spine[i]) * influence;

            spine[i] += delta;
            int index = i * columns;
            vertices[index] += delta;
            vertices[index + 1] += delta;
            vertices[index + 2] += delta;
        }
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
