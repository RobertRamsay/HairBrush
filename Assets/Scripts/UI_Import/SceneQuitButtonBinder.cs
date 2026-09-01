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

        // THROTTLED, because the miss is the normal case. GameObject.Find walks every ACTIVE
        // object in the scene comparing names, and hair cards are root GameObjects - so at forty
        // thousand cards this was forty thousand name comparisons per frame, forever, for a
        // button that may not exist in the runtime scene at all.
        if (Time.unscaledTime < nextBindAttempt) return;
        nextBindAttempt = Time.unscaledTime + BindRetryInterval;

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

    // Retrying four times a second is ample for a binder waiting on a UI object.
    private const float BindRetryInterval = .25f;
    private float nextBindAttempt;
}
