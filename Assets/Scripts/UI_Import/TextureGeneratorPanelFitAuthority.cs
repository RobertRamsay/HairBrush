using UnityEngine;
using UnityEngine.UI;

// Makes the Texture Generator obey its authored fixed row heights.
// The panel originally used childControlHeight=false, so RectTransform defaults stretched
// rows far beyond their LayoutElement preferred heights and pushed Generate below 16:9.
[DefaultExecutionOrder(9400)]
public class TextureGeneratorPanelFitAuthority : MonoBehaviour
{
    private GameObject fittedPanel;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<TextureGeneratorPanelFitAuthority>() != null) return;
        GameObject go = new GameObject("TextureGeneratorPanelFitAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<TextureGeneratorPanelFitAuthority>();
    }

    void Update()
    {
        GameObject panel = FindPanel();
        if (panel == null) return;

        if (panel != fittedPanel)
        {
            fittedPanel = panel;
            FitPanel(panel);
        }

        // Re-assert after other runtime UI helpers have had their turn.
        if (panel.activeInHierarchy)
            FitPanel(panel);
    }

    static GameObject FindPanel()
    {
        foreach (Transform t in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (t != null && t.name == "TextureGeneratorControlsPanel")
                return t.gameObject;
        return null;
    }

    static void FitPanel(GameObject panel)
    {
        VerticalLayoutGroup layout = panel.GetComponent<VerticalLayoutGroup>();
        if (layout == null) return;

        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        layout.spacing = 3f;
        layout.padding = new RectOffset(12, 12, 8, 8);

        SetHeight(panel.transform.Find("PanelTabRow"), 38f);
        SetHeight(panel.transform.Find("ACTIVE CLUSTER CONTROLS"), 22f);
        SetHeight(panel.transform.Find("PlacementStatus"), 30f);
        SetHeight(panel.transform.Find("REPOSITION - CLICK ATLASButton"), 32f);
        SetHeight(panel.transform.Find("ClusterSeedRow"), 30f);
        SetHeight(panel.transform.Find("GENERATE / UPDATEButton"), 40f);

        foreach (Transform child in panel.transform)
        {
            if (child == null) continue;
            if (child.name.EndsWith("_Row"))
                SetHeight(child, 36f);
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(panel.GetComponent<RectTransform>());
    }

    static void SetHeight(Transform target, float height)
    {
        if (target == null) return;

        LayoutElement le = target.GetComponent<LayoutElement>();
        if (le == null) le = target.gameObject.AddComponent<LayoutElement>();
        le.minHeight = height;
        le.preferredHeight = height;
        le.flexibleHeight = 0f;

        RectTransform rect = target as RectTransform;
        if (rect != null)
            rect.sizeDelta = new Vector2(rect.sizeDelta.x, height);
    }
}
