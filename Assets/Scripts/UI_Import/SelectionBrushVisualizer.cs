using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

// Runtime visual helper for Ctrl+Click localized grooming selection.
// Shows the prospective surface brush while Ctrl is held and keeps the
// selected hotspot visible after clicking. Radius follows Falloff Dist;
// colour follows localized edit Strength.
[DefaultExecutionOrder(2000)]
public class SelectionBrushVisualizer : MonoBehaviour
{
    private const int Segments = 64;
    private ModelViewer viewer;
    private LineRenderer ring;
    private LineRenderer normalLine;
    private Material lineMaterial;

    private FieldInfo hasSelectionField;
    private FieldInfo hitPointField;
    private FieldInfo hitNormalField;
    private FieldInfo falloffField;
    private FieldInfo strengthField;
    private FieldInfo groomingField;
    private FieldInfo textureModeField;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        GameObject go = new GameObject("SelectionBrushVisualizer");
        DontDestroyOnLoad(go);
        go.AddComponent<SelectionBrushVisualizer>();
    }

    void Update()
    {
        if (viewer == null)
        {
            viewer = FindFirstObjectByType<ModelViewer>();
            if (viewer == null) { Hide(); return; }
            CacheFields();
            EnsureLines();
        }

        if (!GetBool(groomingField) || GetBool(textureModeField) || viewer.mainCamera == null || Mouse.current == null)
        {
            Hide();
            return;
        }

        bool ctrl = Keyboard.current != null && Keyboard.current.ctrlKey.isPressed;
        bool pointerOverUI = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
        float strength = Mathf.Clamp01(GetFloat(strengthField, .25f));

        if (ctrl && !pointerOverUI)
        {
            Ray ray = viewer.mainCamera.ScreenPointToRay(Mouse.current.position.ReadValue());
            if (Physics.Raycast(ray, out RaycastHit hit))
            {
                Draw(hit.point, hit.normal, GetFloat(falloffField, .125f), strength);
                return;
            }
        }

        if (GetBool(hasSelectionField))
        {
            Vector3 point = GetVector(hitPointField);
            Vector3 normal = GetVector(hitNormalField);
            Draw(point, normal, GetFloat(falloffField, .125f), strength);
            return;
        }

        Hide();
    }

    void CacheFields()
    {
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        System.Type type = typeof(ModelViewer);
        hasSelectionField = type.GetField("hasSelectionHotspot", flags);
        hitPointField = type.GetField("selectionHitPoint", flags);
        hitNormalField = type.GetField("selectionHitNormal", flags);
        falloffField = type.GetField("brushFalloffDistance", flags);
        strengthField = type.GetField("selectionStrength", flags);
        groomingField = type.GetField("isGroomingMode", flags);
        textureModeField = type.GetField("isTextureEditorMode", flags);
    }

    void EnsureLines()
    {
        if (ring != null) return;

        Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
        if (shader != null) lineMaterial = new Material(shader);

        ring = CreateLine("CtrlSelectionRadius", Segments + 1, .0022f);
        normalLine = CreateLine("CtrlSelectionNormal", 2, .0016f);
    }

    LineRenderer CreateLine(string name, int count, float width)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(transform, false);
        LineRenderer lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = true;
        lr.loop = false;
        lr.positionCount = count;
        lr.startWidth = width;
        lr.endWidth = width;
        lr.numCornerVertices = 2;
        lr.numCapVertices = 2;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        if (lineMaterial != null) lr.material = lineMaterial;
        return lr;
    }

    void Draw(Vector3 center, Vector3 normal, float radius, float strength)
    {
        EnsureLines();
        if (ring == null || normalLine == null) return;

        normal = normal.sqrMagnitude > .000001f ? normal.normalized : Vector3.up;
        radius = Mathf.Max(.002f, radius);

        Vector3 tangent = Vector3.Cross(normal, Mathf.Abs(Vector3.Dot(normal, Vector3.up)) > .95f ? Vector3.right : Vector3.up).normalized;
        Vector3 bitangent = Vector3.Cross(normal, tangent).normalized;
        Vector3 liftedCenter = center + normal * .0015f;

        for (int i = 0; i <= Segments; i++)
        {
            float a = ((float)i / Segments) * Mathf.PI * 2f;
            Vector3 p = liftedCenter + (tangent * Mathf.Cos(a) + bitangent * Mathf.Sin(a)) * radius;
            ring.SetPosition(i, p);
        }

        Color color = StrengthColor(strength);
        ring.startColor = ring.endColor = color;
        normalLine.startColor = normalLine.endColor = color;
        normalLine.SetPosition(0, liftedCenter);
        normalLine.SetPosition(1, liftedCenter + normal * Mathf.Min(radius * .35f, .04f));
        ring.enabled = true;
        normalLine.enabled = true;
    }

    Color StrengthColor(float value)
    {
        value = Mathf.Clamp01(value);
        Color black = new Color(0f, 0f, 0f, .95f);
        Color blue = new Color(0f, .35f, 1f, .95f);
        Color orange = new Color(1f, .38f, 0f, .95f);
        Color yellow = new Color(1f, .9f, 0f, .95f);
        Color white = new Color(1f, 1f, 1f, .95f);

        if (value <= .25f) return Color.Lerp(black, blue, value / .25f);
        if (value <= .50f) return Color.Lerp(blue, orange, (value - .25f) / .25f);
        if (value <= .75f) return Color.Lerp(orange, yellow, (value - .50f) / .25f);
        return Color.Lerp(yellow, white, (value - .75f) / .25f);
    }

    void Hide()
    {
        if (ring != null) ring.enabled = false;
        if (normalLine != null) normalLine.enabled = false;
    }

    bool GetBool(FieldInfo field) => field != null && viewer != null && field.GetValue(viewer) is bool b && b;
    float GetFloat(FieldInfo field, float fallback) => field != null && viewer != null && field.GetValue(viewer) is float f ? f : fallback;
    Vector3 GetVector(FieldInfo field) => field != null && viewer != null && field.GetValue(viewer) is Vector3 v ? v : Vector3.zero;
}
