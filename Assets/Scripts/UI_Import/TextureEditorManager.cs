using UnityEngine;
using UnityEngine.UI;
using System.Reflection;

public class TextureEditorManager : MonoBehaviour
{
    private GameObject textureSliderPanelGO;
    private GameObject texturePreviewPlane;
    private Material hairCardMaterial;

    public int currentTextureGroupId = 0;

    public void Init(Material mat)
    {
        hairCardMaterial = mat;
    }

    public void SetPanelActive(bool active, Transform parentCanvas, System.Action onSwitchToGroom)
    {
        if (textureSliderPanelGO == null && active)
            BuildTextureEditorUI(parentCanvas, onSwitchToGroom);
        else if (textureSliderPanelGO != null)
            textureSliderPanelGO.SetActive(active);

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
                {
                    Mesh mesh = meshFilter.sharedMesh;
                    mesh.uv = new Vector2[]
                    {
                        new Vector2(0, 0), new Vector2(1, 0),
                        new Vector2(0, 1), new Vector2(1, 1)
                    };
                }

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

    void BuildTextureEditorUI(Transform parentCanvas, System.Action onSwitchToGroom)
    {
        GameObject panelGO = new GameObject("TextureEditorPanel", typeof(RectTransform), typeof(Image));
        panelGO.transform.SetParent(parentCanvas, false);

        RectTransform panelRect = panelGO.GetComponent<RectTransform>();
        panelRect.anchorMin = new Vector2(1, 0);
        panelRect.anchorMax = new Vector2(1, 1);
        panelRect.pivot = new Vector2(1, 0.5f);
        panelRect.sizeDelta = new Vector2(560, 0);
        panelRect.anchoredPosition = new Vector2(-10, 0);
        panelGO.GetComponent<Image>().color = new Color(0.15f, 0.15f, 0.15f, 0.85f);

        VerticalLayoutGroup layout = panelGO.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(15, 15, 12, 12);
        layout.spacing = 6;
        layout.childControlWidth = true;
        layout.childControlHeight = false;
        layout.childForceExpandHeight = false;
        textureSliderPanelGO = panelGO;

        GameObject tabRowGO = new GameObject("PanelTabRow", typeof(RectTransform), typeof(LayoutElement));
        tabRowGO.transform.SetParent(panelGO.transform, false);
        tabRowGO.GetComponent<LayoutElement>().preferredHeight = 45f;
        HorizontalLayoutGroup hLayout = tabRowGO.AddComponent<HorizontalLayoutGroup>();
        hLayout.spacing = 8;
        hLayout.childControlWidth = true;
        hLayout.childControlHeight = true;
        hLayout.childForceExpandHeight = false;

        GameObject groomTabGO = CreateTab(tabRowGO.transform, "Groom Mode", new Color(0.25f, 0.25f, 0.25f));
        groomTabGO.GetComponent<Button>().onClick.AddListener(() => ExitToGroom(onSwitchToGroom));
        CreateTab(tabRowGO.transform, "Texture Editor", new Color(0.2f, 0.5f, 0.8f));
    }

    private void ExitToGroom(System.Action callback)
    {
        // First let the original ModelViewer path do its normal work.
        callback?.Invoke();

        // Then make the state transition authoritative so a missed callback can never trap us here.
        if (textureSliderPanelGO != null) textureSliderPanelGO.SetActive(false);
        if (texturePreviewPlane != null) texturePreviewPlane.SetActive(false);

        MaterialEditorManager materialEditor = FindFirstObjectByType<MaterialEditorManager>();
        materialEditor?.HidePanel();

        ModelViewer viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer == null) return;

        FieldInfo textureMode = typeof(ModelViewer).GetField("isTextureEditorMode", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        textureMode?.SetValue(viewer, false);

        viewer.OnModelLoaded();
        viewer.ToggleGroomingMode(true);

        if (viewer.groomingSliderPanelGO != null)
            viewer.groomingSliderPanelGO.SetActive(true);

        GameObject groups = FindNamed("GroupManagerPanel");
        if (groups != null) groups.SetActive(true);
    }

    private static GameObject FindNamed(string name)
    {
        foreach (Transform t in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (t != null && t.name == name) return t.gameObject;
        return null;
    }

    private static GameObject CreateTab(Transform parent, string label, Color color)
    {
        GameObject go = new GameObject(label.Replace(" ", "") + "TabButton", typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        go.GetComponent<Image>().color = color;

        GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI));
        textGO.transform.SetParent(go.transform, false);
        RectTransform rect = textGO.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        TMPro.TextMeshProUGUI tmp = textGO.GetComponent<TMPro.TextMeshProUGUI>();
        tmp.text = label;
        tmp.fontSize = 16;
        tmp.fontStyle = TMPro.FontStyles.Bold;
        tmp.alignment = TMPro.TextAlignmentOptions.Center;
        tmp.color = Color.white;
        tmp.raycastTarget = false;
        return go;
    }
}
