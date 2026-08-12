using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// Runtime interaction repairs for compact variance controls.
[DefaultExecutionOrder(1500)]
public class RuntimeToolInteractionFixes : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<RuntimeToolInteractionFixes>() != null) return;
        GameObject go = new GameObject("RuntimeToolInteractionFixes");
        DontDestroyOnLoad(go);
        go.AddComponent<RuntimeToolInteractionFixes>();
    }

    void Update()
    {
        HandleSeedControls();
    }

    void HandleSeedControls()
    {
        if (Mouse.current == null) return;
        Vector2 mouse = Mouse.current.position.ReadValue();
        bool pressed = Mouse.current.leftButton.wasPressedThisFrame;

        TMP_InputField[] fields = FindObjectsByType<TMP_InputField>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
            .Where(f => f.gameObject.name == "SeedInput").ToArray();
        foreach (TMP_InputField field in fields)
        {
            RectTransform rect = field.transform as RectTransform;
            if (rect == null) continue;
            bool hover = ScreenRectContains(rect, mouse);
            Image image = field.GetComponent<Image>();
            if (image != null)
                image.color = field.isFocused ? new Color(.20f,.38f,.24f,1f) : hover ? new Color(.36f,.46f,.30f,1f) : new Color(.12f,.12f,.12f,1f);

            if (pressed && hover && field.interactable)
            {
                field.enabled = true;
                if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(field.gameObject);
                field.Select();
                field.ActivateInputField();
                field.MoveTextEnd(false);
            }
        }

        Button[] randomButtons = FindObjectsByType<Button>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
            .Where(b => b.gameObject.name == "RButton" && b.transform.parent != null && b.transform.parent.name.EndsWith("_VarianceRow")).ToArray();
        foreach (Button button in randomButtons)
        {
            RectTransform rect = button.transform as RectTransform;
            if (rect == null) continue;
            bool hover = ScreenRectContains(rect, mouse);
            Image image = button.GetComponent<Image>();
            if (image != null)
                image.color = hover && button.interactable ? new Color(.52f,.72f,.34f,1f) : new Color(.27f,.34f,.20f,1f);

            if (pressed && hover && button.interactable)
            {
                button.onClick.Invoke();
                if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(button.gameObject);
            }
        }
    }

    bool ScreenRectContains(RectTransform rect, Vector2 screenPoint)
    {
        Canvas canvas = rect.GetComponentInParent<Canvas>();
        Camera cam = null;
        if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
            cam = canvas.worldCamera != null ? canvas.worldCamera : Camera.main;

        Vector3[] corners = new Vector3[4];
        rect.GetWorldCorners(corners);
        Vector2 p0 = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
        Vector2 p1 = RectTransformUtility.WorldToScreenPoint(cam, corners[1]);
        Vector2 p2 = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);
        Vector2 p3 = RectTransformUtility.WorldToScreenPoint(cam, corners[3]);
        float minX = Mathf.Min(p0.x,p1.x,p2.x,p3.x);
        float maxX = Mathf.Max(p0.x,p1.x,p2.x,p3.x);
        float minY = Mathf.Min(p0.y,p1.y,p2.y,p3.y);
        float maxY = Mathf.Max(p0.y,p1.y,p2.y,p3.y);
        const float pad = 2f;
        return screenPoint.x >= minX-pad && screenPoint.x <= maxX+pad && screenPoint.y >= minY-pad && screenPoint.y <= maxY+pad;
    }
}
