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
    private const int RingSegments = 48;

    private static readonly Color SelectedCurve = new Color(.72f, .45f, 1f, .95f);
    private static readonly Color ZoneInner = new Color(.72f, .45f, 1f, .80f);
    private static readonly Color ZoneOuter = new Color(.72f, .45f, 1f, .32f);

    private GuideCurveManager manager;
    private ModelViewer viewer;
    private Material lineMaterial;

    // One, not a pool. Only the selected guide is ever drawn, and a pool sized to the group
    // would be permanently all-but-empty with nothing left to explain what it was for.
    private LineRenderer curveLine;
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
        curveLine = null;
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

        EnsureCurveLine();
        DrawCurve(curveLine, selected, SelectedCurve, .0026f);

        EnsureZoneLines();
        float radius = Mathf.Max(.001f, selected.radius);
        float falloff = Mathf.Max(0f, selected.falloff);

        DrawRing(zoneInner, selected.contact, selected.normal, radius, ZoneInner, .0022f);
        if (falloff > .0001f) DrawRing(zoneOuter, selected.contact, selected.normal, radius + falloff, ZoneOuter, .0014f);
        else SetEnabled(zoneOuter, false);
    }

    void DrawCurve(LineRenderer line, GuideCurveManager.GuideCurve guide, Color color, float width)
    {
        if (line == null) return;

        Vector3 p0 = guide.contact;
        Vector3 p1 = GuideCurveManager.WorldMid(guide);
        Vector3 p2 = GuideCurveManager.WorldEnd(guide);

        line.positionCount = CurveSegments;
        for (int i = 0; i < CurveSegments; i++)
        {
            float t = (float)i / (CurveSegments - 1);
            line.SetPosition(i, GuideCurveManager.Evaluate(p0, p1, p2, t));
        }

        line.startColor = color;
        line.endColor = color;
        line.widthMultiplier = width;
        line.enabled = true;
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

    void EnsureCurveLine()
    {
        EnsureMaterial();
        if (curveLine == null) curveLine = CreateLine("GuideCurveLine", false);
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
        SetEnabled(curveLine, false);
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
    }
}
