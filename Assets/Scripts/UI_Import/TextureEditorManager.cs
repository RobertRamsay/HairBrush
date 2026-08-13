using UnityEngine;
using UnityEngine.UI;
using System;

public class TextureEditorManager : MonoBehaviour
{
    private GameObject textureSliderPanelGO;
    private GameObject texturePreviewPlane;
    private Material hairCardMaterial;
    private Texture2D generatedHairTexture;

    // One generated cluster for the first texture-generation pass.
    public int currentTextureGroupId = 0;
    public float strandCount = 50f;
    public float waveAmount = 0.1f;
    public float clumpStrength = 0.2f;
    public float taperAmount = 0.5f;
    public float noiseScale = 0.1f;
    public float strandLength = 1.0f;
    public float strandWidth = 2.0f;
    public int textureSize = 1024;
    public int generationSeed = 12345;

    public void Init(Material mat)
    {
        hairCardMaterial = mat;
    }

    public void SetPanelActive(bool active, Transform parentCanvas, System.Action onSwitchToGroom)
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
            if (texturePreviewPlane == null)
            {
                texturePreviewPlane = GameObject.CreatePrimitive(PrimitiveType.Quad);
                texturePreviewPlane.name = "HairTexturePreviewPlane";
                texturePreviewPlane.transform.position = new Vector3(0f, 0f, 1.5f);
                texturePreviewPlane.transform.localScale = new Vector3(0.6f, 1.2f, 1.0f);

                MeshFilter meshFilter = texturePreviewPlane.GetComponent<MeshFilter>();
                if (meshFilter != null && meshFilter.sharedMesh != null)
                {
                    Mesh mesh = meshFilter.mesh;
                    mesh.uv = new Vector2[]
                    {
                        new Vector2(0, 0),
                        new Vector2(1, 0),
                        new Vector2(0, 1),
                        new Vector2(1, 1)
                    };
                }

                MeshRenderer mr = texturePreviewPlane.GetComponent<MeshRenderer>();
                if (hairCardMaterial != null)
                    mr.sharedMaterial = hairCardMaterial;
            }
            else
            {
                texturePreviewPlane.SetActive(true);
            }

            if (generatedHairTexture == null)
                GenerateProceduralTexture();
        }
        else if (texturePreviewPlane != null)
        {
            texturePreviewPlane.SetActive(false);
        }
    }

    public void GenerateProceduralTexture()
    {
        int size = Mathf.Clamp(textureSize, 128, 4096);

        if (generatedHairTexture == null || generatedHairTexture.width != size || generatedHairTexture.height != size)
        {
            if (generatedHairTexture != null)
                Destroy(generatedHairTexture);

            generatedHairTexture = new Texture2D(size, size, TextureFormat.RGBA32, true, false);
            generatedHairTexture.name = "GeneratedHairTexture_Runtime";
            generatedHairTexture.wrapMode = TextureWrapMode.Clamp;
            generatedHairTexture.filterMode = FilterMode.Bilinear;
        }

        Color32[] pixels = new Color32[size * size];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = new Color32(0, 0, 0, 0);

        int count = Mathf.Clamp(Mathf.RoundToInt(strandCount), 1, 100);
        System.Random random = new System.Random(generationSeed);

        // First-pass cluster rectangle. Later this becomes an atlas-owned rect with explicit padding.
        float padding = size * 0.06f;
        float minX = padding;
        float maxX = size - 1f - padding;
        float rootY = padding;
        float usableHeight = size - padding * 2f;
        float lengthPixels = Mathf.Clamp01(strandLength / 2.0f) * usableHeight;
        float guidePhase = NextRange(random, 0f, Mathf.PI * 2f);
        float guideWavePixels = waveAmount * size * 0.055f;
        float clusterCentreX = size * 0.5f;

        for (int strandIndex = 0; strandIndex < count; strandIndex++)
        {
            float rootX = Mathf.Lerp(minX, maxX, count <= 1 ? 0.5f : strandIndex / (float)(count - 1));
            rootX += NextRange(random, -4f, 4f);

            float strandLengthVariation = NextRange(random, 0.90f, 1.03f);
            float strandPhase = NextRange(random, 0f, Mathf.PI * 2f);
            float strandWaveScale = NextRange(random, 0.75f, 1.25f);
            float noiseOffset = NextRange(random, 0f, 1000f);
            float widthVariation = NextRange(random, 0.82f, 1.18f);

            float finalLength = Mathf.Min(lengthPixels * strandLengthVariation, usableHeight);
            int samples = Mathf.Clamp(Mathf.CeilToInt(finalLength / 3f), 24, 320);

            for (int sample = 0; sample < samples; sample++)
            {
                float t = samples <= 1 ? 0f : sample / (float)(samples - 1);
                float y = rootY + finalLength * t;

                float guideX = clusterCentreX + Mathf.Sin(t * Mathf.PI * 2f + guidePhase) * guideWavePixels;
                float waveX = Mathf.Sin(t * Mathf.PI * 2f + strandPhase) * guideWavePixels * strandWaveScale;
                float independentX = rootX + waveX;

                float clumpT = Mathf.Clamp01(clumpStrength) * Mathf.SmoothStep(0f, 1f, t);
                float x = Mathf.Lerp(independentX, guideX, clumpT);

                float noiseFrequency = Mathf.Lerp(0.5f, 8f, Mathf.Clamp01(noiseScale));
                float noise = Mathf.PerlinNoise(noiseOffset, t * noiseFrequency) * 2f - 1f;
                x += noise * noiseScale * size * 0.018f;

                float taper = Mathf.Lerp(1f, Mathf.Max(0.08f, 1f - taperAmount), t);
                float radius = Mathf.Max(0.35f, strandWidth * widthVariation * taper);
                StampCircle(pixels, size, x, y, radius);
            }
        }

        generatedHairTexture.SetPixels32(pixels);
        generatedHairTexture.Apply(true, false);
        ApplyGeneratedTextureToHairMaterial();
    }

    private void ApplyGeneratedTextureToHairMaterial()
    {
        if (hairCardMaterial == null || generatedHairTexture == null)
            return;

        if (hairCardMaterial.HasProperty("_BaseMap"))
            hairCardMaterial.SetTexture("_BaseMap", generatedHairTexture);

        if (hairCardMaterial.HasProperty("_MainTex"))
            hairCardMaterial.SetTexture("_MainTex", generatedHairTexture);

        // Preview and all existing cards using this shared material now show the generated texture.
        if (texturePreviewPlane != null)
        {
            MeshRenderer mr = texturePreviewPlane.GetComponent<MeshRenderer>();
            if (mr != null)
                mr.sharedMaterial = hairCardMaterial;
        }
    }

    private static void StampCircle(Color32[] pixels, int size, float cx, float cy, float radius)
    {
        int minX = Mathf.Max(0, Mathf.FloorToInt(cx - radius - 1f));
        int maxX = Mathf.Min(size - 1, Mathf.CeilToInt(cx + radius + 1f));
        int minY = Mathf.Max(0, Mathf.FloorToInt(cy - radius - 1f));
        int maxY = Mathf.Min(size - 1, Mathf.CeilToInt(cy + radius + 1f));

        float feather = Mathf.Max(0.75f, radius * 0.45f);
        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                float dx = (x + 0.5f) - cx;
                float dy = (y + 0.5f) - cy;
                float distance = Mathf.Sqrt(dx * dx + dy * dy);
                float alpha = 1f - Mathf.SmoothStep(Mathf.Max(0f, radius - feather), radius + feather, distance);
                if (alpha <= 0f) continue;

                int index = y * size + x;
                byte oldAlpha = pixels[index].a;
                byte newAlpha = (byte)Mathf.Clamp(Mathf.RoundToInt(alpha * 255f), 0, 255);
                byte combinedAlpha = (byte)Mathf.Max(oldAlpha, newAlpha);
                pixels[index] = new Color32(255, 255, 255, combinedAlpha);
            }
        }
    }

    private static float NextRange(System.Random random, float min, float max)
    {
        return Mathf.Lerp(min, max, (float)random.NextDouble());
    }

    void BuildTextureEditorUI(Transform parentCanvas, System.Action onSwitchToGroom)
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

        HorizontalLayoutGroup hLayout = tabRowGO.AddComponent<HorizontalLayoutGroup>();
        hLayout.spacing = 8;
        hLayout.childControlWidth = true;
        hLayout.childControlHeight = true;

        GameObject groomTabGO = new GameObject("GroomTabButton", typeof(RectTransform), typeof(Image), typeof(Button));
        groomTabGO.transform.SetParent(tabRowGO.transform, false);
        groomTabGO.GetComponent<Image>().color = new Color(0.25f, 0.25f, 0.25f);
        Button groomBtn = groomTabGO.GetComponent<Button>();
        CreateButtonLabel(groomTabGO.transform, "Groom Mode");

        GameObject texTabGO = new GameObject("TexTabButton", typeof(RectTransform), typeof(Image), typeof(Button));
        texTabGO.transform.SetParent(tabRowGO.transform, false);
        texTabGO.GetComponent<Image>().color = new Color(0.2f, 0.5f, 0.8f);
        CreateButtonLabel(texTabGO.transform, "Texture Editor");

        groomBtn.onClick.AddListener(() => onSwitchToGroom?.Invoke());

        CreateSliderUI(panelGO.transform, "Strand Count", 1f, 100f, strandCount, val => strandCount = val, out _, 38, 16);
        CreateSliderUI(panelGO.transform, "Strand Width", 0.5f, 8.0f, strandWidth, val => strandWidth = val, out _, 38, 16);
        CreateSliderUI(panelGO.transform, "Wave Amount", 0.0f, 1.0f, waveAmount, val => waveAmount = val, out _, 38, 16);
        CreateSliderUI(panelGO.transform, "Clump Strength", 0.0f, 1.0f, clumpStrength, val => clumpStrength = val, out _, 38, 16);
        CreateSliderUI(panelGO.transform, "Taper Amount", 0.0f, 1.0f, taperAmount, val => taperAmount = val, out _, 38, 16);
        CreateSliderUI(panelGO.transform, "Noise Scale", 0.0f, 1.0f, noiseScale, val => noiseScale = val, out _, 38, 16);
        CreateSliderUI(panelGO.transform, "Strand Length", 0.1f, 2.0f, strandLength, val => strandLength = val, out _, 38, 16);

        CreateActionButton(panelGO.transform, "Regenerate", GenerateProceduralTexture);
    }

    private void CreateButtonLabel(Transform parent, string text)
    {
        GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI));
        textGO.transform.SetParent(parent, false);
        TMPro.TextMeshProUGUI tmp = textGO.GetComponent<TMPro.TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = 16;
        tmp.fontStyle = TMPro.FontStyles.Bold;
        tmp.alignment = TMPro.TextAlignmentOptions.Center;
        tmp.color = Color.white;
        RectTransform rect = textGO.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.sizeDelta = Vector2.zero;
    }

    private void CreateActionButton(Transform parent, string label, UnityEngine.Events.UnityAction action)
    {
        GameObject buttonGO = new GameObject(label + "Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        buttonGO.transform.SetParent(parent, false);
        buttonGO.GetComponent<Image>().color = new Color(0.2f, 0.5f, 0.8f);
        LayoutElement layout = buttonGO.GetComponent<LayoutElement>();
        layout.minHeight = 42f;
        layout.preferredHeight = 42f;
        Button button = buttonGO.GetComponent<Button>();
        button.onClick.AddListener(action);
        CreateButtonLabel(buttonGO.transform, label);
    }

    GameObject CreateSliderUI(Transform parent, string labelText, float min, float max, float defaultValue, UnityEngine.Events.UnityAction<float> onValueChanged, out Slider createdSlider, float rowHeight = 44f, int fontSize = 16)
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
        tmp.text = labelText + ": " + defaultValue.ToString("F3");
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
            tmp.text = labelText + ": " + val.ToString("F3");
            onValueChanged.Invoke(val);
        });

        createdSlider = slider;
        return rowGO;
    }
}