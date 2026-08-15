using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;

// Selecting a modifier from any group first makes that group the active editor group.
// This keeps left-panel group state, right-panel controls, and modifier ownership aligned.
[DefaultExecutionOrder(5050)]
public class ModifierGroupSelectionAuthority : MonoBehaviour
{
    private ModelViewer viewer;
    private MethodInfo selectGroupMethod;
    private GameObject lastSelected;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<ModifierGroupSelectionAuthority>() != null) return;
        GameObject go = new GameObject("ModifierGroupSelectionAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<ModifierGroupSelectionAuthority>();
    }

    void Update()
    {
        Resolve();
        if (viewer == null || EventSystem.current == null) return;

        GameObject selected = EventSystem.current.currentSelectedGameObject;
        if (selected == null || selected == lastSelected) return;
        lastSelected = selected;

        Transform t = selected.transform;
        while (t != null)
        {
            if (TryParseGroup(t.name, "GroupClumper_", out int clumpGid) ||
                TryParsePostGroup(t.name, out clumpGid))
            {
                if (viewer.currentGroupId != clumpGid)
                {
                    if (selectGroupMethod != null) selectGroupMethod.Invoke(viewer, new object[] { clumpGid });
                    else viewer.currentGroupId = clumpGid;
                }
                return;
            }
            t = t.parent;
        }
    }

    void Resolve()
    {
        if (viewer != null) return;
        viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer != null)
            selectGroupMethod = typeof(ModelViewer).GetMethod("SelectGroup", BindingFlags.Instance | BindingFlags.NonPublic);
    }

    static bool TryParseGroup(string name, string prefix, out int gid)
    {
        gid = -1;
        if (string.IsNullOrEmpty(name) || !name.StartsWith(prefix)) return false;
        return int.TryParse(name.Substring(prefix.Length), out gid);
    }

    static bool TryParsePostGroup(string name, out int gid)
    {
        gid = -1;
        const string prefix = "PostAffector_";
        if (string.IsNullOrEmpty(name) || !name.StartsWith(prefix)) return false;
        string tail = name.Substring(prefix.Length);
        int underscore = tail.IndexOf('_');
        if (underscore <= 0) return false;
        return int.TryParse(tail.Substring(0, underscore), out gid);
    }
}
