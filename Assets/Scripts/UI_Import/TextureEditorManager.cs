using UnityEngine;
using UnityEngine.UI;
using System;
using System.IO;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class TextureEditorManager : MonoBehaviour
{
    private GameObject textureSliderPanelGO;
    private GameObject texturePreviewPlane;
    private Material hairCardMaterial;
    private Material previewMaterial;
    private Texture2D generatedTexture;

    // Strand generation parameters for up to 100 strands per group.
    public int currentTextureGroupId = 0;
    public float strandCount = 50f;
    public float waveAmount = 0.1f;
    public float clumpStrength = 0.2f;
    public float taperAmount = 0.5f;
    public float noiseScale = 0.1f;
    public float strandLength = 1.0f;

    [Header("Procedural Texture")]
    [Range(128, 2048)] public int textureResolution = 512;
    public int randomSeed = 12345;

    public void Init(Material mat)
    {
        hairCardMaterial = mat;
    }

    public void SetPanelActive(bool active, Transform parentCanvas, Action onSwitchToGroom)
    {
        if (textureSliderPanelGO == null && active)
        {
            BuildTextureEditorUI(parentCanvas, onSwitchToGroom);
        }
        else if (textureSliderPanelGO != null)
        {
            textureSliderPanelGO.SetActive(active);
        }

        if (active)
        {
            EnsurePreviewPlane();
            texturePreviewPlane.SetActive(true);

            if (generatedTexture == null)
                GenerateTexture();
        }
        else if (texturePreviewPlane != null)
        {
            texturePreviewPlane.SetActive(false);
        }
    }

    private void EnsurePreviewPlane()
    {
        if (texturePreviewPlane != null)
            return;

        texturePreviewPlane = GameObject.CreatePrimitive(PrimitiveType.Quad);
        texturePreviewPlane.name = "HairTexturePreviewPlane";
        texturePreviewPlane.transform.position = new Vector3(0f, 0f, 1.5f);
        texturePreviewPlane.transform.localScale = new Vector3(0.6f, 1.2f, 1.0f);

        MeshFilter meshFilter = texturePreviewPlane.GetComponent<MeshFilter>();
        if (meshFilter != null && meshFilter.sharedMesh != null)
        {
            // Do not edit Unity's shared primitive mesh asset in place.
            Mesh mesh = Instantiate(meshFilter.sharedMesh);
            mesh.name = "HairTexturePreviewQuadMesh";
            mesh.uv = new Vector2[]
            {
                new Vector2(0, 0),
                new Vector2(1, 0),
                new Vector2(0, 1),
                new Vector2(1, 1)
            };
            meshFilter.sharedMesh = mesh;
        }

        MeshRenderer renderer = texturePreviewPlane.GetComponent<MeshRenderer>();
        if (hairCardMaterial != null)
        {
            previewMaterial = new Material(hairCardMaterial);
            previewMaterial.name = "HairTexturePreviewMaterial (Runtime)";
            renderer.sharedMaterial = previewMaterial;
        }
    }

    private void BuildTextureEditorUI(Transform parentCanvas, Action onSwitchToGroom)
    {
        GameObject panelGO = new GameObject("TextureEditorPanel", typeof(RectTransform), typeof(Image), typeof(GraphicRaycaster));
        panelGO.transform.SetParent(parentCanvas, false);

        RectTransform panelRect = panelGO.GetComponent<RectTransform>();
        panelRect.anchorMin = new Vector2(1, 0);
        panelRect.anchorMax = new Vector2(1, 1);
        panelRect.pivot = new Vector2(1, 0.5f);
        panelRect.sizeDelta = new Vector2(560, 0);
        panelRect.anchoredPosition = new Vector2(-10, 0);

        Image bgImage = panelGO.GetComponent<Image>();
        bgImage.color = new Color(0.15f, 0.15f, 0.15f, 0.85f);

        VerticalLayoutGroup layout = panelGO.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(15, 15, 12, 12);
        layout.spacing = 6;
        layout.childControlWidth = true;
        layout.childControlHeight = false;

        textureSliderPanelGO = panelGO;

        GameObject tabRowGO = new GameObject("PanelTabRow", typeof(RectTransform));
        tabRowGO.transform.SetParent(panelGO.transform, false);
        tabRowGO.GetComponent<RectTransform>().sizeDelta = new Vector2(0, 45);

        HorizontalLayoutGroup tabLayout = tabRowGO.AddComponent<HorizontalLayoutGroup>();
        tabLayout.spacing = 8;
        tabLayout.childControlWidth = true;
        tabLayout.childControlHeight = true;

        GameObject groomTabGO = CreateButton(tabRowGO.transform, "GroomTabButton", "Groom Mode", new Color(0.25f, 0.25f, 0.25f));
        groomTabGO.GetComponent<Button>().onClick.AddListener(() => onSwitchToGroom?.Invoke());
        CreateButton(tabRowGO.transform, "TexTabButton", "Texture Editor", new Color(0.2f, 0.5f, 0.8f));

        CreateSliderUI(panelGO.transform, "Strand Count", 1f, 100f, strandCount, val =>
        {
            strandCount = Mathf.Round(val);
            GenerateTexture();
        }, out Slider countSlider, 38, 16);
        countSlider.wholeNumbers = true;

        CreateSliderUI(panelGO.transform, "Wave Amount", 0.0f, 1.0f, waveAmount, val =>
        {
            waveAmount = val;
            GenerateTexture();
        }, out _, 38, 16);

        CreateSliderUI(panelGO.transform, "Clump Strength", 0.0f, 1.0f, clumpStrength, val =>
        {
            clumpStrength = val;
            GenerateTexture();
        }, out _, 38, 16);

        CreateSliderUI(panelGO.transform, "Taper Amount", 0.0f, 1.0f, taperAmount, val =>
        {
            taperAmount = val;
            GenerateTexture();
        }, out _, 38, 16);

        CreateSliderUI(panelGO.transform, "Noise Scale", 0.0f, 1.0f, noiseScale, val =>
        {
            noiseScale = val;
            GenerateTexture();
        }, out _, 38, 16);

        CreateSliderUI(panelGO.transform, "Strand Length", 0.1f, 2.0f, strandLength, val =>
        {
            strandLength = val;
            GenerateTexture();
        }, out _, 38, 16);

        GameObject actionRow = new GameObject("TextureActionRow", typeof(RectTransform));
        actionRow.transform.SetParent(panelGO.transform, false);
        actionRow.GetComponent<RectTransform>().sizeDelta = new Vector2(0, 42);

        HorizontalLayoutGroup actionLayout = actionRow.AddComponent<HorizontalLayoutGroup>();
        actionLayout.spacing = 8;
        actionLayout.childControlWidth = true;
        actionLayout.childControlHeight = true;

        GameObject regenerateGO = CreateButton(actionRow.transform, "RegenerateTextureButton", "Regenerate", new Color(0.22f, 0.45f, 0.25f));
        regenerateGO.GetComponent<Button>().onClick.AddListener(() =>
        {
            randomSeed++;
            GenerateTexture();
        });

        GameObject exportGO = CreateButton(actionRow.transform, "ExportTextureButton", "Export PNG", new Color(0.35f, 0.35f, 0.35f));
        exportGO.GetComponent<Button>().onClick.AddListener(ExportGeneratedTexture);
    }

    /// <summary>
    /// Generates a stable, transparent RGBA hair texture from the current controls.
    /// Parameter edits keep the current seed so artists can tune a shape without the
    /// random layout jumping around; Regenerate advances the seed explicitly.
    /// </summary>
    public void GenerateTexture()
    {
        int resolution = Mathf.Clamp(textureResolution, 128, 2048);
        int count = Mathf.Clamp(Mathf.RoundToInt(strandCount), 1, 100);

        Color32[] pixels = new Color32[resolution * resolution];
        System.Random rng = new System.Random(randomSeed);

        int clumpCount = Mathf.Clamp(
            Mathf.RoundToInt(Mathf.Lerp(count, Mathf.Max(1, count / 8f), clumpStrength)),
            1,
            count);

        float[] clumpCenters = new float[clumpCount];
        for (int c = 0; c < clumpCount; c++)
        {
            float spacing = 1f / clumpCount;
            float jitter = ((float)rng.NextDouble() - 0.5f) * spacing * 0.35f;
            clumpCenters[c] = Mathf.Clamp01((c + 0.5f) * spacing + jitter);
        }

        float strandSpacing = 1f / count;
        float normalizedLength = Mathf.Clamp01(strandLength);
        const int samplesPerStrand = 160;

        for (int i = 0; i < count; i++)
        {
            float rootJitter = ((float)rng.NextDouble() - 0.5f) * strandSpacing * 0.65f;
            float rootX = Mathf.Clamp01((i + 0.5f) * strandSpacing + rootJitter);
            float phase = (float)rng.NextDouble() * Mathf.PI * 2f;
            float frequencyJitter = Mathf.Lerp(0.8f, 1.2f, (float)rng.NextDouble());
            float noiseOffset = (float)rng.NextDouble() * 1000f;

            int clumpIndex = Mathf.Clamp(Mathf.FloorToInt(i * clumpCount / (float)count), 0, clumpCount - 1);
            float clumpX = clumpCenters[clumpIndex];

            for (int sample = 0; sample < samplesPerStrand; sample++)
            {
                float t = sample / (samplesPerStrand - 1f);
                float y = t * normalizedLength;

                float clumpBlend = Mathf.SmoothStep(0f, clumpStrength, t);
                float x = Mathf.Lerp(rootX, clumpX, clumpBlend);

                float waveFrequency = Mathf.Lerp(1.25f, 4.5f, waveAmount) * frequencyJitter;
                x += Mathf.Sin(t * Mathf.PI * 2f * waveFrequency + phase) * waveAmount * 0.025f;

                float perlin = Mathf.PerlinNoise(noiseOffset, t * Mathf.Lerp(1.5f, 12f, noiseScale));
                x += (perlin - 0.5f) * noiseScale * 0.045f;

                float rootWidth = Mathf.Lerp(1.8f, 3.2f, Mathf.Clamp01(strandSpacing * resolution * 0.08f));
                float tipWidth = Mathf.Lerp(rootWidth, 0.45f, taperAmount);
                float radius = Mathf.Lerp(rootWidth, tipWidth, t);

                int px = Mathf.RoundToInt(x * (resolution - 1));
                int py = Mathf.RoundToInt(y * (resolution - 1));
                DrawSoftDisc(pixels, resolution, px, py, radius, t);
            }
        }

        if (generatedTexture == null || generatedTexture.width != resolution || generatedTexture.height != resolution)
        {
            if (generatedTexture != null)
                Destroy(generatedTexture);

            generatedTexture = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false, true)
            {
                name = "HSD_ProceduralHair_ColorAlpha",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
        }

        generatedTexture.SetPixels32(pixels);
        generatedTexture.Apply(false, false);

        EnsurePreviewPlane();
        ApplyGeneratedTextureToPreview();
    }

    private static void DrawSoftDisc(Color32[] pixels, int resolution, int centerX, int centerY, float radius, float strandT)
    {
        int extent = Mathf.Max(1, Mathf.CeilToInt(radius + 1f));
        int minX = Mathf.Max(0, centerX - extent);
        int maxX = Mathf.Min(resolution - 1, centerX + extent);
        int minY = Mathf.Max(0, centerY - extent);
        int maxY = Mathf.Min(resolution - 1, centerY + extent);

        float safeRadius = Mathf.Max(0.5f, radius);
        byte luminance = (byte)Mathf.RoundToInt(Mathf.Lerp(205f, 255f, strandT));

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                float dx = x - centerX;
                float dy = y - centerY;
                float distance = Mathf.Sqrt(dx * dx + dy * dy);
                float coverage = Mathf.Clamp01(safeRadius + 0.75f - distance);
                if (coverage <= 0f)
                    continue;

                int index = y * resolution + x;
                byte alpha = (byte)Mathf.RoundToInt(coverage * 255f);

                if (alpha > pixels[index].a)
                    pixels[index] = new Color32(luminance, luminance, luminance, alpha);
            }
        }
    }

    private void ApplyGeneratedTextureToPreview()
    {
        if (generatedTexture == null || previewMaterial == null)
            return;

        if (previewMaterial.HasProperty("_BaseMap"))
            previewMaterial.SetTexture("_BaseMap", generatedTexture);

        if (previewMaterial.HasProperty("_MainTex"))
            previewMaterial.SetTexture("_MainTex", generatedTexture);

        if (previewMaterial.HasProperty("_BaseColor"))
            previewMaterial.SetColor("_BaseColor", Color.white);

        if (previewMaterial.HasProperty("_Color"))
            previewMaterial.SetColor("_Color", Color.white);
    }

    private void ExportGeneratedTexture()
    {
        if (generatedTexture == null)
            GenerateTexture();

        byte[] png = generatedTexture.EncodeToPNG();
        if (png == null || png.Length == 0)
        {
            Debug.LogError("Texture Editor: failed to encode generated texture as PNG.");
            return;
        }

#if UNITY_EDITOR
        string path = EditorUtility.SaveFilePanel(
            "Export Procedural Hair Texture",
            "",
            "HSD_ProceduralHair_ColorAlpha.png",
            "png");

        if (string.IsNullOrEmpty(path))
            return;
#else
        string path = Path.Combine(Application.persistentDataPath, "HSD_ProceduralHair_ColorAlpha.png");
#endif

        File.WriteAllBytes(path, png);
        Debug.Log("Texture Editor: exported procedural texture to " + path);
    }

    private GameObject CreateButton(Transform parent, string objectName, string label, Color backgroundColor)
    {
        GameObject buttonGO = new GameObject(objectName, typeof(RectTransform), typeof(Image), typeof(Button));
        buttonGO.transform.SetParent(parent, false);
        buttonGO.GetComponent<Image>().color = backgroundColor;

        GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI));
        textGO.transform.SetParent(buttonGO.transform, false);

        TMPro.TextMeshProUGUI tmp = textGO.GetComponent<TMPro.TextMeshProUGUI>();
        tmp.text = label;
        tmp.fontSize = 16;
        tmp.fontStyle = TMPro.FontStyles.Bold;
        tmp.alignment = TMPro.TextAlignmentOptions.Center;
        tmp.color = Color.white;

        RectTransform textRect = textGO.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.sizeDelta = Vector2.zero;

        return buttonGO;
    }

    private GameObject CreateSliderUI(
        Transform parent,
        string labelText,
        float min,
        float max,
        float defaultValue,
        UnityEngine.Events.UnityAction<float> onValueChanged,
        out Slider createdSlider,
        float rowHeight = 44f,
        int fontSize = 16)
    {
        GameObject rowGO = new GameObject(labelText + "_Row", typeof(RectTransform));
        rowGO.transform.SetParent(parent, false);
        rowGO.GetComponent<RectTransform>().sizeDelta = new Vector2(0, rowHeight);

        VerticalLayoutGroup rowLayout = rowGO.AddComponent<VerticalLayoutGroup>();
        rowLayout.spacing = 2;
        rowLayout.padding = new RectOffset(0, 0, 2, 2);
        rowLayout.childControlWidth = true;
        rowLayout.childControlHeight = false;

        GameObject textGO = new GameObject(labelText + "_Text", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI));
        textGO.transform.SetParent(rowGO.transform, false);
        textGO.GetComponent<RectTransform>().sizeDelta = new Vector2(0, 18);

        TMPro.TextMeshProUGUI tmp = textGO.GetComponent<TMPro.TextMeshProUGUI>();
        tmp.text = labelText + ": " + FormatSliderValue(defaultValue, max);
        tmp.fontSize = fontSize;
        tmp.color = Color.white;

        GameObject sliderGO = new GameObject(labelText + "_Slider", typeof(RectTransform), typeof(Slider));
        sliderGO.transform.SetParent(rowGO.transform, false);
        sliderGO.GetComponent<RectTransform>().sizeDelta = new Vector2(0, 18);

        Slider slider = sliderGO.GetComponent<Slider>();
        slider.minValue = min;
        slider.maxValue = max;
        slider.value = defaultValue;

        GameObject backgroundGO = new GameObject("Background", typeof(RectTransform), typeof(Image));
        backgroundGO.transform.SetParent(sliderGO.transform, false);
        backgroundGO.GetComponent<Image>().color = new Color(0.3f, 0.3f, 0.3f);
        RectTransform bgRect = backgroundGO.GetComponent<RectTransform>();
        bgRect.anchorMin = new Vector2(0, 0.3f);
        bgRect.anchorMax = new Vector2(1, 0.7f);
        bgRect.sizeDelta = Vector2.zero;

        GameObject fillAreaGO = new GameObject("Fill Area", typeof(RectTransform));
        fillAreaGO.transform.SetParent(sliderGO.transform, false);
        RectTransform fillAreaRect = fillAreaGO.GetComponent<RectTransform>();
        fillAreaRect.anchorMin = new Vector2(0, 0.3f);
        fillAreaRect.anchorMax = new Vector2(1, 0.7f);
        fillAreaRect.sizeDelta = Vector2.zero;

        GameObject fillGO = new GameObject("Fill", typeof(RectTransform), typeof(Image));
        fillGO.transform.SetParent(fillAreaGO.transform, false);
        fillGO.GetComponent<Image>().color = new Color(0.2f, 0.6f, 1.0f);
        slider.fillRect = fillGO.GetComponent<RectTransform>();
        slider.fillRect.anchorMin = Vector2.zero;
        slider.fillRect.anchorMax = Vector2.zero;
        slider.fillRect.sizeDelta = Vector2.zero;

        GameObject handleAreaGO = new GameObject("Handle Slide Area", typeof(RectTransform));
        handleAreaGO.transform.SetParent(sliderGO.transform, false);
        RectTransform handleAreaRect = handleAreaGO.GetComponent<RectTransform>();
        handleAreaRect.anchorMin = Vector2.zero;
        handleAreaRect.anchorMax = Vector2.one;
        handleAreaRect.sizeDelta = Vector2.zero;

        GameObject handleGO = new GameObject("Handle", typeof(RectTransform), typeof(Image));
        handleGO.transform.SetParent(handleAreaGO.transform, false);
        handleGO.GetComponent<Image>().color = Color.white;
        slider.handleRect = handleGO.GetComponent<RectTransform>();
        slider.handleRect.sizeDelta = new Vector2(20, 0);

        slider.onValueChanged.AddListener(val =>
        {
            tmp.text = labelText + ": " + FormatSliderValue(val, max);
            onValueChanged.Invoke(val);
        });

        createdSlider = slider;
        return rowGO;
    }

    private static string FormatSliderValue(float value, float max)
    {
        return max >= 10f ? Mathf.RoundToInt(value).ToString() : value.ToString("F3");
    }

    private void OnDestroy()
    {
        if (generatedTexture != null)
            Destroy(generatedTexture);
        if (previewMaterial != null)
            Destroy(previewMaterial);
    }
}