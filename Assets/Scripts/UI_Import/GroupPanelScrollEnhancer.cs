using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

// Stable, one-shot setup for the Hair Groups scroll area.
// Important: this deliberately does NOT continuously rebuild layout or resize controls.
// Dynamic runtime touching of ScrollRect/content was causing visible jitter while CLUMP was open.
[DefaultExecutionOrder(3600)]
public class GroupPanelScrollEnhancer : MonoBehaviour
{
    private ScrollRect configuredScroll;
    private readonly HashSet<int> configuredClumpModifiers = new HashSet<int>();
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
        // Only scan for newly-created/rebuilt UI objects. Existing UI is never touched again.
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + .25f;

        ScrollRect scroll = FindObjectsByType<ScrollRect>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
            .FirstOrDefault(s => s.name == "GroupScrollView");
        if (scroll == null) return;

        if (configuredScroll != scroll)
        {
            configuredScroll = scroll;
            configuredClumpModifiers.Clear();
            ConfigureScrollOnce(scroll);
        }

        ConfigureNewClumpRowsOnce(scroll);
    }

    void ConfigureScrollOnce(ScrollRect scroll)
    {
        scroll.scrollSensitivity = 32f;
        scroll.inertia = true;
        scroll.decelerationRate = .18f;
        scroll.vertical = true;
        scroll.horizontal = false;

        RectTransform viewport = scroll.viewport;
        if (viewport != null)
        {
            // Fixed gutters. Set once, so the viewport cannot oscillate during layout.
            viewport.offsetMin = new Vector2(15f, viewport.offsetMin.y);
            viewport.offsetMax = new Vector2(-6f, viewport.offsetMax.y);
        }

        if (scroll.verticalScrollbar == null)
            CreatePermanentScrollbar(scroll);
        else
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
    }

    void CreatePermanentScrollbar(ScrollRect scroll)
    {
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

    void ConfigureNewClumpRowsOnce(ScrollRect scroll)
    {
        if (scroll.content == null) return;

        foreach (RectTransform modifier in scroll.content.Cast<Transform>()
            .Select(t => t as RectTransform)
            .Where(r => r != null && r.name.StartsWith("ClumpModifier_")))
        {
            int id = modifier.GetInstanceID();
            if (!configuredClumpModifiers.Add(id)) continue;

            bool expanded = modifier.childCount > 1;
            modifier.sizeDelta = new Vector2(modifier.sizeDelta.x, expanded ? 500f : 34f);

            // Normalize handles once when this CLUMP row is born. No per-frame resizing.
            foreach (Slider slider in modifier.GetComponentsInChildren<Slider>(true))
                if (slider.handleRect != null)
                    slider.handleRect.sizeDelta = new Vector2(7f, 11f);

            // One rebuild for a newly-created modifier, then leave the ScrollRect alone.
            LayoutRebuilder.ForceRebuildLayoutImmediate(scroll.content);
        }
    }
}
