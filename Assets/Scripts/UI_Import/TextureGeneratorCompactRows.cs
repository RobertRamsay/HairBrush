using UnityEngine;
using UnityEngine.UI;

[DefaultExecutionOrder(9310)]
public class TextureGeneratorCompactRows : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<TextureGeneratorCompactRows>() != null) return;
        GameObject go = new GameObject("TextureGeneratorCompactRows");
        DontDestroyOnLoad(go);
        go.AddComponent<TextureGeneratorCompactRows>();
    }

    void Update()
    {
        foreach (Transform t in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (t == null || t.name != "TextureGeneratorControlsPanel") continue;

            VerticalLayoutGroup layout = t.GetComponent<VerticalLayoutGroup>();
            if (layout != null)
            {
                layout.spacing = 4f;
                layout.childForceExpandHeight = false;
            }

            foreach (Transform child in t)
            {
                LayoutElement le = child.GetComponent<LayoutElement>();
                if (le == null) continue;

                if (child.name.EndsWith("_Row") || child.name == "ClusterSeedRow")
                {
                    le.minHeight = 36f;
                    le.preferredHeight = 38f;
                    le.flexibleHeight = 0f;
                }
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(t.GetComponent<RectTransform>());
            break;
        }
    }
}
