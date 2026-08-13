using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

// While a persistent POST is selected, SPACE + left-click on the model relocates
// that POST without changing any of its authored settings. This runs before
// ModelViewer so the click cannot fall through into normal hair-card placement.
[DefaultExecutionOrder(-2000)]
public class PostAffectorSurfaceMoveUX : MonoBehaviour
{
    private ModelViewer viewer;
    private PostAffectorManager posts;
    private FieldInfo activeIdField;
    private FieldInfo hasSelectionField;
    private FieldInfo hitPointField;
    private FieldInfo hitNormalField;
    private FieldInfo isSelectionModeField;
    private bool wasEditingPost;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<PostAffectorSurfaceMoveUX>() != null) return;
        GameObject go = new GameObject("PostAffectorSurfaceMoveUX");
        DontDestroyOnLoad(go);
        go.AddComponent<PostAffectorSurfaceMoveUX>();
    }

    void Update()
    {
        Resolve();
        if (viewer == null || posts == null) return;

        bool editingPost = IsEditingPost();
        if (editingPost)
        {
            // A POST selected from the left panel can have a hotspot without passing
            // through ModelViewer.EnterSelectionMode(). Keep its placement guard active
            // so SPACE+click can never create a hair card underneath the move gesture.
            isSelectionModeField?.SetValue(viewer, true);
            TryMoveActivePost();
        }
        else if (wasEditingPost)
        {
            // Returning to the group header restores normal card placement immediately.
            isSelectionModeField?.SetValue(viewer, false);
        }

        wasEditingPost = editingPost;
    }

    void Resolve()
    {
        if (viewer == null)
        {
            viewer = FindFirstObjectByType<ModelViewer>();
            if (viewer != null)
            {
                BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                System.Type type = typeof(ModelViewer);
                hasSelectionField = type.GetField("hasSelectionHotspot", flags);
                hitPointField = type.GetField("selectionHitPoint", flags);
                hitNormalField = type.GetField("selectionHitNormal", flags);
                isSelectionModeField = type.GetField("isSelectionMode", flags);
            }
        }

        if (posts == null)
        {
            posts = FindFirstObjectByType<PostAffectorManager>();
            if (posts != null)
                activeIdField = typeof(PostAffectorManager).GetField("activeId", BindingFlags.Instance | BindingFlags.NonPublic);
        }
    }

    bool IsEditingPost()
    {
        if (activeIdField == null || hasSelectionField == null) return false;
        int activeId = activeIdField.GetValue(posts) is int id ? id : -1;
        bool hasSelection = hasSelectionField.GetValue(viewer) is bool selected && selected;
        return activeId >= 0 && hasSelection;
    }

    void TryMoveActivePost()
    {
        if (Mouse.current == null || Keyboard.current == null) return;
        if (!Keyboard.current.spaceKey.isPressed || !Mouse.current.leftButton.wasPressedThisFrame) return;
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;
        if (viewer.mainCamera == null) return;

        Ray ray = viewer.mainCamera.ScreenPointToRay(Mouse.current.position.ReadValue());
        if (!Physics.Raycast(ray, out RaycastHit hit, Mathf.Infinity, ~0, QueryTriggerInteraction.Ignore))
            return;

        Vector3 normal = hit.normal.sqrMagnitude > .000001f ? hit.normal.normalized : Vector3.up;
        hitPointField?.SetValue(viewer, hit.point);
        hitNormalField?.SetValue(viewer, normal);

        // Keep the visible selection/highlight in step with the moved POST immediately.
        SelectionBrushScaleTuning tuning = FindFirstObjectByType<SelectionBrushScaleTuning>();
        if (tuning != null)
            tuning.RecomputeWeights(hit.point, viewer.brushRadius, viewer.brushFalloffDistance);
    }
}
