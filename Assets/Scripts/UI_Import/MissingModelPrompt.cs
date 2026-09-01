using System;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// "The head this groom was made on is not where the project says it is."
//
// A project stores the model as an absolute path. Move the OBJ, rename its folder, or open the
// project on another machine and that path is a dead end - and until now the load simply wrote a
// Debug.LogError and carried on, so the hair appeared with no head under it. Nothing on screen
// said why, and placement silently did nothing because there was no surface to place onto.
//
// WHY IT BLOCKS THE LOAD RATHER THAN APPEARING AFTER IT. The scale reconciliation
// (RuntimeNavigationProjectIO.MigrateImportScale) rewrites the SAVE PAYLOAD - every length in the
// project - before a single card is spawned from it, because the importer may normalise this OBJ
// to a different working scale than the one the groom was authored at. Once the cards exist it is
// too late for that. So the project pauses here, with nothing spawned, and the rest of the load
// runs from whichever answer comes back. An empty viewport behind the question is the honest
// picture of where the load has got to.
//
// The dialog is deliberately not dismissable by clicking away, unlike INPUT KEYS. Both answers
// here change what happens next, and "I clicked the backdrop" is not one of them.
public class MissingModelPrompt : MonoBehaviour
{
    private const string CanvasName = "MissingModelPromptCanvas";

    // UIThemeAuthority skips this by name, exactly as it skips the other full-screen backdrops -
    // it is a Button whose Graphic covers the screen, and the theme would repaint it white.
    public const string DimmerName = "MissingModelPromptDimmer";

    private static MissingModelPrompt instance;
    private static GameObject root;

    // Read by anything that must stand down while a question is waiting for an answer.
    public static bool IsOpen
    {
        get { return root != null; }
    }

    private static Action onLocated;
    private static Action onContinue;
    private static string missingPath;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        instance = null;
        root = null;
        onLocated = null;
        onContinue = null;
        missingPath = null;
    }

    // `located` is handed the path the user chose; `skipped` is called if they decide to carry on
    // without a head. Exactly one of them runs, exactly once - the load is waiting on it.
    public static void Show(string modelPath, Action<string> located, Action skipped)
    {
        Close();

        missingPath = modelPath;
        onLocated = null;
        onContinue = null;

        if (instance == null)
        {
            GameObject host = new GameObject(nameof(MissingModelPrompt));
            DontDestroyOnLoad(host);
            instance = host.AddComponent<MissingModelPrompt>();
        }

        onContinue = () =>
        {
            Close();
            if (skipped != null) skipped();
        };

        onLocated = () =>
        {
            string chosen = ChooseModelFile();

            // Cancelled the file browser. The question is still unanswered, so the dialog stays
            // up rather than quietly turning into "continue without" - the load is still paused
            // and the user has not said what they want.
            if (string.IsNullOrEmpty(chosen)) return;

            Close();
            if (located != null) located(chosen);
        };

        instance.Build();
    }

    public static void Close()
    {
        onLocated = null;
        onContinue = null;
        if (root == null) return;

        // Deactivated and renamed with a PREFIX before the Destroy. Destroy is deferred to the
        // end of the frame, so a lookup later in this same frame would otherwise still find it.
        root.SetActive(false);
        root.name = "Discarded_" + CanvasName;
        Destroy(root);
        root = null;
    }

    private static string ChooseModelFile()
    {
#if UNITY_EDITOR
        return UnityEditor.EditorUtility.OpenFilePanel("Locate the head mesh for this project", "", "obj");
#else
        return RuntimeFileDialog.OpenFile(
            "Locate the head mesh for this project",
            "OBJ Models\0*.obj\0All Files\0*.*\0\0",
            "obj");
#endif
    }

    private void Build()
    {
        GameObject canvasObject = new GameObject(CanvasName, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        root = canvasObject;
        DontDestroyOnLoad(canvasObject);

        Canvas canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;

        // Above INPUT KEYS at 4800 and the groom UI below it. This one is a question the load is
        // waiting on, so nothing may cover it.
        canvas.sortingOrder = 4900;

        CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);

        // A blocker with no click handler. It swallows anything aimed at the viewport, and
        // clicking it does NOT dismiss - see the note at the top.
        GameObject dimmer = new GameObject(DimmerName, typeof(RectTransform), typeof(Image), typeof(Button));
        dimmer.transform.SetParent(canvasObject.transform, false);
        RectTransform dimmerRect = dimmer.GetComponent<RectTransform>();
        dimmerRect.anchorMin = Vector2.zero;
        dimmerRect.anchorMax = Vector2.one;
        dimmerRect.offsetMin = Vector2.zero;
        dimmerRect.offsetMax = Vector2.zero;
        dimmer.GetComponent<Image>().color = new Color(0f, 0f, 0f, .72f);
        dimmer.GetComponent<Button>().interactable = false;

        GameObject panel = new GameObject("MissingModelPanel", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup));
        panel.transform.SetParent(canvasObject.transform, false);
        RectTransform panelRect = panel.GetComponent<RectTransform>();
        panelRect.anchorMin = new Vector2(.5f, .5f);
        panelRect.anchorMax = new Vector2(.5f, .5f);
        panelRect.pivot = new Vector2(.5f, .5f);
        panelRect.sizeDelta = new Vector2(820f, 330f);
        panel.GetComponent<Image>().color = new Color(.13f, .15f, .18f, .99f);

        VerticalLayoutGroup layout = panel.GetComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(30, 30, 24, 24);
        layout.spacing = 12f;
        layout.childControlHeight = true;
        layout.childControlWidth = true;
        layout.childForceExpandHeight = false;
        layout.childForceExpandWidth = true;

        AddLabel(panel.transform, "HEAD MESH NOT FOUND", 24f, FontStyles.Bold, new Color(1f, .82f, .38f), 34f);

        AddLabel(panel.transform,
            "This project's hair was groomed onto a head mesh that is no longer where the project "
            + "says it is. The groom itself is intact - it just has nothing to sit on.",
            17f, FontStyles.Normal, new Color(.88f, .90f, .93f), 58f);

        string shown = missingPath;
        if (!string.IsNullOrEmpty(shown)) shown = Path.GetFileName(shown) + "   -   " + shown;
        AddLabel(panel.transform, shown, 14f, FontStyles.Italic, new Color(.62f, .66f, .72f), 44f);

        AddLabel(panel.transform,
            "Point it at the same OBJ and the groom is placed on it at the scale it was authored "
            + "at, wherever the file now lives.",
            15f, FontStyles.Normal, new Color(.72f, .76f, .82f), 44f);

        GameObject row = new GameObject("Buttons", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        row.transform.SetParent(panel.transform, false);
        row.GetComponent<LayoutElement>().preferredHeight = 52f;
        HorizontalLayoutGroup rowLayout = row.GetComponent<HorizontalLayoutGroup>();
        rowLayout.spacing = 14f;
        rowLayout.childControlHeight = true;
        rowLayout.childControlWidth = true;
        rowLayout.childForceExpandHeight = true;
        rowLayout.childForceExpandWidth = true;

        AddButton(row.transform, "LOCATE MODEL", new Color(.22f, .45f, .62f), () => { if (onLocated != null) onLocated(); });
        AddButton(row.transform, "CONTINUE WITHOUT", new Color(.30f, .30f, .32f), () => { if (onContinue != null) onContinue(); });
    }

    private static void AddLabel(Transform parent, string text, float size, FontStyles style, Color colour, float height)
    {
        GameObject go = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        go.GetComponent<LayoutElement>().preferredHeight = height;

        TextMeshProUGUI label = go.GetComponent<TextMeshProUGUI>();
        label.text = text;
        label.fontSize = size;
        label.fontStyle = style;
        label.color = colour;
        label.alignment = TextAlignmentOptions.TopLeft;
        label.raycastTarget = false;
        label.textWrappingMode = TextWrappingModes.Normal;
    }

    private static void AddButton(Transform parent, string text, Color colour, Action click)
    {
        GameObject go = new GameObject(text, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        go.GetComponent<Image>().color = colour;
        go.GetComponent<Button>().onClick.AddListener(() => click());

        GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textGO.transform.SetParent(go.transform, false);
        RectTransform rect = textGO.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        TextMeshProUGUI label = textGO.GetComponent<TextMeshProUGUI>();
        label.text = text;
        label.fontSize = 17f;
        label.fontStyle = FontStyles.Bold;
        label.alignment = TextAlignmentOptions.Center;
        label.color = Color.white;
        label.raycastTarget = false;
    }
}
