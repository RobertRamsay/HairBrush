using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// Drawing and editing the marker pairs.
//
// A marker exists on BOTH heads, so every one of them has two drawings: its source placement in
// the left view and its target placement in the right. Which half of the screen the cursor is in
// decides which of the two a click is aimed at, which camera builds the ray, and which layer that
// ray is allowed to hit.
//
// Ring geometry is InfluenceRingPreviewAuthority.DrawRing's, including the degenerate-tangent
// fallback and the small lift off the surface that stops the ring z-fighting the head it is
// drawn on. What is different here is that the radius is constant in PIXELS rather than in world
// units: a marker that shrinks as you dolly out disappears at exactly the moment you pull back to
// place it.
//
// The numbers are screen-space TextMeshProUGUI, not world-space text. There is no world-space
// text anywhere in this project - every TMP usage is on a screen canvas - and the only
// world-to-screen bridge is GuideCurveHandleAuthority's mainCamera.WorldToScreenPoint. Following
// that gives constant legibility at any zoom and correct sorting over the heads for free. The
// same projection also drives hover and picking, so it is computed once per marker per frame and
// used three times.
[DefaultExecutionOrder(9720)]
public class RemapMarkerAuthority : MonoBehaviour
{
    private const int CircleSegments = 40;
    // Apparent size in pixels, so it holds at any dolly distance. The pick radius stays a little
    // wider than the ring: a grab that only works dead centre reads as an unresponsive marker.
    private const float MarkerPixelRadius = 18f;
    private const float PickPixelRadius = 26f;

    // Each side is sized independently, because the two views are not showing the same thing.
    // The left has a full groom drawn over it, so its numbers have to compete with hair; the
    // right is bare geometry being matched by eye, where a bolder ring is what makes a placement
    // easy to judge against the one opposite it. Same reason the ring line is heavier there.
    private const float LabelPointSize = 17f;
    private const float SourceLabelScale = 1.6f;
    private const float TargetMarkerScale = 1.5f;
    private const float TargetRingWidthScale = 2f;
    private const float MinimumSeparation = .004f;

    // Markers are drawn in greyscale, driven by MARKER TONE on the phase bar.
    //
    // A fixed hue was the wrong call. Heads arrive in every shade - a pale grey default material,
    // a dark scanned albedo, anything the user maps on - and no single colour reads on all of
    // them. One tone control the user can slide is both simpler and strictly more capable than
    // picking a better constant.
    //
    // State keeps its colour, though, because state is not decoration: the marker you are placing
    // right now and a marker whose sides disagree both have to be findable at a glance whatever
    // the tone is set to.
    public const string ToneSettingKey = "remapMarkerTone";
    private static float markerTone = 1f;
    private static bool toneLoaded;

    public static float MarkerTone
    {
        get
        {
            LoadTone();
            return markerTone;
        }
        set
        {
            markerTone = Mathf.Clamp01(value);
            toneLoaded = true;
            HairBrushSettings.WriteSetting(ToneSettingKey, markerTone.ToString("F3"));
        }
    }

    // Read once, lazily. Same ini HairBrushSettings keeps the MAYA-NAV preference in, so the tone
    // survives a restart rather than resetting to white every session.
    static void LoadTone()
    {
        if (toneLoaded) return;
        toneLoaded = true;
        System.Collections.Generic.Dictionary<string, string> settings = HairBrushSettings.ReadSettings();
        if (settings == null) return;
        string raw;
        if (!settings.TryGetValue(ToneSettingKey, out raw)) return;
        float parsed;
        if (!float.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out parsed)) return;
        markerTone = Mathf.Clamp01(parsed);
    }

    private static readonly Color ActiveColour = new Color(.42f, 1f, .55f);
    private static readonly Color MismatchColour = new Color(1f, .38f, .32f);

    private RemapSessionController session;
    private Material lineMaterial;
    private Canvas labelCanvas;

    private readonly List<LineRenderer> sourceRings = new List<LineRenderer>();
    private readonly List<LineRenderer> targetRings = new List<LineRenderer>();
    private readonly List<LineRenderer> sourceStubs = new List<LineRenderer>();
    private readonly List<LineRenderer> targetStubs = new List<LineRenderer>();
    private readonly List<TextMeshProUGUI> sourceLabels = new List<TextMeshProUGUI>();
    private readonly List<TextMeshProUGUI> targetLabels = new List<TextMeshProUGUI>();

    private int hoveredIndex = -1;
    private bool hoveredIsTarget;
    private int draggingIndex = -1;
    private bool draggingIsTarget;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<RemapMarkerAuthority>() != null) return;
        GameObject go = new GameObject("RemapMarkerAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<RemapMarkerAuthority>();
    }

    void Update()
    {
        if (session == null) session = RemapSessionController.Instance;
        if (session == null || !session.SessionActive)
        {
            SetAllVisible(false);
            hoveredIndex = -1;
            draggingIndex = -1;
            return;
        }

        EnsureRenderers();
        HandleInput();
    }

    void LateUpdate()
    {
        if (session == null || !session.SessionActive) return;
        DrawAll();
    }

    // ---- input -------------------------------------------------------------------------------

    void HandleInput()
    {
        if (Mouse.current == null) return;

        Vector2 mouse = Mouse.current.position.ReadValue();
        bool overUI = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();

        UpdateHover(mouse, overUI);

        if (Mouse.current.leftButton.wasPressedThisFrame && !overUI)
        {
            if (hoveredIndex >= 0)
            {
                draggingIndex = hoveredIndex;
                draggingIsTarget = hoveredIsTarget;
            }
            else
            {
                PlaceNext(mouse);
            }
        }

        if (Mouse.current.leftButton.wasReleasedThisFrame) draggingIndex = -1;

        if (draggingIndex >= 0 && Mouse.current.leftButton.isPressed) DragTo(mouse);
    }

    // The cursor is over exactly one view, and that view decides everything: which camera builds
    // the ray, which layer it may hit, and which of a marker's two placements is being edited.
    bool ResolveSide(Vector2 mouse, out Camera camera, out int layerMask, out bool isTarget)
    {
        camera = null;
        layerMask = 0;
        isTarget = false;
        if (session == null || session.LeftCamera == null || session.RightCamera == null) return false;

        if (InsideRect(session.LeftCamera, mouse))
        {
            camera = session.LeftCamera;
            layerMask = 1 << session.SourceLayer;
            isTarget = false;
            return true;
        }
        if (InsideRect(session.RightCamera, mouse))
        {
            camera = session.RightCamera;
            layerMask = 1 << session.TargetLayer;
            isTarget = true;
            return true;
        }
        return false;
    }

    static bool InsideRect(Camera camera, Vector2 mouse)
    {
        Rect pixels = camera.pixelRect;
        if (mouse.x < pixels.xMin || mouse.x > pixels.xMax) return false;
        if (mouse.y < pixels.yMin || mouse.y > pixels.yMax) return false;
        return true;
    }

    void UpdateHover(Vector2 mouse, bool overUI)
    {
        hoveredIndex = -1;
        if (overUI || draggingIndex >= 0) return;

        Camera camera;
        int layerMask;
        bool isTarget;
        if (!ResolveSide(mouse, out camera, out layerMask, out isTarget)) return;

        List<RemapMarker> markers = session.Markers;
        // The grab area follows whatever is actually drawn on that side. A ring that looks 1.5x
        // bigger but still picks from the old radius reads as a marker that will not take a drag.
        float pickRadius = PickPixelRadius;
        if (isTarget) pickRadius = PickPixelRadius * TargetMarkerScale;
        float best = pickRadius * pickRadius;
        for (int i = 0; i < markers.Count; i++)
        {
            RemapMarker marker = markers[i];
            if (marker == null || !InteractiveThisPhase(marker)) continue;
            if (isTarget && !marker.targetPlaced) continue;
            if (!isTarget && !marker.sourcePlaced) continue;

            Vector3 world = marker.sourcePoint;
            if (isTarget) world = marker.targetPoint;

            Vector3 screen = camera.WorldToScreenPoint(world);
            if (screen.z <= 0f) continue;
            float d = (new Vector2(screen.x, screen.y) - mouse).sqrMagnitude;
            if (d >= best) continue;
            best = d;
            hoveredIndex = i;
            hoveredIsTarget = isTarget;
        }
    }

    // A click on empty surface places the next marker that needs placing on that side. The order
    // is the marker order, so matching a head is a straight run down the numbers rather than a
    // hunt for which one is missing.
    void PlaceNext(Vector2 mouse)
    {
        Camera camera;
        int layerMask;
        bool isTarget;
        if (!ResolveSide(mouse, out camera, out layerMask, out isTarget)) return;

        int index = NextUnplaced(isTarget);
        if (index < 0) return;

        Vector3 point;
        Vector3 normal;
        if (!Probe(camera, mouse, layerMask, out point, out normal)) return;

        Apply(session.Markers[index], isTarget, point, normal);
        WarnIfTooClose();
    }

    void DragTo(Vector2 mouse)
    {
        if (session == null || draggingIndex < 0 || draggingIndex >= session.Markers.Count) return;

        Camera camera;
        int layerMask;
        bool isTarget;
        if (!ResolveSide(mouse, out camera, out layerMask, out isTarget)) return;
        // A drag that wandered into the other half is not a re-place on the other head.
        if (isTarget != draggingIsTarget) return;

        Vector3 point;
        Vector3 normal;
        if (!Probe(camera, mouse, layerMask, out point, out normal)) return;

        Apply(session.Markers[draggingIndex], draggingIsTarget, point, normal);
    }

    static void Apply(RemapMarker marker, bool isTarget, Vector3 point, Vector3 normal)
    {
        if (isTarget)
        {
            marker.targetPoint = point;
            marker.targetNormal = normal;
            marker.targetPlaced = true;
            return;
        }
        marker.sourcePoint = point;
        marker.sourceNormal = normal;
        marker.sourcePlaced = true;
    }

    // Layer-masked, always. Both heads carry a MeshCollider and nothing else in the project
    // filters a raycast, so an unmasked probe in the right-hand view will happily return a hit on
    // the head in the left-hand one.
    static bool Probe(Camera camera, Vector2 mouse, int layerMask, out Vector3 point, out Vector3 normal)
    {
        point = Vector3.zero;
        normal = Vector3.up;
        Ray ray = camera.ScreenPointToRay(mouse);
        RaycastHit hit;
        if (!Physics.Raycast(ray, out hit, 1000f, layerMask)) return false;
        point = hit.point;
        normal = hit.normal;
        return true;
    }

    // Both of these live on RemapMarkerSet so the view and the phase bar cannot disagree about
    // which marker is next - the highlighted ring and the named instruction have to be the same one.
    int NextUnplaced(bool isTarget)
    {
        return RemapMarkerSet.NextUnplaced(session.Markers, session.Phase, isTarget);
    }

    bool InteractiveThisPhase(RemapMarker marker)
    {
        return RemapMarkerSet.InteractiveInPhase(marker, session.Phase);
    }

    void WarnIfTooClose()
    {
        int first;
        int second;
        if (!RemapMarkerSet.TryFindTooClose(session.Markers, MinimumSeparation, out first, out second)) return;
        StatusToast.Show("Markers " + first + " and " + second + " are almost on top of each other. Spread them out or the warp will be unstable.", true);
    }

    // ---- drawing -----------------------------------------------------------------------------

    void EnsureRenderers()
    {
        if (lineMaterial == null)
        {
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            if (shader != null) lineMaterial = new Material(shader) { name = "HairBrushRemapMarker" };
        }

        if (labelCanvas == null)
        {
            GameObject canvasObject = new GameObject("RemapMarkerLabelCanvas", typeof(RectTransform), typeof(Canvas));
            canvasObject.transform.SetParent(transform, false);
            labelCanvas = canvasObject.GetComponent<Canvas>();
            labelCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // Under the import prompt at 5000, over the groom UI.
            labelCanvas.sortingOrder = 4000;
            // No CanvasScaler on purpose: labels are positioned straight from WorldToScreenPoint,
            // which is in real pixels. A scaler would put every number in the wrong place.
        }

        int wanted = session.Markers.Count;
        while (sourceRings.Count < wanted)
        {
            sourceRings.Add(CreateLine("RemapMarkerSourceRing", session.SourceLayer, true));
            targetRings.Add(CreateLine("RemapMarkerTargetRing", session.TargetLayer, true));
            sourceStubs.Add(CreateLine("RemapMarkerSourceStub", session.SourceLayer, false));
            targetStubs.Add(CreateLine("RemapMarkerTargetStub", session.TargetLayer, false));
            sourceLabels.Add(CreateLabel("RemapMarkerSourceLabel", LabelPointSize * SourceLabelScale));
            targetLabels.Add(CreateLabel("RemapMarkerTargetLabel", LabelPointSize * TargetMarkerScale));
        }
    }

    LineRenderer CreateLine(string name, int layer, bool loop)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(transform, false);
        // The ring has to be culled by the same camera as the head it belongs to, so it carries
        // that head's layer rather than sitting on Default where both cameras would draw it.
        go.layer = layer;
        LineRenderer line = go.AddComponent<LineRenderer>();
        line.loop = loop;
        line.useWorldSpace = true;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
        line.numCapVertices = 0;
        if (lineMaterial != null) line.material = lineMaterial;
        line.enabled = false;
        return line;
    }

    TextMeshProUGUI CreateLabel(string name, float pointSize)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(labelCanvas.transform, false);
        RectTransform rect = go.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.zero;
        rect.pivot = new Vector2(.5f, .5f);
        // Sized from the point size rather than fixed, or the larger numbers get clipped by a box
        // built for the smaller ones.
        rect.sizeDelta = new Vector2(pointSize * 2.7f, pointSize * 1.55f);
        TextMeshProUGUI text = go.GetComponent<TextMeshProUGUI>();
        text.fontSize = pointSize;
        text.fontStyle = FontStyles.Bold;
        text.alignment = TextAlignmentOptions.Center;
        text.raycastTarget = false;
        text.enabled = false;
        return text;
    }

    void DrawAll()
    {
        List<RemapMarker> markers = session.Markers;
        // One active marker at a time, on one side, matching the instruction and the viewport
        // frame exactly. Two independent "next" answers is what let the bar ask for a placement
        // on one head while a green ring sat on the other.
        int pendingIndex;
        bool pendingIsTarget;
        if (!RemapMarkerSet.NextPending(markers, session.Phase, out pendingIndex, out pendingIsTarget)) pendingIndex = -1;
        int activeSource = -1;
        int activeTarget = -1;
        if (pendingIndex >= 0 && pendingIsTarget) activeTarget = pendingIndex;
        if (pendingIndex >= 0 && !pendingIsTarget) activeSource = pendingIndex;

        int mismatched;
        RemapMarkerSet.TryFindSideMismatch(markers, session.SourceRoot, session.TargetRoot, out mismatched);

        for (int i = 0; i < markers.Count; i++)
        {
            RemapMarker marker = markers[i];
            bool interactive = marker != null && InteractiveThisPhase(marker);

            DrawSide(i, marker, false, session.LeftCamera, interactive, i == activeSource, mismatched);
            DrawSide(i, marker, true, session.RightCamera, interactive, i == activeTarget, mismatched);
        }

        // The pool only ever grows, and it outlives a session because this authority does. A
        // second REMAP with fewer markers than the first would otherwise inherit the tail of the
        // previous session's rings, still drawn at the previous session's positions.
        for (int i = markers.Count; i < sourceRings.Count; i++)
        {
            sourceRings[i].enabled = false;
            targetRings[i].enabled = false;
            sourceStubs[i].enabled = false;
            targetStubs[i].enabled = false;
            sourceLabels[i].enabled = false;
            targetLabels[i].enabled = false;
        }
    }

    void DrawSide(int index, RemapMarker marker, bool isTarget, Camera camera, bool interactive, bool isActive, int mismatchedId)
    {
        LineRenderer ring = sourceRings[index];
        LineRenderer stub = sourceStubs[index];
        TextMeshProUGUI label = sourceLabels[index];
        bool placed = marker != null && marker.sourcePlaced;
        Vector3 point = Vector3.zero;
        Vector3 normal = Vector3.up;
        if (marker != null)
        {
            point = marker.sourcePoint;
            normal = marker.sourceNormal;
        }
        if (isTarget)
        {
            ring = targetRings[index];
            stub = targetStubs[index];
            label = targetLabels[index];
            placed = marker != null && marker.targetPlaced;
            if (marker != null)
            {
                point = marker.targetPoint;
                normal = marker.targetNormal;
            }
        }

        if (marker == null || !placed || camera == null)
        {
            ring.enabled = false;
            stub.enabled = false;
            label.enabled = false;
            return;
        }

        float sizeScale = 1f;
        float widthScale = 1f;
        if (isTarget)
        {
            sizeScale = TargetMarkerScale;
            widthScale = TargetRingWidthScale;
        }

        float worldPerPixel = WorldPerPixel(camera, point);
        float radius = MarkerPixelRadius * sizeScale * worldPerPixel;

        Color colour = ColourFor(marker, interactive, isActive, mismatchedId);
        bool hovered = hoveredIndex == index && hoveredIsTarget == isTarget;
        if (hovered) colour = Highlight(colour);

        DrawRing(ring, point, normal, radius, colour, worldPerPixel, hovered, widthScale);
        DrawStub(stub, point, normal, radius, colour, worldPerPixel, widthScale);
        DrawLabel(label, camera, point, normal, marker.label, colour);
    }

    static Color ColourFor(RemapMarker marker, bool interactive, bool isActive, int mismatchedId)
    {
        if (marker.id == mismatchedId) return MismatchColour;
        if (isActive) return ActiveColour;

        float tone = MarkerTone;
        // Markers belonging to the other phase are dimmed rather than hidden - they are still
        // useful context for where you are on the head, just not what you are editing.
        if (!interactive) tone = tone * .45f;
        return new Color(tone, tone, tone, 1f);
    }

    // Hover has to read at BOTH ends of the slider, so it moves toward whichever extreme the
    // current tone is furthest from rather than always toward white. A white marker highlighted
    // white is not a highlight.
    static Color Highlight(Color colour)
    {
        float opposite = 0f;
        if (MarkerTone < .5f) opposite = 1f;
        return Color.Lerp(colour, new Color(opposite, opposite, opposite, 1f), .5f);
    }

    // Constant apparent size. A marker sized in world units vanishes exactly when the user pulls
    // back to see the whole head, which is when they are placing them.
    static float WorldPerPixel(Camera camera, Vector3 point)
    {
        float distance = Vector3.Distance(camera.transform.position, point);
        float height = 2f * distance * Mathf.Tan(camera.fieldOfView * .5f * Mathf.Deg2Rad);
        float pixels = camera.pixelHeight;
        if (pixels < 1f) pixels = 1f;
        return height / pixels;
    }

    static void DrawRing(LineRenderer line, Vector3 point, Vector3 normal, float radius, Color colour, float worldPerPixel, bool hovered, float widthScale)
    {
        Vector3 n = normal;
        if (n.sqrMagnitude < .000001f) n = Vector3.up;
        n = n.normalized;

        Vector3 tangent = Vector3.Cross(n, Vector3.up);
        if (tangent.sqrMagnitude < .000001f) tangent = Vector3.Cross(n, Vector3.right);
        tangent = tangent.normalized;
        Vector3 bitangent = Vector3.Cross(n, tangent).normalized;

        // Lifted off the surface for the same reason DrawRing does it: a ring drawn exactly on
        // the mesh z-fights it. Scaled with the view so the lift stays sub-pixel at any zoom.
        Vector3 centre = point + n * (worldPerPixel * 1.5f);

        line.positionCount = CircleSegments;
        for (int i = 0; i < CircleSegments; i++)
        {
            float a = (i / (float)CircleSegments) * Mathf.PI * 2f;
            line.SetPosition(i, centre + (tangent * Mathf.Cos(a) + bitangent * Mathf.Sin(a)) * radius);
        }

        float width = worldPerPixel * 2f * widthScale;
        if (hovered) width = worldPerPixel * 3.2f * widthScale;
        line.widthMultiplier = width;
        line.startColor = colour;
        line.endColor = colour;
        line.enabled = true;
    }

    static void DrawStub(LineRenderer line, Vector3 point, Vector3 normal, float radius, Color colour, float worldPerPixel, float widthScale)
    {
        Vector3 n = normal;
        if (n.sqrMagnitude < .000001f) n = Vector3.up;
        n = n.normalized;

        line.positionCount = 2;
        line.SetPosition(0, point);
        line.SetPosition(1, point + n * radius * 1.6f);
        line.widthMultiplier = worldPerPixel * 1.6f * widthScale;
        line.startColor = colour;
        line.endColor = colour;
        line.enabled = true;
    }

    static void DrawLabel(TextMeshProUGUI label, Camera camera, Vector3 point, Vector3 normal, string content, Color colour)
    {
        Vector3 screen = camera.WorldToScreenPoint(point);
        // Behind the camera projects to a point in front of it, mirrored. Without this the far
        // side of the head grows a second set of numbers.
        if (screen.z <= 0f)
        {
            label.enabled = false;
            return;
        }
        // Facing away from the viewer. The rings are occluded by the head, but a screen-space
        // label has no depth and would otherwise read straight through the skull.
        if (Vector3.Dot(normal.normalized, camera.transform.forward) > 0f)
        {
            label.enabled = false;
            return;
        }

        label.text = content;
        label.color = colour;
        label.rectTransform.anchoredPosition = new Vector2(screen.x, screen.y);
        label.enabled = true;
    }

    void SetAllVisible(bool visible)
    {
        for (int i = 0; i < sourceRings.Count; i++)
        {
            sourceRings[i].enabled = visible;
            targetRings[i].enabled = visible;
            sourceStubs[i].enabled = visible;
            targetStubs[i].enabled = visible;
            sourceLabels[i].enabled = visible;
            targetLabels[i].enabled = visible;
        }
    }

    void OnDestroy()
    {
        if (lineMaterial != null) Destroy(lineMaterial);
    }
}
