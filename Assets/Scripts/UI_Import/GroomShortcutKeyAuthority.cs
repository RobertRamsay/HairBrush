using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

// Single-key shortcuts for the brush modes and the symmetry toggle.
//
//   P  PLACE     one card per click
//   D  PAINT     continuous placing while held (draw)
//   B  SPRAY     scatters cards through the brush radius
//   F  EVEN      fills to an even spacing, never closer
//   E  ERASE     removes cards in the brush radius
//   S  SYMMETRY  same as clicking the SYMMETRY button
//
//   CTRL + S     SAVE PROJ
//   CTRL + X     EXPORT OBJ
//
// S moved to SYMMETRY and EVEN moved to F, which is the one reassignment here that was not
// asked for: S was EVEN, and something had to give it up. F for "fill to a spacing" is the
// closest free letter to what that brush does.
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

        // Something has taken grooming away on purpose and will hand it back. This is also what
        // keeps every key below out of a REMAP session, which holds the lock for its whole life -
        // and saving mid-remap in particular is not allowed: the preview is not the groom.
        if (GroomingInputLock.AnyHold) return;

        // ---- CTRL chords ----------------------------------------------------------------
        //
        // Ahead of the viewport test below, because SAVE and EXPORT are on the panel in the
        // TEXTURE workspace too and saving from there is an ordinary thing to want. They are also
        // ahead of the bare-key block, which refuses every letter while CTRL is held - so CTRL+S
        // can never reach the S below and toggle SYMMETRY on its way past. That guard was already
        // there for CTRL+Z; this is the same protection, now load-bearing for a key of our own.
        //
        // SHIFT and ALT are excluded rather than ignored: CTRL+SHIFT is the group pick and the
        // guide-point gesture, and a chord that means something else must not also save.
        bool ctrl = keyboard.ctrlKey.isPressed || keyboard.leftCommandKey.isPressed || keyboard.rightCommandKey.isPressed;
        bool shift = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;

        if (ctrl && !shift && !keyboard.altKey.isPressed)
        {
            if (keyboard.sKey.wasPressedThisFrame)
            {
                PressPanelButton("SaveProjectButton", "SAVE PROJ");
                return;
            }

            if (keyboard.xKey.wasPressedThisFrame)
            {
                PressPanelButton("ExportOBJButton", "EXPORT OBJ");
                return;
            }
        }

        // The texture workspace or a remap session owns the viewport. Below this line everything
        // is a groom-viewport action.
        if (GroomViewportSuppressed.Active) return;

        // Bare keys only. CTRL, ALT, SHIFT and CMD are all load-bearing elsewhere - ALT is the
        // camera under MAYA-NAV, CTRL+SHIFT picks a group, CTRL+Z is undo, CTRL+S is the save
        // above - and a shortcut that fired as part of a chord would change the brush under a
        // hand that was doing something else entirely.
        bool modifier = ctrl
            || shift
            || keyboard.altKey.isPressed;
        if (modifier) return;

        // Not mid-stroke. Switching mode with the button down would finish a stroke in a mode it
        // did not start in - the EVEN spacing cache in particular belongs to one stroke and one
        // mode, and would go on being used by whatever the key switched to.
        if (Mouse.current != null && (Mouse.current.leftButton.isPressed || Mouse.current.rightButton.isPressed)) return;

        // ---- the keys -------------------------------------------------------------------

        if (keyboard.sKey.wasPressedThisFrame)
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

        if (keyboard.bKey.wasPressedThisFrame)
        {
            ApplyMode(PlacementBrushModeAuthority.PlacementMode.Spray);
            return;
        }

        if (keyboard.fKey.wasPressedThisFrame)
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

    // Presses the panel button rather than calling whatever it calls. Several authorities rebind
    // these two - the demo build swaps EXPORT for its upgrade prompt, and the save path has its
    // own focus guard - and invoking the click is the only way to get exactly what a click does
    // without having to know which of them won.
    private void PressPanelButton(string objectName, string label)
    {
        GameObject go = GameObject.Find(objectName);
        UnityEngine.UI.Button button = null;
        if (go != null) button = go.GetComponent<UnityEngine.UI.Button>();

        if (button == null || !button.interactable)
        {
            StatusToast.Show(label + " is not available right now.", true);
            return;
        }

        button.onClick.Invoke();
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
