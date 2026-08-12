using System.Linq;
using UnityEngine;
using UnityEngine.UI;

// Runtime repair/enhancement for the left Hair Groups modifier stack.
// Adds a visible vertical scrollbar, speeds mouse-wheel scrolling, and makes
// expanded modifier rows report their real child height to the ScrollRect.
[DefaultExecutionOrder(3600)]
public class GroupPanelScrollEnhancer : MonoBehaviour
{
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<GroupPanelScrollEnhancer>() != null) return;
        GameObject go = new GameObject("GroupPanelScrollEnhancer");
        DontDestroyOnLoad(go);
        go.AddComponent<GroupPanelScrollEnhancer>();
    }

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + .15f;

        ScrollRect scroll = FindObjectsByType<ScrollRect>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
            .FirstOrDefault(s => s.name == "GroupScrollView");
        if (scroll == null) return;

        scroll.scrollSensitivity = 28f;
        scroll.inertia = true;
        scroll.decelerationRate = .18f;
        scroll.vertical = true;
        scroll.horizontal = false;

        EnsureScrollbar(scroll);
        RepairModifierHeights(scroll.content);
        LayoutRebuilder.ForceRebuildLayoutImmediate(scroll.content);
    }

    void EnsureScrollbar(ScrollRect scroll)
    {
        if (scroll.verticalScrollbar != null) return;

        RectTransform root = scroll.transform as RectTransform;
        RectTransform viewport = scroll.viewport;
        if (root == null || viewport == null) return;

        // Reserve a narrow strip on the LEFT as requested.
        viewport.offsetMin = new Vector2(15f, viewport.offsetMin.y);

        GameObject barGO = new GameObject("GroupVerticalScrollbar", typeof(RectTransform), typeof(Image), typeof(Scrollbar));
        barGO.transform.SetParent(scroll.transform, false);
        RectTransform barRect = barGO.GetComponent<RectTransform>();
        barRect.anchorMin = new Vector2(0f, 0f);
        barRect.anchorMax = new Vector2(0f, 1f);
        barRect.pivot = new Vector2(0f, .5f);
        barRect.anchoredPosition = new Vector2(2f, 0f);
        barRect.sizeDelta = new Vector2(11f, -4f);
        Image bg = barGO.GetComponent<Image>();
        bg.color = new Color(.08f, .08f, .08f, .9f);

        GameObject sliding = new GameObject("Sliding Area", typeof(RectTransform));
        sliding.transform.SetParent(barGO.transform, false);
        RectTransform slidingRect = sliding.GetComponent<RectTransform>();
        slidingRect.anchorMin = Vector2.zero;
        slidingRect.anchorMax = Vector2.one;
        slidingRect.offsetMin = new Vector2(1f, 1f);
        slidingRect.offsetMax = new Vector2(-1f, -1f);

        GameObject handleGO = new GameObject("Handle", typeof(RectTransform), typeof(Image));
        handleGO.transform.SetParent(sliding.transform, false);
        RectTransform handle = handleGO.GetComponent<RectTransform>();
        handle.anchorMin = Vector2.zero;
        handle.anchorMax = Vector2.one;
        handle.offsetMin = Vector2.zero;
        handle.offsetMax = Vector2.zero;
        handleGO.GetComponent<Image>().color = new Color(.42f, .52f, .62f, 1f);

        Scrollbar bar = barGO.GetComponent<Scrollbar>();
        bar.handleRect = handle;
        bar.direction = Scrollbar.Direction.BottomToTop;
        bar.targetGraphic = handleGO.GetComponent<Image>();
        scroll.verticalScrollbar = bar;
        scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
        scroll.verticalScrollbarSpacing = 3f;
    }

    void RepairModifierHeights(RectTransform content)
    {
        if (content == null) return;

        foreach (RectTransform modifier in content.Cast<Transform>()
            .Select(t => t as RectTransform)
            .Where(r => r != null && r.name.StartsWith("ClumpModifier_")))
        {
            // Collapsed clump rows are intentionally short.
            if (modifier.childCount <= 1)
            {
                modifier.sizeDelta = new Vector2(modifier.sizeDelta.x, 34f);
                continue;
            }

            VerticalLayoutGroup layout = modifier.GetComponent<VerticalLayoutGroup>();
            float spacing = layout != null ? layout.spacing : 0f;
            float height = layout != null ? layout.padding.top + layout.padding.bottom : 0f;
            int visible = 0;

            for (int i = 0; i < modifier.childCount; i++)
            {
                RectTransform child = modifier.GetChild(i) as RectTransform;
                if (child == null || !child.gameObject.activeSelf) continue;
                float childHeight = LayoutUtility.GetPreferredHeight(child);
                if (childHeight <= 0f) childHeight = child.sizeDelta.y;
                height += Mathf.Max(0f, childHeight);
                visible++;
            }
            if (visible > 1) height += spacing * (visible - 1);

            // A little breathing room at the bottom so the last curve slider is reachable.
            height += 12f;
            modifier.sizeDelta = new Vector2(modifier.sizeDelta.x, Mathf.Max(34f, height));
        }
    }
}
