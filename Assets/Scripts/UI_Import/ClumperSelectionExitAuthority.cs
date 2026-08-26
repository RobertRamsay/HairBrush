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
    private FieldInfo selectedClumperIdField;
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
        if (clumper == null || selectedClumperIdField == null) return;

        int activeId = selectedClumperIdField.GetValue(clumper) is int id ? id : -1;
        if (activeId < 0) return;

        // Ctrl+Click is POST authoring, regardless of which modifier currently owns the
        // right panel. Exit CLUMPER before ModelViewer/PostAffectorManager process the click.
        //
        // CTRL+SHIFT is excluded because that is the group pick, which is not POST authoring and
        // has no business closing the CLUMPER the user is working on. Picking a group is how you
        // check what you are looking at; losing the modifier panel for it would be a punishment.
        // ALT is excluded for the same reason, in both modes. Under MAYA-NAV, CTRL+ALT+LMB is a
        // camera gesture - Maya's own - so with CTRL resting under the hand the panel would close
        // on every swing. With MAYA-NAV off it is a click that is supposed to do nothing at all,
        // and closing the panel is not nothing.
        if (Keyboard.current != null && Mouse.current != null &&
            Keyboard.current.ctrlKey.isPressed && !Keyboard.current.shiftKey.isPressed &&
            !MayaNavigationAuthority.AltReserved &&
            Mouse.current.leftButton.wasPressedThisFrame &&
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

        // Group-root and POST rows explicitly take ownership away from CLUMPER.
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
        selectedClumperIdField = typeof(GroupClumperManager).GetField("selectedClumperId", flags);
        destroyControlsMethod = typeof(GroupClumperManager).GetMethod("DestroyControls", flags);
    }

    void ExitClumper()
    {
        selectedClumperIdField?.SetValue(clumper, -1);
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
