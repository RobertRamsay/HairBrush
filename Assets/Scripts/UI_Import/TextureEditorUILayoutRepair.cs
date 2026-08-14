using UnityEngine;

// Keeps the Texture Editor navigation row at the very top. The UV workspace was
// originally looking for the old PanelTabRow name, so with the current ModeRow name it
// inserted itself at sibling zero and displaced the GROOM / Texture Editor buttons into
// the rectangle controls.
[DefaultExecutionOrder(9300)]
public class TextureEditorUILayoutRepair : MonoBehaviour
{
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<TextureEditorUILayoutRepair>() != null) return;
        GameObject go = new GameObject("TextureEditorUILayoutRepair");
        DontDestroyOnLoad(go);
        go.AddComponent<TextureEditorUILayoutRepair>();
    }

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + .25f;

        GameObject panel = FindNamed("TextureEditorPanel");
        if (panel == null) return;

        Transform modeRow = panel.transform.Find("ModeRow");
        if (modeRow != null && modeRow.GetSiblingIndex() != 0)
            modeRow.SetSiblingIndex(0);

        Transform uvSection = panel.transform.Find("UVWorkspaceSection");
        if (uvSection != null)
        {
            int target = modeRow != null ? 1 : 0;
            if (uvSection.GetSiblingIndex() != target)
                uvSection.SetSiblingIndex(target);
        }
    }

    static GameObject FindNamed(string name)
    {
        foreach (Transform t in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (t != null && t.name == name) return t.gameObject;
        return null;
    }
}
