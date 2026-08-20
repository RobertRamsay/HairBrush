using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
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
// Application.persistentDataPath is the one location guaranteed writable on every platform.
[DefaultExecutionOrder(9700)]
public class WelcomeWhatsNewAuthority : MonoBehaviour
{
    // ---------------------------------------------------------------------------------
    // Release notes. Add a block at the top for each release; only the first is displayed.
    // ---------------------------------------------------------------------------------
    private const string ReleaseHeading = "BETA 0.1.5";

    // Five at most, one line each - the panel does not scroll.
    private static readonly string[] ReleaseNotes =
    {
        "Rename a group in place - double-click its name on the row.",
        "SS/DS button per group for single- or double-sided rendering.",
        "Curl banks into its coil, and bend now keeps curls round.",
        "SEGMENT DENSITY curve finally means density, not position.",
        "Light angle slider, brush mode readout, frozen panel header.",
    };

    // ---------------------------------------------------------------------------------

    private const string SettingsFileName = "hairbrush.ini";
    private const string SuppressKey = "suppressWelcomeForVersion";

    // Bump the file in the repo when a release goes out and every running copy sees it.
    private const string VersionCheckUrl =
        "https://raw.githubusercontent.com/RobertRamsay/HairBrush/main/hairbrush_version.txt";
    private const int VersionCheckTimeoutSeconds = 8;

    private const string PanelName = "WelcomeWhatsNewPanel";

    // UIThemeAuthority skips this name - see BuildStartButton.
    public const string StartButtonName = "WelcomeStartButton";

    // Card position as a fraction of the screen. Top deliberately clears the logo and
    // wordmark; nudge CardTop/CardBottom if the branding artwork ever changes size.
    private const float CardLeft = .07f;
    private const float CardRight = .93f;
    private const float CardBottom = .24f;
    private const float CardTop = .60f;

    // Everything below is authored as PIXELS AT 1080p and converted by U().
    //
    // The start screen's canvas is Constant Pixel Size, so it does not adapt to resolution
    // at all: a fixed size is the same number of pixels on a 4K display as on a 1080p one,
    // which is why the panel came out unreadably small at 4K. Scaling by the canvas's own
    // height against a 1080-high reference makes every element hold its proportion of the
    // screen instead, so the panel looks identical at any resolution.
    private const float ReferenceHeight = 1080f;
    private const float Pad = 12f;
    private const float TitleTop = 12f;
    private const float TitleHeight = 20f;
    private const float DividerTop = 36f;
    private const float DividerHeight = 2f;
    private const float VersionTop = 43f;
    private const float LineHeight = 15f;
    private const float UpdateTop = 60f;
    private const float NotesTop = 80f;

    // Corner controls: one line tall, tight.
    private const float FooterHeight = 22f;
    // Width is measured from the label, not fixed: the button is exactly as wide as the
    // words plus padding.
    private const float StartButtonPadX = 16f;
    private const float SuppressWidth = 250f;
    private const float BoxSize = 15f;

    private const float TitleFont = 16f;
    private const float LineFont = 12.5f;
    private const float NoteFont = 13f;
    private const float ButtonFont = 12.5f;

    // Canvas units per authored pixel, for the canvas this panel was built on.
    private static float unit = 1f;

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

        Canvas canvas = viewer.uiContainer.GetComponentInParent<Canvas>();
        if (canvas == null) return;

        Build(canvas.rootCanvas);
    }

    // -------------------------------------------------------------------------- settings

    static string SettingsPath()
    {
        return Path.Combine(Application.persistentDataPath, SettingsFileName);
    }

    static Dictionary<string, string> ReadSettings()
    {
        Dictionary<string, string> values = new Dictionary<string, string>();

        try
        {
            string path = SettingsPath();
            if (!File.Exists(path)) return values;

            foreach (string line in File.ReadAllLines(path))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                if (trimmed.StartsWith(";") || trimmed.StartsWith("#") || trimmed.StartsWith("[")) continue;

                int split = trimmed.IndexOf('=');
                if (split <= 0) continue;

                values[trimmed.Substring(0, split).Trim()] = trimmed.Substring(split + 1).Trim();
            }
        }
        catch (Exception error)
        {
            // A settings file that cannot be read is not worth failing a launch over.
            Debug.LogWarning("HairBrush: could not read " + SettingsFileName + " - " + error.Message);
        }

        return values;
    }

    static void WriteSetting(string key, string value)
    {
        Dictionary<string, string> values = ReadSettings();
        values[key] = value;

        StringBuilder builder = new StringBuilder();
        builder.AppendLine("; HairBrush settings. Safe to delete - it will be rebuilt.");
        builder.AppendLine("[HairBrush]");
        foreach (KeyValuePair<string, string> pair in values)
            builder.AppendLine(pair.Key + "=" + pair.Value);

        try
        {
            File.WriteAllText(SettingsPath(), builder.ToString());
        }
        catch (Exception error)
        {
            Debug.LogWarning("HairBrush: could not write " + SettingsFileName + " - " + error.Message);
        }
    }

    static bool IsSuppressedForThisVersion()
    {
        string stored;
        if (!ReadSettings().TryGetValue(SuppressKey, out stored)) return false;
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
    void Build(Canvas canvas)
    {
        Transform existing = canvas.transform.Find(PanelName);
        if (existing != null) Destroy(existing.gameObject);

        // Canvas first: GraphicRaycaster auto-adds one, and Canvas is DisallowMultipleComponent,
        // so listing it after the raycaster would fail to add.
        panel = new GameObject(PanelName, typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster), typeof(Image));
        panel.transform.SetParent(canvas.transform, false);

        Canvas panelCanvas = panel.GetComponent<Canvas>();
        panelCanvas.overrideSorting = true;
        panelCanvas.sortingOrder = 200;

        // rect.height is already the screen height divided by the canvas scale factor, so
        // this one expression covers both the scale factor and the resolution.
        RectTransform canvasRect = canvas.transform as RectTransform;
        unit = 1f;
        if (canvasRect != null && canvasRect.rect.height > 1f)
            unit = canvasRect.rect.height / ReferenceHeight;

        Stretch(panel.GetComponent<RectTransform>());
        panel.GetComponent<Image>().color = new Color(0f, 0f, 0f, .55f);

        // The card sits where the menu buttons are, so those go away while it is up.
        HideMenuButtons();

        GameObject card = new GameObject("Card", typeof(RectTransform), typeof(Image));
        card.transform.SetParent(panel.transform, false);
        RectTransform cardRect = card.GetComponent<RectTransform>();
        cardRect.anchorMin = new Vector2(CardLeft, CardBottom);
        cardRect.anchorMax = new Vector2(CardRight, CardTop);
        cardRect.offsetMin = Vector2.zero;
        cardRect.offsetMax = Vector2.zero;
        ApplyNineSlice(card.GetComponent<Image>(), UITheme.FineEdgeSprite, UITheme.PanelDark);

        TextMeshProUGUI title = AddBand(card.transform, "Title", U(TitleTop), U(TitleHeight));
        StyleLine(title, "WELCOME TO HAIRBRUSH BETA", U(TitleFont), FontStyles.Bold, UITheme.TextBright);

        AddDivider(card.transform);

        TextMeshProUGUI version = AddBand(card.transform, "Version", U(VersionTop), U(LineHeight));
        StyleLine(version, "What's new in " + ReleaseHeading + "   (v" + Application.version + ")",
            U(LineFont), FontStyles.Bold, UITheme.FillCyan);

        updateLabel = AddBand(card.transform, "UpdateStatus", U(UpdateTop), U(LineHeight));
        StyleLine(updateLabel, "Checking for updates...", U(LineFont), FontStyles.Bold, UITheme.TextMuted);

        BuildNotes(card.transform);
        BuildSuppressToggle(card.transform);
        BuildStartButton(card.transform);

        StartCoroutine(CheckForUpdate());
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
        rect.offsetMin = new Vector2(U(Pad), U(FooterHeight + Pad + 6f));
        rect.offsetMax = new Vector2(-U(Pad), -U(NotesTop));
        ApplyNineSlice(well.GetComponent<Image>(), UITheme.FineEdgeSprite, UITheme.TrackDark);

        VerticalLayoutGroup layout = well.GetComponent<VerticalLayoutGroup>();
        int inset = Mathf.RoundToInt(U(9f));
        layout.padding = new RectOffset(inset, inset, inset, inset);
        layout.spacing = U(3f);
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
        PinToCorner(rect, new Vector2(1f, 0f), new Vector2(-U(Pad), U(Pad)),
            new Vector2(U(120f), U(FooterHeight)));

        Button button = buttonGO.GetComponent<Button>();
        button.onClick.AddListener(Close);
        ApplyButtonSkin(button);

        TextMeshProUGUI label = AddStretchedText(buttonGO.transform, "Text");
        StyleLine(label, "START GROOMING", U(ButtonFont), FontStyles.Bold, UITheme.TextBright);
        label.alignment = TextAlignmentOptions.Center;

        // As wide as the words need, no wider.
        label.ForceMeshUpdate();
        float textWidth = label.preferredWidth;
        if (textWidth > 1f)
            rect.sizeDelta = new Vector2(textWidth + U(StartButtonPadX) * 2f, U(FooterHeight));
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

    void BuildSuppressToggle(Transform parent)
    {
        GameObject toggleGO = new GameObject("SuppressToggle", typeof(RectTransform), typeof(Toggle));
        toggleGO.transform.SetParent(parent, false);
        PinToCorner(toggleGO.GetComponent<RectTransform>(), new Vector2(0f, 0f),
            new Vector2(U(Pad), U(Pad)), new Vector2(U(SuppressWidth), U(FooterHeight)));

        GameObject boxGO = new GameObject("Box", typeof(RectTransform), typeof(Image));
        boxGO.transform.SetParent(toggleGO.transform, false);
        RectTransform box = boxGO.GetComponent<RectTransform>();
        box.anchorMin = new Vector2(0f, .5f);
        box.anchorMax = new Vector2(0f, .5f);
        box.pivot = new Vector2(0f, .5f);
        box.anchoredPosition = Vector2.zero;
        box.sizeDelta = new Vector2(U(BoxSize), U(BoxSize));
        ApplyNineSlice(boxGO.GetComponent<Image>(), UITheme.FineEdgeSprite, Color.white);

        GameObject tickGO = new GameObject("Tick", typeof(RectTransform), typeof(Image));
        tickGO.transform.SetParent(boxGO.transform, false);
        RectTransform tick = tickGO.GetComponent<RectTransform>();
        tick.anchorMin = Vector2.zero;
        tick.anchorMax = Vector2.one;
        tick.offsetMin = new Vector2(U(3f), U(3f));
        tick.offsetMax = new Vector2(-U(3f), -U(3f));
        tickGO.GetComponent<Image>().color = UITheme.ButtonPressed;

        GameObject captionGO = new GameObject("Caption", typeof(RectTransform), typeof(TextMeshProUGUI));
        captionGO.transform.SetParent(toggleGO.transform, false);
        RectTransform caption = captionGO.GetComponent<RectTransform>();
        caption.anchorMin = Vector2.zero;
        caption.anchorMax = Vector2.one;
        caption.offsetMin = new Vector2(U(BoxSize + 7f), 0f);
        caption.offsetMax = Vector2.zero;

        StyleLine(captionGO.GetComponent<TextMeshProUGUI>(),
            "Don't show this again for v" + Application.version, U(LineFont), FontStyles.Normal, UITheme.TextMuted);

        suppressToggle = toggleGO.GetComponent<Toggle>();
        suppressToggle.targetGraphic = boxGO.GetComponent<Image>();
        suppressToggle.graphic = tickGO.GetComponent<Image>();
        suppressToggle.isOn = false;
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
            WriteSetting(SuppressKey, Application.version);

        RestoreMenuButtons();

        if (panel != null) Destroy(panel);
        panel = null;
    }

    // ------------------------------------------------------------------------- UI helpers

    // Screen pixels -> canvas units for the canvas this panel is on.
    static float U(float pixels)
    {
        return pixels * unit;
    }

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
        rect.offsetMin = new Vector2(U(Pad), -(top + height));
        rect.offsetMax = new Vector2(-U(Pad), -top);

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
        rect.offsetMin = new Vector2(U(Pad), -U(DividerTop + DividerHeight));
        rect.offsetMax = new Vector2(-U(Pad), -U(DividerTop));

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
        layout.minHeight = U(NoteFont + 4f);
        layout.preferredHeight = U(NoteFont + 4f);

        TextMeshProUGUI label = go.GetComponent<TextMeshProUGUI>();
        label.text = text;
        label.fontSize = U(NoteFont);
        label.fontStyle = FontStyles.Normal;
        label.color = UITheme.TextBright;
        label.alignment = TextAlignmentOptions.MidlineLeft;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.overflowMode = TextOverflowModes.Ellipsis;
        label.enableAutoSizing = false;
        label.raycastTarget = false;
    }
}
