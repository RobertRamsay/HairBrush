using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

// TAB+Click and SPACE+Click belong to modifier placement/repositioning, never card creation.
// Runs before ModelViewer.Update and temporarily disables grooming for that one frame so the
// ordinary click-to-place path cannot consume the same click.
[DefaultExecutionOrder(-1200)]
public class ModifierGestureReservation : MonoBehaviour
{
    private ModelViewer viewer;
    private bool restoreNextFrame;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<ModifierGestureReservation>() != null) return;
        GameObject go = new GameObject("ModifierGestureReservation");
        DontDestroyOnLoad(go);
        go.AddComponent<ModifierGestureReservation>();
    }

    void Update()
    {
        if (viewer == null) viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer == null || Mouse.current == null || Keyboard.current == null) return;

        if (restoreNextFrame)
        {
            // Not unconditional any more. This restore lands at order -1200, which is INSIDE the
            // window a guide's placement lockout is trying to hold: GuideCurveHandleAuthority
            // re-asserts at -6100 and ModelViewer.HandleGrooming reads the flag at 0, so a blind
            // ToggleGroomingMode(true) here switched card placement back on for the rest of that
            // frame. SPACE+clicking to reposition a guide would then plant a hair card on the
            // model, directly under the curve being edited - the exact thing the lockout exists
            // to prevent, one frame wide, re-armed by every reposition.
            restoreNextFrame = false;
            if (!GroomingInputLock.AnyHold) viewer.ToggleGroomingMode(true);
        }

        if (!Mouse.current.leftButton.wasPressedThisFrame) return;

        // A click on the PANEL is not a placement gesture and has nothing to reserve against.
        // Without this, holding SPACE - which the guide panel itself tells you to do - and
        // clicking any button drove isGroomingMode false for a frame. If a lockout holder
        // captured during that frame it recorded "grooming was already off", and card placement
        // never came back for the rest of the session, with nothing on screen to explain it.
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;

        // ALT is reserved for the camera, and every authority this reservation is made on behalf
        // of now stands down while it is held: PostSpaceRepositionAuthority, PostAffectorSurfaceMoveUX
        // and GuideCurveManager for SPACE, GroupClumperInteractionAuthority and
        // GroupClumperInteractionFix for TAB. Reserving anyway would drive grooming off for a
        // frame on behalf of a gesture that is not going to happen.
        if (MayaNavigationAuthority.AltReserved) return;

        bool reserved = Keyboard.current.tabKey.isPressed || Keyboard.current.spaceKey.isPressed;
        if (!reserved) return;

        viewer.ToggleGroomingMode(false);
        restoreNextFrame = true;
    }
}
