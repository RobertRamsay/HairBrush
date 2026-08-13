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
        public Vector2Int rootPixel;
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
    private Texture2D generatedHairTexture;

    private readonly List<HairTextureCluster> clusters = new List<HairTextureCluster>();
    private int activeClusterIndex = -1;
    private int nextClusterId = 0;
    private bool placementMode = false;
    private bool panelActive = false;
    private bool textureCreated = false;

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
    public float strandLength = 1f;
    public float strandWidth = 2f;
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

            ModelViewer viewer = GetComponent<ModelViewer>();
            if (viewer != null)
                viewer.hairCardMaterial = generatedHairMaterial;
        }
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

        ModelViewer viewer = GetComponent<ModelViewer>();
        Camera cam = viewer != null ? viewer.mainCamera : Camera.main;
        if (cam == null) return;

        Ray ray = cam.ScreenPointToRay(Mouse.current.position.ReadValue());
        Collider previewCollider = texturePreviewPlane.GetComponent<Collider>();
        if (previewCollider == null) return;

        if (previewCollider.Raycast(ray, out RaycastHit hit, 10000f))
            PlaceActiveClusterAtUV(hit.textureCoord);
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
            EnsurePreviewPlane();
            ApplyGeneratedTextureToHairMaterial();
            RebuildClusterList();
            RefreshPlacementStatus();
        }
        else
        {
            placementMode = false;
            if (texturePreviewPlane != null) texturePreviewPlane.SetActive(false);
        }
    }

    private void EnsurePreviewPlane()
    {
        if (texturePreviewPlane == null)
        {
            texturePreviewPlane = GameObject.CreatePrimitive(PrimitiveType.Quad);
            texturePreviewPlane.name = "HairTexturePreviewPlane";
            texturePreviewPlane.transform.position = new Vector3(0f, 0f, 1.5f);
            texturePreviewPlane.transform.localScale = new Vector3(0.9f, 0.9f, 1f);

            MeshFilter mf = texturePreviewPlane.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                Mesh mesh = mf.mesh;
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

        MeshRenderer mr = texturePreviewPlane.GetComponent<MeshRenderer>();
        if (mr != null && generatedHairMaterial != null)
            mr.sharedMaterial = generatedHairMaterial;
    }

    private void NewTexture()
    {
        textureSize = 4096;

        if (generatedHairTexture != null)
            Destroy(generatedHairTexture);

        generatedHairTexture = new Texture2D(textureSize, textureSize, TextureFormat.RGBA32, true, false);
        generatedHairTexture.name = "GeneratedHairAtlas_4096_Runtime";
        generatedHairTexture.wrapMode = TextureWrapMode.Clamp;
        generatedHairTexture.filterMode = FilterMode.Bilinear;

        Color32[] pixels = new Color32[textureSize * textureSize];
        Color32 black = new Color32(0, 0, 0, 255);
        for (int i = 0; i < pixels.Length; i++) pixels[i] = black;
        generatedHairTexture.SetPixels32(pixels);
        generatedHairTexture.Apply(true, false);

        clusters.Clear();
        activeClusterIndex = -1;
        nextClusterId = 0;
        currentTextureGroupId = -1;
        placementMode = false;
        textureCreated = true;

        ApplyGeneratedTextureToHairMaterial();
        RebuildClusterList();
        RefreshPlacementStatus();
        Debug.Log("Created new 4096 x 4096 procedural hair texture.");
    }

    private void NewCluster()
    {
        if (!textureCreated || generatedHairTexture == null)
        {
            Debug.LogWarning("Create a New Texture first.");
            RefreshPlacementStatus();
            return;
        }

        HairTextureCluster c = new HairTextureCluster
        {
            id = nextClusterId,
            name = "Cluster " + nextClusterId,
            seed = 12345 + nextClusterId * 7919
        };
        nextClusterId++;
        clusters.Add(c);

        SelectCluster(clusters.Count - 1);
        placementMode = true;
        RebuildClusterList();
        RefreshPlacementStatus();
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

        CommitControlsToActiveCluster();
        placementMode = true;
        RefreshPlacementStatus();
    }

    private void PlaceActiveClusterAtUV(Vector2 uv)
    {
        if (!textureCreated || generatedHairTexture == null) return;
        if (activeClusterIndex < 0 || activeClusterIndex >= clusters.Count) return;

        CommitControlsToActiveCluster();
        HairTextureCluster c = clusters[activeClusterIndex];
        bool shouldRegenerate = c.generated && c.placed;
        RectInt oldRect = c.pixelRect;

        int rootX = Mathf.RoundToInt(Mathf.Clamp01(uv.x) * (textureSize - 1));
        int rootY = Mathf.RoundToInt(Mathf.Clamp01(uv.y) * (textureSize - 1));
        int width = Mathf.Clamp(c.rectWidth, 256, textureSize);
        int height = Mathf.Clamp(c.rectHeight, 256, textureSize);
        int pad = Mathf.Clamp(c.padding, 8, Mathf.Min(width, height) / 3);

        // Placement click is the semantic root centre. The cluster rectangle hangs below it.
        int xMin = Mathf.Clamp(rootX - width / 2, 0, textureSize - width);
        int desiredYMin = rootY - (height - pad);
        int yMin = Mathf.Clamp(desiredYMin, 0, textureSize - height);

        // Keep the root on the clicked point whenever there is room. If the click is too close
        // to a canvas edge, clamp only as much as required to keep the cluster drawable.
        int clampedRootX = Mathf.Clamp(rootX, xMin + pad, xMin + width - pad - 1);
        int clampedRootY = Mathf.Clamp(rootY, yMin + pad, yMin + height - pad - 1);

        Color32[] pixels = generatedHairTexture.GetPixels32();
        if (shouldRegenerate)
            ClearRect(pixels, textureSize, oldRect);

        c.pixelRect = new RectInt(xMin, yMin, width, height);
        c.rootPixel = new Vector2Int(clampedRootX, clampedRootY);
        c.placed = true;
        placementMode = false;

        if (shouldRegenerate)
        {
            ClearRect(pixels, textureSize, c.pixelRect);
            DrawCluster(pixels, textureSize, c);
            c.generated = true;
            generatedHairTexture.SetPixels32(pixels);
            generatedHairTexture.Apply(true, false);
            ApplyGeneratedTextureToHairMaterial();
        }
        else
        {
            c.generated = false;
        }

        RebuildClusterList();
        RefreshPlacementStatus();
        Debug.Log(c.name + " root placed at " + c.rootPixel + " in atlas rect " + c.pixelRect + (shouldRegenerate ? " and regenerated." : "."));
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
        if (!textureCreated || generatedHairTexture == null)
        {
            Debug.LogWarning("Create a New Texture first.");
            return;
        }
        if (activeClusterIndex < 0 || activeClusterIndex >= clusters.Count)
        {
            Debug.LogWarning("Create and select a cluster first.");
            return;
        }

        HairTextureCluster c = clusters[activeClusterIndex];
        if (!c.placed)
        {
            placementMode = true;
            RefreshPlacementStatus();
            Debug.LogWarning(c.name + " has not been placed. Click the atlas to place its root.");
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

    private void DrawCluster(Color32[] pixels, int size, HairTextureCluster c)
    {
        RectInt r = c.pixelRect;
        float pad = Mathf.Clamp(c.padding, 8, Mathf.Min(r.width, r.height) / 3);
        float minX = r.xMin + pad;
        float maxX = r.xMax - pad - 1f;
        float rootY = c.rootPixel.y;
        float availableDown = Mathf.Max(1f, rootY - (r.yMin + pad));
        float lengthPixels = Mathf.Clamp01(c.strandLength / 2f) * availableDown;
        float centreX = c.rootPixel.x;
        float guideWavePixels = c.waveAmount * r.width * 0.12f;

        System.Random random = new System.Random(c.seed);
        float guidePhase = NextRange(random, 0f, Mathf.PI * 2f);
        int count = Mathf.Clamp(c.strandCount, 1, 100);

        for (int strandIndex = 0; strandIndex < count; strandIndex++)
        {
            float rootX = Mathf.Lerp(minX, maxX, count <= 1 ? 0.5f : strandIndex / (float)(count - 1));
            rootX += NextRange(random, -8f, 8f);

            float finalLength = Mathf.Min(lengthPixels * NextRange(random, 0.90f, 1.03f), availableDown);
            float phase = NextRange(random, 0f, Mathf.PI * 2f);
            float waveScale = NextRange(random, 0.75f, 1.25f);
            float noiseOffset = NextRange(random, 0f, 1000f);
            float widthVariation = NextRange(random, 0.82f, 1.18f);
            float rootYJitter = NextRange(random, -1.5f, 1.5f) * Mathf.Max(1f, c.strandWidth * 0.35f);
            int samples = Mathf.Clamp(Mathf.CeilToInt(finalLength / 1.5f), 32, 1200);

            for (int sample = 0; sample < samples; sample++)
            {
                float t = samples <= 1 ? 0f : sample / (float)(samples - 1);

                // Texture V increases upward, so subtracting length makes strands hang down.
                float y = rootY + rootYJitter - finalLength * t;
                float guideX = centreX + Mathf.Sin(t * Mathf.PI * 2f + guidePhase) * guideWavePixels;
                float waveX = Mathf.Sin(t * Mathf.PI * 2f + phase) * guideWavePixels * waveScale;
                float independentX = rootX + waveX;
                float clumpT = Mathf.Clamp01(c.clumpStrength) * Mathf.SmoothStep(0f, 1f, t);
                float x = Mathf.Lerp(independentX, guideX, clumpT);

                float noiseFrequency = Mathf.Lerp(0.5f, 8f, Mathf.Clamp01(c.noiseScale));
                float noise = Mathf.PerlinNoise(noiseOffset, t * noiseFrequency) * 2f - 1f;
                x += noise * c.noiseScale * r.width * 0.04f;

                float taper = Mathf.Lerp(1f, Mathf.Max(0.08f, 1f - c.taperAmount), t);

                // Avoid the heavy horizontal root band when width is increased: roots begin
                // narrower, then reach full strand width over the first ~5% of the strand.
                float rootRamp = Mathf.Lerp(0.58f, 1f, Mathf.SmoothStep(0f, 0.05f, t));
                float radius = Mathf.Max(0.5f, c.strandWidth * widthVariation * taper * rootRamp);
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

    private void ApplyGeneratedTextureToHairMaterial()
    {
        if (generatedHairMaterial == null) return;

        if (generatedHairTexture != null)
        {
            if (generatedHairMaterial.HasProperty("_BaseMap")) generatedHairMaterial.SetTexture("_BaseMap", generatedHairTexture);
            if (generatedHairMaterial.HasProperty("_MainTex")) generatedHairMaterial.SetTexture("_MainTex", generatedHairTexture);
        }
        if (generatedHairMaterial.HasProperty("_BaseColor")) generatedHairMaterial.SetColor("_BaseColor", Color.white);
        if (generatedHairMaterial.HasProperty("_Color")) generatedHairMaterial.SetColor("_Color", Color.white);

        if (texturePreviewPlane != null)
        {
            MeshRenderer mr = texturePreviewPlane.GetComponent<MeshRenderer>();
            if (mr != null) mr.sharedMaterial = generatedHairMaterial;
        }
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

        CreateActionButton(leftClusterPanelGO.transform, "+ NEW TEXTURE", NewTexture, 48f);
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

        CreateActionButton(leftClusterPanelGO.transform, "+ NEW CLUSTER", NewCluster, 44f);
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
        statusGO.GetComponent<LayoutElement>().preferredHeight = 48f;
        placementStatusText = statusGO.GetComponent<TMPro.TextMeshProUGUI>();
        placementStatusText.fontSize = 15f;
        placementStatusText.color = Color.white;
        placementStatusText.alignment = TMPro.TextAlignmentOptions.MidlineLeft;

        CreateActionButton(rightControlPanelGO.transform, "REPOSITION - CLICK ATLAS", RepositionActiveCluster, 40f);

        CreateSliderUI(rightControlPanelGO.transform, "Strand Count", 1f, 100f, strandCount, v => strandCount = v, out strandCountSlider);
        CreateSliderUI(rightControlPanelGO.transform, "Strand Width", 0.5f, 8f, strandWidth, v => strandWidth = v, out strandWidthSlider);
        CreateSliderUI(rightControlPanelGO.transform, "Strand Length", 0.1f, 2f, strandLength, v => strandLength = v, out strandLengthSlider);
        CreateSliderUI(rightControlPanelGO.transform, "Wave Amount", 0f, 1f, waveAmount, v => waveAmount = v, out waveSlider);
        CreateSliderUI(rightControlPanelGO.transform, "Clump Strength", 0f, 1f, clumpStrength, v => clumpStrength = v, out clumpSlider);
        CreateSliderUI(rightControlPanelGO.transform, "Taper Amount", 0f, 1f, taperAmount, v => taperAmount = v, out taperSlider);
        CreateSliderUI(rightControlPanelGO.transform, "Noise Scale", 0f, 1f, noiseScale, v => noiseScale = v, out noiseSlider);

        CreateActionButton(rightControlPanelGO.transform, "GENERATE / UPDATE", GenerateOrUpdateActiveCluster, 48f);
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
            buttonGO.GetComponent<LayoutElement>().preferredHeight = 58f;
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
            string state = c.generated ? "generated" : (c.placed ? "placed" : "place on atlas");
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

        if (!textureCreated || generatedHairTexture == null)
        {
            placementStatusText.text = "1. NEW TEXTURE\n2. NEW CLUSTER";
            placementStatusText.color = new Color(1f, 0.75f, 0.25f);
            return;
        }

        if (activeClusterIndex < 0 || activeClusterIndex >= clusters.Count)
        {
            placementStatusText.text = "Texture ready - create a NEW CLUSTER";
            placementStatusText.color = new Color(0.75f, 0.75f, 0.75f);
            return;
        }

        HairTextureCluster c = clusters[activeClusterIndex];
        if (placementMode)
        {
            placementStatusText.text = c.name + ": CLICK THE TEXTURE TO PLACE ROOT";
            placementStatusText.color = new Color(0.25f, 0.75f, 1f);
        }
        else if (!c.placed)
        {
            placementStatusText.text = c.name + ": not placed";
            placementStatusText.color = new Color(1f, 0.72f, 0.25f);
        }
        else
        {
            placementStatusText.text = c.name + "  Root: " + c.rootPixel.x + ", " + c.rootPixel.y + "  Rect: " + c.pixelRect.width + "x" + c.pixelRect.height;
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
