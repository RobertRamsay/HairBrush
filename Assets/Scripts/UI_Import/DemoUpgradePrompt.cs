using TMPro;
using UnityEngine;
using UnityEngine.UI;

// The card a DEMO build shows when somebody reaches for a PRO feature.
//
// Static front, MonoBehaviour behind, the same split StatusToast uses: callers say
// DemoUpgradePrompt.Show() without having to know whether anything has been built yet, and the
// object that owns the UI spawns itself on first use.
//
// The card is built as a ROOT object rather than a child of any panel, and that is what keeps it
// alive: the right panel is destroyed and rebuilt on every model and project load, so a prompt
// parented into it would vanish mid-read. DontDestroyOnLoad on top of that is belt and braces -
// nothing in this project ever loads a scene.
public static class DemoUpgradePrompt
{
    private static DemoUpgradePromptAuthority instance;

    // Read by the handful of things that keep working underneath a modal otherwise - the camera
    // rig and the two global hotkey authorities. Derived from whether the panel object is
    // actually alive rather than tracked in a bool of its own, so if anything ever destroys the
    // panel without going through Close, this answers false on the next frame instead of leaving
    // the app permanently convinced a card is up.
    //
    // Always false in a PRO build: nothing ever spawns the authority.
    public static bool IsOpen
    {
        get
        {
            if (instance == null) return false;
            return instance.HasPanel;
        }
    }

    public static void Show()
    {
        // Cheap insurance rather than a real guard: nothing should call this in a PRO build,
        // but if a future feature gets gated and the gate is written the wrong way round, a PRO
        // user seeing a buy card is a much worse bug than a demo user seeing nothing.
        if (!BuildEdition.IsDemo) return;

        Ensure();
        instance.Open();
    }

    private static void Ensure()
    {
        if (instance != null) return;
        instance = Object.FindFirstObjectByType<DemoUpgradePromptAuthority>();
        if (instance != null) return;

        GameObject go = new GameObject("DemoUpgradePromptAuthority");
        Object.DontDestroyOnLoad(go);
        instance = go.AddComponent<DemoUpgradePromptAuthority>();
    }
}

public class DemoUpgradePromptAuthority : MonoBehaviour
{
    private const string PanelName = "DemoUpgradePanel";

    // Public because UIThemeAuthority is told to leave it alone by name. It is a Button whose
    // Graphic is the full-screen dimmer, and the shared button skin would repaint that white,
    // hand it a button sprite, and then have ClampButtonSize collapse a stretched rect to 32
    // units. See the exemption there.
    public const string DismissLayerName = "DemoPromptDismissLayer";

    // Above the Welcome panel's 200 and every nested panel canvas in the project, which top out
    // around 12. StatusToast sits higher again at 5000, and is left there on purpose: an I/O
    // error has to stay readable even over this.
    private const int PanelSortingOrder = 400;

    private static readonly Vector2 ReferenceResolution = new Vector2(1920f, 1080f);

    private const float CardWidth = 720f;
    private const float CardHeight = 300f;
    private const float Pad = 26f;

    private const float TitleTop = 24f;
    private const float TitleHeight = 32f;
    private const float DividerTop = 66f;
    private const float DividerHeight = 2f;
    private const float BodyTop = 84f;
    // Deliberately taller than the copy needs. Overflow is Truncate, which drops a line without
    // drawing an ellipsis, so a font-asset swap or a copy edit that pushed the paragraph one line
    // longer would silently eat the last sentence - and the last sentence is the one that says
    // saved projects open straight up. The buttons start 242 units down, so this costs nothing.
    private const float BodyHeight = 140f;

    // UITheme.ClampButtonSize pins every button in the project into a 26-32 band, and these are
    // deliberately not exempt from it - a buy button that is a different size from every other
    // button in the app reads as a different app. 32 is the top of that band.
    private const float ButtonHeight = 32f;
    private const float StoreButtonWidth = 240f;
    private const float CloseButtonWidth = 150f;
    private const float ButtonGap = 12f;

    private const float TitleFont = 24f;
    private const float BodyFont = 17f;
    private const float ButtonFont = 15f;

    private GameObject panel;
    private float dismissArmedAt;

    // A click that lands on the dimmer within this of the card appearing is the second half of a
    // double-click on EXPORT OBJ, not a decision to dismiss. Without it the natural response to a
    // button that seems slow - clicking it again - opens the card and closes it in the same
    // gesture, and the card looks like a flicker.
    private const float DismissArmDelay = .35f;

    public bool HasPanel
    {
        get { return panel != null; }
    }

    // NO ESC HANDLER, and that is a decision rather than an omission.
    //
    // ESC in this app is not consumed by whoever reads it first - GuideCurveManager,
    // GroupAddButtonPlacementAuthority and TextureUVRectWorkspace all read the same press and
    // each decides for itself whether to act, guarding against each other by hand. A fourth
    // reader that none of them know about means dismissing this card while a GUIDE is selected
    // would close the card AND drop the guide selection on the same press.
    //
    // Joining that protocol properly would mean editing all three to say "unless the buy card is
    // up". Clicking the dimmed backdrop closes it instead, which is what a modal is expected to
    // do anyway, and it collides with nothing.
    public void Open()
    {
        // Already up. Raising a second copy over the first would leave the first one stranded
        // underneath with nothing able to close it.
        if (panel != null) return;

        GameObject stale = GameObject.Find(PanelName);
        if (stale != null) Destroy(stale);

        Build();
    }

    void DismissFromBackdrop()
    {
        if (Time.unscaledTime < dismissArmedAt) return;
        Close();
    }

    void Close()
    {
        if (panel != null) Destroy(panel);
        panel = null;
    }

    void OpenStore(string url)
    {
        Application.OpenURL(url);

        // Left up on purpose. The browser opens in front, and closing the card would mean that
        // coming back to HairBrush lands on a screen with no sign of what just happened - and no
        // second chance at the other store.
    }

    void Build()
    {
        // Built into a local and only published to the field at the very end. `panel` non-null is
        // what IsOpen reports, and IsOpen is what switches off the camera and the global hotkeys -
        // so a half-built card that threw before it grew a close button would leave the app with
        // no way to orbit, no way to dismiss, and no ESC by design. Either the whole card exists
        // or none of it does.
        GameObject root = new GameObject(PanelName, typeof(RectTransform), typeof(Canvas),
                                         typeof(CanvasScaler), typeof(GraphicRaycaster));
        DontDestroyOnLoad(root);

        // Its own ROOT canvas: a CanvasScaler only does anything on a root, so borrowing an
        // existing canvas would inherit whatever scale mode that one was built with. Same
        // reasoning, and the same settings, as the Welcome panel.
        Canvas canvas = root.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = PanelSortingOrder;

        CanvasScaler scaler = root.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = ReferenceResolution;
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 1f;

        Stretch(root.GetComponent<RectTransform>());

        // ------------------------------------------------------------------ the dimmer
        //
        // A SEPARATE CHILD, sitting behind the card, and that is the whole point of it. Unity
        // resolves a click by walking UP from whatever was hit to the first object that handles
        // one, so a dismiss Button on the panel ROOT catches clicks on the card as well: the
        // card's own Image has no handler, so the click bubbles past it to the root and the card
        // closes when you click its own body text. As a sibling underneath, it only ever sees the
        // clicks that miss the card.
        //
        // Also a raycast target, which is what stops a click aimed at BUY but landing slightly
        // wide from dropping a hair card on the head.
        //
        // It does NOT stop everything on its own. HandleCameraControls, the 1/2 and [ ] hotkeys,
        // SHIFT to cycle brush mode and the variance seed buttons all read the mouse or keyboard
        // directly without asking whether the pointer is over UI. Those are handled at the other
        // end, by each of them checking DemoUpgradePrompt.IsOpen.
        GameObject dimmer = new GameObject(DismissLayerName, typeof(RectTransform), typeof(Image), typeof(Button));
        dimmer.transform.SetParent(root.transform, false);
        Stretch(dimmer.GetComponent<RectTransform>());

        Image backdrop = dimmer.GetComponent<Image>();
        backdrop.color = new Color(0f, 0f, 0f, .6f);
        backdrop.raycastTarget = true;

        // Transition None keeps it invisible as a button, but that is presentation only - what
        // actually protects the dimmer from the shared button skin is the name exemption in
        // UIThemeAuthority. Without it StyleButton would repaint this white, hand it a button
        // sprite, and ClampButtonSize would collapse the stretched rect to 32 units.
        Button dismiss = dimmer.GetComponent<Button>();
        dismiss.transition = Selectable.Transition.None;
        dismiss.targetGraphic = backdrop;
        dismiss.onClick.AddListener(DismissFromBackdrop);

        // ------------------------------------------------------------------ the card
        GameObject card = new GameObject("Card", typeof(RectTransform), typeof(Image));
        card.transform.SetParent(root.transform, false);
        RectTransform cardRect = card.GetComponent<RectTransform>();
        cardRect.anchorMin = new Vector2(.5f, .5f);
        cardRect.anchorMax = new Vector2(.5f, .5f);
        cardRect.pivot = new Vector2(.5f, .5f);
        cardRect.anchoredPosition = Vector2.zero;
        cardRect.sizeDelta = new Vector2(CardWidth, CardHeight);
        ApplyNineSlice(card.GetComponent<Image>(), UITheme.FineEdgeSprite, UITheme.PanelDark);

        TextMeshProUGUI title = AddBand(card.transform, "Title", TitleTop, TitleHeight);
        StyleLine(title, "EXPORT IS IN THE FULL VERSION", TitleFont, FontStyles.Bold, UITheme.TextBright);

        AddDivider(card.transform);

        TextMeshProUGUI body = AddBand(card.transform, "Body", BodyTop, BodyHeight);
        StyleLine(body,
            "This is the HairBrush demo. Everything else works - place and groom the hair, use " +
            "every modifier, save and load your project. Writing the cards out as an OBJ is the " +
            "one thing kept for the full version.\n\n" +
            "Buy it and your saved projects open straight up, ready to export.",
            BodyFont, FontStyles.Normal, UITheme.TextMuted);

        // Wrapped and top-aligned, unlike StyleLine's single-line default: this is the only
        // paragraph of prose in the card and it has to be readable, not truncated.
        body.textWrappingMode = TextWrappingModes.Normal;
        body.overflowMode = TextOverflowModes.Truncate;
        body.alignment = TextAlignmentOptions.TopLeft;

        AddButton(card.transform, "BuyArtStationButton", "BUY ON ARTSTATION",
                  new Vector2(Pad, Pad), new Vector2(StoreButtonWidth, ButtonHeight),
                  delegate { OpenStore(BuildEdition.ArtStationUrl); });

        AddButton(card.transform, "BuyItchButton", "BUY ON ITCH.IO",
                  new Vector2(Pad + StoreButtonWidth + ButtonGap, Pad),
                  new Vector2(StoreButtonWidth, ButtonHeight),
                  delegate { OpenStore(BuildEdition.ItchUrl); });

        AddCloseButton(card.transform);

        dismissArmedAt = Time.unscaledTime + DismissArmDelay;
        panel = root;
    }

    // ------------------------------------------------------------------------- UI helpers
    //
    // Deliberately local copies rather than shared with the Welcome panel. They are eight lines
    // each, and the alternative - making that panel's private helpers public so this one can
    // borrow them - would tie two unrelated cards together for no saving worth having.

    void AddButton(Transform parent, string name, string label, Vector2 inset, Vector2 size,
                   UnityEngine.Events.UnityAction action)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);

        RectTransform rect = go.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 0f);
        rect.anchorMax = new Vector2(0f, 0f);
        rect.pivot = new Vector2(0f, 0f);
        rect.anchoredPosition = inset;
        rect.sizeDelta = size;

        Button button = go.GetComponent<Button>();
        button.onClick.AddListener(action);

        // Skinned by UIThemeAuthority's next pass, which is what keeps these looking like every
        // other button in the app. This is only the fallback for the frame or two before it runs.
        ApplyNineSlice(go.GetComponent<Image>(), UITheme.ButtonNormalSprite, Color.white);

        TextMeshProUGUI text = AddStretchedText(go.transform, "Text");
        StyleLine(text, label, ButtonFont, FontStyles.Bold, UITheme.TextBright);
        text.alignment = TextAlignmentOptions.Center;
    }

    void AddCloseButton(Transform parent)
    {
        GameObject go = new GameObject("DemoPromptCloseButton", typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);

        RectTransform rect = go.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(1f, 0f);
        rect.anchorMax = new Vector2(1f, 0f);
        rect.pivot = new Vector2(1f, 0f);
        rect.anchoredPosition = new Vector2(-Pad, Pad);
        rect.sizeDelta = new Vector2(CloseButtonWidth, ButtonHeight);

        go.GetComponent<Button>().onClick.AddListener(Close);
        ApplyNineSlice(go.GetComponent<Image>(), UITheme.ButtonNormalSprite, Color.white);

        TextMeshProUGUI text = AddStretchedText(go.transform, "Text");
        StyleLine(text, "NOT NOW", ButtonFont, FontStyles.Bold, UITheme.TextBright);
        text.alignment = TextAlignmentOptions.Center;
    }

    static TextMeshProUGUI AddBand(Transform parent, string name, float top, float height)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);

        RectTransform rect = go.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(.5f, 1f);
        rect.offsetMin = new Vector2(Pad, -(top + height));
        rect.offsetMax = new Vector2(-Pad, -top);

        return go.GetComponent<TextMeshProUGUI>();
    }

    static void AddDivider(Transform parent)
    {
        GameObject go = new GameObject("Divider", typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);

        RectTransform rect = go.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(.5f, 1f);
        rect.offsetMin = new Vector2(Pad, -DividerTop + DividerHeight);
        rect.offsetMax = new Vector2(-Pad, -DividerTop);

        Image image = go.GetComponent<Image>();
        ApplyNineSlice(image, UITheme.DividerSprite, Color.white);
        image.raycastTarget = false;
    }

    static TextMeshProUGUI AddStretchedText(Transform parent, string name)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        Stretch(go.GetComponent<RectTransform>());
        return go.GetComponent<TextMeshProUGUI>();
    }

    static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    static void ApplyNineSlice(Image image, Sprite sprite, Color colour)
    {
        if (image == null) return;
        image.color = colour;

        // Falls back to a flat fill if the sprites are missing, exactly as UITheme does.
        if (sprite == null) return;
        image.sprite = sprite;
        image.type = Image.Type.Sliced;
    }

    static void StyleLine(TextMeshProUGUI label, string text, float size, FontStyles style, Color colour)
    {
        if (label == null) return;
        label.text = text;
        label.fontSize = size;
        label.fontStyle = style;
        label.color = colour;
        label.alignment = TextAlignmentOptions.MidlineLeft;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.overflowMode = TextOverflowModes.Ellipsis;
        label.enableAutoSizing = false;
        label.raycastTarget = false;
    }
}
