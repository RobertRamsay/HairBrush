using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Keeps the runtime-added QUIT action visually identical to the existing
// LOAD MODEL / LOAD PROJECT menu buttons instead of inheriting a zero-width layout.
[DefaultExecutionOrder(2000)]
public class MenuQuitVisualFix : MonoBehaviour
{
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        GameObject go = new GameObject("MenuQuitVisualFix");
        DontDestroyOnLoad(go);
        go.AddComponent<MenuQuitVisualFix>();
    }

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + 0.2f;

        ModelViewer viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer == null || viewer.uiContainer == null || viewer.loadProjectButton == null) return;

        Transform quitTransform = viewer.uiContainer.transform.Find("QuitButton_Runtime");
        if (quitTransform == null) return;

        RectTransform quitRect = quitTransform as RectTransform;
        RectTransform referenceRect = viewer.loadProjectButton.transform as RectTransform;
        if (quitRect == null || referenceRect == null) return;

        // Copy the reference button's actual layout footprint. The menu container
        // does not automatically give runtime children a useful width.
        quitRect.anchorMin = referenceRect.anchorMin;
        quitRect.anchorMax = referenceRect.anchorMax;
        quitRect.pivot = referenceRect.pivot;
        quitRect.sizeDelta = referenceRect.sizeDelta;
        quitRect.localScale = referenceRect.localScale;

        LayoutElement referenceLayout = viewer.loadProjectButton.GetComponent<LayoutElement>();
        LayoutElement quitLayout = quitTransform.GetComponent<LayoutElement>();
        if (quitLayout == null) quitLayout = quitTransform.gameObject.AddComponent<LayoutElement>();

        float referenceWidth = referenceRect.rect.width;
        float referenceHeight = referenceRect.rect.height;
        quitLayout.minWidth = referenceLayout != null && referenceLayout.minWidth >= 0f ? referenceLayout.minWidth : referenceWidth;
        quitLayout.preferredWidth = referenceLayout != null && referenceLayout.preferredWidth >= 0f ? referenceLayout.preferredWidth : referenceWidth;
        quitLayout.flexibleWidth = referenceLayout != null ? referenceLayout.flexibleWidth : 0f;
        quitLayout.minHeight = referenceLayout != null && referenceLayout.minHeight >= 0f ? referenceLayout.minHeight : referenceHeight;
        quitLayout.preferredHeight = referenceLayout != null && referenceLayout.preferredHeight >= 0f ? referenceLayout.preferredHeight : referenceHeight;
        quitLayout.flexibleHeight = referenceLayout != null ? referenceLayout.flexibleHeight : 0f;

        // Match the menu typography and explicitly disable wrapping so QUIT can
        // never collapse into the infamous Q/U/I/T tower again.
        TMP_Text referenceText = viewer.loadProjectButton.GetComponentInChildren<TMP_Text>(true);
        TMP_Text quitText = quitTransform.GetComponentInChildren<TMP_Text>(true);
        if (quitText != null)
        {
            quitText.text = "QUIT";
            quitText.textWrappingMode = TextWrappingModes.NoWrap;
            quitText.overflowMode = TextOverflowModes.Overflow;
            quitText.alignment = TextAlignmentOptions.Center;

            if (referenceText != null)
            {
                quitText.font = referenceText.font;
                quitText.fontSize = referenceText.fontSize;
                quitText.fontStyle = referenceText.fontStyle;
                quitText.characterSpacing = referenceText.characterSpacing;
            }

            RectTransform textRect = quitText.rectTransform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
        }

        // Put it immediately after LOAD PROJECT in menu order.
        quitTransform.SetSiblingIndex(viewer.loadProjectButton.transform.GetSiblingIndex() + 1);
    }
}
