using UnityEngine;
using UnityEngine.UI;
#if UNITY_EDITOR
using UnityEditor;
#endif

// Uses the authored QUIT button in SampleScene and prevents the runtime
// navigation helper from generating a duplicate fallback button.
[DefaultExecutionOrder(1700)]
public class SceneQuitButtonBinder : MonoBehaviour
{
    private bool bound;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        var go = new GameObject("SceneQuitButtonBinder");
        DontDestroyOnLoad(go);
        go.AddComponent<SceneQuitButtonBinder>();
    }

    void Update()
    {
        if (bound) return;

        GameObject quitGO = GameObject.Find("Button_Quit");
        if (quitGO == null) return;

        Button quit = quitGO.GetComponent<Button>();
        if (quit == null) return;

        // RuntimeNavigationProjectIO checks for this name before creating its
        // fallback button, so reuse the authored button under that name.
        quitGO.name = "QuitButton_Runtime";
        quit.onClick.RemoveAllListeners();
        quit.onClick.AddListener(QuitApplication);
        bound = true;
    }

    void QuitApplication()
    {
#if UNITY_EDITOR
        EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }
}
