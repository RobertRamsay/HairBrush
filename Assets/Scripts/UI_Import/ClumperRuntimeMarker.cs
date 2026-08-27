using System.Collections;
using System.Reflection;
using UnityEngine;

// Runtime CLUMPER marker: visible in the actual groom editor, not just Scene-view Gizmos.
// Shows the active clumper radius as a green ring plus its surface normal.
[DefaultExecutionOrder(5270)]
public class ClumperRuntimeMarker : MonoBehaviour
{
    private GroupClumperManager clumpers;
    private FieldInfo byGroupField;
    private FieldInfo selectedGroupField;
    private LineRenderer ring;
    private LineRenderer normalLine;
    private Material lineMaterial;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<ClumperRuntimeMarker>() != null) return;
        GameObject go = new GameObject("ClumperRuntimeMarker");
        DontDestroyOnLoad(go);
        go.AddComponent<ClumperRuntimeMarker>();
    }

    void LateUpdate()
    {
        Resolve();
        // Nothing groom-related draws while the texture workspace is up - see TextureModeProbe.
        //
        // Currently unreachable, and left in anyway. ActiveClumper casts byGroup's value to a
        // GroupClumper when the dictionary actually holds a List of them, so it returns null every
        // frame and this authority has never drawn anything - the same dead cast documented at
        // InfluenceRingPreviewAuthority.UpdateClumperRings. Fixing it is not a matter of correcting
        // the cast: SelectedClumperRadialPreviewAuthority already draws these rings, at the same
        // execution order and under the same object name, so a corrected cast would double them up.
        GroupClumperManager.GroupClumper c = ActiveClumper();
        if (c == null || GroomViewportSuppressed.Active)
        {
            SetVisible(false);
            return;
        }

        EnsureLines();
        SetVisible(true);

        Vector3 n = c.normal.sqrMagnitude > .000001f ? c.normal.normalized : Vector3.up;
        Vector3 tangent = Vector3.Cross(n, Mathf.Abs(Vector3.Dot(n, Vector3.up)) > .95f ? Vector3.right : Vector3.up).normalized;
        Vector3 bitangent = Vector3.Cross(n, tangent).normalized;
        float radius = Mathf.Max(.003f, c.radius);

        const int segments = 64;
        ring.positionCount = segments + 1;
        for (int i = 0; i <= segments; i++)
        {
            float a = (i / (float)segments) * Mathf.PI * 2f;
            ring.SetPosition(i, c.center + tangent * Mathf.Cos(a) * radius + bitangent * Mathf.Sin(a) * radius);
        }

        normalLine.positionCount = 2;
        normalLine.SetPosition(0, c.center);
        normalLine.SetPosition(1, c.center + n * Mathf.Max(.03f, radius * .35f));
    }

    GroupClumperManager.GroupClumper ActiveClumper()
    {
        if (clumpers == null || byGroupField == null || selectedGroupField == null) return null;
        int gid = selectedGroupField.GetValue(clumpers) is int v ? v : -1;
        if (gid < 0) return null;
        if (!(byGroupField.GetValue(clumpers) is IDictionary dict) || !dict.Contains(gid)) return null;
        return dict[gid] as GroupClumperManager.GroupClumper;
    }

    void Resolve()
    {
        if (clumpers != null) return;
        clumpers = FindFirstObjectByType<GroupClumperManager>();
        if (clumpers == null) return;
        BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic;
        byGroupField = typeof(GroupClumperManager).GetField("byGroup", f);
        selectedGroupField = typeof(GroupClumperManager).GetField("selectedGroup", f);
    }

    void EnsureLines()
    {
        if (lineMaterial == null)
        {
            Shader shader = Shader.Find("Sprites/Default");
            if (shader != null) lineMaterial = new Material(shader);
        }

        if (ring == null)
        {
            GameObject go = new GameObject("ClumperRadiusRing");
            go.transform.SetParent(transform, false);
            ring = go.AddComponent<LineRenderer>();
            ring.useWorldSpace = true;
            ring.loop = false;
            ring.widthMultiplier = .002f;
            ring.numCornerVertices = 2;
            ring.numCapVertices = 2;
            if (lineMaterial != null) ring.material = lineMaterial;
            ring.startColor = ring.endColor = new Color(.15f, 1f, .45f, .95f);
        }

        if (normalLine == null)
        {
            GameObject go = new GameObject("ClumperNormalLine");
            go.transform.SetParent(transform, false);
            normalLine = go.AddComponent<LineRenderer>();
            normalLine.useWorldSpace = true;
            normalLine.widthMultiplier = .0025f;
            normalLine.numCapVertices = 2;
            if (lineMaterial != null) normalLine.material = lineMaterial;
            normalLine.startColor = normalLine.endColor = new Color(.15f, 1f, .45f, 1f);
        }
    }

    void SetVisible(bool visible)
    {
        if (ring != null) ring.enabled = visible;
        if (normalLine != null) normalLine.enabled = visible;
    }
}
