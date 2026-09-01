using System.Collections;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Final layout/alignment pass for Texture mode. TextureUVRectWorkspace deliberately owns the
// orthographic camera and preview scale, so this runs just after it and centres the completed
// atlas in the actually usable screen area between the two side panels. It also keeps the UV
// rectangle world visuals locked to that final plane transform.
[DefaultExecutionOrder(9300)]
public class TextureWorkspacePolishFix : MonoBehaviour
{
    private const float MaterialPanelWidth = 300f;
    private const float SliderWidth = 216f;
    private const float SliderValueWidth = 44f;
    private const float SliderHeight = 18f;
    // THE TWO KNOBS FOR THE SMOOTHNESS / METALLIC PAIR.
    //
    // SliderRowHeight is the whole height of one of those rows - label on top, slider under it -
    // so it is the gap BETWEEN them: whatever is left below the slider is empty space. 48 puts
    // the two close together; raise it to push them apart.
    //
    // SmoothnessTopGap is added to the first of the pair only, and moves that row's label, slider
    // and value down together. It is the space between the master colour block and SMOOTHNESS.
    private const float SliderRowHeight = 48f;
    private const float SmoothnessTopGap = 12f;

    // How far the slider sits below the top of its row, which is what leaves room for the label.
    private const float SliderLabelToControl = 30f;

    // MASTER COLOUR. One line per channel rather than the two-line block Smoothness and Metallic
    // get: those need a full-width name above the control, but R/G/B under a heading do not, and
    // three two-line blocks would take 162px of a 300px panel to say one colour.
    private const float TintHeaderHeight = 26f;
    private const float TintRowHeight = 24f;
    private const float TintChannelLabelWidth = 16f;
    private const float TintSliderWidth = 196f;
    private const float TintSliderHeight = 16f;
    private const float TintSwatchWidth = 44f;

    // The texture rows' right-hand controls. The column holds LOAD and LOCATE stacked, 24 each
    // with a 2px gap; CLR stands beside it at half their width and the full height of both.
    private const float ContentWidth = 280f;
    // Referenced, not repeated: MaterialEditorManager builds the row against this same number,
    // and two constants that must agree are one refactor from disagreeing.
    private const float TextureRowHeight = MaterialEditorManager.TextureRowHeight;
    private const float TextureLabelY = 2f;
    private const float TextureButtonY = 22f;
    private const float TextureButtonHeight = 24f;
    private const float TextureButtonGap = 4f;
    private const float TextureFileY = 50f;

    private GameObject texturePanel;
    private GameObject materialPanel;
    private GameObject previewPlane;
    private TextureUVRectWorkspace uvWorkspace;

    private FieldInfo rectanglesField;
    private FieldInfo draftLineField;
    private MethodInfo updateRectangleVisualMethod;
    private MethodInfo updateOutlineVisualMethod;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<TextureWorkspacePolishFix>() != null) return;
        GameObject go = new GameObject("TextureWorkspacePolishFix");
        DontDestroyOnLoad(go);
        go.AddComponent<TextureWorkspacePolishFix>();
    }

    void LateUpdate()
    {
        Resolve();
        if (texturePanel == null || !texturePanel.activeInHierarchy) return;

        PolishMaterialPanel();

        if (previewPlane == null || Camera.main == null) return;
        Vector3 moveDelta = CentrePreviewBetweenPanels();
        RefreshUVVisuals(moveDelta);
    }

    void Resolve()
    {
        if (texturePanel == null)
            texturePanel = FindInactive("TextureEditorPanel");
        if (materialPanel == null)
            materialPanel = FindInactive("TextureMaterialPanel");
        if (previewPlane == null)
            previewPlane = FindInactive("HairTexturePreviewPlane");

        if (uvWorkspace == null)
        {
            uvWorkspace = FindFirstObjectByType<TextureUVRectWorkspace>();
            if (uvWorkspace != null)
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                System.Type type = typeof(TextureUVRectWorkspace);
                rectanglesField = type.GetField("rectangles", flags);
                draftLineField = type.GetField("draftLine", flags);
                updateRectangleVisualMethod = type.GetMethod("CreateOrUpdateRectangleVisual", flags);
                updateOutlineVisualMethod = type.GetMethod("UpdateOutlineVisual", flags);
            }
        }
    }

    void PolishMaterialPanel()
    {
        if (materialPanel == null || !materialPanel.activeInHierarchy) return;

        RectTransform panelRect = materialPanel.GetComponent<RectTransform>();
        if (panelRect != null)
            panelRect.sizeDelta = new Vector2(MaterialPanelWidth, panelRect.sizeDelta.y);

        Transform properties = materialPanel.transform.Find("Properties");
        if (properties == null) return;

        foreach (Transform row in properties)
        {
            if (row == null) continue;

            // MASTER COLOUR first. These two branches were lost once already, to a rewrite of the
            // texture-row block below that spliced them out with it - and the symptom is not
            // subtle: without them the tint rows fall back to their own layout group, which in
            // this column gives the sliders no height at all and pushes the values off the panel.
            // They are keyed on the row NAME rather than on a child, so nothing about the rows
            // below can shadow them.
            if (row.name == "MasterColourRow")
            {
                LayoutHeaderRow(row);
                continue;
            }

            if (row.name.EndsWith("TintRow", System.StringComparison.Ordinal))
            {
                LayoutTintChannelRow(row);
                continue;
            }

            // A texture slot, in three lines:
            //
            //     Albedo
            //     [LOAD] [FIND] [CLEAR]
            //     FILE: HSD_NiceHairsExport_Color
            //
            // Placed by hand like everything else in this panel. The buttons were previously a
            // stacked column with the filename beside them, which left the name sharing a line
            // with two buttons and overlapping the row underneath.
            Transform load = row.Find("LOADButton");
            if (load != null)
            {
                DisableRowLayout(row);
                SetRowHeight(row, TextureRowHeight);

                PlaceTop(row.Find("Label"), 0f, TextureLabelY, ContentWidth, 18f);

                float buttonSpan = (ContentWidth - TextureButtonGap * 2f) / 3f;
                PlaceTop(load, 0f, TextureButtonY, buttonSpan, TextureButtonHeight);
                PlaceTop(row.Find("FINDButton"), buttonSpan + TextureButtonGap, TextureButtonY, buttonSpan, TextureButtonHeight);
                PlaceTop(row.Find("CLEARButton"), (buttonSpan + TextureButtonGap) * 2f, TextureButtonY, buttonSpan, TextureButtonHeight);

                Transform file = row.Find("File");
                PlaceTop(file, 0f, TextureFileY, ContentWidth, 20f);

                if (file != null)
                {
                    TextMeshProUGUI tmp = file.GetComponent<TextMeshProUGUI>();
                    if (tmp != null)
                    {
                        // Autosizing down from 21.6 - the size asked for - so an ordinary name is
                        // large and a very long one shrinks rather than being cut off.
                        tmp.enableAutoSizing = true;
                        tmp.fontSize = 21.6f;
                        tmp.fontSizeMax = 21.6f;
                        tmp.fontSizeMin = 12f;
                        tmp.margin = Vector4.zero;
                    }
                }

                continue;
            }

            Transform sliderTransform = row.Find("SmoothnessSlider");
            if (sliderTransform == null) sliderTransform = row.Find("MetallicSlider");
            if (sliderTransform == null) continue;

            // These material properties read much better as a compact two-line block:
            // property name on the first line, then the actual control directly underneath.
            // Disable the original one-line HorizontalLayoutGroup and position its existing
            // children explicitly inside the 280 px content width of the 300 px side panel.
            HorizontalLayoutGroup rowLayout = row.GetComponent<HorizontalLayoutGroup>();
            if (rowLayout != null) rowLayout.enabled = false;

            // A LEADING gap on the first of the two, so the pair sits clear of the colour block
            // above it rather than running straight on from B. Zero for Metallic, which wants to
            // stay with the slider above it.
            float topGap = 0f;
            if (row.name == "SmoothnessRow") topGap = SmoothnessTopGap;

            // Through SetRowHeight, which writes the RectTransform as well as the LayoutElement.
            // Setting only the LayoutElement did nothing at all: the Properties container is a
            // VerticalLayoutGroup with childControlHeight FALSE (MaterialEditorManager.
            // CreateContainer), so it lays these rows out by their rect height and ignores what
            // the LayoutElement asks for. That is why SliderRowHeight looked like a dead number.
            SetRowHeight(row, SliderRowHeight + topGap);

            RectTransform labelRect = row.Find("Label") as RectTransform;
            if (labelRect != null)
            {
                labelRect.anchorMin = new Vector2(0f, 1f);
                labelRect.anchorMax = new Vector2(1f, 1f);
                labelRect.pivot = new Vector2(0f, 1f);
                labelRect.anchoredPosition = new Vector2(0f, -topGap);
                labelRect.sizeDelta = new Vector2(0f, 22f);

                LayoutElement labelLayout = labelRect.GetComponent<LayoutElement>();
                if (labelLayout != null)
                {
                    labelLayout.preferredWidth = -1f;
                    labelLayout.minWidth = -1f;
                    labelLayout.preferredHeight = 22f;
                }
            }

            LayoutElement sliderLayout = sliderTransform.GetComponent<LayoutElement>();
            if (sliderLayout != null)
            {
                sliderLayout.preferredWidth = SliderWidth;
                sliderLayout.minWidth = SliderWidth;
                sliderLayout.preferredHeight = SliderHeight;
                sliderLayout.minHeight = SliderHeight;
            }

            RectTransform sliderRect = sliderTransform.GetComponent<RectTransform>();
            if (sliderRect != null)
            {
                sliderRect.anchorMin = new Vector2(0f, 1f);
                sliderRect.anchorMax = new Vector2(0f, 1f);
                sliderRect.pivot = new Vector2(0f, .5f);
                sliderRect.anchoredPosition = new Vector2(0f, -(SliderLabelToControl + topGap));
                sliderRect.sizeDelta = new Vector2(SliderWidth, SliderHeight);
            }

            RectTransform valueRect = row.Find("Value") as RectTransform;
            if (valueRect != null)
            {
                valueRect.anchorMin = new Vector2(0f, 1f);
                valueRect.anchorMax = new Vector2(0f, 1f);
                valueRect.pivot = new Vector2(0f, .5f);
                valueRect.anchoredPosition = new Vector2(SliderWidth + 8f, -(SliderLabelToControl + topGap));
                valueRect.sizeDelta = new Vector2(SliderValueWidth, 22f);

                LayoutElement valueLayout = valueRect.GetComponent<LayoutElement>();
                if (valueLayout != null)
                {
                    valueLayout.preferredWidth = SliderValueWidth;
                    valueLayout.minWidth = SliderValueWidth;
                    valueLayout.preferredHeight = 22f;
                }
            }
        }
    }

    // "MASTER COLOUR" on the left, the current colour as a swatch, then WHITE - one line.
    void LayoutHeaderRow(Transform row)
    {
        SetRowHeight(row, TintHeaderHeight);
        DisableRowLayout(row);

        float cursor = 0f;
        cursor += PlaceLeft(row.Find("Label"), cursor, 132f, 20f, TintHeaderHeight) + 6f;
        cursor += PlaceLeft(row.Find("Swatch"), cursor, TintSwatchWidth, 18f, TintHeaderHeight) + 6f;
        PlaceLeft(row.Find("WHITEButton"), cursor, 62f, 20f, TintHeaderHeight);
    }

    // R / G / B: a one-character label, the slider, and the value hard against its right end
    // rather than out at the panel edge.
    void LayoutTintChannelRow(Transform row)
    {
        SetRowHeight(row, TintRowHeight);
        DisableRowLayout(row);

        float cursor = 0f;
        cursor += PlaceLeft(row.Find("Label"), cursor, TintChannelLabelWidth, 18f, TintRowHeight) + 4f;
        cursor += PlaceLeft(FindSlider(row), cursor, TintSliderWidth, TintSliderHeight, TintRowHeight) + 8f;
        PlaceLeft(row.Find("Value"), cursor, SliderValueWidth, 18f, TintRowHeight);
    }

    // The slider is not named "Slider" - it carries the channel name so nothing else in this
    // file's "*Slider" lookups can pick it up - so it is found by component instead.
    static Transform FindSlider(Transform row)
    {
        Slider slider = row.GetComponentInChildren<Slider>(true);
        if (slider == null) return null;
        return slider.transform;
    }

    // Both, not just preferred: the properties column does not always control its children's
    // heights, and a row that only offers a preferred height can end up with none at all - which
    // is what collapsed the tint sliders to zero pixels and made them disappear.
    static void SetRowHeight(Transform row, float height)
    {
        LayoutElement element = row.GetComponent<LayoutElement>();
        if (element != null)
        {
            element.preferredHeight = height;
            element.minHeight = height;
        }

        RectTransform rect = row as RectTransform;
        if (rect != null) rect.sizeDelta = new Vector2(rect.sizeDelta.x, height);
    }

    static void DisableRowLayout(Transform row)
    {
        HorizontalLayoutGroup group = row.GetComponent<HorizontalLayoutGroup>();
        if (group != null) group.enabled = false;
    }

    // Anchored to the row's top-left, a given distance DOWN from the top rather than centred -
    // which is what a row of three stacked lines needs.
    static void PlaceTop(Transform child, float x, float y, float width, float height)
    {
        if (child == null) return;

        RectTransform rect = child as RectTransform;
        if (rect == null) return;

        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(x, -y);
        rect.sizeDelta = new Vector2(width, height);

        LayoutElement element = child.GetComponent<LayoutElement>();
        if (element != null)
        {
            element.preferredWidth = width;
            element.minWidth = width;
            element.preferredHeight = height;
            element.minHeight = height;
        }
    }

    // Anchored to the row's top-left and centred vertically in it, which is the same convention
    // the Smoothness and Metallic rows are placed with. Returns the width used, so a caller can
    // walk left to right without repeating the arithmetic.
    static float PlaceLeft(Transform child, float x, float width, float height, float rowHeight)
    {
        if (child == null) return width;

        RectTransform rect = child as RectTransform;
        if (rect == null) return width;

        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, .5f);
        rect.anchoredPosition = new Vector2(x, -rowHeight * .5f);
        rect.sizeDelta = new Vector2(width, height);

        LayoutElement element = child.GetComponent<LayoutElement>();
        if (element != null)
        {
            element.preferredWidth = width;
            element.minWidth = width;
            element.preferredHeight = height;
            element.minHeight = height;
        }

        return width;
    }

    Vector3 CentrePreviewBetweenPanels()
    {
        Camera camera = Camera.main;
        if (camera == null || previewPlane == null) return Vector3.zero;

        RectTransform leftRect = materialPanel != null && materialPanel.activeInHierarchy
            ? materialPanel.GetComponent<RectTransform>() : null;
        RectTransform rightRect = texturePanel.GetComponent<RectTransform>();
        if (leftRect == null || rightRect == null) return Vector3.zero;

        if (!TryGetScreenBounds(leftRect, out float leftMin, out float leftMax) ||
            !TryGetScreenBounds(rightRect, out float rightMin, out float rightMax))
            return Vector3.zero;

        float workspaceLeft = leftMax;
        float workspaceRight = rightMin;
        if (workspaceRight <= workspaceLeft + 20f) return Vector3.zero;

        float centreX = (workspaceLeft + workspaceRight) * .5f;
        float depth = Vector3.Dot(previewPlane.transform.position - camera.transform.position, camera.transform.forward);
        if (depth <= camera.nearClipPlane)
            depth = Mathf.Max(1f, camera.nearClipPlane + .1f);

        Vector3 oldPosition = previewPlane.transform.position;
        Vector3 target = camera.ScreenToWorldPoint(new Vector3(centreX, Screen.height * .5f, depth));
        previewPlane.transform.position = target;
        return target - oldPosition;
    }

    static bool TryGetScreenBounds(RectTransform rect, out float minX, out float maxX)
    {
        minX = float.MaxValue;
        maxX = float.MinValue;
        if (rect == null) return false;

        Vector3[] corners = new Vector3[4];
        rect.GetWorldCorners(corners);
        Camera canvasCamera = null;
        Canvas canvas = rect.GetComponentInParent<Canvas>();
        if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
            canvasCamera = canvas.worldCamera;

        for (int i = 0; i < corners.Length; i++)
        {
            Vector2 screen = RectTransformUtility.WorldToScreenPoint(canvasCamera, corners[i]);
            minX = Mathf.Min(minX, screen.x);
            maxX = Mathf.Max(maxX, screen.x);
        }
        return maxX >= minX;
    }

    void RefreshUVVisuals(Vector3 moveDelta)
    {
        if (uvWorkspace == null) return;

        // Existing saved rectangles are re-evaluated from their UV coordinates against the
        // preview plane's final transform. That keeps both cyan outlines and number labels glued
        // to the texture instead of retaining world positions from before the workspace shift.
        if (rectanglesField != null && updateRectangleVisualMethod != null)
        {
            IEnumerable definitions = rectanglesField.GetValue(uvWorkspace) as IEnumerable;
            if (definitions != null)
            {
                foreach (object definition in definitions)
                    if (definition != null)
                        updateRectangleVisualMethod.Invoke(uvWorkspace, new[] { definition });
            }
        }

        // Same reasoning as the rectangles above: the plane-edge outline is re-derived from the
        // plane's final transform rather than shifted by delta, since it has no stored UV data.
        updateOutlineVisualMethod?.Invoke(uvWorkspace, null);

        // The in-progress draft is recalculated by TextureUVRectWorkspace during Update, before
        // this final plane centring pass, so carry it by the same one-frame delta while drawing.
        if (moveDelta.sqrMagnitude > .0000001f && draftLineField != null)
        {
            LineRenderer draft = draftLineField.GetValue(uvWorkspace) as LineRenderer;
            if (draft != null && draft.gameObject.activeInHierarchy)
            {
                for (int i = 0; i < draft.positionCount; i++)
                    draft.SetPosition(i, draft.GetPosition(i) + moveDelta);
            }
        }
    }

    // Was a private full-scene sweep, called three times from an UNGATED LateUpdate - and the
    // panels it looks for do not exist at all in a grooming session, so the null guard above
    // never latched and all three ran to completion over every object in the scene, every frame.
    // See RuntimeNamedObjectCache for what that actually cost.
    static GameObject FindInactive(string objectName)
    {
        return RuntimeNamedObjectCache.Find(objectName);
    }
}
