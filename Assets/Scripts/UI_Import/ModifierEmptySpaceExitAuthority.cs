using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

// A plain left-click on genuinely empty 3D space is the universal modifier deselect gesture.
// It exits POST or CLUMPER without stealing clicks from UI, the model surface, or any of the
// modifier authoring gestures (Ctrl/Shift/Space/Tab).
//
// Run before ModelViewer's default Update so the legacy localized-selection state is cleared
// before ModelViewer decides whether this click is allowed to place cards. Because this runs
// very early, do a fresh EventSystem raycast at the current mouse position rather than relying
// on IsPointerOverGameObject(), whose cached pointer state can still describe the previous frame.
[DefaultExecutionOrder(-100)]
public class ModifierEmptySpaceExitAuthority : MonoBehaviour
{
    private ModelViewer viewer;
    private PostAffectorManager posts;
    private GroupClumperManager clumper;

    private FieldInfo postActiveIdField;
    private FieldInfo postActiveGroupField;
    private FieldInfo isSelectionModeField;
    private FieldInfo hasSelectionField;
    private FieldInfo hitPointField;
    private FieldInfo hitNormalField;
    private MethodInfo clearSelectionMethod;

    private FieldInfo selectedClumperIdField;
    private FieldInfo selectedClumperGroupField;
    private MethodInfo destroyClumperControlsMethod;

    // If a later Update authority re-establishes POST/localized selection during the same
    // click, repeat the exact teardown in LateUpdate. The click has already been classified
    // as genuine empty space, so this cannot turn a model/UI click into a deselect.
    private int plainSpaceExitFrame = -1;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<ModifierEmptySpaceExitAuthority>() != null) return;
        GameObject go = new GameObject("ModifierEmptySpaceExitAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<ModifierEmptySpaceExitAuthority>();
    }

    void Update()
    {
        Resolve();
        if (!IsPlainEmptySpaceClick()) return;

        plainSpaceExitFrame = Time.frameCount;
        CompleteExit();
    }

    void LateUpdate()
    {
        if (plainSpaceExitFrame != Time.frameCount) return;

        Resolve();
        if (!HasActiveModifierOrLocalizedSelection()) return;
        CompleteExit();
    }

    bool IsPlainEmptySpaceClick()
    {
        if (viewer == null || viewer.mainCamera == null || Mouse.current == null) return false;
        if (!Mouse.current.leftButton.wasPressedThisFrame) return false;
        if (!HasActiveModifierOrLocalizedSelection()) return false;

        // "Click in space" is deliberately a plain click. Modified clicks continue to own
        // POST creation/move, CLUMPER creation/move, placement, and the other groom gestures.
        if (Keyboard.current != null &&
            (Keyboard.current.ctrlKey.isPressed ||
             Keyboard.current.shiftKey.isPressed ||
             Keyboard.current.altKey.isPressed ||
             Keyboard.current.spaceKey.isPressed ||
             Keyboard.current.tabKey.isPressed))
            return false;

        if (PointerOverCurrentUI()) return false;

        Ray ray = viewer.mainCamera.ScreenPointToRay(Mouse.current.position.ReadValue());
        if (Physics.Raycast(ray, out _, Mathf.Infinity, ~0, QueryTriggerInteraction.Ignore)) return false;

        return true;
    }

    bool PointerOverCurrentUI()
    {
        if (EventSystem.current == null || Mouse.current == null) return false;

        PointerEventData pointer = new PointerEventData(EventSystem.current)
        {
            position = Mouse.current.position.ReadValue()
        };
        List<RaycastResult> hits = new List<RaycastResult>();
        EventSystem.current.RaycastAll(pointer, hits);
        return hits.Count > 0;
    }

    void CompleteExit()
    {
        ExitPostAndLocalizedSelection();
        ExitClumper();

        // Coming out of POST or CLUMPER editing back to plain group context is exactly the
        // same "which values are the sliders actually showing right now" problem SelectGroup
        // already solves - without this, the sliders keep showing whatever the modifier you
        // just exited left them at, not the group's own root values.
        if (viewer != null) viewer.SyncShapeSlidersToGroupRoot(viewer.currentGroupId);

        if (EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(null);
    }

    void Resolve()
    {
        if (viewer == null)
        {
            viewer = FindFirstObjectByType<ModelViewer>();
            if (viewer != null)
            {
                BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                TypeCacheViewer(flags);
            }
        }

        if (posts == null)
        {
            posts = FindFirstObjectByType<PostAffectorManager>();
            if (posts != null)
            {
                BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                postActiveIdField = typeof(PostAffectorManager).GetField("activeId", flags);
                postActiveGroupField = typeof(PostAffectorManager).GetField("activeGroup", flags);
            }
        }

        if (clumper == null)
        {
            clumper = FindFirstObjectByType<GroupClumperManager>();
            if (clumper != null)
            {
                BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                selectedClumperIdField = typeof(GroupClumperManager).GetField("selectedClumperId", flags);
                selectedClumperGroupField = typeof(GroupClumperManager).GetField("selectedGroup", flags);
                destroyClumperControlsMethod = typeof(GroupClumperManager).GetMethod("DestroyControls", flags);
            }
        }
    }

    void TypeCacheViewer(BindingFlags flags)
    {
        System.Type type = typeof(ModelViewer);
        isSelectionModeField = type.GetField("isSelectionMode", flags);
        hasSelectionField = type.GetField("hasSelectionHotspot", flags);
        hitPointField = type.GetField("selectionHitPoint", flags);
        hitNormalField = type.GetField("selectionHitNormal", flags);
        clearSelectionMethod = type.GetMethod("ClearSelectionHotspot", flags);
    }

    bool HasActiveModifierOrLocalizedSelection()
    {
        bool localizedSelection =
            (isSelectionModeField != null && isSelectionModeField.GetValue(viewer) is bool selectionMode && selectionMode) ||
            (hasSelectionField != null && hasSelectionField.GetValue(viewer) is bool hotspot && hotspot);
        bool postActive = posts != null && postActiveIdField != null &&
            postActiveIdField.GetValue(posts) is int postId && postId >= 0;
        bool clumpActive = clumper != null && selectedClumperIdField != null &&
            selectedClumperIdField.GetValue(clumper) is int clumpId && clumpId >= 0;
        return localizedSelection || postActive || clumpActive;
    }

    void ExitPostAndLocalizedSelection()
    {
        // POST selection and ModelViewer's legacy localized-selection state can get out of sync.
        // Clear each independently; do not make one conditional on the other still being active.
        if (posts != null && postActiveIdField != null &&
            postActiveIdField.GetValue(posts) is int id && id >= 0)
        {
            postActiveIdField.SetValue(posts, -1);
            postActiveGroupField?.SetValue(posts, -1);
        }

        if (clearSelectionMethod != null)
            clearSelectionMethod.Invoke(viewer, null);
        else
        {
            isSelectionModeField?.SetValue(viewer, false);
            hasSelectionField?.SetValue(viewer, false);
        }

        hitPointField?.SetValue(viewer, Vector3.zero);
        hitNormalField?.SetValue(viewer, Vector3.zero);
    }

    void ExitClumper()
    {
        if (clumper == null || selectedClumperIdField == null) return;
        if (!(selectedClumperIdField.GetValue(clumper) is int id) || id < 0) return;

        selectedClumperIdField.SetValue(clumper, -1);
        selectedClumperGroupField?.SetValue(clumper, -1);
        destroyClumperControlsMethod?.Invoke(clumper, null);

        GameObject scrollHost = GameObject.Find("ClumperScrollHost");
        if (scrollHost != null) Destroy(scrollHost);
    }
}
