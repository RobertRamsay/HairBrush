using TMPro;
using UnityEngine;
using UnityEngine.UI;

[DefaultExecutionOrder(13000)]
public class RandomButtonStyleAuthority : MonoBehaviour
{
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<RandomButtonStyleAuthority>() != null) return;
        GameObject go = new GameObject("RandomButtonStyleAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<RandomButtonStyleAuthority>();
    }

    void LateUpdate()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + .1f;

        foreach (Button button in FindObjectsByType<Button>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (!IsRandomButton(button)) continue;
            Style(button);
        }
    }

    static bool IsRandomButton(Button button)
    {
        if (button == null) return false;
        if (button.gameObject.name == "RButton" || button.gameObject.name == "GroupUVRandomSeedButton") return true;
        TextMeshProUGUI label = button.GetComponentInChildren<TextMeshProUGUI>(true);
        return label != null && label.text.Trim() == "R";
    }

    static void Style(Button button)
    {
        RectTransform rect = button.transform as RectTransform;
        if (rect != null) rect.sizeDelta = new Vector2(46f, 30f);

        LayoutElement le = button.GetComponent<LayoutElement>();
        if (le != null)
        {
            le.minWidth = 42f;
            le.preferredWidth = 46f;
            le.minHeight = 28f;
            le.preferredHeight = 30f;
        }

        Image image = button.GetComponent<Image>();
        if (image == null) image = button.gameObject.AddComponent<Image>();
        image.raycastTarget = true;
        button.targetGraphic = image;
        button.transition = Selectable.Transition.ColorTint;

        ColorBlock colors = button.colors;
        colors.normalColor = new Color(.25f, .42f, .58f, 1f);
        colors.highlightedColor = new Color(.32f, .58f, .78f, 1f);
        colors.selectedColor = new Color(.30f, .52f, .70f, 1f);
        colors.pressedColor = new Color(.16f, .36f, .56f, 1f);
        colors.disabledColor = new Color(.16f, .20f, .24f, .65f);
        colors.colorMultiplier = 1f;
        colors.fadeDuration = .06f;
        button.colors = colors;
        image.color = colors.normalColor;

        TextMeshProUGUI label = button.GetComponentInChildren<TextMeshProUGUI>(true);
        if (label != null)
        {
            label.text = "R";
            label.fontSize = Mathf.Max(label.fontSize, 14f);
            label.fontStyle = FontStyles.Bold;
            label.alignment = TextAlignmentOptions.Center;
            label.raycastTarget = false;
        }
    }
}
