using System.Collections.Generic;
using UnityEngine;

// Draws GUIDE curves in the viewport.
//
// Every guide in the current group is drawn faint so you can see the shape of the groom you are
// building; the SELECTED one is drawn bright, with its influence zone as a ring pair on the
// surface. The ring pair is the same visual language the CLUMPER and POST previews use - solid
// inner ring at the radius, faint outer ring at radius + falloff - so a guide's reach reads the
// same way theirs does.
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
    private static readonly Color OtherCurve = new Color(.55f, .40f, .78f, .38f);
    private static readonly Color ZoneInner = new Color(.72f, .45f, 1f, .80f);
    private static readonly Color ZoneOuter = new Color(.72f, .45f, 1f, .32f);

    private GuideCurveManager manager;
    private ModelViewer viewer;
    private Material lineMaterial;

    private readonly List<LineRenderer> curveLines = new List<LineRenderer>();
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
        curveLines.Clear();
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

        // Allocation-free probe first: GetGroupGuides builds a list and a LINQ closure, and this
        // runs every LateUpdate whether or not the project contains a single guide.
        if (!manager.HasAnyGuideInGroup(viewer.currentGroupId))
        {
            HideAll();
            return;
        }

        List<GuideCurveManager.GuideCurve> guides = manager.GetGroupGuides(viewer.currentGroupId);

        GuideCurveManager.GuideCurve selected = manager.GetSelectedGuide();

        EnsureCurveLines(guides.Count);
        for (int i = 0; i < curveLines.Count; i++)
        {
            if (i >= guides.Count)
            {
                SetEnabled(curveLines[i], false);
                continue;
            }

            GuideCurveManager.GuideCurve guide = guides[i];
            bool isSelected = selected != null && selected.id == guide.id;

            Color color = OtherCurve;
            float width = .0016f;
            if (isSelected)
            {
                color = SelectedCurve;
                width = .0026f;
            }
            DrawCurve(curveLines[i], guide, color, width);
        }

        if (selected == null || selected.groupId != viewer.currentGroupId)
        {
            SetEnabled(zoneInner, false);
            SetEnabled(zoneOuter, false);
            return;
        }

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

    void EnsureCurveLines(int count)
    {
        EnsureMaterial();
        while (curveLines.Count < count)
        {
            curveLines.Add(CreateLine("GuideCurveLine_" + curveLines.Count, false));
        }
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
        foreach (LineRenderer line in curveLines) SetEnabled(line, false);
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
