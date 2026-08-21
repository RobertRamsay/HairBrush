using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// Freezes the top rows of the grooming panel, spreadsheet style: MENU / TEXTURE MODE,
// SAVE PROJ / RESET / EXPORT OBJ and the PLACEMENT mode strip stay put while everything
// below them scrolls under.
//
// GroomingPanelWheelScrollAuthority scrolls by shifting the panel's VerticalLayoutGroup
// padding, deliberately keeping every row a direct child of the panel because so many
// other authorities look rows up that way. Reparenting the header into its own container
// would break all of those, so the frozen rows are instead taken out of the layout flow
// (ignoreLayout) and parked against the panel's top edge by hand:
//
//   - a spacer of exactly the header's height takes the header's old place in the flow,
//     so unscrolled content still starts below it and the wheel script's existing
//     content-height maths needs no adjustment at all;
//   - an opaque backdrop sits behind the frozen rows so scrolled content passing beneath
//     them is hidden rather than showing through the gaps between buttons;
//   - drawing on top is done with a per-row nested Canvas using overrideSorting, NOT by
//     moving the rows to the end of the sibling list. Sibling index is load-bearing here:
//     ClumperControlsScrollFix pins the two utility rows to indices 0 and 1 every frame,
//     and both SelectionBrushScaleTuning and PlacementBrushModeAuthority insert their own
//     rows at "just after TopControlsRow". Reordering would fight the first and send the
//     others to the bottom of the panel.
[DefaultExecutionOrder(9450)]
public class GroomingPanelFrozenHeaderAuthority : MonoBehaviour
{
    // The rows pinned to the top, in the order they appear. Anything not present yet -
    // PlacementModeRow is built a fraction of a second after the panel - is simply
    // skipped until it exists.
    //
    // Radius and Falloff are the POST affector's spatial controls, and they belong up here for
    // the same reason the mode strip does: they are what you reach for constantly while
    // shaping a POST, and having to scroll back up to them every time breaks the loop. They
    // are also the only two frozen rows that come and go - SelectionBrushScaleTuning builds
    // them when a POST is selected and DESTROYS them when it is deselected, so they simply
    // drop out of the list and the header shrinks back by itself. No special casing needed:
    // CollectFrozenRows already skips names it cannot find, and Apply sizes the spacer from
    // whatever it actually collected.
    //
    // This is POST only by construction. The CLUMPER's own Radius/Falloff live inside
    // ClumperScrollHost, which LateUpdate stands clear of entirely.
    private static readonly string[] FrozenRowNames =
    {
        "PanelTabRow", "TopControlsRow", "PlacementModeRow",
        "Radius_Row",

        // BOTH falloff spellings, because two different paths build that row and they do not
        // agree on its label:
        //
        //   ModelViewer.cs                creates it as "Falloff Dist"  -> "Falloff Dist_Row"
        //   SelectionBrushScaleTuning.cs  creates it as "Falloff"       -> "Falloff_Row"
        //
        // SelectionBrushScaleTuning only builds its own when ModelViewer's legacy row is
        // absent, so exactly one of the two is live at a time - but which one depends on how
        // the POST was entered. Listing only "Falloff_Row" is why Radius froze and Falloff
        // scrolled away with the rest of the panel. Naming both costs nothing: a name that is
        // not present is skipped, so the header still stacks Radius then whichever falloff
        // row actually exists.
        "Falloff Dist_Row", "Falloff_Row"
    };

    private const string SpacerName = "FrozenHeaderSpacer";
    private const string BackdropName = "FrozenHeaderBackdrop";
    private const string SeparatorName = "FrozenHeaderSeparator";

    // Leaves the 6px scrollbar and its 2px inset clear of the backdrop.
    private const float ScrollbarClearance = 10f;

    // Sorting orders for the nested canvases: backdrop below the separator, both below
    // the rows themselves.
    private const int BackdropSortingOrder = 10;
    private const int SeparatorSortingOrder = 11;
    private const int RowSortingOrder = 12;
    private const float SeparatorHeight = 2f;
    private const float BackdropPadding = 4f;

    private ModelViewer viewer;
    private GameObject boundPanel;
    private RectTransform spacer;
    private RectTransform backdrop;
    private RectTransform separator;

    private readonly List<RectTransform> frozen = new List<RectTransform>();
    private readonly Dictionary<RectTransform, float> frozenHeights = new Dictionary<RectTransform, float>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<GroomingPanelFrozenHeaderAuthority>() != null) return;
        GameObject go = new GameObject(nameof(GroomingPanelFrozenHeaderAuthority));
        DontDestroyOnLoad(go);
        go.AddComponent<GroomingPanelFrozenHeaderAuthority>();
    }

    void Awake()
    {
        viewer = null;
        boundPanel = null;
        spacer = null;
        backdrop = null;
        separator = null;
        frozen.Clear();
        frozenHeights.Clear();
    }

    void LateUpdate()
    {
        if (viewer == null) viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer == null) return;

        GameObject panel = viewer.groomingSliderPanelGO;
        if (panel == null || !panel.activeInHierarchy)
        {
            boundPanel = null;
            return;
        }

        if (boundPanel != panel)
        {
            boundPanel = panel;
            spacer = null;
            backdrop = null;
            separator = null;
            frozen.Clear();
            frozenHeights.Clear();
        }

        // ClumperControlsScrollFix takes the panel over while a clumper is being edited:
        // it hides every other row, pins the two utility rows to indices 0 and 1 itself
        // every frame, and insets its own scroll host below them. Stand clear until it
        // hands the panel back, rather than fighting it for the same rows.
        Transform clumperHost = panel.transform.Find("ClumperScrollHost");
        if (clumperHost != null && clumperHost.gameObject.activeInHierarchy) return;

        VerticalLayoutGroup layout = panel.GetComponent<VerticalLayoutGroup>();
        if (layout == null) return;

        CollectFrozenRows(panel.transform);
        if (frozen.Count == 0) return;

        Apply(panel, layout);
    }

    void CollectFrozenRows(Transform panel)
    {
        frozen.Clear();
        foreach (string rowName in FrozenRowNames)
        {
            Transform row = panel.Find(rowName);
            if (row == null) continue;
            if (!row.gameObject.activeSelf) continue;

            RectTransform rect = row as RectTransform;
            if (rect == null) continue;

            // The row's authored height is captured the first time it is seen, because
            // from then on this component owns its sizeDelta and would otherwise be
            // reading its own value back.
            if (!frozenHeights.ContainsKey(rect))
            {
                float height = rect.sizeDelta.y;
                if (height <= 1f) height = LayoutUtility.GetPreferredHeight(rect);
                if (height <= 1f) height = rect.rect.height;
                frozenHeights[rect] = Mathf.Max(1f, height);
            }

            frozen.Add(rect);
        }
    }

    void Apply(GameObject panel, VerticalLayoutGroup layout)
    {
        float spacing = layout.spacing;
        int left = layout.padding.left;
        int right = layout.padding.right;
        float topPadding = ResolveBaseTopPadding(panel, layout);

        float headerHeight = 0f;
        foreach (RectTransform row in frozen) headerHeight += frozenHeights[row];
        if (frozen.Count > 1) headerHeight += spacing * (frozen.Count - 1);

        EnsureSpacer(panel.transform);
        if (spacer != null)
        {
            SetSize(spacer, new Vector2(0f, headerHeight));
            if (spacer.gameObject.activeSelf && spacer.GetSiblingIndex() != 0) spacer.SetAsFirstSibling();
        }

        float bandHeight = topPadding + headerHeight + BackdropPadding;
        EnsureBackdrop(panel, bandHeight);
        EnsureSeparator(panel.transform, bandHeight);

        // Park each frozen row against the panel's top edge, in order.
        float y = -topPadding;
        foreach (RectTransform row in frozen)
        {
            LayoutElement element = row.GetComponent<LayoutElement>();
            if (element == null) element = row.gameObject.AddComponent<LayoutElement>();
            if (!element.ignoreLayout) element.ignoreLayout = true;

            float height = frozenHeights[row];
            SetAnchors(row);
            SetPosition(row, new Vector2((left - right) * .5f, y));
            SetSize(row, new Vector2(-(left + right), height));

            LiftAbovePanel(row.gameObject, RowSortingOrder, true);

            y -= height + spacing;
        }
    }

    // A nested Canvas with overrideSorting draws this object above its siblings whatever
    // its position in the child list. A nested Canvas also needs its own raycaster, or the
    // buttons inside it stop receiving clicks.
    static void LiftAbovePanel(GameObject go, int sortingOrder, bool needsRaycaster)
    {
        Canvas canvas = go.GetComponent<Canvas>();
        if (canvas == null) canvas = go.AddComponent<Canvas>();
        if (!canvas.overrideSorting) canvas.overrideSorting = true;
        if (canvas.sortingOrder != sortingOrder) canvas.sortingOrder = sortingOrder;

        if (!needsRaycaster) return;
        if (go.GetComponent<GraphicRaycaster>() == null) go.AddComponent<GraphicRaycaster>();
    }

    float ResolveBaseTopPadding(GameObject panel, VerticalLayoutGroup layout)
    {
        // layout.padding.top carries the live scroll offset, so ask the scroll component
        // for the real baseline. Before it has captured one, the padding IS the baseline.
        GroomingPanelWheelScroll wheel = panel.GetComponent<GroomingPanelWheelScroll>();
        if (wheel != null && wheel.HasBasePadding) return wheel.BaseTopPadding;
        return layout.padding.top;
    }

    void EnsureSpacer(Transform panel)
    {
        if (spacer != null) return;

        Transform existing = panel.Find(SpacerName);
        if (existing is RectTransform found)
        {
            spacer = found;
            return;
        }

        GameObject go = new GameObject(SpacerName, typeof(RectTransform));
        go.transform.SetParent(panel, false);
        spacer = go.GetComponent<RectTransform>();
        spacer.SetAsFirstSibling();
    }

    void EnsureBackdrop(GameObject panel, float bandHeight)
    {
        if (backdrop == null)
        {
            Transform existing = panel.transform.Find(BackdropName);
            if (existing is RectTransform found)
            {
                backdrop = found;
            }
            else
            {
                GameObject go = new GameObject(BackdropName, typeof(RectTransform), typeof(Image), typeof(LayoutElement));
                go.transform.SetParent(panel.transform, false);
                go.GetComponent<LayoutElement>().ignoreLayout = true;
                backdrop = go.GetComponent<RectTransform>();
            }
        }

        if (backdrop == null) return;

        Image image = backdrop.GetComponent<Image>();
        if (image != null)
        {
            // Match the panel, but fully opaque - content sliding underneath must not
            // ghost through the header.
            Image panelImage = panel.GetComponent<Image>();
            Color colour = new Color(.15f, .15f, .15f, 1f);
            if (panelImage != null) colour = new Color(panelImage.color.r, panelImage.color.g, panelImage.color.b, 1f);
            if (image.color != colour) image.color = colour;
            image.raycastTarget = false;
        }

        SetAnchors(backdrop);
        SetPosition(backdrop, new Vector2(-ScrollbarClearance * .5f, 0f));
        SetSize(backdrop, new Vector2(-ScrollbarClearance, bandHeight));
        LiftAbovePanel(backdrop.gameObject, BackdropSortingOrder, false);
    }

    void EnsureSeparator(Transform panel, float bandHeight)
    {
        if (separator == null)
        {
            Transform existing = panel.Find(SeparatorName);
            if (existing is RectTransform found)
            {
                separator = found;
            }
            else
            {
                GameObject go = new GameObject(SeparatorName, typeof(RectTransform), typeof(Image), typeof(LayoutElement));
                go.transform.SetParent(panel, false);
                go.GetComponent<LayoutElement>().ignoreLayout = true;
                Image line = go.GetComponent<Image>();
                line.color = new Color(.30f, .65f, .70f, .55f);
                line.raycastTarget = false;
                separator = go.GetComponent<RectTransform>();
            }
        }

        if (separator == null) return;

        SetAnchors(separator);
        SetPosition(separator, new Vector2(-ScrollbarClearance * .5f, -bandHeight));
        SetSize(separator, new Vector2(-ScrollbarClearance, SeparatorHeight));
        LiftAbovePanel(separator.gameObject, SeparatorSortingOrder, false);
    }

    static void SetAnchors(RectTransform rect)
    {
        Vector2 min = new Vector2(0f, 1f);
        Vector2 max = new Vector2(1f, 1f);
        Vector2 pivot = new Vector2(.5f, 1f);
        if (rect.anchorMin != min) rect.anchorMin = min;
        if (rect.anchorMax != max) rect.anchorMax = max;
        if (rect.pivot != pivot) rect.pivot = pivot;
    }

    // Only write when the value actually differs - re-assigning identical values every
    // frame keeps marking the layout dirty for no reason.
    static void SetPosition(RectTransform rect, Vector2 position)
    {
        if (rect.anchoredPosition != position) rect.anchoredPosition = position;
    }

    static void SetSize(RectTransform rect, Vector2 size)
    {
        if (rect.sizeDelta != size) rect.sizeDelta = size;
    }
}
