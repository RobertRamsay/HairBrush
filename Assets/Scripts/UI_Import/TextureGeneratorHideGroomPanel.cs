using UnityEngine;

[DefaultExecutionOrder(9320)]
public class TextureGeneratorHideGroomPanel : MonoBehaviour
{
    private GameObject groupPanel;
    private bool previousActive;
    private bool captured;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<TextureGeneratorHideGroomPanel>() != null) return;
        GameObject go = new GameObject("TextureGeneratorHideGroomPanel");
        DontDestroyOnLoad(go);
        go.AddComponent<TextureGeneratorHideGroomPanel>();
    }

    void Update()
    {
        GameObject generator = FindNamed("TextureGeneratorControlsPanel");
        bool active = generator != null && generator.activeInHierarchy;

        if (active)
        {
            if (groupPanel == null) groupPanel = FindNamed("GroupManagerPanel");
            if (groupPanel != null)
            {
                if (!captured)
                {
                    previousActive = groupPanel.activeSelf;
                    captured = true;
                }
                groupPanel.SetActive(false);
            }
        }
        else if (captured)
        {
            if (groupPanel != null) groupPanel.SetActive(previousActive);
            captured = false;
        }
    }

    static GameObject FindNamed(string objectName)
    {
        foreach (Transform t in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (t != null && t.name == objectName) return t.gameObject;
        return null;
    }
}
