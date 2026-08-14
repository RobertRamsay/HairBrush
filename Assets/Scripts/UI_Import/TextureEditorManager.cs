using UnityEngine;
using UnityEngine.UI;
using System.Reflection;

public class TextureEditorManager : MonoBehaviour
{
    private GameObject textureSliderPanelGO;
    private GameObject texturePreviewPlane;
    private Material hairCardMaterial;

    public int currentTextureGroupId = 0;

    public void Init(Material mat) { hairCardMaterial = mat; }

    public void SetPanelActive(bool active, Transform parentCanvas, System.Action onSwitchToGroom)
    {
        if (textureSliderPanelGO == null && active)
            BuildTextureEditorUI(parentCanvas, onSwitchToGroom);
        else if (textureSliderPanelGO != null)
            textureSliderPanelGO.SetActive(active);

        if (active && textureSliderPanelGO != null)
            textureSliderPanelGO.transform.SetAsLastSibling();

        if (active)
        {
            if (texturePreviewPlane == null)
            {
                texturePreviewPlane = GameObject.CreatePrimitive(PrimitiveType.Quad);
                texturePreviewPlane.name = "HairTexturePreviewPlane";
                texturePreviewPlane.transform.position = new Vector3(0f, 0f, 1.5f);
                texturePreviewPlane.transform.localScale = new Vector3(0.6f, 1.2f, 1.0f);

                MeshFilter meshFilter = texturePreviewPlane.GetComponent<MeshFilter>();
                if (meshFilter != null && meshFilter.sharedMesh != null)
                    meshFilter.sharedMesh.uv = new Vector2[]
                    {
                        new Vector2(0,0), new Vector2(1,0),
                        new Vector2(0,1), new Vector2(1,1)
                    };

                MeshRenderer mr = texturePreviewPlane.GetComponent<MeshRenderer>();
                if (hairCardMaterial != null) mr.sharedMaterial = hairCardMaterial;
            }
            else texturePreviewPlane.SetActive(true);
        }
        else if (texturePreviewPlane != null)
        {
            texturePreviewPlane.SetActive(false);
        }
    }

    public void SetPreviewMaterial(Material material)
    {
        if (material == null) return;
        hairCardMaterial = material;
        if (texturePreviewPlane != null)
        {
            MeshRenderer mr = texturePreviewPlane.GetComponent<MeshRenderer>();
            if (mr != null) mr.sharedMaterial = material;
        }
    }

    private void BuildTextureEditorUI(Transform parentCanvas, System.Action onSwitchToGroom)
    {
        GameObject panelGO = new GameObject("TextureEditorPanel", typeof(RectTransform), typeof(Image));
        panelGO.transform.SetParent(parentCanvas, false);
        panelGO.transform.SetAsLastSibling();

        RectTransform panelRect = panelGO.GetComponent<RectTransform>();
        panelRect.anchorMin = new Vector2(1, 0);
        panelRect.anchorMax = new Vector2(1, 1);
        panelRect.pivot = new Vector2(1, 0.5f);
        panelRect.sizeDelta = new Vector2(560, 0);
        panelRect.anchoredPosition = new Vector2(-10, 0);

        Image panelImage = panelGO.GetComponent<Image>();
        panelImage.color = new Color(0.15f, 0.15f, 0.15f, 0.85f);
        panelImage.raycastTarget = false;

        VerticalLayoutGroup layout = panelGO.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(15, 15, 12, 12);
        layout.spacing = 6;
        layout.childControlWidth = true;
        layout.childControlHeight = false;
        layout.childForceExpandHeight = false;
        textureSliderPanelGO = panelGO;

        // Texture mode shows only the destination mode: one centered GROOM MODE button.
        GameObject topRow = new GameObject("ModeRow", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        topRow.transform.SetParent(panelGO.transform, false);
        topRow.GetComponent<LayoutElement>().preferredHeight = 64f;

        HorizontalLayoutGroup rowLayout = topRow.GetComponent<HorizontalLayoutGroup>();
        rowLayout.childControlWidth = false;
        rowLayout.childControlHeight = true;
        rowLayout.childForceExpandWidth = false;
        rowLayout.childForceExpandHeight = false;
        rowLayout.childAlignment = TextAnchor.MiddleCenter;

        GameObject groomButton = CreateModeButton(topRow.transform, "GROOM MODE");
        groomButton.GetComponent<Button>().onClick.AddListener(() => ExitToGroom(onSwitchToGroom));
    }

    private void ExitToGroom(System.Action callback)
    {
        if (textureSliderPanelGO != null) textureSliderPanelGO.SetActive(false);
        if (texturePreviewPlane != null) texturePreviewPlane.SetActive(false);
        FindFirstObjectByType<MaterialEditorManager>()?.HidePanel();

        ModelViewer viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer != null)
        {
            FieldInfo textureMode = typeof(ModelViewer).GetField(
                "isTextureEditorMode",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            textureMode?.SetValue(viewer, false);

            viewer.OnModelLoaded();
            viewer.ToggleGroomingMode(true);

            if (viewer.groomingSliderPanelGO != null)
                viewer.groomingSliderPanelGO.SetActive(true);

            GameObject groups = FindNamed("GroupManagerPanel");
            if (groups != null) groups.SetActive(true);
        }

        callback?.Invoke();
    }

    private static GameObject CreateModeButton(Transform parent, string label)
    {
        GameObject go = new GameObject("ModeSwitchButton", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        go.transform.SetParent(parent, false);

        LayoutElement le = go.GetComponent<LayoutElement>();
        le.preferredWidth = 300f;
        le.minWidth = 300f;
        le.preferredHeight = 64f;

        Image image = go.GetComponent<Image>();
        image.color = new Color(0.20f, 0.50f, 0.82f, 1f);

        Button button = go.GetComponent<Button>();
        ColorBlock colors = button.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = new Color(1f, 1f, 1f, 0.92f);
        colors.pressedColor = new Color(0.85f, 0.85f, 0.85f, 1f);
        button.colors = colors;

        AddCenteredLabel(go.transform, label, 16f, Color.white);
        return go;
    }

    private static void AddCenteredLabel(Transform parent, string label, float fontSize, Color color)
    {
        GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI));
        textGO.transform.SetParent(parent, false);

        RectTransform rect = textGO.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        TMPro.TextMeshProUGUI tmp = textGO.GetComponent<TMPro.TextMeshProUGUI>();
        tmp.text = label;
        tmp.fontSize = fontSize;
        tmp.fontStyle = TMPro.FontStyles.Bold;
        tmp.alignment = TMPro.TextAlignmentOptions.Center;
        tmp.color = color;
        tmp.raycastTarget = false;
    }

    private static GameObject FindNamed(string name)
    {
        foreach (Transform t in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (t != null && t.name == name) return t.gameObject;
        return null;
    }
}