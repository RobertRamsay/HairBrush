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
    //
    // Brushed steel and gold, sampled from HairBrush_Logo.png: the wordmark's chrome for the
    // steel, the brush's gold for accents (pressed, primary action, brand title, a modifier
    // owning the panel). The sprites carry the real shading; these flat colours are the
    // fallback when the sprites are missing, and the tints the code applies over them.
    public static readonly Color PanelDark = new Color(.14f, .16f, .19f, 1f);
    public static readonly Color ButtonNormal = new Color(.27f, .33f, .40f, 1f);
    public static readonly Color ButtonHighlight = new Color(.40f, .48f, .57f, 1f);
    public static readonly Color ButtonPressed = new Color(.88f, .69f, .22f, 1f);
    public static readonly Color ButtonDisabled = new Color(.16f, .18f, .19f, .65f);
    public static readonly Color ButtonMuted = new Color(.32f, .34f, .36f, 1f);
    public static readonly Color TextBright = new Color(.94f, .97f, 1f, 1f);
    public static readonly Color TextMuted = new Color(.62f, .67f, .72f, 1f);
    public static readonly Color TrackDark = new Color(.12f, .14f, .16f, 1f);
    // The wordmark's steel blue (sampled 101,133,162 / 131,164,199). Still named for its old
    // colour so the code that reads it keeps working; it is steel now, not cyan.
    public static readonly Color FillCyan = new Color(.45f, .58f, .72f, 1f);

    public static readonly Color GoldBright = new Color(1f, .89f, .50f, 1f);
    public static readonly Color Gold = new Color(.94f, .76f, .26f, 1f);
    public static readonly Color GoldDeep = new Color(.72f, .50f, .10f, 1f);
    public static readonly Color TextOnGold = new Color(.12f, .08f, .02f, 1f);

    // Panels wear HB_PanelSteel, which has its colour baked in, so their Image tint is white;
    // the alpha keeps the slight see-through the old flat grey had.
    public static readonly Color PanelTint = new Color(1f, 1f, 1f, .94f);
    // A panel taken over by a modifier (POST): the gold, multiplied over the steel.
    public static readonly Color PanelModifierTint = new Color(1f, .80f, .42f, .96f);
    // The panel sprite's colour just under its top sheen, for anything that has to paint a flat
    // patch matching the panel (GroomingPanelFrozenHeaderAuthority's backdrop).
    public static readonly Color PanelBody = new Color(36f / 255f, 41f / 255f, 48f / 255f, 1f);

    private const string ResourcesFolder = "UI_GFX/";

    private static Sprite normalSprite, hoverSprite, clickSprite, fineEdgeSprite, fineGlowSprite, dividerSprite;
    private static Sprite goldSprite, goldHoverSprite, panelSprite;
    private static bool loaded;
    private static bool warned;

    public static Sprite ButtonNormalSprite => Ensure() ? normalSprite : null;
    public static Sprite ButtonHoverSprite => Ensure() ? hoverSprite : null;
    public static Sprite ButtonClickSprite => Ensure() ? clickSprite : null;
    public static Sprite FineEdgeSprite => Ensure() ? fineEdgeSprite : null;
    public static Sprite FineGlowSprite => Ensure() ? fineGlowSprite : null;
    public static Sprite DividerSprite => Ensure() ? dividerSprite : null;
    public static Sprite GoldSprite => Ensure() ? goldSprite : null;
    public static Sprite GoldHoverSprite => Ensure() ? goldHoverSprite : null;
    public static Sprite PanelSprite => Ensure() ? panelSprite : null;

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
        goldSprite = Resources.Load<Sprite>(ResourcesFolder + "HB_9sliceGold");
        goldHoverSprite = Resources.Load<Sprite>(ResourcesFolder + "HB_9sliceGoldHov");
        panelSprite = Resources.Load<Sprite>(ResourcesFolder + "HB_PanelSteel");

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
        if (image == null)
        {
            // A GameObject can only hold one Graphic. Buttons that already carry a
            // different one - a legacy Text, for instance, as used by Unity's own
            // runtime widgets - cannot be given an Image, and AddComponent returns
            // null there. Leave those alone instead of logging an error and then
            // dereferencing null on every single frame.
            if (button.GetComponent<Graphic>() != null) return;
            image = button.gameObject.AddComponent<Image>();
            if (image == null) return;
        }
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

    // --- Toggles ---------------------------------------------------------------------------
    //
    // Tints over the steel button sprite. They used to be flat fills (grey .28 off, teal on),
    // which multiplied over the steel turned every OFF button near-black. Now: OFF is the plain
    // steel, ON is the logo's gold, a two-way mode's second state is a cool steel, and a state
    // the code has forced (e.g. under DIAMOND) is dimmed.
    public static readonly Color ToggleOffTint = Color.white;
    public static readonly Color ToggleOnTint = new Color(1f, .78f, .32f, 1f);
    public static readonly Color ToggleAltTint = new Color(.72f, .86f, 1f, 1f);
    public static readonly Color ToggleWarnTint = new Color(1f, .55f, .25f, 1f);
    public static readonly Color ToggleForcedTint = new Color(.50f, .52f, .56f, 1f);

    // Group list rows are flat fills (no sprite): dark steel, and gold for the selected group -
    // selection is the same accent as an ON toggle.
    public static readonly Color GroupRowColour = new Color(.19f, .22f, .26f, 1f);
    public static readonly Color GroupRowSelectedColour = new Color(.42f, .33f, .12f, 1f);

    public enum ToggleLook { Off, On, Alt, Warning }

    // For the full-width toggles (SYMMETRY, GUIDES ON TOP, MAYA-NAV, CARD, TOPOLOGY): ON swaps
    // to the gold sprite with a dark label rather than tinting, so it reads as lit, not stained.
    // Writes only what changed - callers repaint on a poll.
    public static void PaintToggle(Image image, TextMeshProUGUI label, ToggleLook look)
    {
        if (image == null) return;
        bool gold = (look == ToggleLook.On || look == ToggleLook.Warning) && GoldSprite != null;

        Sprite sprite = gold ? GoldSprite : ButtonNormalSprite;
        Color colour = look == ToggleLook.Alt ? ToggleAltTint
                     : look == ToggleLook.Warning ? (gold ? new Color(1f, .80f, .62f, 1f) : ToggleWarnTint)
                     : look == ToggleLook.On && !gold ? ToggleOnTint
                     : Color.white;

        if (sprite != null && image.sprite != sprite)
        {
            image.sprite = sprite;
            image.type = Image.Type.Sliced;
            Button button = image.GetComponent<Button>();
            if (button != null && button.transition == Selectable.Transition.SpriteSwap)
            {
                SpriteState state = button.spriteState;
                state.highlightedSprite = gold ? (GoldHoverSprite != null ? GoldHoverSprite : GoldSprite) : ButtonHoverSprite;
                state.selectedSprite = state.highlightedSprite;
                state.disabledSprite = sprite;
                button.spriteState = state;
            }
        }
        if (image.color != colour) image.color = colour;

        if (label != null)
        {
            Color text = gold ? TextOnGold : TextBright;
            if (label.color != text) label.color = text;
            // Dark type on gold needs the weight to read; white on steel already has it.
            if (gold && (label.fontStyle & FontStyles.Bold) == 0) label.fontStyle |= FontStyles.Bold;
        }
    }

    // The primary action of a screen (START GROOMING): gold at rest, brighter gold on hover, dark
    // label. Skin only - unlike StyleButton it never touches the button's size, for callers that
    // lay their button out themselves.
    public static void StylePrimaryButton(Button button)
    {
        if (button == null) return;
        Image image = button.GetComponent<Image>();
        if (image == null) return;
        button.targetGraphic = image;

        if (GoldSprite == null)
        {
            image.sprite = null;
            image.type = Image.Type.Simple;
            image.color = Gold;
            button.transition = Selectable.Transition.ColorTint;
        }
        else
        {
            image.sprite = GoldSprite;
            image.type = Image.Type.Sliced;
            image.color = Color.white;
            button.transition = Selectable.Transition.SpriteSwap;
            SpriteState state = button.spriteState;
            state.highlightedSprite = GoldHoverSprite != null ? GoldHoverSprite : GoldSprite;
            state.pressedSprite = ButtonClickSprite;
            state.selectedSprite = state.highlightedSprite;
            state.disabledSprite = GoldSprite;
            button.spriteState = state;
        }

        foreach (TextMeshProUGUI label in button.GetComponentsInChildren<TextMeshProUGUI>(true))
            label.color = TextOnGold;
    }

    // The steel panel skin. tint is the panel's state colour (PanelTint normally,
    // PanelModifierTint for a modifier); it multiplies the sprite, so pass white-ish values.
    public static void ApplyPanelSkin(Image image, Color tint)
    {
        if (image == null) return;
        if (PanelSprite == null)
        {
            // No sprites: a flat colour close to the old panels.
            image.color = new Color(PanelBody.r * tint.r, PanelBody.g * tint.g, PanelBody.b * tint.b, tint.a);
            return;
        }
        image.sprite = PanelSprite;
        image.type = Image.Type.Sliced;
        image.color = tint;
    }

    public static void ApplyPanelSkin(Image image) => ApplyPanelSkin(image, PanelTint);

    // A brand title in the logo's gold, shaded top to bottom like the brush.
    public static void StyleGoldTitle(TextMeshProUGUI label)
    {
        if (label == null) return;
        label.color = Color.white;
        label.enableVertexGradient = true;
        label.colorGradient = new VertexGradient(GoldBright, GoldBright, GoldDeep, GoldDeep);
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
        image.color = Color.white;
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
            // Shrink-to-fit, capped at the old fixed 11pt: these buttons are short strips, and
            // with the rounded skin's wider slice border the fixed size spilled over the edges.
            label.enableAutoSizing = true;
            label.fontSizeMin = 6f;
            label.fontSizeMax = 10f;
            label.fontSize = 10f;
            label.alignment = TextAlignmentOptions.Center;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Overflow;
            label.margin = new Vector4(3f, 0f, 3f, 0f);
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

    private const float LargeLabelThreshold = 12f;
    private const float LargeLabelScale = 1.12f;

    public static void TidyLabel(TextMeshProUGUI label)
    {
        if (label == null) return;

        // Cap auto-sizing to shrink-only, bounded by whatever the label's own author picked -
        // never grow past their intended size, and never touch textWrappingMode here: some
        // labels (e.g. GroomShapeCurveAuthority's popup buttons) are deliberately built to rely
        // on wrapping to stay readable at a fixed narrow width, and forcing wrap off pushed them
        // straight into the ellipsis fallback below instead of the two-line layout they need.
        float original = label.fontSize;

        // The full-width buttons (SYMMETRY, SAVE PROJ, PLACEMENT, MENU... all 13-15pt) read a
        // size up; the small row buttons (9-11pt) keep theirs, they are sized to their slots.
        // Auto-sizing below still shrinks a label that would not fit, so this cannot overflow.
        if (original >= LargeLabelThreshold)
            original = Mathf.Round(original * LargeLabelScale * 2f) / 2f;
        label.fontSize = original;

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
