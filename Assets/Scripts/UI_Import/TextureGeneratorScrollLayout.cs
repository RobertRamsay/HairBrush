using UnityEngine;
using UnityEngine.UI;

// Keeps the Texture Generator usable as controls are added. The tab/header/status remain
// visible while the parameter stack scrolls inside the available height of the right panel.
[DefaultExecutionOrder(9300)]
public class TextureGeneratorScrollLayout : MonoBehaviour
{
    private GameObject processedPanel;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<TextureGeneratorScrollLayout>() != null) return;
        GameObject go = new GameObject("TextureGeneratorScrollLayout");
        DontDestroyOnLoad(go);
        go.AddComponent<TextureGeneratorScrollLayout>();
    }

    void Update()
    {
        GameObject panel = FindPanel();
        if (panel == null || panel == processedPanel) return;
        if (panel.transform.Find("ControlsScrollView") != null)
        {
            processedPanel = panel;
            return;
        }

        BuildScrollLayout(panel);
        processedPanel = panel;
    }

    static GameObject FindPanel()
    {
        foreach (Transform t in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (t != null && t.name == "TextureGeneratorControlsPanel")
                return t.gameObject;
        return null;
    }

    static void BuildScrollLayout(GameObject panel)
    {
        Transform panelTransform = panel.transform;
        VerticalLayoutGroup panelLayout = panel.GetComponent<VerticalLayoutGroup>();
        if (panelLayout == null) return;

        // Fixed controls stay above the scroll area.
        Transform tabRow = panelTransform.Find("PanelTabRow");
        Transform header = panelTransform.Find("ACTIVE CLUSTER CONTROLS");
        Transform status = panelTransform.Find("PlacementStatus");

        GameObject scrollGO = new GameObject(
            "ControlsScrollView",
            typeof(RectTransform), typeof(Image), typeof(ScrollRect), typeof(LayoutElement));
        scrollGO.transform.SetParent(panelTransform, false);
        scrollGO.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.10f);

        LayoutElement scrollLayout = scrollGO.GetComponent<LayoutElement>();
        scrollLayout.flexibleHeight = 1f;
        scrollLayout.minHeight = 180f;

        GameObject viewportGO = new GameObject(
            "Viewport", typeof(RectTransform), typeof(Image), typeof(RectMask2D));
        viewportGO.transform.SetParent(scrollGO.transform, false);
        RectTransform viewportRect = viewportGO.GetComponent<RectTransform>();
        viewportRect.anchorMin = Vector2.zero;
        viewportRect.anchorMax = Vector2.one;
        viewportRect.offsetMin = Vector2.zero;
        viewportRect.offsetMax = new Vector2(-12f, 0f);
        viewportGO.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.001f);

        GameObject contentGO = new GameObject(
            "Content",
            typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
        contentGO.transform.SetParent(viewportGO.transform, false);
        RectTransform contentRect = contentGO.GetComponent<RectTransform>();
        contentRect.anchorMin = new Vector2(0f, 1f);
        contentRect.anchorMax = new Vector2(1f, 1f);
        contentRect.pivot = new Vector2(0.5f, 1f);
        contentRect.anchoredPosition = Vector2.zero;
        contentRect.sizeDelta = Vector2.zero;

        VerticalLayoutGroup contentLayout = contentGO.GetComponent<VerticalLayoutGroup>();
        contentLayout.spacing = 6f;
        contentLayout.padding = new RectOffset(0, 4, 2, 8);
        contentLayout.childControlWidth = true;
        contentLayout.childControlHeight = false;
        contentLayout.childForceExpandWidth = true;
        contentLayout.childForceExpandHeight = false;

        ContentSizeFitter fitter = contentGO.GetComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        GameObject scrollbarGO = new GameObject(
            "Scrollbar Vertical", typeof(RectTransform), typeof(Image), typeof(Scrollbar));
        scrollbarGO.transform.SetParent(scrollGO.transform, false);
        RectTransform scrollbarRect = scrollbarGO.GetComponent<RectTransform>();
        scrollbarRect.anchorMin = new Vector2(1f, 0f);
        scrollbarRect.anchorMax = new Vector2(1f, 1f);
        scrollbarRect.pivot = new Vector2(1f, 0.5f);
        scrollbarRect.sizeDelta = new Vector2(9f, 0f);
        scrollbarRect.anchoredPosition = Vector2.zero;
        scrollbarGO.GetComponent<Image>().color = new Color(0.18f, 0.18f, 0.18f, 0.8f);

        GameObject handleGO = new GameObject("Sliding Area", typeof(RectTransform));
        handleGO.transform.SetParent(scrollbarGO.transform, false);
        RectTransform slidingRect = handleGO.GetComponent<RectTransform>();
        slidingRect.anchorMin = Vector2.zero;
        slidingRect.anchorMax = Vector2.one;
        slidingRect.offsetMin = Vector2.zero;
        slidingRect.offsetMax = Vector2.zero;

        GameObject thumbGO = new GameObject("Handle", typeof(RectTransform), typeof(Image));
        thumbGO.transform.SetParent(handleGO.transform, false);
        RectTransform thumbRect = thumbGO.GetComponent<RectTransform>();
        thumbRect.anchorMin = Vector2.zero;
        thumbRect.anchorMax = Vector2.one;
        thumbRect.offsetMin = Vector2.zero;
        thumbRect.offsetMax = Vector2.zero;
        thumbGO.GetComponent<Image>().color = new Color(0.35f, 0.65f, 0.95f, 0.9f);

        Scrollbar scrollbar = scrollbarGO.GetComponent<Scrollbar>();
        scrollbar.handleRect = thumbRect;
        scrollbar.direction = Scrollbar.Direction.BottomToTop;

        ScrollRect scroll = scrollGO.GetComponent<ScrollRect>();
        scroll.viewport = viewportRect;
        scroll.content = contentRect;
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 28f;
        scroll.verticalScrollbar = scrollbar;
        scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHideAndExpandViewport;
        scroll.verticalScrollbarSpacing = 2f;

        // Everything after PlacementStatus belongs to the scrolling parameter stack.
        // Cache first because reparenting changes sibling indices immediately.
        System.Collections.Generic.List<Transform> movable = new System.Collections.Generic.List<Transform>();
        for (int i = 0; i < panelTransform.childCount; i++)
        {
            Transform child = panelTransform.GetChild(i);
            if (child == tabRow || child == header || child == status || child == scrollGO.transform) continue;
            movable.Add(child);
        }

        foreach (Transform child in movable)
            child.SetParent(contentGO.transform, false);

        // Ensure the scroll area sits after the fixed status block.
        int scrollIndex = status != null ? status.GetSiblingIndex() + 1 : panelTransform.childCount - 1;
        scrollGO.transform.SetSiblingIndex(Mathf.Clamp(scrollIndex, 0, panelTransform.childCount - 1));

        LayoutRebuilder.ForceRebuildLayoutImmediate(panel.GetComponent<RectTransform>());
        LayoutRebuilder.ForceRebuildLayoutImmediate(contentRect);
    }
}
