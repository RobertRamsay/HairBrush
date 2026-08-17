using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

// A plain left-click on genuinely empty 3D space is the universal modifier deselect gesture.
// It exits POST or CLUMPER without stealing clicks from UI, the model surface, or any of the
// modifier authoring gestures (Ctrl/Shift/Space/Tab).
[DefaultExecutionOrder(5255)]
public class ModifierEmptySpaceExitAuthority : MonoBehaviour
{
    private ModelViewer viewer;
    private PostAffectorManager posts;
    private GroupClumperManager clumper;

    private FieldInfo postActiveIdField;
    private FieldInfo postActiveGroupField;
    private FieldInfo hasSelectionField;
    private FieldInfo hitPointField;
    private FieldInfo hitNormalField;
    private MethodInfo clearSelectionMethod;

    private FieldInfo selectedClumperIdField;
    private FieldInfo selectedClumperGroupField;
    private MethodInfo destroyClumperControlsMethod;

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
        if (viewer == null || viewer.mainCamera == null || Mouse.current == null) return;
        if (!Mouse.current.leftButton.wasPressedThisFrame) return;
        if (!HasActiveModifier()) return;

        // "Click in space" is deliberately a plain click. Modified clicks continue to own
        // POST creation/move, CLUMPER creation/move, placement, and the other groom gestures.
        if (Keyboard.current != null &&
            (Keyboard.current.ctrlKey.isPressed ||
             Keyboard.current.shiftKey.isPressed ||
             Keyboard.current.altKey.isPressed ||
             Keyboard.current.spaceKey.isPressed ||
             Keyboard.current.tabKey.isPressed))
            return;

        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;

        Ray ray = viewer.mainCamera.ScreenPointToRay(Mouse.current.position.ReadValue());
        if (Physics.Raycast(ray, out _, Mathf.Infinity, ~0, QueryTriggerInteraction.Ignore)) return;

        ExitPost();
        ExitClumper();

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
        hasSelectionField = type.GetField("hasSelectionHotspot", flags);
        hitPointField = type.GetField("selectionHitPoint", flags);
        hitNormalField = type.GetField("selectionHitNormal", flags);
        clearSelectionMethod = type.GetMethod("ClearSelectionHotspot", flags);
    }

    bool HasActiveModifier()
    {
        bool postActive = posts != null && postActiveIdField != null &&
            postActiveIdField.GetValue(posts) is int postId && postId >= 0;
        bool clumpActive = clumper != null && selectedClumperIdField != null &&
            selectedClumperIdField.GetValue(clumper) is int clumpId && clumpId >= 0;
        return postActive || clumpActive;
    }

    void ExitPost()
    {
        if (posts == null || postActiveIdField == null) return;
        if (!(postActiveIdField.GetValue(posts) is int id) || id < 0) return;

        postActiveIdField.SetValue(posts, -1);
        postActiveGroupField?.SetValue(posts, -1);

        // Match ModelViewer's original Ctrl+click-in-empty-space teardown exactly. Clearing
        // only the hotspot leaves isSelectionMode enabled, which blocks normal hair placement
        // after a plain click exits POST editing.
        if (clearSelectionMethod != null)
            clearSelectionMethod.Invoke(viewer, null);
        else
            hasSelectionField?.SetValue(viewer, false);

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
