using UnityEngine;
using UnityEngine.UI;
using System;
using System.Collections.Generic;

public class TextureEditorManager : MonoBehaviour
{
    [Serializable]
    public class HairTextureCluster
    {
        public int id;
        public string name;
        public int strandCount = 50;
        public float strandWidth = 2f;
        public float strandLength = 1f;
        public float waveAmount = 0.1f;
        public float clumpStrength = 0.2f;
        public float taperAmount = 0.5f;
        public float noiseScale = 0.1f;
        public int seed = 12345;
        public int padding = 48;
        public RectInt pixelRect;
        public bool generated;
    }

    private GameObject textureSliderPanelGO;
    private GameObject texturePreviewPlane;
    private Transform clusterListRoot;
    private Material sourceHairCardMaterial;
    private Material generatedHairMaterial;
    private Material hairCardMaterial;
    private Texture2D generatedHairTexture;

    private readonly List<HairTextureCluster> clusters = new List<HairTextureCluster>();
    private int activeClusterIndex = -1;

    private Slider strandCountSlider;
    private Slider strandWidthSlider;
    private Slider strandLengthSlider;
    private Slider waveSlider;
    private Slider clumpSlider;
    private Slider taperSlider;
    private Slider noiseSlider;

    public int currentTextureGroupId = 0;
    public float strandCount = 50f;
    public float waveAmount = 0.1f;
    public float clumpStrength = 0.2f;
    public float taperAmount = 0.5f;
    public float noiseScale = 0.1f;
    public float strandLength = 1.0f;
    public float strandWidth = 2.0f;
    public int textureSize = 4096;
    public int generationSeed = 12345;

    public void Init(Material mat)
    {
        sourceHairCardMaterial = mat;

        if (generatedHairMaterial != null)
            Destroy(generatedHairMaterial);

        if (sourceHairCardMaterial != null)
        {
            generatedHairMaterial = new Material(sourceHairCardMaterial);
            generatedHairMaterial.name = sourceHairCardMaterial.name + "_Generated_Runtime";
            hairCardMaterial = generatedHairMaterial;

            ModelViewer viewer = GetComponent<ModelViewer>();
            if (viewer != null)
                viewer.hairCardMaterial = generatedHairMaterial;
        }

        EnsureAtlas();
        EnsureFirstCluster();
    }

    private void OnDestroy()
    {
        if (generatedHairTexture != null) Destroy(generatedHairTexture);
        if (generatedHairMaterial != null) Destroy(generatedHairMaterial);
    }

    public void SetPanelActive(bool active, Transform parentCanvas, System.Action onSwitchToGroom)
    {
        if (textureSliderPanelGO == null && active)
            BuildTextureEditorUI(parentCanvas, onSwitchToGroom);
        else if (textureSliderPanelGO != null)
            textureSliderPanelGO.SetActive(active);

        if (active)
        {
            EnsureAtlas();
            EnsureFirstCluster();

            if (texturePreviewPlane == null)
            {
                texturePreviewPlane = GameObject.CreatePrimitive(PrimitiveType.Quad);
                texturePreviewPlane.name = "HairTexturePreviewPlane";
                texturePreviewPlane.transform.position = new Vector3(0f, 0f, 1.5f);
                texturePreviewPlane.transform.localScale = new Vector3(0.9f, 0.9f, 1f);

                MeshFilter meshFilter = texturePreviewPlane.GetComponent<MeshFilter>();
                if (meshFilter != null && meshFilter.sharedMesh != null)
                {
                    Mesh mesh = meshFilter.mesh;
                    mesh.uv = new Vector2[]
                    {
                        new Vector2(0, 0), new Vector2(1, 0),
                        new Vector2(0, 1), new Vector2(1, 1)
                    };
                }
            }
            else
            {
                texturePreviewPlane.SetActive(true);
            }

            ApplyGeneratedTextureToHairMaterial();
        }
        else if (texturePreviewPlane != null)
        {
            texturePreviewPlane.SetActive(false);
        }
    }

    private void EnsureAtlas()
    {
        const int size = 4096;
        textureSize = size;

        if (generatedHairTexture != null && generatedHairTexture.width == size)
            return;

        if (generatedHairTexture != null)
            Destroy(generatedHairTexture);

        generatedHairTexture = new Texture2D(size, size, TextureFormat.RGBA32, true, false);
        generatedHairTexture.name = "GeneratedHairAtlas_4096_Runtime";
        generatedHairTexture.wrapMode = TextureWrapMode.Clamp;
        generatedHairTexture.filterMode = FilterMode.Bilinear;

        Color32[] pixels = new Color32[size * size];
        Color32 black = new Color32(0, 0, 0, 255);
        for (int i = 0; i < pixels.Length; i++) pixels[i] = black;
        generatedHairTexture.SetPixels32(pixels);
        generatedHairTexture.Apply(true, false);
        ApplyGeneratedTextureToHairMaterial();
    }

    private void EnsureFirstCluster()
    {
        if (clusters.Count > 0) return;
        AddClusterInternal();
        SelectCluster(0);
    }

    private void AddClusterInternal()
    {
        if (clusters.Count >= 16)
        {
            Debug.LogWarning("Procedural atlas MVP currently supports 16 cluster slots.");
            return;
        }

        int id = clusters.Count;
        HairTextureCluster cluster = new HairTextureCluster();
        cluster.id = id;
        cluster.name = "Cluster " + id;
        cluster.seed = generationSeed + id * 7919;
        cluster.pixelRect = AllocateClusterRect(id);
        clusters.Add(cluster);
    }

    private RectInt AllocateClusterRect(int index)
    {
        const int columns = 4;
        const int rows = 4;
        int slotW = textureSize / columns;
        int slotH = textureSize / rows;
        int x = (index % columns) * slotW;
        int y = (index / columns) * slotH;
        return new RectInt(x, y, slotW, slotH);
    }

    private void NewCluster()
    {
        int before = clusters.Count;
        AddClusterInternal();
        if (clusters.Count > before)
        {
            SelectCluster(clusters.Count - 1);
            RebuildClusterList();
        }
    }

    private void SelectCluster(int index)
    {
        if (index < 0 || index >= clusters.Count) return;
        activeClusterIndex = index;
        HairTextureCluster c = clusters[index];
        currentTextureGroupId = c.id;
        strandCount = c.strandCount;
        strandWidth = c.strandWidth;
        strandLength = c.strandLength;
        waveAmount = c.waveAmount;
        clumpStrength = c.clumpStrength;
        taperAmount = c.taperAmount;
        noiseScale = c.noiseScale;
        generationSeed = c.seed;
        SyncSlidersFromActiveCluster();
        RebuildClusterList();
    }

    private void CommitControlsToActiveCluster()
    {
        if (activeClusterIndex < 0 || activeClusterIndex >= clusters.Count) return;
        HairTextureCluster c = clusters[activeClusterIndex];
        c.strandCount = Mathf.Clamp(Mathf.RoundToInt(strandCount), 1, 100);
        c.strandWidth = strandWidth;
        c.strandLength = strandLength;
        c.waveAmount = waveAmount;
        c.clumpStrength = clumpStrength;
        c.taperAmount = taperAmount;
        c.noiseScale = noiseScale;
        c.seed = generationSeed;
    }

    public void GenerateProceduralTexture()
    {
        GenerateOrUpdateActiveCluster();
    }

    public void GenerateOrUpdateActiveCluster()
    {
        EnsureAtlas();
        EnsureFirstCluster();
        CommitControlsToActiveCluster();

        HairTextureCluster c = clusters[activeClusterIndex];
        Color32[] pixels = generatedHairTexture.GetPixels32();
        ClearRect(pixels, textureSize, c.pixelRect);
        DrawCluster(pixels, textureSize, c);
        c.generated = true;

        generatedHairTexture.SetPixels32(pixels);
        generatedHairTexture.Apply(true, false);
        ApplyGeneratedTextureToHairMaterial();
        RebuildClusterList();
    }

    private void DrawCluster(Color32[] pixels, int size, HairTextureCluster c)
    {
        RectInt r = c.pixelRect;
        float pad = Mathf.Clamp(c.padding, 8, Mathf.Min(r.width, r.height) / 3);
        float minX = r.xMin + pad;
        float maxX = r.xMax - pad - 1f;
        float rootY = r.yMin + pad;
        float usableHeight = r.height - pad * 2f;
        float lengthPixels = Mathf.Clamp01(c.strandLength / 2.0f) * usableHeight;
        float centreX = r.center.x;
        float guideWavePixels = c.waveAmount * r.width * 0.12f;

        System.Random random = new System.Random(c.seed);
        float guidePhase = NextRange(random, 0f, Mathf.PI * 2f);
        int count = Mathf.Clamp(c.strandCount, 1, 100);

        for (int strandIndex = 0; strandIndex < count; strandIndex++)
        {
            float rootX = Mathf.Lerp(minX, maxX, count <= 1 ? 0.5f : strandIndex / (float)(count - 1));
            rootX += NextRange(random, -8f, 8f);

            float finalLength = Mathf.Min(lengthPixels * NextRange(random, 0.90f, 1.03f), usableHeight);
            float strandPhase = NextRange(random, 0f, Mathf.PI * 2f);
            float strandWaveScale = NextRange(random, 0.75f, 1.25f);
            float noiseOffset = NextRange(random, 0f, 1000f);
            float widthVariation = NextRange(random, 0.82f, 1.18f);
            int samples = Mathf.Clamp(Mathf.CeilToInt(finalLength / 1.5f), 32, 900);

            for (int sample = 0; sample < samples; sample++)
            {
                float t = samples <= 1 ? 0f : sample / (float)(samples - 1);
                float y = rootY + finalLength * t;
                float guideX = centreX + Mathf.Sin(t * Mathf.PI * 2f + guidePhase) * guideWavePixels;
                float waveX = Mathf.Sin(t * Mathf.PI * 2f + strandPhase) * guideWavePixels * strandWaveScale;
                float independentX = rootX + waveX;
                float clumpT = Mathf.Clamp01(c.clumpStrength) * Mathf.SmoothStep(0f, 1f, t);
                float x = Mathf.Lerp(independentX, guideX, clumpT);

                float noiseFrequency = Mathf.Lerp(0.5f, 8f, Mathf.Clamp01(c.noiseScale));
                float noise = Mathf.PerlinNoise(noiseOffset, t * noiseFrequency) * 2f - 1f;
                x += noise * c.noiseScale * r.width * 0.04f;

                float taper = Mathf.Lerp(1f, Mathf.Max(0.08f, 1f - c.taperAmount), t);
                float radius = Mathf.Max(0.5f, c.strandWidth * widthVariation * taper);
                StampCircle(pixels, size, x, y, radius);
            }
        }
    }

    private static void ClearRect(Color32[] pixels, int size, RectInt rect)
    {
        Color32 black = new Color32(0, 0, 0, 255);
        for (int y = rect.yMin; y < rect.yMax; y++)
            for (int x = rect.xMin; x < rect.xMax; x++)
                pixels[y * size + x] = black;
    }

    private void ApplyGeneratedTextureToHairMaterial()
    {
        if (generatedHairMaterial == null || generatedHairTexture == null) return;
        if (generatedHairMaterial.HasProperty("_BaseMap")) generatedHairMaterial.SetTexture("_BaseMap", generatedHairTexture);
        if (generatedHairMaterial.HasProperty("_MainTex")) generatedHairMaterial.SetTexture("_MainTex", generatedHairTexture);
        if (generatedHairMaterial.HasProperty("_BaseColor")) generatedHairMaterial.SetColor("_BaseColor", Color.white);
        if (generatedHairMaterial.HasProperty("_Color")) generatedHairMaterial.SetColor("_Color", Color.white);

        if (texturePreviewPlane != null)
        {
            MeshRenderer mr = texturePreviewPlane.GetComponent<MeshRenderer>();
            if (mr != null) mr.sharedMaterial = generatedHairMaterial;
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
                float coverage = 1f - Mathf.SmoothStep(Mathf.Max(0f, radius - feather), radius + feather, distance);
                if (coverage <= 0f) continue;
                int index = y * size + x;
                byte white = (byte)Mathf.Clamp(Mathf.RoundToInt(coverage * 255f), 0, 255);
                byte value = (byte)Mathf.Max(pixels[index].r, white);
                pixels[index] = new Color32(value, value, value, 255);
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
        panelGO.GetComponent<Image>().color = new Color(0.12f, 0.12f, 0.12f, 0.94f);

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
        CreateButtonLabel(texTabGO.transform, "Texture Generator");
        groomBtn.onClick.AddListener(() => onSwitchToGroom?.Invoke());

        CreateSectionLabel(panelGO.transform, "PROCEDURAL HAIR ATLAS  •  4096 x 4096");
        CreateSectionLabel(panelGO.transform, "CLUSTERS");

        GameObject clusterListGO = new GameObject("ClusterList", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(LayoutElement));
        clusterListGO.transform.SetParent(panelGO.transform, false);
        clusterListGO.GetComponent<LayoutElement>().preferredHeight = 150f;
        VerticalLayoutGroup cl = clusterListGO.GetComponent<VerticalLayoutGroup>();
        cl.spacing = 4;
        cl.childControlHeight = false;
        cl.childControlWidth = true;
        clusterListRoot = clusterListGO.transform;
        RebuildClusterList();

        CreateActionButton(panelGO.transform, "+ New Cluster", NewCluster);
        CreateSectionLabel(panelGO.transform, "ACTIVE CLUSTER PARAMETERS");

        CreateSliderUI(panelGO.transform, "Strand Count", 1f, 100f, strandCount, val => strandCount = val, out strandCountSlider, 38, 15);
        CreateSliderUI(panelGO.transform, "Strand Width", 0.5f, 8f, strandWidth, val => strandWidth = val, out strandWidthSlider, 38, 15);
        CreateSliderUI(panelGO.transform, "Strand Length", 0.1f, 2f, strandLength, val => strandLength = val, out strandLengthSlider, 38, 15);
        CreateSliderUI(panelGO.transform, "Wave Amount", 0f, 1f, waveAmount, val => waveAmount = val, out waveSlider, 38, 15);
        CreateSliderUI(panelGO.transform, "Clump Strength", 0f, 1f, clumpStrength, val => clumpStrength = val, out clumpSlider, 38, 15);
        CreateSliderUI(panelGO.transform, "Taper Amount", 0f, 1f, taperAmount, val => taperAmount = val, out taperSlider, 38, 15);
        CreateSliderUI(panelGO.transform, "Noise Scale", 0f, 1f, noiseScale, val => noiseScale = val, out noiseSlider, 38, 15);

        CreateActionButton(panelGO.transform, "Generate / Update Cluster", GenerateOrUpdateActiveCluster);
        SyncSlidersFromActiveCluster();
    }

    private void RebuildClusterList()
    {
        if (clusterListRoot == null) return;
        for (int i = clusterListRoot.childCount - 1; i >= 0; i--)
            Destroy(clusterListRoot.GetChild(i).gameObject);

        for (int i = 0; i < clusters.Count; i++)
        {
            int captured = i;
            HairTextureCluster c = clusters[i];
            string state = c.generated ? "  ✓" : "";
            string text = c.name + "   " + c.strandCount + " strands" + state;
            GameObject b = CreateActionButton(clusterListRoot, text, () => SelectCluster(captured));
            Image img = b.GetComponent<Image>();
            if (img != null) img.color = i == activeClusterIndex ? new Color(0.18f, 0.42f, 0.68f) : new Color(0.22f, 0.22f, 0.22f);
        }
    }

    private void SyncSlidersFromActiveCluster()
    {
        if (strandCountSlider != null) strandCountSlider.SetValueWithoutNotify(strandCount);
        if (strandWidthSlider != null) strandWidthSlider.SetValueWithoutNotify(strandWidth);
        if (strandLengthSlider != null) strandLengthSlider.SetValueWithoutNotify(strandLength);
        if (waveSlider != null) waveSlider.SetValueWithoutNotify(waveAmount);
        if (clumpSlider != null) clumpSlider.SetValueWithoutNotify(clumpStrength);
        if (taperSlider != null) taperSlider.SetValueWithoutNotify(taperAmount);
        if (noiseSlider != null) noiseSlider.SetValueWithoutNotify(noiseScale);
    }

    private void CreateSectionLabel(Transform parent, string text)
    {
        GameObject go = new GameObject("Section_" + text, typeof(RectTransform), typeof(TMPro.TextMeshProUGUI), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        go.GetComponent<LayoutElement>().preferredHeight = 24f;
        TMPro.TextMeshProUGUI tmp = go.GetComponent<TMPro.TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = 15f;
        tmp.fontStyle = TMPro.FontStyles.Bold;
        tmp.color = new Color(0.45f, 0.75f, 1f);
        tmp.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
    }

    private void CreateButtonLabel(Transform parent, string text)
    {
        GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI));
        textGO.transform.SetParent(parent, false);
        TMPro.TextMeshProUGUI tmp = textGO.GetComponent<TMPro.TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = 15;
        tmp.fontStyle = TMPro.FontStyles.Bold;
        tmp.alignment = TMPro.TextAlignmentOptions.Center;
        tmp.color = Color.white;
        RectTransform rect = textGO.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.sizeDelta = Vector2.zero;
    }

    private GameObject CreateActionButton(Transform parent, string label, UnityEngine.Events.UnityAction action)
    {
        GameObject buttonGO = new GameObject(label + "Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        buttonGO.transform.SetParent(parent, false);
        buttonGO.GetComponent<Image>().color = new Color(0.2f, 0.5f, 0.8f);
        LayoutElement le = buttonGO.GetComponent<LayoutElement>();
        le.minHeight = 36f;
        le.preferredHeight = 36f;
        Button button = buttonGO.GetComponent<Button>();
        button.onClick.AddListener(action);
        CreateButtonLabel(buttonGO.transform, label);
        return buttonGO;
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