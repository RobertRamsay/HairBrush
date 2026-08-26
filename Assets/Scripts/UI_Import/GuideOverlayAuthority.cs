using TMPro;
using UnityEngine;
using UnityEngine.UI;

// GUIDES ON TOP: draw the selected guide's curve and its influence rings through everything,
// instead of letting the hair and the head hide them.
//
// Off - the default, and what the tool has always done - the curve is depth-tested, so you can
// read where it passes behind the head and the zone rings sit convincingly on the scalp. That is
// the right picture for judging a guide against the surface, and the wrong one the moment the
// group has any density in it: a guide runs through the very hair it is steering, so on a full
// head the curve spends most of its length buried and you are shaping something you cannot see.
//
// The handle points are NOT affected by this and stay on top. That is deliberate and predates the
// toggle - see GuideCurveHandleAuthority, which explains that what you can grab has to be what you
// can see, and that PickHandle has no occlusion test to match. This toggle is about the parts you
// LOOK at; the parts you GRAB were never negotiable.
//
// Keeping that true took one extra step. Switched on, the curve joins the handles on the SAME
// shader, at the same queue, and passes exactly through every handle centre - so the transparent
// queue's distance sort has nothing to separate them and the handles would flicker in and out
// behind the curve. GuideCurvePreviewAuthority.PushBelowHandles pins the curve one queue lower.
//
// Session-and-file scope, same as MAYA-NAV: it is a preference about how you want to see, not a
// property of the groom, so it lives in hairbrush.ini and survives quitting and updating rather
// than being written into a project.
[DefaultExecutionOrder(8960)]
public class GuideOverlayAuthority : MonoBehaviour
{
    // GroupPanelPostHintStats orders the left panel and needs to know this button by name.
    public const string ButtonName = "GuidesOnTopToggleButton";

    public const string SettingsKey = "guidesOnTop";

    private const float ScanInterval = .25f;
    private const float ButtonHeight = 32f;

    // ---- state ------------------------------------------------------------------------
    // Initialised here so nothing downstream has to test for existence.
    private static bool onTop;

    // Read from the ini once, lazily, on the first question anybody asks - not in ResetStatics,
    // for the same reason MayaNavigationAuthority does not: persistentDataPath is real work and
    // SubsystemRegistration is too early to be reaching for the filesystem.
    private static bool loaded;

    // Bumped every time the value changes. GuideCurvePreviewAuthority cannot simply re-read a
    // bool, because acting on it means rebuilding a Material and re-pointing two LineRenderers -
    // work that must happen ONCE per change, not once per frame. A generation counter is how it
    // tells "still off" from "just switched off".
    private static int generation;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        onTop = false;
        loaded = false;
        generation = 0;
    }

    public static bool Enabled
    {
        get
        {
            if (!loaded)
            {
                loaded = true;
                onTop = HairBrushSettings.GetBool(SettingsKey, false);
            }
            return onTop;
        }
    }

    // Starts at 0 and only ever moves in SetEnabled, so the value loaded from the ini does NOT
    // bump it - readers cannot use this to notice the first load. They do not need to:
    // GuideCurvePreviewAuthority starts its copy at -1, so its first frame always rebuilds
    // whatever this says.
    public static int Generation
    {
        get { return generation; }
    }

    public static void SetEnabled(bool value)
    {
        // Compared against Enabled, not against the raw field, and that ordering matters. Reading
        // the field first would let a SetEnabled that arrives before anything has read Enabled
        // latch `loaded` true, match a default-false field against a false argument, and return -
        // leaving the session off while the ini still said on, and flipping back at the next
        // launch. Enabled loads the file before answering.
        if (Enabled == value) return;

        onTop = value;
        generation++;
        HairBrushSettings.SetBool(SettingsKey, value);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Spawn()
    {
        if (FindFirstObjectByType<GuideOverlayAuthority>() != null) return;
        GameObject go = new GameObject(nameof(GuideOverlayAuthority));
        DontDestroyOnLoad(go);
        go.AddComponent<GuideOverlayAuthority>();
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
        // binding has to be re-checked rather than established once. Like MAYA-NAV and unlike
        // SYMMETRY, a model swap does not switch this off: it is about the person's eyes.
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
        // authority for this panel and puts this button under MAYA-NAV every scan.
        Transform above = parent.Find(MayaNavigationAuthority.ButtonName);
        if (above == null) above = parent.Find(GroomSymmetryAuthority.ButtonName);
        if (above != null) go.transform.SetSiblingIndex(Mathf.Clamp(above.GetSiblingIndex() + 1, 0, parent.childCount - 1));

        Repaint();
    }

    private void Repaint()
    {
        // Only write when the value actually changed - a TMP text assignment forces a mesh
        // rebuild of the label whether or not the string differs.
        if (label != null)
        {
            string text = "GUIDES ON TOP: OFF";
            if (Enabled) text = "GUIDES ON TOP: ON";
            if (label.text != text) label.text = text;
        }

        if (image != null)
        {
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
            StatusToast.Show("GUIDES ON TOP - the curve and its rings draw through hair and the head.", false, 4f);
        }
        else
        {
            StatusToast.Show("GUIDES ON TOP off - the curve is hidden where it passes behind something.", false, 4f);
        }

        // Repaint on the very next frame rather than waiting out the scan interval.
        nextScan = 0f;
        Repaint();
    }
}
