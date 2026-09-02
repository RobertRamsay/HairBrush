using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

// Welcome / What's New panel over the start screen, plus a quiet check for a newer release.
//
// Shown every launch until the user ticks "Don't show this again", which records the CURRENT
// version in the settings file. Bumping the version therefore un-suppresses it automatically:
// the recorded version no longer matches, so the next release announces itself without the
// user having to go looking for it.
//
// Settings live in an ini beside the player's other saved data rather than next to the
// executable, because an installed build usually sits somewhere the user cannot write to.
// HairBrushSettings owns that file; this panel is one of its two callers.
//
// The card also carries the MAYA-NAV checkbox. That is discovery rather than control - the
// control is the left-panel button, reachable at any time - and it is here because this is the
// one screen every user looks at, and because a navigation scheme is something a Maya user wants
// in the first thirty seconds or not at all.
[DefaultExecutionOrder(9700)]
public class WelcomeWhatsNewAuthority : MonoBehaviour
{
    // ---------------------------------------------------------------------------------
    // Release notes. Add a block at the top for each release; only the first is displayed.
    // ---------------------------------------------------------------------------------
    // Derived from Player Settings rather than typed out, so the number on the panel is the
    // number the build actually carries. It used to be a hand-edited constant next to the notes,
    // which meant a release where the notes got rewritten and the heading did not - or the other
    // way round - shipped a panel announcing a version nobody was running. There is only one
    // version in this project now: bundleVersion, which is what Application.version reads.
    //
    // The notes below still have to be written by hand. Nothing can derive those.
    private static string ReleaseHeading
    {
        get
        {
            string version = Application.version;
            if (string.IsNullOrWhiteSpace(version)) version = "0.0.0";

            // A demo build says so on the first screen the user sees, so nobody gets as far as
            // the export button believing they are running the full version. BuildEdition
            // returns an empty suffix in a PRO build, so this line is unchanged there.
            return "BETA " + version + BuildEdition.EditionSuffix;
        }
    }

    // Five at most, one line each - the panel does not scroll, and anything past about eighty
    // characters is silently ellipsised.
    //
    // 0.3.3 is a small release and the list is honestly three lines rather than five. A panel
    // padded out to its capacity with things nobody needed telling is worse than a short one.
    //
    // MESH+TRANSP leads because it is the only item here that is a NEW THING TO DO, and because
    // what it is for is not written on the button. A translucent second pass over the groom is
    // not a mode anyone goes looking for; it earns its line by explaining why you would press it.
    //
    // DITHER is second: a control that did not exist, in a panel the user already knows, sitting
    // exactly where the note says it does. One line is the whole of what needs saying.
    //
    // The VAR +/- repair is last and is a repair, not a feature. It is here only because the
    // breakage was VISIBLE - the value wrapped onto a second line and landed on top of the SEED
    // row underneath - so anyone who saw that is owed the news that it was not their project.
    //
    // What did not make the cut: the dither value now saving with the project and taking part in
    // undo. That is what a slider is expected to do, and a release note claiming credit for it
    // would only raise the question of what the other sliders have been doing.
    private static readonly string[] ReleaseNotes =
    {
        "MESH+TRANSP: draw a soft translucent copy of the groom to judge the look.",
        "DITHER slider in the texture panel, under Smoothness and Metallic.",
        "The VAR +/- rows read straight again - value on the right, visible track.",
    };

    // ---------------------------------------------------------------------------------

    // The ini itself lives in HairBrushSettings now - see that file for why there is exactly
    // one reader and one writer of it, and why the file survives a version bump even though
    // THIS key deliberately does not.
    private const string SuppressKey = "suppressWelcomeForVersion";

    // Bump the file in the repo when a release goes out and every running copy sees it.
    private const string VersionCheckUrl =
        "https://raw.githubusercontent.com/RobertRamsay/HairBrush/main/hairbrush_version.txt";
    private const int VersionCheckTimeoutSeconds = 8;

    private const string PanelName = "WelcomeWhatsNewPanel";

    // UIThemeAuthority skips this name - see BuildStartButton.
    public const string StartButtonName = "WelcomeStartButton";

    // The panel builds its own root Canvas with a CanvasScaler set to ScaleWithScreenSize
    // against 1920x1080, matching on height. That is the whole answer to "can Unity just
    // be consistent": it can, but only for a ROOT canvas - a CanvasScaler on a nested one
    // does nothing. Borrowing the start screen's canvas meant inheriting its Constant Pixel
    // Size scaler, which never adapts to resolution, hence tiny at 4K and hand-rolled
    // scaling maths to compensate. On its own canvas every number below is simply "pixels
    // at 1080p" and Unity handles every resolution and aspect from there.
    private static readonly Vector2 ReferenceResolution = new Vector2(1920f, 1080f);
    private const int PanelSortingOrder = 200;

    // Card width as a fraction of the screen; its height is fixed in reference pixels so
    // the contents never squash.
    private const float CardLeft = .10f;
    private const float CardRight = .90f;
    // 400 rather than the 360 it was. The footer carries two rows now - the MAYA-NAV checkbox
    // above the suppress checkbox - and the notes well below is already within a few pixels of
    // full at five notes, so the height had to come from the card rather than from the well.
    private const float CardHeight = 400f;

    // The card's top edge is measured from the logo at runtime rather than fixed, because
    // the branding sits on the START SCREEN's canvas, which is Constant Pixel Size and does
    // not grow with resolution, while this panel's canvas does. Their relationship therefore
    // changes with every resolution - no single fraction can clear the logo at 1080p AND at
    // 4K. Reading where the artwork actually ends solves it at any size or aspect.
    private const float LogoGap = 26f;
    private const float FallbackCardTop = .62f;
    // Raised with CardHeight, and for the same 40 pixels. This clamp is what stops a very low
    // logo pushing the card off the bottom of the screen; at .34 with a 400-tall card the bottom
    // edge would have gone under it, where at .34 with the old 360 it cleared by a few pixels.
    private const float MinCardTop = .38f;
    private const float MaxCardTop = .95f;

    private const float Pad = 22f;
    private const float TitleTop = 18f;
    private const float TitleHeight = 30f;
    private const float DividerTop = 52f;
    private const float DividerHeight = 2f;
    private const float VersionTop = 62f;
    private const float LineHeight = 24f;
    private const float UpdateTop = 88f;
    private const float NotesTop = 122f;

    private const float FooterHeight = 34f;
    private const float StartButtonPadX = 22f;
    private const float SuppressWidth = 380f;

    // The MAYA-NAV caption spells the three drags out rather than saying "Maya-style camera" and
    // leaving the user to find out which button does what, so its row is wider. The card is 80%
    // of the screen - about 1530px at the 1920 reference - so there is room to spare, and nothing
    // else sits on this row.
    private const float MayaNavWidth = 760f;
    private const float FooterRowGap = 6f;
    private const float BoxSize = 22f;

    private const float TitleFont = 25f;
    private const float LineFont = 18f;
    private const float NoteFont = 18f;
    private const float ButtonFont = 17f;

    private ModelViewer viewer;
    private GameObject panel;
    private TextMeshProUGUI updateLabel;
    private Toggle suppressToggle;
    private readonly List<GameObject> hiddenMenuButtons = new List<GameObject>();
    private bool evaluated;
    private bool updateChecked;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<WelcomeWhatsNewAuthority>() != null) return;
        GameObject go = new GameObject(nameof(WelcomeWhatsNewAuthority));
        DontDestroyOnLoad(go);
        go.AddComponent<WelcomeWhatsNewAuthority>();
    }

    void Awake()
    {
        viewer = null;
        panel = null;
        updateLabel = null;
        suppressToggle = null;
        hiddenMenuButtons.Clear();
        evaluated = false;
        updateChecked = false;
    }

    void Update()
    {
        if (evaluated) return;

        if (viewer == null) viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer == null || viewer.uiContainer == null) return;

        // Wait for the start screen to actually be up, so the panel lands over it.
        if (!viewer.uiContainer.activeInHierarchy) return;

        evaluated = true;

        if (IsSuppressedForThisVersion()) return;

        Build();
    }

    // -------------------------------------------------------------------------- settings
    //
    // These used to be private helpers right here, reading and writing the ini directly. They
    // moved to HairBrushSettings when MAYA-NAV gained a preference that is written from two
    // places and read before either exists; two independent read-modify-writes of the same file
    // is how a setting gets silently dropped. See that file.

    static bool IsSuppressedForThisVersion()
    {
        string stored;
        if (!HairBrushSettings.ReadSettings().TryGetValue(SuppressKey, out stored)) return false;
        return string.Equals(stored, Application.version, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------------ update check

    IEnumerator CheckForUpdate()
    {
        if (updateChecked) yield break;
        updateChecked = true;

        using (UnityWebRequest request = UnityWebRequest.Get(VersionCheckUrl))
        {
            request.timeout = VersionCheckTimeoutSeconds;
            yield return request.SendWebRequest();

            // Offline, blocked, or the file moved - say nothing rather than cry wolf.
            if (request.result != UnityWebRequest.Result.Success)
            {
                SetUpdateText(string.Empty, UITheme.TextMuted);
                yield break;
            }

            string latest = (request.downloadHandler.text ?? string.Empty).Trim();
            if (latest.Length == 0) yield break;

            if (IsNewerThanRunning(latest))
            {
                SetUpdateText("UPDATE AVAILABLE - v" + latest + " is out. You are running v" + Application.version + ".",
                    new Color(1f, .82f, .35f, 1f));
            }
            else
            {
                SetUpdateText("You are on the latest version.", new Color(.62f, .82f, .70f, 1f));
            }
        }
    }

    void SetUpdateText(string text, Color colour)
    {
        if (updateLabel == null) return;
        updateLabel.text = text;
        updateLabel.color = colour;
    }

    // Dotted numeric compare, so 0.1.10 correctly beats 0.1.9 where a string compare would not.
    static bool IsNewerThanRunning(string latest)
    {
        int[] remote = ParseVersion(latest);
        int[] running = ParseVersion(Application.version);

        int length = Mathf.Max(remote.Length, running.Length);
        for (int i = 0; i < length; i++)
        {
            int a = 0;
            int b = 0;
            if (i < remote.Length) a = remote[i];
            if (i < running.Length) b = running[i];

            if (a > b) return true;
            if (a < b) return false;
        }

        return false;
    }

    static int[] ParseVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new int[0];

        string[] parts = value.Trim().Split('.');
        List<int> numbers = new List<int>(parts.Length);
        foreach (string part in parts)
        {
            int number;
            if (int.TryParse(new string(TrimToDigits(part)), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                numbers.Add(number);
            else
                numbers.Add(0);
        }

        return numbers.ToArray();
    }

    static char[] TrimToDigits(string value)
    {
        List<char> digits = new List<char>(value.Length);
        foreach (char c in value)
        {
            if (char.IsDigit(c)) digits.Add(c);
        }
        return digits.ToArray();
    }

    // ------------------------------------------------------------------------------- UI

    // Every rect here is placed by offsetMin/offsetMax alone. An earlier version set the
    // offsets and then also assigned anchoredPosition, which recomputes those same offsets
    // from the pivot - the two fought and bands landed on top of each other. Offsets only,
    // so where a thing sits is stated once.
    void Build()
    {
        GameObject stale = GameObject.Find(PanelName);
        if (stale != null) Destroy(stale);

        // Its own ROOT canvas, so the CanvasScaler below actually applies.
        panel = new GameObject(PanelName, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(Image));

        Canvas panelCanvas = panel.GetComponent<Canvas>();
        panelCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        panelCanvas.sortingOrder = PanelSortingOrder;

        CanvasScaler scaler = panel.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = ReferenceResolution;

        // Match on height: the card is anchored by screen fractions, so a wider or narrower
        // aspect just gives it more or less room either side rather than resizing the type.
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 1f;

        Stretch(panel.GetComponent<RectTransform>());
        panel.GetComponent<Image>().color = new Color(0f, 0f, 0f, .55f);

        // The card sits where the menu buttons are, so those go away while it is up.
        HideMenuButtons();

        GameObject card = new GameObject("Card", typeof(RectTransform), typeof(Image));
        card.transform.SetParent(panel.transform, false);
        RectTransform cardRect = card.GetComponent<RectTransform>();
        // Anchored by its top edge with a fixed height: the card hangs from just under the
        // logo rather than stretching between two fractions.
        float top = ResolveCardTopFraction();
        cardRect.anchorMin = new Vector2(CardLeft, top);
        cardRect.anchorMax = new Vector2(CardRight, top);
        cardRect.pivot = new Vector2(.5f, 1f);
        cardRect.anchoredPosition = Vector2.zero;
        cardRect.sizeDelta = new Vector2(0f, CardHeight);
        ApplyNineSlice(card.GetComponent<Image>(), UITheme.FineEdgeSprite, UITheme.PanelDark);

        TextMeshProUGUI title = AddBand(card.transform, "Title", TitleTop, TitleHeight);
        // The suffix lands on both lines in a demo build, and this is the larger of the two.
        // Empty in a PRO build, so this reads exactly as it does today.
        StyleLine(title, "WELCOME TO HAIRBRUSH BETA" + BuildEdition.EditionSuffix,
                  TitleFont, FontStyles.Bold, UITheme.TextBright);

        AddDivider(card.transform);

        TextMeshProUGUI version = AddBand(card.transform, "Version", VersionTop, LineHeight);
        // The version in brackets used to follow the heading here, back when the heading was a
        // separate hand-typed string that could disagree with it. They are the same number now.
        StyleLine(version, "What's new in " + ReleaseHeading,
            LineFont, FontStyles.Bold, UITheme.FillCyan);

        updateLabel = AddBand(card.transform, "UpdateStatus", UpdateTop, LineHeight);
        StyleLine(updateLabel, "Checking for updates...", LineFont, FontStyles.Bold, UITheme.TextMuted);

        BuildNotes(card.transform);
        BuildMayaNavToggle(card.transform);
        BuildSuppressToggle(card.transform);
        BuildStartButton(card.transform);

        StartCoroutine(CheckForUpdate());
    }

    // Where the branding artwork ends, as a fraction of screen height, minus a gap.
    // HideMenuButtons has already run, so every Image still active inside the menu is part
    // of the logo block. The start screen's canvas is Screen Space - Overlay, which means a
    // rect's world corners are screen pixels directly.
    float ResolveCardTopFraction()
    {
        if (viewer == null || viewer.uiContainer == null) return FallbackCardTop;
        if (Screen.height <= 1) return FallbackCardTop;

        float lowest = float.MaxValue;
        Vector3[] corners = new Vector3[4];

        foreach (Image art in viewer.uiContainer.GetComponentsInChildren<Image>(false))
        {
            // Sprite-bearing images only: that is the brush mark and the wordmark, not a
            // background fill or a label's backing plate.
            if (art == null || art.sprite == null) continue;
            if (!art.isActiveAndEnabled) continue;

            RectTransform rect = art.rectTransform;
            if (rect == null || rect.rect.height < 1f) continue;

            rect.GetWorldCorners(corners);
            float bottom = Mathf.Min(corners[0].y, corners[3].y);
            if (bottom < lowest) lowest = bottom;
        }

        if (lowest == float.MaxValue) return FallbackCardTop;

        // The gap is authored at the 1080 reference, so scale it to the real screen.
        float gap = LogoGap * (Screen.height / ReferenceResolution.y);
        return Mathf.Clamp((lowest - gap) / Screen.height, MinCardTop, MaxCardTop);
    }

    // Fills the space between the header block and the corner controls. No ScrollRect:
    // five one-line bullets always fit, and a scrollbar on five lines is just noise.
    void BuildNotes(Transform parent)
    {
        GameObject well = new GameObject("Notes", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup));
        well.transform.SetParent(parent, false);

        RectTransform rect = well.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        // Clears BOTH footer rows plus the gap between them - MAYA-NAV sits directly above the
        // suppress checkbox, and the notes well would otherwise draw over it.
        rect.offsetMin = new Vector2(Pad, (FooterHeight * 2f) + FooterRowGap + Pad + 10f);
        rect.offsetMax = new Vector2(-Pad, -NotesTop);
        // Deliberately a flat fill, not a second FineEdge: two nested 9-slice borders
        // read as boxes clipping each other rather than as one panel.
        well.GetComponent<Image>().color = new Color(.07f, .09f, .10f, .85f);

        VerticalLayoutGroup layout = well.GetComponent<VerticalLayoutGroup>();
        int inset = 14;
        layout.padding = new RectOffset(inset, inset, inset, inset);
        layout.spacing = 6f;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandHeight = false;
        layout.childAlignment = TextAnchor.UpperLeft;

        int index = 0;
        foreach (string note in ReleaseNotes)
        {
            index++;
            if (string.IsNullOrEmpty(note)) continue;
            AddNote(well.transform, "Note" + index, "•  " + note);
        }
    }

    // Named WelcomeStartButton so UIThemeAuthority leaves it alone. The shared pass calls
    // UITheme.ClampButtonSize, which forces every button to 26-32 CANVAS units tall - on
    // this 5.43x canvas that is 140-170 screen pixels, which is what turned this button
    // into a giant square. It gets the same 9-slice skin here, just not the clamp.
    void BuildStartButton(Transform parent)
    {
        GameObject buttonGO = new GameObject(StartButtonName, typeof(RectTransform), typeof(Image), typeof(Button));
        buttonGO.transform.SetParent(parent, false);

        RectTransform rect = buttonGO.GetComponent<RectTransform>();
        PinToCorner(rect, new Vector2(1f, 0f), new Vector2(-Pad, Pad),
            new Vector2(200f, FooterHeight));

        Button button = buttonGO.GetComponent<Button>();
        button.onClick.AddListener(Close);
        ApplyButtonSkin(button);

        TextMeshProUGUI label = AddStretchedText(buttonGO.transform, "Text");
        StyleLine(label, "START GROOMING", ButtonFont, FontStyles.Bold, UITheme.TextBright);
        label.alignment = TextAlignmentOptions.Center;

        // As wide as the words need, no wider.
        label.ForceMeshUpdate();
        float textWidth = label.preferredWidth;
        if (textWidth > 1f)
            rect.sizeDelta = new Vector2(textWidth + StartButtonPadX * 2f, FooterHeight);
    }

    static void ApplyButtonSkin(Button button)
    {
        Image image = button.GetComponent<Image>();
        if (image == null) return;

        Sprite normal = UITheme.ButtonNormalSprite;
        button.targetGraphic = image;

        // Flat colour fallback when the sprite set is missing, same as UITheme does.
        if (normal == null)
        {
            image.color = UITheme.ButtonNormal;
            button.transition = Selectable.Transition.ColorTint;
            return;
        }

        image.sprite = normal;
        image.type = Image.Type.Sliced;
        image.color = Color.white;

        button.transition = Selectable.Transition.SpriteSwap;
        SpriteState state = button.spriteState;
        state.highlightedSprite = UITheme.ButtonHoverSprite;
        state.pressedSprite = UITheme.ButtonClickSprite;
        state.selectedSprite = UITheme.ButtonHoverSprite;
        state.disabledSprite = normal;
        button.spriteState = state;
    }

    // MAYA-NAV's DISCOVERY, not its control. The control is the left-panel button, which is
    // reachable at any time - this box exists because nobody finds a left-panel button they are
    // not already looking for, and a navigation scheme is exactly the kind of thing a Maya user
    // wants in the first thirty seconds or never.
    //
    // It writes through on change rather than on Close, so it agrees with the left-panel button
    // the instant it is clicked - both go through MayaNavigationAuthority.SetEnabled, which is the
    // single writer. Reading its starting state from Enabled rather than from false matters too:
    // this panel reappears on every version bump, and a box that showed unticked while MAYA-NAV
    // was on would invite the user to "turn it on" and silently turn it off.
    void BuildMayaNavToggle(Transform parent)
    {
        Toggle toggle = BuildCheckbox(parent, "MayaNavToggle", new Vector2(Pad, Pad + FooterHeight + FooterRowGap),
            MayaNavWidth, "Maya-style camera - ALT + drag: LEFT tumbles, MIDDLE tracks, RIGHT dollies");

        // isOn BEFORE the listener, deliberately. Wired the other way round, setting the initial
        // state would fire onValueChanged and write the value straight back - harmless while the
        // value matches, and a silent rewrite of the user's preference the moment it does not.
        toggle.isOn = MayaNavigationAuthority.Enabled;
        toggle.onValueChanged.AddListener(MayaNavigationAuthority.SetEnabled);
    }

    void BuildSuppressToggle(Transform parent)
    {
        suppressToggle = BuildCheckbox(parent, "SuppressToggle", new Vector2(Pad, Pad),
            SuppressWidth, "Don't show this again for v" + Application.version);
        suppressToggle.isOn = false;
    }

    // The two footer checkboxes are identical apart from where they sit, how wide they are and
    // what they say, so the second one is not a second copy of this. offset is from the card's
    // bottom-LEFT corner.
    //
    // width is not cosmetic. StyleLine sets NoWrap with Ellipsis, so a caption wider than the row
    // is not wrapped or shrunk - it is silently cut off with a "...", which is how a checkbox ends
    // up shipping as "Maya-style camera - ALT + drag: LEFT tumbles, MID...".
    Toggle BuildCheckbox(Transform parent, string name, Vector2 offset, float width, string caption)
    {
        GameObject toggleGO = new GameObject(name, typeof(RectTransform), typeof(Toggle));
        toggleGO.transform.SetParent(parent, false);
        PinToCorner(toggleGO.GetComponent<RectTransform>(), new Vector2(0f, 0f),
            offset, new Vector2(width, FooterHeight));

        GameObject boxGO = new GameObject("Box", typeof(RectTransform), typeof(Image));
        boxGO.transform.SetParent(toggleGO.transform, false);
        RectTransform box = boxGO.GetComponent<RectTransform>();
        box.anchorMin = new Vector2(0f, .5f);
        box.anchorMax = new Vector2(0f, .5f);
        box.pivot = new Vector2(0f, .5f);
        box.anchoredPosition = Vector2.zero;
        box.sizeDelta = new Vector2(BoxSize, BoxSize);
        ApplyNineSlice(boxGO.GetComponent<Image>(), UITheme.FineEdgeSprite, Color.white);

        GameObject tickGO = new GameObject("Tick", typeof(RectTransform), typeof(Image));
        tickGO.transform.SetParent(boxGO.transform, false);
        RectTransform tick = tickGO.GetComponent<RectTransform>();
        tick.anchorMin = Vector2.zero;
        tick.anchorMax = Vector2.one;
        tick.offsetMin = new Vector2(4f, 4f);
        tick.offsetMax = new Vector2(-4f, -4f);
        tickGO.GetComponent<Image>().color = UITheme.ButtonPressed;

        GameObject captionGO = new GameObject("Caption", typeof(RectTransform), typeof(TextMeshProUGUI));
        captionGO.transform.SetParent(toggleGO.transform, false);
        RectTransform captionRect = captionGO.GetComponent<RectTransform>();
        captionRect.anchorMin = Vector2.zero;
        captionRect.anchorMax = Vector2.one;
        captionRect.offsetMin = new Vector2(BoxSize + 10f, 0f);
        captionRect.offsetMax = Vector2.zero;

        StyleLine(captionGO.GetComponent<TextMeshProUGUI>(),
            caption, LineFont, FontStyles.Normal, UITheme.TextMuted);

        Toggle toggle = toggleGO.GetComponent<Toggle>();
        toggle.targetGraphic = boxGO.GetComponent<Image>();
        toggle.graphic = tickGO.GetComponent<Image>();
        return toggle;
    }

    // ------------------------------------------------------------------- menu visibility

    void HideMenuButtons()
    {
        hiddenMenuButtons.Clear();
        if (viewer == null || viewer.uiContainer == null) return;

        // Only the action buttons - the logo and wordmark stay on show above the card.
        foreach (Button button in viewer.uiContainer.GetComponentsInChildren<Button>(true))
        {
            if (button == null || !button.gameObject.activeSelf) continue;
            hiddenMenuButtons.Add(button.gameObject);
            button.gameObject.SetActive(false);
        }
    }

    void RestoreMenuButtons()
    {
        foreach (GameObject go in hiddenMenuButtons)
        {
            if (go != null) go.SetActive(true);
        }
        hiddenMenuButtons.Clear();
    }

    void Close()
    {
        // Only records anything if the box is ticked. Left unticked, the panel comes back
        // next launch - and either way a version bump makes the stored value stop matching.
        if (suppressToggle != null && suppressToggle.isOn)
            HairBrushSettings.WriteSetting(SuppressKey, Application.version);

        RestoreMenuButtons();

        if (panel != null) Destroy(panel);
        panel = null;
    }

    // ------------------------------------------------------------------------- UI helpers

    static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    static void PinToCorner(RectTransform rect, Vector2 corner, Vector2 inset, Vector2 size)
    {
        rect.anchorMin = corner;
        rect.anchorMax = corner;
        rect.pivot = corner;
        rect.anchoredPosition = inset;
        rect.sizeDelta = size;
    }

    // A full-width band whose top edge sits `top` units below the card's top edge.
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

    static void AddNote(Transform parent, string name, string text)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(LayoutElement), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);

        LayoutElement layout = go.GetComponent<LayoutElement>();
        layout.minHeight = NoteFont + 7f;
        layout.preferredHeight = NoteFont + 7f;

        TextMeshProUGUI label = go.GetComponent<TextMeshProUGUI>();
        label.text = text;
        label.fontSize = NoteFont;
        label.fontStyle = FontStyles.Normal;
        label.color = UITheme.TextBright;
        label.alignment = TextAlignmentOptions.MidlineLeft;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.overflowMode = TextOverflowModes.Ellipsis;
        label.enableAutoSizing = false;
        label.raycastTarget = false;
    }
}
