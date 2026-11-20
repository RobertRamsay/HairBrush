using System;
using System.IO;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// The project the session belongs to, if it has one.
//
// Before this, a session had no file: every SAVE PROJ opened a picker, and nothing knew whether
// the work on screen had been saved. Now a session that came from NEW PROJECT, LOAD PROJECT or
// a successful SAVE PROJ remembers its file, SAVE PROJ writes straight back to it, and the
// reminder below knows when there is unsaved work.
//
// A session with no file (LOAD MODEL, RECOVER) behaves as before: SAVE PROJ asks where to put it,
// and once it has been saved it has a file like any other.
public static class ProjectSession
{
    public static string CurrentPath { get; private set; }
    public static bool HasPath => !string.IsNullOrEmpty(CurrentPath);
    public static string DisplayName => HasPath ? Path.GetFileNameWithoutExtension(CurrentPath) : "Untitled";

    // The undo history's edit counter at the last save. A load settles over several frames and
    // bumps the counter while it does, so the baseline is re-taken until SettleUntil passes.
    static int savedGeneration;
    public static float SettleUntil { get; private set; }
    public static float LastSaveTime { get; private set; }

    public static bool HasUnsavedChanges
    {
        get
        {
            if (Time.unscaledTime < SettleUntil) savedGeneration = UndoHistoryAuthority.EditGeneration;
            return UndoHistoryAuthority.EditGeneration != savedGeneration;
        }
    }

    public static void MarkSaved(string path)
    {
        CurrentPath = path;
        savedGeneration = UndoHistoryAuthority.EditGeneration;
        LastSaveTime = Time.unscaledTime;
    }

    // A project file has just been opened. The autosave copies are not projects: RECOVER brings
    // the work back, but it still has to be saved somewhere real.
    public static void MarkLoaded(string path)
    {
        if (IsAutosaveFile(path))
        {
            CurrentPath = null;
            savedGeneration = int.MinValue;
            LastSaveTime = Time.unscaledTime;
            return;
        }
        MarkSaved(path);
        SettleUntil = Time.unscaledTime + 3f;
    }

    // A new head with no project behind it (LOAD MODEL, MCP load_model).
    public static void Clear()
    {
        CurrentPath = null;
        savedGeneration = UndoHistoryAuthority.EditGeneration;
        LastSaveTime = Time.unscaledTime;
        SettleUntil = Time.unscaledTime + 3f;
    }

    static bool IsAutosaveFile(string path)
    {
        if (string.IsNullOrEmpty(path)) return true;
        try
        {
            string full = Path.GetFullPath(path);
            return string.Equals(full, Path.GetFullPath(AutosaveAuthority.LastSessionPath), StringComparison.OrdinalIgnoreCase)
                || string.Equals(full, Path.GetFullPath(AutosaveAuthority.AutosavePath), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------ the default head

    public const string DefaultHeadFileName = "DefaultHead.obj";
    public static string DefaultHeadFolder => Path.Combine(Application.streamingAssetsPath, "DefaultHead");
    public static string DefaultHeadPath => Path.Combine(DefaultHeadFolder, DefaultHeadFileName);
    public static string DefaultHeadAlbedoPath => Path.Combine(DefaultHeadFolder, "DefaultHead_Albedo.png");

    public static string DefaultProjectFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "HairBrush Projects");

    public static bool IsDefaultHead(string modelPath) =>
        !string.IsNullOrEmpty(modelPath)
        && string.Equals(Path.GetFileName(modelPath), DefaultHeadFileName, StringComparison.OrdinalIgnoreCase);
}

// NEW PROJECT on the start menu, the save reminder, and the default head's skin texture.
[DefaultExecutionOrder(9860)]
public class ProjectSessionAuthority : MonoBehaviour
{
    const float ReminderInterval = 300f;
    const string NewProjectButtonName = "Button_NewProject";

    ModelViewer viewer;
    Button newProjectButton;
    bool menuLaidOut;
    float nextScan;
    float nextReminder;
    GameObject lastModel;
    static FieldInfo loadedModelField;
    static FieldInfo currentModelPathField;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<ProjectSessionAuthority>() != null) return;
        GameObject go = new GameObject(nameof(ProjectSessionAuthority));
        DontDestroyOnLoad(go);
        go.AddComponent<ProjectSessionAuthority>();
    }

    void Awake()
    {
        loadedModelField = typeof(ModelViewer).GetField("loadedModel", BindingFlags.Instance | BindingFlags.NonPublic);
        currentModelPathField = typeof(ModelViewer).GetField("currentModelPath", BindingFlags.Instance | BindingFlags.NonPublic);
        nextReminder = ReminderInterval;
    }

    void Update()
    {
        if (viewer == null) viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer == null) return;

        if (Time.unscaledTime >= nextScan)
        {
            nextScan = Time.unscaledTime + .25f;
            if (!menuLaidOut) LayOutMenu();
            if (newProjectButton != null)
            {
                bool show = viewer.uiContainer != null && viewer.uiContainer.activeInHierarchy && !WelcomeWhatsNewAuthority.IsOpen;
                if (newProjectButton.gameObject.activeSelf != show) newProjectButton.gameObject.SetActive(show);
            }
            SkinDefaultHead();
        }

        UpdateReminder();
    }

    GameObject LoadedModel => loadedModelField != null ? loadedModelField.GetValue(viewer) as GameObject : null;

    // ------------------------------------------------------------------ the menu button

    // NEW PROJECT takes LOAD MODEL's slot at the top; everything else moves down one.
    void LayOutMenu()
    {
        Button loadModel = viewer.loadButton;
        Button loadProject = viewer.loadProjectButton;
        if (loadModel == null || loadProject == null) return;

        RectTransform top = loadModel.GetComponent<RectTransform>();
        RectTransform second = loadProject.GetComponent<RectTransform>();
        if (top == null || second == null) return;
        Vector2 step = second.anchoredPosition - top.anchoredPosition;
        Vector2 topSlot = top.anchoredPosition;

        Transform menu = loadModel.transform.parent;
        foreach (Transform child in menu)
        {
            if (!child.name.StartsWith("Button_", StringComparison.Ordinal)) continue;
            RectTransform rect = child as RectTransform;
            if (rect != null) rect.anchoredPosition += step;
        }

        GameObject go = Instantiate(loadModel.gameObject, menu, false);
        go.name = NewProjectButtonName;
        RectTransform goRect = go.GetComponent<RectTransform>();
        goRect.anchoredPosition = topSlot;

        newProjectButton = go.GetComponent<Button>();
        newProjectButton.onClick = new Button.ButtonClickedEvent();
        newProjectButton.onClick.AddListener(() => NewProjectDialog.Show(CreateProject));
        foreach (TextMeshProUGUI tmp in go.GetComponentsInChildren<TextMeshProUGUI>(true)) tmp.text = "NEW PROJECT";
        foreach (Text legacy in go.GetComponentsInChildren<Text>(true)) legacy.text = "NEW PROJECT";
        if (go.GetComponent<KeepOwnButtonSkin>() == null) go.AddComponent<KeepOwnButtonSkin>();
        UITheme.StylePrimaryButton(newProjectButton);
        foreach (Text legacy in go.GetComponentsInChildren<Text>(true)) legacy.color = UITheme.TextOnGold;

        menuLaidOut = true;
    }

    // Returns null on success, or what went wrong for the dialog to show.
    string CreateProject(string path)
    {
        if (!File.Exists(ProjectSession.DefaultHeadPath))
            return "The default head is missing from this install (" + ProjectSession.DefaultHeadPath + ").";

        RuntimeBuildLoadAuthority build = FindFirstObjectByType<RuntimeBuildLoadAuthority>();
        RuntimeNavigationProjectIO io = FindFirstObjectByType<RuntimeNavigationProjectIO>();
        if (build == null || io == null) return "HairBrush is not ready yet. Try again in a moment.";

        try
        {
            string folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        }
        catch (Exception error)
        {
            return "Could not create the folder: " + error.Message;
        }

        UndoHistoryAuthority.NotifySessionReplaced();
        io.CleanupEditorUIAndCards();
        WelcomeWhatsNewAuthority.DismissIfOpen();
        build.LoadModelAtPath(ProjectSession.DefaultHeadPath, false);
        if (LoadedModel == null) return "The default head could not be loaded.";
        SkinDefaultHead();

        if (!io.SaveProjectTo(path, announce: false))
            return null; // the save has shown its own error; the session is still usable

        StatusToast.Show("New project " + Path.GetFileNameWithoutExtension(path) + " saved to " + path + ". SAVE PROJ now saves straight to it.", false, 6f);
        return null;
    }

    // Projects do not record a head texture, so the default head is given its skin every time it
    // comes in, whether from NEW PROJECT or from opening a project made with it.
    void SkinDefaultHead()
    {
        GameObject model = LoadedModel;
        if (model == lastModel) return;
        lastModel = model;
        if (model == null) return;

        string path = currentModelPathField != null ? currentModelPathField.GetValue(viewer) as string : null;
        if (!ProjectSession.IsDefaultHead(path)) return;
        if (File.Exists(ProjectSession.DefaultHeadAlbedoPath))
            ImportedHeadAppearance.TryApplyAlbedo(model, ProjectSession.DefaultHeadAlbedoPath);
    }

    // ------------------------------------------------------------------ the reminder

    void UpdateReminder()
    {
        bool inSession = LoadedModel != null && viewer.uiContainer != null && !viewer.uiContainer.activeInHierarchy;
        if (!inSession || !ProjectSession.HasUnsavedChanges)
        {
            // The clock only runs while there is something unsaved.
            nextReminder = Time.unscaledTime + ReminderInterval;
            return;
        }

        if (Time.unscaledTime - ProjectSession.LastSaveTime < ReminderInterval) return;
        if (Time.unscaledTime < nextReminder) return;
        if (SaveReminderDialog.IsOpen || NewProjectDialog.IsOpen || !IsQuietMoment()) return;

        SaveReminderDialog.Show(
            () => RuntimeNavigationProjectIO.SaveFromAnyButton(),
            () => nextReminder = Time.unscaledTime + ReminderInterval);
        nextReminder = Time.unscaledTime + ReminderInterval;
    }

    static bool IsQuietMoment()
    {
        Mouse mouse = Mouse.current;
        if (mouse != null && (mouse.leftButton.isPressed || mouse.rightButton.isPressed || mouse.middleButton.isPressed)) return false;
        if (UndoHistoryAuthority.Restoring) return false;
        if (GroomingInputLock.AnyHold) return false;
        if (HairProjectSaveData.PendingModifierRestore != null) return false;
        if (CanonicalProjectStateBridge.PendingCanonicalRestore != null) return false;
        GameObject selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        if (selected != null && selected.GetComponent<TMP_InputField>() != null) return false;
        return true;
    }
}

// Small modal dialogs, built the way MissingModelPrompt builds its own.
static class ProjectDialogKit
{
    public static GameObject BuildCanvas(string name, Vector2 panelSize, out Transform panel)
    {
        GameObject canvasObject = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        UnityEngine.Object.DontDestroyOnLoad(canvasObject);
        Canvas canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 4850;
        CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 1f;

        GameObject dimmer = new GameObject("Dimmer", typeof(RectTransform), typeof(Image));
        dimmer.transform.SetParent(canvasObject.transform, false);
        RectTransform dimmerRect = dimmer.GetComponent<RectTransform>();
        dimmerRect.anchorMin = Vector2.zero;
        dimmerRect.anchorMax = Vector2.one;
        dimmerRect.offsetMin = Vector2.zero;
        dimmerRect.offsetMax = Vector2.zero;
        dimmer.GetComponent<Image>().color = new Color(0f, 0f, 0f, .6f);

        GameObject p = new GameObject("Panel", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup));
        p.transform.SetParent(canvasObject.transform, false);
        RectTransform rect = p.GetComponent<RectTransform>();
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f);
        rect.sizeDelta = panelSize;
        UITheme.ApplyPanelSkin(p.GetComponent<Image>(), Color.white);
        VerticalLayoutGroup layout = p.GetComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(30, 30, 22, 24);
        layout.spacing = 12f;
        layout.childControlHeight = true;
        layout.childControlWidth = true;
        layout.childForceExpandHeight = false;
        layout.childForceExpandWidth = true;
        panel = p.transform;
        return canvasObject;
    }

    public static TextMeshProUGUI Label(Transform parent, string text, float size, FontStyles style, Color colour, float height)
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
        return label;
    }

    public static Transform Row(Transform parent, float height)
    {
        GameObject row = new GameObject("Row", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        row.transform.SetParent(parent, false);
        row.GetComponent<LayoutElement>().preferredHeight = height;
        HorizontalLayoutGroup layout = row.GetComponent<HorizontalLayoutGroup>();
        layout.spacing = 14f;
        layout.childControlHeight = true;
        layout.childControlWidth = true;
        layout.childForceExpandHeight = true;
        layout.childForceExpandWidth = true;
        return row.transform;
    }

    public static Button Button(Transform parent, string text, bool primary, Action click)
    {
        GameObject go = new GameObject(text, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        Button button = go.GetComponent<Button>();
        button.onClick.AddListener(() => click());

        GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textGO.transform.SetParent(go.transform, false);
        RectTransform rect = textGO.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        TextMeshProUGUI label = textGO.GetComponent<TextMeshProUGUI>();
        label.text = text;
        label.fontSize = 18f;
        label.fontStyle = FontStyles.Bold;
        label.alignment = TextAlignmentOptions.Center;
        label.raycastTarget = false;

        go.AddComponent<KeepOwnButtonSkin>();
        if (primary) UITheme.StylePrimaryButton(button);
        else UITheme.StyleButton(button, true);
        label.color = primary ? UITheme.TextOnGold : UITheme.TextBright;
        return button;
    }

    public static TMP_InputField Input(Transform parent, string value, float height)
    {
        GameObject go = TMP_DefaultControls.CreateInputField(new TMP_DefaultControls.Resources());
        go.name = "NameField";
        go.transform.SetParent(parent, false);
        LayoutElement le = go.AddComponent<LayoutElement>();
        le.preferredHeight = height;
        go.GetComponent<Image>().color = UITheme.TrackDark;

        TMP_InputField field = go.GetComponent<TMP_InputField>();
        field.lineType = TMP_InputField.LineType.SingleLine;
        field.characterLimit = 80;
        field.textComponent.fontSize = 20f;
        field.textComponent.color = UITheme.TextBright;
        field.textComponent.alignment = TextAlignmentOptions.MidlineLeft;
        if (field.placeholder is TextMeshProUGUI placeholder)
        {
            placeholder.text = "Project name";
            placeholder.fontSize = 20f;
            placeholder.alignment = TextAlignmentOptions.MidlineLeft;
        }
        field.caretColor = UITheme.Gold;
        field.selectionColor = new Color(UITheme.Gold.r, UITheme.Gold.g, UITheme.Gold.b, .35f);
        field.text = value;
        return field;
    }
}

// NEW PROJECT: name it, pick where it lives, and it is saved there before any grooming starts.
public class NewProjectDialog : MonoBehaviour
{
    static NewProjectDialog open;
    public static bool IsOpen => open != null;

    Func<string, string> create;
    TMP_InputField nameField;
    TextMeshProUGUI whereLabel;
    TextMeshProUGUI errorLabel;
    string folder;

    public static void Show(Func<string, string> create)
    {
        if (open != null) return;
        GameObject canvas = ProjectDialogKit.BuildCanvas("NewProjectDialog", new Vector2(760f, 330f), out Transform panel);
        NewProjectDialog dialog = canvas.AddComponent<NewProjectDialog>();
        dialog.create = create;
        dialog.folder = ProjectSession.DefaultProjectFolder;
        dialog.Build(panel);
        open = dialog;
    }

    void Build(Transform panel)
    {
        TextMeshProUGUI title = ProjectDialogKit.Label(panel, "NEW PROJECT", 26f, FontStyles.Bold, UITheme.GoldBright, 36f);
        UITheme.StyleGoldTitle(title);
        ProjectDialogKit.Label(panel, "Starts on the default head and is saved straight away, so SAVE PROJ, autosave and the save reminder all have a file from the outset.",
            16f, FontStyles.Normal, UITheme.TextMuted, 44f);

        nameField = ProjectDialogKit.Input(panel, UniqueName("My Hairstyle"), 46f);
        nameField.onValueChanged.AddListener(_ => Refresh());
        nameField.onSubmit.AddListener(_ => Create());

        whereLabel = ProjectDialogKit.Label(panel, "", 14f, FontStyles.Italic, UITheme.TextMuted, 22f);
        errorLabel = ProjectDialogKit.Label(panel, "", 15f, FontStyles.Bold, UITheme.ToggleWarnTint, 22f);

        Transform row = ProjectDialogKit.Row(panel, 50f);
        ProjectDialogKit.Button(row, "CREATE", true, Create);
        ProjectDialogKit.Button(row, "CHANGE FOLDER...", false, ChooseLocation);
        ProjectDialogKit.Button(row, "CANCEL", false, Close);

        Refresh();
        nameField.Select();
        nameField.ActivateInputField();
    }

    void Update()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame) Close();
    }

    string UniqueName(string baseName)
    {
        string name = baseName;
        for (int i = 2; File.Exists(PathFor(name)) && i < 1000; i++) name = baseName + " " + i;
        return name;
    }

    string CleanName()
    {
        string name = (nameField.text ?? "").Trim();
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c.ToString(), "");
        return name.Trim().TrimEnd('.');
    }

    string PathFor(string name) => Path.Combine(folder, name + ".json");

    void Refresh()
    {
        string name = CleanName();
        whereLabel.text = string.IsNullOrEmpty(name) ? folder : PathFor(name);
        errorLabel.text = !string.IsNullOrEmpty(name) && File.Exists(PathFor(name))
            ? "A project with that name already exists here. Choose another name."
            : "";
    }

    void ChooseLocation()
    {
        string name = CleanName();
        string path;
#if UNITY_EDITOR
        path = UnityEditor.EditorUtility.SaveFilePanel("New Hair Project", folder, string.IsNullOrEmpty(name) ? "HairProject" : name, "json");
#else
        path = RuntimeFileDialog.SaveFile("New Hair Project", "HairBrush Projects\0*.json\0All Files\0*.*\0\0", string.IsNullOrEmpty(name) ? "HairProject" : name, "json");
#endif
        if (string.IsNullOrEmpty(path)) return;
        folder = Path.GetDirectoryName(path);
        nameField.text = Path.GetFileNameWithoutExtension(path);
        Refresh();
    }

    void Create()
    {
        string name = CleanName();
        if (string.IsNullOrEmpty(name)) { errorLabel.text = "Give the project a name."; return; }
        string path = PathFor(name);
        if (File.Exists(path)) { errorLabel.text = "A project with that name already exists here. Choose another name."; return; }

        Close();
        string error = create(path);
        if (!string.IsNullOrEmpty(error)) StatusToast.Show(error, true, 7f);
    }

    void Close()
    {
        if (open == this) open = null;
        gameObject.SetActive(false);
        Destroy(gameObject);
    }
}

// Every five minutes of unsaved work: save now, or later.
public class SaveReminderDialog : MonoBehaviour
{
    static SaveReminderDialog open;
    public static bool IsOpen => open != null;

    Action save;
    Action later;

    public static void Show(Action save, Action later)
    {
        if (open != null) return;
        GameObject canvas = ProjectDialogKit.BuildCanvas("SaveReminderDialog", new Vector2(640f, 230f), out Transform panel);
        SaveReminderDialog dialog = canvas.AddComponent<SaveReminderDialog>();
        dialog.save = save;
        dialog.later = later;

        TextMeshProUGUI title = ProjectDialogKit.Label(panel, "SAVE YOUR WORK?", 24f, FontStyles.Bold, UITheme.GoldBright, 34f);
        UITheme.StyleGoldTitle(title);
        int minutes = Mathf.Max(5, Mathf.RoundToInt((Time.unscaledTime - ProjectSession.LastSaveTime) / 60f));
        string body = ProjectSession.HasPath
            ? ProjectSession.DisplayName + " has unsaved changes from the last " + minutes + " minutes."
            : "This session has never been saved as a project. Choose where to keep it.";
        ProjectDialogKit.Label(panel, body, 17f, FontStyles.Normal, UITheme.TextBright, 50f);

        Transform row = ProjectDialogKit.Row(panel, 50f);
        ProjectDialogKit.Button(row, ProjectSession.HasPath ? "SAVE" : "SAVE AS...", true, () => { dialog.Close(); save(); });
        ProjectDialogKit.Button(row, "LATER", false, () => { dialog.Close(); later(); });
        open = dialog;
    }

    void Update()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null) return;
        if (keyboard.escapeKey.wasPressedThisFrame) { Close(); later(); }
        else if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame) { Close(); save(); }
    }

    void Close()
    {
        if (open == this) open = null;
        gameObject.SetActive(false);
        Destroy(gameObject);
    }
}

// Marks a button that styles itself, so UIThemeAuthority's shared pass leaves it alone.
public class KeepOwnButtonSkin : MonoBehaviour {}
