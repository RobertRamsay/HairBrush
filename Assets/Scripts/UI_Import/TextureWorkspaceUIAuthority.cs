using UnityEngine;
using UnityEngine.UI;
using TMPro;

// Makes the runtime Texture/Material workspace deterministic for pointer input.
// These panels are created dynamically alongside other UI, so visual order can otherwise
// disagree with EventSystem raycast order and make buttons intermittently unclickable.
[DefaultExecutionOrder(12000)]
public class TextureWorkspaceUIAuthority : MonoBehaviour
{
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Spawn()
    {
        if (FindFirstObjectByType<TextureWorkspaceUIAuthority>() != null) return;
        GameObject go = new GameObject("TextureWorkspaceUIAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<TextureWorkspaceUIAuthority>();
    }

    private void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + 0.1f;

        GameObject texture = FindNamed("TextureEditorPanel");
        if (texture != null) Enforce(texture, 200);

        GameObject material = FindNamed("TextureMaterialPanel");
        if (material != null) Enforce(material, 210);
    }

    private static void Enforce(GameObject panel, int sortingOrder)
    {
        Canvas canvas = panel.GetComponent<Canvas>();
        if (canvas == null) canvas = panel.AddComponent<Canvas>();
        canvas.overrideSorting = true;
        canvas.sortingOrder = sortingOrder;

        if (panel.GetComponent<GraphicRaycaster>() == null)
            panel.AddComponent<GraphicRaycaster>();

        // The large panel image is visual only. It must never eat pointer events.
        Image rootImage = panel.GetComponent<Image>();
        if (rootImage != null) rootImage.raycastTarget = false;

        foreach (Button button in panel.GetComponentsInChildren<Button>(true))
        {
            if (button == null) continue;
            button.interactable = true;
            Image buttonImage = button.GetComponent<Image>();
            if (buttonImage != null) buttonImage.raycastTarget = true;
            button.transform.SetAsLastSibling();
        }

        // Labels are never intended to receive clicks; letting them raycast can produce
        // inconsistent hit ordering over the actual Button target.
        foreach (TextMeshProUGUI text in panel.GetComponentsInChildren<TextMeshProUGUI>(true))
            if (text != null) text.raycastTarget = false;

        foreach (Text text in panel.GetComponentsInChildren<Text>(true))
            if (text != null) text.raycastTarget = false;
    }

    private static GameObject FindNamed(string name)
    {
        foreach (Transform t in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (t != null && t.name == name) return t.gameObject;
        return null;
    }
}
