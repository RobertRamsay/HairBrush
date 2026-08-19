using UnityEngine;
using UnityEngine.UI;

// Keeps the shape AnimationCurve editor in the same vertical layout as its profile row.
// The existing editor still owns all curve interaction/state; this authority only changes
// presentation from a centred canvas popup to an inline expandable panel. Because it is a
// direct GroomingPanel child, opening it naturally pushes every following control downward
// and GroomingPanelWheelScrollAuthority includes it in the normal right-panel scroll range.
[DefaultExecutionOrder(9520)]
public class InlineGroomShapeCurveEditorAuthority : MonoBehaviour
{
    private const float ProfileRowHeight = 27f;
    private const float InlineHeight = 340f;

    private ModelViewer viewer;
    private GameObject panel;
    private GroomShapeCurveEditor attachedEditor;
    private RectTransform attachedRoot;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<InlineGroomShapeCurveEditorAuthority>() != null) return;
        GameObject go = new GameObject("InlineGroomShapeCurveEditorAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<InlineGroomShapeCurveEditorAuthority>();
    }

    void LateUpdate()
    {
        Resolve();
        if (panel == null) return;

        // GroomingPanel's legacy VerticalLayoutGroup does not control child heights.
        // Shape profile rows were therefore visually 27px tall while their RectTransforms
        // still occupied Unity's default ~100px, leaving a large invisible band underneath.
        // Keep their real layout height equal to the visible row height.
        bool profileLayoutChanged = TightenProfileRows();

        GroomShapeCurveEditor editor = FindFirstObjectByType<GroomShapeCurveEditor>();
        if (editor == null)
        {
            attachedEditor = null;
            attachedRoot = null;
            if (profileLayoutChanged) RebuildPanelLayout();
            return;
        }

        Transform row = panel.transform.Find("ShapeCurve_" + editor.Channel + "_Row");
        if (row == null)
        {
            if (profileLayoutChanged) RebuildPanelLayout();
            return;
        }

        RectTransform root = editor.GetComponent<RectTransform>();
        if (root == null)
        {
            if (profileLayoutChanged) RebuildPanelLayout();
            return;
        }

        bool newlyAttached = attachedEditor != editor || root.parent != panel.transform;
        if (root.parent != panel.transform)
            root.SetParent(panel.transform, false);

        // The VerticalLayoutGroup owns placement/width; an explicit height makes this a
        // genuine accordion section even though the legacy grooming layout does not force
        // child heights. LayoutElement also lets any future layout implementation read the
        // same preferred size rather than relying on RectTransform.sizeDelta alone.
        root.anchorMin = new Vector2(0f, .5f);
        root.anchorMax = new Vector2(1f, .5f);
        root.pivot = new Vector2(.5f, .5f);
        root.localScale = Vector3.one;
        root.sizeDelta = new Vector2(0f, InlineHeight);

        LayoutElement element = root.GetComponent<LayoutElement>();
        if (element == null) element = root.gameObject.AddComponent<LayoutElement>();
        element.minHeight = InlineHeight;
        element.preferredHeight = InlineHeight;
        element.flexibleHeight = 0f;

        int desired = Mathf.Min(row.GetSiblingIndex() + 1, panel.transform.childCount - 1);
        if (root.GetSiblingIndex() != desired)
            root.SetSiblingIndex(desired);

        attachedEditor = editor;
        attachedRoot = root;

        if (newlyAttached || profileLayoutChanged)
        {
            RebuildPanelLayout();

            // The graph was first drawn at popup dimensions. Redraw once after the inline
            // layout has established its narrower panel width so line/point positions match.
            if (newlyAttached) editor.RefreshAll();
        }
    }

    bool TightenProfileRows()
    {
        bool changed = false;
        changed |= TightenProfileRow("ShapeCurve_Bend_Row");
        changed |= TightenProfileRow("ShapeCurve_X_Row");
        changed |= TightenProfileRow("ShapeCurve_Y_Row");
        changed |= TightenProfileRow("ShapeCurve_Z_Row");
        changed |= TightenProfileRow("ShapeCurve_CurlFrequency_Row");
        changed |= TightenProfileRow("ShapeCurve_CurlDiameter_Row");
        changed |= TightenProfileRow("ShapeCurve_SegmentDensity_Row");
        return changed;
    }

    bool TightenProfileRow(string rowName)
    {
        Transform found = panel.transform.Find(rowName);
        RectTransform rect = found as RectTransform;
        if (rect == null) return false;

        bool changed = !Mathf.Approximately(rect.sizeDelta.y, ProfileRowHeight);
        if (changed)
            rect.sizeDelta = new Vector2(rect.sizeDelta.x, ProfileRowHeight);

        LayoutElement element = rect.GetComponent<LayoutElement>();
        if (element == null)
        {
            element = rect.gameObject.AddComponent<LayoutElement>();
            changed = true;
        }

        if (!Mathf.Approximately(element.minHeight, ProfileRowHeight))
        {
            element.minHeight = ProfileRowHeight;
            changed = true;
        }
        if (!Mathf.Approximately(element.preferredHeight, ProfileRowHeight))
        {
            element.preferredHeight = ProfileRowHeight;
            changed = true;
        }
        if (!Mathf.Approximately(element.flexibleHeight, 0f))
        {
            element.flexibleHeight = 0f;
            changed = true;
        }

        return changed;
    }

    void RebuildPanelLayout()
    {
        RectTransform panelRect = panel != null ? panel.GetComponent<RectTransform>() : null;
        Canvas.ForceUpdateCanvases();
        if (panelRect != null) LayoutRebuilder.ForceRebuildLayoutImmediate(panelRect);
        Canvas.ForceUpdateCanvases();

        GroomingPanelWheelScroll wheel = panel != null ? panel.GetComponent<GroomingPanelWheelScroll>() : null;
        if (wheel != null) wheel.RefreshClamp();
    }

    void Resolve()
    {
        if (viewer == null) viewer = FindFirstObjectByType<ModelViewer>();
        GameObject livePanel = viewer != null ? viewer.groomingSliderPanelGO : null;
        if (livePanel == panel) return;

        panel = livePanel;
        attachedEditor = null;
        attachedRoot = null;
    }
}
