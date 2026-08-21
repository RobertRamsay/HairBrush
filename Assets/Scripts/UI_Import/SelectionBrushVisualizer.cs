using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

// Runtime visual helper for Ctrl+Click localized grooming selection.
// Inner ring = full Radius. Outer ring = Radius + Falloff. Colour = Strength.
[DefaultExecutionOrder(2000)]
public class SelectionBrushVisualizer : MonoBehaviour
{
    private const int Segments = 64;
    private ModelViewer viewer;
    private LineRenderer innerRing;
    private LineRenderer outerRing;
    private LineRenderer normalLine;
    private Material lineMaterial;

    private FieldInfo hasSelectionField;
    private FieldInfo hitPointField;
    private FieldInfo hitNormalField;
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

        // The +POST button arms a placement mode that switches grooming input OFF for its
        // duration, so the ordinary grooming gate below would hide the ring for exactly the
        // flow that most needs it - a modal "click the effect point" with no size feedback.
        bool armedForPost = GroupAddButtonPlacementAuthority.ArmedKind ==
                            GroupAddButtonPlacementAuthority.AddKind.Post;

        if ((!GetBool(groomingField) && !armedForPost) || GetBool(textureModeField) ||
            viewer.mainCamera == null || Mouse.current == null)
        {
            Hide();
            return;
        }

        bool ctrl = Keyboard.current != null && Keyboard.current.ctrlKey.isPressed;
        bool pointerOverUI = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
        float strength = Mathf.Clamp01(viewer.selectionStrength);

        // Two different rings, and they must not be drawn from the same numbers.
        //
        // CTRL+hover - and the armed +POST button - is an AIM ring: it previews the POST this
        // click is about to CREATE. PostGroupLifetimeAuthority forces every new POST to the
        // creation defaults, so the defaults are what the click will actually produce.
        //
        // This used to read viewer.brushRadius, which still holds the LAST SELECTED POST's
        // radius. Tune a POST to 0.15, then Ctrl+hover to place another: the ring showed 0.15,
        // you aimed with it, and the POST that appeared was 0.025 - the ring visibly snapping
        // smaller the instant you clicked.
        if ((ctrl || armedForPost) && !pointerOverUI)
        {
            Ray ray = viewer.mainCamera.ScreenPointToRay(Mouse.current.position.ReadValue());
            if (Physics.Raycast(ray, out RaycastHit hit))
            {
                // Full strength, NOT viewer.selectionStrength. Every new POST is created at
                // weight 1 (PostAffectorManager.CreateAffector), but selectionStrength still
                // holds whatever the last selection left - and several paths deliberately zero
                // it, including creating a new hair group. StrengthColor(0) is pure black, so
                // the aim ring for a +POST on a brand-new group was drawn invisible against
                // the model. Same argument as the radius and falloff above: the ring must show
                // what the click will actually produce.
                Draw(hit.point, hit.normal,
                     PostGroupLifetimeAuthority.DefaultPostRadius,
                     PostGroupLifetimeAuthority.DefaultPostFalloff,
                     1f);
                return;
            }
        }

        // CTRL up: this ring belongs to the POST that is currently SELECTED, and here the
        // viewer fields are the correct source - SelectAffector loads them from that POST and
        // the Radius/Falloff sliders write straight back to it, so the ring tracks the drag.
        if (GetBool(hasSelectionField))
        {
            Draw(GetVector(hitPointField), GetVector(hitNormalField),
                 Mathf.Max(.001f, viewer.brushRadius),
                 Mathf.Max(0f, viewer.brushFalloffDistance),
                 strength);
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
        groomingField = type.GetField("isGroomingMode", flags);
        textureModeField = type.GetField("isTextureEditorMode", flags);
    }

    void EnsureLines()
    {
        if (innerRing != null) return;

        Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
        if (shader != null) lineMaterial = new Material(shader);

        innerRing = CreateLine("CtrlSelectionRadiusInner", Segments + 1, .0024f);
        outerRing = CreateLine("CtrlSelectionFalloffOuter", Segments + 1, .0018f);
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

    void Draw(Vector3 center, Vector3 normal, float radius, float falloff, float strength)
    {
        EnsureLines();
        if (innerRing == null || outerRing == null || normalLine == null) return;

        normal = normal.sqrMagnitude > .000001f ? normal.normalized : Vector3.up;
        radius = Mathf.Max(.002f, radius);
        float outerRadius = Mathf.Max(radius, radius + falloff);

        Vector3 tangent = Vector3.Cross(normal, Mathf.Abs(Vector3.Dot(normal, Vector3.up)) > .95f ? Vector3.right : Vector3.up).normalized;
        Vector3 bitangent = Vector3.Cross(normal, tangent).normalized;
        Vector3 liftedCenter = center + normal * .0015f;

        SetRing(innerRing, liftedCenter, tangent, bitangent, radius);
        SetRing(outerRing, liftedCenter, tangent, bitangent, outerRadius);

        Color color = StrengthColor(strength);
        Color outerColor = color;
        outerColor.a *= .72f;

        innerRing.startColor = innerRing.endColor = color;
        outerRing.startColor = outerRing.endColor = outerColor;
        normalLine.startColor = normalLine.endColor = color;
        normalLine.SetPosition(0, liftedCenter);
        normalLine.SetPosition(1, liftedCenter + normal * Mathf.Min(outerRadius * .30f, .04f));

        innerRing.enabled = true;
        outerRing.enabled = falloff > .0001f;
        normalLine.enabled = true;
    }

    void SetRing(LineRenderer ring, Vector3 center, Vector3 tangent, Vector3 bitangent, float radius)
    {
        for (int i = 0; i <= Segments; i++)
        {
            float a = ((float)i / Segments) * Mathf.PI * 2f;
            Vector3 p = center + (tangent * Mathf.Cos(a) + bitangent * Mathf.Sin(a)) * radius;
            ring.SetPosition(i, p);
        }
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
        if (innerRing != null) innerRing.enabled = false;
        if (outerRing != null) outerRing.enabled = false;
        if (normalLine != null) normalLine.enabled = false;
    }

    bool GetBool(FieldInfo field) => field != null && viewer != null && field.GetValue(viewer) is bool b && b;
    Vector3 GetVector(FieldInfo field) => field != null && viewer != null && field.GetValue(viewer) is Vector3 v ? v : Vector3.zero;
}
