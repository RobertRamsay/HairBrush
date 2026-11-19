using System;
using System.IO;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// Autosave for every session, and RECOVER on the start menu.
//
// Before this, the only autosave belonged to the MCP server and only ran while its listener was
// on, so somebody grooming by hand in a built player had no crash recovery at all.
//
// WHEN IT WRITES
//   * Every RollingInterval seconds, but only if the session has really changed since the last
//     autosave (UndoHistoryAuthority.EditGeneration), and only at a quiet moment: no mouse
//     button held, no undo step being replayed, no project restore still settling, no REMAP
//     preview (whose scene is not the groom). A write is a full BuildSaveData, so it must never
//     land in the middle of a stroke.
//   * On quit, and in the editor on leaving Play mode, if anything changed.
//   * Never for an empty session - an autosave of nothing would bury the last real one.
//   Writes go through SafeFileWrite with a .bak, so a crash during the autosave cannot destroy
//   the autosave.
//
// RECOVERY
//   At startup, the autosave the PREVIOUS run left behind is copied aside to LastSessionPath
//   before this run can write anything. RECOVER loads that copy. Without the copy, a user who
//   crashed, relaunched and started grooming by habit would have their crashed session
//   overwritten by the new one two minutes later.
//
//   RECOVER sits in RESUME's slot on the menu. The two can never be on screen together: RESUME
//   shows only when a session exists, RECOVER only when none does - recovering over live work
//   would throw that work away with no question asked.
[DefaultExecutionOrder(9850)]
public class AutosaveAuthority : MonoBehaviour
{
    const float RollingInterval = 120f;
    const float MenuScanInterval = .25f;
    const string RecoverButtonName = "Button_Recover";

    public static string AutosavePath => Path.Combine(Application.persistentDataPath, "autosave.json");
    public static string LastSessionPath => Path.Combine(Application.persistentDataPath, "autosave_last_session.json");

    // The file the MCP server wrote before autosave moved here. Still read once at startup so an
    // existing user's last session is not lost by the upgrade.
    static string LegacyMcpAutosavePath => Path.Combine(Application.persistentDataPath, "mcp_autosave.json");

    static AutosaveAuthority instance;

    int savedGeneration;
    float dirtySince = -1f;
    float nextMenuScan;

    ModelViewer viewer;
    RuntimeNavigationProjectIO io;
    Button recoverButton;
    static FieldInfo loadedModelField;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<AutosaveAuthority>() != null) return;
        GameObject go = new GameObject(nameof(AutosaveAuthority));
        DontDestroyOnLoad(go);
        go.AddComponent<AutosaveAuthority>();
    }

    void Awake()
    {
        instance = this;
        savedGeneration = UndoHistoryAuthority.EditGeneration;
        PreserveLastSession();
    }

    void OnDestroy()
    {
        if (instance == this) instance = null;
    }

    // ------------------------------------------------------------------ the rolling save

    void Update()
    {
        if (UndoHistoryAuthority.EditGeneration != savedGeneration)
        {
            if (dirtySince < 0f) dirtySince = Time.unscaledTime;
            if (Time.unscaledTime - dirtySince >= RollingInterval && IsQuietMoment())
                WriteNow("rolling", force: false);
        }

        if (Time.unscaledTime >= nextMenuScan)
        {
            nextMenuScan = Time.unscaledTime + MenuScanInterval;
            MaintainRecoverButton();
        }
    }

    void OnApplicationQuit()
    {
        WriteNow("quit", force: false);
    }

#if UNITY_EDITOR
    // ExitingPlayMode fires while the play-mode scene is still alive - the last reliable moment
    // to write it before a recompile or a stop wipes it.
    void OnEnable() { UnityEditor.EditorApplication.playModeStateChanged += OnPlayModeChanged; }
    void OnDisable() { UnityEditor.EditorApplication.playModeStateChanged -= OnPlayModeChanged; }

    void OnPlayModeChanged(UnityEditor.PlayModeStateChange change)
    {
        if (change == UnityEditor.PlayModeStateChange.ExitingPlayMode) WriteNow("exiting play mode", force: false);
    }
#endif

    static bool IsQuietMoment()
    {
        Mouse mouse = Mouse.current;
        if (mouse != null && (mouse.leftButton.isPressed || mouse.rightButton.isPressed || mouse.middleButton.isPressed)) return false;
        if (UndoHistoryAuthority.Restoring) return false;
        if (GroomingInputLock.AnyHold) return false;
        if (HairProjectSaveData.PendingModifierRestore != null) return false;
        if (CanonicalProjectStateBridge.PendingCanonicalRestore != null) return false;
        return true;
    }

    // Writes the session to AutosavePath. force = write even if nothing changed since the last
    // autosave (the MCP server's checkpoints). Returns true if a file was written. Never throws:
    // an autosave failing must not take anything else down with it.
    public static bool WriteNow(string reason, bool force)
    {
        AutosaveAuthority self = instance;
        int generation = UndoHistoryAuthority.EditGeneration;
        if (!force && self != null && generation == self.savedGeneration) return false;

        try
        {
            int cards = HairCard.All.Count;
            if (cards == 0) return false;

            RuntimeNavigationProjectIO io = self != null && self.io != null ? self.io : FindFirstObjectByType<RuntimeNavigationProjectIO>();
            if (io == null) return false;
            if (self != null) self.io = io;

            SafeFileWrite.WriteAllText(AutosavePath, JsonUtility.ToJson(io.BuildSaveData(), true), keepBackup: true);
            Debug.Log("HairBrush: autosaved " + cards + " cards (" + reason + ") to " + AutosavePath);

            if (self != null)
            {
                self.savedGeneration = generation;
                self.dirtySince = -1f;
            }
            return true;
        }
        catch (Exception error)
        {
            Debug.LogWarning("HairBrush: autosave failed (" + reason + ") - " + error.Message);

            // Try again a full interval from now rather than every frame.
            if (self != null) self.dirtySince = Time.unscaledTime;
            return false;
        }
    }

    // ------------------------------------------------------------------ recovery

    // Copies whatever autosave the previous run left to LastSessionPath, before this run writes.
    static void PreserveLastSession()
    {
        try
        {
            string source = NewestExisting(AutosavePath, LegacyMcpAutosavePath);
            if (source == null) return;

            // Already preserved: nothing has been autosaved since the copy was taken.
            if (File.Exists(LastSessionPath) && File.GetLastWriteTimeUtc(LastSessionPath) >= File.GetLastWriteTimeUtc(source)) return;

            File.Copy(source, LastSessionPath, true);
        }
        catch (Exception error)
        {
            Debug.LogWarning("HairBrush: could not set aside the last session's autosave - " + error.Message);
        }
    }

    static string NewestExisting(string a, string b)
    {
        bool hasA = File.Exists(a), hasB = File.Exists(b);
        if (hasA && hasB) return File.GetLastWriteTimeUtc(a) >= File.GetLastWriteTimeUtc(b) ? a : b;
        if (hasA) return a;
        if (hasB) return b;
        return null;
    }

    void MaintainRecoverButton()
    {
        if (viewer == null) viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer == null || viewer.uiContainer == null) return;

        bool show = viewer.uiContainer.activeInHierarchy
                    && !WelcomeWhatsNewAuthority.IsOpen
                    && !HasSession()
                    && File.Exists(LastSessionPath);

        if (recoverButton == null)
        {
            if (!show) return;
            recoverButton = CreateRecoverButton();
            if (recoverButton == null) return;
        }

        if (recoverButton.gameObject.activeSelf != show) recoverButton.gameObject.SetActive(show);
    }

    bool HasSession()
    {
        if (HairCard.All.Count > 0) return true;
        if (loadedModelField == null)
            loadedModelField = typeof(ModelViewer).GetField("loadedModel", BindingFlags.Instance | BindingFlags.NonPublic);
        return loadedModelField != null && loadedModelField.GetValue(viewer) as GameObject != null;
    }

    // A copy of the authored LOAD PROJECT button, so it matches the menu exactly, moved into
    // RESUME's slot.
    Button CreateRecoverButton()
    {
        Button template = viewer.loadProjectButton;
        if (template == null) return null;

        Transform existing = template.transform.parent.Find(RecoverButtonName);
        GameObject go = existing != null
            ? existing.gameObject
            : Instantiate(template.gameObject, template.transform.parent, false);
        go.name = RecoverButtonName;

        Button button = go.GetComponent<Button>();

        // A fresh event, not RemoveAllListeners: that only removes runtime listeners, and the
        // copy would keep any persistent LOAD PROJECT binding authored in the scene.
        button.onClick = new Button.ButtonClickedEvent();
        button.onClick.AddListener(Recover);

        foreach (TextMeshProUGUI tmp in go.GetComponentsInChildren<TextMeshProUGUI>(true)) tmp.text = "RECOVER";
        foreach (Text legacy in go.GetComponentsInChildren<Text>(true)) legacy.text = "RECOVER";

        RectTransform rect = go.GetComponent<RectTransform>();
        RectTransform resume = FindResumeSlot(template.transform.parent);
        if (rect != null && resume != null) rect.anchoredPosition = resume.anchoredPosition;
        else if (rect != null) rect.anchoredPosition += new Vector2(0f, -64f);

        return button;
    }

    static RectTransform FindResumeSlot(Transform menu)
    {
        foreach (Transform child in menu)
            if (child.name == "Button_Resume") return child as RectTransform;
        return null;
    }

    void Recover()
    {
        if (!File.Exists(LastSessionPath))
        {
            StatusToast.Show("There is no autosave to recover.", true);
            return;
        }

        if (io == null) io = FindFirstObjectByType<RuntimeNavigationProjectIO>();
        if (io == null)
        {
            StatusToast.Show("RECOVER is not available right now.", true);
            return;
        }

        string when = File.GetLastWriteTime(LastSessionPath).ToString("ddd d MMM, HH:mm");
        io.LoadProjectFromPath(LastSessionPath);

        // LoadProjectFromPath reports its own failures; only say "recovered" if it worked.
        if (HasSession())
            StatusToast.Show("Recovered the session autosaved " + when + ". Use SAVE PROJ to keep it as a project.", false, 6f);
    }
}
