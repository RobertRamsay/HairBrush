using UnityEngine;
using UnityEngine.UI;
using System;
using System.IO;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class MaterialEditorManager : MonoBehaviour
{
    private const string AlbedoProperty = "_Albedo";
    private const string NormalProperty = "_Normal";
    private const string OpacityProperty = "_OpacityMask";

    private ModelViewer viewer;
    private Material sourceMaterial;
    private Material runtimeMaterial;
    private GameObject panelGO;

    public void Init(ModelViewer modelViewer, Material materialTemplate)
    {
        viewer = modelViewer;
        sourceMaterial = materialTemplate;

        if (runtimeMaterial != null)
            Destroy(runtimeMaterial);

        if (sourceMaterial != null)
        {
            runtimeMaterial = new Material(sourceMaterial);
            runtimeMaterial.name = sourceMaterial.name + "_ProjectRuntime";
            ApplyRuntimeMaterial();
        }
    }

    public void TogglePanel(Transform parentCanvas)
    {
        if (panelGO == null)
            BuildUI(parentCanvas);
        else
            panelGO.SetActive(!panelGO.activeSelf);
    }

    public void ShowPanel(Transform parentCanvas)
    {
        if (panelGO == null)
            BuildUI(parentCanvas);
        panelGO.SetActive(true);
    }

    public void HidePanel()
    {
        if (panelGO != null)
            panelGO.SetActive(false);
    }

    private void BuildUI(Transform parentCanvas)
    {
        panelGO = new GameObject("MaterialEditorPanel", typeof(RectTransform), typeof(Image), typeof(GraphicRaycaster));
        panelGO.transform.SetParent(parentCanvas, false);

        RectTransform rect = panelGO.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(1f, 0f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(1f, 0.5f);
        rect.sizeDelta = new Vector2(430f, 0f);
        rect.anchoredPosition = new Vector2(-10f, 0f);

        panelGO.GetComponent<Image>().color = new Color(0.12f, 0.12f, 0.12f, 0.96f);

        VerticalLayoutGroup layout = panelGO.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(14, 14, 14, 14);
        layout.spacing = 12f;
        layout.childControlWidth = true;
        layout.childControlHeight = false;

        CreateHeader(panelGO.transform, "MATERIAL EDITOR");
        CreateSubLabel(panelGO.transform, runtimeMaterial != null ? runtimeMaterial.name : "No material loaded");

        CreateTextureSlot(panelGO.transform, "ALBEDO", AlbedoProperty, false);
        CreateTextureSlot(panelGO.transform, "NORMAL", NormalProperty, true);
        CreateTextureSlot(panelGO.transform, "OPACITY MASK", OpacityProperty, false);

        CreateActionButton(panelGO.transform, "CLOSE", HidePanel, 42f);
    }

    private void CreateTextureSlot(Transform parent, string label, string propertyName, bool normalMap)
    {
        GameObject row = new GameObject(label + "Slot", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup), typeof(LayoutElement));
        row.transform.SetParent(parent, false);
        row.GetComponent<Image>().color = new Color(0.18f, 0.18f, 0.18f, 1f);
        row.GetComponent<LayoutElement>().preferredHeight = 110f;

        VerticalLayoutGroup rowLayout = row.GetComponent<VerticalLayoutGroup>();
        rowLayout.padding = new RectOffset(10, 10, 8, 8);
        rowLayout.spacing = 6f;
        rowLayout.childControlWidth = true;
        rowLayout.childControlHeight = false;

        CreateSubLabel(row.transform, label);

        TMPro.TextMeshProUGUI valueLabel = CreateSubLabel(row.transform, GetCurrentTextureName(propertyName));
        valueLabel.color = new Color(0.8f, 0.8f, 0.8f, 1f);

        CreateActionButton(row.transform, "LOAD TEXTURE", () => LoadTextureIntoSlot(propertyName, normalMap, valueLabel), 36f);
    }

    private void LoadTextureIntoSlot(string propertyName, bool normalMap, TMPro.TextMeshProUGUI valueLabel)
    {
#if UNITY_EDITOR
        string path = EditorUtility.OpenFilePanel("Load " + propertyName + " texture", "", "png,jpg,jpeg,tga");
        if (string.IsNullOrEmpty(path))
            return;

        byte[] bytes = File.ReadAllBytes(path);
        Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, !normalMap);
        texture.name = Path.GetFileNameWithoutExtension(path);

        if (!texture.LoadImage(bytes, false))
        {
            Destroy(texture);
            Debug.LogError("Could not load texture: " + path);
            return;
        }

        texture.wrapMode = TextureWrapMode.Clamp;
        texture.filterMode = FilterMode.Bilinear;

        if (runtimeMaterial != null && runtimeMaterial.HasProperty(propertyName))
        {
            runtimeMaterial.SetTexture(propertyName, texture);
            valueLabel.text = texture.name;
            ApplyRuntimeMaterial();
        }
#else
        Debug.LogWarning("Runtime file browser support is not wired yet. In-editor loading is available.");
#endif
    }

    private void ApplyRuntimeMaterial()
    {
        if (viewer == null || runtimeMaterial == null)
            return;

        viewer.hairCardMaterial = runtimeMaterial;

        HairCard[] cards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        foreach (HairCard card in cards)
        {
            MeshRenderer renderer = card.GetComponent<MeshRenderer>();
            if (renderer != null)
                renderer.sharedMaterial = runtimeMaterial;
        }
    }

    private string GetCurrentTextureName(string propertyName)
    {
        if (runtimeMaterial == null || !runtimeMaterial.HasProperty(propertyName))
            return "Not available";
        Texture texture = runtimeMaterial.GetTexture(propertyName);
        return texture != null ? texture.name : "None";
    }

    private static void CreateHeader(Transform parent, string text)
    {
        GameObject go = new GameObject(text, typeof(RectTransform), typeof(TMPro.TextMeshProUGUI), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        go.GetComponent<LayoutElement>().preferredHeight = 32f;
        TMPro.TextMeshProUGUI tmp = go.GetComponent<TMPro.TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = 18f;
        tmp.fontStyle = TMPro.FontStyles.Bold;
        tmp.color = new Color(0.35f, 0.75f, 1f);
        tmp.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
    }

    private static TMPro.TextMeshProUGUI CreateSubLabel(Transform parent, string text)
    {
        GameObject go = new GameObject("Label", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        go.GetComponent<LayoutElement>().preferredHeight = 22f;
        TMPro.TextMeshProUGUI tmp = go.GetComponent<TMPro.TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = 14f;
        tmp.color = Color.white;
        tmp.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
        return tmp;
    }

    private static void CreateActionButton(Transform parent, string label, UnityEngine.Events.UnityAction action, float height)
    {
        GameObject buttonGO = new GameObject(label + "Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        buttonGO.transform.SetParent(parent, false);
        buttonGO.GetComponent<Image>().color = new Color(0.20f, 0.50f, 0.82f);
        LayoutElement le = buttonGO.GetComponent<LayoutElement>();
        le.minHeight = height;
        le.preferredHeight = height;

        Button button = buttonGO.GetComponent<Button>();
        button.onClick.AddListener(action);

        GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI));
        textGO.transform.SetParent(buttonGO.transform, false);
        RectTransform rect = textGO.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.sizeDelta = Vector2.zero;
        TMPro.TextMeshProUGUI tmp = textGO.GetComponent<TMPro.TextMeshProUGUI>();
        tmp.text = label;
        tmp.fontSize = 14f;
        tmp.fontStyle = TMPro.FontStyles.Bold;
        tmp.alignment = TMPro.TextAlignmentOptions.Center;
        tmp.color = Color.white;
        tmp.raycastTarget = false;
    }

    private void OnDestroy()
    {
        if (runtimeMaterial != null)
            Destroy(runtimeMaterial);
    }
}
