using System.Linq;
using UnityEngine;
using UnityEngine.UI;

// Runtime enhancement for the left Hair Groups modifier stack.
// Keeps a stable visible scrollbar, faster wheel scrolling, correct modifier
// heights and compact slider handles without forcing layout every scan.
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
        nextScan = Time.unscaledTime + .2f;

        ScrollRect scroll = FindObjectsByType<ScrollRect>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
            .FirstOrDefault(s => s.name == "GroupScrollView");
        if (scroll == null) return;

        scroll.scrollSensitivity = 32f;
        scroll.inertia = true;
        scroll.decelerationRate = .18f;
        scroll.vertical = true;
        scroll.horizontal = false;

        EnsureScrollbar(scroll);
        bool changed = RepairModifierHeights(scroll.content);
        CompactSliderHandles(scroll);
        if (changed && scroll.content != null)
            LayoutRebuilder.ForceRebuildLayoutImmediate(scroll.content);
    }

    void EnsureScrollbar(ScrollRect scroll)
    {
        RectTransform viewport = scroll.viewport;
        if (viewport == null) return;

        // Fixed gutters keep controls clear of both edges and stop content clipping.
        viewport.offsetMin = new Vector2(15f, viewport.offsetMin.y);
        viewport.offsetMax = new Vector2(-5f, viewport.offsetMax.y);

        if (scroll.verticalScrollbar != null)
        {
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
            return;
        }

        GameObject barGO = new GameObject("GroupVerticalScrollbar", typeof(RectTransform), typeof(Image), typeof(Scrollbar));
        barGO.transform.SetParent(scroll.transform, false);
        RectTransform barRect = barGO.GetComponent<RectTransform>();
        barRect.anchorMin = new Vector2(0f, 0f);
        barRect.anchorMax = new Vector2(0f, 1f);
        barRect.pivot = new Vector2(0f, .5f);
        barRect.anchoredPosition = new Vector2(2f, 0f);
        barRect.sizeDelta = new Vector2(10f, -4f);
        barGO.GetComponent<Image>().color = new Color(.08f, .08f, .08f, .9f);

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
        scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
        scroll.verticalScrollbarSpacing = 3f;
    }

    bool RepairModifierHeights(RectTransform content)
    {
        if (content == null) return false;
        bool changed = false;

        foreach (RectTransform modifier in content.Cast<Transform>()
            .Select(t => t as RectTransform)
            .Where(r => r != null && r.name.StartsWith("ClumpModifier_")))
        {
            float target;
            if (modifier.childCount <= 1)
            {
                target = 34f;
            }
            else
            {
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
                target = Mathf.Max(34f, height + 8f);
            }

            if (Mathf.Abs(modifier.sizeDelta.y - target) > .5f)
            {
                modifier.sizeDelta = new Vector2(modifier.sizeDelta.x, target);
                changed = true;
            }
        }
        return changed;
    }

    void CompactSliderHandles(ScrollRect scroll)
    {
        if (scroll.content == null) return;
        foreach (Slider slider in scroll.content.GetComponentsInChildren<Slider>(true))
        {
            if (slider.handleRect == null) continue;
            Vector2 size = slider.handleRect.sizeDelta;
            if (size.x > 7.5f || size.y > 12f)
                slider.handleRect.sizeDelta = new Vector2(7f, 11f);
        }
    }
}
