using UnityEngine;

// World-space radial preview for the currently selected CLUMPER point.
// Inner ring = full radius. Outer ring = radius + falloff.
[DefaultExecutionOrder(5270)]
public class SelectedClumperRadialPreviewAuthority : MonoBehaviour
{
    private const int Segments = 72;
    private GroupClumperManager manager;
    private LineRenderer inner;
    private LineRenderer outer;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<SelectedClumperRadialPreviewAuthority>() != null) return;
        GameObject go = new GameObject("SelectedClumperRadialPreviewAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<SelectedClumperRadialPreviewAuthority>();
    }

    void Update()
    {
        if (manager == null) manager = FindFirstObjectByType<GroupClumperManager>();
        GroupClumperManager.GroupClumper clumper = manager != null ? manager.GetSelectedClumper() : null;

        // Nothing groom-related draws while the texture workspace is up - see TextureModeProbe.
        if (clumper == null || clumper.mode == GroupClumperManager.ClumpMode.DispersedEvenly ||
            GroomViewportSuppressed.Active)
        {
            SetVisible(false);
            return;
        }

        EnsureLines();
        SetVisible(true);

        Vector3 normal = clumper.normal.sqrMagnitude > .000001f ? clumper.normal.normalized : Vector3.up;
        Vector3 tangent = Vector3.Cross(normal, Mathf.Abs(Vector3.Dot(normal, Vector3.up)) > .95f ? Vector3.right : Vector3.up).normalized;
        Vector3 bitangent = Vector3.Cross(normal, tangent).normalized;
        Vector3 center = clumper.center + normal * .0008f;

        DrawCircle(inner, center, tangent, bitangent, Mathf.Max(.001f, clumper.radius));
        DrawCircle(outer, center, tangent, bitangent, Mathf.Max(.001f, clumper.radius + Mathf.Max(0f, clumper.falloff)));
    }

    void EnsureLines()
    {
        if (inner == null) inner = CreateLine("ClumperRadiusRing", .0015f, 1f);
        if (outer == null) outer = CreateLine("ClumperFalloffRing", .0011f, .45f);
    }

    LineRenderer CreateLine(string name, float width, float alpha)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(transform, false);
        LineRenderer line = go.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.loop = true;
        line.positionCount = Segments;
        line.startWidth = width;
        line.endWidth = width;
        line.numCornerVertices = 2;
        line.numCapVertices = 2;
        Shader shader = Shader.Find("Sprites/Default");
        if (shader != null) line.material = new Material(shader);
        Color c = new Color(.20f, 1f, .42f, alpha);
        line.startColor = c;
        line.endColor = c;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
        return line;
    }

    static void DrawCircle(LineRenderer line, Vector3 center, Vector3 tangent, Vector3 bitangent, float radius)
    {
        if (line == null) return;
        for (int i = 0; i < Segments; i++)
        {
            float a = (Mathf.PI * 2f * i) / Segments;
            line.SetPosition(i, center + (tangent * Mathf.Cos(a) + bitangent * Mathf.Sin(a)) * radius);
        }
    }

    void SetVisible(bool visible)
    {
        if (inner != null) inner.enabled = visible;
        if (outer != null) outer.enabled = visible;
    }
}
