using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

// [ and ] resize whichever brush radius is currently relevant: the Ctrl+Click localized
// selection radius while a selection hotspot is active, otherwise the Spray/Erase placement
// brush radius while one of those placement modes is active. Tap for one step; hold past
// 0.5s to auto-repeat, matching standard keyboard-repeat behaviour (e.g. holding backspace
// in a text field). Place/Paint placement modes have no radius concept to adjust, so the keys
// are a no-op there unless a Ctrl+Click selection is active.
[DefaultExecutionOrder(1000)]
public class BrushRadiusHotkeyAuthority : MonoBehaviour
{
    private const float Step = .01f;
    private const float HoldDelay = .5f;
    private const float RepeatInterval = .06f;

    // These mirror SelectionBrushScaleTuning.MaxRadius and PlacementBrushModeAuthority's own
    // "Brush Radius" slider bounds. Both are private to their own scripts, so the bounds are
    // duplicated here rather than exposed just for this one external caller.
    private const float SelectionMinRadius = .001f;
    private const float SelectionMaxRadius = .25f;
    private const float PlacementMinRadius = .002f;
    private const float PlacementMaxRadius = .20f;

    private ModelViewer viewer;
    private PlacementBrushModeAuthority placement;
    private FieldInfo hasSelectionField;
    private FieldInfo placementModeField;
    private FieldInfo placementRadiusField;

    private float leftHeldSince = -1f;
    private float rightHeldSince = -1f;
    private float nextLeftRepeat;
    private float nextRightRepeat;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<BrushRadiusHotkeyAuthority>() != null) return;
        GameObject go = new GameObject("BrushRadiusHotkeyAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<BrushRadiusHotkeyAuthority>();
    }

    void Update()
    {
        Resolve();
        if (viewer == null || Keyboard.current == null) return;

        if (IsTypingInField())
        {
            leftHeldSince = -1f;
            rightHeldSince = -1f;
            return;
        }

        HandleKey(Keyboard.current.leftBracketKey, -1f, ref leftHeldSince, ref nextLeftRepeat);
        HandleKey(Keyboard.current.rightBracketKey, 1f, ref rightHeldSince, ref nextRightRepeat);
    }

    void HandleKey(KeyControl key, float direction, ref float heldSince, ref float nextRepeat)
    {
        if (key.wasPressedThisFrame)
        {
            heldSince = Time.unscaledTime;
            nextRepeat = Time.unscaledTime + HoldDelay;
            ApplyStep(direction);
        }
        else if (key.isPressed && heldSince >= 0f && Time.unscaledTime >= nextRepeat)
        {
            nextRepeat = Time.unscaledTime + RepeatInterval;
            ApplyStep(direction);
        }
        else if (key.wasReleasedThisFrame)
        {
            heldSince = -1f;
        }
    }

    void ApplyStep(float direction)
    {
        // Ctrl+Click localized selection takes priority when active - SelectionBrushScaleTuning
        // already polls viewer.brushRadius every frame and syncs its own slider/weights from it,
        // so writing the value directly here is enough; no need to duplicate that logic.
        if (GetBool(hasSelectionField))
        {
            viewer.brushRadius = Mathf.Clamp(viewer.brushRadius + direction * Step, SelectionMinRadius, SelectionMaxRadius);
            return;
        }

        if (placement == null || placementModeField == null || placementRadiusField == null) return;
        if (placementModeField.GetValue(placement) is PlacementBrushModeAuthority.PlacementMode mode &&
            (mode == PlacementBrushModeAuthority.PlacementMode.Spray || mode == PlacementBrushModeAuthority.PlacementMode.Erase))
        {
            float current = placementRadiusField.GetValue(placement) is float f ? f : 0f;
            float updated = Mathf.Clamp(current + direction * Step, PlacementMinRadius, PlacementMaxRadius);
            placementRadiusField.SetValue(placement, updated);
        }
    }

    bool IsTypingInField()
    {
        return EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null &&
               EventSystem.current.currentSelectedGameObject.GetComponent<TMP_InputField>() != null;
    }

    void Resolve()
    {
        if (viewer == null)
        {
            viewer = FindFirstObjectByType<ModelViewer>();
            if (viewer != null)
                hasSelectionField = typeof(ModelViewer).GetField("hasSelectionHotspot", BindingFlags.Instance | BindingFlags.NonPublic);
        }
        if (placement == null)
        {
            placement = FindFirstObjectByType<PlacementBrushModeAuthority>();
            if (placement != null)
            {
                BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                placementModeField = typeof(PlacementBrushModeAuthority).GetField("mode", flags);
                placementRadiusField = typeof(PlacementBrushModeAuthority).GetField("brushRadius", flags);
            }
        }
    }

    bool GetBool(FieldInfo field) => field != null && viewer != null && field.GetValue(viewer) is bool b && b;
}
