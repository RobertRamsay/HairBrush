using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

// CLUMPER owns a dedicated right-panel view. Clicking a Group root or POST row, or
// Ctrl+Clicking the model to author a POST, immediately hands editing back to Group/POST.
[DefaultExecutionOrder(5260)]
public class ClumperSelectionExitAuthority : MonoBehaviour
{
    private GroupClumperManager clumper;
    private FieldInfo selectedGroupField;
    private MethodInfo destroyControlsMethod;
    private GameObject lastSelected;
    private int lastCtrlExitFrame = -1;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<ClumperSelectionExitAuthority>() != null) return;
        GameObject go = new GameObject("ClumperSelectionExitAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<ClumperSelectionExitAuthority>();
    }

    void Update()
    {
        Resolve();
        if (clumper == null || selectedGroupField == null) return;

        int active = selectedGroupField.GetValue(clumper) is int value ? value : -1;
        if (active < 0) return;

        // Ctrl+Click is POST authoring, regardless of which modifier currently owns the
        // right panel. Exit CLUMPER before ModelViewer/PostAffectorManager process the click.
        if (Keyboard.current != null && Mouse.current != null &&
            Keyboard.current.ctrlKey.isPressed && Mouse.current.leftButton.wasPressedThisFrame &&
            (EventSystem.current == null || !EventSystem.current.IsPointerOverGameObject()) &&
            lastCtrlExitFrame != Time.frameCount)
        {
            lastCtrlExitFrame = Time.frameCount;
            ExitClumper();
            return;
        }

        if (EventSystem.current == null) return;
        GameObject selected = EventSystem.current.currentSelectedGameObject;
        if (selected == null || selected == lastSelected) return;
        lastSelected = selected;

        if (Inside(selected.transform, "GroupClumper_") ||
            Inside(selected.transform, "ClumperControls") ||
            Inside(selected.transform, "ClumperScrollHost"))
            return;

        // Only editor-mode rows should switch ownership. Ordinary slider interaction and
        // unrelated UI should not unexpectedly close the clumper panel.
        if (Inside(selected.transform, "GroupItem_") || Inside(selected.transform, "PostAffector_"))
            ExitClumper();
    }

    void Resolve()
    {
        if (clumper != null) return;
        clumper = FindFirstObjectByType<GroupClumperManager>();
        if (clumper == null) return;

        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        selectedGroupField = typeof(GroupClumperManager).GetField("selectedGroup", flags);
        destroyControlsMethod = typeof(GroupClumperManager).GetMethod("DestroyControls", flags);
    }

    void ExitClumper()
    {
        selectedGroupField?.SetValue(clumper, -1);
        destroyControlsMethod?.Invoke(clumper, null);

        // The scroll helper owns this overlay separately from ClumperControls, so remove it
        // in the same transition instead of waiting for a later cleanup scan.
        GameObject host = GameObject.Find("ClumperScrollHost");
        if (host != null) Destroy(host);
    }

    static bool Inside(Transform t, string prefix)
    {
        while (t != null)
        {
            if (t.name.StartsWith(prefix)) return true;
            t = t.parent;
        }
        return false;
    }
}
