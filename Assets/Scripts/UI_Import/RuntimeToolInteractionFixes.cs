using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// Runtime interaction repairs for compact variance controls.
[DefaultExecutionOrder(1500)]
public class RuntimeToolInteractionFixes : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<RuntimeToolInteractionFixes>() != null) return;
        GameObject go = new GameObject("RuntimeToolInteractionFixes");
        DontDestroyOnLoad(go);
        go.AddComponent<RuntimeToolInteractionFixes>();
    }

    void Update()
    {
        HandleSeedControls();
    }

    void HandleSeedControls()
    {
        if (Mouse.current == null) return;

        // This is the one mouse authority in the project that hit-tests with its own screen-rect
        // maths instead of asking the EventSystem, which is what lets it drive the seed controls
        // through the layers that sit over them. The cost is that a modal's backdrop means nothing
        // to it: a click aimed at dismissing the demo's buy card, landing over the groom panel
        // behind it, would reroll that group's variance seed or drop a caret in a hidden text
        // field. Always false in a PRO build.
        if (DemoUpgradePrompt.IsOpen) return;

        Vector2 mouse = Mouse.current.position.ReadValue();

        // Same argument as the modal above, one modifier along. This authority ignores the
        // EventSystem by design, so it also ignores the per-button nav latch that stops an ALT
        // press over the panel from moving the camera - and an ALT+LMB tumble begun over a group's
        // R button would reroll that group's variance seed. Conditional on MAYA-NAV: with it off
        // there is no gesture here to protect against.
        //
        // It suppresses the PRESS, not the whole method. Returning early would have been the
        // obvious move and is wrong: everything below force-paints hover and focus tints every
        // frame, and under MAYA-NAV ALT is held more or less continuously - so an early return
        // freezes those tints for as long as the user is driving the camera. Hover the R button,
        // hold ALT to swing the view away, and it stays lit until ALT comes up.
        bool pressed = Mouse.current.leftButton.wasPressedThisFrame &&
                       !MayaNavigationAuthority.CameraGestureActive;

        TMP_InputField[] fields = FindObjectsByType<TMP_InputField>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
            .Where(f => f.gameObject.name == "SeedInput").ToArray();
        foreach (TMP_InputField field in fields)
        {
            RectTransform rect = field.transform as RectTransform;
            if (rect == null) continue;
            bool hover = ScreenRectContains(rect, mouse);
            Image image = field.GetComponent<Image>();
            if (image != null)
            {
                // The seed box is now the FineEdge sliced sprite (see GroomVarianceSeedUIFix),
                // so hover/focus feedback is expressed as near-white tint variations that let
                // the sprite show through. The old opaque dark fills here were force-painted
                // every frame and completely buried the sprite no matter what set it.
                image.color = field.isFocused ? new Color(.75f, 1f, .95f, 1f)
                    : hover ? new Color(.85f, 1f, .97f, 1f)
                    : Color.white;
            }

            if (pressed && hover && field.interactable)
            {
                field.enabled = true;
                if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(field.gameObject);
                field.Select();
                field.ActivateInputField();
                field.MoveTextEnd(false);
            }
        }

        Button[] randomButtons = FindObjectsByType<Button>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
            .Where(b => b.gameObject.name == "RButton" && b.transform.parent != null && b.transform.parent.name.EndsWith("_VarianceRow")).ToArray();
        foreach (Button button in randomButtons)
        {
            RectTransform rect = button.transform as RectTransform;
            if (rect == null) continue;
            bool hover = ScreenRectContains(rect, mouse);
            Image image = button.GetComponent<Image>();
            if (image != null)
                image.color = hover && button.interactable ? new Color(.52f,.72f,.34f,1f) : new Color(.27f,.34f,.20f,1f);

            if (pressed && hover && button.interactable)
            {
                button.onClick.Invoke();
                if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(button.gameObject);
            }
        }
    }

    bool ScreenRectContains(RectTransform rect, Vector2 screenPoint)
    {
        Canvas canvas = rect.GetComponentInParent<Canvas>();
        Camera cam = null;
        if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
            cam = canvas.worldCamera != null ? canvas.worldCamera : Camera.main;

        Vector3[] corners = new Vector3[4];
        rect.GetWorldCorners(corners);
        Vector2 p0 = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
        Vector2 p1 = RectTransformUtility.WorldToScreenPoint(cam, corners[1]);
        Vector2 p2 = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);
        Vector2 p3 = RectTransformUtility.WorldToScreenPoint(cam, corners[3]);
        float minX = Mathf.Min(p0.x,p1.x,p2.x,p3.x);
        float maxX = Mathf.Max(p0.x,p1.x,p2.x,p3.x);
        float minY = Mathf.Min(p0.y,p1.y,p2.y,p3.y);
        float maxY = Mathf.Max(p0.y,p1.y,p2.y,p3.y);
        const float pad = 2f;
        return screenPoint.x >= minX-pad && screenPoint.x <= maxX+pad && screenPoint.y >= minY-pad && screenPoint.y <= maxY+pad;
    }
}
