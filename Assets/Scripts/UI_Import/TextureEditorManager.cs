using UnityEngine;
using UnityEngine.UI;
using System.Reflection;

public class TextureEditorManager : MonoBehaviour
{
    private const float MaterialPanelWidth = 300f;
    private const float MaterialTextureInfoWidth = 220f;
    private const float MaterialSliderWidth = 125f;

    private GameObject textureSliderPanelGO;
    private GameObject texturePreviewPlane;
    private Material hairCardMaterial;

    public int currentTextureGroupId = 0;

    public void Init(Material mat) { hairCardMaterial = mat; }

    private void LateUpdate()
    {
        if (textureSliderPanelGO == null || !textureSliderPanelGO.activeInHierarchy) return;
        ApplyMaterialWorkspaceLayout();
        CenterPreviewPlaneBetweenPanels();
        KeepGroomHidden();
    }

    // ModelViewer.loadedModel is private, and reflection is this project's established way in -
    // TextureModeProbe reaches isTextureEditorMode the same way, for the same reason. Cached
    // because this runs every frame the workspace is open.
    private System.Reflection.FieldInfo loadedModelField;
    private ModelViewer hiddenGroomViewer;

    private GameObject ResolveLoadedModel()
    {
        if (hiddenGroomViewer == null)
        {
            hiddenGroomViewer = FindFirstObjectByType<ModelViewer>();
            loadedModelField = null;
        }
        if (hiddenGroomViewer == null) return null;

        if (loadedModelField == null)
        {
            loadedModelField = typeof(ModelViewer).GetField("loadedModel",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        }
        if (loadedModelField == null) return null;

        return loadedModelField.GetValue(hiddenGroomViewer) as GameObject;
    }

    // ModelViewer.SwitchEditorMode hides the groom on the way IN: it deactivates the model and
    // switches off the MeshRenderer of every hair card that exists at that moment. Cards created
    // afterwards were never told.
    //
    // An UNDO in the texture workspace does exactly that. The restore rebuilds the session's
    // cards from the step's payload, and the new ones arrive with their renderers on - so the
    // groom appears in the middle of the texture, in front of the preview plane, because the
    // plane sits at z = 1.5 and the cards sit at the origin with the camera looking down +z.
    //
    // Re-asserted every frame the workspace is up rather than fixed at the one call site that
    // caused it: a project load, a REMAP commit and a group restore can all produce cards in
    // here too, and an invariant that has to be remembered by everything that might break it is
    // one that gets broken again. Cheap - the loop only runs while the workspace is open, and
    // touches a renderer only when it disagrees.
    private void KeepGroomHidden()
    {
        GameObject model = ResolveLoadedModel();
        if (model != null && model.activeSelf) model.SetActive(false);

        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
        {
            if (card == null) continue;

            MeshRenderer renderer = card.GetComponent<MeshRenderer>();
            if (renderer != null && renderer.enabled) renderer.enabled = false;
        }
    }

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

            ApplyMaterialWorkspaceLayout();
            CenterPreviewPlaneBetweenPanels();
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

    private void CenterPreviewPlaneBetweenPanels()
    {
        if (texturePreviewPlane == null) return;

        Camera previewCamera = Camera.main;
        if (previewCamera == null)
        {
            ModelViewer viewer = FindFirstObjectByType<ModelViewer>();
            if (viewer != null) previewCamera = viewer.mainCamera;
        }
        if (previewCamera == null) return;

        // Texture mode is a three-column workspace: Materials | preview | UV controls.
        // The atlas should sit in the centre of the free middle column, not in the centre
        // of the whole screen. Read the actual rendered edges of both panels so this stays
        // correct if panel widths, Canvas scaling, aspect ratio, or resolution change later.
        float targetViewportX = 0.5f;
        GameObject materialPanel = FindNamed("TextureMaterialPanel");
        RectTransform leftRect = materialPanel != null ? materialPanel.GetComponent<RectTransform>() : null;
        RectTransform rightRect = textureSliderPanelGO != null ? textureSliderPanelGO.GetComponent<RectTransform>() : null;

        if (leftRect != null && rightRect != null && leftRect.gameObject.activeInHierarchy && rightRect.gameObject.activeInHierarchy)
        {
            Vector3[] leftCorners = new Vector3[4];
            Vector3[] rightCorners = new Vector3[4];
            leftRect.GetWorldCorners(leftCorners);
            rightRect.GetWorldCorners(rightCorners);

            float leftPanelRight = float.NegativeInfinity;
            float rightPanelLeft = float.PositiveInfinity;
            for (int i = 0; i < 4; i++)
            {
                float leftX = RectTransformUtility.WorldToScreenPoint(null, leftCorners[i]).x;
                float rightX = RectTransformUtility.WorldToScreenPoint(null, rightCorners[i]).x;
                if (leftX > leftPanelRight) leftPanelRight = leftX;
                if (rightX < rightPanelLeft) rightPanelLeft = rightX;
            }

            if (rightPanelLeft > leftPanelRight)
            {
                float workspaceCenterX = (leftPanelRight + rightPanelLeft) * 0.5f;
                targetViewportX = previewCamera.ScreenToViewportPoint(new Vector3(workspaceCenterX, Screen.height * 0.5f, 0f)).x;
                targetViewportX = Mathf.Clamp01(targetViewportX);
            }
        }

        float depth = Vector3.Dot(
            texturePreviewPlane.transform.position - previewCamera.transform.position,
            previewCamera.transform.forward);
        if (depth <= previewCamera.nearClipPlane)
            depth = Mathf.Max(1.5f, previewCamera.nearClipPlane + 0.1f);

        texturePreviewPlane.transform.position = previewCamera.ViewportToWorldPoint(new Vector3(targetViewportX, 0.5f, depth));
        texturePreviewPlane.transform.rotation = previewCamera.transform.rotation;
    }

    private void ApplyMaterialWorkspaceLayout()
    {
        GameObject materialPanel = FindNamed("TextureMaterialPanel");
        if (materialPanel == null || !materialPanel.activeInHierarchy) return;

        RectTransform panelRect = materialPanel.GetComponent<RectTransform>();
        if (panelRect != null)
            panelRect.sizeDelta = new Vector2(MaterialPanelWidth, panelRect.sizeDelta.y);

        // Match the 300 px Groom Groups panel. The old 320 px filename column and 180 px
        // sliders were themselves wider than the new panel interior, so compact those known
        // controls as part of the workspace layout while preserving two-line filename wrapping.
        Transform properties = materialPanel.transform.Find("Properties");
        if (properties == null) return;

        foreach (Transform child in properties)
        {
            if (child == null) continue;

            Transform info = child.Find("Info");
            if (info != null)
            {
                LayoutElement infoLayout = info.GetComponent<LayoutElement>();
                if (infoLayout != null) infoLayout.preferredWidth = MaterialTextureInfoWidth;
            }

            Transform sliderTransform = child.Find("SmoothnessSlider");
            if (sliderTransform == null) sliderTransform = child.Find("MetallicSlider");
            if (sliderTransform != null)
            {
                LayoutElement sliderLayout = sliderTransform.GetComponent<LayoutElement>();
                if (sliderLayout != null) sliderLayout.preferredWidth = MaterialSliderWidth;

                RectTransform sliderRect = sliderTransform.GetComponent<RectTransform>();
                if (sliderRect != null)
                    sliderRect.sizeDelta = new Vector2(MaterialSliderWidth, sliderRect.sizeDelta.y);
            }
        }
    }

    private void BuildTextureEditorUI(Transform parentCanvas, System.Action onSwitchToGroom)
    {
        // The panels below are what four separate authorities sit waiting for by name. Their
        // lookups are throttled now rather than sweeping the scene every frame, so tell the
        // cache the answer has just changed - otherwise binding could lag the build by up to
        // the sweep interval, and these panels are styled and laid out the instant they appear.
        RuntimeNamedObjectCache.Invalidate();

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

        // Texture mode keeps the same always-available project utility as Groom/Clumper.
        // GROOM MODE remains the destination-mode button; SAVE PROJ is always beside it.
        GameObject topRow = new GameObject("ModeRow", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        topRow.transform.SetParent(panelGO.transform, false);
        topRow.GetComponent<LayoutElement>().preferredHeight = 64f;

        HorizontalLayoutGroup rowLayout = topRow.GetComponent<HorizontalLayoutGroup>();
        rowLayout.spacing = 8f;
        rowLayout.childControlWidth = false;
        rowLayout.childControlHeight = true;
        rowLayout.childForceExpandWidth = false;
        rowLayout.childForceExpandHeight = false;
        rowLayout.childAlignment = TextAnchor.MiddleCenter;

        GameObject groomButton = CreateUtilityButton(topRow.transform, "GROOM MODE", 250f, new Color(0.20f, 0.50f, 0.82f, 1f));
        groomButton.GetComponent<Button>().onClick.AddListener(() => ExitToGroom(onSwitchToGroom));

        GameObject saveButton = CreateUtilityButton(topRow.transform, "SAVE PROJ", 250f, new Color(0.20f, 0.50f, 0.30f, 1f));
        saveButton.GetComponent<Button>().onClick.AddListener(InvokeSaveProject);
    }

    private void InvokeSaveProject()
    {
        ModelViewer viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer == null) return;
        MethodInfo save = typeof(ModelViewer).GetMethod("SaveProject", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        save?.Invoke(viewer, null);
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

    private static GameObject CreateUtilityButton(Transform parent, string label, float width, Color color)
    {
        GameObject go = new GameObject(label.Replace(" ", "") + "Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        go.transform.SetParent(parent, false);

        LayoutElement le = go.GetComponent<LayoutElement>();
        le.preferredWidth = width;
        le.minWidth = width;
        le.preferredHeight = 64f;

        Image image = go.GetComponent<Image>();
        image.color = color;

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
