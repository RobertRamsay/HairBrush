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

        foreach (HairMaterialEntry e in materials)
            if (e.material != null) Destroy(e.material);
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
        else if (panelGO != null)
        {
            panelGO.SetActive(false);
        }
    }

    public void TogglePanel(Transform parentCanvas) => SetWorkspaceVisible(panelGO == null || !panelGO.activeSelf, parentCanvas);
    public void ShowPanel(Transform parentCanvas) => SetWorkspaceVisible(true, parentCanvas);
    public void HidePanel() { if (panelGO != null) panelGO.SetActive(false); }

    private HairMaterialEntry CreateEntry(string name, Material template)
    {
        Material mat = new Material(template);
        mat.name = name + "_Runtime";
        return new HairMaterialEntry { name = name, material = mat };
    }

    private void AddNewMaterial()
    {
        if (sourceMaterial == null) return;
        materials.Add(CreateEntry("Mat " + (materials.Count + 1), sourceMaterial));
        selectedMaterialIndex = materials.Count - 1;
        RefreshPanel();
    }

    private void AssignSelectedToCurrentGroup()
    {
        if (viewer == null || selectedMaterialIndex < 0 || selectedMaterialIndex >= materials.Count) return;
        groupMaterial[viewer.currentGroupId] = selectedMaterialIndex;
        SyncViewerMaterialToCurrentGroup();
        ApplyAssignments();
        RefreshPanel();
    }

    private void SelectMaterial(int index)
    {
        if (index < 0 || index >= materials.Count) return;
        selectedMaterialIndex = index;
        RefreshPanel();
    }

    private void BuildUI(Transform parentCanvas)
    {
        panelGO = new GameObject("TextureMaterialPanel", typeof(RectTransform), typeof(Image), typeof(GraphicRaycaster));
        panelGO.transform.SetParent(parentCanvas, false);

        RectTransform rect = panelGO.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 0f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, .5f);
        rect.sizeDelta = new Vector2(300f, 0f);
        rect.anchoredPosition = new Vector2(10f, 0f);
        panelGO.GetComponent<Image>().color = new Color(.12f, .12f, .12f, .96f);

        VerticalLayoutGroup layout = panelGO.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(12, 12, 12, 12);
        layout.spacing = 8f;
        layout.childControlWidth = true;
        layout.childControlHeight = false;

        CreateHeader(panelGO.transform, "MATERIALS");

        GameObject listRow = new GameObject("MaterialButtons", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        listRow.transform.SetParent(panelGO.transform, false);
        listRow.GetComponent<LayoutElement>().preferredHeight = 34f;
        HorizontalLayoutGroup rowLayout = listRow.GetComponent<HorizontalLayoutGroup>();
        rowLayout.spacing = 5f;
        rowLayout.childControlWidth = false;
        rowLayout.childControlHeight = true;
        materialListRoot = listRow.transform;

        assignmentLabel = CreateSubLabel(panelGO.transform, "");
        CreateActionButton(panelGO.transform, "ASSIGN TO GROUP", AssignSelectedToCurrentGroup, 32f);

        CreateHeader(panelGO.transform, "MATERIAL PROPERTIES");
        propertiesRoot = CreateContainer(panelGO.transform, "Properties", 285f).transform;
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
                CreateSmallButton(materialListRoot, label, () => SelectMaterial(capture), 62f);
            }
            CreateSmallButton(materialListRoot, "+", AddNewMaterial, 34f);
        }

        if (assignmentLabel != null)
        {
            string assigned = groupMaterial.TryGetValue(viewer.currentGroupId, out int idx) && idx >= 0 && idx < materials.Count
                ? materials[idx].name : "Mat 1";
            assignmentLabel.text = "Group " + viewer.currentGroupId + ": " + assigned;
        }

        if (propertiesRoot != null)
        {
            ClearChildren(propertiesRoot);
            HairMaterialEntry entry = materials[selectedMaterialIndex];
            CreateSubLabel(propertiesRoot, entry.name + "  •  " + (entry.material != null && entry.material.shader != null ? entry.material.shader.name : "No Shader"));
            CreateTextureRow(propertiesRoot, "Albedo", AlbedoProperty, false, entry.albedoPath);
            CreateTextureRow(propertiesRoot, "Normal", NormalProperty, true, entry.normalPath);
            CreateTextureRow(propertiesRoot, "Opacity Mask", OpacityProperty, false, entry.opacityPath);
        }
    }

    private void CreateTextureRow(Transform parent, string label, string propertyName, bool normalMap, string currentPath)
    {
        GameObject row = new GameObject(label + "Row", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(LayoutElement));
        row.transform.SetParent(parent, false);
        row.GetComponent<LayoutElement>().preferredHeight = 78f;
        VerticalLayoutGroup layout = row.GetComponent<VerticalLayoutGroup>();
        layout.spacing = 3f;
        layout.childControlWidth = true;
        layout.childControlHeight = false;

        CreateSubLabel(row.transform, label);
        string shown = string.IsNullOrEmpty(currentPath) ? GetCurrentTextureName(propertyName) : Path.GetFileName(currentPath);
        TMPro.TextMeshProUGUI file = CreateSubLabel(row.transform, shown);
        file.fontSize = 11f;
        file.color = new Color(.75f, .75f, .75f);
        CreateActionButton(row.transform, "LOAD", () => LoadTextureIntoSlot(propertyName, normalMap), 26f);
    }

    private void LoadTextureIntoSlot(string propertyName, bool normalMap)
    {
#if UNITY_EDITOR
        if (selectedMaterialIndex < 0 || selectedMaterialIndex >= materials.Count) return;
        HairMaterialEntry entry = materials[selectedMaterialIndex];
        string path = EditorUtility.OpenFilePanel("Load texture", "", "png,jpg,jpeg,tga");
        if (string.IsNullOrEmpty(path)) return;

        byte[] bytes = File.ReadAllBytes(path);
        Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, !normalMap);
        texture.name = Path.GetFileNameWithoutExtension(path);
        if (!texture.LoadImage(bytes, false)) { Destroy(texture); return; }
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.filterMode = FilterMode.Bilinear;

        if (entry.material != null && entry.material.HasProperty(propertyName))
        {
            entry.material.SetTexture(propertyName, texture);
            if (propertyName == AlbedoProperty) entry.albedoPath = path;
            else if (propertyName == NormalProperty) entry.normalPath = path;
            else if (propertyName == OpacityProperty) entry.opacityPath = path;
            ApplyAssignments();
            RefreshPanel();
        }
#endif
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
        if (materials.Count == 0) return "None";
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
        layout.spacing = 7f;
        layout.childControlWidth = true;
        layout.childControlHeight = false;
        return go;
    }

    private static void ClearChildren(Transform root)
    {
        for (int i = root.childCount - 1; i >= 0; i--) Destroy(root.GetChild(i).gameObject);
    }

    private static void CreateHeader(Transform parent, string text)
    {
        GameObject go = new GameObject(text, typeof(RectTransform), typeof(TMPro.TextMeshProUGUI), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        go.GetComponent<LayoutElement>().preferredHeight = 26f;
        TMPro.TextMeshProUGUI tmp = go.GetComponent<TMPro.TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = 16f;
        tmp.fontStyle = TMPro.FontStyles.Bold;
        tmp.color = new Color(.35f, .75f, 1f);
        tmp.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
    }

    private static TMPro.TextMeshProUGUI CreateSubLabel(Transform parent, string text)
    {
        GameObject go = new GameObject("Label", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        go.GetComponent<LayoutElement>().preferredHeight = 18f;
        TMPro.TextMeshProUGUI tmp = go.GetComponent<TMPro.TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = 12f;
        tmp.color = Color.white;
        tmp.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
        return tmp;
    }

    private static void CreateSmallButton(Transform parent, string label, UnityEngine.Events.UnityAction action, float width)
    {
        GameObject go = new GameObject(label + "Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        LayoutElement le = go.GetComponent<LayoutElement>();
        le.preferredWidth = width;
        le.minWidth = width;
        go.GetComponent<Image>().color = new Color(.20f, .50f, .82f);
        go.GetComponent<Button>().onClick.AddListener(action);
        AddButtonText(go.transform, label, 11f);
    }

    private static void CreateActionButton(Transform parent, string label, UnityEngine.Events.UnityAction action, float height)
    {
        GameObject go = new GameObject(label + "Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        go.GetComponent<Image>().color = new Color(.20f, .50f, .82f);
        LayoutElement le = go.GetComponent<LayoutElement>();
        le.minHeight = height;
        le.preferredHeight = height;
        go.GetComponent<Button>().onClick.AddListener(action);
        AddButtonText(go.transform, label, 11f);
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
        foreach (HairMaterialEntry e in materials)
            if (e.material != null) Destroy(e.material);
    }
}
