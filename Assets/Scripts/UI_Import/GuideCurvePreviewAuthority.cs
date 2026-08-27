using UnityEngine;

// Draws the SELECTED GUIDE curve in the viewport, and nothing else.
//
// Every guide in the group used to be drawn, the unselected ones faint, on the theory that
// seeing them all showed the shape of the groom being built. In practice it showed a thicket:
// guides are placed a few centimetres apart and each one is as long as the hair it guides, so
// half a dozen of them bury the very cards you are trying to judge. One curve at a time, and
// the model is visible again.
//
// Nothing is lost by hiding the rest. A guide is selected from its row in the left panel, never
// by clicking its curve in the view - GuideCurveHandleAuthority only ever picks handles on the
// guide that is already selected - so an invisible guide was never a clickable one.
//
// The influence zone is drawn with it, as a ring pair on the surface. That is the same visual
// language the CLUMPER and POST previews use - solid inner ring at the radius, faint outer ring
// at radius + falloff - so a guide's reach reads the same way theirs does.
//
// Order 5280 puts this after GuideCurveManager (5240) and the mesh evaluator (5255), so a curve
// that has just been created, moved or deleted is drawn in the state it actually ended the frame
// in rather than one frame behind.
[DefaultExecutionOrder(5280)]
public class GuideCurvePreviewAuthority : MonoBehaviour
{
    private const int CurveSegments = 48;
    private const int MaxCurveSegments = 320;
    private const int RingSegments = 48;

    // The curve is a generated tube, not a LineRenderer.
    //
    // A LineRenderer is a ribbon: two vertices per sample, spread apart along one axis. Unity
    // picks that axis, and on a curve that bends towards or away from the camera the axis it
    // picks rolls, so the ribbon turns edge-on and the guide goes from a solid stroke to a hair
    // and back again as it sweeps. That is the twisting - it is not a bug in the sampling, it is
    // what a flat ribbon does in three dimensions, and no amount of extra corner vertices fixes
    // it because the geometry genuinely has no width in that direction.
    //
    // A tube has the same silhouette from every direction, so there is no angle at which it can
    // thin out and no roll for it to thin out over. Six sides rather than four: the difference
    // in cost is a few hundred triangles, and four sides still varies its apparent width by 40
    // percent between flat-on and corner-on, which is a milder version of the same complaint.
    private const int TubeSides = 6;

    // RADIUS in pixels, not width, and not world units.
    //
    // Pixels because the handles have always been sized that way and the curve joining them has
    // not, so the curve was the one thing in a guide that got thinner the further back the
    // camera stood. 2 is a four pixel stroke, against the three-ish pixels the old fixed .0026
    // world WIDTH worked out to at a head-filling distance - a little heavier, which is what was
    // asked for, and still in proportion to the two and a half pixel outlines of the rings.
    private const float TubePixelRadius = 2f;

    // A floor and no ceiling, matching GuideCurveHandleAuthority.DrawRing exactly. A ceiling
    // here and none there would mean the curve stopped growing while its own handles carried on,
    // so at a far enough zoom the curve would look thin next to them again - which is the thing
    // the tube exists to stop.
    private const float MinTubeWorldRadius = .0005f;

    // The three strengths a guide is drawn at. The COLOUR is the guide's own now - see
    // GuideCurveManager.CurveColor - and these are only the alphas it is used at, so a recoloured
    // guide keeps the same reading of solid curve, firm inner ring, faint outer ring.
    //
    // The purple these replace lives on as GuideCurveManager.DefaultGuideHue, so an untouched
    // guide still draws in exactly the colour it always did.
    private const float CurveAlpha = .95f;
    private const float ZoneInnerAlpha = .80f;
    private const float ZoneOuterAlpha = .32f;

    private GuideCurveManager manager;
    private ModelViewer viewer;
    private Material lineMaterial;
    private Material tubeMaterial;

    // The GUIDES ON TOP generation both materials were built against. Initialised to a value the
    // authority can never report, so the first frame always rebuilds and nothing depends on the
    // order this component and the toggle happen to wake up in.
    private int overlayGeneration = -1;

    // One, not a pool. Only the selected guide is ever drawn, and a pool sized to the group
    // would be permanently all-but-empty with nothing left to explain what it was for.
    // Reused between frames; see DrawCurve.
    private Vector3[] control;

    // Reused between frames as well. Every one of these only changes length when a point is
    // added to or removed from the guide, which is a keystroke, not a frame.
    private Vector3[] samples;
    private Vector3[] tubeVertices;
    private int[] tubeTriangles;

    private GameObject tubeObject;
    private MeshFilter tubeFilter;
    private MeshRenderer tubeRenderer;
    private Mesh tubeMesh;

    private LineRenderer zoneInner;
    private LineRenderer zoneOuter;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<GuideCurvePreviewAuthority>() != null) return;
        GameObject go = new GameObject("GuideCurvePreviewAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<GuideCurvePreviewAuthority>();
    }

    void Awake()
    {
        manager = null;
        viewer = null;
        lineMaterial = null;
        tubeMaterial = null;
        overlayGeneration = -1;
        control = null;
        samples = null;
        tubeVertices = null;
        tubeTriangles = null;
        tubeObject = null;
        tubeFilter = null;
        tubeRenderer = null;
        tubeMesh = null;
        zoneInner = null;
        zoneOuter = null;
    }

    void LateUpdate()
    {
        if (manager == null) manager = FindFirstObjectByType<GuideCurveManager>();
        if (viewer == null) viewer = FindFirstObjectByType<ModelViewer>();

        // The texture workspace parks the camera front-on against an opaque preview plane, and a
        // curve still drawn there would be a stroke across a UV atlas. Nothing in this
        // authority means anything in that mode, so it draws nothing at all.
        if (manager == null || viewer == null || GroomViewportSuppressed.Active)
        {
            HideAll();
            return;
        }

        // The selected guide is the only thing drawn, so it is also the only thing to look up,
        // and one lookup replaces two. What was here before was a cheap HasAnyGuideInGroup probe
        // guarding a GetGroupGuides call that built a list every LateUpdate the current group had
        // a guide in it. GetSelectedGuide allocates nothing at all, so the guard has nothing left
        // to guard.
        GuideCurveManager.GuideCurve selected = manager.GetSelectedGuide();
        if (selected == null || selected.groupId != viewer.currentGroupId)
        {
            HideAll();
            return;
        }

        // Both materials are rebuilt when GUIDES ON TOP flips, because the difference between
        // the two states is which SHADER they carry and a material's shader cannot be swapped
        // after the fact. Keyed on a generation counter rather than on the bool so this runs once
        // per change instead of testing - and rebuilding - every frame.
        ReleaseMaterialsIfOverlayChanged();

        EnsureTube();
        ApplyCurveColor(selected);
        DrawCurve(selected);

        EnsureZoneLines();
        float radius = Mathf.Max(.001f, selected.radius);
        float falloff = Mathf.Max(0f, selected.falloff);

        Color zoneInnerColor = GuideCurveManager.CurveColor(selected, ZoneInnerAlpha);
        Color zoneOuterColor = GuideCurveManager.CurveColor(selected, ZoneOuterAlpha);

        DrawRing(zoneInner, selected.contact, selected.normal, radius, zoneInnerColor, .0022f);
        if (falloff > .0001f) DrawRing(zoneOuter, selected.contact, selected.normal, radius + falloff, zoneOuterColor, .0014f);
        else SetEnabled(zoneOuter, false);
    }

    // The tube is a root object rather than a child of this one, so it does not go inactive when
    // this component or its GameObject does. Nothing disables it today; this is what keeps a
    // stale curve off the screen if anything ever starts to.
    void OnDisable()
    {
        HideAll();
    }

    void DrawCurve(GuideCurveManager.GuideCurve guide)
    {
        if (tubeMesh == null || tubeRenderer == null)
        {
            if (tubeRenderer != null) tubeRenderer.enabled = false;
            return;
        }

        // Filled in place rather than through WorldPoints, which allocates. This runs every
        // LateUpdate a guide is selected, and the array only changes size when a point is added
        // or removed.
        int nodes = GuideCurveManager.NodeCount(guide);
        if (nodes < 1)
        {
            tubeRenderer.enabled = false;
            return;
        }
        if (control == null || control.Length != nodes + 1) control = new Vector3[nodes + 1];
        control[0] = guide.contact;
        for (int n = 0; n < nodes; n++) control[n + 1] = GuideCurveManager.WorldNode(guide, n);

        // Enough samples for the number of spans, so a twenty point guide is not drawn as a
        // chain of visible straight runs.
        int steps = Mathf.Clamp(CurveSegments * (control.Length - 1) / 2, CurveSegments, MaxCurveSegments);
        if (steps < 2)
        {
            tubeRenderer.enabled = false;
            return;
        }

        if (samples == null || samples.Length != steps) samples = new Vector3[steps];
        for (int i = 0; i < steps; i++)
        {
            float t = (float)i / (steps - 1);
            samples[i] = GuideCurveManager.EvaluatePoints(control, t);
        }

        BuildTube(samples);
        tubeRenderer.enabled = true;
    }

    // Sweeps a ring of TubeSides vertices along the sampled path.
    //
    // The ring is carried from one sample to the next by parallel transport - each ring starts
    // from the previous ring's own reference direction, re-squared against the new tangent -
    // rather than being rebuilt from a fixed world axis at every sample. Rebuilding is what
    // makes generated tubes spin around their own centre line wherever the curve passes near
    // that axis. Carrying costs one cross product and gives a frame that turns only as much as
    // the curve itself does.
    void BuildTube(Vector3[] path)
    {
        int count = path.Length;
        int vertexCount = count * TubeSides;

        bool resized = tubeVertices == null || tubeVertices.Length != vertexCount;
        if (resized)
        {
            tubeVertices = new Vector3[vertexCount];
            tubeTriangles = new int[(count - 1) * TubeSides * 6];

            int write = 0;
            for (int i = 0; i < count - 1; i++)
            {
                int a = i * TubeSides;
                int b = (i + 1) * TubeSides;
                // Wound so the front faces point OUT of the tube. The frame below is right
                // handed with normal x binormal = tangent, and in that frame the obvious
                // ordering comes out inside-out - which the Sprites/Default shader happens to
                // hide, because it does not cull, and either fallback shader would not.
                for (int s = 0; s < TubeSides; s++)
                {
                    int next = (s + 1) % TubeSides;
                    tubeTriangles[write++] = a + s;
                    tubeTriangles[write++] = b + next;
                    tubeTriangles[write++] = b + s;
                    tubeTriangles[write++] = a + s;
                    tubeTriangles[write++] = a + next;
                    tubeTriangles[write++] = b + next;
                }
            }
        }

        // Seeded from something that is definitely not parallel to the first tangent, then
        // carried. Which direction it starts in does not matter - the tube is round.
        Vector3 tangent = SafeTangent(path, 0, Vector3.up);
        Vector3 reference = Vector3.up;
        if (Mathf.Abs(Vector3.Dot(tangent, reference)) > .95f) reference = Vector3.right;
        // No further fallback needed: the .95 test above guarantees reference and tangent are at
        // least eighteen degrees apart, so this cross cannot collapse. The loop below re-squares
        // the frame against every tangent anyway, and has its own guard for the degenerate case.
        Vector3 normal = Vector3.Cross(reference, tangent).normalized;

        for (int i = 0; i < count; i++)
        {
            tangent = SafeTangent(path, i, tangent);

            // Square the carried reference back up against this sample's tangent. Cross twice
            // rather than subtracting a projection so a reference that has drifted almost onto
            // the tangent still comes back with unit length instead of collapsing.
            Vector3 binormal = Vector3.Cross(tangent, normal);
            if (binormal.sqrMagnitude < .000001f)
            {
                Vector3 fallback = Vector3.up;
                if (Mathf.Abs(Vector3.Dot(tangent, fallback)) > .95f) fallback = Vector3.right;
                binormal = Vector3.Cross(tangent, fallback);
            }
            binormal = binormal.normalized;
            normal = Vector3.Cross(binormal, tangent).normalized;

            float radius = WorldRadiusAt(path[i]);
            int baseIndex = i * TubeSides;
            for (int s = 0; s < TubeSides; s++)
            {
                float a = (s / (float)TubeSides) * Mathf.PI * 2f;
                tubeVertices[baseIndex + s] = path[i] + (normal * Mathf.Cos(a) + binormal * Mathf.Sin(a)) * radius;
            }
        }

        // Clear first when the size changed, so the old triangle list is never left pointing at
        // indices the new vertex array no longer has.
        if (resized)
        {
            tubeMesh.Clear();
            tubeMesh.vertices = tubeVertices;
            tubeMesh.triangles = tubeTriangles;
        }
        else
        {
            tubeMesh.vertices = tubeVertices;
        }
        tubeMesh.RecalculateBounds();
    }

    // Central difference where there is one, one-sided at the ends. A pair of samples that land
    // on top of each other - which Catmull-Rom will produce wherever two control points are
    // almost coincident - gives no direction at all, so the previous one is carried instead of
    // normalizing a zero vector into a NaN.
    static Vector3 SafeTangent(Vector3[] path, int index, Vector3 previous)
    {
        Vector3 delta = Vector3.zero;
        if (index > 0 && index < path.Length - 1) delta = path[index + 1] - path[index - 1];
        else if (index > 0) delta = path[index] - path[index - 1];
        else if (path.Length > 1) delta = path[1] - path[0];

        if (delta.sqrMagnitude < .0000001f) return previous;
        return delta.normalized;
    }

    // The same constant-on-screen sizing the handles use, so the curve and the points on it keep
    // their proportions to each other at any zoom.
    //
    // Deliberately the SAME formula as GuideCurveHandleAuthority.DrawRing, including the fact that
    // it has no orthographic case. Matching the handles is the whole point, and there is nothing
    // for an orthographic branch to be right about: the texture editor's locked front view was the
    // only orthographic camera in the project, and neither this nor the handles draw there at all
    // any more.
    float WorldRadiusAt(Vector3 point)
    {
        Camera cam = viewer.mainCamera;
        if (cam == null || cam.pixelHeight <= 0) return MinTubeWorldRadius;

        float distance = Vector3.Distance(cam.transform.position, point);
        float radius = 2f * distance * Mathf.Tan(cam.fieldOfView * .5f * Mathf.Deg2Rad) *
                       (TubePixelRadius / cam.pixelHeight);

        return Mathf.Max(MinTubeWorldRadius, radius);
    }

    void DrawRing(LineRenderer line, Vector3 center, Vector3 normal, float radius, Color color, float width)
    {
        if (line == null) return;

        Vector3 n = Vector3.up;
        if (normal.sqrMagnitude > .000001f) n = normal.normalized;

        Vector3 reference = Vector3.up;
        if (Mathf.Abs(Vector3.Dot(n, Vector3.up)) > .95f) reference = Vector3.right;
        Vector3 tangent = Vector3.Cross(reference, n).normalized;
        Vector3 bitangent = Vector3.Cross(n, tangent).normalized;

        // Lifted off the surface by the same 1.5mm the other ring previews use, so it is not
        // fighting the model's own polygons for depth.
        Vector3 lifted = center + n * .0015f;

        line.positionCount = RingSegments;
        for (int i = 0; i < RingSegments; i++)
        {
            float a = (i / (float)RingSegments) * Mathf.PI * 2f;
            line.SetPosition(i, lifted + (tangent * Mathf.Cos(a) + bitangent * Mathf.Sin(a)) * radius);
        }

        line.startColor = color;
        line.endColor = color;
        line.widthMultiplier = width;
        line.enabled = true;
    }

    // Runs every frame a guide is selected, not once. Everything it builds is re-checked rather
    // than assumed, so a mesh that was collected, a material whose shader was not found on the
    // one frame it was first asked for, or a renderer that lost its material all recover on the
    // next frame instead of leaving the curve invisible or magenta for the rest of the session.
    void EnsureTube()
    {
        if (tubeMesh == null)
        {
            tubeMesh = new Mesh { name = "HairBrushGuideCurveTube" };
            tubeMesh.MarkDynamic();

            // A fresh mesh has no index buffer. BuildTube decides whether to write one from the
            // length of tubeVertices, not from the mesh, so the vertex array has to be dropped
            // with it or the new mesh would be handed vertices and no triangles.
            tubeVertices = null;
            tubeTriangles = null;
            if (tubeFilter != null) tubeFilter.sharedMesh = tubeMesh;
        }

        EnsureTubeMaterial();

        if (tubeObject == null)
        {
            // Deliberately a ROOT object, like TextureUVRectWorkspace's visual root. Its vertices
            // are built in world space, so any parent that was ever moved, rotated or scaled
            // would transform the curve a second time. Parented to nothing, there is nothing to
            // transform it by.
            tubeObject = new GameObject("GuideCurveTube");
            DontDestroyOnLoad(tubeObject);

            tubeFilter = tubeObject.AddComponent<MeshFilter>();
            tubeRenderer = tubeObject.AddComponent<MeshRenderer>();
            tubeRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            tubeRenderer.receiveShadows = false;
            tubeRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            tubeRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            tubeRenderer.enabled = false;
        }

        if (tubeFilter != null && tubeFilter.sharedMesh != tubeMesh) tubeFilter.sharedMesh = tubeMesh;
        if (tubeRenderer != null && tubeMaterial != null && tubeRenderer.sharedMaterial != tubeMaterial)
            tubeRenderer.sharedMaterial = tubeMaterial;
    }

    // Its own material instance rather than the one the rings share, because the colour has to
    // live ON the material here. The rings carry theirs as LineRenderer vertex colours, which a
    // mesh would have to supply itself - and supplying both would multiply them together and
    // give the curve a darker, more saturated colour than the rings it is meant to match. Both
    // property names are set because which of the three shaders is found depends on the pipeline.
    void EnsureTubeMaterial()
    {
        if (tubeMaterial != null) return;

        bool isOverlay;
        Shader shader = ResolveShader(out isOverlay);
        if (shader == null) return;

        tubeMaterial = new Material(shader) { name = "HairBrushGuideCurveTube" };
        PushBelowHandles(tubeMaterial, isOverlay);
    }

    // On the GUIDES ON TOP path the curve, the zone rings and the guide's HANDLE points all end
    // up on HairBrush/Overlay - same queue, same ZTest Always. The curve passes exactly through
    // every handle centre, so the transparent queue's distance sort has nothing to separate them
    // and which one draws last is undefined frame to frame: the handles would flicker in and out
    // from behind the curve.
    //
    // One below the queue the handles use puts the curve first for certain. The handle rings are
    // what you reach for, and HairBrushOverlay.shader's own header says the Overlay queue exists
    // so a point sits on top of the guide curve - this is what keeps that true once the curve is
    // in that queue too.
    //
    // Does nothing on the depth-tested path. With the toggle off the handles are on the overlay
    // queue and the curve is not, so they are already above it; with the toggle on but the overlay
    // shader missing, the handles have fallen back to the SAME queue as the curve, which is a tie
    // rather than an inversion - and stamping a number on one side of that tie is what would
    // create the inversion.
    static void PushBelowHandles(Material material, bool isOverlay)
    {
        if (material == null) return;

        // Keyed on the shader that was actually RESOLVED, not on the toggle. On a build where
        // HairBrush/Overlay is missing, ResolveShader falls back to the depth-tested shader with
        // the toggle still on - and the handle rings fall back with it, to queue 3000. Stamping
        // 3999 on the curve there would put it ABOVE the handles, inverting the very ordering this
        // method exists to guarantee.
        if (!isOverlay) return;

        material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Overlay - 1;
    }

    // The colour is pushed every frame rather than at creation, because the hue slider moves it
    // while the guide is selected and a material built once would hold the colour it was born in.
    // Cheap: SetColor on an already-correct material is a no-op comparison away, and this runs
    // only while a guide is actually selected.
    void ApplyCurveColor(GuideCurveManager.GuideCurve guide)
    {
        if (tubeMaterial == null) return;

        Color colour = GuideCurveManager.CurveColor(guide, CurveAlpha);
        if (tubeMaterial.HasProperty("_BaseColor")) tubeMaterial.SetColor("_BaseColor", colour);
        if (tubeMaterial.HasProperty("_Color")) tubeMaterial.SetColor("_Color", colour);
    }

    // Which shader the curve and its rings draw with, and therefore whether they are hidden by
    // the hair and the head.
    //
    // HairBrush/Overlay is ZTest Always at queue Overlay. It used to carry a note saying it was
    // deliberately NOT for the curve tube or the influence rings - written when depth-testing them
    // was the only behaviour there was. GUIDES ON TOP is the case that note did not anticipate,
    // and the shader's header now says so. The handle points it was originally written for are
    // unaffected either way: they are not routed through here, and PushBelowHandles keeps them on
    // top of the curve once the curve joins them in that queue.
    static Shader ResolveShader(out bool isOverlay)
    {
        isOverlay = false;

        if (GuideOverlayAuthority.Enabled)
        {
            Shader overlay = Shader.Find("HairBrush/Overlay");
            if (overlay != null)
            {
                isOverlay = true;
                return overlay;
            }

            // Missing overlay shader falls through to the depth-tested path rather than drawing
            // nothing. GuideCurveHandleAuthority already warns about this exact shader being
            // absent, so a second warning here would be noise on the same broken build.
        }

        Shader shader = Shader.Find("Sprites/Default");
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Unlit/Color");
        return shader;
    }

    // A material's shader is fixed once it is created, so flipping GUIDES ON TOP means throwing
    // both materials away and letting Ensure* build them again against the other shader. The
    // LineRenderers keep their own references, so they are re-pointed in EnsureZoneLines.
    void ReleaseMaterialsIfOverlayChanged()
    {
        int generation = GuideOverlayAuthority.Generation;
        if (generation == overlayGeneration) return;
        overlayGeneration = generation;

        if (tubeMaterial != null) Destroy(tubeMaterial);
        tubeMaterial = null;
        if (lineMaterial != null) Destroy(lineMaterial);
        lineMaterial = null;
    }

    void EnsureZoneLines()
    {
        EnsureMaterial();
        if (zoneInner == null) zoneInner = CreateLine("GuideZoneRadiusRing", true);
        if (zoneOuter == null) zoneOuter = CreateLine("GuideZoneFalloffRing", true);

        // Re-pointed every time rather than only at creation. The rings outlive the material when
        // GUIDES ON TOP flips - ReleaseMaterialsIfOverlayChanged destroys it and EnsureMaterial
        // builds a new one - and a LineRenderer left holding the destroyed one draws as magenta.
        if (lineMaterial == null) return;
        if (zoneInner != null && zoneInner.sharedMaterial != lineMaterial) zoneInner.sharedMaterial = lineMaterial;
        if (zoneOuter != null && zoneOuter.sharedMaterial != lineMaterial) zoneOuter.sharedMaterial = lineMaterial;
    }

    void EnsureMaterial()
    {
        if (lineMaterial != null) return;
        bool isOverlay;
        Shader shader = ResolveShader(out isOverlay);
        if (shader == null) return;
        lineMaterial = new Material(shader) { name = "HairBrushGuideCurvePreview" };
        PushBelowHandles(lineMaterial, isOverlay);
    }

    LineRenderer CreateLine(string name, bool loop)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(transform, false);
        LineRenderer line = go.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.loop = loop;
        line.numCornerVertices = 2;
        line.numCapVertices = 2;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
        // sharedMaterial, NOT material. Assigning .material makes Unity clone the material into
        // a per-renderer instance, so the two rings would hold two copies of it and neither would
        // be the one OnDestroy cleans up - and, since GUIDES ON TOP now swaps the material out,
        // the ring would go on drawing with a shader the toggle has already moved off. They carry
        // their colour as vertex colours, so there is nothing per-ring for an instance to hold.
        if (lineMaterial != null) line.sharedMaterial = lineMaterial;
        line.enabled = false;
        return line;
    }

    void HideAll()
    {
        if (tubeRenderer != null) tubeRenderer.enabled = false;
        SetEnabled(zoneInner, false);
        SetEnabled(zoneOuter, false);
    }

    static void SetEnabled(LineRenderer line, bool enabled)
    {
        if (line == null) return;
        line.enabled = enabled;
    }

    void OnDestroy()
    {
        if (lineMaterial != null) Destroy(lineMaterial);
        if (tubeMaterial != null) Destroy(tubeMaterial);
        if (tubeMesh != null) Destroy(tubeMesh);

        // The tube is a root object rather than a child, so it does not go with this one.
        if (tubeObject != null) Destroy(tubeObject);
    }
}
