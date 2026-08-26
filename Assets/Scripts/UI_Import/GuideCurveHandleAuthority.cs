using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

// Screen-space drag handles for the selected GUIDE's control points, and the card-placement
// lockout that makes them safe to use.
//
// A guide has a draggable ROOT at its contact plus two points to start with, a middle and a tip,
// and can be given up to twenty of the latter. Dragging the root slides it over the surface and
// leaves every other point where it is, so the curve re-aims from the new base - which is the only
// way to change the direction hair leaves the scalp in without moving each point by hand. SPACE
// and click is deliberately the opposite gesture: it carries the whole guide across with its form
// intact.
// CTRL+SHIFT and left click ON the curve inserts a point where it was clicked; CTRL+SHIFT and
// right click on a point removes it. The TIP always refuses, and so does any removal that would
// take a guide below two nodes, so a guide keeps a root, something in the middle and a tip
// whatever is done to it. CTRL+SHIFT owns those two clicks outright - neither can also start a
// drag, and ModelViewer stands the right button's camera gesture down while CTRL+SHIFT is held so
// the right click is free to mean this.
//
// These were ALT clicks until MAYA-NAV needed ALT for the camera. See MayaNavigationAuthority for
// why the whole ALT set moved at once, and why it moved whether MAYA-NAV is on or off.
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
    // 12 rather than the 9 this started at. At 9 the inner points, drawn at .78 of it, were
    // fourteen pixels across on a curve that can carry twenty of them - small enough that
    // picking one out from the curve running behind it took a second look. At 12 they are about
    // nineteen, and the tip that cannot be removed is twenty four.
    //
    // The grab radius deliberately does NOT move with it. It is already wider than the drawn
    // handle, which is what makes a slightly-off grab forgiving, and it doubles as the band
    // InsertPointAt refuses to insert inside - so widening it would shrink the gap between two
    // neighbouring points that will still accept a new one, on exactly the crowded guides where
    // that gap is already the tightest.
    private const float HandlePixelRadius = 12f;
    private const float GrabPixelRadius = 16f;
    private const int CircleSegments = 28;

    // How far a press has to travel before it counts as a root drag rather than a click.
    private const float DragStartPixels = 3f;

    // How close to the nearest handle another one has to project before the two count as tied and
    // depth decides between them. Four pixels: wide enough to catch the end-on case, where the
    // whole guide collapses to a smudge, and narrow enough that two handles a comfortable
    // distance apart on screen are still picked by pointing at one of them.
    private const float PickTieBandPixels = 4f;

    // How far a node may be dragged from the contact, as a multiple of the guide's own extent at
    // the moment of the press, and the floor that multiple is taken against. See CaptureNodeDrag.
    private const float DragReachFactor = 3f;
    private const float MinDragReach = .01f;

    // The ROOT, as a value dragging can hold alongside a node index.
    //
    // Not an index because the root is not a node: it is guide.contact, which lives outside
    // nodesLocal and moves through MoveGuideRoot rather than SetNode. A sentinel keeps that
    // distinction in one variable instead of a second bool that could disagree with the first.
    private const int RootHandle = -2;

    // How close, in pixels, a click has to land to the drawn curve to count as ON it. Wider than
    // the grab radius would make a CTRL+SHIFT click near a handle ambiguous; much narrower and the
    // curve becomes hard to hit at a shallow angle.
    private const float CurvePixelRadius = 14f;

    // Green, and deliberately nowhere near the purple the curve tube and the two influence rings
    // are drawn in BY DEFAULT - all three of which are drawn at or from this exact point. While
    // the contact was a marker, matching them was the right call; now that it is the handle that
    // moves the whole guide it has to read as a handle, and the other handles are all off that
    // palette for the same reason.
    //
    // "By default" is doing real work in that sentence now. A guide's colour is the user's, and
    // this green's own hue is .375 - so a Colour slider dialled to about there returns
    // (.45, 1, .59) against this (.40, 1, .55), and the root handle stops standing out from the
    // curve it sits on. These stay fixed rather than being pushed off the
    // chosen hue: the handle colours mean POSITION - root, middle, tip - and a set that shifted
    // with the guide would stop meaning that. Two guides coloured to collide with their own
    // handles is a thing the user can see and undo; handles that changed colour per guide is not.
    private static readonly Color ContactColor = new Color(.40f, 1f, .55f, .95f);
    private static readonly Color MidColor = new Color(1f, .78f, .30f, .95f);
    private static readonly Color EndColor = new Color(.40f, .85f, 1f, .95f);

    // The points between the two ends. Colour marks position along the curve, not when a point
    // was added - see DrawHandles, where SIZE is what marks the tip as the one that stays.
    private static readonly Color InnerColor = new Color(.62f, .55f, .90f, .95f);
    private static readonly Color HotColor = new Color(1f, 1f, 1f, 1f);

    private GuideCurveManager manager;
    private ModelViewer viewer;
    // One, on HairBrush/Overlay (ZTest Always), for every ring this authority draws.
    //
    // The contact ring used to be depth-tested on the grounds that it sits flat on the scalp and
    // drawing it through the skull would read as a bug. That was right while it was a marker. It
    // is a grab target now, and what you can grab has to be what you can see: depth-tested, it
    // spent most of its life behind the very hair the guide is steering, and PickHandle has no
    // occlusion test - so clicking where an invisible ring happened to project would seize the
    // root and move it. Seeing it through hair is the lesser of the two oddities.
    private Material overlayMaterial;

    // Once per session, not once per frame - EnsureRings runs every frame a guide is selected.
    // Reset explicitly, the way GroomingInputLock and GroupParameterClipboardAuthority reset
    // theirs, because with Disable Domain Reload a static keeps its value from the last run and
    // the warning would then fire on the first Play of an editor session and never again.
    private static bool warnedMissingOverlayShader;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        warnedMissingOverlayShader = false;
    }

    private LineRenderer contactRing;
    private readonly List<LineRenderer> nodeRings = new List<LineRenderer>();

    // A node index, RootHandle for the contact, or -1 for nothing.
    private int dragging = -1;
    private int draggingGuideId = -1;

    // Where every node sat in the world at the moment the ROOT was grabbed. See MoveGuideRoot for
    // why a root drag re-derives from this snapshot every frame rather than from the guide's live
    // positions. Grown on demand; a guide has at most twenty nodes.
    private Vector3[] rootDragWorld = new Vector3[0];
    private int rootDragCount;

    // Where the press landed, and whether the cursor has since travelled far enough to mean it.
    private Vector2 rootDragOrigin;
    private bool rootDragArmed;

    // The plane a NODE drag solves against, captured at the press, and how far from the contact
    // that drag may take the node. All initialised here. See CaptureNodeDrag.
    private Vector3 dragPlanePoint = Vector3.zero;
    private Vector3 dragPlaneNormal = Vector3.back;
    private float dragReachLimit = 0f;

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
        overlayMaterial = null;
        contactRing = null;
        nodeRings.Clear();
        dragging = -1;
        draggingGuideId = -1;
        rootDragWorld = new Vector3[0];
        rootDragCount = 0;
        rootDragOrigin = Vector2.zero;
        rootDragArmed = false;
        dragPlanePoint = Vector3.zero;
        dragPlaneNormal = Vector3.back;
        dragReachLimit = 0f;
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

        // The texture-mode test is on the DRAWING half only, deliberately. Update above holds
        // GroomingInputLock for as long as a guide is selected and releases it on a delay when one
        // is not; gating that on the mode as well would walk out of the frame still holding the
        // lock, and card placement would stay off after coming back to groom mode.
        //
        // The node rings are the worst offender of the lot. Everything else in the viewport at
        // least depth-tests, and could in principle be hidden by something in front of it; these
        // draw with ZTest Always and are hidden by nothing at all.
        GuideCurveManager.GuideCurve guide = GetSelectedGuide();
        if (guide == null || viewer == null || viewer.mainCamera == null || Mouse.current == null ||
            TextureModeProbe.Active)
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

        // "Is the button down", not "was it released this frame". The Input System can report a
        // press and a release in the same frame on a fast click; the release test would then
        // clear nothing, the pick below would set a drag, and it would outlive the click -
        // carrying a stale grab, and for the root a stale world snapshot with it, into whatever
        // the next press turned out to be.
        if (!Mouse.current.leftButton.isPressed)
        {
            dragging = -1;
            draggingGuideId = -1;
        }

        // ALT is reserved for the camera, in BOTH modes.
        //
        // Under MAYA-NAV the tumble is ALT plus a mouse button and every branch below reads a
        // mouse button, so without this ALT+LMB would grab whatever handle happened to be under
        // the cursor and drag the guide around with the camera.
        //
        // With MAYA-NAV off it matters just as much, and is easier to miss. ALT+click used to be
        // the point editor and returned right here; now that the editor is CTRL+SHIFT, an ALT
        // click with no test would fall through to the handle pick below and DEFORM the guide -
        // or, missing both handles and the model, reach the deselect test and close guide editing.
        // Both silent, and the drag is a saved change. Anyone reaching for the old binding would
        // wreck the curve they were trying to edit.
        bool altReserved = MayaNavigationAuthority.AltReserved;
        if (altReserved)
        {
            dragging = -1;
            draggingGuideId = -1;
            DrawHandles(guide, -1);
            return;
        }

        // CTRL+SHIFT is the point editor: plus left adds a point where the curve was clicked, plus
        // right removes the point that was clicked. Handled before everything below so the click
        // can never also start a drag, and returning afterwards so it cannot fall through into the
        // deselect test either.
        //
        // This was ALT until MAYA-NAV took ALT for the camera - see MayaNavigationAuthority. CTRL
        // on its own was not available (that is POST authoring), hence the pair. The modifierHeld
        // test further down already stands the handle drag down for CTRL, so the two cannot both
        // claim the same press even if this branch ever stopped returning.
        bool pointEditHeld = Keyboard.current != null &&
                             Keyboard.current.ctrlKey.isPressed &&
                             Keyboard.current.shiftKey.isPressed;
        if (pointEditHeld && !pointerOverUI)
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

        // A camera gesture mid-drag drops the drag. DragTo solves against a plane captured at the
        // press and fixed in the world, so while the camera swings the cursor goes on hitting that
        // same plane from a new direction - the handle is dragged sideways across it, and the guide
        // is left arbitrarily deformed by a gesture that was only meant to change the view.
        //
        // The plane used to be rebuilt every frame instead, and this test was needed then too for
        // a slightly different reason: the handle was carried rigidly around the model. The test
        // predates the capture and survives it unchanged.
        //
        // Under MAYA-NAV a bare right or middle press moves no camera at all, and this still drops
        // the drag. Deliberate: what the test really means is "the user has started doing
        // something else with the mouse", the drop costs nothing but the drag, and a test that had
        // to know which scheme is live would be one more thing to keep in step.
        bool cameraGesture = Mouse.current.rightButton.isPressed || Mouse.current.middleButton.isPressed;
        if (cameraGesture)
        {
            dragging = -1;
            draggingGuideId = -1;
        }

        // The guide can be swapped or deleted mid-drag; never keep dragging a stale one. A point
        // removed from under the drag is the same problem, hence the count test.
        if (dragging != -1 && draggingGuideId != guide.id) dragging = -1;
        if (dragging >= 0 && dragging >= GuideCurveManager.NodeCount(guide)) dragging = -1;

        // CTRL, TAB and SPACE all mean "this click belongs to another gesture". SPACE+click in
        // particular repositions the guide, and GuideCurveManager has already moved its contact by
        // the time this LateUpdate runs - so without this test the same click would ALSO grab a
        // handle (viewed end-on the points project within a few pixels of each other, and
        // PickHandle hands that click to whichever NODE is nearest the camera - the root is
        // settled on screen distance before depth is consulted) and drag it for the rest of the
        // hold. The gesture that promises to keep the guide's form would be the one destroying it.
        bool modifierHeld = Keyboard.current != null &&
                            (Keyboard.current.ctrlKey.isPressed ||
                             Keyboard.current.tabKey.isPressed ||
                             Keyboard.current.spaceKey.isPressed);

        if (dragging == -1 && !pointerOverUI && !modifierHeld &&
            Mouse.current.leftButton.wasPressedThisFrame)
        {
            int hit = PickHandle(guide, mouse);
            if (hit != -1)
            {
                dragging = hit;
                draggingGuideId = guide.id;
                if (hit == RootHandle) CaptureRootDrag(guide, mouse);
                else CaptureNodeDrag(guide, hit);
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

        if (dragging == RootHandle && Mouse.current.leftButton.isPressed && !pointerOverUI)
        {
            // Not until the cursor has actually travelled. A node drag solves against a plane
            // through the handle, so a click that does not move leaves it exactly where it was;
            // the root instead lands wherever a fresh surface raycast says, which near a
            // silhouette or a grazing face is a long way from where it started. Without a
            // threshold a plain click on the root would teleport the guide's base, its zone and
            // its aim, and commit that as an undo step.
            float travel = (mouse - rootDragOrigin).sqrMagnitude;
            bool travelled = travel > DragStartPixels * DragStartPixels;
            if (rootDragArmed || travelled)
            {
                rootDragArmed = true;
                DragRootTo(guide, mouse);
            }
        }
        else if (dragging >= 0 && Mouse.current.leftButton.isPressed)
        {
            DragTo(guide, mouse);
        }

        int hot = dragging;
        if (hot == -1 && !pointerOverUI && !modifierHeld) hot = PickHandle(guide, mouse);
        DrawHandles(guide, hot);
    }

    // Drags in the plane that faced the camera AT THE PRESS. No axis gizmo, by design: the
    // handle simply goes where the cursor goes, at the depth it already had. See CaptureNodeDrag
    // for why the plane is captured rather than rebuilt each frame.
    void DragTo(GuideCurveManager.GuideCurve guide, Vector2 mouse)
    {
        Camera cam = viewer.mainCamera;
        Plane plane = new Plane(dragPlaneNormal, dragPlanePoint);
        Ray ray = cam.ScreenPointToRay(mouse);

        float distance;
        if (!plane.Raycast(ray, out distance)) return;

        Vector3 world = ray.GetPoint(distance);
        Vector3 local = GuideCurveManager.ToLocal(guide, world);

        // The only limit left is reach. A height floor used to follow it and no longer exists -
        // see GuideCurveManager, where the constant was, for what it did and why a flat plane was
        // the wrong shape for the job. A control point may now go anywhere the cursor puts it,
        // including below the root and back towards the head, because on a curved skull most of
        // what that forbade was hair falling perfectly normally.
        if (dragReachLimit > 0f && local.magnitude > dragReachLimit)
            local = local.normalized * dragReachLimit;

        GuideCurveManager.SetNode(guide, dragging, local);
    }

    // The root does not drag in a camera-facing plane the way the others do. It has to stay ON
    // the model - it is where the influence rings are centred, where ZoneWeight measures from, and
    // the surface it lands on supplies the normal the whole frame is carried by - so it follows
    // the cursor across the surface itself, by the same raycast SPACE and click uses.
    //
    // Off the model, nothing happens. Sliding the cursor past the silhouette mid-drag leaves the
    // root at the last place that WAS on the model rather than flinging the guide at a point in
    // space, and picking the cursor back up on the surface carries on from there.
    void DragRootTo(GuideCurveManager.GuideCurve guide, Vector2 mouse)
    {
        if (manager == null) return;

        Ray ray = viewer.mainCamera.ScreenPointToRay(mouse);
        RaycastHit hit;
        if (!Physics.Raycast(ray, out hit)) return;

        manager.MoveGuideRoot(guide, hit.point, hit.normal, rootDragWorld, rootDragCount);
    }

    // Everything a NODE drag has to remember from the moment of the press.
    //
    // THE PLANE. DragTo used to rebuild it every frame from the node's CURRENT position, which is
    // fine right up until something moves the node off that plane - and a clamp does exactly that,
    // on every frame it engages. The node is pushed, the plane is rebuilt at wherever it landed,
    // and at any angle where the push has a component along the view the pair walk each other away
    // from the camera a little per frame. Captured once, the plane cannot be pushed, so a clamp
    // slides the node ALONG it and stops.
    //
    // The clamp this was written against was the height floor, which is gone. The reach clamp
    // below does the same thing - it rescales the offset, which moves the node off the plane just
    // as surely - so the capture is still what stops the walk.
    //
    // Captured as a world point and a world normal rather than as a distance from the camera, so
    // that a wheel zoom mid-drag - the one camera move that does NOT drop the drag - moves the
    // view without moving the plane the node is being solved against.
    //
    // THE REACH. How far this node may end up from the contact. A camera-facing plane is a plane:
    // it extends forever, so the arithmetic is perfectly happy to put a control point a hundred
    // units out in space, and at a shallow angle a small cursor movement is a large world one.
    // Three times the guide's current extent is enough to lengthen a guide substantially in one
    // drag and nowhere near enough to lose it.
    void CaptureNodeDrag(GuideCurveManager.GuideCurve guide, int index)
    {
        dragPlanePoint = GuideCurveManager.WorldNode(guide, index);
        dragPlaneNormal = -viewer.mainCamera.transform.forward;

        float reach = 0f;
        int count = GuideCurveManager.NodeCount(guide);
        for (int i = 0; i < count; i++)
        {
            float length = guide.nodesLocal[i].magnitude;
            if (length > reach) reach = length;
        }

        // The floor matters for a guide that has been dragged almost flat: without it the reach
        // limit collapses to nothing and the node cannot be pulled back OUT again.
        if (reach < MinDragReach) reach = MinDragReach;
        dragReachLimit = reach * DragReachFactor;
    }

    // Taken the instant the root is grabbed, and read unchanged for the rest of the drag.
    void CaptureRootDrag(GuideCurveManager.GuideCurve guide, Vector2 mouse)
    {
        rootDragOrigin = mouse;
        rootDragArmed = false;

        rootDragCount = GuideCurveManager.NodeCount(guide);
        if (rootDragWorld.Length < rootDragCount) rootDragWorld = new Vector3[rootDragCount];
        for (int i = 0; i < rootDragCount; i++)
            rootDragWorld[i] = GuideCurveManager.WorldNode(guide, i);
    }

    int PickHandle(GuideCurveManager.GuideCurve guide, Vector2 mouse)
    {
        int count = GuideCurveManager.NodeCount(guide);

        // NO occlusion test. What is drawn is what can be grabbed, and since the handles moved
        // to HairBrush/Overlay every handle on the selected guide is drawn.
        //
        // There used to be one, and the argument for it was that a handle behind the model is not
        // drawn, so grabbing it would be a blind drag discovered only after orbiting. That was
        // true of a depth-tested ring and is simply false of this one. Keeping the test now would
        // invert the complaint: a point sitting plainly on screen, under the cursor, that refuses
        // to move for no reason the person can see.
        //
        // It was never a hair test either way. Hair cards carry no colliders at all in this
        // project, so the raycast it used could not see the very cards that hide the points in
        // the first place - only the imported head has one. It spoke about the skull and nothing
        // else, and a point drawn through the skull is one you have gone looking at deliberately.
        //
        // TWO PASSES, and the second one is the whole point.
        //
        // Screen distance alone cannot separate these handles at the angle it matters most.
        // Viewed end-on - which is the view you aim a guide FROM - every node projects within a
        // few pixels of the contact and of each other, so whichever happened to be a fraction of
        // a pixel nearer won. The one that won was usually not the one being reached for, and
        // because each handle sits at its own depth the drag then solved in that handle's plane:
        // the point jumped to a depth the user never asked for and the guide appeared to fling
        // itself into space. That is the "sometimes it drifts backwards" report, and it is a PICK
        // bug wearing a drag bug's clothes.
        //
        // So: pass one finds the nearest on screen, pass two treats everything within a few pixels
        // of that as tied and hands the tie to the handle NEAREST THE CAMERA - the one drawn on
        // top, and therefore the one the user is looking at when they click. Every handle draws
        // with ZTest Always, so "on top" is the only reading of the picture there is.
        int best = -1;
        float bestNodeDistance = float.MaxValue;

        for (int i = 0; i < count; i++)
        {
            float distance = ScreenDistance(mouse, GuideCurveManager.WorldNode(guide, i));
            if (distance > GrabPixelRadius) continue;
            if (distance < bestNodeDistance) bestNodeDistance = distance;
        }

        // The ROOT competes on distance like everything else rather than being consulted only
        // after every node has refused. Last-resort ordering sounded like the safe choice and made
        // the handle unreachable from the one view you actually aim a guide from.
        float rootDistance = ScreenDistance(mouse, guide.contact);
        bool rootInRange = rootDistance <= GrabPixelRadius;

        // Settled on SCREEN DISTANCE, before depth is consulted at all, and on exactly the rule it
        // has always had: strictly nearer than every node wins.
        //
        // It must NOT go into the depth contest below, and that is not a preference. A guide is
        // born standing straight out along the surface normal, so looking down that axis - the
        // view the depth rule exists for - the contact is by construction the FURTHEST point of
        // the guide from the camera. Depth would hand every one of those clicks to the tip, and
        // the root ring would stop responding from the one view you aim a guide from: the exact
        // complaint the paragraph above records fixing.
        if (rootInRange && rootDistance < bestNodeDistance) return RootHandle;

        if (bestNodeDistance > GrabPixelRadius) return -1;

        // Among the NODES, everything within a few pixels of the nearest counts as tied and the
        // one nearest the camera - the one drawn on top - takes it.
        //
        // Clamped to the grab radius, so a near-miss at the edge cannot pull in a handle that was
        // never grabbable in the first place: unclamped, a best of 16 would consider handles out
        // to 20 and the effective radius would drift with the arrangement.
        // The trade this makes, stated so nobody has to rediscover it: depth is consulted on
        // EVERY pick, not only the end-on one, so on a guide crowded enough that consecutive nodes
        // project three or four pixels apart, a click dead on one of them can still go to its
        // neighbour if that neighbour is nearer the camera. Pre-patch an exact hit always won.
        // Four pixels against inner handles drawn nineteen across - the tip and the contact are
        // drawn larger still - is a small window to lose, and the
        // alternative - trusting screen distance when it is precise - is what produced the
        // original complaint, because end-on EVERY hit is precise and they are all on top of
        // each other.
        float band = Mathf.Min(bestNodeDistance + PickTieBandPixels, GrabPixelRadius);
        float bestDepth = float.MaxValue;

        for (int i = 0; i < count; i++)
        {
            Vector3 world = GuideCurveManager.WorldNode(guide, i);
            if (ScreenDistance(mouse, world) > band) continue;

            float depth = CameraDepth(world);
            if (depth >= bestDepth) continue;

            bestDepth = depth;
            best = i;
        }

        return best;
    }

    // Distance along the camera's view axis. Only ever compared against another of these, so the
    // sign convention matters and the units do not.
    float CameraDepth(Vector3 world)
    {
        Camera cam = viewer.mainCamera;
        return Vector3.Dot(world - cam.transform.position, cam.transform.forward);
    }

    // ---------------------------------------------------------------------------------
    // Adding and removing points
    // ---------------------------------------------------------------------------------

    // CTRL+SHIFT plus left. The new point goes where the curve was clicked, not where the cursor is:
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
            StatusToast.Show("CTRL + SHIFT and click ON the guide curve to add a point.");
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

        // Unclamped, exactly as a drag is. This used to lift the point to the contact plane so an
        // inserted node could never sit somewhere the drag handles were unable to reproduce - a
        // curve can bow below the plane between two points that are both above it. Now that a drag
        // can reach anywhere, the point simply lands where the curve was clicked, which is what
        // the gesture says it does.
        Vector3 local = GuideCurveManager.ToLocal(guide, point);

        int index = GuideCurveManager.InsertNode(guide, span, local);
        if (index < 0) return;

        StatusToast.Show("Point added. " + (GuideCurveManager.NodeCount(guide) + 1) +
                         " points on this guide. CTRL + SHIFT and right click a point to remove it.");
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

    // CTRL+SHIFT plus right. The tip refuses, and so does the last removal that would take the guide
    // below two points, so a guide always keeps a root, something between and a tip.
    void RemovePointAt(GuideCurveManager.GuideCurve guide, Vector2 mouse)
    {
        int index = PickHandle(guide, mouse);

        // The root answers PickHandle like anything else now, so it can be what was clicked, and
        // "click one of the guide's points" would be a lie told to someone who just did.
        if (index == RootHandle)
        {
            StatusToast.Show("The root stays. Drag it to move where the guide starts.");
            return;
        }

        if (index < 0)
        {
            StatusToast.Show("CTRL + SHIFT and right click one of the guide's points to remove it.");
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

        // A real handle now, not a marker. It used to be drawn small and faint because nothing
        // could grab it; it is the point that decides where the guide starts and which way it
        // launches, so it is drawn at the same size as the tip and lights up like the rest.
        Color contact = ContactColor;
        if (hot == RootHandle) contact = HotColor;
        DrawRing(contactRing, guide.contact, contact, HandlePixelRadius);

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
        while (nodeRings.Count < count) nodeRings.Add(CreateRing("GuideNodeRing_" + nodeRings.Count, overlayMaterial));

        // Same back-fill EnsureRings does for the contact ring, for the same reason.
        for (int i = 0; i < nodeRings.Count; i++) BindMaterial(nodeRings[i], overlayMaterial);
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

    // Every ring gets HairBrush/Overlay: a flat vertex-coloured draw with ZTest Always.
    //
    // The points are what you reach for, and a guide runs through the very hair it is guiding, so
    // in any group with density in it they spent most of their time behind cards. Hair renders as
    // opaque alpha-cutout at queue 2450 and writes depth; Sprites/Default sits at queue 3000 and
    // depth-tests, so the points were already being drawn LAST and still lost - being later in
    // the queue was never the problem, the depth test was.
    //
    // The fallback chain is kept so a build that stripped the shader still draws SOMETHING - the
    // rings go back to being depth-tested rather than vanishing. It is a degraded path, not a
    // supported one: PickHandle has no occlusion test, so under the fallback a handle behind the
    // skull is invisible and still grabbable, and for the root that means an unseen click can
    // move the whole guide. The shader is registered in the project's Always Included Shaders
    // precisely so this does not happen, and it says so out loud once if it ever does.
    void EnsureRings()
    {
        if (overlayMaterial == null)
        {
            Shader shader = Shader.Find("HairBrush/Overlay");
            if (shader == null)
            {
                if (!warnedMissingOverlayShader)
                {
                    warnedMissingOverlayShader = true;
                    Debug.LogWarning("HairBrush: the HairBrush/Overlay shader was not found, so guide " +
                                     "points will be hidden by hair again. Check that it is listed under " +
                                     "Project Settings, Graphics, Always Included Shaders.");
                }
                shader = Shader.Find("Sprites/Default");
            }
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            if (shader != null) overlayMaterial = new Material(shader) { name = "HairBrushGuideHandle" };
        }

        if (contactRing == null) contactRing = CreateRing("GuideHandleContact", overlayMaterial);

        // Bound here as well as at creation. CreateRing can only assign what existed at the
        // moment it ran, and a Shader.Find that came back empty on that first frame would leave
        // the ring on Unity's built-in default line material for the rest of the session.
        BindMaterial(contactRing, overlayMaterial);
    }

    LineRenderer CreateRing(string name, Material material)
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

        // sharedMaterial, not material. Assigning through .material clones the material per
        // renderer, so twenty-one rings would mean twenty-one copies of one flat unlit material
        // and twenty-one extra draw setups for no difference on screen. Nothing here writes to
        // the material at all - the ring colours travel as LineRenderer vertex colours.
        BindMaterial(line, material);
        line.enabled = false;
        return line;
    }

    static void BindMaterial(LineRenderer line, Material material)
    {
        if (line == null || material == null) return;
        if (line.sharedMaterial == material) return;
        line.sharedMaterial = material;
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
        if (overlayMaterial != null) Destroy(overlayMaterial);
    }
}
