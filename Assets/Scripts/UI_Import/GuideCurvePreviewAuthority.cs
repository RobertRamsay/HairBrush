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

    private static readonly Color SelectedCurve = new Color(.72f, .45f, 1f, .95f);
    private static readonly Color ZoneInner = new Color(.72f, .45f, 1f, .80f);
    private static readonly Color ZoneOuter = new Color(.72f, .45f, 1f, .32f);

    private GuideCurveManager manager;
    private ModelViewer viewer;
    private Material lineMaterial;
    private Material tubeMaterial;

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

        if (manager == null || viewer == null)
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

        EnsureTube();
        DrawCurve(selected);

        EnsureZoneLines();
        float radius = Mathf.Max(.001f, selected.radius);
        float falloff = Mathf.Max(0f, selected.falloff);

        DrawRing(zoneInner, selected.contact, selected.normal, radius, ZoneInner, .0022f);
        if (falloff > .0001f) DrawRing(zoneOuter, selected.contact, selected.normal, radius + falloff, ZoneOuter, .0014f);
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
    // Deliberately the SAME formula as GuideCurveHandleAuthority.DrawRing, including the fact
    // that it has no orthographic case. Matching the handles is the whole point - a separately
    // correct orthographic branch here would only make the curve and its own handles disagree
    // about their size in the one mode (the texture editor's locked front view) where they are
    // both drawn under an orthographic camera.
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
    // give the curve a darker, more saturated purple than the rings it is meant to match. Both
    // property names are set because which of the three shaders is found depends on the pipeline.
    void EnsureTubeMaterial()
    {
        if (tubeMaterial != null) return;

        Shader shader = Shader.Find("Sprites/Default");
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Unlit/Color");
        if (shader == null) return;

        tubeMaterial = new Material(shader) { name = "HairBrushGuideCurveTube" };
        if (tubeMaterial.HasProperty("_BaseColor")) tubeMaterial.SetColor("_BaseColor", SelectedCurve);
        if (tubeMaterial.HasProperty("_Color")) tubeMaterial.SetColor("_Color", SelectedCurve);
    }

    void EnsureZoneLines()
    {
        EnsureMaterial();
        if (zoneInner == null) zoneInner = CreateLine("GuideZoneRadiusRing", true);
        if (zoneOuter == null) zoneOuter = CreateLine("GuideZoneFalloffRing", true);
    }

    void EnsureMaterial()
    {
        if (lineMaterial != null) return;
        Shader shader = Shader.Find("Sprites/Default");
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Unlit/Color");
        if (shader == null) return;
        lineMaterial = new Material(shader) { name = "HairBrushGuideCurvePreview" };
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
        if (lineMaterial != null) line.material = lineMaterial;
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
