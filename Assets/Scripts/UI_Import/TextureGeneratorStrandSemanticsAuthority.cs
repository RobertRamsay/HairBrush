using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;
using System.Reflection;

[DefaultExecutionOrder(9500)]
public class TextureGeneratorStrandSemanticsAuthority : MonoBehaviour
{
    const float BaseRadius = 1f;
    const float DefaultSpreadValue = 2f;
    const int DefaultClusterWidth = 760;
    const int DefaultClusterHeight = 1800;

    TextureEditorManager manager;
    Slider spreadSlider;
    Slider thicknessSlider;
    Slider lengthSlider;
    TMPro.TextMeshProUGUI spreadLabel;
    TMPro.TextMeshProUGUI thicknessLabel;
    TMPro.TextMeshProUGUI lengthLabel;
    Button generateButton;
    GameObject boundPanel;
    bool wasGeneratorActive;
    int lastClusterId = int.MinValue;

    readonly Dictionary<int, float> spreadByCluster = new Dictionary<int, float>();
    readonly Dictionary<int, float> thicknessByCluster = new Dictionary<int, float>();
    readonly Dictionary<int, float> lengthByCluster = new Dictionary<int, float>();

    FieldInfo clustersField;
    FieldInfo activeIndexField;
    FieldInfo textureField;
    FieldInfo loadedModelField;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<TextureGeneratorStrandSemanticsAuthority>() != null) return;
        GameObject go = new GameObject("TextureGeneratorStrandSemanticsAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<TextureGeneratorStrandSemanticsAuthority>();
    }

    void Update()
    {
        if (manager == null)
        {
            manager = FindFirstObjectByType<TextureEditorManager>();
            if (manager != null) CacheReflection();
        }
        if (manager == null) return;

        GameObject panel = FindNamed("TextureGeneratorControlsPanel");
        bool generatorActive = panel != null && panel.activeInHierarchy;

        if (panel != null && panel != boundPanel)
            BindPanel(panel);

        if (generatorActive)
        {
            ApplyControlSpacing(panel);
            SyncActiveClusterControls();
        }

        if (wasGeneratorActive && !generatorActive)
            RestoreLoadedModelRenderers();

        wasGeneratorActive = generatorActive;
    }

    void CacheReflection()
    {
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        System.Type t = typeof(TextureEditorManager);
        clustersField = t.GetField("clusters", flags);
        activeIndexField = t.GetField("activeClusterIndex", flags);
        textureField = t.GetField("generatedHairTexture", flags);
        loadedModelField = typeof(ModelViewer).GetField("loadedModel", flags);
    }

    void BindPanel(GameObject panel)
    {
        boundPanel = panel;

        spreadSlider = FindSlider(panel.transform, "Strand Width_Slider");
        thicknessSlider = FindSlider(panel.transform, "Thickness Amount_Slider");
        lengthSlider = FindSlider(panel.transform, "Strand Length_Slider");

        spreadLabel = GetTMP(panel.transform, "Strand Width_Text");
        thicknessLabel = GetTMP(panel.transform, "Thickness Amount_Text");
        lengthLabel = GetTMP(panel.transform, "Strand Length_Text");

        Transform generateT = FindRecursive(panel.transform, "GENERATE / UPDATEButton");
        generateButton = generateT != null ? generateT.GetComponent<Button>() : null;

        if (spreadSlider != null)
        {
            spreadSlider.onValueChanged.RemoveAllListeners();
            spreadSlider.onValueChanged.AddListener(OnSpreadChanged);
        }

        if (thicknessSlider != null)
        {
            thicknessSlider.minValue = 1f;
            thicknessSlider.maxValue = 10f;
            thicknessSlider.onValueChanged.RemoveAllListeners();
            thicknessSlider.onValueChanged.AddListener(OnThicknessChanged);
        }

        if (lengthSlider != null)
        {
            lengthSlider.minValue = 0.1f;
            lengthSlider.maxValue = 2f;
            lengthSlider.onValueChanged.RemoveAllListeners();
            lengthSlider.onValueChanged.AddListener(OnLengthChanged);
        }

        if (generateButton != null)
        {
            generateButton.onClick.RemoveAllListeners();
            generateButton.onClick.AddListener(GenerateWithSemantics);
        }

        lastClusterId = int.MinValue;
    }

    void OnSpreadChanged(float value)
    {
        int id = manager != null ? manager.currentTextureGroupId : -1;
        if (id >= 0) spreadByCluster[id] = value;
        UpdateLabel(spreadLabel, "Strand Width", value);
        PrepareActiveClusterGeometry(value, GetStoredLength(id), false);
    }

    void OnThicknessChanged(float value)
    {
        int id = manager != null ? manager.currentTextureGroupId : -1;
        if (id >= 0) thicknessByCluster[id] = value;
        UpdateLabel(thicknessLabel, "Thickness Amount", value);

        // Renderer multiplies strandWidth * thicknessAmount. Keep width at exactly one pixel
        // so this slider becomes a literal 1..10 pixel circle radius control.
        manager.strandWidth = BaseRadius;
        manager.thicknessAmount = Mathf.Clamp(value, 1f, 10f);
    }

    void OnLengthChanged(float value)
    {
        int id = manager != null ? manager.currentTextureGroupId : -1;
        if (id >= 0) lengthByCluster[id] = value;
        UpdateLabel(lengthLabel, "Strand Length", value);
        PrepareActiveClusterGeometry(GetStoredSpread(id), value, false);
    }

    void SyncActiveClusterControls()
    {
        int id = manager.currentTextureGroupId;
        if (id < 0) return;

        if (!spreadByCluster.ContainsKey(id)) spreadByCluster[id] = DefaultSpreadValue;
        if (!thicknessByCluster.ContainsKey(id)) thicknessByCluster[id] = 1f;
        if (!lengthByCluster.ContainsKey(id)) lengthByCluster[id] = 1f;

        float spread = spreadByCluster[id];
        float thickness = thicknessByCluster[id];
        float length = lengthByCluster[id];

        if (id != lastClusterId)
        {
            lastClusterId = id;
            if (spreadSlider != null) spreadSlider.SetValueWithoutNotify(spread);
            if (thicknessSlider != null) thicknessSlider.SetValueWithoutNotify(thickness);
            if (lengthSlider != null) lengthSlider.SetValueWithoutNotify(length);
            UpdateLabel(spreadLabel, "Strand Width", spread);
            UpdateLabel(thicknessLabel, "Thickness Amount", thickness);
            UpdateLabel(lengthLabel, "Strand Length", length);
        }

        // These are the values TextureEditorManager commits immediately before rasterising.
        manager.strandWidth = BaseRadius;
        manager.thicknessAmount = thickness;
        manager.strandLength = 2f; // full allocated cluster height; our length slider controls that allocation.
        PrepareActiveClusterGeometry(spread, length, false);
    }

    void GenerateWithSemantics()
    {
        if (manager == null) return;

        int id = manager.currentTextureGroupId;
        float spread = GetStoredSpread(id);
        float thickness = GetStoredThickness(id);
        float length = GetStoredLength(id);

        ClearOldActiveRect();

        manager.strandWidth = BaseRadius;
        manager.thicknessAmount = thickness;
        manager.strandLength = 2f;
        PrepareActiveClusterGeometry(spread, length, true);
        manager.GenerateOrUpdateActiveCluster();
    }

    void PrepareActiveClusterGeometry(float spreadValue, float lengthValue, bool resizePlacedRect)
    {
        TextureEditorManager.HairTextureCluster c = GetActiveCluster();
        if (c == null) return;

        c.strandWidth = BaseRadius;
        c.thicknessAmount = GetStoredThickness(c.id);
        c.strandLength = 2f;

        float spreadScale = Mathf.Max(0.1f, spreadValue / DefaultSpreadValue);
        int desiredWidth = Mathf.Clamp(Mathf.RoundToInt(DefaultClusterWidth * spreadScale), 180, 3000);
        int desiredHeight = Mathf.Clamp(Mathf.RoundToInt(DefaultClusterHeight * Mathf.Clamp(lengthValue, 0.1f, 2f)), 256, 4000);

        c.rectWidth = desiredWidth;
        c.rectHeight = desiredHeight;

        if (!resizePlacedRect || !c.placed) return;

        int size = manager.textureSize;
        int pad = Mathf.Clamp(c.padding, 8, Mathf.Min(desiredWidth, desiredHeight) / 3);
        int x = Mathf.Clamp(c.rootPixel.x - desiredWidth / 2, 0, Mathf.Max(0, size - desiredWidth));
        int desiredY = c.rootPixel.y - (desiredHeight - pad);
        int y = Mathf.Clamp(desiredY, 0, Mathf.Max(0, size - desiredHeight));
        c.pixelRect = new RectInt(x, y, desiredWidth, desiredHeight);
    }

    float GetStoredSpread(int id)
    {
        return id >= 0 && spreadByCluster.TryGetValue(id, out float value) ? value : DefaultSpreadValue;
    }

    float GetStoredThickness(int id)
    {
        return id >= 0 && thicknessByCluster.TryGetValue(id, out float value) ? value : 1f;
    }

    float GetStoredLength(int id)
    {
        return id >= 0 && lengthByCluster.TryGetValue(id, out float value) ? value : 1f;
    }

    TextureEditorManager.HairTextureCluster GetActiveCluster()
    {
        if (clustersField == null || activeIndexField == null || manager == null) return null;
        var list = clustersField.GetValue(manager) as List<TextureEditorManager.HairTextureCluster>;
        if (list == null) return null;
        int index = (int)activeIndexField.GetValue(manager);
        if (index < 0 || index >= list.Count) return null;
        return list[index];
    }

    void ClearOldActiveRect()
    {
        TextureEditorManager.HairTextureCluster c = GetActiveCluster();
        Texture2D tex = textureField != null ? textureField.GetValue(manager) as Texture2D : null;
        if (c == null || tex == null || !c.placed) return;

        Color32[] pixels = tex.GetPixels32();
        RectInt r = c.pixelRect;
        int minX = Mathf.Clamp(r.xMin, 0, tex.width);
        int maxX = Mathf.Clamp(r.xMax, 0, tex.width);
        int minY = Mathf.Clamp(r.yMin, 0, tex.height);
        int maxY = Mathf.Clamp(r.yMax, 0, tex.height);
        Color32 black = new Color32(0, 0, 0, 255);
        for (int y = minY; y < maxY; y++)
            for (int x = minX; x < maxX; x++)
                pixels[y * tex.width + x] = black;
        tex.SetPixels32(pixels);
        tex.Apply(true, false);
    }

    void ApplyControlSpacing(GameObject panel)
    {
        if (panel == null) return;
        VerticalLayoutGroup layout = panel.GetComponent<VerticalLayoutGroup>();
        if (layout != null)
        {
            // Deliberately generous: approximately another compact label+slider row between controls.
            layout.spacing = 28f;
            layout.padding = new RectOffset(12, 12, 8, 8);
        }
    }

    void RestoreLoadedModelRenderers()
    {
        ModelViewer viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer == null || loadedModelField == null) return;
        GameObject model = loadedModelField.GetValue(viewer) as GameObject;
        if (model == null) return;

        model.SetActive(true);
        foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>(true))
            renderer.enabled = true;
    }

    static TMPro.TextMeshProUGUI GetTMP(Transform root, string name)
    {
        Transform t = FindRecursive(root, name);
        return t != null ? t.GetComponent<TMPro.TextMeshProUGUI>() : null;
    }

    static void UpdateLabel(TMPro.TextMeshProUGUI label, string name, float value)
    {
        if (label != null) label.text = name + ": " + value.ToString("F3");
    }

    static Slider FindSlider(Transform root, string name)
    {
        Transform t = FindRecursive(root, name);
        return t != null ? t.GetComponent<Slider>() : null;
    }

    static Transform FindRecursive(Transform root, string name)
    {
        if (root == null) return null;
        if (root.name == name) return root;
        foreach (Transform child in root)
        {
            Transform hit = FindRecursive(child, name);
            if (hit != null) return hit;
        }
        return null;
    }

    static GameObject FindNamed(string objectName)
    {
        foreach (Transform t in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (t != null && t.name == objectName) return t.gameObject;
        return null;
    }
}
