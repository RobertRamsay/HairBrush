using TMPro;
using UnityEngine;
using UnityEngine.UI;
#if UNITY_EDITOR
using UnityEditor;
#endif

// One owner for "import a model", and the question that has to be asked when a groom already
// exists.
//
// Two authorities bound viewer.loadButton before this existed, both calling RemoveAllListeners
// first: RuntimeNavigationProjectIO.HookInitialMenuButtons at order 1800, and
// RuntimeBuildLoadAuthority.BindButtons at order 10000. Later order wins the same frame, every
// frame, so ChooseAndLoadModel was always the live handler and
// RuntimeNavigationProjectIO.LoadFreshModel has never run in a build that has both.
//
// That matters, because the teardown lives in the path that never runs. LoadModelAtPath destroys
// the old model and nothing else, so importing a second head TODAY leaves every card exactly
// where it was - a groom hanging in space around a head it was never placed on. There was no
// decision point because there was no decision: the app only had one behaviour, and it was the
// wrong one.
//
// So this claims the button at 10500, above both, and routes:
//
//     pick the file      (nothing destroyed yet - a cancelled picker must cost nothing)
//       |
//     count HairCards
//       |-- none  -> straight through to RuntimeBuildLoadAuthority.LoadModelAtPath
//       '-- some  -> ask: REMAP EXISTING HAIR / START AFRESH / CANCEL
//
// Picking BEFORE any teardown is also the fix for the other half of the old arrangement:
// LoadFreshModel destroyed the cards and the panels and then opened the picker, so cancelling it
// cost the user their groom with no undo - which its own comment acknowledges.
[DefaultExecutionOrder(10500)]
public class ModelImportRouter : MonoBehaviour
{
    private const string PromptCanvasName = "RemapImportPromptCanvas";

    private ModelViewer viewer;
    private RuntimeBuildLoadAuthority buildLoad;
    private RuntimeNavigationProjectIO projectIO;
    private Button boundButton;
    private float nextScan;

    private GameObject promptRoot;
    private string pendingPath = string.Empty;

    // The start screen is what the question is asked over, and it is busy - a logo and four large
    // buttons. A dimmer alone leaves all of it legible behind the panel, so the menu is hidden for
    // as long as the prompt is up and put back exactly as it was when the prompt goes.
    private bool menuWasActiveBeforePrompt;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<ModelImportRouter>() != null) return;
        GameObject go = new GameObject("ModelImportRouter");
        DontDestroyOnLoad(go);
        go.AddComponent<ModelImportRouter>();
    }

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + .25f;

        if (viewer == null) viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer == null) return;
        if (buildLoad == null) buildLoad = FindFirstObjectByType<RuntimeBuildLoadAuthority>();
        if (projectIO == null) projectIO = FindFirstObjectByType<RuntimeNavigationProjectIO>();

        ClaimLoadButton();
    }

    // Reclaimed on every scan rather than once. Both of the other binders re-bind when
    // viewer.loadButton becomes a different instance, and this has to be the last word each time
    // that happens; a one-shot claim would lose the button the first time the menu is rebuilt.
    void ClaimLoadButton()
    {
        if (viewer.loadButton == null) return;
        boundButton = viewer.loadButton;
        boundButton.onClick.RemoveAllListeners();
        boundButton.onClick.AddListener(HandleImportRequest);
    }

    void HandleImportRequest()
    {
        if (promptRoot != null) return;

        string path = ChooseModelFile();
        if (string.IsNullOrEmpty(path)) return;

        if (CountCards() == 0)
        {
            ImportAsNewSession(path);
            return;
        }

        pendingPath = path;
        menuWasActiveBeforePrompt = false;
        if (viewer.uiContainer != null)
        {
            menuWasActiveBeforePrompt = viewer.uiContainer.activeSelf;
            viewer.uiContainer.SetActive(false);
        }
        BuildPrompt();
    }

    static string ChooseModelFile()
    {
#if UNITY_EDITOR
        return EditorUtility.OpenFilePanel("Select OBJ Model", "", "obj");
#else
        return RuntimeFileDialog.OpenFile("Select OBJ Model", "OBJ Models\0*.obj\0All Files\0*.*\0\0", "obj");
#endif
    }

    static int CountCards()
    {
        return FindObjectsByType<HairCard>(FindObjectsSortMode.None).Length;
    }

    // The ordinary import, unchanged: RuntimeBuildLoadAuthority still owns what a fresh head
    // means, including the optional-albedo prompt that follows it.
    void ImportAsNewSession(string path)
    {
        if (buildLoad == null)
        {
            Debug.LogError("HairBrush: no RuntimeBuildLoadAuthority to import through.");
            return;
        }
        buildLoad.LoadModelAtPath(path);
    }

    void ChooseStartAfresh()
    {
        string path = pendingPath;
        DismissPrompt();
        // The teardown LoadModelAtPath never had. Cards and both groom panels go, and they go
        // AFTER the file has been chosen, so there is no longer a window where a cancelled picker
        // has already cost the user their work.
        UndoHistoryAuthority.NotifySessionReplaced();
        if (projectIO != null) projectIO.CleanupEditorUIAndCards();
        ImportAsNewSession(path);
    }

    void ChooseRemap()
    {
        string path = pendingPath;
        DismissPrompt();

        RemapSessionController session = RemapSessionController.Instance;
        if (session == null)
        {
            StatusToast.Show("HairBrush: REMAP is unavailable in this build.", true);
            return;
        }

        // Imported into a root the session owns, and never assigned to ModelViewer.loadedModel.
        // Fifteen authorities poll that field for reference identity and treat a change as "new
        // session, clear my state" - assigning the target there would wipe the groom this whole
        // operation exists to keep.
        GameObject target = CustomOBJImporter.Load(path);
        if (target == null)
        {
            StatusToast.Show("HairBrush could not import that OBJ model.", true);
            return;
        }

        if (!session.Begin(viewer, target))
        {
            Destroy(target);
            StatusToast.Show("HairBrush could not start a REMAP session.", true);
        }
    }

    void ChooseCancel()
    {
        DismissPrompt();
    }

    void BuildPrompt()
    {
        GameObject canvasObject = new GameObject(PromptCanvasName, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        promptRoot = canvasObject;
        Canvas canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        // Above the groom UI, which is on the scene canvas at the default order.
        canvas.sortingOrder = 5000;
        CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);

        // Full-screen blocker. It is what makes this modal: it swallows the click that would
        // otherwise reach the viewport, and it is why the grooming path stays quiet while the
        // question is up - every placement authority already stands down on
        // EventSystem.IsPointerOverGameObject.
        GameObject dimmer = new GameObject("RemapImportPromptDimmer", typeof(RectTransform), typeof(Image));
        dimmer.transform.SetParent(canvasObject.transform, false);
        RectTransform dimmerRect = dimmer.GetComponent<RectTransform>();
        dimmerRect.anchorMin = Vector2.zero;
        dimmerRect.anchorMax = Vector2.one;
        dimmerRect.offsetMin = Vector2.zero;
        dimmerRect.offsetMax = Vector2.zero;
        dimmer.GetComponent<Image>().color = new Color(0f, 0f, 0f, .62f);

        GameObject panel = new GameObject("RemapImportPromptPanel", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup));
        panel.transform.SetParent(canvasObject.transform, false);
        RectTransform panelRect = panel.GetComponent<RectTransform>();
        panelRect.anchorMin = new Vector2(.5f, .5f);
        panelRect.anchorMax = new Vector2(.5f, .5f);
        panelRect.pivot = new Vector2(.5f, .5f);
        panelRect.sizeDelta = new Vector2(560f, 300f);
        panel.GetComponent<Image>().color = new Color(.14f, .16f, .19f, .98f);
        VerticalLayoutGroup layout = panel.GetComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(24, 24, 22, 22);
        layout.spacing = 12f;
        layout.childControlHeight = false;
        layout.childControlWidth = true;
        layout.childForceExpandHeight = false;
        layout.childForceExpandWidth = true;

        AddLabel(panel.transform, "PromptTitle", "This project already has hair on it.", 24, FontStyles.Bold, 40f);
        AddLabel(panel.transform, "PromptBody", "Keep the groom and move it onto the new head, or clear it and start over. Cancelling changes nothing.", 17, FontStyles.Normal, 68f);

        AddButton(panel.transform, "RemapImportPromptRemap", "REMAP EXISTING HAIR", new Color(.20f, .44f, .34f), ChooseRemap);
        AddButton(panel.transform, "RemapImportPromptAfresh", "START AFRESH", new Color(.44f, .26f, .22f), ChooseStartAfresh);
        AddButton(panel.transform, "RemapImportPromptCancel", "CANCEL", new Color(.24f, .30f, .38f), ChooseCancel);
    }

    void DismissPrompt()
    {
        pendingPath = string.Empty;
        // Restored to exactly what it was, on every branch including the two that are about to
        // hide it again themselves. Both of those run in this same frame - LoadModelAtPath hides
        // it, and RemapSessionController.Begin reads its state and then hides it - so nothing is
        // drawn in between and there is no flash. Restoring it here rather than second-guessing
        // which branch wants it is also what keeps Begin's own record honest: cancelling out of a
        // REMAP session has to return the user to the menu they started from.
        if (viewer != null && viewer.uiContainer != null) viewer.uiContainer.SetActive(menuWasActiveBeforePrompt);
        if (promptRoot == null) return;
        Destroy(promptRoot);
        promptRoot = null;
    }

    static void AddLabel(Transform parent, string name, string content, int size, FontStyles style, float height)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        TextMeshProUGUI text = go.GetComponent<TextMeshProUGUI>();
        text.text = content;
        text.fontSize = size;
        text.fontStyle = style;
        text.color = new Color(.92f, .94f, .96f);
        text.alignment = TextAlignmentOptions.TopLeft;
        text.textWrappingMode = TextWrappingModes.Normal;
        go.GetComponent<LayoutElement>().preferredHeight = height;
    }

    // Built the way every other runtime button in the project is - Image and Button on the root,
    // TextMeshProUGUI on a stretched child with raycastTarget off. UIThemeAuthority will restyle
    // these within a quarter second and repaint them white, which is fine here: a modal that
    // matches the rest of the UI is the point, and unlike the texture-editor rect rows this panel
    // is created once and destroyed on the user's answer, so there is no churn for the theme pass
    // to fight with.
    static void AddButton(Transform parent, string name, string label, Color color, UnityEngine.Events.UnityAction onClick)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        go.GetComponent<Image>().color = color;
        go.GetComponent<LayoutElement>().preferredHeight = 38f;

        GameObject textObject = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textObject.transform.SetParent(go.transform, false);
        RectTransform textRect = textObject.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;
        TextMeshProUGUI text = textObject.GetComponent<TextMeshProUGUI>();
        text.text = label;
        text.fontSize = 17;
        text.fontStyle = FontStyles.Bold;
        text.color = Color.white;
        text.alignment = TextAlignmentOptions.Center;
        text.raycastTarget = false;

        go.GetComponent<Button>().onClick.AddListener(onClick);
    }
}
