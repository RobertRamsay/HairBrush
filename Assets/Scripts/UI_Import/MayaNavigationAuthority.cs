using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// MAYA-NAV: drive the camera the way Maya does.
//
//   ALT + LMB  tumble (orbit)
//   ALT + MMB  track  (pan)
//   ALT + RMB  dolly  (zoom)
//
// Off - which is how every existing groom opens - the camera is exactly what it always was:
// RMB orbits, MMB pans, the wheel zooms, no modifier involved. The wheel keeps working in
// BOTH modes. Maya has a wheel zoom too, and taking it away in exchange for a modifier chord
// would be a loss with nothing bought.
//
// Two responsibilities here, same shape as GroomSymmetryAuthority:
//
//   1. The on/off state - which, unlike SYMMETRY and SOLO, is NOT session-only. It is a
//      preference about how the user's hands work, not a property of the groom, so it lives in
//      hairbrush.ini and survives quitting, updating and buying. See HairBrushSettings for why
//      the file outlives a version bump.
//
//   2. The toggle button in the left panel, under SYMMETRY.
//
// The camera gestures themselves are NOT here. They live in ModelViewer.HandleCameraControls,
// which is where every other camera gesture in this project lives, and it reads Enabled.
//
// ----------------------------------------------------------------------------------------
// Why ALT had to be evacuated first, and what it cost
// ----------------------------------------------------------------------------------------
// ALT was already load-bearing in six places before any of this, and two of them wanted the
// exact chords Maya wants: ALT+LMB picked the group of the hair under the cursor, and
// ALT+click on a guide curve added a point while ALT+right-click removed one. There is no
// arrangement in which ALT+LMB both tumbles the camera and selects a group.
//
// So those two gestures moved to CTRL+SHIFT, and they moved UNCONDITIONALLY - not only while
// MAYA-NAV is on. A binding that depends on a toggle is a binding nobody can build a habit
// around, and it would have made the manual's key reference a table with an if in it.
//
// CTRL alone was not available: CTRL+LMB is POST authoring, which is the single most-used
// authoring gesture in the tool, and CTRL+Z/Y is undo. Adding SHIFT is what keeps clear of it.
//
// Adding SHIFT is not free either. The reasoning for each collision is recorded where it is
// fixed rather than here, but the index has to be complete or the next one gets missed:
//   PlacementBrushModeAuthority    a bare SHIFT press cycles the brush mode
//   ModelViewer.HandleGrooming     a SHIFT hold opens a new-group stroke session
//   PostAffectorManager            CTRL+LMB creates a POST, and would fire on CTRL+SHIFT+LMB too
//   ClumperSelectionExitAuthority  the same click, exiting CLUMPER ahead of it
//   SelectionBrushVisualizer       the CTRL-hover aim ring, which would promise that POST
[DefaultExecutionOrder(8955)]
public class MayaNavigationAuthority : MonoBehaviour
{
    // GroupPanelPostHintStats orders the left panel and needs to know this button by name.
    public const string ButtonName = "MayaNavToggleButton";

    // A plain key, deliberately not version-scoped. See HairBrushSettings.
    public const string SettingsKey = "mayaNavigation";

    private const float ScanInterval = .25f;
    private const float ButtonHeight = 32f;

    // ---- state ------------------------------------------------------------------------
    // Initialised here so nothing downstream has to test for existence.
    private static bool mayaNavOn;

    // The ini is read once, lazily, on the first question anybody asks - NOT in ResetStatics
    // below. Application.persistentDataPath is a property with real work behind it and
    // SubsystemRegistration runs early enough that reaching for the filesystem there is asking
    // for trouble; the first Enabled read happens inside a frame, where it is ordinary.
    private static bool loaded;

    // Statics survive "Enter Play Mode -> Disable Domain Reload". Clearing `loaded` rather
    // than clearing `mayaNavOn` is the point: the next read re-reads the file, so a value the
    // user changed on disk between play sessions is picked up instead of a stale one persisting.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        mayaNavOn = false;
        loaded = false;
    }

    public static bool Enabled
    {
        get
        {
            if (!loaded)
            {
                loaded = true;
                mayaNavOn = HairBrushSettings.GetBool(SettingsKey, false);
            }
            return mayaNavOn;
        }
    }

    // Sets the state AND records it, in that order, so a failed write still leaves the running
    // session behaving the way the user just asked. HairBrushSettings swallows write failures
    // by design - a read-only settings folder should not cost you the toggle you just pressed.
    public static void SetEnabled(bool value)
    {
        loaded = true;
        mayaNavOn = value;
        HairBrushSettings.SetBool(SettingsKey, value);
    }

    // The one question every click-consuming script has to ask: "is ALT down?"
    //
    // It does NOT consult Enabled, and that is the whole design. ALT is reserved for the camera
    // in BOTH modes, so this is one predicate with one answer rather than a scheme-dependent one.
    //
    // Why it has to be unconditional. A camera gesture in MAYA-NAV is a modifier plus a MOUSE
    // BUTTON, and mouse buttons are what the rest of this tool authors with - so with MAYA-NAV on,
    // ALT+LMB tumbling the view would otherwise ALSO paint hair, place an armed POST, move a
    // clumper and grab a guide handle, all on the same press. Nothing here blocks raycasts: the
    // scripts that read Mouse.current directly never ask about raycasts, which is exactly the
    // lesson DemoUpgradePrompt.IsOpen records, so each one has to stand down for itself.
    //
    // With MAYA-NAV OFF the reason is different and easier to get wrong. ALT used to carry the
    // group pick and the guide point editor; both moved to CTRL+SHIFT. If ALT merely stopped
    // meaning anything, every ALT click would fall THROUGH to whatever sat below the branch that
    // used to catch it - planting a card, dragging a guide handle, creating a clumper - so
    // everyone with the old muscle memory would damage the thing they were pointing at. Inert is
    // the only safe answer for a binding that has moved.
    //
    // An earlier cut of this was a single MAYA-NAV-conditional test, and the split it created is
    // what made it wrong: six authorities guarded on it and five reserved ALT outright, so with
    // MAYA-NAV off an ALT+TAB click still created a clumper while the aim ring that promised one
    // stayed hidden. Everything that authors into the VIEWPORT now asks this one question.
    //
    // Tested on ALT being HELD, not on a button being down, so a script stands down for the whole
    // gesture including the press frame that starts it.
    public static bool AltReserved
    {
        get
        {
            if (Keyboard.current == null) return false;
            return Keyboard.current.leftAltKey.isPressed || Keyboard.current.rightAltKey.isPressed;
        }
    }

    // The OTHER question, and the line between the two is worth getting right because collapsing
    // them was a bug in both directions.
    //
    // AltReserved above means "ALT must author nothing". It is unconditional because the gestures
    // that used to live on ALT have MOVED, so an ALT click in the viewport has to be inert in both
    // schemes or it falls through and damages what it is pointing at.
    //
    // This one means "the user is driving the camera RIGHT NOW", and it is conditional because
    // that is only true when MAYA-NAV is on. Use it where the question is about the camera rather
    // than about authoring:
    //
    //   PANEL controls. ALT+click on a slider or a group row never meant anything special, so
    //   there is nothing to reserve - but under MAYA-NAV that press is someone reaching for the
    //   tumble with the cursor a few pixels inside the panel, and it must not reset the slider or
    //   raise the delete-group prompt. Guarding those on AltReserved instead would break
    //   ALT+clicking a panel for everyone who never turns MAYA-NAV on.
    //
    //   MESSAGING. "Release ALT" is correct advice when ALT is a dead key and terrible advice when
    //   ALT is how the user moves the camera.
    //
    //   Anything that CANCELS on a gesture. With MAYA-NAV off, ALT+RMB is the ordinary classic
    //   orbit and has always cancelled an armed placement; only the dolly should be exempt.
    public static bool CameraGestureActive
    {
        get { return Enabled && AltReserved; }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Spawn()
    {
        if (FindFirstObjectByType<MayaNavigationAuthority>() != null) return;
        GameObject go = new GameObject(nameof(MayaNavigationAuthority));
        DontDestroyOnLoad(go);
        go.AddComponent<MayaNavigationAuthority>();
    }

    // ---- the button ---------------------------------------------------------------------

    private GameObject boundPanel;
    private Button button;
    private TextMeshProUGUI label;
    private Image image;
    private float nextScan;

    private void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + ScanInterval;

        // The left panel is destroyed and rebuilt on every model and project load, so the
        // binding has to be re-checked rather than established once.
        //
        // Note what does NOT happen here, and does in GroomSymmetryAuthority: a model swap
        // does not switch this off. SYMMETRY turns itself off with the model because a mirror
        // plane belongs to a particular body. A navigation scheme belongs to the person.
        GameObject panel = GameObject.Find("GroupManagerPanel");
        if (panel == null)
        {
            boundPanel = null;
            button = null;
            label = null;
            image = null;
            return;
        }

        if (boundPanel != panel || button == null) Bind(panel);
        Repaint();
    }

    private void Bind(GameObject panel)
    {
        boundPanel = panel;

        Transform existing = panel.transform.Find(ButtonName);
        if (existing != null)
        {
            button = existing.GetComponent<Button>();
            label = existing.GetComponentInChildren<TextMeshProUGUI>(true);
            image = existing.GetComponent<Image>();
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

        image = go.GetComponent<Image>();
        button = go.GetComponent<Button>();
        button.onClick.AddListener(Toggle);

        GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textGO.transform.SetParent(go.transform, false);
        RectTransform textRect = textGO.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;

        label = textGO.GetComponent<TextMeshProUGUI>();
        label.fontSize = 13f;
        label.fontStyle = FontStyles.Bold;
        label.alignment = TextAlignmentOptions.Center;
        label.color = Color.white;
        label.raycastTarget = false;

        // First guess only. GroupPanelPostHintStats.MaintainPanelOrder is the running order
        // authority for this panel and puts this button under the SYMMETRY toggle every scan.
        Transform symmetry = parent.Find(GroomSymmetryAuthority.ButtonName);
        if (symmetry != null) go.transform.SetSiblingIndex(Mathf.Clamp(symmetry.GetSiblingIndex() + 1, 0, parent.childCount - 1));

        Repaint();
    }

    private void Repaint()
    {
        // Only write when the value actually changed - a TMP text assignment forces a mesh
        // rebuild of the label whether or not the string differs.
        if (label != null)
        {
            string text = "MAYA-NAV: OFF";
            if (Enabled) text = "MAYA-NAV: ON";
            if (label.text != text) label.text = text;
        }

        if (image != null)
        {
            // The same off-grey and on-teal SYMMETRY uses. There is no amber third state here:
            // SYMMETRY needs one because a mirror can be unreliable on an asymmetric model,
            // and a navigation scheme is either the one you picked or it is not.
            Color colour = new Color(.28f, .28f, .28f, 1f);
            if (Enabled) colour = new Color(.20f, .58f, .45f, 1f);
            if (image.color != colour) image.color = colour;
        }
    }

    private void Toggle()
    {
        SetEnabled(!Enabled);

        if (Enabled)
        {
            StatusToast.Show("MAYA-NAV ON - ALT and drag: LEFT tumbles, MIDDLE tracks, RIGHT dollies.", false, 4f);
        }
        else
        {
            StatusToast.Show("MAYA-NAV OFF - RIGHT drag orbits, MIDDLE drag pans, wheel zooms.", false, 4f);
        }

        // Repaint on the very next frame rather than waiting out the scan interval.
        nextScan = 0f;
        Repaint();
    }
}
