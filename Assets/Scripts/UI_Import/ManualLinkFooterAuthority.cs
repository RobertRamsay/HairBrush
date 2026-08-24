using TMPro;
using UnityEngine;
using UnityEngine.UI;

// OPEN MANUAL, pinned to the bottom of the left panel.
//
// The panel is a VerticalLayoutGroup whose last flowing child is the group list, and the group
// list is the one thing on screen that grows without limit. A help link placed in that flow would
// be pushed off the bottom by the fourth group. So the footer is taken out of the flow entirely,
// anchored to the panel's bottom edge, and the group list is shortened to stop above it.
//
// The shortening is the reason this is an authority rather than a few lines in
// ModelViewer.BuildGroupManagementUI. GroupScrollView's height is written there as a flat 600,
// which was already only correct at one window size, and the whole left panel is destroyed and
// rebuilt on every model and project load. The height is therefore recomputed here each scan from
// what the other panel children actually occupy, and re-applied after every rebuild.
[DefaultExecutionOrder(9050)]
public class ManualLinkFooterAuthority : MonoBehaviour
{
    // The published manual. Same convention as the two GitHub URLs already in the project:
    // a named constant at the top of the class, so a new link is a one-line change.
    //
    // A Drive file share rather than the Google Doc this used to point at. /view is Drive's
    // read-only viewer, which is what a link handed to every copy of the build should be - the
    // check worth repeating whenever this changes is that the URL cannot carry edit rights, so
    // no /edit form and no share set to "anyone with the link can edit".
    private const string ManualUrl =
        "https://drive.google.com/file/d/1jF8IKTM0yhYRrZu_eFEoMpgehs7itVPf/view?usp=sharing";

    // Other authorities that reorder the panel need to know these by name.
    public const string FooterName = "ManualFooter";
    public const string ButtonName = "OpenManualButton";

    private const string PanelName = "GroupManagerPanel";
    private const string ScrollName = "GroupScrollView";

    private const float ScanInterval = .15f;

    // Strip height, and the gap left between it and the bottom of the group list. The button
    // itself is 30, which sits inside UITheme.ClampButtonSize's 26-32 band, so the theme pass
    // leaves it alone and this component does not need to opt out of skinning.
    //
    // The strip runs edge to edge, which is what makes it read as a fixed edge of the panel
    // rather than one more row. The button inside it is inset by the panel's own 10, so it
    // lines up with every control in the flow above.
    private const float StripHeight = 40f;
    private const float ButtonHeight = 30f;
    private const float SideInset = 10f;
    private const float StripGap = 6f;
    private const float DividerHeight = 1f;

    // Roughly one group row. Below this the panel is over-subscribed no matter what, and the
    // strip stands down rather than trapping the bottom of the list underneath itself.
    //
    // Deliberately low. The workspace canvas matches on WIDTH against a 1920 reference, so the
    // panel's height in canvas units is 1080 only at 16:9 and shrinks as the display gets wider:
    // a 32:9 monitor gives 540, which leaves about 76 units for the list once the fixed rows and
    // this strip are paid for. A threshold anywhere near that would take the manual button away
    // from ultrawide users permanently, with nothing on screen to explain why.
    private const float MinScrollHeight = 56f;

    private Transform boundPanel;
    private RectTransform footer;
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<ManualLinkFooterAuthority>() != null) return;
        GameObject go = new GameObject(nameof(ManualLinkFooterAuthority));
        DontDestroyOnLoad(go);
        go.AddComponent<ManualLinkFooterAuthority>();
    }

    void Awake()
    {
        boundPanel = null;
        footer = null;
        nextScan = 0f;
    }

    // LateUpdate, and ordered after GroupPanelPostHintStats at 9000: that component reindexes the
    // panel's named children every scan, and the scroll height computed here has to be measured
    // against the arrangement it settles on rather than the one before it.
    void LateUpdate()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + ScanInterval;

        GameObject panelGO = GameObject.Find(PanelName);
        if (panelGO == null)
        {
            boundPanel = null;
            footer = null;
            return;
        }

        Transform panel = panelGO.transform;
        if (boundPanel != panel)
        {
            boundPanel = panel;
            footer = null;
        }

        if (footer == null) footer = Resolve(panel);

        // The scroll height still has to be maintained if the strip could not be resolved,
        // otherwise a failure to build the button would also leave the list at its raw 600.
        if (footer != null) KeepOnTop(footer);
        ApplyScrollHeight(panel as RectTransform, footer);
    }

    // Adopts an existing footer before building one. The panel survives a domain reload with its
    // children intact in the editor, and a second strip would sit exactly on top of the first.
    RectTransform Resolve(Transform panel)
    {
        Transform existing = panel.Find(FooterName);
        if (existing != null)
        {
            // A half-built husk from an interrupted rebuild is worse than no strip at all: it is
            // an opaque band with no button, and adopting it makes that permanent. Same check
            // GroomSymmetryAuthority.Bind makes on its own control.
            RectTransform rect = existing as RectTransform;
            if (rect != null && rect.Find(ButtonName) != null) return rect;
            Destroy(existing.gameObject);
        }

        return Build(panel);
    }

    // The footer draws over the group list, so it has to be the last child. GroupPanelPostHintStats
    // reindexes eight named children to the front of the panel every scan and leaves everything
    // else behind them, so this mostly holds by itself - but only mostly, and a strip drawn under
    // the list it is meant to cover is worse than a redundant check.
    static void KeepOnTop(RectTransform rect)
    {
        int last = rect.parent.childCount - 1;
        if (rect.GetSiblingIndex() != last) rect.SetSiblingIndex(last);
    }

    // ------------------------------------------------------------------------------ building

    RectTransform Build(Transform panel)
    {
        GameObject go = new GameObject(FooterName, typeof(RectTransform), typeof(Image), typeof(LayoutElement));
        go.transform.SetParent(panel, false);

        // Out of the flow, so the VerticalLayoutGroup neither places it nor counts it, and pinned
        // across the bottom edge instead.
        go.GetComponent<LayoutElement>().ignoreLayout = true;

        RectTransform rect = go.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 0f);
        rect.anchorMax = new Vector2(1f, 0f);
        rect.pivot = new Vector2(.5f, 0f);
        rect.sizeDelta = new Vector2(0f, StripHeight);
        rect.anchoredPosition = Vector2.zero;

        // Opaque, not the panel's 0.85. Group rows pass underneath when the list is scrolled and
        // a translucent strip would show them sliding through the button.
        go.GetComponent<Image>().color = new Color(.10f, .11f, .12f, 1f);

        BuildDivider(rect);
        BuildButton(rect);

        return rect;
    }

    // A hairline along the top of the strip, so it reads as a fixed edge of the panel rather than
    // as one more row that happens to be at the bottom.
    static void BuildDivider(RectTransform parent)
    {
        GameObject go = new GameObject("Divider", typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);

        RectTransform rect = go.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(.5f, 1f);
        rect.sizeDelta = new Vector2(0f, DividerHeight);
        rect.anchoredPosition = Vector2.zero;

        Image image = go.GetComponent<Image>();
        image.color = new Color(.30f, .33f, .36f, 1f);
        image.raycastTarget = false;
    }

    static void BuildButton(RectTransform parent)
    {
        GameObject go = new GameObject(ButtonName, typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        go.transform.SetParent(parent, false);

        // Centred vertically with an explicit height rather than stretched: UITheme.ClampButtonSize
        // writes sizeDelta.y directly, and against stretched anchors that number is an offset from
        // the parent's height, not a height - the button would come out 70 tall instead of 30.
        RectTransform rect = go.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, .5f);
        rect.anchorMax = new Vector2(1f, .5f);
        rect.pivot = new Vector2(.5f, .5f);
        rect.sizeDelta = new Vector2(-SideInset * 2f, ButtonHeight);
        rect.anchoredPosition = Vector2.zero;

        // Fallback tint only. UIThemeAuthority replaces this with the 9-slice skin within a
        // quarter of a second; it is what the button looks like if the sprite set fails to load.
        go.GetComponent<Image>().color = UITheme.ButtonNormal;
        go.GetComponent<Button>().onClick.AddListener(OpenManual);

        GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textGO.transform.SetParent(go.transform, false);

        RectTransform textRect = textGO.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;

        TextMeshProUGUI label = textGO.GetComponent<TextMeshProUGUI>();
        label.text = "OPEN MANUAL";
        label.fontSize = 14f;
        label.fontStyle = FontStyles.Bold;
        label.alignment = TextAlignmentOptions.Center;
        label.color = Color.white;
        label.raycastTarget = false;
    }

    static void OpenManual()
    {
        // The manual is a web document, so this hands off to the default browser. On a platform
        // with no browser to hand off to the call simply does nothing, hence the toast either way:
        // a button that looks like it did nothing is indistinguishable from one that is broken.
        Application.OpenURL(ManualUrl);
        StatusToast.Show("Opening the HairBrush manual in your browser.");
    }

    // ------------------------------------------------------------------- shortening the list

    // GroupScrollView is a flat 600 tall as built, which overflows a short window and leaves a gap
    // on a tall one. Recomputed here from what the other children occupy, so the list ends exactly
    // above the footer at any window size.
    void ApplyScrollHeight(RectTransform panel, RectTransform strip)
    {
        if (panel == null) return;

        // Zero on any frame where the canvas has not sized itself yet. Computing against it
        // would pin the list at its floor for a scan before snapping back.
        if (panel.rect.height <= 1f) return;

        RectTransform scroll = panel.Find(ScrollName) as RectTransform;
        if (scroll == null) return;

        float top = 0f;
        float spacing = 0f;
        VerticalLayoutGroup layout = panel.GetComponent<VerticalLayoutGroup>();
        if (layout != null)
        {
            // Only the TOP padding is subtracted. The bottom padding is the band the strip is
            // pinned over, so charging for it as well would reserve it twice.
            top = layout.padding.top;
            spacing = layout.spacing;

            // The panel is built without touching childForceExpandHeight, and Unity's default
            // is true - which quietly defeats everything below. Any space this method frees up
            // for the strip is handed straight back to the children as extra cell height, and
            // the list ends up roughly a row's worth UNDERNEATH the footer rather than above
            // it. The inner content layout in BuildGroupManagementUI already sets this false
            // for the same reason; the panel's was simply never given the line.
            //
            // Asserted here rather than at the build site so it survives any future rebuild
            // path, and only on a real change - the setter dirties the layout either way.
            if (layout.childForceExpandHeight) layout.childForceExpandHeight = false;
        }

        float used = 0f;
        int flowing = 0;
        for (int i = 0; i < panel.childCount; i++)
        {
            RectTransform child = panel.GetChild(i) as RectTransform;
            if (child == null || child == scroll) continue;
            if (!child.gameObject.activeSelf) continue;

            // Skips this strip, the only child of the panel that is out of the flow.
            LayoutElement element = child.GetComponent<LayoutElement>();
            if (element != null && element.ignoreLayout) continue;

            used += child.rect.height;
            flowing++;
        }

        // childControlHeight is off, so a child's own rect height is exactly what it occupies.
        // A preferred-height lookup would be the wrong measure and would disagree with the
        // arrangement actually on screen. `flowing` excludes the scroll view, so it is already
        // the number of GAPS between the flow children rather than the count of them.
        float shared = panel.rect.height - top - used - spacing * flowing;
        float available = shared - StripHeight - StripGap;

        // Too short to give the list a usable height AND keep the strip. Standing the strip down
        // is the safe half to drop: leaving it up would put the bottom of the list behind an
        // opaque, click-absorbing band with no way to reach it.
        bool room = available >= MinScrollHeight;
        if (strip != null && strip.gameObject.activeSelf != room) strip.gameObject.SetActive(room);

        // No floor once the strip has stood down: `shared` is then all the room there physically
        // is, and forcing anything larger pushes the bottom of the viewport past the panel edge,
        // which on this panel is the bottom of the screen. That is the same unreachable list the
        // strip was just removed to avoid, moved somewhere harder to notice.
        float height = room ? available : Mathf.Max(1f, shared);

        // Written only on a real change. A rect assignment marks the layout dirty whether or not
        // the value moved, and this runs several times a second for the life of the session.
        if (Mathf.Abs(scroll.sizeDelta.y - height) > 1f)
            scroll.sizeDelta = new Vector2(scroll.sizeDelta.x, height);
    }
}
