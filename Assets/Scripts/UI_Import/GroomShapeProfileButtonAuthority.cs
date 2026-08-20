using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Makes each generated shape PROFILE control an actual, obvious button. The underlying
// GroomShapeCurveAuthority still owns editor creation/state; this only fixes the row UX.
[DefaultExecutionOrder(9530)]
public class GroomShapeProfileButtonAuthority : MonoBehaviour
{
    private ModelViewer viewer;
    private GroomShapeCurveAuthority curves;
    private MethodInfo openEditor;
    private GameObject boundPanel;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<GroomShapeProfileButtonAuthority>() != null) return;
        GameObject go = new GameObject("GroomShapeProfileButtonAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<GroomShapeProfileButtonAuthority>();
    }

    void LateUpdate()
    {
        Resolve();
        if (viewer == null || curves == null || openEditor == null) return;

        GameObject livePanel = viewer.groomingSliderPanelGO;
        if (livePanel == null) return;
        if (boundPanel != livePanel) boundPanel = livePanel;

        UpgradeRow("ShapeCurve_Bend_Row", GroomShapeCurveChannel.Bend);
        UpgradeRow("ShapeCurve_X_Row", GroomShapeCurveChannel.X);
        UpgradeRow("ShapeCurve_Y_Row", GroomShapeCurveChannel.Y);
        UpgradeRow("ShapeCurve_Z_Row", GroomShapeCurveChannel.Z);
        UpgradeRow("ShapeCurve_CurlFrequency_Row", GroomShapeCurveChannel.CurlFrequency);
        UpgradeRow("ShapeCurve_CurlDiameter_Row", GroomShapeCurveChannel.CurlDiameter);
        UpgradeRow("ShapeCurve_SegmentDensity_Row", GroomShapeCurveChannel.SegmentDensity);
        UpgradeRow("ShapeCurve_Width_Row", GroomShapeCurveChannel.Width);
        NormalizeOpenCurveEditor();
    }

    void Resolve()
    {
        if (viewer == null) viewer = FindFirstObjectByType<ModelViewer>();
        if (curves == null)
        {
            curves = FindFirstObjectByType<GroomShapeCurveAuthority>();
            if (curves != null)
                openEditor = typeof(GroomShapeCurveAuthority).GetMethod(
                    "OpenEditor", BindingFlags.Instance | BindingFlags.NonPublic);
        }
    }

    void UpgradeRow(string rowName, GroomShapeCurveChannel channel)
    {
        Transform row = boundPanel != null ? boundPanel.transform.Find(rowName) : null;
        if (row == null) return;

        // The old generated row was laid out for the original wide panel (135px left inset,
        // label + EDIT + RESET). At the compact 300px width that forces the controls outside
        // the panel. The profile button is now the whole row and RESET lives inside the editor.
        HorizontalLayoutGroup rowLayout = row.GetComponent<HorizontalLayoutGroup>();
        if (rowLayout != null)
        {
            rowLayout.padding = new RectOffset(0, 0, 0, 0);
            rowLayout.spacing = 0f;
            rowLayout.childControlWidth = true;
            rowLayout.childControlHeight = true;
            rowLayout.childForceExpandWidth = true;
            rowLayout.childForceExpandHeight = false;
        }

        Transform oldLabel = row.Find("Label");
        Transform oldEdit = row.Find("EDIT CURVEButton");
        Transform oldReset = row.Find("RESETButton");
        Transform existingButton = row.Find("ProfileButton");

        if (existingButton != null)
        {
            NormalizeProfileButton(existingButton.gameObject);
            if (oldLabel != null) Destroy(oldLabel.gameObject);
            if (oldEdit != null) Destroy(oldEdit.gameObject);
            if (oldReset != null) Destroy(oldReset.gameObject);
            return;
        }

        string label = oldLabel != null && oldLabel.GetComponent<TextMeshProUGUI>() != null
            ? oldLabel.GetComponent<TextMeshProUGUI>().text
            : channel + " PROFILE";

        GameObject buttonGO = new GameObject("ProfileButton", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        buttonGO.transform.SetParent(row, false);
        buttonGO.transform.SetSiblingIndex(0);
        NormalizeProfileButton(buttonGO);

        Image image = buttonGO.GetComponent<Image>();
        image.color = new Color(.20f, .50f, .82f, 1f);

        Button button = buttonGO.GetComponent<Button>();
        GroomShapeCurveChannel captured = channel;
        button.onClick.AddListener(() => openEditor?.Invoke(curves, new object[] { captured }));

        GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textGO.transform.SetParent(buttonGO.transform, false);
        RectTransform textRect = textGO.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(8f, 0f);
        textRect.offsetMax = new Vector2(-8f, 0f);

        TextMeshProUGUI text = textGO.GetComponent<TextMeshProUGUI>();
        text.text = label + "   EDIT";
        text.fontSize = 10f;
        text.fontSizeMax = 10f;
        text.fontSizeMin = 8f;
        text.enableAutoSizing = true;
        text.fontStyle = FontStyles.Bold;
        text.alignment = TextAlignmentOptions.MidlineLeft;
        text.color = Color.white;
        text.raycastTarget = false;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Ellipsis;

        if (oldLabel != null) Destroy(oldLabel.gameObject);
        if (oldEdit != null) Destroy(oldEdit.gameObject);
        if (oldReset != null) Destroy(oldReset.gameObject);
    }

    static void NormalizeProfileButton(GameObject buttonGO)
    {
        if (buttonGO == null) return;
        LayoutElement layout = buttonGO.GetComponent<LayoutElement>();
        if (layout == null) layout = buttonGO.AddComponent<LayoutElement>();
        layout.minWidth = 0f;
        layout.preferredWidth = 0f;
        layout.flexibleWidth = 1f;
        layout.preferredHeight = 25f;
        layout.minHeight = 25f;

        TextMeshProUGUI text = buttonGO.GetComponentInChildren<TextMeshProUGUI>(true);
        if (text != null)
        {
            text.fontSizeMax = 10f;
            text.fontSizeMin = 8f;
            text.enableAutoSizing = true;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Ellipsis;
        }
    }

    // The curve editor is a fixed-size modal, but its long hint/title text still needs to be
    // bounded explicitly so display scaling cannot push words into neighboring controls.
    static void NormalizeOpenCurveEditor()
    {
        GameObject popup = GameObject.Find("GroomShapeCurveEditor");
        if (popup == null) return;

        NormalizePopupText(popup.transform.Find("Title")?.GetComponent<TextMeshProUGUI>(), 12f, 18f);
        NormalizePopupText(popup.transform.Find("Hint")?.GetComponent<TextMeshProUGUI>(), 8f, 11f);
        NormalizePopupText(popup.transform.Find("RESETButton/Text")?.GetComponent<TextMeshProUGUI>(), 8f, 11f);
    }

    static void NormalizePopupText(TextMeshProUGUI text, float minSize, float maxSize)
    {
        if (text == null) return;
        text.enableAutoSizing = true;
        text.fontSizeMin = minSize;
        text.fontSizeMax = maxSize;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Ellipsis;
    }
}
