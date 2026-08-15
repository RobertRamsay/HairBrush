using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// When CLUMPER is active it owns the editable portion of the right panel, while the
// shared top utility rows (Texture Editor + Save/Reset) remain available. The clumper
// itself lives in a wheel-scrollable viewport below those rows; slider/button pointer
// events go directly to the controls rather than through a drag-stealing ScrollRect.
[DefaultExecutionOrder(5250)]
public class ClumperControlsScrollFix : MonoBehaviour
{
    private ModelViewer viewer;
    private GameObject host;
    private RectTransform content;
    private Transform boundPanel;
    private readonly Dictionary<GameObject, bool> previousActive = new Dictionary<GameObject, bool>();

    // Runtime grooming panel uses a 45px editor-tab row + 40px utility row, with panel
    // padding/spacing around them. Leave enough room so CLUMPER starts cleanly below both.
    private const float TopUtilityInset = 101f;

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
            RestorePanel();
            DestroyHost();
            return;
        }

        Transform panel = viewer.groomingSliderPanelGO.transform;
        Transform controls = FindControls(panel);

        if (controls == null)
        {
            RestorePanel();
            if (host != null && (content == null || content.childCount == 0)) DestroyHost();
            return;
        }

        if (boundPanel != panel)
        {
            RestorePanel();
            boundPanel = panel;
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

        HideNonClumperPanelChildren(panel);
        KeepUtilityRowsVisible(panel);
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

    static bool IsPersistentUtilityRow(GameObject go)
    {
        if (go == null) return false;
        return go.name == "PanelTabRow" || go.name == "TopControlsRow";
    }

    void HideNonClumperPanelChildren(Transform panel)
    {
        for (int i = 0; i < panel.childCount; i++)
        {
            GameObject go = panel.GetChild(i).gameObject;
            if (go == host || IsPersistentUtilityRow(go)) continue;
            if (!previousActive.ContainsKey(go)) previousActive[go] = go.activeSelf;
            if (go.activeSelf) go.SetActive(false);
        }
    }

    void KeepUtilityRowsVisible(Transform panel)
    {
        Transform tabs = panel.Find("PanelTabRow");
        if (tabs != null && !tabs.gameObject.activeSelf) tabs.gameObject.SetActive(true);
        Transform top = panel.Find("TopControlsRow");
        if (top != null && !top.gameObject.activeSelf) top.gameObject.SetActive(true);
    }

    void RestorePanel()
    {
        foreach (var kv in previousActive)
        {
            if (kv.Key != null) kv.Key.SetActive(kv.Value);
        }
        previousActive.Clear();
        boundPanel = null;
    }

    void BuildHost(Transform panel)
    {
        host = new GameObject("ClumperScrollHost", typeof(RectTransform), typeof(LayoutElement), typeof(Image));
        host.transform.SetParent(panel, false);

        LayoutElement layoutElement = host.GetComponent<LayoutElement>();
        layoutElement.ignoreLayout = true;

        RectTransform hostRT = host.GetComponent<RectTransform>();
        hostRT.anchorMin = Vector2.zero;
        hostRT.anchorMax = Vector2.one;
        hostRT.offsetMin = new Vector2(4f, 4f);
        hostRT.offsetMax = new Vector2(-4f, -TopUtilityInset);

        Image hostImage = host.GetComponent<Image>();
        hostImage.color = new Color(.08f, .09f, .11f, .98f);
        hostImage.raycastTarget = false;

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

    void OnDestroy()
    {
        RestorePanel();
    }
}

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
