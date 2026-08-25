using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

// Screen-space drag handles for the selected GUIDE's control points, and the card-placement
// lockout that makes them safe to use.
//
// A guide starts with two draggable points, a middle and a tip, and can be given up to twenty.
// ALT and left click ON the curve inserts a point where it was clicked; ALT and right click on a
// point removes it. The first and the last always refuse, so a guide keeps a root, a middle and
// a tip whatever is done to it. ALT owns those two clicks outright - neither can also start a
// drag, and ModelViewer stands its camera orbit down while ALT is held so the right click is
// free to mean this.
//
// THE LOCKOUT IS THE POINT, not a detail. Both handles float off the model on the end of a curve,
// so a grab that misses one continues straight past and hits the surface behind it - and the
// surface is where hair cards get planted. Without a lockout, every slightly-off grab while
// shaping a guide would spawn a card, on the model, under the curve you are working on. So a
// selected guide switches card placement off outright for as long as it stays selected: nothing
// is planted until you leave the guide, via DONE, ESC, another group, another modifier, or a
// click on empty space.
//
// This is the same reservation GroupAddButtonPlacementAuthority uses for an armed placement, held
// for a selection rather than a single click, and re-asserted every frame - ModifierGestureReservation
// hands grooming back unconditionally after any TAB or SPACE click, so a one-shot toggle here
// would be quietly undone by a SPACE+click reposition.
//
// Order -6100 does two jobs at once. Update runs before PlacementBrushModeAuthority (-5000) and
// ModelViewer (0), so the lockout is in place before either reads isGroomingMode. LateUpdate runs
// after every Update - including EventSystem's pointer raycast, so IsPointerOverGameObject is
// answering for THIS frame rather than the last one, and including GuideCurveManager's SPACE
// reposition, so a handle is hit-tested against the position the guide actually holds now.
// (Order -6100 makes this the FIRST LateUpdate, so that second guarantee rests on the manager
// doing its repositioning in Update, not on the order.)
[DefaultExecutionOrder(-6100)]
public class GuideCurveHandleAuthority : MonoBehaviour
{
    // Grab radius in PIXELS. Handles are drawn at a constant pixel size too, so a guide stays
    // equally grabbable whether the camera is up against the scalp or backed off the whole head.
    private const float HandlePixelRadius = 9f;
    private const float GrabPixelRadius = 16f;
    private const int CircleSegments = 28;

    // A guide pointing into the model is meaningless - the cards would be driven through the
    // scalp - so a handle cannot be dragged below the surface plane it grows out of.
    private const float MinHeightAboveSurface = .002f;

    // How close, in pixels, a click has to land to the drawn curve to count as ON it. Wider than
    // the grab radius would make an ALT+click near a handle ambiguous; much narrower and the
    // curve becomes hard to hit at a shallow angle.
    private const float CurvePixelRadius = 14f;

    private static readonly Color ContactColor = new Color(.72f, .45f, 1f, .55f);
    private static readonly Color MidColor = new Color(1f, .78f, .30f, .95f);
    private static readonly Color EndColor = new Color(.40f, .85f, 1f, .95f);

    // The points between the two ends. Colour marks position along the curve, not when a point
    // was added - see DrawHandles, where SIZE is what marks the tip as the one that stays.
    private static readonly Color InnerColor = new Color(.62f, .55f, .90f, .95f);
    private static readonly Color HotColor = new Color(1f, 1f, 1f, 1f);

    private GuideCurveManager manager;
    private ModelViewer viewer;
    private Material lineMaterial;

    private LineRenderer contactRing;
    private readonly List<LineRenderer> nodeRings = new List<LineRenderer>();

    // Index into the guide's node list, or -1 for nothing.
    private int dragging = -1;
    private int draggingGuideId = -1;

    private const string LockOwner = "GuideHandleEditing";
    private bool restorePending;
    private int restoreRequestedFrame;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<GuideCurveHandleAuthority>() != null) return;
        GameObject go = new GameObject("GuideCurveHandleAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<GuideCurveHandleAuthority>();
    }

    void Awake()
    {
        manager = null;
        viewer = null;
        lineMaterial = null;
        contactRing = null;
        nodeRings.Clear();
        dragging = -1;
        draggingGuideId = -1;
        restorePending = false;
        restoreRequestedFrame = -1;
    }

    // ---------------------------------------------------------------------------------
    // Card-placement lockout
    // ---------------------------------------------------------------------------------

    void Update()
    {
        Resolve();
        ServiceDeferredRestore();
        if (viewer == null) return;

        bool editing = GetSelectedGuide() != null;

        if (editing)
        {
            // Every frame, not once. ModifierGestureReservation restores grooming with an
            // unconditional ToggleGroomingMode(true) one frame after any TAB or SPACE click, and
            // a project load re-enables it too - either would quietly turn card placement back on
            // underneath a guide that is still being edited. Only the FIRST holder of the lock
            // captures the pre-suppression state, so joining an armed placement here cannot
            // record its already-suppressed value.
            restorePending = false;
            GroomingInputLock.Hold(LockOwner, viewer);
            return;
        }

        if (!GroomingInputLock.Holds(LockOwner)) return;

        // Deferred, for the same reason the placement buttons defer: handing grooming back the
        // instant the guide is deselected would let the tail of that very click - the DONE press,
        // or a Paint-mode hold - start planting cards.
        GroomingInputLock.Release(LockOwner);
        restorePending = true;
        restoreRequestedFrame = Time.frameCount;
    }

    void ServiceDeferredRestore()
    {
        if (!restorePending) return;
        if (Time.frameCount <= restoreRequestedFrame) return;
        if (Mouse.current != null && Mouse.current.leftButton.isPressed) return;

        // Restores only once every holder has let go - an armed placement that outlives the
        // guide selection keeps the lock, and this stops asking.
        if (GroomingInputLock.TryRestore(viewer)) restorePending = false;
    }

    void OnDisable()
    {
        restorePending = false;
        GroomingInputLock.Release(LockOwner);
        GroomingInputLock.TryRestore(viewer);
    }

    // ---------------------------------------------------------------------------------
    // Handles
    // ---------------------------------------------------------------------------------

    void LateUpdate()
    {
        Resolve();

        GuideCurveManager.GuideCurve guide = GetSelectedGuide();
        if (guide == null || viewer == null || viewer.mainCamera == null || Mouse.current == null)
        {
            dragging = -1;
            draggingGuideId = -1;
            HideAll();
            return;
        }

        // An armed +POST/+CLUMPER/+GUIDE placement owns the next click. Hit-testing handles
        // during one would let a placement click grab a handle instead of placing anything.
        if (GroupAddButtonPlacementAuthority.ArmedKind != GroupAddButtonPlacementAuthority.AddKind.None)
        {
            dragging = -1;
            DrawHandles(guide, -1);
            return;
        }

        bool pointerOverUI = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
        Vector2 mouse = Mouse.current.position.ReadValue();

        if (Mouse.current.leftButton.wasReleasedThisFrame)
        {
            dragging = -1;
            draggingGuideId = -1;
        }

        // ALT is the point editor: ALT plus left adds a point where the curve was clicked, ALT
        // plus right removes the point that was clicked. Handled before everything below so an
        // ALT click can never also start a drag, and returning afterwards so it cannot fall
        // through into the deselect test either.
        bool altHeld = Keyboard.current != null &&
                       (Keyboard.current.leftAltKey.isPressed || Keyboard.current.rightAltKey.isPressed);
        if (altHeld && !pointerOverUI)
        {
            if (Mouse.current.leftButton.wasPressedThisFrame)
            {
                dragging = -1;
                InsertPointAt(guide, mouse);
                DrawHandles(guide, -1);
                return;
            }

            if (Mouse.current.rightButton.wasPressedThisFrame)
            {
                dragging = -1;
                RemovePointAt(guide, mouse);
                DrawHandles(guide, -1);
                return;
            }
        }

        // Orbiting or panning mid-drag drops the drag. DragTo solves against a plane rebuilt from
        // the CURRENT camera forward through the handle's CURRENT position, so while the camera
        // swings the solution keeps the same screen position and depth - the handle is carried
        // rigidly around the model and the height clamp then slides it along the surface, leaving
        // the guide arbitrarily deformed by a gesture that was only meant to change the view.
        bool cameraGesture = Mouse.current.rightButton.isPressed || Mouse.current.middleButton.isPressed;
        if (cameraGesture)
        {
            dragging = -1;
            draggingGuideId = -1;
        }

        // The guide can be swapped or deleted mid-drag; never keep dragging a stale one. A point
        // removed from under the drag is the same problem, hence the count test.
        if (dragging >= 0 && (draggingGuideId != guide.id || dragging >= GuideCurveManager.NodeCount(guide)))
            dragging = -1;

        // CTRL, TAB and SPACE all mean "this click belongs to another gesture". SPACE+click in
        // particular repositions the guide, and GuideCurveManager has already moved its contact by
        // the time this LateUpdate runs - so without this test the same click would ALSO grab a
        // handle (END wins ties, and viewed end-on all three points project within a few pixels
        // of each other) and drag it for the rest of the hold. The gesture that promises to keep
        // the guide's form would be the one destroying it.
        bool modifierHeld = Keyboard.current != null &&
                            (Keyboard.current.ctrlKey.isPressed ||
                             Keyboard.current.tabKey.isPressed ||
                             Keyboard.current.spaceKey.isPressed);

        if (dragging < 0 && !pointerOverUI && !modifierHeld &&
            Mouse.current.leftButton.wasPressedThisFrame)
        {
            int hit = PickHandle(guide, mouse);
            if (hit >= 0)
            {
                dragging = hit;
                draggingGuideId = guide.id;
            }
            else
            {
                // Missed both handles. A click on the model does nothing at all - placement is
                // locked out, which is the whole point - but a click into EMPTY SPACE is the
                // ordinary "I am done here" gesture the rest of the tool already uses, so it
                // leaves guide editing. Requiring the raycast to miss is what keeps an
                // off-target grab on the model from dropping you out of the guide by accident.
                RaycastHit surface;
                Ray ray = viewer.mainCamera.ScreenPointToRay(mouse);
                if (!Physics.Raycast(ray, out surface) && manager != null)
                {
                    manager.ClearSelection();
                    HideAll();
                    return;
                }
            }
        }

        if (dragging >= 0 && Mouse.current.leftButton.isPressed)
        {
            DragTo(guide, GuideCurveManager.WorldNode(guide, dragging), mouse);
        }

        int hot = dragging;
        if (hot < 0 && !pointerOverUI && !modifierHeld) hot = PickHandle(guide, mouse);
        DrawHandles(guide, hot);
    }

    // Drags in the plane through the handle that faces the camera. No axis gizmo, by design:
    // the handle simply goes where the cursor goes, at the depth it already had.
    void DragTo(GuideCurveManager.GuideCurve guide, Vector3 anchor, Vector2 mouse)
    {
        Camera cam = viewer.mainCamera;
        Plane plane = new Plane(-cam.transform.forward, anchor);
        Ray ray = cam.ScreenPointToRay(mouse);

        float distance;
        if (!plane.Raycast(ray, out distance)) return;

        Vector3 world = ray.GetPoint(distance);
        Vector3 local = GuideCurveManager.ToLocal(guide, world);
        if (local.y < MinHeightAboveSurface) local.y = MinHeightAboveSurface;

        GuideCurveManager.SetNode(guide, dragging, local);
    }

    int PickHandle(GuideCurveManager.GuideCurve guide, Vector2 mouse)
    {
        int count = GuideCurveManager.NodeCount(guide);
        if (count == 0) return -1;

        // A handle behind the model is not drawn - the ring material is depth-tested - so it must
        // not be grabbable either. Without this, clicking the forehead within the grab radius of
        // an invisible handle on the back of the skull silently grabs it and drags it out of
        // sight, and the damage is only discovered after orbiting.
        //
        // Unless the guide's CONTACT is visible, in which case a hidden handle is rescued instead.
        //
        // The height clamp only holds a handle above the tangent plane at the contact - outside
        // the mesh on a convex skull, but inside it in a concave one, behind an ear or under a
        // jaw. A handle buried there would be permanently ungrabbable and the only way back would
        // be deleting the guide.
        //
        // Contact visibility is what separates the two cases. If the root of the guide can be
        // seen, you are looking at this guide from its own side and a hidden handle has to be
        // buried in a concavity - rescue it. If the contact is hidden too, the whole guide is
        // simply round the back of the head, and grabbing a handle you cannot see there is the
        // blind drag this test exists to stop. "Both handles hidden" alone would not have
        // distinguished them, and round-the-back is by far the more common of the two.
        bool contactVisible = !Occluded(guide.contact);

        // The handle FURTHEST out wins a tie, which is what the two point version did and for the
        // same reason: viewed end-on the points project within a few pixels of each other, and
        // the one being reached for is the one nearest the tip. Walking backwards and taking the
        // first inside the radius gives that for any number of them.
        int best = -1;
        float bestDistance = float.MaxValue;
        for (int i = count - 1; i >= 0; i--)
        {
            Vector3 world = GuideCurveManager.WorldNode(guide, i);
            float distance = ScreenDistance(mouse, world);
            if (distance > GrabPixelRadius) continue;
            if (Occluded(world) && !contactVisible) continue;
            if (distance >= bestDistance) continue;

            bestDistance = distance;
            best = i;
        }

        return best;
    }

    // ---------------------------------------------------------------------------------
    // Adding and removing points
    // ---------------------------------------------------------------------------------

    // ALT plus left. The new point goes where the curve was clicked, not where the cursor is:
    // the curve is a line in the air, so the nearest point ON it is the only reading of "here"
    // that leaves the shape alone. Clicking anywhere else does nothing at all.
    void InsertPointAt(GuideCurveManager.GuideCurve guide, Vector2 mouse)
    {
        if (GuideCurveManager.NodeCount(guide) >= GuideCurveManager.MaxGuideNodes)
        {
            StatusToast.Show("This guide already has the maximum of " +
                             (GuideCurveManager.MaxGuideNodes + 1) + " points.");
            return;
        }

        Vector3[] control = GuideCurveManager.WorldPoints(guide);
        if (control.Length < 2) return;

        int spans = control.Length - 1;

        // Coarse pass then a refinement around the winner. A single fixed sweep is a count in
        // curve parameter while the tolerance below is in PIXELS, so on a curve that fills the
        // screen the samples end up further apart than the tolerance and a click landing exactly
        // on the line is rejected - in bands, which reads as the feature being broken. The second
        // pass makes the accepted position independent of zoom as well as the acceptance itself.
        int steps = Mathf.Clamp(24 * spans, 48, 480);
        float bestT = NearestT(control, mouse, 0f, 1f, steps);
        float window = 1f / steps;
        bestT = NearestT(control, mouse, bestT - window, bestT + window, 24);

        float bestDistance = ScreenDistance(mouse, GuideCurveManager.EvaluatePoints(control, bestT));
        if (bestDistance > CurvePixelRadius)
        {
            StatusToast.Show("ALT and click ON the guide curve to add a point.");
            return;
        }

        // A click that lands on a point already there would insert a second one in the same
        // place, and Catmull-Rom does not ignore a zero length span - it bulges around it - so
        // a click meant for a handle would visibly kink the curve.
        //
        // Measured in PIXELS, against the same grab radius that decides whether a click counts
        // as being on a handle at all. A band expressed as a fraction of a span looks equivalent
        // and is not: a span is a slice of curve parameter, so on a long span zoomed in it covers
        // hundreds of pixels of open curve, and on a twenty point guide it shrinks below the
        // radius that accepts the click in the first place. Either way the rule stops matching
        // what the user can see.
        for (int i = 0; i < control.Length; i++)
        {
            if (ScreenDistance(mouse, control[i]) > GrabPixelRadius) continue;
            StatusToast.Show("That is a point already. Click the curve between two of them.");
            return;
        }

        // Which span the hit falls in decides where the node is inserted, so the point lands
        // between the two it was drawn between rather than at the end of the list.
        int span = Mathf.Clamp(Mathf.FloorToInt(bestT * spans), 0, spans - 1);
        Vector3 point = GuideCurveManager.EvaluatePoints(control, bestT);

        // Clamped exactly as a drag is. The curve can bow below the contact plane between two
        // points that are both legally above it, and a node planted down there would be one the
        // drag handles could never reproduce.
        Vector3 local = GuideCurveManager.ToLocal(guide, point);
        if (local.y < MinHeightAboveSurface) local.y = MinHeightAboveSurface;

        int index = GuideCurveManager.InsertNode(guide, span, local);
        if (index < 0) return;

        StatusToast.Show("Point added. " + (GuideCurveManager.NodeCount(guide) + 1) +
                         " points on this guide. ALT and right click a point to remove it.");
    }

    // Nearest point on the curve to the cursor, in screen space, searched over a range of t.
    float NearestT(Vector3[] control, Vector2 mouse, float from, float to, int steps)
    {
        float low = Mathf.Clamp01(Mathf.Min(from, to));
        float high = Mathf.Clamp01(Mathf.Max(from, to));

        float bestT = low;
        float bestDistance = float.MaxValue;
        for (int i = 0; i <= steps; i++)
        {
            float t = steps == 0 ? low : Mathf.Lerp(low, high, (float)i / steps);
            float distance = ScreenDistance(mouse, GuideCurveManager.EvaluatePoints(control, t));
            if (distance >= bestDistance) continue;

            bestDistance = distance;
            bestT = t;
        }
        return bestT;
    }

    // ALT plus right. The tip refuses, and so does the last removal that would take the guide
    // below two points, so a guide always keeps a root, something between and a tip.
    void RemovePointAt(GuideCurveManager.GuideCurve guide, Vector2 mouse)
    {
        int index = PickHandle(guide, mouse);
        if (index < 0)
        {
            StatusToast.Show("ALT and right click one of the guide's points to remove it.");
            return;
        }

        int count = GuideCurveManager.NodeCount(guide);
        if (index == count - 1)
        {
            StatusToast.Show("The tip stays. Remove one of the points below it instead.");
            return;
        }

        if (count <= GuideCurveManager.MinGuideNodes)
        {
            StatusToast.Show("A guide keeps a root, a middle and a tip. Nothing left to remove.");
            return;
        }

        if (!GuideCurveManager.RemoveNode(guide, index)) return;
        StatusToast.Show("Point removed. " + (GuideCurveManager.NodeCount(guide) + 1) + " points on this guide.");
    }

    bool Occluded(Vector3 world)
    {
        Camera cam = viewer.mainCamera;
        Vector3 delta = world - cam.transform.position;
        float distance = delta.magnitude;
        if (distance <= .002f) return false;

        RaycastHit blocker;
        return Physics.Raycast(cam.transform.position, delta / distance, out blocker, distance - .002f);
    }

    float ScreenDistance(Vector2 mouse, Vector3 world)
    {
        Vector3 screen = viewer.mainCamera.WorldToScreenPoint(world);
        if (screen.z <= 0f) return float.MaxValue;
        return Vector2.Distance(mouse, new Vector2(screen.x, screen.y));
    }

    // ---------------------------------------------------------------------------------
    // Drawing
    // ---------------------------------------------------------------------------------

    void DrawHandles(GuideCurveManager.GuideCurve guide, int hot)
    {
        EnsureRings();
        DrawRing(contactRing, guide.contact, ContactColor, HandlePixelRadius * .7f);

        int count = GuideCurveManager.NodeCount(guide);
        EnsureNodeRings(count);

        for (int i = 0; i < nodeRings.Count; i++)
        {
            if (i >= count)
            {
                SetEnabled(nodeRings[i], false);
                continue;
            }

            // Size says what can be removed, colour says where on the curve it sits. The tip is
            // the only point that refuses removal, so it is the only one drawn large - anything
            // else large would read as protected too, which is what made the previous version
            // confusing the moment a point was inserted below the original middle.
            bool tip = i == count - 1;
            Color color = tip ? EndColor : (i == 0 ? MidColor : InnerColor);
            float radius = tip ? HandlePixelRadius : HandlePixelRadius * .78f;
            if (i == hot) color = HotColor;

            DrawRing(nodeRings[i], GuideCurveManager.WorldNode(guide, i), color, radius);
        }
    }

    void EnsureNodeRings(int count)
    {
        while (nodeRings.Count < count) nodeRings.Add(CreateRing("GuideNodeRing_" + nodeRings.Count));
    }

    // Sized in world units from the pixel radius and the distance to the camera, so the handle
    // keeps the same on-screen size and the grab radius above stays honest at any zoom.
    void DrawRing(LineRenderer line, Vector3 center, Color color, float pixelRadius)
    {
        if (line == null) return;

        Camera cam = viewer.mainCamera;
        float distance = Vector3.Distance(cam.transform.position, center);
        float worldRadius = .002f;
        if (cam.pixelHeight > 0)
        {
            worldRadius = 2f * distance * Mathf.Tan(cam.fieldOfView * .5f * Mathf.Deg2Rad) *
                          (pixelRadius / cam.pixelHeight);
        }
        worldRadius = Mathf.Max(.0005f, worldRadius);

        Vector3 right = cam.transform.right;
        Vector3 up = cam.transform.up;

        line.positionCount = CircleSegments;
        for (int i = 0; i < CircleSegments; i++)
        {
            float a = (i / (float)CircleSegments) * Mathf.PI * 2f;
            line.SetPosition(i, center + (right * Mathf.Cos(a) + up * Mathf.Sin(a)) * worldRadius);
        }

        line.startColor = color;
        line.endColor = color;
        line.widthMultiplier = Mathf.Max(.0004f, worldRadius * .22f);
        line.enabled = true;
    }

    void EnsureRings()
    {
        if (lineMaterial == null)
        {
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            if (shader != null) lineMaterial = new Material(shader) { name = "HairBrushGuideHandle" };
        }

        if (contactRing == null) contactRing = CreateRing("GuideHandleContact");
    }

    LineRenderer CreateRing(string name)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(transform, false);
        LineRenderer line = go.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.loop = true;
        line.numCornerVertices = 1;
        line.numCapVertices = 1;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
        if (lineMaterial != null) line.material = lineMaterial;
        line.enabled = false;
        return line;
    }

    void HideAll()
    {
        SetEnabled(contactRing, false);
        foreach (LineRenderer ring in nodeRings) SetEnabled(ring, false);
    }

    static void SetEnabled(LineRenderer line, bool enabled)
    {
        if (line == null) return;
        line.enabled = enabled;
    }

    // ---------------------------------------------------------------------------------

    void Resolve()
    {
        if (manager == null) manager = FindFirstObjectByType<GuideCurveManager>();
        if (viewer == null)
        {
            viewer = FindFirstObjectByType<ModelViewer>();
        }
    }

    GuideCurveManager.GuideCurve GetSelectedGuide()
    {
        if (manager == null) return null;
        return manager.GetSelectedGuide();
    }
    void OnDestroy()
    {
        if (lineMaterial != null) Destroy(lineMaterial);
    }
}
