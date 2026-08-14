using UnityEngine;
using UnityEngine.UI;

#if UNITY_EDITOR
using UnityEditor;
#endif

[DefaultExecutionOrder(9100)]
public class MaterialEditorBootstrap : MonoBehaviour
{
    private ModelViewer viewer;
    private MaterialEditorManager editor;
    private Button openButton;
    private bool initialised;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Spawn()
    {
        if (FindFirstObjectByType<MaterialEditorBootstrap>() != null)
            return;

        GameObject go = new GameObject("MaterialEditorBootstrap");
        DontDestroyOnLoad(go);
        go.AddComponent<MaterialEditorBootstrap>();
    }

    private void Update()
    {
        if (viewer == null)
            viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer == null)
            return;

        if (!initialised)
            InitialiseEditor();

        if (viewer.groomingSliderPanelGO != null && openButton == null)
            CreateOpenButton(viewer.groomingSliderPanelGO.transform);
    }

    private void InitialiseEditor()
    {
        editor = viewer.GetComponent<MaterialEditorManager>();
        if (editor == null)
            editor = viewer.gameObject.AddComponent<MaterialEditorManager>();

        Material template = null;
#if UNITY_EDITOR
        template = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/HairCard_dithSdr.mat");
#endif
        if (template == null)
            template = viewer.hairCardMaterial;

        editor.Init(viewer, template);
        initialised = true;
    }

    private void CreateOpenButton(Transform parent)
    {
        GameObject buttonGO = new GameObject(
            "MaterialEditorButton",
            typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        buttonGO.transform.SetParent(parent, false);

        LayoutElement le = buttonGO.GetComponent<LayoutElement>();
        le.minHeight = 40f;
        le.preferredHeight = 40f;

        buttonGO.GetComponent<Image>().color = new Color(0.20f, 0.50f, 0.82f);
        openButton = buttonGO.GetComponent<Button>();
        openButton.onClick.AddListener(OpenEditor);

        GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI));
        textGO.transform.SetParent(buttonGO.transform, false);
        RectTransform rect = textGO.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.sizeDelta = Vector2.zero;

        TMPro.TextMeshProUGUI tmp = textGO.GetComponent<TMPro.TextMeshProUGUI>();
        tmp.text = "MATERIAL EDITOR";
        tmp.fontSize = 14f;
        tmp.fontStyle = TMPro.FontStyles.Bold;
        tmp.alignment = TMPro.TextAlignmentOptions.Center;
        tmp.color = Color.white;
        tmp.raycastTarget = false;
    }

    private void OpenEditor()
    {
        if (editor == null || viewer == null || viewer.groomingSliderPanelGO == null)
            return;

        Transform canvas = viewer.groomingSliderPanelGO.transform.root;
        editor.ShowPanel(canvas);
    }
}
