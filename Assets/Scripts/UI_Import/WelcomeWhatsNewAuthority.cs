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

    private static readonly string[] ReleaseNotes =
    {
        "GROUPS",
        "Rename a group in place - double-click the name and type. Caret, backspace, CTRL+BACKSPACE to clear, ESC to cancel, empty keeps the old name. Works in the standalone build, which the old rename dialog never did.",
        "New SS/DS button on each group row toggles single- or double-sided rendering for that group. Saved with the project.",
        "The group list scrollbar now hides when there is nothing to scroll, and the wheel no longer jumps.",
        "",
        "SHAPE",
        "Curl now banks into its own coil, so a curled card reads as a coil rather than a ribbon twisting on the spot.",
        "Bend aims each cross-section along the spine's real direction of travel. Curls keep their round section however hard the card is bent - they used to squash to about half their width at a 90 degree bend.",
        "SEGMENT DENSITY finally means density: Y is segments per unit length, flat gives even spacing at any height, and a dip to zero puts no segments there at all.",
        "",
        "WORKFLOW",
        "Loading a project selects its first group and adopts that group's own settings, so hairs added straight after a load match the ones already there.",
        "BRUSH MODE is shown across the bottom of the viewport with a note on what a click does in it.",
        "The panel header - MENU, SAVE PROJ, PLACEMENT - stays put while the controls below it scroll.",
        "LIGHT ANGLE slider in the Hair Groups panel swings the key light around the model.",
        "CTRL+BACKSPACE no longer summons Unity's rendering debugger over the tool.",
    };

    // ---------------------------------------------------------------------------------

    private const string SettingsFileName = "hairbrush.ini";
    private const string SuppressKey = "suppressWelcomeForVersion";

    // Bump the file in the repo when a release goes out and every running copy sees it.
    private const string VersionCheckUrl =
        "https://raw.githubusercontent.com/RobertRamsay/HairBrush/main/hairbrush_version.txt";
    private const int VersionCheckTimeoutSeconds = 8;

    private const string PanelName = "WelcomeWhatsNewPanel";

    // Card geometry as a fraction of the screen, so it stays a wide banner at any
    // resolution rather than a fixed pixel box that shrinks on a large display.
    private const float CardMarginX = .12f;
    private const float CardMarginBottom = .26f;
    private const float CardMarginTop = .72f;

    private const float Pad = 16f;
    private const float TitleHeight = 26f;
    private const float DividerHeight = 3f;
    private const float LineHeight = 18f;

    // Buttons a single line tall with tight padding, per the rest of the tool.
    private const float FooterHeight = 26f;
    private const float StartButtonWidth = 190f;
    private const float SuppressWidth = 300f;
    private const float BoxSize = 18f;

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

    // Regions are anchored explicitly rather than stacked in a VerticalLayoutGroup. The
    // card is a fixed shape and the only part that needs to grow is the notes area, so
    // pinning each band to the card's edges is both simpler and exact - no relying on
    // flexible-height distribution to land where the design says it should.
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

        RectTransform dim = panel.GetComponent<RectTransform>();
        dim.anchorMin = Vector2.zero;
        dim.anchorMax = Vector2.one;
        dim.offsetMin = Vector2.zero;
        dim.offsetMax = Vector2.zero;
        panel.GetComponent<Image>().color = new Color(0f, 0f, 0f, .55f);

        // The card sits over the menu buttons, so those are hidden while it is up.
        HideMenuButtons();

        GameObject card = new GameObject("Card", typeof(RectTransform), typeof(Image));
        card.transform.SetParent(panel.transform, false);
        RectTransform cardRect = card.GetComponent<RectTransform>();
        cardRect.anchorMin = new Vector2(CardMarginX, CardMarginBottom);
        cardRect.anchorMax = new Vector2(1f - CardMarginX, CardMarginTop);
        cardRect.offsetMin = Vector2.zero;
        cardRect.offsetMax = Vector2.zero;
        ApplyNineSlice(card.GetComponent<Image>(), UITheme.FineEdgeSprite, UITheme.PanelDark);

        float y = -Pad;

        TextMeshProUGUI title = AddBand(card.transform, "Title", y, TitleHeight);
        StyleText(title, "WELCOME TO HAIRBRUSH BETA", 20f, FontStyles.Bold, UITheme.TextBright);
        y -= TitleHeight + 4f;

        AddDivider(card.transform, y);
        y -= DividerHeight + 6f;

        TextMeshProUGUI version = AddBand(card.transform, "Version", y, LineHeight);
        StyleText(version, "What's new in " + ReleaseHeading + "   (v" + Application.version + ")", 13f,
            FontStyles.Bold, UITheme.FillCyan);
        y -= LineHeight + 2f;

        updateLabel = AddBand(card.transform, "UpdateStatus", y, LineHeight);
        StyleText(updateLabel, "Checking for updates...", 12f, FontStyles.Bold, UITheme.TextMuted);
        y -= LineHeight + 8f;

        BuildNotes(card.transform, y);
        BuildFooter(card.transform);

        StartCoroutine(CheckForUpdate());
    }

    // Notes fill everything between the header block above and the footer below.
    void BuildNotes(Transform parent, float top)
    {
        GameObject scrollGO = new GameObject("NotesScroll", typeof(RectTransform), typeof(Image), typeof(ScrollRect));
        scrollGO.transform.SetParent(parent, false);

        RectTransform scrollRect = scrollGO.GetComponent<RectTransform>();
        scrollRect.anchorMin = new Vector2(0f, 0f);
        scrollRect.anchorMax = new Vector2(1f, 1f);
        scrollRect.offsetMin = new Vector2(Pad, Pad + FooterHeight + 8f);
        scrollRect.offsetMax = new Vector2(-Pad, top);
        ApplyNineSlice(scrollGO.GetComponent<Image>(), UITheme.FineEdgeSprite, UITheme.TrackDark);

        GameObject viewportGO = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
        viewportGO.transform.SetParent(scrollGO.transform, false);
        RectTransform viewport = viewportGO.GetComponent<RectTransform>();
        viewport.anchorMin = Vector2.zero;
        viewport.anchorMax = Vector2.one;
        viewport.offsetMin = new Vector2(12f, 8f);
        viewport.offsetMax = new Vector2(-12f, -8f);

        GameObject contentGO = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
        contentGO.transform.SetParent(viewportGO.transform, false);
        RectTransform content = contentGO.GetComponent<RectTransform>();
        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = new Vector2(1f, 1f);
        content.pivot = new Vector2(.5f, 1f);
        content.sizeDelta = Vector2.zero;

        VerticalLayoutGroup contentLayout = contentGO.GetComponent<VerticalLayoutGroup>();
        contentLayout.spacing = 5f;
        contentLayout.childControlWidth = true;
        contentLayout.childControlHeight = true;
        contentLayout.childForceExpandHeight = false;
        contentGO.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        int index = 0;
        foreach (string note in ReleaseNotes)
        {
            index++;
            if (string.IsNullOrEmpty(note))
            {
                AddGap(contentGO.transform, "Gap" + index);
                continue;
            }

            // A line with no lower-case letters is a section heading, not a bullet.
            if (note == note.ToUpperInvariant())
            {
                AddNote(contentGO.transform, "Section" + index, note, 12f, FontStyles.Bold, UITheme.FillCyan);
                continue;
            }

            AddNote(contentGO.transform, "Note" + index, "•  " + note, 12f, FontStyles.Normal, UITheme.TextBright);
        }

        ScrollRect scroll = scrollGO.GetComponent<ScrollRect>();
        scroll.content = content;
        scroll.viewport = viewport;
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.inertia = false;
        scroll.scrollSensitivity = 26f;
    }

    void BuildFooter(Transform parent)
    {
        BuildSuppressToggle(parent);

        GameObject buttonGO = new GameObject("CloseButton", typeof(RectTransform), typeof(Image), typeof(Button));
        buttonGO.transform.SetParent(parent, false);

        RectTransform rect = buttonGO.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(1f, 0f);
        rect.anchorMax = new Vector2(1f, 0f);
        rect.pivot = new Vector2(1f, 0f);
        rect.anchoredPosition = new Vector2(-Pad, Pad);
        rect.sizeDelta = new Vector2(StartButtonWidth, FooterHeight);

        Button button = buttonGO.GetComponent<Button>();
        button.onClick.AddListener(Close);

        // Same 9-slice skin, hover and click states as every other button in the tool.
        UITheme.StyleButton(button);

        TextMeshProUGUI label = AddStretchedText(buttonGO.transform, "Text");
        StyleText(label, "START GROOMING", 14f, FontStyles.Bold, UITheme.TextBright);
        label.alignment = TextAlignmentOptions.Center;
    }

    void BuildSuppressToggle(Transform parent)
    {
        GameObject toggleGO = new GameObject("SuppressToggle", typeof(RectTransform), typeof(Toggle));
        toggleGO.transform.SetParent(parent, false);

        RectTransform rect = toggleGO.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 0f);
        rect.anchorMax = new Vector2(0f, 0f);
        rect.pivot = new Vector2(0f, 0f);
        rect.anchoredPosition = new Vector2(Pad, Pad);
        rect.sizeDelta = new Vector2(SuppressWidth, FooterHeight);

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
        RectTransform caption = captionGO.GetComponent<RectTransform>();
        caption.anchorMin = Vector2.zero;
        caption.anchorMax = Vector2.one;
        caption.offsetMin = new Vector2(BoxSize + 8f, 0f);
        caption.offsetMax = Vector2.zero;

        TextMeshProUGUI captionText = captionGO.GetComponent<TextMeshProUGUI>();
        StyleText(captionText, "Don't show this again for v" + Application.version, 12f, FontStyles.Normal, UITheme.TextMuted);

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

        // Only the action buttons - the logo and the brand header stay on show behind the card.
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

    static void ApplyNineSlice(Image image, Sprite sprite, Color colour)
    {
        if (image == null) return;
        image.color = colour;

        // Falls back to a flat fill if the sprites are missing, exactly as UITheme does.
        if (sprite == null) return;
        image.sprite = sprite;
        image.type = Image.Type.Sliced;
    }

    // A full-width band pinned below the card's top edge.
    static TextMeshProUGUI AddBand(Transform parent, string name, float top, float height)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);

        RectTransform rect = go.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(.5f, 1f);
        rect.offsetMin = new Vector2(Pad, -height);
        rect.offsetMax = new Vector2(-Pad, 0f);
        rect.anchoredPosition = new Vector2(rect.anchoredPosition.x, top);

        return go.GetComponent<TextMeshProUGUI>();
    }

    static void AddDivider(Transform parent, float top)
    {
        GameObject go = new GameObject("Divider", typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);

        RectTransform rect = go.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(.5f, 1f);
        rect.offsetMin = new Vector2(Pad, -DividerHeight);
        rect.offsetMax = new Vector2(-Pad, 0f);
        rect.anchoredPosition = new Vector2(rect.anchoredPosition.x, top);

        Image image = go.GetComponent<Image>();
        ApplyNineSlice(image, UITheme.DividerSprite, Color.white);
        image.raycastTarget = false;
    }

    static TextMeshProUGUI AddStretchedText(Transform parent, string name)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);

        RectTransform rect = go.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        return go.GetComponent<TextMeshProUGUI>();
    }

    static void StyleText(TextMeshProUGUI label, string text, float size, FontStyles style, Color colour)
    {
        if (label == null) return;
        label.text = text;
        label.fontSize = size;
        label.fontStyle = style;
        label.color = colour;
        label.alignment = TextAlignmentOptions.MidlineLeft;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.overflowMode = TextOverflowModes.Ellipsis;
        label.raycastTarget = false;
    }

    static void AddGap(Transform parent, string name)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        LayoutElement layout = go.GetComponent<LayoutElement>();
        layout.minHeight = 9f;
        layout.preferredHeight = 9f;
    }

    static void AddNote(Transform parent, string name, string text, float size, FontStyles style, Color colour)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);

        TextMeshProUGUI label = go.GetComponent<TextMeshProUGUI>();
        label.text = text;
        label.fontSize = size;
        label.fontStyle = style;
        label.color = colour;
        label.alignment = TextAlignmentOptions.TopLeft;
        label.textWrappingMode = TextWrappingModes.Normal;
        label.raycastTarget = false;
    }
}
