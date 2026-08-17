using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Keeps the right-side workspaces the same width as the Group Manager without
// allowing fixed-width utility/curve buttons to spill outside the narrower panel.
[DefaultExecutionOrder(12500)]
public class CompactRightPanelAuthority : MonoBehaviour
{
    private const float FallbackPanelWidth = 300f;
    private const float MinimumButtonWidth = 44f;
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<CompactRightPanelAuthority>() != null) return;
        GameObject go = new GameObject("CompactRightPanelAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<CompactRightPanelAuthority>();
    }

    void LateUpdate()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + .15f;

        float targetWidth = GetLeftPanelWidth();
        CompactPanel(FindNamed("GroomingPanel"), targetWidth);
        CompactPanel(FindNamed("TextureEditorPanel"), targetWidth);
    }

    static float GetLeftPanelWidth()
    {
        GameObject left = FindNamed("GroupManagerPanel");
        if (left == null) return FallbackPanelWidth;

        RectTransform rect = left.GetComponent<RectTransform>();
        if (rect == null) return FallbackPanelWidth;

        float width = rect.rect.width;
        if (width <= 1f) width = Mathf.Abs(rect.sizeDelta.x);
        return width > 1f ? width : FallbackPanelWidth;
    }

    static void CompactPanel(GameObject panel, float targetWidth)
    {
        if (panel == null) return;

        RectTransform panelRect = panel.GetComponent<RectTransform>();
        if (panelRect == null) return;

        Vector2 size = panelRect.sizeDelta;
        if (!Mathf.Approximately(size.x, targetWidth))
        {
            size.x = targetWidth;
            panelRect.sizeDelta = size;
        }

        // Match the left panel's 10px horizontal padding so controls retain as much
        // useful slider travel as possible at the smaller width.
        VerticalLayoutGroup rootLayout = panel.GetComponent<VerticalLayoutGroup>();
        float innerWidth = targetWidth;
        if (rootLayout != null)
        {
            if (rootLayout.padding.left != 10 || rootLayout.padding.right != 10)
                rootLayout.padding = new RectOffset(10, 10, rootLayout.padding.top, rootLayout.padding.bottom);
            innerWidth -= rootLayout.padding.left + rootLayout.padding.right;
        }

        HorizontalLayoutGroup[] rows = panel.GetComponentsInChildren<HorizontalLayoutGroup>(true);
        foreach (HorizontalLayoutGroup row in rows)
            CompactRow(row, innerWidth);

        // Long labels such as TEXTURE MODE / EDIT CURVE should scale down rather than
        // wrap or push their button outside the panel.
        foreach (Button button in panel.GetComponentsInChildren<Button>(true))
        {
            TextMeshProUGUI label = button.GetComponentInChildren<TextMeshProUGUI>(true);
            if (label == null) continue;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.enableAutoSizing = true;
            label.fontSizeMin = 9f;
            if (label.fontSizeMax < 9f || label.fontSizeMax > 16f) label.fontSizeMax = 16f;
        }

        LayoutRebuilder.MarkLayoutForRebuild(panelRect);
    }

    static void CompactRow(HorizontalLayoutGroup row, float parentInnerWidth)
    {
        if (row == null) return;

        List<Transform> activeChildren = new List<Transform>();
        List<LayoutElement> fixedElements = new List<LayoutElement>();
        float fixedWidth = 0f;

        for (int i = 0; i < row.transform.childCount; i++)
        {
            Transform child = row.transform.GetChild(i);
            if (!child.gameObject.activeSelf) continue;
            activeChildren.Add(child);

            LayoutElement le = child.GetComponent<LayoutElement>();
            if (le == null) continue;

            float requested = le.preferredWidth > 0f ? le.preferredWidth : le.minWidth;
            if (requested <= 0f) continue;
            fixedElements.Add(le);
            fixedWidth += requested;
        }

        if (activeChildren.Count == 0 || fixedElements.Count == 0) return;

        float available = parentInnerWidth - row.padding.left - row.padding.right;
        available -= row.spacing * Mathf.Max(0, activeChildren.Count - 1);
        if (available <= 0f || fixedWidth <= available) return;

        int flexibleCount = activeChildren.Count - fixedElements.Count;
        float reserveForFlexible = flexibleCount > 0
            ? Mathf.Min(80f * flexibleCount, available * .45f)
            : 0f;
        float widthForFixed = Mathf.Max(0f, available - reserveForFlexible);
        float scale = fixedWidth > 0f ? Mathf.Min(1f, widthForFixed / fixedWidth) : 1f;

        foreach (LayoutElement le in fixedElements)
        {
            float requested = le.preferredWidth > 0f ? le.preferredWidth : le.minWidth;
            float compact = Mathf.Max(MinimumButtonWidth, requested * scale);
            le.preferredWidth = compact;
            if (le.minWidth > compact || le.minWidth < 0f) le.minWidth = compact;
            le.flexibleWidth = 0f;
        }
    }

    static GameObject FindNamed(string name)
    {
        foreach (Transform t in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (t != null && t.name == name) return t.gameObject;
        return null;
    }
}
