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

    // HairBrush intentionally has one active hair material for the whole session. Multiple
    // material entries are authoring presets/stages, never per-group assignments. Keeping the
    // old dictionary with one reserved key lets existing project persistence migrate cleanly.
    private const int GlobalMaterialKey = int.MinValue;

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
            groupMaterial[GlobalMaterialKey] = 0;
            ApplyAssignments();
        }
    }

    private void Update()
    {
        if (viewer == null || materials.Count == 0) return;
        if (Time.unscaledTime < nextApplyScan) return;
        nextApplyScan = Time.unscaledTime + .2f;

        // Group selection no longer changes material state. Refresh only so any group-related
        // workspace around this panel can change without accidentally changing the hair material.
        if (lastGroupId != viewer.currentGroupId)
        {
            lastGroupId = viewer.currentGroupId;
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
            UpdatePreviewForSelectedMaterial();
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

    private void AssignSelectedToAllGroups()
    {
        if (viewer == null || selectedMaterialIndex < 0 || selectedMaterialIndex >= materials.Count) return;

        // A material choice is session-global by design. Clear any legacy per-group mappings so
        // there is only one source of truth from this point onward.
        groupMaterial.Clear();
        groupMaterial[GlobalMaterialKey] = selectedMaterialIndex;
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

    private int GetGlobalMaterialIndex()
    {
        if (materials.Count == 0) return -1;
        int index = groupMaterial.TryGetValue(GlobalMaterialKey, out int assigned) ? assigned : 0;
        return Mathf.Clamp(index, 0, materials.Count - 1);
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
        CreateActionButton(panelGO.transform, "APPLY ALL", AssignSelectedToAllGroups, 24f);
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
            int assignedIndex = GetGlobalMaterialIndex();
            string assigned = assignedIndex >= 0 ? materials[assignedIndex].name : "Mat 1";
            assignmentLabel.text = "ALL GROUPS  •  " + assigned;
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
        if (selectedMaterialIndex < 0 || selectedMaterialIndex >= materials.Count) return;
        HairMaterialEntry entry = materials[selectedMaterialIndex];

        string path;
#if UNITY_EDITOR
        path = EditorUtility.OpenFilePanel("Load texture", "", "png,jpg,jpeg,tga");
#else
        path = RuntimeFileDialog.OpenFile("Load texture", "Images\0*.png;*.jpg;*.jpeg;*.tga\0All Files\0*.*\0\0", "png");
#endif
        if (string.IsNullOrEmpty(path)) return;

        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception ex)
        {
            Debug.LogError("Could not read texture file: " + ex.Message);
            StatusToast.Show("Couldn't read that file: " + ex.Message, true);
            return;
        }

        Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear);
        texture.name = Path.GetFileNameWithoutExtension(path);
        if (!texture.LoadImage(bytes, false))
        {
            Destroy(texture);
            Debug.LogError("Could not decode texture: " + path);
            StatusToast.Show("Couldn't decode that image (unsupported format or corrupt file): " + Path.GetFileName(path), true);
            return;
        }

        texture.wrapMode = TextureWrapMode.Clamp;
        texture.filterMode = FilterMode.Bilinear;

        if (entry.material == null || !entry.material.HasProperty(propertyName))
        {
            Destroy(texture);
            Debug.LogError("Selected hair shader has no texture property " + propertyName);
            StatusToast.Show("Current shader has no " + propertyName + " texture slot.", true);
            return;
        }

        entry.material.SetTexture(propertyName, texture);
        if (propertyName == AlbedoProperty) entry.albedoPath = path;
        else if (propertyName == NormalProperty) entry.normalPath = path;
        else if (propertyName == OpacityProperty) entry.opacityPath = path;

        StatusToast.Show("Loaded " + Path.GetFileName(path));

        // Edits to the active global material must update every existing card immediately.
        if (GetGlobalMaterialIndex() == selectedMaterialIndex)
        {
            viewer.hairCardMaterial = entry.material;
            ApplyAssignments();
        }

        // Always preview the material currently being edited, even before applying it globally.
        UpdatePreviewForSelectedMaterial();
        RefreshPanel();
    }

    private void UpdatePreviewForSelectedMaterial()
    {
        if (selectedMaterialIndex < 0 || selectedMaterialIndex >= materials.Count) return;
        TextureEditorManager textureEditor = FindFirstObjectByType<TextureEditorManager>();
        textureEditor?.SetPreviewMaterial(materials[selectedMaterialIndex].material);
    }

    // Kept under its historical method name because project restore invokes ApplyAssignments via
    // reflection. Viewer material is now global; currentGroupId is deliberately irrelevant.
    private void SyncViewerMaterialToCurrentGroup()
    {
        if (viewer == null || materials.Count == 0) return;
        int index = GetGlobalMaterialIndex();
        if (index < 0) return;
        viewer.hairCardMaterial = materials[index].material;
    }

    private void ApplyAssignments()
    {
        if (viewer == null || materials.Count == 0) return;
        int index = GetGlobalMaterialIndex();
        if (index < 0) return;
        Material active = materials[index].material;

        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
        {
            MeshRenderer renderer = card.GetComponent<MeshRenderer>();
            if (renderer != null && renderer.sharedMaterial != active)
                renderer.sharedMaterial = active;
        }
        viewer.hairCardMaterial = active;
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
