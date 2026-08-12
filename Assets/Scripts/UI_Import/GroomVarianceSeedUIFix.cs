using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Repairs the compact seed controls after GroomVarianceController builds them.
// TMP_InputField expects a real text viewport; without one its caret/selection
// code can throw RectTransformUtility null-reference exceptions on interaction.
[DefaultExecutionOrder(1000)]
public class GroomVarianceSeedUIFix : MonoBehaviour
{
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        GameObject go = new GameObject("GroomVarianceSeedUIFix");
        DontDestroyOnLoad(go);
        go.AddComponent<GroomVarianceSeedUIFix>();
    }

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + 0.1f;

        TMP_InputField[] fields = FindObjectsByType<TMP_InputField>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Where(f => f.gameObject.name == "SeedInput")
            .ToArray();

        foreach (TMP_InputField field in fields)
            RepairSeedField(field);

        Button[] randomButtons = FindObjectsByType<Button>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Where(b => b.gameObject.name == "RButton" && b.transform.parent != null && b.transform.parent.name.EndsWith("_VarianceRow"))
            .ToArray();

        foreach (Button button in randomButtons)
            StyleRandomButton(button);

        RectTransform[] rows = FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Where(r => r.name.EndsWith("_VarianceRow"))
            .ToArray();

        foreach (RectTransform row in rows)
        {
            HorizontalLayoutGroup layout = row.GetComponent<HorizontalLayoutGroup>();
            if (layout == null) continue;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.padding = new RectOffset(4, 4, 2, 2);
            layout.spacing = 5f;
        }
    }

    void RepairSeedField(TMP_InputField input)
    {
        if (input == null) return;

        Image background = input.GetComponent<Image>();
        if (background == null) background = input.gameObject.AddComponent<Image>();

        RectTransform root = input.transform as RectTransform;
        if (root == null) return;
        root.sizeDelta = new Vector2(root.sizeDelta.x, 24f);

        RectTransform viewport = input.textViewport;
        if (viewport == null)
        {
            Transform existing = input.transform.Find("Text Area");
            GameObject viewportGO;
            if (existing != null)
            {
                viewportGO = existing.gameObject;
                viewport = viewportGO.GetComponent<RectTransform>();
            }
            else
            {
                viewportGO = new GameObject("Text Area", typeof(RectTransform), typeof(RectMask2D));
                viewportGO.transform.SetParent(input.transform, false);
                viewport = viewportGO.GetComponent<RectTransform>();
            }

            viewport.anchorMin = Vector2.zero;
            viewport.anchorMax = Vector2.one;
            viewport.offsetMin = new Vector2(4f, 1f);
            viewport.offsetMax = new Vector2(-4f, -1f);
            input.textViewport = viewport;
        }

        TextMeshProUGUI text = input.textComponent;
        if (text == null)
        {
            text = input.GetComponentInChildren<TextMeshProUGUI>(true);
            input.textComponent = text;
        }

        if (text != null)
        {
            if (text.transform.parent != viewport)
                text.transform.SetParent(viewport, false);

            RectTransform textRect = text.rectTransform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            text.alignment = TextAlignmentOptions.Center;
            text.enableWordWrapping = false;
            text.raycastTarget = false;
        }

        input.targetGraphic = background;
        input.contentType = TMP_InputField.ContentType.IntegerNumber;
        input.lineType = TMP_InputField.LineType.SingleLine;
        input.characterLimit = 10;
        input.caretWidth = 2;
        input.selectionColor = new Color(0.25f, 0.65f, 1f, 0.45f);
        input.transition = Selectable.Transition.ColorTint;

        ColorBlock colors = input.colors;
        colors.normalColor = new Color(0.12f, 0.12f, 0.12f, 1f);
        colors.highlightedColor = new Color(0.24f, 0.30f, 0.22f, 1f);
        colors.selectedColor = new Color(0.20f, 0.38f, 0.24f, 1f);
        colors.pressedColor = new Color(0.16f, 0.28f, 0.18f, 1f);
        colors.disabledColor = new Color(0.08f, 0.08f, 0.08f, 0.6f);
        colors.colorMultiplier = 1f;
        colors.fadeDuration = 0.08f;
        input.colors = colors;

        background.color = colors.normalColor;
    }

    void StyleRandomButton(Button button)
    {
        if (button == null) return;

        RectTransform rect = button.transform as RectTransform;
        if (rect != null) rect.sizeDelta = new Vector2(30f, 24f);

        Image image = button.GetComponent<Image>();
        if (image == null) image = button.gameObject.AddComponent<Image>();
        button.targetGraphic = image;
        button.transition = Selectable.Transition.ColorTint;

        ColorBlock colors = button.colors;
        colors.normalColor = new Color(0.27f, 0.34f, 0.20f, 1f);
        colors.highlightedColor = new Color(0.42f, 0.62f, 0.28f, 1f);
        colors.selectedColor = new Color(0.36f, 0.54f, 0.24f, 1f);
        colors.pressedColor = new Color(0.20f, 0.44f, 0.18f, 1f);
        colors.disabledColor = new Color(0.12f, 0.14f, 0.10f, 0.6f);
        colors.colorMultiplier = 1f;
        colors.fadeDuration = 0.08f;
        button.colors = colors;

        image.color = colors.normalColor;

        TextMeshProUGUI label = button.GetComponentInChildren<TextMeshProUGUI>(true);
        if (label != null)
        {
            label.alignment = TextAlignmentOptions.Center;
            label.raycastTarget = false;
        }
    }
}
