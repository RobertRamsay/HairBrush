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
    private Transform slotsRoot;
    private TMPro.TextMeshProUGUI selectedMaterialLabel;
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
            materials.Add(CreateEntry("Hair Material 1", sourceMaterial));
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

    public void TogglePanel(Transform parentCanvas)
    {
        if (panelGO == null) BuildUI(parentCanvas);
        else panelGO.SetActive(!panelGO.activeSelf);
        RefreshPanel();
    }

    public void ShowPanel(Transform parentCanvas)
    {
        if (panelGO == null) BuildUI(parentCanvas);
        panelGO.SetActive(true);
        RefreshPanel();
    }

    public void HidePanel()
    {
        if (panelGO != null) panelGO.SetActive(false);
    }

    private HairMaterialEntry CreateEntry(string name, Material template)
    {
        Material mat = new Material(template);
        mat.name = name + "_Runtime";
        return new HairMaterialEntry { name = name, material = mat };
    }

    private void AddNewMaterial()
    {
        if (sourceMaterial == null) return;
        HairMaterialEntry entry = CreateEntry("Hair Material " + (materials.Count + 1), sourceMaterial);
        materials.Add(entry);
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
        panelGO = new GameObject("MaterialEditorPanel", typeof(RectTransform), typeof(Image), typeof(GraphicRaycaster));
        panelGO.transform.SetParent(parentCanvas, false);

        RectTransform rect = panelGO.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(1f, 0f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(1f, 0.5f);
        rect.sizeDelta = new Vector2(470f, 0f);
        rect.anchoredPosition = new Vector2(-10f, 0f);
        panelGO.GetComponent<Image>().color = new Color(0.12f, 0.12f, 0.12f, 0.97f);

        VerticalLayoutGroup layout = panelGO.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(14, 14, 14, 14);
        layout.spacing = 8f;
        layout.childControlWidth = true;
        layout.childControlHeight = false;

        CreateHeader(panelGO.transform, "MATERIAL EDITOR");
        CreateActionButton(panelGO.transform, "+ NEW MATERIAL", AddNewMaterial, 38f);

        materialListRoot = CreateContainer(panelGO.transform, "MaterialList", 150f).transform;

        selectedMaterialLabel = CreateSubLabel(panelGO.transform, "");
        assignmentLabel = CreateSubLabel(panelGO.transform, "");
        CreateActionButton(panelGO.transform, "ASSIGN TO SELECTED GROUP", AssignSelectedToCurrentGroup, 40f);

        slotsRoot = CreateContainer(panelGO.transform, "TextureSlots", 330f).transform;
        CreateActionButton(panelGO.transform, "CLOSE", HidePanel, 38f);
    }

    private void RefreshPanel()
    {
        if (panelGO == null) return;
        if (materials.Count == 0) return;
        selectedMaterialIndex = Mathf.Clamp(selectedMaterialIndex, 0, materials.Count - 1);

        if (materialListRoot != null)
        {
            ClearChildren(materialListRoot);
            for (int i = 0; i < materials.Count; i++)
            {
                int capture = i;
                string prefix = i == selectedMaterialIndex ? "> " : "";
                CreateActionButton(materialListRoot, prefix + materials[i].name, () => SelectMaterial(capture), 30f);
            }
        }

        HairMaterialEntry entry = materials[selectedMaterialIndex];
        if (selectedMaterialLabel != null) selectedMaterialLabel.text = "Editing: " + entry.name;
        if (assignmentLabel != null)
        {
            string assigned = groupMaterial.TryGetValue(viewer.currentGroupId, out int idx) && idx >= 0 && idx < materials.Count
                ? materials[idx].name : "Default";
            assignmentLabel.text = "Group " + viewer.currentGroupId + " material: " + assigned;
        }

        if (slotsRoot != null)
        {
            ClearChildren(slotsRoot);
            CreateTextureSlot(slotsRoot, "ALBEDO / BASE COLOR", AlbedoProperty, false, entry.albedoPath);
            CreateTextureSlot(slotsRoot, "NORMAL", NormalProperty, true, entry.normalPath);
            CreateTextureSlot(slotsRoot, "OPACITY MASK", OpacityProperty, false, entry.opacityPath);
        }
    }

    private void CreateTextureSlot(Transform parent, string label, string propertyName, bool normalMap, string currentPath)
    {
        GameObject row = new GameObject(label + "Slot", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup), typeof(LayoutElement));
        row.transform.SetParent(parent, false);
        row.GetComponent<Image>().color = new Color(0.18f, 0.18f, 0.18f, 1f);
        row.GetComponent<LayoutElement>().preferredHeight = 98f;

        VerticalLayoutGroup rowLayout = row.GetComponent<VerticalLayoutGroup>();
        rowLayout.padding = new RectOffset(8, 8, 6, 6);
        rowLayout.spacing = 3f;
        rowLayout.childControlWidth = true;
        rowLayout.childControlHeight = false;

        CreateSubLabel(row.transform, label);
        string shown = string.IsNullOrEmpty(currentPath) ? GetCurrentTextureName(propertyName) : currentPath;
        TMPro.TextMeshProUGUI pathLabel = CreateSubLabel(row.transform, shown);
        pathLabel.fontSize = 11f;
        pathLabel.enableWordWrapping = false;
        pathLabel.overflowMode = TMPro.TextOverflowModes.Ellipsis;
        CreateActionButton(row.transform, "REPLACE FILE", () => LoadTextureIntoSlot(propertyName, normalMap), 30f);
    }

    private void LoadTextureIntoSlot(string propertyName, bool normalMap)
    {
#if UNITY_EDITOR
        if (selectedMaterialIndex < 0 || selectedMaterialIndex >= materials.Count) return;
        HairMaterialEntry entry = materials[selectedMaterialIndex];

        string path = EditorUtility.OpenFilePanel("Load " + propertyName + " texture", "", "png,jpg,jpeg,tga");
        if (string.IsNullOrEmpty(path)) return;

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

        if (entry.material != null && entry.material.HasProperty(propertyName))
        {
            Texture old = entry.material.GetTexture(propertyName);
            entry.material.SetTexture(propertyName, texture);
            if (propertyName == AlbedoProperty) entry.albedoPath = path;
            else if (propertyName == NormalProperty) entry.normalPath = path;
            else if (propertyName == OpacityProperty) entry.opacityPath = path;

            if (old != null && old != sourceMaterial?.GetTexture(propertyName) && old is Texture2D)
                Destroy(old);

            ApplyAssignments();
            RefreshPanel();
        }
#else
        Debug.LogWarning("Runtime file browser support is not wired yet. In-editor loading is available.");
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
        layout.spacing = 4f;
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
        go.GetComponent<LayoutElement>().preferredHeight = 28f;
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
        go.GetComponent<LayoutElement>().preferredHeight = 20f;
        TMPro.TextMeshProUGUI tmp = go.GetComponent<TMPro.TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = 13f;
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
        buttonGO.GetComponent<Button>().onClick.AddListener(action);

        GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI));
        textGO.transform.SetParent(buttonGO.transform, false);
        RectTransform rect = textGO.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        TMPro.TextMeshProUGUI tmp = textGO.GetComponent<TMPro.TextMeshProUGUI>();
        tmp.text = label;
        tmp.fontSize = 13f;
        tmp.fontStyle = TMPro.FontStyles.Bold;
        tmp.alignment = TMPro.TextAlignmentOptions.Center;
        tmp.color = Color.white;
        tmp.raycastTarget = false;
    }

    private void OnDestroy()
    {
        foreach (HairMaterialEntry e in materials)
            if (e.material != null) Destroy(e.material);
    }
}
