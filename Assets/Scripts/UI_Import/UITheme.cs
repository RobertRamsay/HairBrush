using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Central 9-slice skin + colour palette for the HairBrush UI. Loads the sliced sprites via
// Resources.Load, which is the one loading mechanism that works identically in the Editor and
// in an actual Player build - the previous AssetDatabase-based loader only ever worked in the
// Editor (AssetDatabase doesn't exist in a build at all), so none of this skin ever rendered in
// a build until now. The sprites live at Assets/Resources/UI_GFX/ - a build-safe duplicate of
// Assets/UI_GFX/, kept in sync manually since Resources.Load requires that specific folder name.
// Exposes small static helpers that any authority script can call to reskin whatever
// Buttons/Sliders it built, without every builder needing its own copy of this logic. Colours
// below were sampled directly from the target mockup rather than guessed.
public static class UITheme
{
    // --- Palette -----------------------------------------------------------------------
    public static readonly Color PanelDark = new Color(.11f, .13f, .15f, 1f);
    public static readonly Color ButtonNormal = new Color(.10f, .18f, .20f, 1f);
    public static readonly Color ButtonHighlight = new Color(.28f, .58f, .61f, 1f);
    public static readonly Color ButtonPressed = new Color(.36f, .74f, .78f, 1f);
    public static readonly Color ButtonDisabled = new Color(.16f, .18f, .19f, .65f);
    public static readonly Color ButtonMuted = new Color(.32f, .34f, .36f, 1f);
    public static readonly Color TextBright = new Color(.94f, .99f, 1f, 1f);
    public static readonly Color TextMuted = new Color(.62f, .68f, .70f, 1f);
    public static readonly Color TrackDark = new Color(.12f, .14f, .16f, 1f);
    public static readonly Color FillCyan = new Color(.20f, .60f, .68f, 1f);

    private const string ResourcesFolder = "UI_GFX/";

    private static Sprite normalSprite, hoverSprite, clickSprite, fineEdgeSprite, fineGlowSprite, dividerSprite;
    private static bool loaded;
    private static bool warned;

    public static Sprite ButtonNormalSprite => Ensure() ? normalSprite : null;
    public static Sprite ButtonHoverSprite => Ensure() ? hoverSprite : null;
    public static Sprite ButtonClickSprite => Ensure() ? clickSprite : null;
    public static Sprite FineEdgeSprite => Ensure() ? fineEdgeSprite : null;
    public static Sprite FineGlowSprite => Ensure() ? fineGlowSprite : null;
    public static Sprite DividerSprite => Ensure() ? dividerSprite : null;

    private static bool Ensure()
    {
        if (loaded) return normalSprite != null;
        loaded = true;

        normalSprite = Resources.Load<Sprite>(ResourcesFolder + "HB_9sliceSolid");
        hoverSprite = Resources.Load<Sprite>(ResourcesFolder + "HB_9sliceSolidHov");
        clickSprite = Resources.Load<Sprite>(ResourcesFolder + "HB_9sliceSolidClick");
        fineEdgeSprite = Resources.Load<Sprite>(ResourcesFolder + "HB_9slice_FineEdge");
        fineGlowSprite = Resources.Load<Sprite>(ResourcesFolder + "HB_9slice_FineGlow");
        dividerSprite = Resources.Load<Sprite>(ResourcesFolder + "Divider");

        if (normalSprite == null && !warned)
        {
            warned = true;
            Debug.LogWarning("UITheme: could not load sliced sprites from Resources/" + ResourcesFolder +
                " - buttons/sliders will fall back to flat colour styling. Check that Assets/Resources/UI_GFX/ exists with each texture's Sprite Mode set to Single.");
        }
        return normalSprite != null;
    }


    // One-time setup for a Button: sliced sprite skin (with graceful flat-colour fallback if
    // sprites aren't found), consistent size clamp, and a tidied label. Safe to call once per
    // button; call RefreshInteractable each frame after that to keep disabled state in sync.
    public static void StyleButton(Button button, bool primary = true)
    {
        if (button == null) return;

        Image image = button.GetComponent<Image>();
        if (image == null) image = button.gameObject.AddComponent<Image>();
        button.targetGraphic = image;

        if (Ensure())
        {
            image.sprite = ButtonNormalSprite;
            image.type = Image.Type.Sliced;
            image.color = Color.white;

            button.transition = Selectable.Transition.SpriteSwap;
            SpriteState state = button.spriteState;
            state.highlightedSprite = ButtonHoverSprite;
            state.pressedSprite = ButtonClickSprite;
            state.selectedSprite = ButtonHoverSprite;
            state.disabledSprite = ButtonNormalSprite;
            button.spriteState = state;
        }
        else
        {
            image.type = Image.Type.Simple;
            button.transition = Selectable.Transition.ColorTint;
            ColorBlock colors = button.colors;
            colors.normalColor = primary ? ButtonNormal : ButtonMuted;
            colors.highlightedColor = ButtonHighlight;
            colors.pressedColor = ButtonPressed;
            colors.selectedColor = ButtonHighlight;
            colors.disabledColor = ButtonDisabled;
            colors.fadeDuration = .06f;
            button.colors = colors;
        }

        ClampButtonSize(button);
        TidyLabel(button.GetComponentInChildren<TextMeshProUGUI>(true));
    }

    // SpriteSwap transition has no built-in disabled dimming (Unity only auto-dims under
    // ColorTint), so this keeps a visible disabled cue by hand. Cheap - safe to call every poll.
    // THE single style for every seed-reroll button (variance rows, Group UV row, POST UV row).
    // All three builders/fixers call this so there is exactly one definition of how a RANDOMIZE
    // button looks; per-site fallback branches were removed deliberately - one path, one look.
    public static void StyleRerollButton(Button button)
    {
        if (button == null) return;

        Image image = button.GetComponent<Image>();
        if (image == null) image = button.gameObject.AddComponent<Image>();
        image.raycastTarget = true;
        button.targetGraphic = image;

        image.sprite = ButtonNormalSprite;
        image.type = Image.Type.Sliced;
        image.color = new Color(.62f, 1f, .96f, 1f);
        button.transition = Selectable.Transition.SpriteSwap;
        SpriteState state = button.spriteState;
        state.highlightedSprite = ButtonHoverSprite;
        state.pressedSprite = ButtonClickSprite;
        button.spriteState = state;

        TextMeshProUGUI label = button.GetComponentInChildren<TextMeshProUGUI>(true);
        if (label != null)
        {
            label.text = "RANDOMIZE";
            label.fontStyle = FontStyles.Bold;
            label.enableAutoSizing = false;
            label.fontSize = 11f;
            label.alignment = TextAlignmentOptions.Center;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Overflow;
            label.margin = Vector4.zero;
            label.color = TextBright;
            label.raycastTarget = false;

            // Different builders created these labels with different fixed-size child rects
            // (the UV row's was sized for the old single-letter "R"). Normalising the rect to
            // stretch-fill its button here is what makes every button render identically
            // regardless of which script originally built it.
            RectTransform labelRect = label.rectTransform;
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;
            labelRect.anchoredPosition = Vector2.zero;
        }
    }

    public static void RefreshInteractable(Button button)
    {
        if (button == null) return;
        Image image = button.targetGraphic as Image;
        if (image == null) return;
        if (button.transition != Selectable.Transition.SpriteSwap) return;
        image.color = button.interactable ? Color.white : new Color(1f, 1f, 1f, .45f);
    }

    // Keeps auto-generated buttons from ballooning to whatever size their creator guessed.
    public static void ClampButtonSize(Button button)
    {
        RectTransform rect = button.transform as RectTransform;
        if (rect == null) return;

        LayoutElement le = button.GetComponent<LayoutElement>();
        if (le == null) le = button.gameObject.AddComponent<LayoutElement>();

        const float minHeight = 26f;
        const float maxHeight = 32f;
        float current = rect.sizeDelta.y > 0f ? rect.sizeDelta.y : minHeight;
        float h = Mathf.Clamp(current, minHeight, maxHeight);
        le.minHeight = h;
        le.preferredHeight = h;
        rect.sizeDelta = new Vector2(rect.sizeDelta.x, h);
    }

    public static void TidyLabel(TextMeshProUGUI label)
    {
        if (label == null) return;

        // Cap auto-sizing to shrink-only, bounded by whatever the label's own author picked -
        // never grow past their intended size, and never touch textWrappingMode here: some
        // labels (e.g. GroomShapeCurveAuthority's popup buttons) are deliberately built to rely
        // on wrapping to stay readable at a fixed narrow width, and forcing wrap off pushed them
        // straight into the ellipsis fallback below instead of the two-line layout they need.
        float original = label.fontSize;
        label.enableAutoSizing = true;
        label.fontSizeMin = Mathf.Min(9f, original);
        label.fontSizeMax = original;
        label.overflowMode = TextOverflowModes.Ellipsis;
        label.margin = new Vector4(6f, 1f, 6f, 1f);
        if (label.color.a > 0f) label.color = TextBright;
    }

    // Reskins a Slider's Background/Fill/Handle. Leaves min/max/value/listeners alone.
    public static void StyleSlider(Slider slider)
    {
        if (slider == null) return;

        Transform bg = slider.transform.Find("Background");
        if (bg != null)
        {
            Image bgImage = bg.GetComponent<Image>();
            if (bgImage != null) bgImage.color = TrackDark;
        }

        if (slider.fillRect != null)
        {
            Image fillImage = slider.fillRect.GetComponent<Image>();
            if (fillImage != null) fillImage.color = FillCyan;
        }

        if (slider.handleRect != null)
        {
            Image handleImage = slider.handleRect.GetComponent<Image>();
            if (handleImage != null)
            {
                if (Ensure())
                {
                    handleImage.sprite = FineGlowSprite;
                    handleImage.type = Image.Type.Sliced;
                    handleImage.color = Color.white;
                }
                else
                {
                    handleImage.color = TextBright;
                }
            }
        }
    }

    // Thin horizontal rule using the Divider sprite. Idempotent - skips if a divider already
    // sits immediately before the target sibling so repeated polling doesn't stack copies.
    public static void InsertDividerBefore(Transform parent, Transform beforeThis)
    {
        if (parent == null || beforeThis == null || !Ensure()) return;
        int index = beforeThis.GetSiblingIndex();
        if (index > 0 && parent.GetChild(index - 1).name == "ThemeDivider") return;

        GameObject go = new GameObject("ThemeDivider", typeof(RectTransform), typeof(LayoutElement), typeof(Image));
        go.transform.SetParent(parent, false);
        go.transform.SetSiblingIndex(index);

        LayoutElement le = go.GetComponent<LayoutElement>();
        le.minHeight = 10f;
        le.preferredHeight = 10f;
        le.flexibleWidth = 1f;

        Image image = go.GetComponent<Image>();
        image.sprite = DividerSprite;
        image.type = Image.Type.Sliced;
        image.raycastTarget = false;
        image.color = Color.white;
    }
}
