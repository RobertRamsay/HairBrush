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
        if (row == null || row.Find("ProfileButton") != null) return;

        Transform oldLabel = row.Find("Label");
        Transform oldEdit = row.Find("EDIT CURVEButton");
        string label = oldLabel != null && oldLabel.GetComponent<TextMeshProUGUI>() != null
            ? oldLabel.GetComponent<TextMeshProUGUI>().text
            : channel + " PROFILE";

        GameObject buttonGO = new GameObject("ProfileButton", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        buttonGO.transform.SetParent(row, false);
        buttonGO.transform.SetSiblingIndex(0);

        LayoutElement layout = buttonGO.GetComponent<LayoutElement>();
        layout.preferredWidth = 268f;
        layout.minWidth = 268f;
        layout.preferredHeight = 25f;
        layout.minHeight = 25f;

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
        text.fontStyle = FontStyles.Bold;
        text.alignment = TextAlignmentOptions.MidlineLeft;
        text.color = Color.white;
        text.raycastTarget = false;

        if (oldLabel != null) Destroy(oldLabel.gameObject);
        if (oldEdit != null) Destroy(oldEdit.gameObject);
    }
}
