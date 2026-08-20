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
    private const float PanelWidth = 720f;
    private const float PanelHeight = 560f;

    private ModelViewer viewer;
    private GameObject panel;
    private TextMeshProUGUI updateLabel;
    private Toggle suppressToggle;
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
                SetUpdateText(string.Empty, new Color(.72f, .78f, .86f, 1f));
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
                SetUpdateText("You are on the latest version.", new Color(.62f, .78f, .66f, 1f));
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

    void Build(Canvas canvas)
    {
        Transform existing = canvas.transform.Find(PanelName);
        if (existing != null) Destroy(existing.gameObject);

        // Full-screen dimmer, so the panel reads as modal and nothing behind it is clickable.
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
        panel.GetComponent<Image>().color = new Color(0f, 0f, 0f, .72f);

        GameObject card = new GameObject("Card", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup));
        card.transform.SetParent(panel.transform, false);
        RectTransform cardRect = card.GetComponent<RectTransform>();
        cardRect.anchorMin = new Vector2(.5f, .5f);
        cardRect.anchorMax = new Vector2(.5f, .5f);
        cardRect.pivot = new Vector2(.5f, .5f);
        cardRect.sizeDelta = new Vector2(PanelWidth, PanelHeight);
        card.GetComponent<Image>().color = new Color(.13f, .14f, .16f, 1f);

        VerticalLayoutGroup cardLayout = card.GetComponent<VerticalLayoutGroup>();
        cardLayout.padding = new RectOffset(26, 26, 22, 20);
        cardLayout.spacing = 10f;
        cardLayout.childControlWidth = true;
        cardLayout.childControlHeight = false;
        cardLayout.childForceExpandHeight = false;

        AddLabel(card.transform, "Title", "WELCOME TO HAIRBRUSH BETA", 24f, FontStyles.Bold,
            Color.white, TextAlignmentOptions.MidlineLeft, 34f);
        AddLabel(card.transform, "Version", "What's new in " + ReleaseHeading + "   (v" + Application.version + ")", 14f,
            FontStyles.Bold, new Color(.62f, .82f, .88f, 1f), TextAlignmentOptions.MidlineLeft, 22f);

        updateLabel = AddLabel(card.transform, "UpdateStatus", "Checking for updates...", 13f, FontStyles.Bold,
            new Color(.72f, .78f, .86f, 1f), TextAlignmentOptions.MidlineLeft, 20f);

        BuildNotes(card.transform);
        BuildFooter(card.transform);

        StartCoroutine(CheckForUpdate());
    }

    void BuildNotes(Transform parent)
    {
        GameObject scrollGO = new GameObject("NotesScroll", typeof(RectTransform), typeof(Image), typeof(ScrollRect), typeof(LayoutElement));
        scrollGO.transform.SetParent(parent, false);
        scrollGO.GetComponent<Image>().color = new Color(.09f, .10f, .11f, 1f);

        LayoutElement scrollLayout = scrollGO.GetComponent<LayoutElement>();
        scrollLayout.flexibleHeight = 1f;
        scrollLayout.preferredHeight = 360f;

        GameObject viewportGO = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
        viewportGO.transform.SetParent(scrollGO.transform, false);
        RectTransform viewport = viewportGO.GetComponent<RectTransform>();
        viewport.anchorMin = Vector2.zero;
        viewport.anchorMax = Vector2.one;
        viewport.offsetMin = new Vector2(12f, 10f);
        viewport.offsetMax = new Vector2(-12f, -10f);

        GameObject contentGO = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
        contentGO.transform.SetParent(viewportGO.transform, false);
        RectTransform content = contentGO.GetComponent<RectTransform>();
        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = new Vector2(1f, 1f);
        content.pivot = new Vector2(.5f, 1f);
        content.sizeDelta = Vector2.zero;

        VerticalLayoutGroup contentLayout = contentGO.GetComponent<VerticalLayoutGroup>();
        contentLayout.spacing = 6f;
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
                AddNote(contentGO.transform, "Section" + index, note, 13f, FontStyles.Bold,
                    new Color(.62f, .82f, .88f, 1f));
                continue;
            }

            AddNote(contentGO.transform, "Note" + index, "•  " + note, 13f, FontStyles.Normal,
                new Color(.86f, .88f, .92f, 1f));
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
        GameObject footer = new GameObject("Footer", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        footer.transform.SetParent(parent, false);

        LayoutElement footerLayout = footer.GetComponent<LayoutElement>();
        footerLayout.minHeight = 38f;
        footerLayout.preferredHeight = 38f;

        HorizontalLayoutGroup layout = footer.GetComponent<HorizontalLayoutGroup>();
        layout.spacing = 12f;
        layout.childControlWidth = false;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childAlignment = TextAnchor.MiddleLeft;

        BuildSuppressToggle(footer.transform);

        GameObject spacer = new GameObject("Spacer", typeof(RectTransform), typeof(LayoutElement));
        spacer.transform.SetParent(footer.transform, false);
        spacer.GetComponent<LayoutElement>().flexibleWidth = 1f;

        GameObject buttonGO = new GameObject("CloseButton", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        buttonGO.transform.SetParent(footer.transform, false);
        LayoutElement buttonLayout = buttonGO.GetComponent<LayoutElement>();
        buttonLayout.minWidth = 150f;
        buttonLayout.preferredWidth = 150f;
        buttonGO.GetComponent<Image>().color = new Color(.20f, .50f, .80f, 1f);
        buttonGO.GetComponent<Button>().onClick.AddListener(Close);

        AddLabel(buttonGO.transform, "Text", "START GROOMING", 15f, FontStyles.Bold,
            Color.white, TextAlignmentOptions.Center, 0f, true);
    }

    void BuildSuppressToggle(Transform parent)
    {
        GameObject toggleGO = new GameObject("SuppressToggle", typeof(RectTransform), typeof(Toggle), typeof(LayoutElement));
        toggleGO.transform.SetParent(parent, false);
        LayoutElement toggleLayout = toggleGO.GetComponent<LayoutElement>();
        toggleLayout.minWidth = 300f;
        toggleLayout.preferredWidth = 300f;

        GameObject boxGO = new GameObject("Box", typeof(RectTransform), typeof(Image));
        boxGO.transform.SetParent(toggleGO.transform, false);
        RectTransform box = boxGO.GetComponent<RectTransform>();
        box.anchorMin = new Vector2(0f, .5f);
        box.anchorMax = new Vector2(0f, .5f);
        box.pivot = new Vector2(0f, .5f);
        box.anchoredPosition = new Vector2(0f, 0f);
        box.sizeDelta = new Vector2(20f, 20f);
        boxGO.GetComponent<Image>().color = new Color(.24f, .25f, .28f, 1f);

        GameObject tickGO = new GameObject("Tick", typeof(RectTransform), typeof(Image));
        tickGO.transform.SetParent(boxGO.transform, false);
        RectTransform tick = tickGO.GetComponent<RectTransform>();
        tick.anchorMin = Vector2.zero;
        tick.anchorMax = Vector2.one;
        tick.offsetMin = new Vector2(4f, 4f);
        tick.offsetMax = new Vector2(-4f, -4f);
        tickGO.GetComponent<Image>().color = new Color(.30f, .72f, .82f, 1f);

        TextMeshProUGUI caption = AddLabel(toggleGO.transform, "Caption",
            "Don't show this again for v" + Application.version, 13f, FontStyles.Normal,
            new Color(.80f, .84f, .90f, 1f), TextAlignmentOptions.MidlineLeft, 0f, true);
        RectTransform captionRect = caption.rectTransform;
        captionRect.offsetMin = new Vector2(28f, 0f);
        captionRect.offsetMax = new Vector2(0f, 0f);

        suppressToggle = toggleGO.GetComponent<Toggle>();
        suppressToggle.targetGraphic = boxGO.GetComponent<Image>();
        suppressToggle.graphic = tickGO.GetComponent<Image>();
        suppressToggle.isOn = false;
    }

    void Close()
    {
        // Only records anything if the box is ticked. Left unticked, the panel comes back
        // next launch - and either way a version bump makes the stored value stop matching.
        if (suppressToggle != null && suppressToggle.isOn)
            WriteSetting(SuppressKey, Application.version);

        if (panel != null) Destroy(panel);
        panel = null;
    }

    static TextMeshProUGUI AddLabel(Transform parent, string name, string text, float size, FontStyles style,
        Color colour, TextAlignmentOptions alignment, float height, bool stretch = false)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);

        RectTransform rect = go.GetComponent<RectTransform>();
        if (stretch)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }
        else
        {
            rect.sizeDelta = new Vector2(0f, height);
            LayoutElement layout = go.AddComponent<LayoutElement>();
            layout.minHeight = height;
            layout.preferredHeight = height;
        }

        TextMeshProUGUI label = go.GetComponent<TextMeshProUGUI>();
        label.text = text;
        label.fontSize = size;
        label.fontStyle = style;
        label.color = colour;
        label.alignment = alignment;
        label.raycastTarget = false;
        return label;
    }

    static void AddGap(Transform parent, string name)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        LayoutElement layout = go.GetComponent<LayoutElement>();
        layout.minHeight = 10f;
        layout.preferredHeight = 10f;
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
