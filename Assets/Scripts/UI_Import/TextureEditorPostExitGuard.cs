using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;

// Texture Editor is a separate authoring workspace, never a continuation of a local
// grooming/POST edit. Entering it forcefully closes every shared POST-selection state so
// returning to Groom Mode always resumes at the group-root context.
[DefaultExecutionOrder(9150)]
public class TextureEditorPostExitGuard : MonoBehaviour
{
    private ModelViewer viewer;
    private PostAffectorManager posts;
    private GroomRootStateAuthority roots;
    private GameObject texturePanel;

    private FieldInfo activeIdField;
    private FieldInfo activeGroupField;
    private FieldInfo nextUIScanField;
    private FieldInfo hasSelectionField;
    private FieldInfo isSelectionModeField;
    private FieldInfo hitPointField;
    private FieldInfo hitNormalField;
    private MethodInfo clearSelectionMethod;
    private MethodInfo rebuildPostRowsMethod;

    private bool wasTextureActive;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<TextureEditorPostExitGuard>() != null) return;
        GameObject go = new GameObject("TextureEditorPostExitGuard");
        DontDestroyOnLoad(go);
        go.AddComponent<TextureEditorPostExitGuard>();
    }

    void Update()
    {
        Resolve();
        if (viewer == null) return;

        if (texturePanel == null)
            texturePanel = FindTexturePanel();

        bool textureActive = texturePanel != null && texturePanel.activeInHierarchy;
        if (textureActive && (!wasTextureActive || HasAnyLocalEditState()))
            FullyExitLocalEdit();

        wasTextureActive = textureActive;
    }

    void Resolve()
    {
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        if (viewer == null)
        {
            viewer = FindFirstObjectByType<ModelViewer>();
            if (viewer != null)
            {
                System.Type type = typeof(ModelViewer);
                hasSelectionField = type.GetField("hasSelectionHotspot", flags);
                isSelectionModeField = type.GetField("isSelectionMode", flags);
                hitPointField = type.GetField("selectionHitPoint", flags);
                hitNormalField = type.GetField("selectionHitNormal", flags);
                clearSelectionMethod = type.GetMethod("ClearSelectionHotspot", flags);
            }
        }

        if (posts == null)
        {
            posts = FindFirstObjectByType<PostAffectorManager>();
            if (posts != null)
            {
                System.Type type = typeof(PostAffectorManager);
                activeIdField = type.GetField("activeId", flags);
                activeGroupField = type.GetField("activeGroup", flags);
                nextUIScanField = type.GetField("nextUIScan", flags);
                rebuildPostRowsMethod = type.GetMethod("RebuildGroupRows", flags);
            }
        }

        if (roots == null)
            roots = FindFirstObjectByType<GroomRootStateAuthority>();
    }

    GameObject FindTexturePanel()
    {
        foreach (Transform t in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (t != null && t.name == "TextureEditorPanel") return t.gameObject;
        return null;
    }

    bool HasAnyLocalEditState()
    {
        if (posts != null && activeIdField != null && activeIdField.GetValue(posts) is int activeId && activeId >= 0)
            return true;
        if (hasSelectionField != null && hasSelectionField.GetValue(viewer) is bool selected && selected)
            return true;
        if (isSelectionModeField != null && isSelectionModeField.GetValue(viewer) is bool selectionMode && selectionMode)
            return true;
        return false;
    }

    void FullyExitLocalEdit()
    {
        int previousGroup = viewer.currentGroupId;
        if (posts != null && activeGroupField != null && activeGroupField.GetValue(posts) is int activeGroup && activeGroup >= 0)
            previousGroup = activeGroup;

        // Clear the POST manager's own edit context first. Clearing only ModelViewer's
        // hotspot leaves activeId alive and makes the yellow/local mode appear to survive.
        if (posts != null)
        {
            activeIdField?.SetValue(posts, -1);
            activeGroupField?.SetValue(posts, -1);
            nextUIScanField?.SetValue(posts, 0f);
        }

        // Use ModelViewer's normal exit path because it also restores panel colour,
        // destroys local Falloff/Weight rows, clears card weights, and exits selection mode.
        if (clearSelectionMethod != null)
            clearSelectionMethod.Invoke(viewer, null);
        else
        {
            hasSelectionField?.SetValue(viewer, false);
            isSelectionModeField?.SetValue(viewer, false);
            foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
                if (card != null) card.SetSelectionWeight(0f);
        }

        hitPointField?.SetValue(viewer, Vector3.zero);
        hitNormalField?.SetValue(viewer, Vector3.zero);
        viewer.lastPlacedCard = null;

        // Restore authored group-root control state now; PostRootContextRestore will also
        // synchronize the visible sliders on its next pass before Groom Mode is used again.
        roots?.RestoreRootToViewer(viewer.currentGroupId);

        // Recreate the previous POST row in its normal, unselected appearance.
        if (posts != null && previousGroup >= 0)
            rebuildPostRowsMethod?.Invoke(posts, new object[] { previousGroup });

        if (EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(null);
    }
}
