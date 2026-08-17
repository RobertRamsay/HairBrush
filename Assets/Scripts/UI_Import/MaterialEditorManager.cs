using UnityEngine;
using UnityEngine.UI;
using System;
using System.IO;
using System.Collections.Generic;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class MaterialEditorManager : MonoBehaviour
{
    private const string AlbedoProperty = "_Albedo";
    private const string NormalProperty = "_Normal";
    private const string OpacityProperty = "_OpacityMask";

    [Serializable]
    private class HairMaterialEntry
    {
        public string name;
        public Material material;
        public string albedoPath = "";
        public string normalPath = "";
        public string opacityPath = "";
    }

    private ModelViewer viewer;
    private Material sourceMaterial;
    private readonly List<HairMaterialEntry> materials = new List<HairMaterialEntry>();
    private readonly Dictionary<int, int> groupMaterial = new Dictionary<int, int>();
    private int selectedMaterialIndex;
    private int lastGroupId = int.MinValue;
    private float nextApplyScan;

    private GameObject panelGO;
    private Transform materialListRoot;
    private Transform propertiesRoot;
    private TMPro.TextMeshProUGUI assignmentLabel;

    public void Init(ModelViewer modelViewer, Material materialTemplate)
    {
        viewer = modelViewer;
        sourceMaterial = materialTemplate;
        foreach (HairMaterialEntry e in materials) if (e.material != null) Destroy(e.material);
        materials.Clear();
        groupMaterial.Clear();
        if (sourceMaterial != null)
        {
            materials.Add(CreateEntry("Mat 1", sourceMaterial));
            selectedMaterialIndex = 0;
            groupMaterial[viewer.currentGroupId] = 0;
            ApplyAssignments();
        }
    }

    private void Update()
    {
        if (viewer == null || materials.Count == 0) return;
        if (Time.unscaledTime < nextApplyScan) return;
        nextApplyScan = Time.unscaledTime + .2f;
        if (lastGroupId != viewer.currentGroupId)
        {
            lastGroupId = viewer.currentGroupId;
            if (groupMaterial.TryGetValue(lastGroupId, out int index) && index >= 0 && index < materials.Count)
                selectedMaterialIndex = index;
            SyncViewerMaterialToCurrentGroup();
            RefreshPanel();
        }
        ApplyAssignments();
    }

    public void SetWorkspaceVisible(bool visible, Transform parentCanvas)
    {
        if (visible)
        {
            if (panelGO == null) BuildUI(parentCanvas);
            else if (panelGO.transform.parent != parentCanvas) panelGO.transform.SetParent(parentCanvas, false);
            panelGO.SetActive(true);
            RefreshPanel();
        }
        else if (panelGO != null) panelGO.SetActive(false);
    }

    public void TogglePanel(Transform parentCanvas) => SetWorkspaceVisible(panelGO == null || !panelGO.activeSelf, parentCanvas);
    public void ShowPanel(Transform parentCanvas) => SetWorkspaceVisible(true, parentCanvas);
    public void HidePanel() { if (panelGO != null) panelGO.SetActive(false); }

    private HairMaterialEntry CreateEntry(string name, Material template)
    {
        Material mat = new Material(template); mat.name = name + "_Runtime";
        return new HairMaterialEntry { name = name, material = mat };
    }

    private void AddNewMaterial()
    {
        if (sourceMaterial == null) return;
        materials.Add(CreateEntry("Mat " + (materials.Count + 1), sourceMaterial));
        selectedMaterialIndex = materials.Count - 1;
        RefreshPanel();
        UpdatePreviewForSelectedMaterial();
    }

    private void AssignSelectedToCurrentGroup()
    {
        if (viewer == null || selectedMaterialIndex < 0 || selectedMaterialIndex >= materials.Count) return;
        groupMaterial[viewer.currentGroupId] = selectedMaterialIndex;
        SyncViewerMaterialToCurrentGroup();
        ApplyAssignments();
        UpdatePreviewForSelectedMaterial();
        RefreshPanel();
    }

    private void SelectMaterial(int index)
    {
        if (index < 0 || index >= materials.Count) return;
        selectedMaterialIndex = index;
        UpdatePreviewForSelectedMaterial();
        RefreshPanel();
    }

    private void BuildUI(Transform parentCanvas)
    {
        // Root Canvas already owns raycasting; do not add another GraphicRaycaster here.
        panelGO = new GameObject("TextureMaterialPanel", typeof(RectTransform), typeof(Image));
        panelGO.transform.SetParent(parentCanvas, false);
        RectTransform rect = panelGO.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 0f); rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, .5f); rect.sizeDelta = new Vector2(250f, 0f); rect.anchoredPosition = new Vector2(10f, 0f);
        panelGO.GetComponent<Image>().color = new Color(.12f, .12f, .12f, .96f);

        VerticalLayoutGroup layout = panelGO.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(10, 10, 10, 10);
        layout.spacing = 2f;
        layout.childControlWidth = true;
        layout.childControlHeight = false;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        layout.childAlignment = TextAnchor.UpperLeft;

        CreateHeader(panelGO.transform, "MATERIALS", 20f);
        GameObject listRow = new GameObject("MaterialButtons", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        listRow.transform.SetParent(panelGO.transform, false);
        listRow.GetComponent<LayoutElement>().preferredHeight = 26f;
        HorizontalLayoutGroup rowLayout = listRow.GetComponent<HorizontalLayoutGroup>();
        rowLayout.spacing = 4f;
        rowLayout.childControlWidth = false;
        rowLayout.childControlHeight = true;
        rowLayout.childForceExpandWidth = false;
        rowLayout.childForceExpandHeight = false;
        materialListRoot = listRow.transform;

        assignmentLabel = CreateSubLabel(panelGO.transform, "", 16f);
        CreateActionButton(panelGO.transform, "ASSIGN", AssignSelectedToCurrentGroup, 24f);
        CreateHeader(panelGO.transform, "PROPERTIES", 20f);
        propertiesRoot = CreateContainer(panelGO.transform, "Properties", 180f).transform;
    }

    private void RefreshPanel()
    {
        if (panelGO == null || materials.Count == 0) return;
        selectedMaterialIndex = Mathf.Clamp(selectedMaterialIndex, 0, materials.Count - 1);

        if (materialListRoot != null)
        {
            ClearChildren(materialListRoot);
            for (int i = 0; i < materials.Count; i++)
            {
                int capture = i;
                string label = i == selectedMaterialIndex ? "[" + materials[i].name + "]" : materials[i].name;
                CreateSmallButton(materialListRoot, label, () => SelectMaterial(capture), 48f, 24f);
            }
            CreateSmallButton(materialListRoot, "+", AddNewMaterial, 26f, 24f);
        }

        if (assignmentLabel != null)
        {
            string assigned = groupMaterial.TryGetValue(viewer.currentGroupId, out int idx) && idx >= 0 && idx < materials.Count ? materials[idx].name : "Mat 1";
            assignmentLabel.text = "Group " + viewer.currentGroupId + "  •  " + assigned;
        }

        if (propertiesRoot != null)
        {
            ClearChildren(propertiesRoot);
            HairMaterialEntry entry = materials[selectedMaterialIndex];
            CreateSubLabel(propertiesRoot, entry.name, 15f);
            // Texture2D's final constructor argument is "linear": colour/albedo must be false
            // (sRGB), while normals and opacity masks are data textures and stay linear.
            CreateTextureRow(propertiesRoot, "Albedo", AlbedoProperty, false, entry.albedoPath);
            CreateTextureRow(propertiesRoot, "Normal", NormalProperty, true, entry.normalPath);
            CreateTextureRow(propertiesRoot, "Opacity Mask", OpacityProperty, true, entry.opacityPath);
        }
    }

    private void CreateTextureRow(Transform parent, string label, string propertyName, bool linear, string currentPath)
    {
        GameObject row = new GameObject(label + "Row", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        row.transform.SetParent(parent, false);
        row.GetComponent<LayoutElement>().preferredHeight = 46f;
        HorizontalLayoutGroup layout = row.GetComponent<HorizontalLayoutGroup>();
        layout.spacing = 4f;
        layout.childControlWidth = false;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;

        GameObject textBlock = new GameObject("Info", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(LayoutElement));
        textBlock.transform.SetParent(row.transform, false);
        textBlock.GetComponent<LayoutElement>().preferredWidth = 160f;
        VerticalLayoutGroup textLayout = textBlock.GetComponent<VerticalLayoutGroup>();
        textLayout.spacing = 0f;
        textLayout.childControlHeight = false;
        textLayout.childControlWidth = true;
        textLayout.childForceExpandHeight = false;

        CreateSubLabel(textBlock.transform, label, 16f);
        string currentName = string.IsNullOrEmpty(currentPath) ? GetCurrentTextureName(propertyName) : Path.GetFileName(currentPath);
        TMPro.TextMeshProUGUI file = CreateSubLabel(textBlock.transform, "Current: " + currentName, 16f);
        file.fontSize = 10f;
        file.color = new Color(.72f, .72f, .72f);
        file.overflowMode = TMPro.TextOverflowModes.Ellipsis;

        CreateSmallButton(row.transform, "LOAD", () => LoadTextureIntoSlot(propertyName, linear), 48f, 28f);
    }

    private void LoadTextureIntoSlot(string propertyName, bool linear)
    {
#if UNITY_EDITOR
        if (selectedMaterialIndex < 0 || selectedMaterialIndex >= materials.Count) return;
        HairMaterialEntry entry = materials[selectedMaterialIndex];

        string path = EditorUtility.OpenFilePanel("Load texture", "", "png,jpg,jpeg,tga");
        if (string.IsNullOrEmpty(path)) return;

        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception ex) { Debug.LogError("Could not read texture file: " + ex.Message); return; }

        Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear);
        texture.name = Path.GetFileNameWithoutExtension(path);
        if (!texture.LoadImage(bytes, false))
        {
            Destroy(texture);
            Debug.LogError("Could not decode texture: " + path);
            return;
        }

        texture.wrapMode = TextureWrapMode.Clamp;
        texture.filterMode = FilterMode.Bilinear;

        if (entry.material == null || !entry.material.HasProperty(propertyName))
        {
            Destroy(texture);
            Debug.LogError("Selected hair shader has no texture property " + propertyName);
            return;
        }

        entry.material.SetTexture(propertyName, texture);
        if (propertyName == AlbedoProperty) entry.albedoPath = path;
        else if (propertyName == NormalProperty) entry.normalPath = path;
        else if (propertyName == OpacityProperty) entry.opacityPath = path;

        // If this material is assigned to the current group, update the actual hair immediately.
        if (groupMaterial.TryGetValue(viewer.currentGroupId, out int assigned) && assigned == selectedMaterialIndex)
        {
            viewer.hairCardMaterial = entry.material;
            ApplyAssignments();
        }

        // Always preview the material currently being edited, even before assigning it.
        UpdatePreviewForSelectedMaterial();
        RefreshPanel();
#endif
    }

    private void UpdatePreviewForSelectedMaterial()
    {
        if (selectedMaterialIndex < 0 || selectedMaterialIndex >= materials.Count) return;
        TextureEditorManager textureEditor = FindFirstObjectByType<TextureEditorManager>();
        textureEditor?.SetPreviewMaterial(materials[selectedMaterialIndex].material);
    }

    private void SyncViewerMaterialToCurrentGroup()
    {
        if (viewer == null || materials.Count == 0) return;
        int index = groupMaterial.TryGetValue(viewer.currentGroupId, out int assigned) ? assigned : 0;
        if (index < 0 || index >= materials.Count) index = 0;
        viewer.hairCardMaterial = materials[index].material;
    }

    private void ApplyAssignments()
    {
        if (viewer == null || materials.Count == 0) return;
        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
        {
            int index = groupMaterial.TryGetValue(card.groupId, out int assigned) ? assigned : 0;
            if (index < 0 || index >= materials.Count) index = 0;
            MeshRenderer renderer = card.GetComponent<MeshRenderer>();
            if (renderer != null && renderer.sharedMaterial != materials[index].material)
                renderer.sharedMaterial = materials[index].material;
        }
        SyncViewerMaterialToCurrentGroup();
    }

    private string GetCurrentTextureName(string propertyName)
    {
        HairMaterialEntry entry = materials[Mathf.Clamp(selectedMaterialIndex, 0, materials.Count - 1)];
        if (entry.material == null || !entry.material.HasProperty(propertyName)) return "Not available";
        Texture texture = entry.material.GetTexture(propertyName);
        return texture != null ? texture.name : "None";
    }

    private static GameObject CreateContainer(Transform parent, string name, float height)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        go.GetComponent<LayoutElement>().preferredHeight = height;
        VerticalLayoutGroup layout = go.GetComponent<VerticalLayoutGroup>();
        layout.spacing = 2f;
        layout.childControlWidth = true;
        layout.childControlHeight = false;
        layout.childForceExpandHeight = false;
        return go;
    }

    private static void ClearChildren(Transform root)
    {
        for (int i = root.childCount - 1; i >= 0; i--) Destroy(root.GetChild(i).gameObject);
    }

    private static void CreateHeader(Transform parent, string text, float height)
    {
        GameObject go = new GameObject(text, typeof(RectTransform), typeof(TMPro.TextMeshProUGUI), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        go.GetComponent<LayoutElement>().preferredHeight = height;
        TMPro.TextMeshProUGUI tmp = go.GetComponent<TMPro.TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = 15f;
        tmp.fontStyle = TMPro.FontStyles.Bold;
        tmp.color = new Color(.35f, .75f, 1f);
        tmp.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
    }

    private static TMPro.TextMeshProUGUI CreateSubLabel(Transform parent, string text, float height)
    {
        GameObject go = new GameObject("Label", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        go.GetComponent<LayoutElement>().preferredHeight = height;
        TMPro.TextMeshProUGUI tmp = go.GetComponent<TMPro.TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = 11f;
        tmp.color = Color.white;
        tmp.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
        tmp.enableWordWrapping = false;
        return tmp;
    }

    private static void CreateSmallButton(Transform parent, string label, UnityEngine.Events.UnityAction action, float width, float height)
    {
        GameObject go = new GameObject(label + "Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        LayoutElement le = go.GetComponent<LayoutElement>();
        le.preferredWidth = width; le.minWidth = width; le.preferredHeight = height; le.minHeight = height;
        go.GetComponent<Image>().color = new Color(.20f, .50f, .82f);
        go.GetComponent<Button>().onClick.AddListener(action);
        AddButtonText(go.transform, label, 10f);
    }

    private static void CreateActionButton(Transform parent, string label, UnityEngine.Events.UnityAction action, float height)
    {
        GameObject go = new GameObject(label + "Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        go.GetComponent<Image>().color = new Color(.20f, .50f, .82f);
        LayoutElement le = go.GetComponent<LayoutElement>();
        le.minHeight = height; le.preferredHeight = height;
        go.GetComponent<Button>().onClick.AddListener(action);
        AddButtonText(go.transform, label, 10f);
    }

    private static void AddButtonText(Transform parent, string label, float fontSize)
    {
        GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI));
        textGO.transform.SetParent(parent, false);
        RectTransform rect = textGO.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = Vector2.zero; rect.offsetMax = Vector2.zero;
        TMPro.TextMeshProUGUI tmp = textGO.GetComponent<TMPro.TextMeshProUGUI>();
        tmp.text = label; tmp.fontSize = fontSize; tmp.fontStyle = TMPro.FontStyles.Bold;
        tmp.alignment = TMPro.TextAlignmentOptions.Center; tmp.color = Color.white; tmp.raycastTarget = false;
    }

    private void OnDestroy()
    {
        foreach (HairMaterialEntry e in materials) if (e.material != null) Destroy(e.material);
    }
}
