using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;
using System.Reflection;

[DefaultExecutionOrder(9500)]
public class TextureGeneratorStrandSemanticsAuthority : MonoBehaviour
{
    const float BaseRadius = 2f;
    const float DefaultSpreadValue = 2f;
    const int DefaultClusterWidth = 760;

    TextureEditorManager manager;
    Slider spreadSlider;
    TMPro.TextMeshProUGUI spreadLabel;
    Button generateButton;
    GameObject boundPanel;
    bool wasGeneratorActive;
    int lastClusterId = int.MinValue;

    readonly Dictionary<int, float> spreadByCluster = new Dictionary<int, float>();

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
            ApplyCompactSpacing(panel);
            SyncActiveClusterSpread();
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
        Transform labelT = FindRecursive(panel.transform, "Strand Width_Text");
        spreadLabel = labelT != null ? labelT.GetComponent<TMPro.TextMeshProUGUI>() : null;

        Transform generateT = FindRecursive(panel.transform, "GENERATE / UPDATEButton");
        generateButton = generateT != null ? generateT.GetComponent<Button>() : null;

        if (spreadSlider != null)
        {
            spreadSlider.onValueChanged.RemoveAllListeners();
            spreadSlider.onValueChanged.AddListener(OnSpreadChanged);
        }

        if (generateButton != null)
        {
            generateButton.onClick.RemoveAllListeners();
            generateButton.onClick.AddListener(GenerateWithSpread);
        }

        lastClusterId = int.MinValue;
    }

    void OnSpreadChanged(float value)
    {
        int id = manager != null ? manager.currentTextureGroupId : -1;
        if (id >= 0) spreadByCluster[id] = value;
        UpdateSpreadLabel(value);
        PrepareActiveClusterGeometry(value, false);
    }

    void SyncActiveClusterSpread()
    {
        int id = manager.currentTextureGroupId;
        if (id < 0) return;

        if (!spreadByCluster.ContainsKey(id))
            spreadByCluster[id] = DefaultSpreadValue;

        float value = spreadByCluster[id];
        if (id != lastClusterId)
        {
            lastClusterId = id;
            if (spreadSlider != null) spreadSlider.SetValueWithoutNotify(value);
            UpdateSpreadLabel(value);
        }

        // Strand Width is now purely cluster X spread. Keep the renderer's base circle radius fixed.
        manager.strandWidth = BaseRadius;
        PrepareActiveClusterGeometry(value, false);
    }

    void GenerateWithSpread()
    {
        if (manager == null) return;
        int id = manager.currentTextureGroupId;
        float spread = id >= 0 && spreadByCluster.TryGetValue(id, out float v) ? v : DefaultSpreadValue;

        ClearOldActiveRect();
        manager.strandWidth = BaseRadius;
        PrepareActiveClusterGeometry(spread, true);
        manager.GenerateOrUpdateActiveCluster();
    }

    void PrepareActiveClusterGeometry(float spreadValue, bool resizePlacedRect)
    {
        TextureEditorManager.HairTextureCluster c = GetActiveCluster();
        if (c == null) return;

        c.strandWidth = BaseRadius;
        float spreadScale = Mathf.Max(0.1f, spreadValue / DefaultSpreadValue);
        int desiredWidth = Mathf.Clamp(Mathf.RoundToInt(DefaultClusterWidth * spreadScale), 180, 3000);
        c.rectWidth = desiredWidth;

        if (!resizePlacedRect || !c.placed) return;

        int size = manager.textureSize;
        int x = Mathf.Clamp(c.rootPixel.x - desiredWidth / 2, 0, Mathf.Max(0, size - desiredWidth));
        c.pixelRect = new RectInt(x, c.pixelRect.y, desiredWidth, c.pixelRect.height);
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

    void ApplyCompactSpacing(GameObject panel)
    {
        if (panel == null) return;
        VerticalLayoutGroup layout = panel.GetComponent<VerticalLayoutGroup>();
        if (layout != null) layout.spacing = 6f;
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

    void UpdateSpreadLabel(float value)
    {
        if (spreadLabel != null)
            spreadLabel.text = "Strand Width: " + value.ToString("F3");
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
