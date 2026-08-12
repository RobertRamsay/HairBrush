using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Prevents the runtime-created SAVE PROJ button from retaining EventSystem focus
/// while Unity's native SaveFilePanel is open. Without this, pressing Enter to
/// accept a new filename can be delivered back to the still-selected Button when
/// the native dialog closes, causing SaveProject() to run a second time.
/// </summary>
public class SaveProjectFocusGuard : MonoBehaviour, IPointerDownHandler
{
    private static SaveProjectFocusGuardWatcher watcher;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void InstallWatcher()
    {
        if (watcher != null) return;

        GameObject watcherObject = new GameObject("SaveProjectFocusGuardWatcher");
        watcherObject.hideFlags = HideFlags.HideAndDontSave;
        Object.DontDestroyOnLoad(watcherObject);
        watcher = watcherObject.AddComponent<SaveProjectFocusGuardWatcher>();
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        if (EventSystem.current != null)
        {
            EventSystem.current.SetSelectedGameObject(null);
        }
    }
}

public class SaveProjectFocusGuardWatcher : MonoBehaviour
{
    private GameObject guardedButton;

    private void Update()
    {
        if (guardedButton != null) return;

        GameObject saveButton = GameObject.Find("SaveProjectButton");
        if (saveButton == null) return;

        if (saveButton.GetComponent<SaveProjectFocusGuard>() == null)
        {
            saveButton.AddComponent<SaveProjectFocusGuard>();
        }

        guardedButton = saveButton;
    }
}
