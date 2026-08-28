using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

// Single-key shortcuts for the brush modes and the symmetry toggle.
//
//   P  PLACE     one card per click
//   D  PAINT     continuous placing while held (draw)
//   S  EVEN      brush that fills to an even spacing, never closer
//   E  ERASE     removes cards in the brush radius
//   X  SYMMETRY  same as clicking the SYMMETRY button
//
// SPRAY is deliberately unbound. Four of the five modes have a letter that names them; spray
// does not, and inventing one would put a scatter brush a mistyped key away from a groom.
//
// WHY THIS IS A SEPARATE AUTHORITY, not four lines in PlacementBrushModeAuthority.Update:
// that method returns early for whole minutes at a time - the texture workspace, an armed
// placement, a remap session - and it also returns whenever grooming is off. Symmetry has to
// stay togglable in some of those windows and the mode keys must not fire in others, so the
// two need different gates. Putting them in the same method means one gate serving both, and
// the wrong one either way.
//
// THE TEXT-FIELD GUARD is the whole point of the request. Every one of these letters appears
// in ordinary group names - "Sideburns" alone contains P, S and E - so a shortcut that fires
// while a name is being typed does not just get ignored, it silently switches the brush to
// ERASE behind a rename box. GroupNameInlineEditAuthority.IsEnteringText is the project's
// existing answer to that and is asked first here; the parent walk below it covers a TMP field
// whose selected object is a child rather than the field itself.
[DefaultExecutionOrder(-45)]
public class GroomShortcutKeyAuthority : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Spawn()
    {
        if (FindFirstObjectByType<GroomShortcutKeyAuthority>() != null) return;
        GameObject go = new GameObject(nameof(GroomShortcutKeyAuthority));
        DontDestroyOnLoad(go);
        go.AddComponent<GroomShortcutKeyAuthority>();
    }

    private void Update()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null) return;

        // ---- the guards -----------------------------------------------------------------
        //
        // Read top to bottom: a key is acted on only if every one of these says nothing else
        // owns the keyboard this frame.

        // Somebody is typing. Group rename, a SEED box, anything with a TMP field focused.
        if (IsTyping()) return;

        // A modal is up. Always false in a PRO build.
        if (DemoUpgradePrompt.IsOpen) return;

        // The INPUT KEYS page is up. It is the list of these very keys, so a press made while
        // reading it is somebody checking what a key does, not asking for it.
        if (InputKeysDialog.IsOpen) return;

        // The texture workspace or a remap session owns the viewport.
        if (GroomViewportSuppressed.Active) return;

        // Something has taken grooming away on purpose and will hand it back.
        if (GroomingInputLock.AnyHold) return;

        // Bare keys only. CTRL, ALT, SHIFT and CMD are all load-bearing elsewhere - ALT is the
        // camera under MAYA-NAV, CTRL+SHIFT picks a group, CTRL+Z is undo - and a shortcut that
        // fired as part of a chord would change the brush under a hand that was doing something
        // else entirely.
        bool modifier = keyboard.ctrlKey.isPressed
            || keyboard.altKey.isPressed
            || keyboard.leftShiftKey.isPressed
            || keyboard.rightShiftKey.isPressed
            || keyboard.leftCommandKey.isPressed
            || keyboard.rightCommandKey.isPressed;
        if (modifier) return;

        // Not mid-stroke. Switching mode with the button down would finish a stroke in a mode it
        // did not start in - the EVEN spacing cache in particular belongs to one stroke and one
        // mode, and would go on being used by whatever the key switched to.
        if (Mouse.current != null && (Mouse.current.leftButton.isPressed || Mouse.current.rightButton.isPressed)) return;

        // ---- the keys -------------------------------------------------------------------

        if (keyboard.xKey.wasPressedThisFrame)
        {
            // No grooming test on this one: the SYMMETRY button stays clickable whenever the
            // panel is up, and the key is meant to be that button. The toggle raises its own
            // toast, including the "no model loaded yet" case.
            GroomSymmetryAuthority.RequestToggle();
            return;
        }

        if (keyboard.pKey.wasPressedThisFrame)
        {
            ApplyMode(PlacementBrushModeAuthority.PlacementMode.Place);
            return;
        }

        if (keyboard.dKey.wasPressedThisFrame)
        {
            ApplyMode(PlacementBrushModeAuthority.PlacementMode.Paint);
            return;
        }

        if (keyboard.sKey.wasPressedThisFrame)
        {
            ApplyMode(PlacementBrushModeAuthority.PlacementMode.Even);
            return;
        }

        if (keyboard.eKey.wasPressedThisFrame)
        {
            ApplyMode(PlacementBrushModeAuthority.PlacementMode.Erase);
            return;
        }
    }

    private void ApplyMode(PlacementBrushModeAuthority.PlacementMode next)
    {
        PlacementBrushModeAuthority brush = PlacementBrushModeAuthority.Instance;
        if (brush == null) return;

        // Changing the placement mode outside grooming would be invisible - the mode strip is
        // part of the grooming panel - and the user would come back to a brush they do not
        // remember choosing. Say so instead of doing it silently.
        //
        // Asked of the brush authority rather than worked out here. It already resolves
        // ModelViewer's grooming flag and loaded model, and two places deciding separately what
        // "grooming is on" means is how they end up disagreeing.
        if (!brush.GroomingActive)
        {
            StatusToast.Show("Switch to GROOMING to change the placement mode.", true);
            return;
        }

        if (brush.CurrentMode == next) return;

        brush.ApplyModeFromShortcut(next);
        StatusToast.Show("PLACEMENT: " + next.ToString().ToUpperInvariant()
            + " - " + PlacementBrushModeAuthority.DescribeMode(next));
    }

    // True while any text field could be receiving these letters.
    private static bool IsTyping()
    {
        if (GroupNameInlineEditAuthority.IsEnteringText) return true;

        EventSystem events = EventSystem.current;
        if (events == null) return false;

        GameObject selected = events.currentSelectedGameObject;
        if (selected == null) return false;

        // GetComponentInParent rather than GetComponent: a TMP field can leave its own text or
        // viewport child selected, and that child is not the field.
        return selected.GetComponentInParent<TMP_InputField>() != null;
    }
}
