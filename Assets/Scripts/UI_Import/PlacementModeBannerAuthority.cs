using System.Reflection;
using TMPro;
using UnityEngine;

// White, bold readout of the active placement mode across the bottom of the viewport,
// with a short line saying what a click actually does in it.
//
// The mode is already shown on the right-hand panel, but that panel scrolls and the mode
// changes from a keystroke, so it is easy to tap SHIFT and not notice what you landed on.
// This sits in the middle of where you are already looking. It draws over the 3D view
// only - never on the menu or the texture editor - and it never takes pointer input.
[DefaultExecutionOrder(9600)]
public class PlacementModeBannerAuthority : MonoBehaviour
{
    private const string ObjectName = "PlacementModeBanner";

    // The build stamp sits at y=10 with a height of 24, so start above that.
    private const float BottomInset = 42f;
    private const float Height = 26f;
    private const float FontSize = 16f;
    private const float ShadowOffset = 1.5f;

    private ModelViewer viewer;
    private PlacementBrushModeAuthority placement;
    private FieldInfo groomingModeField;

    private GameObject bannerObject;
    private TextMeshProUGUI label;
    private TextMeshProUGUI shadow;
    private Canvas boundCanvas;
    private string lastText;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<PlacementModeBannerAuthority>() != null) return;
        GameObject go = new GameObject(nameof(PlacementModeBannerAuthority));
        DontDestroyOnLoad(go);
        go.AddComponent<PlacementModeBannerAuthority>();
    }

    void Awake()
    {
        viewer = null;
        placement = null;
        groomingModeField = null;
        bannerObject = null;
        label = null;
        shadow = null;
        boundCanvas = null;
        lastText = string.Empty;
    }

    void LateUpdate()
    {
        Resolve();

        if (viewer == null || placement == null)
        {
            SetVisible(false);
            return;
        }

        if (!ReadBool(groomingModeField) || GroomViewportSuppressed.Active)
        {
            SetVisible(false);
            return;
        }

        Canvas canvas = ResolveCanvas();
        if (canvas == null)
        {
            SetVisible(false);
            return;
        }

        if (boundCanvas != canvas || label == null) Build(canvas);
        if (label == null) return;

        SetVisible(true);

        PlacementBrushModeAuthority.PlacementMode mode = placement.CurrentMode;
        string text = "BRUSH MODE: " + mode.ToString().ToUpperInvariant() +
                      "   (" + PlacementBrushModeAuthority.DescribeMode(mode) + ")    SHIFT to cycle";

        if (text == lastText) return;
        lastText = text;
        label.text = text;
        if (shadow != null) shadow.text = text;
    }

    void Resolve()
    {
        if (viewer == null)
        {
            viewer = FindFirstObjectByType<ModelViewer>();
            groomingModeField = null;
        }

        if (viewer != null && groomingModeField == null)
        {
            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            groomingModeField = typeof(ModelViewer).GetField("isGroomingMode", flags);
        }

        if (placement == null) placement = FindFirstObjectByType<PlacementBrushModeAuthority>();
    }

    bool ReadBool(FieldInfo field)
    {
        if (field == null) return false;
        if (viewer == null) return false;
        object value = field.GetValue(viewer);
        if (value is bool flag) return flag;
        return false;
    }

    // The grooming slider panel is the anchor for "the groom screen's canvas". Using its
    // root canvas keeps the banner on whichever canvas the tool is actually drawing to,
    // rather than guessing by name.
    Canvas ResolveCanvas()
    {
        if (viewer == null) return null;
        if (viewer.groomingSliderPanelGO == null) return null;

        Canvas canvas = viewer.groomingSliderPanelGO.GetComponentInParent<Canvas>();
        if (canvas == null) return null;
        return canvas.rootCanvas;
    }

    void Build(Canvas canvas)
    {
        boundCanvas = canvas;
        lastText = string.Empty;

        Transform existing = canvas.transform.Find(ObjectName);
        if (existing != null)
        {
            bannerObject = existing.gameObject;
            shadow = FindChildText(existing, "Shadow");
            label = FindChildText(existing, "Label");
            if (label != null) return;
            Destroy(bannerObject);
            bannerObject = null;
        }

        bannerObject = new GameObject(ObjectName, typeof(RectTransform));
        bannerObject.transform.SetParent(canvas.transform, false);

        RectTransform rect = bannerObject.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 0f);
        rect.anchorMax = new Vector2(1f, 0f);
        rect.pivot = new Vector2(.5f, 0f);
        rect.anchoredPosition = new Vector2(0f, BottomInset);
        rect.sizeDelta = new Vector2(-24f, Height);

        // Drawn first so it sits behind: a plain dark copy offset by a pixel keeps the
        // white text readable over a pale model or a bright patch of hair.
        shadow = BuildText(bannerObject.transform, "Shadow", new Color(0f, 0f, 0f, .75f),
            new Vector2(ShadowOffset, -ShadowOffset));
        label = BuildText(bannerObject.transform, "Label", Color.white, Vector2.zero);

        // Sits above the viewport, so it must never eat a placement click.
        bannerObject.transform.SetAsLastSibling();
    }

    static TextMeshProUGUI FindChildText(Transform parent, string childName)
    {
        Transform child = parent.Find(childName);
        if (child == null) return null;
        return child.GetComponent<TextMeshProUGUI>();
    }

    static TextMeshProUGUI BuildText(Transform parent, string childName, Color color, Vector2 offset)
    {
        GameObject go = new GameObject(childName, typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);

        RectTransform rect = go.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.pivot = new Vector2(.5f, .5f);
        rect.offsetMin = new Vector2(offset.x, offset.y);
        rect.offsetMax = new Vector2(offset.x, offset.y);

        TextMeshProUGUI text = go.GetComponent<TextMeshProUGUI>();
        text.fontSize = FontSize;
        text.fontStyle = FontStyles.Bold;
        text.color = color;
        text.alignment = TextAlignmentOptions.Center;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Overflow;
        text.raycastTarget = false;
        return text;
    }

    void SetVisible(bool visible)
    {
        if (bannerObject == null) return;
        if (bannerObject.activeSelf == visible) return;
        bannerObject.SetActive(visible);
    }
}
