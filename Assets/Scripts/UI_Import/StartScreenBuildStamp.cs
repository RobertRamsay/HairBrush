using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Adds a small version/build stamp to the application's start/menu screen.
// Runtime-created so the scene does not need a manually wired reference.
public class StartScreenBuildStamp : MonoBehaviour
{
    private const string ObjectName = "HairBrushBuildStamp";
    private const float ScanInterval = 0.5f;

    // Authored as pixels at 1080p. The start screen's canvas is Constant Pixel Size, so a
    // fixed size is the same number of pixels at 4K as at 1080p - it would read as a quarter
    // of the size on a 4K display. Scaling by the canvas height against a 1080-high
    // reference keeps the stamp the same proportion of the screen at any resolution.
    private const float ReferenceHeight = 1080f;
    private const float FontPixels = 14f;
    private const float HeightPixels = 26f;
    private const float BottomInsetPixels = 10f;
    private const float SideInsetPixels = 24f;

    private float nextScan;
    private GameObject stampObject;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<StartScreenBuildStamp>() != null) return;

        GameObject go = new GameObject(nameof(StartScreenBuildStamp));
        DontDestroyOnLoad(go);
        go.AddComponent<StartScreenBuildStamp>();
    }

    void Update()
    {
        if (stampObject != null && stampObject.activeInHierarchy) return;
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + ScanInterval;

        TryAttachToStartScreen();
    }

    void TryAttachToStartScreen()
    {
        Canvas[] canvases = FindObjectsByType<Canvas>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        Canvas best = null;
        int bestScore = int.MinValue;

        foreach (Canvas canvas in canvases)
        {
            if (canvas == null || !canvas.isActiveAndEnabled) continue;

            // The start screen is the full-screen UI with the HairBrush branding/menu controls.
            // Score by common menu words rather than relying on one fragile scene object name.
            int score = 0;
            TextMeshProUGUI[] labels = canvas.GetComponentsInChildren<TextMeshProUGUI>(true);
            foreach (TextMeshProUGUI label in labels)
            {
                if (label == null) continue;
                string text = label.text ?? string.Empty;
                if (text.IndexOf("HAIRBRUSH", StringComparison.OrdinalIgnoreCase) >= 0) score += 5;
                if (text.IndexOf("NEW", StringComparison.OrdinalIgnoreCase) >= 0) score += 2;
                if (text.IndexOf("LOAD", StringComparison.OrdinalIgnoreCase) >= 0) score += 2;
                if (text.IndexOf("PROJECT", StringComparison.OrdinalIgnoreCase) >= 0) score += 1;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = canvas;
            }
        }

        // Avoid showing this on Groom/Material UI if no menu-like canvas is present.
        if (best == null || bestScore < 2) return;

        Transform existing = best.transform.Find(ObjectName);
        if (existing != null)
        {
            stampObject = existing.gameObject;
            return;
        }

        BuildStamp(best.transform);
    }

    void BuildStamp(Transform parent)
    {
        if (parent == null) return;

        stampObject = new GameObject(ObjectName, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        stampObject.transform.SetParent(parent, false);

        RectTransform rect = stampObject.GetComponent<RectTransform>();
        if (rect == null)
        {
            Destroy(stampObject);
            stampObject = null;
            return;
        }

        float unit = 1f;
        RectTransform canvasRect = parent as RectTransform;
        if (canvasRect == null) canvasRect = parent.GetComponentInParent<Canvas>()?.transform as RectTransform;
        if (canvasRect != null && canvasRect.rect.height > 1f) unit = canvasRect.rect.height / ReferenceHeight;

        rect.anchorMin = new Vector2(0f, 0f);
        rect.anchorMax = new Vector2(1f, 0f);
        rect.pivot = new Vector2(0.5f, 0f);
        rect.anchoredPosition = new Vector2(0f, BottomInsetPixels * unit);
        rect.sizeDelta = new Vector2(-SideInsetPixels * unit, HeightPixels * unit);

        TextMeshProUGUI text = stampObject.GetComponent<TextMeshProUGUI>();
        if (text == null)
        {
            Destroy(stampObject);
            stampObject = null;
            return;
        }

        text.text = BuildLabel();
        text.fontSize = FontPixels * unit;
        text.fontStyle = FontStyles.Normal;
        text.alignment = TextAlignmentOptions.Bottom;
        text.color = new Color(1f, 1f, 1f, 0.42f);
        text.raycastTarget = false;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Overflow;
    }

    static string BuildLabel()
    {
        string version = string.IsNullOrWhiteSpace(Application.version) ? "0.0.0" : Application.version;
        string mode = Application.isEditor ? "EDITOR" : "BUILD";

        // This is intentionally the run/build-visible date rather than a hard-coded release string.
        // Application.version remains the authoritative version number from Player Settings.
        string date = DateTime.Now.ToString("yyyy.MM.dd");
        return $"HAIRBRUSH BETA  •  v{version}  •  {mode}  {date}";
    }
}
