using System.Collections;
using UnityEngine;
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
            viewer.ToggleGroomingMode(true);
            restoreNextFrame = false;
        }

        if (!Mouse.current.leftButton.wasPressedThisFrame) return;
        bool reserved = Keyboard.current.tabKey.isPressed || Keyboard.current.spaceKey.isPressed;
        if (!reserved) return;

        viewer.ToggleGroomingMode(false);
        restoreNextFrame = true;
    }
}
