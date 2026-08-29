using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// INPUT KEYS - the whole control reference, on a button instead of down the side of the panel.
//
// It replaces the ten-line INSTRUCTIONS block that used to live in the left panel. That block
// was out of room long before it was out of date: it carried a comment saying to bump its
// height every time a line was added, it never mentioned the camera, the brush radius keys, the
// sidedness keys or undo, and two of the ten lines it did carry were wrong -
//
//   "DEL or right click removes a group"   there is no DEL key in this project. DEL is a button
//                                          on the group row. No deleteKey read exists anywhere.
//   "SHIFT to Toggle brushing mode"        SHIFT CYCLES five modes, it does not toggle two.
//
// A panel is a bad home for a reference that only grows. A dialogue has as much room as it
// needs, is read when it is wanted rather than sitting in the corner of every screenshot, and
// gives the left panel its 152px back.
//
// The camera section is built at OPEN time rather than once, because it depends on MAYA-NAV:
// both schemes are always listed - a reference with an "if" in it is worse than one that shows
// both - but the live one is marked, so the answer to "which of these applies to me" is on the
// page rather than in the reader's memory of a toggle.
[DefaultExecutionOrder(8960)]
public class InputKeysDialog : MonoBehaviour
{
    // GroupPanelPostHintStats orders the left panel and needs to know this button by name.
    public const string ButtonName = "InputKeysButton";

    // UIThemeAuthority skips this one by name. It is a Button whose Graphic is the full-screen
    // backdrop, and the shared skin would repaint the whole screen white with a button sprite
    // on it - exactly what the demo card's dismiss layer is exempted for.
    public const string DimmerName = "InputKeysDimmer";

    private const string DialogCanvasName = "InputKeysDialogCanvas";
    private const float ButtonHeight = 32f;
    private const float ScanInterval = .25f;
    private const float ChordColumnWidth = 232f;
    private const float RowHeight = 22f;
    private const float HeadingHeight = 34f;

    private static readonly Color HeadingColour = new Color(.55f, .82f, .74f, 1f);
    private static readonly Color ChordColour = new Color(.98f, .86f, .55f, 1f);
    private static readonly Color BodyColour = new Color(.84f, .88f, .93f, 1f);
    private static readonly Color NoteColour = new Color(.62f, .68f, .76f, 1f);

    // Open state, asked by GroomShortcutKeyAuthority and by every ESC reader in the project so
    // that closing this dialogue does not also cancel a guide or an armed placement behind it.
    private static bool open = false;

    // The frame the page was closed on. Without it the ESC guards elsewhere would depend on
    // script execution order: ESC is not consumed by whoever reads it first, so a reader that
    // runs AFTER this component's close would see IsOpen already false on the very frame the
    // press was meant for the dialogue, and would cancel the guide or the armed placement
    // sitting behind it. Reporting open for the whole of the closing frame makes every reader
    // agree regardless of the order they run in.
    private static int closedFrame = -1;

    public static bool IsOpen
    {
        get
        {
            if (open) return true;
            return Time.frameCount == closedFrame;
        }
    }

    // Statics survive "Enter Play Mode -> Disable Domain Reload". A dialogue left open when play
    // stopped would otherwise have every ESC reader in the project standing down forever.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        open = false;
        closedFrame = -1;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Spawn()
    {
        if (FindFirstObjectByType<InputKeysDialog>() != null) return;
        GameObject go = new GameObject(nameof(InputKeysDialog));
        DontDestroyOnLoad(go);
        go.AddComponent<InputKeysDialog>();
    }

    private GameObject boundPanel;
    private Button button;
    private GameObject dialogRoot;
    private float nextScan;

    private void Update()
    {
        if (open && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            Close();
            return;
        }

        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + ScanInterval;

        // The left panel is destroyed and rebuilt on every model and project load, so the
        // binding is re-checked rather than established once - the same shape SYMMETRY and
        // MAYA-NAV use, for the same reason.
        GameObject panel = GameObject.Find("GroupManagerPanel");
        if (panel == null)
        {
            boundPanel = null;
            button = null;
            return;
        }

        if (boundPanel != panel || button == null) Bind(panel);
    }

    private void OnDestroy()
    {
        if (dialogRoot != null) Destroy(dialogRoot);
        open = false;
    }

    // ---- the button ---------------------------------------------------------------------

    private void Bind(GameObject panel)
    {
        boundPanel = panel;

        Transform existing = panel.transform.Find(ButtonName);
        if (existing != null)
        {
            button = existing.GetComponent<Button>();
            if (button != null) return;

            // A half-built husk from an interrupted rebuild - start again rather than adopt it.
            Destroy(existing.gameObject);
        }

        BuildButton(panel.transform);
    }

    private void BuildButton(Transform parent)
    {
        GameObject go = new GameObject(ButtonName, typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, ButtonHeight);
        go.GetComponent<LayoutElement>().preferredHeight = ButtonHeight;
        go.GetComponent<LayoutElement>().minHeight = ButtonHeight;

        // A starting colour only. UIThemeAuthority skins every button that is not exempted by
        // name, so this one ends up in the shared white skin within a quarter second - which is
        // right for it: SYMMETRY and MAYA-NAV repaint themselves teal because their colour is
        // reporting STATE, and this button has no state to report.
        go.GetComponent<Image>().color = new Color(.24f, .32f, .44f, 1f);

        button = go.GetComponent<Button>();
        button.onClick.AddListener(Open);

        GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textGO.transform.SetParent(go.transform, false);
        RectTransform textRect = textGO.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;

        TextMeshProUGUI label = textGO.GetComponent<TextMeshProUGUI>();
        label.text = "INPUT KEYS";
        label.fontSize = 13f;
        label.fontStyle = FontStyles.Bold;
        label.alignment = TextAlignmentOptions.Center;
        label.color = Color.white;
        label.raycastTarget = false;

        // First guess only. GroupPanelPostHintStats.MaintainPanelOrder is the running order
        // authority for this panel and puts this button where the instructions used to be.
        Transform above = parent.Find(SceneLightAngleAuthority.RowName);
        if (above != null) go.transform.SetSiblingIndex(Mathf.Clamp(above.GetSiblingIndex() + 1, 0, parent.childCount - 1));
    }

    // ---- the dialogue -------------------------------------------------------------------

    public void Open()
    {
        if (dialogRoot != null) return;
        open = true;
        Build();
    }

    public void Close()
    {
        if (open) closedFrame = Time.frameCount;
        open = false;
        if (dialogRoot == null) return;
        Destroy(dialogRoot);
        dialogRoot = null;
    }

    private void Build()
    {
        GameObject canvasObject = new GameObject(DialogCanvasName, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        dialogRoot = canvasObject;
        DontDestroyOnLoad(canvasObject);

        Canvas canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;

        // Above the groom UI, and BELOW the import prompt's 5000. That prompt hides the whole
        // menu while it is up, so the two can never be on screen together - but if a later one
        // does not, a help page must not cover a question that is waiting for an answer.
        canvas.sortingOrder = 4800;

        CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);

        // Full-screen blocker, and a close button in its own right. It is what makes this modal:
        // it swallows the click that would otherwise reach the viewport, which is why every
        // placement authority stays quiet while the page is up - they all stand down on
        // EventSystem.IsPointerOverGameObject.
        GameObject dimmer = new GameObject(DimmerName, typeof(RectTransform), typeof(Image), typeof(Button));
        dimmer.transform.SetParent(canvasObject.transform, false);
        RectTransform dimmerRect = dimmer.GetComponent<RectTransform>();
        dimmerRect.anchorMin = Vector2.zero;
        dimmerRect.anchorMax = Vector2.one;
        dimmerRect.offsetMin = Vector2.zero;
        dimmerRect.offsetMax = Vector2.zero;
        dimmer.GetComponent<Image>().color = new Color(0f, 0f, 0f, .66f);
        dimmer.GetComponent<Button>().onClick.AddListener(Close);

        GameObject panel = new GameObject("InputKeysPanel", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup));
        panel.transform.SetParent(canvasObject.transform, false);
        RectTransform panelRect = panel.GetComponent<RectTransform>();
        panelRect.anchorMin = new Vector2(.5f, .5f);
        panelRect.anchorMax = new Vector2(.5f, .5f);
        panelRect.pivot = new Vector2(.5f, .5f);
        panelRect.sizeDelta = new Vector2(900f, 760f);
        panel.GetComponent<Image>().color = new Color(.13f, .15f, .18f, .99f);

        VerticalLayoutGroup panelLayout = panel.GetComponent<VerticalLayoutGroup>();
        panelLayout.padding = new RectOffset(26, 26, 20, 20);
        panelLayout.spacing = 10f;

        // Both true. A layout group ignores LayoutElement.preferred* on an axis it does not
        // control, and with childControlHeight off the scroll view below collapses to nothing -
        // the same failure that flattened the remap phase bar.
        panelLayout.childControlHeight = true;
        panelLayout.childControlWidth = true;
        panelLayout.childForceExpandHeight = false;
        panelLayout.childForceExpandWidth = true;

        BuildTitle(panel.transform);
        BuildScrollingBody(panel.transform);
        BuildCloseButton(panel.transform);
    }

    private void BuildTitle(Transform parent)
    {
        GameObject go = new GameObject("InputKeysTitle", typeof(RectTransform), typeof(TextMeshProUGUI), typeof(LayoutElement));
        go.transform.SetParent(parent, false);

        TextMeshProUGUI text = go.GetComponent<TextMeshProUGUI>();
        text.text = "INPUT KEYS";
        text.fontSize = 26f;
        text.fontStyle = FontStyles.Bold;
        text.color = Color.white;
        text.alignment = TextAlignmentOptions.MidlineLeft;
        text.raycastTarget = false;

        LayoutElement layout = go.GetComponent<LayoutElement>();
        layout.preferredHeight = 36f;
        layout.minHeight = 36f;
    }

    private void BuildScrollingBody(Transform parent)
    {
        GameObject viewport = new GameObject("InputKeysViewport", typeof(RectTransform), typeof(Image), typeof(Mask), typeof(ScrollRect), typeof(LayoutElement));
        viewport.transform.SetParent(parent, false);

        // The mask needs a graphic to cut against; it is not meant to be seen, so it is nearly
        // transparent rather than absent. showMaskGraphic off would hide it completely and take
        // the scroll wheel's raycast target with it.
        viewport.GetComponent<Image>().color = new Color(.10f, .12f, .14f, .35f);

        LayoutElement viewportLayout = viewport.GetComponent<LayoutElement>();
        viewportLayout.preferredHeight = 640f;
        viewportLayout.flexibleHeight = 1f;

        GameObject content = new GameObject("InputKeysContent", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
        content.transform.SetParent(viewport.transform, false);
        RectTransform contentRect = content.GetComponent<RectTransform>();
        contentRect.anchorMin = new Vector2(0f, 1f);
        contentRect.anchorMax = new Vector2(1f, 1f);
        contentRect.pivot = new Vector2(.5f, 1f);
        contentRect.offsetMin = new Vector2(0f, 0f);
        contentRect.offsetMax = new Vector2(0f, 0f);

        VerticalLayoutGroup contentLayout = content.GetComponent<VerticalLayoutGroup>();
        contentLayout.padding = new RectOffset(14, 14, 10, 14);
        contentLayout.spacing = 2f;
        contentLayout.childControlHeight = true;
        contentLayout.childControlWidth = true;
        contentLayout.childForceExpandHeight = false;
        contentLayout.childForceExpandWidth = true;

        ContentSizeFitter fitter = content.GetComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        ScrollRect scroll = viewport.GetComponent<ScrollRect>();
        scroll.content = contentRect;
        scroll.viewport = viewport.GetComponent<RectTransform>();
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 28f;

        FillContent(content.transform);
    }

    private void BuildCloseButton(Transform parent)
    {
        GameObject go = new GameObject("InputKeysCloseButton", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        go.GetComponent<Image>().color = new Color(.24f, .30f, .38f, 1f);

        LayoutElement layout = go.GetComponent<LayoutElement>();
        layout.preferredHeight = 38f;
        layout.minHeight = 38f;

        GameObject textObject = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textObject.transform.SetParent(go.transform, false);
        RectTransform textRect = textObject.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;

        TextMeshProUGUI text = textObject.GetComponent<TextMeshProUGUI>();
        text.text = "CLOSE   (ESC)";
        text.fontSize = 16f;
        text.fontStyle = FontStyles.Bold;
        text.color = Color.white;
        text.alignment = TextAlignmentOptions.Center;
        text.raycastTarget = false;

        go.GetComponent<Button>().onClick.AddListener(Close);
    }

    // ---- the reference itself -----------------------------------------------------------
    //
    // Every line here was read off the code rather than off the old hint block, which is how the
    // two wrong lines in that block were found. Where a gesture only works in one context, the
    // heading says so - a list that does not is a list people file bugs against.

    private void FillContent(Transform parent)
    {
        bool maya = MayaNavigationAuthority.Enabled;

        string cameraHeading = "CAMERA   -   MAYA-NAV IS OFF";
        if (maya)
        {
            cameraHeading = "CAMERA   -   MAYA-NAV IS ON";
        }

        Heading(parent, cameraHeading);
        if (maya)
        {
            Row(parent, "ALT + LEFT drag", "Tumble (orbit) the view");
            Row(parent, "ALT + MIDDLE drag", "Track (pan) the view");
            Row(parent, "ALT + RIGHT drag", "Dolly - drag right to come closer");
            Row(parent, "WHEEL", "Zoom in and out");
            Note(parent, "With MAYA-NAV off it is RIGHT drag to orbit and MIDDLE drag to pan, with no ALT.");
        }
        else
        {
            Row(parent, "RIGHT drag", "Orbit the view");
            Row(parent, "MIDDLE drag", "Pan the view");
            Row(parent, "WHEEL", "Zoom in and out");
            Note(parent, "Switch MAYA-NAV on for ALT + LEFT tumble, ALT + MIDDLE track, ALT + RIGHT dolly.");
        }
        Note(parent, "ALT never places anything, in either mode - it belongs to the camera.");

        Heading(parent, "PLACING HAIR");
        Row(parent, "P", "PLACE - one card per click");
        Row(parent, "D", "PAINT - draw continuously while held");
        Row(parent, "B", "SPRAY - scatter through the brush radius");
        Row(parent, "F", "EVEN - fill to an even spacing, never closer");
        Row(parent, "E", "ERASE - remove cards under the brush");
        Row(parent, "S", "SYMMETRY on / off");
        Row(parent, "SHIFT", "Cycle PLACE - PAINT - SPRAY - EVEN - ERASE");
        Row(parent, "LEFT click / hold", "Place, paint or erase, depending on the mode");
        Row(parent, "[  and  ]", "Brush radius smaller and bigger");
        Row(parent, "1  /  2", "All hair cards single-sided / double-sided");
        Note(parent, "The radius keys work in SPRAY, EVEN and ERASE, and on a live CTRL selection. PLACE and PAINT have no radius.");
        Note(parent, "1 and 2 do nothing while CARD is set to DIAMOND - a closed card is single-sided by construction. Same for SS/DS and N+/N- on the group rows.");

        Heading(parent, "SELECTING");
        Row(parent, "CTRL + LEFT click", "Set a soft selection hotspot on the group");
        Row(parent, "CTRL + SHIFT + LEFT click", "Select the group of the hair under the cursor");
        Row(parent, "LEFT click in empty space", "Come out of the current mode");
        Row(parent, "CTRL (held, no click)", "Preview the selection radius");

        Heading(parent, "GROUPS");
        Row(parent, "LEFT click a group row", "Make it the current group");
        Row(parent, "DOUBLE click the name", "Rename the group");
        Row(parent, "CTRL + BACKSPACE", "Clear the name while typing it");
        Row(parent, "RIGHT click a group row", "Delete the group (the DEL button does the same)");
        Row(parent, "RIGHT click any slider", "Put that slider back to its default");

        Heading(parent, "MODIFIERS - POST AND CLUMPER");
        Row(parent, "CTRL + LEFT click", "Add a POST modifier where you click");
        Row(parent, "TAB + LEFT click", "Add a CLUMP modifier where you click");
        Row(parent, "SPACE + LEFT click", "Move the selected modifier to a new point");
        Row(parent, "CTRL + click empty space", "Leave the CLUMPER editor");
        Row(parent, "RIGHT click  or  ESC", "Cancel an armed +POST / +GUIDE / +CLUMP");

        Heading(parent, "GUIDE CURVES");
        Row(parent, "SPACE + LEFT click", "Move the guide's root, keeping its shape");
        Row(parent, "CTRL + SHIFT + LEFT click", "Insert a point on the curve");
        Row(parent, "CTRL + SHIFT + RIGHT click", "Remove that point");
        Row(parent, "Drag a handle", "Reshape the curve");
        Row(parent, "ESC  or  empty space", "Finish editing the guide");

        Heading(parent, "SHAPE CURVE GRAPH");
        Row(parent, "LEFT click the graph", "Add a point");
        Row(parent, "Drag a point", "Move it");
        Row(parent, "RIGHT click a point", "Remove it");

        Heading(parent, "TEXTURE / UV WORKSPACE");
        Row(parent, "LEFT drag on the texture", "Draw a UV rect, while DRAW UV RECT is on");
        Row(parent, "ESC", "Leave DRAW UV RECT mode");
        Row(parent, "RIGHT click a rect or its row", "Delete that rect");
        Row(parent, "Drag a list row", "Reorder the rects");

        Heading(parent, "REMAP SESSION");
        Row(parent, "LEFT click a head", "Place the next marker");
        Row(parent, "Drag a marker", "Move it");
        Row(parent, "ESC", "Cancel the remap - the groom is untouched");

        Heading(parent, "EVERYWHERE");
        Row(parent, "CTRL + S", "SAVE PROJ");
        Row(parent, "CTRL + X", "EXPORT OBJ");
        Row(parent, "CTRL + Z", "Undo");
        Row(parent, "CTRL + Y", "Redo");
        Row(parent, "CTRL + SHIFT + Z", "Redo");
        Note(parent, "No shortcut fires while you are typing a group name or a seed value.");
    }

    private static void Heading(Transform parent, string caption)
    {
        GameObject go = new GameObject("Heading", typeof(RectTransform), typeof(TextMeshProUGUI), typeof(LayoutElement));
        go.transform.SetParent(parent, false);

        TextMeshProUGUI text = go.GetComponent<TextMeshProUGUI>();
        text.text = caption;
        text.fontSize = 15f;
        text.fontStyle = FontStyles.Bold;
        text.color = HeadingColour;
        text.alignment = TextAlignmentOptions.BottomLeft;
        text.raycastTarget = false;

        LayoutElement layout = go.GetComponent<LayoutElement>();
        layout.preferredHeight = HeadingHeight;
        layout.minHeight = HeadingHeight;
    }

    private static void Row(Transform parent, string chord, string meaning)
    {
        GameObject go = new GameObject("Row", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        go.transform.SetParent(parent, false);

        HorizontalLayoutGroup layout = go.GetComponent<HorizontalLayoutGroup>();
        layout.spacing = 12f;
        layout.childControlHeight = true;
        layout.childControlWidth = true;
        layout.childForceExpandHeight = true;
        layout.childForceExpandWidth = false;

        LayoutElement rowLayout = go.GetComponent<LayoutElement>();
        rowLayout.preferredHeight = RowHeight;
        rowLayout.minHeight = RowHeight;

        Cell(go.transform, "Chord", chord, ChordColour, FontStyles.Bold, ChordColumnWidth, 0f);
        Cell(go.transform, "Meaning", meaning, BodyColour, FontStyles.Normal, 0f, 1f);
    }

    private static void Note(Transform parent, string caption)
    {
        GameObject go = new GameObject("Note", typeof(RectTransform), typeof(TextMeshProUGUI), typeof(LayoutElement));
        go.transform.SetParent(parent, false);

        TextMeshProUGUI text = go.GetComponent<TextMeshProUGUI>();
        text.text = caption;
        text.fontSize = 12f;
        text.fontStyle = FontStyles.Italic;
        text.color = NoteColour;
        text.alignment = TextAlignmentOptions.MidlineLeft;
        text.textWrappingMode = TextWrappingModes.Normal;
        text.raycastTarget = false;

        LayoutElement layout = go.GetComponent<LayoutElement>();
        layout.preferredHeight = 24f;
        layout.minHeight = 24f;
    }

    private static void Cell(Transform parent, string name, string content, Color colour, FontStyles style, float width, float flexible)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI), typeof(LayoutElement));
        go.transform.SetParent(parent, false);

        TextMeshProUGUI text = go.GetComponent<TextMeshProUGUI>();
        text.text = content;
        text.fontSize = 14f;
        text.fontStyle = style;
        text.color = colour;
        text.alignment = TextAlignmentOptions.MidlineLeft;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.raycastTarget = false;

        LayoutElement layout = go.GetComponent<LayoutElement>();
        if (width > 0f)
        {
            layout.preferredWidth = width;
            layout.minWidth = width;
        }
        layout.flexibleWidth = flexible;
    }
}
