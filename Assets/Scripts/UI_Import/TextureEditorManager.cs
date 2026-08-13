using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
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
        public int rectWidth = 760;
        public int rectHeight = 1800;
        public RectInt pixelRect;
        public bool placed;
        public bool generated;
    }

    private GameObject leftClusterPanelGO;
    private GameObject rightControlPanelGO;
    private GameObject texturePreviewPlane;
    private Transform clusterListRoot;
    private TMPro.TextMeshProUGUI placementStatusText;

    private Material sourceHairCardMaterial;
    private Material generatedHairMaterial;
    private Material hairCardMaterial;
    private Texture2D generatedHairTexture;

    private readonly List<HairTextureCluster> clusters = new List<HairTextureCluster>();
    private int activeClusterIndex = -1;
    private int nextClusterId = 0;
    private bool placementMode = false;
    private bool panelActive = false;

    private Slider strandCountSlider;
    private Slider strandWidthSlider;
    private Slider strandLengthSlider;
    private Slider waveSlider;
    private Slider clumpSlider;
    private Slider taperSlider;
    private Slider noiseSlider;

    public int currentTextureGroupId = -1;
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
    }

    private void OnDestroy()
    {
        if (generatedHairTexture != null) Destroy(generatedHairTexture);
        if (generatedHairMaterial != null) Destroy(generatedHairMaterial);
    }

    private void Update()
    {
        if (!panelActive || !placementMode || Mouse.current == null)
            return;

        if (!Mouse.current.leftButton.wasPressedThisFrame)
            return;

        if (activeClusterIndex < 0 || activeClusterIndex >= clusters.Count || texturePreviewPlane == null)
            return;

        Camera cam = null;
        ModelViewer viewer = GetComponent<ModelViewer>();
        if (viewer != null) cam = viewer.mainCamera;
        if (cam == null) cam = Camera.main;
        if (cam == null) return;

        Ray ray = cam.ScreenPointToRay(Mouse.current.position.ReadValue());
        RaycastHit hit;
        Collider previewCollider = texturePreviewPlane.GetComponent<Collider>();
        if (previewCollider == null || !previewCollider.Raycast(ray, out hit, 10000f))
            return;

        Vector2 uv = hit.textureCoord;
        PlaceActiveClusterAtUV(uv);
    }

    public void SetPanelActive(bool active, Transform parentCanvas, System.Action onSwitchToGroom)
    {
        panelActive = active;

        if (leftClusterPanelGO == null && active)
            BuildTextureEditorUI(parentCanvas, onSwitchToGroom);

        if (leftClusterPanelGO != null) leftClusterPanelGO.SetActive(active);
        if (rightControlPanelGO != null) rightControlPanelGO.SetActive(active);

        if (active)
        {
            EnsureAtlas();

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
            RefreshPlacementStatus();
        }
        else
        {
            placementMode = false;
            if (texturePreviewPlane != null) texturePreviewPlane.SetActive(false);
        }
    }

    private void EnsureAtlas()
    {
        const int size = 4096;
        textureSize = size;

        if (generatedHairTexture != null && generatedHairTexture.width == size && generatedHairTexture.height == size)
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

    private void NewCluster()
    {
        HairTextureCluster cluster = new HairTextureCluster();
        cluster.id = nextClusterId++;
        cluster.name = "Cluster " + cluster.id;
        cluster.seed = 12345 + cluster.id * 7919;
        clusters.Add(cluster);

        SelectCluster(clusters.Count - 1);
        placementMode = true;
        RebuildClusterList();
        RefreshPlacementStatus();
    }

    private void PlaceActiveClusterAtUV(Vector2 uv)
    {
        if (activeClusterIndex < 0 || activeClusterIndex >= clusters.Count)
            return;

        HairTextureCluster c = clusters[activeClusterIndex];
        int centreX = Mathf.RoundToInt(Mathf.Clamp01(uv.x) * (textureSize - 1));
        int centreY = Mathf.RoundToInt(Mathf.Clamp01(uv.y) * (textureSize - 1));

        int width = Mathf.Clamp(c.rectWidth, 256, textureSize);
        int height = Mathf.Clamp(c.rectHeight, 256, textureSize);
        int x = centreX - width / 2;
        int y = centreY - height / 2;
        x = Mathf.Clamp(x, 0, textureSize - width);
        y = Mathf.Clamp(y, 0, textureSize - height);

        if (c.generated && c.placed)
            ClearClusterRect(c);

        c.pixelRect = new RectInt(x, y, width, height);
        c.placed = true;
        c.generated = false;
        placementMode = false;

        RebuildClusterList();
        RefreshPlacementStatus();
        Debug.Log(c.name + " placed at atlas rect " + c.pixelRect);
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

        placementMode = false;
        SyncSlidersFromActiveCluster();
        RebuildClusterList();
        RefreshPlacementStatus();
    }

    private void RepositionActiveCluster()
    {
        if (activeClusterIndex < 0 || activeClusterIndex >= clusters.Count)
            return;
        placementMode = true;
        RefreshPlacementStatus();
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

        if (activeClusterIndex < 0 || activeClusterIndex >= clusters.Count)
        {
            Debug.LogWarning("Create and select a cluster first.");
            return;
        }

        HairTextureCluster c = clusters[activeClusterIndex];
        if (!c.placed)
        {
            Debug.LogWarning(c.name + " has not been placed. Click the atlas to place it first.");
            placementMode = true;
            RefreshPlacementStatus();
            return;
        }

        CommitControlsToActiveCluster();

        Color32[] pixels = generatedHairTexture.GetPixels32();
        ClearRect(pixels, textureSize, c.pixelRect);
        DrawCluster(pixels, textureSize, c);
        c.generated = true;

        generatedHairTexture.SetPixels32(pixels);
        generatedHairTexture.Apply(true, false);
        ApplyGeneratedTextureToHairMaterial();
        RebuildClusterList();
        RefreshPlacementStatus();
    }

    private void ClearClusterRect(HairTextureCluster c)
    {
        if (generatedHairTexture == null || c == null || !c.placed) return;
        Color32[] pixels = generatedHairTexture.GetPixels32();
        ClearRect(pixels, textureSize, c.pixelRect);
        generatedHairTexture.SetPixels32(pixels);
        generatedHairTexture.Apply(true, false);
    }

    private void DrawCluster(Color32[] pixels, int size, HairTextureCluster c)
    {
        RectInt r = c.pixelRect;
        float pad = Mathf.Clamp(c.padding, 8, Mathf.Min(r.width, r.height) / 3);
        float minX = r.xMin + pad;
        float maxX = r.xMax - pad - 1f;
        float rootY = r.yMin + pad;
        float usableHeight = Mathf.Max(1f, r.height - pad * 2f);
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
            int samples = Mathf.Clamp(Mathf.CeilToInt(finalLength / 1.5f), 32, 1200);

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
        int minX = Mathf.Clamp(rect.xMin, 0, size);
        int maxX = Mathf.Clamp(rect.xMax, 0, size);
        int minY = Mathf.Clamp(rect.yMin, 0, size);
        int maxY = Mathf.Clamp(rect.yMax, 0, size);
        for (int y = minY; y < maxY; y++)
            for (int x = minX; x < maxX; x++)
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

    private void BuildTextureEditorUI(Transform parentCanvas, System.Action onSwitchToGroom)
    {
        BuildLeftClusterPanel(parentCanvas);
        BuildRightControlPanel(parentCanvas, onSwitchToGroom);
        RebuildClusterList();
        RefreshPlacementStatus();
    }

    private void BuildLeftClusterPanel(Transform parentCanvas)
    {
        leftClusterPanelGO = new GameObject("TextureClusterListPanel", typeof(RectTransform), typeof(Image), typeof(GraphicRaycaster));
        leftClusterPanelGO.transform.SetParent(parentCanvas, false);
        RectTransform rect = leftClusterPanelGO.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 0f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 0.5f);
        rect.sizeDelta = new Vector2(300f, 0f);
        rect.anchoredPosition = new Vector2(10f, 0f);
        leftClusterPanelGO.GetComponent<Image>().color = new Color(0.12f, 0.12f, 0.12f, 0.94f);

        VerticalLayoutGroup layout = leftClusterPanelGO.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(12, 12, 12, 12);
        layout.spacing = 8;
        layout.childControlWidth = true;
        layout.childControlHeight = false;

        CreateHeader(leftClusterPanelGO.transform, "CLUSTERS");

        GameObject listGO = new GameObject("ClusterList", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(LayoutElement));
        listGO.transform.SetParent(leftClusterPanelGO.transform, false);
        LayoutElement le = listGO.GetComponent<LayoutElement>();
        le.flexibleHeight = 1f;
        VerticalLayoutGroup listLayout = listGO.GetComponent<VerticalLayoutGroup>();
        listLayout.spacing = 5f;
        listLayout.childControlWidth = true;
        listLayout.childControlHeight = false;
        clusterListRoot = listGO.transform;

        CreateActionButton(leftClusterPanelGO.transform, "+ New Cluster", NewCluster, 44f);
    }

    private void BuildRightControlPanel(Transform parentCanvas, System.Action onSwitchToGroom)
    {
        rightControlPanelGO = new GameObject("TextureGeneratorControlsPanel", typeof(RectTransform), typeof(Image), typeof(GraphicRaycaster));
        rightControlPanelGO.transform.SetParent(parentCanvas, false);
        RectTransform rect = rightControlPanelGO.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(1f, 0f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(1f, 0.5f);
        rect.sizeDelta = new Vector2(470f, 0f);
        rect.anchoredPosition = new Vector2(-10f, 0f);
        rightControlPanelGO.GetComponent<Image>().color = new Color(0.12f, 0.12f, 0.12f, 0.94f);

        VerticalLayoutGroup layout = rightControlPanelGO.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(14, 14, 12, 12);
        layout.spacing = 6;
        layout.childControlWidth = true;
        layout.childControlHeight = false;

        GameObject tabRow = new GameObject("PanelTabRow", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        tabRow.transform.SetParent(rightControlPanelGO.transform, false);
        tabRow.GetComponent<RectTransform>().sizeDelta = new Vector2(0, 44);
        HorizontalLayoutGroup tabLayout = tabRow.GetComponent<HorizontalLayoutGroup>();
        tabLayout.spacing = 8;
        tabLayout.childControlWidth = true;
        tabLayout.childControlHeight = true;

        CreateActionButton(tabRow.transform, "Groom Mode", () => onSwitchToGroom?.Invoke(), 44f);
        CreateDisabledLabelButton(tabRow.transform, "Texture Generator", 44f);

        CreateHeader(rightControlPanelGO.transform, "ACTIVE CLUSTER CONTROLS");

        GameObject statusGO = new GameObject("PlacementStatus", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI), typeof(LayoutElement));
        statusGO.transform.SetParent(rightControlPanelGO.transform, false);
        statusGO.GetComponent<LayoutElement>().preferredHeight = 42f;
        placementStatusText = statusGO.GetComponent<TMPro.TextMeshProUGUI>();
        placementStatusText.fontSize = 15f;
        placementStatusText.color = Color.white;
        placementStatusText.alignment = TMPro.TextAlignmentOptions.MidlineLeft;

        CreateActionButton(rightControlPanelGO.transform, "Place / Reposition On Atlas", RepositionActiveCluster, 38f);

        CreateSliderUI(rightControlPanelGO.transform, "Strand Count", 1f, 100f, strandCount, v => strandCount = v, out strandCountSlider);
        CreateSliderUI(rightControlPanelGO.transform, "Strand Width", 0.5f, 8f, strandWidth, v => strandWidth = v, out strandWidthSlider);
        CreateSliderUI(rightControlPanelGO.transform, "Strand Length", 0.1f, 2f, strandLength, v => strandLength = v, out strandLengthSlider);
        CreateSliderUI(rightControlPanelGO.transform, "Wave Amount", 0f, 1f, waveAmount, v => waveAmount = v, out waveSlider);
        CreateSliderUI(rightControlPanelGO.transform, "Clump Strength", 0f, 1f, clumpStrength, v => clumpStrength = v, out clumpSlider);
        CreateSliderUI(rightControlPanelGO.transform, "Taper Amount", 0f, 1f, taperAmount, v => taperAmount = v, out taperSlider);
        CreateSliderUI(rightControlPanelGO.transform, "Noise Scale", 0f, 1f, noiseScale, v => noiseScale = v, out noiseSlider);

        CreateActionButton(rightControlPanelGO.transform, "Generate / Update Cluster", GenerateOrUpdateActiveCluster, 48f);
    }

    private void RebuildClusterList()
    {
        if (clusterListRoot == null) return;

        for (int i = clusterListRoot.childCount - 1; i >= 0; i--)
            Destroy(clusterListRoot.GetChild(i).gameObject);

        for (int i = 0; i < clusters.Count; i++)
        {
            int capturedIndex = i;
            HairTextureCluster c = clusters[i];
            bool active = i == activeClusterIndex;

            GameObject buttonGO = new GameObject(c.name + "Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
            buttonGO.transform.SetParent(clusterListRoot, false);
            LayoutElement le = buttonGO.GetComponent<LayoutElement>();
            le.preferredHeight = 58f;
            buttonGO.GetComponent<Image>().color = active ? new Color(0.18f, 0.48f, 0.78f, 1f) : new Color(0.20f, 0.20f, 0.20f, 1f);
            buttonGO.GetComponent<Button>().onClick.AddListener(() => SelectCluster(capturedIndex));

            GameObject labelGO = new GameObject("Text", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI));
            labelGO.transform.SetParent(buttonGO.transform, false);
            RectTransform lr = labelGO.GetComponent<RectTransform>();
            lr.anchorMin = Vector2.zero;
            lr.anchorMax = Vector2.one;
            lr.offsetMin = new Vector2(10f, 4f);
            lr.offsetMax = new Vector2(-8f, -4f);
            TMPro.TextMeshProUGUI tmp = labelGO.GetComponent<TMPro.TextMeshProUGUI>();
            string state = c.generated ? "generated" : (c.placed ? "placed" : "click atlas to place");
            tmp.text = c.name + "\n" + c.strandCount + " strands  •  " + state;
            tmp.fontSize = 14f;
            tmp.color = Color.white;
            tmp.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
            tmp.raycastTarget = false;
        }
    }

    private void RefreshPlacementStatus()
    {
        if (placementStatusText == null) return;

        if (activeClusterIndex < 0 || activeClusterIndex >= clusters.Count)
        {
            placementStatusText.text = "1. New Cluster\n2. Click the atlas to place it";
            placementStatusText.color = new Color(0.75f, 0.75f, 0.75f);
            return;
        }

        HairTextureCluster c = clusters[activeClusterIndex];
        if (placementMode)
        {
            placementStatusText.text = c.name + ": CLICK THE ATLAS TO PLACE";
            placementStatusText.color = new Color(0.25f, 0.75f, 1f);
        }
        else if (!c.placed)
        {
            placementStatusText.text = c.name + ": not placed";
            placementStatusText.color = new Color(1f, 0.72f, 0.25f);
        }
        else
        {
            placementStatusText.text = c.name + "  Rect: " + c.pixelRect.x + ", " + c.pixelRect.y + "  " + c.pixelRect.width + "×" + c.pixelRect.height;
            placementStatusText.color = Color.white;
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

    private void CreateHeader(Transform parent, string text)
    {
        GameObject go = new GameObject(text, typeof(RectTransform), typeof(TMPro.TextMeshProUGUI), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        go.GetComponent<LayoutElement>().preferredHeight = 28f;
        TMPro.TextMeshProUGUI tmp = go.GetComponent<TMPro.TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = 16f;
        tmp.fontStyle = TMPro.FontStyles.Bold;
        tmp.color = new Color(0.35f, 0.75f, 1f);
        tmp.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
    }

    private void CreateDisabledLabelButton(Transform parent, string text, float height)
    {
        GameObject go = new GameObject(text, typeof(RectTransform), typeof(Image), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        go.GetComponent<LayoutElement>().preferredHeight = height;
        go.GetComponent<Image>().color = new Color(0.18f, 0.48f, 0.78f, 1f);
        CreateButtonLabel(go.transform, text);
    }

    private void CreateActionButton(Transform parent, string label, UnityEngine.Events.UnityAction action, float height)
    {
        GameObject buttonGO = new GameObject(label + "Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        buttonGO.transform.SetParent(parent, false);
        buttonGO.GetComponent<Image>().color = new Color(0.20f, 0.50f, 0.82f);
        LayoutElement layout = buttonGO.GetComponent<LayoutElement>();
        layout.minHeight = height;
        layout.preferredHeight = height;
        Button button = buttonGO.GetComponent<Button>();
        button.onClick.AddListener(action);
        CreateButtonLabel(buttonGO.transform, label);
    }

    private void CreateButtonLabel(Transform parent, string text)
    {
        GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI));
        textGO.transform.SetParent(parent, false);
        TMPro.TextMeshProUGUI tmp = textGO.GetComponent<TMPro.TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = 14f;
        tmp.fontStyle = TMPro.FontStyles.Bold;
        tmp.alignment = TMPro.TextAlignmentOptions.Center;
        tmp.color = Color.white;
        tmp.raycastTarget = false;
        RectTransform rect = textGO.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.sizeDelta = Vector2.zero;
    }

    private GameObject CreateSliderUI(Transform parent, string labelText, float min, float max, float defaultValue, UnityEngine.Events.UnityAction<float> onValueChanged, out Slider createdSlider)
    {
        GameObject rowGO = new GameObject(labelText + "_Row", typeof(RectTransform), typeof(LayoutElement));
        rowGO.transform.SetParent(parent, false);
        rowGO.GetComponent<LayoutElement>().preferredHeight = 48f;

        VerticalLayoutGroup rowLayout = rowGO.AddComponent<VerticalLayoutGroup>();
        rowLayout.spacing = 2;
        rowLayout.padding = new RectOffset(0, 0, 1, 1);
        rowLayout.childControlWidth = true;
        rowLayout.childControlHeight = false;

        GameObject textGO = new GameObject(labelText + "_Text", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI));
        textGO.transform.SetParent(rowGO.transform, false);
        textGO.GetComponent<RectTransform>().sizeDelta = new Vector2(0, 20);
        TMPro.TextMeshProUGUI tmp = textGO.GetComponent<TMPro.TextMeshProUGUI>();
        tmp.text = labelText + ": " + defaultValue.ToString("F3");
        tmp.fontSize = 14f;
        tmp.color = Color.white;

        GameObject sliderGO = new GameObject(labelText + "_Slider", typeof(RectTransform), typeof(Slider));
        sliderGO.transform.SetParent(rowGO.transform, false);
        sliderGO.GetComponent<RectTransform>().sizeDelta = new Vector2(0, 20);

        Slider slider = sliderGO.GetComponent<Slider>();
        slider.minValue = min;
        slider.maxValue = max;
        slider.value = defaultValue;

        GameObject backgroundGO = new GameObject("Background", typeof(RectTransform), typeof(Image));
        backgroundGO.transform.SetParent(sliderGO.transform, false);
        backgroundGO.GetComponent<Image>().color = new Color(0.28f, 0.28f, 0.28f);
        RectTransform bgRect = backgroundGO.GetComponent<RectTransform>();
        bgRect.anchorMin = new Vector2(0, 0.35f);
        bgRect.anchorMax = new Vector2(1, 0.65f);
        bgRect.sizeDelta = Vector2.zero;

        GameObject fillAreaGO = new GameObject("Fill Area", typeof(RectTransform));
        fillAreaGO.transform.SetParent(sliderGO.transform, false);
        RectTransform fillAreaRect = fillAreaGO.GetComponent<RectTransform>();
        fillAreaRect.anchorMin = new Vector2(0, 0.35f);
        fillAreaRect.anchorMax = new Vector2(1, 0.65f);
        fillAreaRect.sizeDelta = Vector2.zero;

        GameObject fillGO = new GameObject("Fill", typeof(RectTransform), typeof(Image));
        fillGO.transform.SetParent(fillAreaGO.transform, false);
        fillGO.GetComponent<Image>().color = new Color(0.20f, 0.60f, 1f);
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
        slider.handleRect.sizeDelta = new Vector2(18, 18);

        slider.onValueChanged.AddListener(val =>
        {
            tmp.text = labelText + ": " + val.ToString("F3");
            onValueChanged.Invoke(val);
        });

        createdSlider = slider;
        return rowGO;
    }
}