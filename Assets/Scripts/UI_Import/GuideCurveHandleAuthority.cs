using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

// Screen-space drag handles for the selected GUIDE's MID and END control points, and the
// card-placement lockout that makes them safe to use.
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

    private enum Grab { None = 0, Mid = 1, End = 2 }

    private static readonly Color ContactColor = new Color(.72f, .45f, 1f, .55f);
    private static readonly Color MidColor = new Color(1f, .78f, .30f, .95f);
    private static readonly Color EndColor = new Color(.40f, .85f, 1f, .95f);
    private static readonly Color HotColor = new Color(1f, 1f, 1f, 1f);

    private GuideCurveManager manager;
    private ModelViewer viewer;
    private Material lineMaterial;

    private LineRenderer contactRing;
    private LineRenderer midRing;
    private LineRenderer endRing;

    private Grab dragging;
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
        midRing = null;
        endRing = null;
        dragging = Grab.None;
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
            dragging = Grab.None;
            draggingGuideId = -1;
            HideAll();
            return;
        }

        // An armed +POST/+CLUMPER/+GUIDE placement owns the next click. Hit-testing handles
        // during one would let a placement click grab a handle instead of placing anything.
        if (GroupAddButtonPlacementAuthority.ArmedKind != GroupAddButtonPlacementAuthority.AddKind.None)
        {
            dragging = Grab.None;
            DrawHandles(guide, Grab.None);
            return;
        }

        Vector3 midWorld = GuideCurveManager.WorldMid(guide);
        Vector3 endWorld = GuideCurveManager.WorldEnd(guide);
        bool pointerOverUI = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
        Vector2 mouse = Mouse.current.position.ReadValue();

        if (Mouse.current.leftButton.wasReleasedThisFrame)
        {
            dragging = Grab.None;
            draggingGuideId = -1;
        }

        // Orbiting or panning mid-drag drops the drag. DragTo solves against a plane rebuilt from
        // the CURRENT camera forward through the handle's CURRENT position, so while the camera
        // swings the solution keeps the same screen position and depth - the handle is carried
        // rigidly around the model and the height clamp then slides it along the surface, leaving
        // the guide arbitrarily deformed by a gesture that was only meant to change the view.
        bool cameraGesture = Mouse.current.rightButton.isPressed || Mouse.current.middleButton.isPressed;
        if (cameraGesture)
        {
            dragging = Grab.None;
            draggingGuideId = -1;
        }

        // The guide can be swapped or deleted mid-drag; never keep dragging a stale one.
        if (dragging != Grab.None && draggingGuideId != guide.id) dragging = Grab.None;

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

        if (dragging == Grab.None && !pointerOverUI && !modifierHeld &&
            Mouse.current.leftButton.wasPressedThisFrame)
        {
            Grab hit = PickHandle(mouse, guide.contact, midWorld, endWorld);
            if (hit != Grab.None)
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

        if (dragging != Grab.None && Mouse.current.leftButton.isPressed)
        {
            Vector3 anchor = midWorld;
            if (dragging == Grab.End) anchor = endWorld;
            DragTo(guide, anchor, mouse);

            midWorld = GuideCurveManager.WorldMid(guide);
            endWorld = GuideCurveManager.WorldEnd(guide);
        }

        Grab hot = dragging;
        if (hot == Grab.None && !pointerOverUI && !modifierHeld) hot = PickHandle(mouse, guide.contact, midWorld, endWorld);
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

        if (dragging == Grab.Mid) guide.midLocal = local;
        else guide.endLocal = local;
    }

    Grab PickHandle(Vector2 mouse, Vector3 contactWorld, Vector3 midWorld, Vector3 endWorld)
    {
        float midDistance = ScreenDistance(mouse, midWorld);
        float endDistance = ScreenDistance(mouse, endWorld);

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
        bool contactVisible = !Occluded(contactWorld);
        if (Occluded(midWorld) && !contactVisible) midDistance = float.MaxValue;
        if (Occluded(endWorld) && !contactVisible) endDistance = float.MaxValue;

        // END wins a tie. It is the handle that sits furthest out, so it is the one most often
        // overlapping the other when the curve is viewed end-on, and it is the one being reached
        // for in that situation.
        if (endDistance <= GrabPixelRadius && endDistance <= midDistance) return Grab.End;
        if (midDistance <= GrabPixelRadius) return Grab.Mid;
        return Grab.None;
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

    void DrawHandles(GuideCurveManager.GuideCurve guide, Grab hot)
    {
        EnsureRings();

        Color midColor = MidColor;
        if (hot == Grab.Mid) midColor = HotColor;
        Color endColor = EndColor;
        if (hot == Grab.End) endColor = HotColor;

        DrawRing(contactRing, guide.contact, ContactColor, HandlePixelRadius * .7f);
        DrawRing(midRing, GuideCurveManager.WorldMid(guide), midColor, HandlePixelRadius);
        DrawRing(endRing, GuideCurveManager.WorldEnd(guide), endColor, HandlePixelRadius);
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
        if (midRing == null) midRing = CreateRing("GuideHandleMid");
        if (endRing == null) endRing = CreateRing("GuideHandleEnd");
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
        SetEnabled(midRing, false);
        SetEnabled(endRing, false);
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
