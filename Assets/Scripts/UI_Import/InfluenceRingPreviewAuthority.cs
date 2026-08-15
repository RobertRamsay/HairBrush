using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

// Shared visual language for radius/falloff tools:
// strong inner ring = full-effect radius, faint outer ring = zero-effect falloff edge.
[DefaultExecutionOrder(9600)]
public class InfluenceRingPreviewAuthority : MonoBehaviour
{
    private const int CircleSegments = 64;

    private ModelViewer viewer;
    private PlacementBrushModeAuthority placement;
    private GroupClumperManager clumperManager;

    private FieldInfo placementModeField;
    private FieldInfo placementRadiusField;
    private FieldInfo placementFalloffField;
    private FieldInfo textureModeField;
    private FieldInfo clumperByGroupField;
    private FieldInfo clumperSelectedGroupField;

    private LineRenderer sprayOuter;
    private LineRenderer clumpInner;
    private LineRenderer clumpOuter;
    private Material lineMaterial;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<InfluenceRingPreviewAuthority>() != null) return;
        GameObject go = new GameObject("InfluenceRingPreviewAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<InfluenceRingPreviewAuthority>();
    }

    void Update()
    {
        Resolve();
        UpdateSprayRings();
        UpdateClumperRings();
    }

    void Resolve()
    {
        if (viewer == null)
        {
            viewer = FindFirstObjectByType<ModelViewer>();
            if (viewer != null)
                textureModeField = typeof(ModelViewer).GetField("isTextureEditorMode", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        }

        if (placement == null)
        {
            placement = FindFirstObjectByType<PlacementBrushModeAuthority>();
            if (placement != null)
            {
                BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                System.Type t = typeof(PlacementBrushModeAuthority);
                placementModeField = t.GetField("mode", flags);
                placementRadiusField = t.GetField("brushRadius", flags);
                placementFalloffField = t.GetField("sprayFalloff", flags);
            }
        }

        if (clumperManager == null)
        {
            clumperManager = FindFirstObjectByType<GroupClumperManager>();
            if (clumperManager != null)
            {
                BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                System.Type t = typeof(GroupClumperManager);
                clumperByGroupField = t.GetField("byGroup", flags);
                clumperSelectedGroupField = t.GetField("selectedGroup", flags);
            }
        }
    }

    void UpdateSprayRings()
    {
        if (viewer == null || viewer.mainCamera == null || placement == null || Mouse.current == null ||
            placementModeField == null || placementRadiusField == null || placementFalloffField == null || IsTextureMode())
        {
            SetEnabled(sprayOuter, false);
            return;
        }

        object modeObj = placementModeField.GetValue(placement);
        if (modeObj == null || modeObj.ToString() != "Spray" ||
            (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()))
        {
            SetEnabled(sprayOuter, false);
            return;
        }

        Ray ray = viewer.mainCamera.ScreenPointToRay(Mouse.current.position.ReadValue());
        if (!Physics.Raycast(ray, out RaycastHit hit))
        {
            SetEnabled(sprayOuter, false);
            return;
        }

        float radius = Mathf.Max(.001f, (float)placementRadiusField.GetValue(placement));
        float falloff01 = Mathf.Clamp01((float)placementFalloffField.GetValue(placement));

        // Spray's existing Falloff slider is normalized. Treat it as an additional shell
        // up to one radius wide, so the current inner radius remains the full-density zone.
        float outerRadius = radius * (1f + falloff01);
        if (falloff01 <= .0001f)
        {
            SetEnabled(sprayOuter, false);
            return;
        }

        EnsureRenderers();
        DrawRing(sprayOuter, hit.point, hit.normal, outerRadius,
            new Color(.25f, .85f, 1f, .42f), false);
    }

    void UpdateClumperRings()
    {
        if (clumperManager == null || clumperByGroupField == null || clumperSelectedGroupField == null || IsTextureMode())
        {
            HideClumper();
            return;
        }

        int selected = (int)clumperSelectedGroupField.GetValue(clumperManager);
        if (selected < 0)
        {
            HideClumper();
            return;
        }

        var byGroup = clumperByGroupField.GetValue(clumperManager) as Dictionary<int, GroupClumperManager.GroupClumper>;
        if (byGroup == null || !byGroup.TryGetValue(selected, out GroupClumperManager.GroupClumper clumper) || clumper == null ||
            clumper.mode == GroupClumperManager.ClumpMode.DispersedEvenly)
        {
            HideClumper();
            return;
        }

        EnsureRenderers();
        float radius = Mathf.Max(.001f, clumper.radius);
        float falloff = Mathf.Max(0f, clumper.falloff);
        Vector3 normal = clumper.normal.sqrMagnitude > .000001f ? clumper.normal.normalized : Vector3.up;

        DrawRing(clumpInner, clumper.center, normal, radius,
            new Color(.35f, 1f, .50f, .92f), true);

        if (falloff > .0001f)
            DrawRing(clumpOuter, clumper.center, normal, radius + falloff,
                new Color(.35f, 1f, .50f, .38f), false);
        else
            SetEnabled(clumpOuter, false);
    }

    bool IsTextureMode()
    {
        return viewer != null && textureModeField != null && textureModeField.GetValue(viewer) is bool b && b;
    }

    void EnsureRenderers()
    {
        if (lineMaterial == null)
        {
            Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            if (shader != null) lineMaterial = new Material(shader) { name = "HairBrushInfluenceRingPreview" };
        }

        if (sprayOuter == null) sprayOuter = CreateRing("SprayFalloffRing");
        if (clumpInner == null) clumpInner = CreateRing("ClumperRadiusRing");
        if (clumpOuter == null) clumpOuter = CreateRing("ClumperFalloffRing");
    }

    LineRenderer CreateRing(string name)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(transform, false);
        LineRenderer lr = go.AddComponent<LineRenderer>();
        lr.loop = true;
        lr.useWorldSpace = true;
        lr.positionCount = CircleSegments;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        if (lineMaterial != null) lr.material = lineMaterial;
        lr.enabled = false;
        return lr;
    }

    void DrawRing(LineRenderer line, Vector3 point, Vector3 normal, float radius, Color color, bool strong)
    {
        if (line == null) return;
        Vector3 n = normal.sqrMagnitude > .000001f ? normal.normalized : Vector3.up;
        Vector3 tangent = Vector3.Cross(n, Vector3.up);
        if (tangent.sqrMagnitude < .000001f) tangent = Vector3.Cross(n, Vector3.right);
        tangent.Normalize();
        Vector3 bitangent = Vector3.Cross(n, tangent).normalized;
        Vector3 center = point + n * .0015f;

        for (int i = 0; i < CircleSegments; i++)
        {
            float a = (i / (float)CircleSegments) * Mathf.PI * 2f;
            line.SetPosition(i, center + (tangent * Mathf.Cos(a) + bitangent * Mathf.Sin(a)) * radius);
        }

        line.startColor = color;
        line.endColor = color;
        float width = strong ? radius * .027f : radius * .016f;
        line.widthMultiplier = Mathf.Clamp(width, .00028f, .0024f);
        line.enabled = true;
    }

    void HideClumper()
    {
        SetEnabled(clumpInner, false);
        SetEnabled(clumpOuter, false);
    }

    static void SetEnabled(LineRenderer line, bool enabled)
    {
        if (line != null) line.enabled = enabled;
    }

    void OnDestroy()
    {
        if (lineMaterial != null) Destroy(lineMaterial);
    }
}
