using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// Applies the shared UITheme skin/palette to every Button and Slider under any Canvas. This is
// a styling-only pass: it never touches layout ownership (row widths, sibling order, listeners)
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
    private int handledRebuildFrame = -1;
    private readonly HashSet<Button> styledButtons = new HashSet<Button>();
    private readonly HashSet<Slider> styledSliders = new HashSet<Slider>();
    private readonly Dictionary<Button, bool> lastInteractable = new Dictionary<Button, bool>();

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
        // Also on the frame the UI was rebuilt. A quarter of a second is a long time to leave
        // a freshly built button wearing no skin, and this authority is the last thing to run in
        // the frame, so answering the signal here means the skin lands before the button is ever
        // drawn. See RuntimeUIRebuildSignal.
        bool rebuilt = RuntimeUIRebuildSignal.TryConsume(ref handledRebuildFrame);
        if (!rebuilt && Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + .25f;

        if (viewer == null) viewer = FindFirstObjectByType<ModelViewer>();

        StyleAllButtons();
        StyleAllSliders();
    }

    void StyleAllButtons()
    {
        foreach (Button button in FindObjectsByType<Button>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (button == null) continue;

            // Buttons that deliberately style themselves (currently the variance RANDOMIZE
            // reroll) opt out of the shared skin here, otherwise this pass overwrites their
            // custom tint with the standard white one the moment they're created.
            // Reroll buttons style themselves through UITheme.StyleRerollButton - the shared
            // pass must never touch any of them or its label treatment re-truncates the text.
            string goName = button.gameObject.name;
            // The welcome panel's START button opts out too: it sizes itself to its own
            // label and is one line tall, which ClampButtonSize's 26-32 canvas-unit floor
            // would blow up into a giant square on the start screen's 5.43x canvas.
            if (goName == "RANDOMIZEButton" || goName == "RButton" || goName == "GroupUVRandomSeedButton") continue;
            if (goName == WelcomeWhatsNewAuthority.StartButtonName) continue;
            // The demo buy card's backdrop is a Button so that clicking away dismisses it, but
            // the Graphic it points at is the full-screen dimmer. Skinning that would repaint the
            // whole screen white with a button sprite on it, and ClampButtonSize would drag the
            // stretched rect off by 32 units on the way past. The three buttons INSIDE the card
            // are not named here and are skinned normally, which is the point of them.
            if (goName == DemoUpgradePromptAuthority.DismissLayerName) continue;
            // The INPUT KEYS page's dimmer is the same shape and the same trap: a Button whose
            // Graphic is the full-screen backdrop, so that clicking away closes the page. The
            // CLOSE button inside it is not named here and is skinned normally.
            if (goName == InputKeysDialog.DimmerName) continue;
            // And the missing-head question's backdrop, for the same reason again. Its two
            // answer buttons inside the card are not named here and are skinned normally.
            if (goName == MissingModelPrompt.DimmerName) continue;

            if (!styledButtons.Contains(button))
            {
                UITheme.StyleButton(button);
                styledButtons.Add(button);
                lastInteractable[button] = button.interactable;
                UITheme.RefreshInteractable(button);
                continue;
            }

            // Only touch a button's visuals again if its interactable state actually changed
            // since the last poll. Reasserting identical values every poll was harmless in
            // principle but gave every other authority that also restyles a button (several
            // do) one more chance per second to visibly race this one - this removes that
            // window entirely rather than relying on execution order alone.
            bool current = button.interactable;
            if (!lastInteractable.TryGetValue(button, out bool previous) || previous != current)
            {
                lastInteractable[button] = current;
                UITheme.RefreshInteractable(button);
            }
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
}
