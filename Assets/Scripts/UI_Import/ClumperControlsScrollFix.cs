using UnityEngine;
using UnityEngine.UI;

// Keeps the normal Groom/POST panel unchanged, but when GroupClumperManager creates
// its ClumperControls block, present that block in a dedicated wheel-scrollable viewport.
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
        host = new GameObject("ClumperScrollHost", typeof(RectTransform), typeof(Image), typeof(ScrollRect), typeof(LayoutElement));
        host.transform.SetParent(panel, false);

        // Critical: the Groom panel itself has a VerticalLayoutGroup. This scroll view is
        // an overlay, not another row in that layout, otherwise it gets collapsed to 0 height.
        LayoutElement layoutElement = host.GetComponent<LayoutElement>();
        layoutElement.ignoreLayout = true;

        RectTransform hostRT = host.GetComponent<RectTransform>();
        hostRT.anchorMin = Vector2.zero;
        hostRT.anchorMax = Vector2.one;
        hostRT.offsetMin = new Vector2(4f, 4f);
        hostRT.offsetMax = new Vector2(-4f, -4f);

        Image hostImage = host.GetComponent<Image>();
        hostImage.color = new Color(.08f, .09f, .11f, .98f);
        hostImage.raycastTarget = true;

        GameObject viewportGO = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(RectMask2D));
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

        ScrollRect scroll = host.GetComponent<ScrollRect>();
        scroll.viewport = viewport;
        scroll.content = content;
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.inertia = true;
        scroll.decelerationRate = .12f;
        scroll.scrollSensitivity = 32f;
        scroll.verticalNormalizedPosition = 1f;
    }

    void DestroyHost()
    {
        if (host != null) Destroy(host);
        host = null;
        content = null;
    }
}
