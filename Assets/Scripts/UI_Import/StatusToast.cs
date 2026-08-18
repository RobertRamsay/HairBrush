using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Minimal on-screen status/error toast. Several I/O failures in this project only ever reached
// Debug.LogError, which is invisible while an actual Player build is running - there's no console
// to check, and the Player.log file isn't something most people know to go find mid-session. This
// puts the same message on screen instead, so failures are diagnosable without leaving the app.
public static class StatusToast
{
    private static StatusToastAuthority instance;

    public static void Show(string message, bool isError = false)
    {
        Show(message, isError, isError ? 6f : 3f);
    }

    public static void Show(string message, bool isError, float durationSeconds)
    {
        Debug.Log((isError ? "[HairBrush ERROR] " : "[HairBrush] ") + message);
        Ensure();
        instance.Display(message, isError, durationSeconds);
    }

    private static void Ensure()
    {
        if (instance != null) return;
        instance = Object.FindFirstObjectByType<StatusToastAuthority>();
        if (instance != null) return;
        GameObject go = new GameObject("StatusToastAuthority");
        Object.DontDestroyOnLoad(go);
        instance = go.AddComponent<StatusToastAuthority>();
    }
}

public class StatusToastAuthority : MonoBehaviour
{
    private Canvas canvas;
    private TextMeshProUGUI label;
    private Image background;
    private float hideAt;

    void Awake()
    {
        BuildUI();
    }

    void Update()
    {
        if (canvas == null) return;
        if (canvas.gameObject.activeSelf && Time.unscaledTime >= hideAt)
            canvas.gameObject.SetActive(false);
    }

    public void Display(string message, bool isError, float durationSeconds)
    {
        if (canvas == null) BuildUI();
        label.text = message;
        background.color = isError ? new Color(.55f, .16f, .16f, .92f) : new Color(.14f, .30f, .32f, .92f);
        canvas.gameObject.SetActive(true);
        hideAt = Time.unscaledTime + Mathf.Max(0.5f, durationSeconds);
    }

    void BuildUI()
    {
        GameObject canvasGO = new GameObject("StatusToastCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasGO.transform.SetParent(transform, false);
        canvas = canvasGO.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 5000;
        CanvasScaler scaler = canvasGO.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);

        GameObject panelGO = new GameObject("Panel", typeof(RectTransform), typeof(Image));
        panelGO.transform.SetParent(canvasGO.transform, false);
        RectTransform panelRect = panelGO.GetComponent<RectTransform>();
        panelRect.anchorMin = new Vector2(.5f, 0f);
        panelRect.anchorMax = new Vector2(.5f, 0f);
        panelRect.pivot = new Vector2(.5f, 0f);
        panelRect.anchoredPosition = new Vector2(0f, 40f);
        panelRect.sizeDelta = new Vector2(760f, 56f);
        background = panelGO.GetComponent<Image>();
        background.color = new Color(.14f, .30f, .32f, .92f);

        GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textGO.transform.SetParent(panelGO.transform, false);
        RectTransform textRect = textGO.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(16f, 4f);
        textRect.offsetMax = new Vector2(-16f, -4f);
        label = textGO.GetComponent<TextMeshProUGUI>();
        label.alignment = TextAlignmentOptions.Center;
        label.fontSize = 20f;
        label.color = Color.white;
        label.enableWordWrapping = true;

        canvasGO.SetActive(false);
    }
}
