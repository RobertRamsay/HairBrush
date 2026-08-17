using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// Applies the shared UITheme skin/palette to every Button and Slider under any Canvas, plus
// section dividers at a couple of known row boundaries inside the grooming panel. This is a
// styling-only pass: it never touches layout ownership (row widths, sibling order, listeners)
// that other authorities already manage, so it can't reintroduce the kind of sizing conflict
// those scripts have with each other.
//
// Runs in LateUpdate at a very high DefaultExecutionOrder so it is always the last thing to
// touch a Button/Slider's visuals in a given frame, regardless of what order other authorities
// (several of which restyle buttons themselves, in both Update and LateUpdate) run in.
[DefaultExecutionOrder(50000)]
public class UIThemeAuthority : MonoBehaviour
{
    private ModelViewer viewer;
    private float nextScan;
    private readonly HashSet<Button> styledButtons = new HashSet<Button>();
    private readonly HashSet<Slider> styledSliders = new HashSet<Slider>();

    // Row names confirmed to exist in the current grooming panel that read as the start of a
    // new section. Deliberately conservative - only known row names get a divider, nothing is
    // guessed at from label text.
    private static readonly string[] SectionStartRows =
    {
        "GroupUVMode_Row",
        "GroupUVPredetermined_Row",
    };

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<UIThemeAuthority>() != null) return;
        GameObject go = new GameObject("UIThemeAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<UIThemeAuthority>();
    }

    void LateUpdate()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + .25f;

        if (viewer == null) viewer = FindFirstObjectByType<ModelViewer>();

        StyleAllButtons();
        StyleAllSliders();
        InsertPanelDividers();
    }

    void StyleAllButtons()
    {
        foreach (Button button in FindObjectsByType<Button>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (button == null) continue;
            if (!styledButtons.Contains(button))
            {
                UITheme.StyleButton(button);
                styledButtons.Add(button);
            }
            // Cheap per-poll sync so a button disabled/re-enabled after its initial styling
            // still shows the right dimmed/undimmed state.
            UITheme.RefreshInteractable(button);
        }
    }

    void StyleAllSliders()
    {
        foreach (Slider slider in FindObjectsByType<Slider>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (slider == null || styledSliders.Contains(slider)) continue;
            UITheme.StyleSlider(slider);
            styledSliders.Add(slider);
        }
    }

    void InsertPanelDividers()
    {
        if (viewer == null || viewer.groomingSliderPanelGO == null) return;
        Transform panel = viewer.groomingSliderPanelGO.transform;

        foreach (string rowName in SectionStartRows)
        {
            Transform row = panel.Find(rowName);
            if (row != null) UITheme.InsertDividerBefore(panel, row);
        }
    }
}
