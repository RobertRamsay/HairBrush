using System.Collections;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// Opt-in wrapper around ModelViewer's legacy Shift-stroke "make a new group?" dialog.
// OFF is the default: Shift painting behaves continuously with no modal prompt.
// ON preserves the existing dialog and grouping behavior without duplicating that logic here.
[DefaultExecutionOrder(-1400)]
public class ShiftStrokeGroupingOption : MonoBehaviour
{
    private ModelViewer viewer;
    private FieldInfo wasHoldingShiftDragField;
    private FieldInfo sessionPlacedCardsField;

    private GameObject boundPanel;
    private GameObject optionsRow;
    private Button toggleButton;
    private TextMeshProUGUI toggleText;
    private bool askAfterShiftStroke;
    private float nextUIScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<ShiftStrokeGroupingOption>() != null) return;
        GameObject go = new GameObject("ShiftStrokeGroupingOption");
        DontDestroyOnLoad(go);
        go.AddComponent<ShiftStrokeGroupingOption>();
    }

    void Update()
    {
        ResolveViewer();
        if (viewer == null) return;

        SuppressLegacyPromptWhenDisabled();

        if (Time.unscaledTime < nextUIScan) return;
        nextUIScan = Time.unscaledTime + .10f;
        MaintainLeftPanelUI();
    }

    void ResolveViewer()
    {
        if (viewer != null) return;
        viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer == null) return;

        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        wasHoldingShiftDragField = typeof(ModelViewer).GetField("wasHoldingShiftDrag", flags);
        sessionPlacedCardsField = typeof(ModelViewer).GetField("sessionPlacedCards", flags);
    }

    void SuppressLegacyPromptWhenDisabled()
    {
        if (askAfterShiftStroke || wasHoldingShiftDragField == null) return;

        bool shiftHeld = Keyboard.current != null &&
            (Keyboard.current.leftShiftKey.isPressed || Keyboard.current.rightShiftKey.isPressed);
        if (shiftHeld) return;

        if (!(wasHoldingShiftDragField.GetValue(viewer) is bool wasDragging) || !wasDragging) return;

        // Run before ModelViewer.Update. Clearing this flag on Shift release skips only the
        // legacy modal prompt; all cards have already been placed and refreshed normally.
        wasHoldingShiftDragField.SetValue(viewer, false);
        if (sessionPlacedCardsField?.GetValue(viewer) is IList placed)
            placed.Clear();
    }

    void MaintainLeftPanelUI()
    {
        GameObject panel = GameObject.Find("GroupManagerPanel");
        if (panel == null)
        {
            boundPanel = null;
            optionsRow = null;
            toggleButton = null;
            toggleText = null;
            return;
        }

        if (boundPanel != panel || optionsRow == null)
            Bind(panel);

        CompactHeader(panel);
        MaintainOrder(panel.transform);
        UpdateToggleVisual();
    }

    void Bind(GameObject panel)
    {
        boundPanel = panel;

        Transform existing = panel.transform.Find("GroupQuickActionsRow");
        if (existing != null)
            optionsRow = existing.gameObject;
        else
            optionsRow = BuildOptionsRow(panel.transform);

        Transform toggle = optionsRow != null ? optionsRow.transform.Find("ShiftGroupPromptToggle") : null;
        if (toggle != null)
        {
            toggleButton = toggle.GetComponent<Button>();
            toggleText = toggle.GetComponentInChildren<TextMeshProUGUI>(true);
        }

        RehomeNewGroupButton(panel.transform);
        UpdateToggleVisual();
    }

    GameObject BuildOptionsRow(Transform parent)
    {
        GameObject row = new GameObject("GroupQuickActionsRow", typeof(RectTransform), typeof(LayoutElement), typeof(HorizontalLayoutGroup));
        row.transform.SetParent(parent, false);
        row.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 32f);

        LayoutElement le = row.GetComponent<LayoutElement>();
        le.minHeight = 32f;
        le.preferredHeight = 32f;

        HorizontalLayoutGroup layout = row.GetComponent<HorizontalLayoutGroup>();
        layout.spacing = 4f;
        layout.padding = new RectOffset(0, 0, 0, 0);
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = true;

        GameObject toggleGO = new GameObject("ShiftGroupPromptToggle", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        toggleGO.transform.SetParent(row.transform, false);
        LayoutElement toggleLayout = toggleGO.GetComponent<LayoutElement>();
        toggleLayout.flexibleWidth = 1f;
        toggleLayout.preferredWidth = 150f;

        toggleButton = toggleGO.GetComponent<Button>();
        toggleButton.onClick.AddListener(() =>
        {
            askAfterShiftStroke = !askAfterShiftStroke;
            UpdateToggleVisual();
        });

        GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textGO.transform.SetParent(toggleGO.transform, false);
        RectTransform tr = textGO.GetComponent<RectTransform>();
        tr.anchorMin = Vector2.zero;
        tr.anchorMax = Vector2.one;
        tr.offsetMin = Vector2.zero;
        tr.offsetMax = Vector2.zero;

        toggleText = textGO.GetComponent<TextMeshProUGUI>();
        toggleText.fontSize = 10.5f;
        toggleText.fontStyle = FontStyles.Bold;
        toggleText.alignment = TextAlignmentOptions.Center;
        toggleText.color = Color.white;
        toggleText.raycastTarget = false;

        return row;
    }

    void RehomeNewGroupButton(Transform panel)
    {
        if (optionsRow == null) return;

        Transform newGroup = panel.Find("NewGroupButton");
        if (newGroup == null)
            newGroup = optionsRow.transform.Find("NewGroupButton");
        if (newGroup == null) return;

        if (newGroup.parent != optionsRow.transform)
            newGroup.SetParent(optionsRow.transform, false);
        newGroup.SetSiblingIndex(0);

        RectTransform rect = newGroup as RectTransform;
        if (rect != null) rect.sizeDelta = new Vector2(rect.sizeDelta.x, 32f);

        LayoutElement le = newGroup.GetComponent<LayoutElement>();
        if (le == null) le = newGroup.gameObject.AddComponent<LayoutElement>();
        le.flexibleWidth = 1f;
        le.preferredWidth = 110f;
        le.minHeight = 32f;
        le.preferredHeight = 32f;

        TextMeshProUGUI label = newGroup.GetComponentInChildren<TextMeshProUGUI>(true);
        if (label != null) label.fontSize = 14f;

        Transform toggle = optionsRow.transform.Find("ShiftGroupPromptToggle");
        if (toggle != null) toggle.SetSiblingIndex(1);
    }

    void CompactHeader(GameObject panel)
    {
        VerticalLayoutGroup layout = panel.GetComponent<VerticalLayoutGroup>();
        if (layout != null)
        {
            layout.padding = new RectOffset(8, 8, 6, 8);
            layout.spacing = 4f;
        }

        Transform title = panel.transform.Find("TitleText");
        if (title is RectTransform titleRect)
            titleRect.sizeDelta = new Vector2(titleRect.sizeDelta.x, 28f);

        Transform poly = panel.transform.Find("HairPolygonCounterText");
        if (poly != null)
        {
            LayoutElement le = poly.GetComponent<LayoutElement>();
            if (le != null)
            {
                le.minHeight = 18f;
                le.preferredHeight = 18f;
            }
            if (poly is RectTransform polyRect)
                polyRect.sizeDelta = new Vector2(polyRect.sizeDelta.x, 18f);
        }

        Transform hint = panel.transform.Find("PostCreateHint");
        if (hint != null)
        {
            LayoutElement le = hint.GetComponent<LayoutElement>();
            if (le != null)
            {
                le.minHeight = 34f;
                le.preferredHeight = 34f;
            }
            if (hint is RectTransform hintRect)
                hintRect.sizeDelta = new Vector2(hintRect.sizeDelta.x, 34f);
            TextMeshProUGUI text = hint.GetComponent<TextMeshProUGUI>();
            if (text != null) text.fontSize = 10.5f;
        }

        RehomeNewGroupButton(panel.transform);
    }

    void MaintainOrder(Transform panel)
    {
        if (optionsRow == null || panel == null) return;

        Transform hint = panel.Find("PostCreateHint");
        Transform poly = panel.Find("HairPolygonCounterText");
        Transform title = panel.Find("TitleText");

        int target = hint != null ? hint.GetSiblingIndex() + 1 :
                     poly != null ? poly.GetSiblingIndex() + 1 :
                     title != null ? title.GetSiblingIndex() + 1 : 0;
        optionsRow.transform.SetSiblingIndex(Mathf.Clamp(target, 0, panel.childCount - 1));

        Transform scroll = panel.Find("GroupScrollView");
        if (scroll != null)
            scroll.SetSiblingIndex(panel.childCount - 1);
    }

    void UpdateToggleVisual()
    {
        if (toggleButton == null) return;

        Image image = toggleButton.GetComponent<Image>();
        if (image != null)
            image.color = askAfterShiftStroke ? new Color(.20f, .50f, .80f, 1f) : new Color(.25f, .25f, .25f, 1f);

        if (toggleText != null)
            toggleText.text = askAfterShiftStroke ? "SHIFT GROUP ASK: ON" : "SHIFT GROUP ASK: OFF";
    }
}
