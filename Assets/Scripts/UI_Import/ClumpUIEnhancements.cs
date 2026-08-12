using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Runtime-only polish for the clump prototype. Keeps window behaviour and visual-only
// widgets out of ModelViewer/ClumpLayerManager while those systems are still evolving.
public class ClumpUIEnhancements : MonoBehaviour
{
    private ModelViewer viewer;
    private GameObject clumpPanel;
    private GameObject curvePanel;
    private TextMeshProUGUI paintLabel;
    private bool installed;
    private bool groomingSuppressed;

    public void Init(ModelViewer owner)
    {
        viewer = owner;
    }

    void Update()
    {
        if (viewer == null) viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer == null) return;

        if (!installed)
            TryInstallUIEnhancements();

        UpdatePaintInputGate();
    }

    void TryInstallUIEnhancements()
    {
        clumpPanel = GameObject.Find("ClumpLayerModal");
        if (clumpPanel == null) return;

        curvePanel = FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .FirstOrDefault(r => r.gameObject.name == "ClumpCurveModal")?.gameObject;

        InstallDragHandle(clumpPanel);
        if (curvePanel != null)
        {
            InstallDragHandle(curvePanel);
            InstallCurvePreview(curvePanel);
        }

        paintLabel = clumpPanel.GetComponentsInChildren<TextMeshProUGUI>(true)
            .FirstOrDefault(t => t.text.StartsWith("PAINT:"));

        installed = true;
    }

    void InstallDragHandle(GameObject window)
    {
        RectTransform windowRect = window.GetComponent<RectTransform>();
        if (windowRect == null) return;

        TextMeshProUGUI title = window.GetComponentsInChildren<TextMeshProUGUI>(true)
            .FirstOrDefault(t => t.transform.parent == window.transform);
        if (title == null) return;

        title.raycastTarget = true;
        ClumpWindowDragHandle drag = title.gameObject.GetComponent<ClumpWindowDragHandle>();
        if (drag == null) drag = title.gameObject.AddComponent<ClumpWindowDragHandle>();
        drag.target = windowRect;
    }

    void InstallCurvePreview(GameObject window)
    {
        if (window.transform.Find("CurvePreview") != null) return;

        Slider[] sliders = window.GetComponentsInChildren<Slider>(true);
        if (sliders.Length < 3) return;

        GameObject previewGO = new GameObject("CurvePreview", typeof(RectTransform), typeof(LayoutElement), typeof(ClumpCurvePreviewGraphic));
        previewGO.transform.SetParent(window.transform, false);

        LayoutElement le = previewGO.GetComponent<LayoutElement>();
        le.preferredHeight = 100f;
        le.minHeight = 80f;

        RectTransform rt = previewGO.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(0f, 100f);

        ClumpCurvePreviewGraphic graphic = previewGO.GetComponent<ClumpCurvePreviewGraphic>();
        graphic.early = sliders[0];
        graphic.mid = sliders[1];
        graphic.tip = sliders[2];
        graphic.color = new Color(0.2f, 1f, 0.35f, 1f);

        // Put the graph below the subtitle and above the controls.
        int desiredIndex = Mathf.Min(2, window.transform.childCount - 1);
        previewGO.transform.SetSiblingIndex(desiredIndex);
    }

    void UpdatePaintInputGate()
    {
        if (!installed || clumpPanel == null)
        {
            RestoreGroomingIfNeeded();
            return;
        }

        if (!clumpPanel.activeInHierarchy)
        {
            RestoreGroomingIfNeeded();
            return;
        }

        if (paintLabel == null)
        {
            paintLabel = clumpPanel.GetComponentsInChildren<TextMeshProUGUI>(true)
                .FirstOrDefault(t => t.text.StartsWith("PAINT:"));
        }

        bool painting = paintLabel != null && paintLabel.text.Contains("ON");
        if (painting && !groomingSuppressed)
        {
            viewer.ToggleGroomingMode(false);
            groomingSuppressed = true;
        }
        else if (!painting && groomingSuppressed)
        {
            viewer.ToggleGroomingMode(true);
            groomingSuppressed = false;
        }
    }

    void RestoreGroomingIfNeeded()
    {
        if (!groomingSuppressed || viewer == null) return;
        viewer.ToggleGroomingMode(true);
        groomingSuppressed = false;
    }

    void OnDisable()
    {
        RestoreGroomingIfNeeded();
    }
}

public class ClumpWindowDragHandle : MonoBehaviour, IBeginDragHandler, IDragHandler
{
    public RectTransform target;
    private Canvas canvas;
    private Vector2 pointerOffset;

    public void OnBeginDrag(PointerEventData eventData)
    {
        if (target == null) return;
        canvas = target.GetComponentInParent<Canvas>();
        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            target,
            eventData.position,
            eventData.pressEventCamera,
            out pointerOffset);
        target.SetAsLastSibling();
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (target == null || canvas == null) return;
        RectTransform canvasRect = canvas.transform as RectTransform;
        if (canvasRect == null) return;

        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
            canvasRect,
            eventData.position,
            eventData.pressEventCamera,
            out Vector2 localPointer))
        {
            float scale = Mathf.Max(0.0001f, canvas.scaleFactor);
            Vector2 offset = pointerOffset / scale;
            target.localPosition = localPointer - offset;
        }
    }
}

public class ClumpCurvePreviewGraphic : MaskableGraphic
{
    public Slider early;
    public Slider mid;
    public Slider tip;

    private const int Samples = 48;
    private const float StrokeWidth = 2.5f;

    void Update()
    {
        SetVerticesDirty();
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        if (early == null || mid == null || tip == null) return;

        Rect r = rectTransform.rect;
        float pad = 8f;
        Rect plot = new Rect(r.xMin + pad, r.yMin + pad, Mathf.Max(1f, r.width - pad * 2f), Mathf.Max(1f, r.height - pad * 2f));

        DrawGrid(vh, plot);

        AnimationCurve curve = new AnimationCurve(
            new Keyframe(0f, 0f),
            new Keyframe(0.25f, early.value),
            new Keyframe(0.65f, mid.value),
            new Keyframe(1f, tip.value));

        Vector2 previous = CurvePoint(plot, 0f, curve.Evaluate(0f));
        for (int i = 1; i <= Samples; i++)
        {
            float t = i / (float)Samples;
            Vector2 current = CurvePoint(plot, t, Mathf.Clamp01(curve.Evaluate(t)));
            AddLine(vh, previous, current, StrokeWidth, color);
            previous = current;
        }
    }

    Vector2 CurvePoint(Rect plot, float x, float y)
    {
        return new Vector2(
            Mathf.Lerp(plot.xMin, plot.xMax, x),
            Mathf.Lerp(plot.yMin, plot.yMax, y));
    }

    void DrawGrid(VertexHelper vh, Rect plot)
    {
        Color grid = new Color(1f, 1f, 1f, 0.13f);
        AddLine(vh, new Vector2(plot.xMin, plot.yMin), new Vector2(plot.xMax, plot.yMin), 1f, grid);
        AddLine(vh, new Vector2(plot.xMin, plot.yMin), new Vector2(plot.xMin, plot.yMax), 1f, grid);
        AddLine(vh, new Vector2(plot.xMin, plot.yMax), new Vector2(plot.xMax, plot.yMax), 1f, grid);
        AddLine(vh, new Vector2(plot.xMax, plot.yMin), new Vector2(plot.xMax, plot.yMax), 1f, grid);

        for (int i = 1; i < 4; i++)
        {
            float x = Mathf.Lerp(plot.xMin, plot.xMax, i / 4f);
            float y = Mathf.Lerp(plot.yMin, plot.yMax, i / 4f);
            AddLine(vh, new Vector2(x, plot.yMin), new Vector2(x, plot.yMax), 1f, grid);
            AddLine(vh, new Vector2(plot.xMin, y), new Vector2(plot.xMax, y), 1f, grid);
        }
    }

    void AddLine(VertexHelper vh, Vector2 a, Vector2 b, float width, Color c)
    {
        Vector2 dir = (b - a).normalized;
        if (dir.sqrMagnitude < 0.0001f) return;
        Vector2 n = new Vector2(-dir.y, dir.x) * width * 0.5f;
        int start = vh.currentVertCount;

        UIVertex v = UIVertex.simpleVert;
        v.color = c;
        v.position = a - n; vh.AddVert(v);
        v.position = a + n; vh.AddVert(v);
        v.position = b + n; vh.AddVert(v);
        v.position = b - n; vh.AddVert(v);
        vh.AddTriangle(start, start + 1, start + 2);
        vh.AddTriangle(start, start + 2, start + 3);
    }
}
