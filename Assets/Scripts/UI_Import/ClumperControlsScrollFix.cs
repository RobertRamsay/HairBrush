using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// When CLUMPER is active it owns the editable portion of the right panel, while the
// shared top utility rows (Texture Editor + Save/Reset) remain available. The clumper
// itself lives in a wheel-scrollable viewport below those rows; slider/button pointer
// events go directly to the controls rather than through a drag-stealing ScrollRect.
// The gap left for those utility rows is measured live each frame (see UpdateTopInset)
// rather than assumed at a fixed pixel height, since that assumption drifting out of sync
// with their real rendered height is what let this overlay start covering them.
[DefaultExecutionOrder(5250)]
public class ClumperControlsScrollFix : MonoBehaviour
{
    private ModelViewer viewer;
    private GameObject host;
    private RectTransform content;
    private Transform boundPanel;
    private readonly Dictionary<GameObject, bool> previousActive = new Dictionary<GameObject, bool>();

    // Fallback used only for the single frame before the utility rows can be measured.
    private const float FallbackTopInset = 101f;

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

        // Self-heal, and it is not theoretical. Selecting a GUIDE destroys the clumper's
        // controls, but Unity's Destroy is deferred: for the rest of that frame
        // content.Find("ClumperControls") can still return the doomed object, so it wins
        // FindControls and the freshly created GuideControls is caught by the hide sweep above.
        // Nothing else ever reactivates a controls object - previousActive is only replayed by
        // RestorePanel - so the guide would read as selected with a completely blank panel,
        // permanently. Asserting the chosen controls are active costs nothing and closes it
        // whichever way the destroy ordering happens to fall.
        if (!controls.gameObject.activeSelf) controls.gameObject.SetActive(true);
        previousActive.Remove(controls.gameObject);

        KeepUtilityRowsVisible(panel);
        UpdateTopInset(panel);
        host.transform.SetAsLastSibling();
    }

    // Measures the real current height of the persistent utility rows instead of assuming a
    // fixed pixel figure - a hardcoded guess here is exactly what let the overlay drift out of
    // sync with their actual rendered height and start covering the bottom of TopControlsRow.
    void UpdateTopInset(Transform panel)
    {
        if (host == null) return;
        RectTransform hostRT = host.transform as RectTransform;
        if (hostRT == null) return;

        Canvas.ForceUpdateCanvases();

        float inset = 0f;
        int visibleRows = 0;
        Transform tabs = panel.Find("PanelTabRow");
        if (tabs != null && tabs.gameObject.activeSelf)
        {
            inset += MeasuredHeight(tabs as RectTransform);
            visibleRows++;
        }
        Transform top = panel.Find("TopControlsRow");
        if (top != null && top.gameObject.activeSelf)
        {
            inset += MeasuredHeight(top as RectTransform);
            visibleRows++;
        }

        VerticalLayoutGroup panelLayout = panel.GetComponent<VerticalLayoutGroup>();
        if (panelLayout != null && visibleRows > 0)
            inset += panelLayout.spacing * visibleRows + panelLayout.padding.top;

        if (inset <= 0f) inset = FallbackTopInset;

        hostRT.offsetMax = new Vector2(hostRT.offsetMax.x, -inset);
    }

    static float MeasuredHeight(RectTransform rect)
    {
        if (rect == null) return 0f;
        float h = LayoutUtility.GetPreferredHeight(rect);
        if (h <= .01f) h = rect.rect.height;
        return Mathf.Max(0f, h);
    }

    // GUIDE controls are hosted by exactly the same mechanism as CLUMPER controls, and have to
    // be, for two reasons that both bite immediately without it.
    //
    // Visibility: a modifier panel appended to the groom panel's own VerticalLayoutGroup lands
    // below twenty-odd groom rows, off the bottom of the panel, with nothing on screen saying a
    // modifier is even selected.
    //
    // Interactivity: ModifierCoreLock disables every Slider under the groom panel whenever the
    // group has a POST and no POST is selected, exempting only ClumperControls/ClumperScrollHost.
    // Outside the host, every GUIDE slider is dead the moment the group also has a POST - the
    // symptom written up in claude/groom-panel-lock-why-sliders-are-dead.md.
    //
    // Only one of the two can exist at a time: GuideCurveManager and GroupClumperManager clear
    // each other's selection, so there is never a clumper panel and a guide panel to choose
    // between.
    Transform FindControls(Transform panel)
    {
        Transform found = FindNamed(panel, "ClumperControls");
        if (found != null) return found;
        return FindNamed(panel, "GuideControls");
    }

    Transform FindNamed(Transform panel, string name)
    {
        Transform direct = panel.Find(name);
        if (direct != null) return direct;

        if (content != null)
        {
            Transform nested = content.Find(name);
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
        // Force these to the very front of the sibling order every frame, rather than only
        // ensuring they're active. Whatever assigned their original sibling index (built once,
        // at whatever point panel construction happened to reach them) is what actually decides
        // where the VerticalLayoutGroup stacks them - measuring/insetting a separate overlay to
        // guess at that position was the wrong lever. Pinning the index directly is what
        // guarantees top placement regardless of build order or anything else that moves.
        Transform tabs = panel.Find("PanelTabRow");
        if (tabs != null)
        {
            if (!tabs.gameObject.activeSelf) tabs.gameObject.SetActive(true);
            tabs.SetSiblingIndex(0);
        }
        Transform top = panel.Find("TopControlsRow");
        if (top != null)
        {
            if (!top.gameObject.activeSelf) top.gameObject.SetActive(true);
            top.SetSiblingIndex(tabs != null ? 1 : 0);
        }
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
        hostRT.offsetMax = new Vector2(-4f, -FallbackTopInset);

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
