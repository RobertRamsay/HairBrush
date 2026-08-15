using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Keeps the normal Groom/POST panel unchanged, but when GroupClumperManager creates
// its ClumperControls block, present that block in a dedicated wheel-scrollable viewport.
// Important: this is wheel-scroll ONLY. No full-screen raycast graphic sits above the
// controls: sliders/buttons receive pointer events directly, while wheel scrolling is
// handled by the viewport only when it is actually the raycast target.
[DefaultExecutionOrder(5250)]
public class ClumperControlsScrollFix : MonoBehaviour
{
    private ModelViewer viewer;
    private GameObject host;
    private RectTransform content;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<ClumperControlsScrollFix>() != null) return;
        GameObject go = new GameObject("ClumperControlsScrollFix");
        DontDestroyOnLoad(go);
        go.AddComponent<ClumperControlsScrollFix>();
    }

    void LateUpdate()
    {
        if (viewer == null) viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer == null || viewer.groomingSliderPanelGO == null)
        {
            DestroyHost();
            return;
        }

        Transform panel = viewer.groomingSliderPanelGO.transform;
        Transform controls = FindControls(panel);

        if (controls == null)
        {
            if (host != null && (content == null || content.childCount == 0)) DestroyHost();
            return;
        }

        if (host == null) BuildHost(panel);
        if (host == null || content == null) return;

        if (controls.parent != content)
        {
            RectTransform rt = controls as RectTransform;
            controls.SetParent(content, false);
            if (rt != null)
            {
                rt.anchorMin = new Vector2(0f, 1f);
                rt.anchorMax = new Vector2(1f, 1f);
                rt.pivot = new Vector2(.5f, 1f);
                rt.anchoredPosition = Vector2.zero;
                rt.sizeDelta = new Vector2(0f, rt.sizeDelta.y);
            }

            ContentSizeFitter fitter = controls.GetComponent<ContentSizeFitter>();
            if (fitter == null) fitter = controls.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        }

        host.transform.SetAsLastSibling();
    }

    Transform FindControls(Transform panel)
    {
        Transform direct = panel.Find("ClumperControls");
        if (direct != null) return direct;

        if (content != null)
        {
            Transform nested = content.Find("ClumperControls");
            if (nested != null) return nested;
        }
        return null;
    }

    void BuildHost(Transform panel)
    {
        host = new GameObject("ClumperScrollHost", typeof(RectTransform), typeof(LayoutElement));
        host.transform.SetParent(panel, false);

        LayoutElement layoutElement = host.GetComponent<LayoutElement>();
        layoutElement.ignoreLayout = true;

        RectTransform hostRT = host.GetComponent<RectTransform>();
        hostRT.anchorMin = Vector2.zero;
        hostRT.anchorMax = Vector2.one;
        hostRT.offsetMin = new Vector2(4f, 4f);
        hostRT.offsetMax = new Vector2(-4f, -4f);

        // The viewport is behind the content. It catches wheel events only in empty space;
        // child sliders/buttons are later in the hierarchy and therefore win raycasts.
        GameObject viewportGO = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(RectMask2D), typeof(ClumperWheelScroll));
        viewportGO.transform.SetParent(host.transform, false);
        RectTransform viewport = viewportGO.GetComponent<RectTransform>();
        viewport.anchorMin = Vector2.zero;
        viewport.anchorMax = Vector2.one;
        viewport.offsetMin = new Vector2(6f, 6f);
        viewport.offsetMax = new Vector2(-6f, -6f);
        Image viewportImage = viewportGO.GetComponent<Image>();
        viewportImage.color = new Color(0f, 0f, 0f, 0.001f);
        viewportImage.raycastTarget = true;

        GameObject contentGO = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
        contentGO.transform.SetParent(viewportGO.transform, false);
        content = contentGO.GetComponent<RectTransform>();
        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = new Vector2(1f, 1f);
        content.pivot = new Vector2(.5f, 1f);
        content.anchoredPosition = Vector2.zero;
        content.sizeDelta = Vector2.zero;

        VerticalLayoutGroup outerLayout = contentGO.GetComponent<VerticalLayoutGroup>();
        outerLayout.padding = new RectOffset(2, 2, 2, 12);
        outerLayout.spacing = 0f;
        outerLayout.childControlWidth = true;
        outerLayout.childControlHeight = false;
        outerLayout.childForceExpandWidth = true;
        outerLayout.childForceExpandHeight = false;

        ContentSizeFitter outerFitter = contentGO.GetComponent<ContentSizeFitter>();
        outerFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        outerFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        ClumperWheelScroll wheel = viewportGO.GetComponent<ClumperWheelScroll>();
        wheel.viewport = viewport;
        wheel.content = content;
        wheel.sensitivity = 34f;
    }

    void DestroyHost()
    {
        if (host != null) Destroy(host);
        host = null;
        content = null;
    }
}

// Minimal scroll handler deliberately implementing ONLY wheel scrolling.
// It never handles pointer down/begin-drag/drag, leaving those events to Slider.
public class ClumperWheelScroll : MonoBehaviour, IScrollHandler
{
    public RectTransform viewport;
    public RectTransform content;
    public float sensitivity = 34f;

    public void OnScroll(PointerEventData eventData)
    {
        if (viewport == null || content == null) return;
        Canvas.ForceUpdateCanvases();

        float viewportHeight = viewport.rect.height;
        float contentHeight = LayoutUtility.GetPreferredHeight(content);
        float maxOffset = Mathf.Max(0f, contentHeight - viewportHeight);
        if (maxOffset <= 0f)
        {
            content.anchoredPosition = Vector2.zero;
            return;
        }

        Vector2 pos = content.anchoredPosition;
        pos.y = Mathf.Clamp(pos.y + (-eventData.scrollDelta.y * sensitivity), 0f, maxOffset);
        content.anchoredPosition = pos;
        eventData.Use();
    }
}
