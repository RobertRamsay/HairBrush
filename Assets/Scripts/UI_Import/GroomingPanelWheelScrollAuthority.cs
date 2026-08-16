using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Makes the existing runtime grooming panel vertically wheel-scrollable without reparenting
// any controls. Many HairBrush authorities intentionally address rows as direct children of
// ModelViewer.groomingSliderPanelGO, so scrolling is implemented by shifting the panel's
// VerticalLayoutGroup padding rather than introducing a new Content hierarchy.
[DefaultExecutionOrder(9500)]
public class GroomingPanelWheelScrollAuthority : MonoBehaviour
{
    private ModelViewer viewer;
    private GameObject boundPanel;
    private GroomingPanelWheelScroll wheel;
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<GroomingPanelWheelScrollAuthority>() != null) return;
        GameObject go = new GameObject("GroomingPanelWheelScrollAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<GroomingPanelWheelScrollAuthority>();
    }

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + .12f;

        if (viewer == null) viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer == null || viewer.groomingSliderPanelGO == null) return;

        GameObject panel = viewer.groomingSliderPanelGO;
        if (boundPanel != panel || wheel == null)
            Bind(panel);

        if (wheel != null) wheel.RefreshClamp();
    }

    void Bind(GameObject panel)
    {
        boundPanel = panel;
        if (panel == null) return;

        // Clip rows that are moved above/below the visible panel bounds.
        RectMask2D mask = panel.GetComponent<RectMask2D>();
        if (mask == null) mask = panel.AddComponent<RectMask2D>();

        wheel = panel.GetComponent<GroomingPanelWheelScroll>();
        if (wheel == null) wheel = panel.AddComponent<GroomingPanelWheelScroll>();
        wheel.Bind(panel.GetComponent<RectTransform>(), panel.GetComponent<VerticalLayoutGroup>());
    }
}

public class GroomingPanelWheelScroll : MonoBehaviour, IScrollHandler
{
    private RectTransform panel;
    private VerticalLayoutGroup layout;
    private int baseTop;
    private int baseBottom;
    private float offset;

    public float sensitivity = 42f;

    public void Bind(RectTransform rect, VerticalLayoutGroup verticalLayout)
    {
        panel = rect;
        layout = verticalLayout;
        if (layout != null)
        {
            baseTop = layout.padding.top;
            baseBottom = layout.padding.bottom;
        }
        offset = 0f;
        ApplyOffset();
    }

    public void OnScroll(PointerEventData eventData)
    {
        if (eventData == null || panel == null || layout == null) return;

        // CLUMPER has its own dedicated scroll viewport while its controls are selected.
        // Let that child viewport own wheel input rather than moving the whole grooming panel.
        Transform clumperHost = transform.Find("ClumperScrollHost");
        if (clumperHost != null && clumperHost.gameObject.activeInHierarchy) return;

        float maxOffset = GetMaxOffset();
        if (maxOffset <= .5f)
        {
            offset = 0f;
            ApplyOffset();
            return;
        }

        offset = Mathf.Clamp(offset + (-eventData.scrollDelta.y * sensitivity), 0f, maxOffset);
        ApplyOffset();
        eventData.Use();
    }

    public void RefreshClamp()
    {
        if (panel == null || layout == null) return;
        float maxOffset = GetMaxOffset();
        float clamped = Mathf.Clamp(offset, 0f, maxOffset);
        if (!Mathf.Approximately(clamped, offset))
        {
            offset = clamped;
            ApplyOffset();
        }
    }

    float GetMaxOffset()
    {
        if (panel == null || layout == null) return 0f;
        Canvas.ForceUpdateCanvases();

        float contentHeight = baseTop + baseBottom;
        int activeLayoutChildren = 0;
        for (int i = 0; i < transform.childCount; i++)
        {
            RectTransform child = transform.GetChild(i) as RectTransform;
            if (child == null || !child.gameObject.activeInHierarchy) continue;

            LayoutElement element = child.GetComponent<LayoutElement>();
            if (element != null && element.ignoreLayout) continue;

            float h = LayoutUtility.GetPreferredHeight(child);
            if (h <= .01f) h = child.rect.height;
            if (h <= .01f) h = child.sizeDelta.y;
            contentHeight += Mathf.Max(0f, h);
            activeLayoutChildren++;
        }

        if (activeLayoutChildren > 1)
            contentHeight += layout.spacing * (activeLayoutChildren - 1);

        return Mathf.Max(0f, contentHeight - panel.rect.height + 8f);
    }

    void ApplyOffset()
    {
        if (layout == null) return;
        int rounded = Mathf.RoundToInt(offset);
        RectOffset p = layout.padding;
        p.top = baseTop - rounded;
        p.bottom = baseBottom + rounded;
        layout.padding = p;
        LayoutRebuilder.MarkLayoutForRebuild(panel);
    }
}
