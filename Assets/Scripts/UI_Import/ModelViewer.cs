using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;
using System.Collections.Generic;
using System.Linq;
using System.Collections;
using System;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class ModelViewer : MonoBehaviour
{
    [Header("UI Elements")]
    public GameObject uiContainer;
    public Button loadButton;
    public Button loadProjectButton;

    [Header("Texture Editor Reference")]
    private TextureEditorManager textureEditorManager;
    private bool isTextureEditorMode = false;

    [Header("Camera Rig")]
    public Transform cameraPivot;
    public Camera mainCamera;

    [Header("Controls Settings")]
    public float rotateSpeed = 5f;
    public float zoomSpeed = 500f;
    public float panSpeed = 0.5f;

    [Header("Grooming Settings")]
    public Material hairCardMaterial;

    private GameObject loadedModel;
    private string currentModelPath = "";
    private float pitch = 0f;
    private bool isGroomingMode = false;

    [Header("Grooming Defaults & Active State")]
    public HairCard lastPlacedCard;

    public float currentLength = 0.2f;
    public float currentWidth = 0.01f;
    public int currentSegments = 12;
    public float currentBend = 0f;
    public float currentTwist = 0f;
    public float currentEmbedDepth = 0.002f;
    public float currentOffsetX = 0f;
    public float currentOffsetY = 0f;
    public float currentOffsetZ = 0f;
    public float currentUScale = 1.0f;
    public float currentVScale = 1.0f;
    public float currentUOffset = 0.0f;
    public float currentVOffset = 0.0f;
    public float currentCurlFrequency = 0f;
    public float currentCurlDiameter = 0f;
    public float currentWaveAmplitude = 0f;
    public float currentWaveFrequency = 0f;
    public float currentWaveDirection = 1f;
    public float currentArch = HairCard.ArchNeutral;

    // Group Management
    public int currentGroupId = 0;
    private HashSet<int> allGroupIds = new HashSet<int>() { 0 };
    private Dictionary<int, string> groupNames = new Dictionary<int, string>() { { 0, "Group 0 (Default)" } };
    private Dictionary<int, bool> groupSoloState = new Dictionary<int, bool>();
    private Dictionary<int, float> groupUScales = new Dictionary<int, float>() { { 0, 1.0f } };
    private Dictionary<int, float> groupVScales = new Dictionary<int, float>() { { 0, 1.0f } };
    private Dictionary<int, float> groupUOffsets = new Dictionary<int, float>() { { 0, 0.0f } };
    private Dictionary<int, float> groupVOffsets = new Dictionary<int, float>() { { 0, 0.0f } };

    private List<HairCard> sessionPlacedCards = new List<HairCard>();
    private Transform groupListContentTransform;
    private bool wasHoldingShiftDrag = false;
    private Coroutine flashGroupCoroutine;
    private float lastGroupClickTime = 0f;
    private int lastClickedGroupId = -1;
#if UNITY_EDITOR
    private double nextAllowedSaveDialogTime = 0.0;
#endif

    [Header("Brush Settings")]
    public float brushRadius = 0.2f;
    public float brushFalloffDistance = 0.05f;
    public float selectionStrength = 0.25f;
    private bool isSelectionMode = false;
    // Latched on the right button press edge; see HandleCameraControls.
    private bool orbitSuppressed = false;

    private bool hasSelectionHotspot = false;
    private bool isRelativeMode = false;
    private Vector3 selectionHitPoint;
    private Vector3 selectionHitNormal;

    private GameObject falloffRowGO;
    private GameObject strengthRowGO;

    private Slider lengthSlider;
    private Slider widthSlider;
    private Slider curlFrequencySlider;
    private Slider curlDiameterSlider;
    private Slider waveAmplitudeSlider;
    private Slider waveFrequencySlider;
    private Slider waveDirectionSlider;
    private Slider archSlider;
    private Slider segmentsSlider;
    private Slider bendSlider;
    private Slider twistSlider;
    private Slider depthSlider;
    private Slider offsetXSlider;
    private Slider offsetYSlider;
    private Slider offsetZSlider;
    private Slider uScaleSlider;
    private Slider vScaleSlider;
    private Slider uOffsetSlider;
    private Slider vOffsetSlider;
    private GroomRootStateAuthority rootStateAuthority;

    private float spawnCooldown = 0.05f;
    private float lastSpawnTime = 0f;

    [Header("UI References")]
    public GameObject importPanelGO;
    public GameObject groomingSliderPanelGO;

    private GameObject activeSliderPanel;
    private Image activePanelImage;

    void Start()
    {
        if (loadButton != null)
            loadButton.onClick.AddListener(LoadModel);

        if (loadProjectButton != null)
            loadProjectButton.onClick.AddListener(LoadProject);

        pitch = cameraPivot.eulerAngles.x;

        textureEditorManager = GetComponent<TextureEditorManager>();
        if (textureEditorManager == null)
        {
            textureEditorManager = gameObject.AddComponent<TextureEditorManager>();
        }
        textureEditorManager.Init(hairCardMaterial);
    }

    void LoadModel()
    {
        string path = "";
#if UNITY_EDITOR
        path = EditorUtility.OpenFilePanel("Select OBJ Model", "", "obj");
#else
        Debug.LogError("Native file browser requires a 3rd party plugin in a built game!");
#endif

        if (string.IsNullOrEmpty(path)) return;
        currentModelPath = path;
        if (loadedModel != null) Destroy(loadedModel);

        loadedModel = CustomOBJImporter.Load(path);

        if (loadedModel != null)
        {
            loadedModel.transform.position = Vector3.zero;
            loadedModel.transform.eulerAngles = new Vector3(0f, 180f, 0f);

            MeshRenderer[] renderers = loadedModel.GetComponentsInChildren<MeshRenderer>();
            if (renderers.Length > 0)
            {
                Bounds combinedBounds = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++) combinedBounds.Encapsulate(renderers[i].bounds);
                if (cameraPivot != null) cameraPivot.position = combinedBounds.center;
            }

            if (uiContainer != null) uiContainer.SetActive(false);
            OnModelLoaded();
            BuildRuntimeGroomingUI();
            BuildGroupManagementUI();
            isGroomingMode = true;
        }
    }

    public void OnModelLoaded()
    {
        if (importPanelGO != null) importPanelGO.SetActive(false);
        if (groomingSliderPanelGO != null) groomingSliderPanelGO.SetActive(true);
    }

    public void OnActualSliderLengthChanged(float val)
    {
        float delta = val - currentLength;
        currentLength = val;
        if (HasLiveSelection()) UpdateActiveCard();
        else ApplyGroupUpdate(c => c.SetParameters(Mathf.Max(0.0001f, isRelativeMode ? c.length + delta : val), c.width, c.segments, c.bendAngle, c.twistAngle, c.GetOffsetX(), c.GetOffsetY(), c.GetOffsetZ(), c.GetEmbedDepth(), 1f, c.uScale, c.vScale, c.uOffset, c.vOffset, c.curlFrequency, c.curlDiameter, c.waveAmplitude, c.waveFrequency, c.waveDirection, c.arch));
    }

    public void OnSliderWidthChanged(float val)
    {
        float delta = val - currentWidth;
        currentWidth = val;
        if (HasLiveSelection()) UpdateActiveCard();
        else ApplyGroupUpdate(c => c.SetParameters(c.length, Mathf.Max(0.0005f, isRelativeMode ? c.width + delta : val), c.segments, c.bendAngle, c.twistAngle, c.GetOffsetX(), c.GetOffsetY(), c.GetOffsetZ(), c.GetEmbedDepth(), 1f, c.uScale, c.vScale, c.uOffset, c.vOffset, c.curlFrequency, c.curlDiameter, c.waveAmplitude, c.waveFrequency, c.waveDirection, c.arch));
    }

    public void OnSliderCurlFrequencyChanged(float val)
    {
        float delta = val - currentCurlFrequency;
        currentCurlFrequency = val;
        if (HasLiveSelection()) UpdateActiveCard();
        else ApplyGroupUpdate(c => c.SetParameters(c.length, c.width, c.segments, c.bendAngle, c.twistAngle, c.GetOffsetX(), c.GetOffsetY(), c.GetOffsetZ(), c.GetEmbedDepth(), 1f, c.uScale, c.vScale, c.uOffset, c.vOffset, isRelativeMode ? c.curlFrequency + delta : val, c.curlDiameter, c.waveAmplitude, c.waveFrequency, c.waveDirection, c.arch));
    }

    public void OnSliderWaveAmplitudeChanged(float val)
    {
        float delta = val - currentWaveAmplitude;
        currentWaveAmplitude = val;
        if (HasLiveSelection()) UpdateActiveCard();
        else ApplyGroupUpdate(c => c.SetParameters(c.length, c.width, c.segments, c.bendAngle, c.twistAngle, c.GetOffsetX(), c.GetOffsetY(), c.GetOffsetZ(), c.GetEmbedDepth(), 1f, c.uScale, c.vScale, c.uOffset, c.vOffset, c.curlFrequency, c.curlDiameter, Mathf.Max(0f, isRelativeMode ? c.waveAmplitude + delta : val), c.waveFrequency, c.waveDirection, c.arch));
    }

    public void OnSliderArchChanged(float val)
    {
        float delta = val - currentArch;
        currentArch = val;
        if (HasLiveSelection()) UpdateActiveCard();
        else ApplyGroupUpdate(c => c.SetParameters(c.length, c.width, c.segments, c.bendAngle, c.twistAngle, c.GetOffsetX(), c.GetOffsetY(), c.GetOffsetZ(), c.GetEmbedDepth(), 1f, c.uScale, c.vScale, c.uOffset, c.vOffset, c.curlFrequency, c.curlDiameter, c.waveAmplitude, c.waveFrequency, c.waveDirection, Mathf.Max(0f, isRelativeMode ? c.arch + delta : val)));
    }

    public void OnSliderWaveDirectionChanged(float val)
    {
        float delta = val - currentWaveDirection;
        currentWaveDirection = val;
        if (HasLiveSelection()) UpdateActiveCard();
        else ApplyGroupUpdate(c => c.SetParameters(c.length, c.width, c.segments, c.bendAngle, c.twistAngle, c.GetOffsetX(), c.GetOffsetY(), c.GetOffsetZ(), c.GetEmbedDepth(), 1f, c.uScale, c.vScale, c.uOffset, c.vOffset, c.curlFrequency, c.curlDiameter, c.waveAmplitude, c.waveFrequency, Mathf.Clamp01(isRelativeMode ? c.waveDirection + delta : val), c.arch));
    }

    public void OnSliderWaveFrequencyChanged(float val)
    {
        float delta = val - currentWaveFrequency;
        currentWaveFrequency = val;
        if (HasLiveSelection()) UpdateActiveCard();
        else ApplyGroupUpdate(c => c.SetParameters(c.length, c.width, c.segments, c.bendAngle, c.twistAngle, c.GetOffsetX(), c.GetOffsetY(), c.GetOffsetZ(), c.GetEmbedDepth(), 1f, c.uScale, c.vScale, c.uOffset, c.vOffset, c.curlFrequency, c.curlDiameter, c.waveAmplitude, isRelativeMode ? c.waveFrequency + delta : val, c.waveDirection, c.arch));
    }

    public void OnSliderCurlDiameterChanged(float val)
    {
        float delta = val - currentCurlDiameter;
        currentCurlDiameter = val;
        if (HasLiveSelection()) UpdateActiveCard();
        else ApplyGroupUpdate(c => c.SetParameters(c.length, c.width, c.segments, c.bendAngle, c.twistAngle, c.GetOffsetX(), c.GetOffsetY(), c.GetOffsetZ(), c.GetEmbedDepth(), 1f, c.uScale, c.vScale, c.uOffset, c.vOffset, c.curlFrequency, Mathf.Max(0f, isRelativeMode ? c.curlDiameter + delta : val), c.waveAmplitude, c.waveFrequency, c.waveDirection, c.arch));
    }

    public void OnSliderSegmentsChanged(float val)
    {
        int targetSegs = Mathf.RoundToInt(val);
        int deltaSegs = targetSegs - currentSegments;
        currentSegments = targetSegs;
        if (HasLiveSelection()) UpdateActiveCard();
        else ApplyGroupUpdate(c => c.SetParameters(c.length, c.width, Mathf.Clamp(isRelativeMode ? c.segments + deltaSegs : targetSegs, 4, 60), c.bendAngle, c.twistAngle, c.GetOffsetX(), c.GetOffsetY(), c.GetOffsetZ(), c.GetEmbedDepth(), 1f, c.uScale, c.vScale, c.uOffset, c.vOffset, c.curlFrequency, c.curlDiameter, c.waveAmplitude, c.waveFrequency, c.waveDirection, c.arch));
    }

    public void OnSliderBendChanged(float val)
    {
        float delta = val - currentBend;
        currentBend = val;
        if (HasLiveSelection()) UpdateActiveCard();
        else ApplyGroupUpdate(c => c.SetParameters(c.length, c.width, c.segments, isRelativeMode ? c.bendAngle + delta : val, c.twistAngle, c.GetOffsetX(), c.GetOffsetY(), c.GetOffsetZ(), c.GetEmbedDepth(), 1f, c.uScale, c.vScale, c.uOffset, c.vOffset, c.curlFrequency, c.curlDiameter, c.waveAmplitude, c.waveFrequency, c.waveDirection, c.arch));
    }

    public void OnSliderTwistChanged(float val)
    {
        float delta = val - currentTwist;
        currentTwist = val;
        if (HasLiveSelection()) UpdateActiveCard();
        else ApplyGroupUpdate(c => c.SetParameters(c.length, c.width, c.segments, c.bendAngle, isRelativeMode ? c.twistAngle + delta : val, c.GetOffsetX(), c.GetOffsetY(), c.GetOffsetZ(), c.GetEmbedDepth(), 1f, c.uScale, c.vScale, c.uOffset, c.vOffset, c.curlFrequency, c.curlDiameter, c.waveAmplitude, c.waveFrequency, c.waveDirection, c.arch));
    }

    public void OnSliderEmbedDepthChanged(float val)
    {
        float delta = val - currentEmbedDepth;
        currentEmbedDepth = val;
        if (HasLiveSelection()) UpdateActiveCard();
        else ApplyGroupUpdate(c => c.SetParameters(c.length, c.width, c.segments, c.bendAngle, c.twistAngle, c.GetOffsetX(), c.GetOffsetY(), c.GetOffsetZ(), Mathf.Max(0f, isRelativeMode ? c.GetEmbedDepth() + delta : val), 1f, c.uScale, c.vScale, c.uOffset, c.vOffset, c.curlFrequency, c.curlDiameter, c.waveAmplitude, c.waveFrequency, c.waveDirection, c.arch));
    }

    public void OnSliderOffsetXChanged(float val)
    {
        float delta = val - currentOffsetX;
        currentOffsetX = val;
        if (HasLiveSelection()) UpdateActiveCard();
        else ApplyGroupUpdate(c => c.SetParameters(c.length, c.width, c.segments, c.bendAngle, c.twistAngle, isRelativeMode ? c.GetOffsetX() + delta : val, c.GetOffsetY(), c.GetOffsetZ(), c.GetEmbedDepth(), 1f, c.uScale, c.vScale, c.uOffset, c.vOffset, c.curlFrequency, c.curlDiameter, c.waveAmplitude, c.waveFrequency, c.waveDirection, c.arch));
    }

    public void OnSliderOffsetYChanged(float val)
    {
        float delta = val - currentOffsetY;
        currentOffsetY = val;
        if (HasLiveSelection()) UpdateActiveCard();
        else ApplyGroupUpdate(c => c.SetParameters(c.length, c.width, c.segments, c.bendAngle, c.twistAngle, c.GetOffsetX(), isRelativeMode ? c.GetOffsetY() + delta : val, c.GetOffsetZ(), c.GetEmbedDepth(), 1f, c.uScale, c.vScale, c.uOffset, c.vOffset, c.curlFrequency, c.curlDiameter, c.waveAmplitude, c.waveFrequency, c.waveDirection, c.arch));
    }

    public void OnSliderOffsetZChanged(float val)
    {
        float delta = val - currentOffsetZ;
        currentOffsetZ = val;
        if (HasLiveSelection()) UpdateActiveCard();
        else ApplyGroupUpdate(c => c.SetParameters(c.length, c.width, c.segments, c.bendAngle, c.twistAngle, c.GetOffsetX(), c.GetOffsetY(), isRelativeMode ? c.GetOffsetZ() + delta : val, c.GetEmbedDepth(), 1f, c.uScale, c.vScale, c.uOffset, c.vOffset, c.curlFrequency, c.curlDiameter, c.waveAmplitude, c.waveFrequency, c.waveDirection, c.arch));
    }

    public void OnSliderUScaleChanged(float val)
    {
        currentUScale = val;
        groupUScales[currentGroupId] = val;
        ApplyGroupUpdate(c => c.SetParameters(c.length, c.width, c.segments, c.bendAngle, c.twistAngle, c.GetOffsetX(), c.GetOffsetY(), c.GetOffsetZ(), c.GetEmbedDepth(), 1f, val, c.vScale, c.uOffset, c.vOffset, c.curlFrequency, c.curlDiameter, c.waveAmplitude, c.waveFrequency, c.waveDirection, c.arch));
    }

    public void OnSliderVScaleChanged(float val)
    {
        currentVScale = val;
        groupVScales[currentGroupId] = val;
        ApplyGroupUpdate(c => c.SetParameters(c.length, c.width, c.segments, c.bendAngle, c.twistAngle, c.GetOffsetX(), c.GetOffsetY(), c.GetOffsetZ(), c.GetEmbedDepth(), 1f, c.uScale, val, c.uOffset, c.vOffset, c.curlFrequency, c.curlDiameter, c.waveAmplitude, c.waveFrequency, c.waveDirection, c.arch));
    }

    public void OnSliderUOffsetChanged(float val)
    {
        currentUOffset = val;
        groupUOffsets[currentGroupId] = val;
        ApplyGroupUpdate(c => c.SetParameters(c.length, c.width, c.segments, c.bendAngle, c.twistAngle, c.GetOffsetX(), c.GetOffsetY(), c.GetOffsetZ(), c.GetEmbedDepth(), 1f, c.uScale, c.vScale, val, c.vOffset, c.curlFrequency, c.curlDiameter, c.waveAmplitude, c.waveFrequency, c.waveDirection, c.arch));
    }

    public void OnSliderVOffsetChanged(float val)
    {
        currentVOffset = val;
        groupVOffsets[currentGroupId] = val;
        ApplyGroupUpdate(c => c.SetParameters(c.length, c.width, c.segments, c.bendAngle, c.twistAngle, c.GetOffsetX(), c.GetOffsetY(), c.GetOffsetZ(), c.GetEmbedDepth(), 1f, c.uScale, c.vScale, c.uOffset, val, c.curlFrequency, c.curlDiameter, c.waveAmplitude, c.waveFrequency, c.waveDirection, c.arch));
    }

    void ResetAllSliders()
    {
        currentLength = 0.2f;
        currentWidth = 0.01f;
        currentCurlFrequency = 0f;
        currentCurlDiameter = 0f;
        currentWaveAmplitude = 0f;
        currentWaveFrequency = 0f;
        currentWaveDirection = 1f;
        currentArch = HairCard.ArchNeutral;
        currentSegments = 12;
        currentBend = 0f;
        currentTwist = 0f;
        currentEmbedDepth = 0.002f;
        currentOffsetX = 0f;
        currentOffsetY = 0f;
        currentOffsetZ = 0f;
        currentUScale = 1.0f;
        currentVScale = 1.0f;
        currentUOffset = 0.0f;
        currentVOffset = 0.0f;
        groupUScales[currentGroupId] = currentUScale;
        groupVScales[currentGroupId] = currentVScale;
        groupUOffsets[currentGroupId] = currentUOffset;
        groupVOffsets[currentGroupId] = currentVOffset;
        if (lengthSlider != null) lengthSlider.value = currentLength;
        if (widthSlider != null) widthSlider.value = currentWidth;
        if (curlFrequencySlider != null) curlFrequencySlider.value = currentCurlFrequency;
        if (curlDiameterSlider != null) curlDiameterSlider.value = currentCurlDiameter;
        if (waveAmplitudeSlider != null) waveAmplitudeSlider.value = currentWaveAmplitude;
        if (waveFrequencySlider != null) waveFrequencySlider.value = currentWaveFrequency;
        if (waveDirectionSlider != null) waveDirectionSlider.value = currentWaveDirection;
        if (archSlider != null) archSlider.value = currentArch;
        if (segmentsSlider != null) segmentsSlider.value = currentSegments;
        if (bendSlider != null) bendSlider.value = currentBend;
        if (twistSlider != null) twistSlider.value = currentTwist;
        if (depthSlider != null) depthSlider.value = currentEmbedDepth;
        if (offsetXSlider != null) offsetXSlider.value = currentOffsetX;
        if (offsetYSlider != null) offsetYSlider.value = currentOffsetY;
        if (offsetZSlider != null) offsetZSlider.value = currentOffsetZ;
        if (uScaleSlider != null) uScaleSlider.value = currentUScale;
        if (vScaleSlider != null) vScaleSlider.value = currentVScale;
        if (uOffsetSlider != null) uOffsetSlider.value = currentUOffset;
        if (vOffsetSlider != null) vOffsetSlider.value = currentVOffset;
        UpdateActiveCard();
    }

    void ApplyGroupUpdate(System.Action<HairCard> updateAction)
    {
        HairCard[] allCards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        foreach (HairCard card in allCards) if (card.groupId == currentGroupId) updateAction(card);
    }

    void CreateModeToggleButton(Transform parent)
    {
        GameObject containerGO = new GameObject("TopControlsRow", typeof(RectTransform));
        containerGO.transform.SetParent(parent, false);
        RectTransform containerRect = containerGO.GetComponent<RectTransform>();
        containerRect.sizeDelta = new Vector2(0, 40);
        HorizontalLayoutGroup hLayout = containerGO.AddComponent<HorizontalLayoutGroup>();
        hLayout.spacing = 8;
        hLayout.childControlWidth = true;
        hLayout.childControlHeight = true;

        GameObject modeBtnGO = new GameObject("ModeToggleButton", typeof(RectTransform), typeof(Image), typeof(Button));
        modeBtnGO.transform.SetParent(containerGO.transform, false);
        Image modeImg = modeBtnGO.GetComponent<Image>();
        modeImg.color = new Color(0.25f, 0.25f, 0.25f);
        Button modeBtn = modeBtnGO.GetComponent<Button>();
        GameObject modeTxtGO = new GameObject("Text", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI));
        modeTxtGO.transform.SetParent(modeBtnGO.transform, false);
        TMPro.TextMeshProUGUI modeTmp = modeTxtGO.GetComponent<TMPro.TextMeshProUGUI>();
        modeTmp.text = "Mode: ABS";
        modeTmp.fontSize = 16;
        modeTmp.fontStyle = TMPro.FontStyles.Bold;
        modeTmp.alignment = TMPro.TextAlignmentOptions.Center;
        modeTmp.color = Color.white;
        modeTxtGO.GetComponent<RectTransform>().anchorMin = Vector2.zero;
        modeTxtGO.GetComponent<RectTransform>().anchorMax = Vector2.one;
        modeTxtGO.GetComponent<RectTransform>().sizeDelta = Vector2.zero;
        modeBtn.onClick.AddListener(() => {
            isRelativeMode = !isRelativeMode;
            modeTmp.text = isRelativeMode ? "Mode: REL" : "Mode: ABS";
            modeImg.color = isRelativeMode ? new Color(0.2f, 0.5f, 0.8f) : new Color(0.25f, 0.25f, 0.25f);
        });

        GameObject saveProjBtnGO = new GameObject("SaveProjectButton", typeof(RectTransform), typeof(Image), typeof(Button));
        saveProjBtnGO.transform.SetParent(containerGO.transform, false);
        saveProjBtnGO.GetComponent<Image>().color = new Color(0.2f, 0.5f, 0.3f);
        Button saveProjBtn = saveProjBtnGO.GetComponent<Button>();
        GameObject saveProjTxtGO = new GameObject("Text", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI));
        saveProjTxtGO.transform.SetParent(saveProjBtnGO.transform, false);
        TMPro.TextMeshProUGUI saveProjTmp = saveProjTxtGO.GetComponent<TMPro.TextMeshProUGUI>();
        saveProjTmp.text = "SAVE PROJ";
        saveProjTmp.fontSize = 15;
        saveProjTmp.fontStyle = TMPro.FontStyles.Bold;
        saveProjTmp.alignment = TMPro.TextAlignmentOptions.Center;
        saveProjTmp.color = Color.white;
        saveProjTxtGO.GetComponent<RectTransform>().anchorMin = Vector2.zero;
        saveProjTxtGO.GetComponent<RectTransform>().anchorMax = Vector2.one;
        saveProjTxtGO.GetComponent<RectTransform>().sizeDelta = Vector2.zero;
        saveProjBtn.onClick.AddListener(SaveProject);

        GameObject resetBtnGO = new GameObject("ResetButton", typeof(RectTransform), typeof(Image), typeof(Button));
        resetBtnGO.transform.SetParent(containerGO.transform, false);
        resetBtnGO.GetComponent<Image>().color = new Color(0.6f, 0.2f, 0.2f);
        Button resetBtn = resetBtnGO.GetComponent<Button>();
        GameObject resetTxtGO = new GameObject("Text", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI));
        resetTxtGO.transform.SetParent(resetBtnGO.transform, false);
        TMPro.TextMeshProUGUI resetTmp = resetTxtGO.GetComponent<TMPro.TextMeshProUGUI>();
        resetTmp.text = "RESET";
        resetTmp.fontSize = 16;
        resetTmp.fontStyle = TMPro.FontStyles.Bold;
        resetTmp.alignment = TMPro.TextAlignmentOptions.Center;
        resetTmp.color = Color.white;
        resetTxtGO.GetComponent<RectTransform>().anchorMin = Vector2.zero;
        resetTxtGO.GetComponent<RectTransform>().anchorMax = Vector2.one;
        resetTxtGO.GetComponent<RectTransform>().sizeDelta = Vector2.zero;
        resetBtn.onClick.AddListener(ResetAllSliders);
    }

    void CreatePanelTabSwitcher(Transform parent)
    {
        GameObject tabRowGO = new GameObject("PanelTabRow", typeof(RectTransform));
        tabRowGO.transform.SetParent(parent, false);
        tabRowGO.GetComponent<RectTransform>().sizeDelta = new Vector2(0, 45);
        HorizontalLayoutGroup hLayout = tabRowGO.AddComponent<HorizontalLayoutGroup>();
        hLayout.spacing = 8;
        hLayout.childControlWidth = true;
        hLayout.childControlHeight = true;

        GameObject groomTabGO = new GameObject("GroomTabButton", typeof(RectTransform), typeof(Image), typeof(Button));
        groomTabGO.transform.SetParent(tabRowGO.transform, false);
        groomTabGO.GetComponent<Image>().color = new Color(0.2f, 0.5f, 0.8f);
        Button groomBtn = groomTabGO.GetComponent<Button>();
        GameObject groomTxtGO = new GameObject("Text", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI));
        groomTxtGO.transform.SetParent(groomTabGO.transform, false);
        TMPro.TextMeshProUGUI groomTmp = groomTxtGO.GetComponent<TMPro.TextMeshProUGUI>();
        groomTmp.text = "Groom Mode";
        groomTmp.fontSize = 16;
        groomTmp.fontStyle = TMPro.FontStyles.Bold;
        groomTmp.alignment = TMPro.TextAlignmentOptions.Center;
        groomTmp.color = Color.white;
        groomTxtGO.GetComponent<RectTransform>().anchorMin = Vector2.zero;
        groomTxtGO.GetComponent<RectTransform>().anchorMax = Vector2.one;
        groomTxtGO.GetComponent<RectTransform>().sizeDelta = Vector2.zero;

        GameObject texTabGO = new GameObject("TexTabButton", typeof(RectTransform), typeof(Image), typeof(Button));
        texTabGO.transform.SetParent(tabRowGO.transform, false);
        texTabGO.GetComponent<Image>().color = new Color(0.25f, 0.25f, 0.25f);
        Button texBtn = texTabGO.GetComponent<Button>();
        GameObject texTxtGO = new GameObject("Text", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI));
        texTxtGO.transform.SetParent(texTabGO.transform, false);
        TMPro.TextMeshProUGUI texTmp = texTxtGO.GetComponent<TMPro.TextMeshProUGUI>();
        texTmp.text = "Texture Editor";
        texTmp.fontSize = 16;
        texTmp.fontStyle = TMPro.FontStyles.Bold;
        texTmp.alignment = TMPro.TextAlignmentOptions.Center;
        texTmp.color = Color.white;
        texTxtGO.GetComponent<RectTransform>().anchorMin = Vector2.zero;
        texTxtGO.GetComponent<RectTransform>().anchorMax = Vector2.one;
        texTxtGO.GetComponent<RectTransform>().sizeDelta = Vector2.zero;
        groomBtn.onClick.AddListener(() => SwitchEditorMode(false));
        texBtn.onClick.AddListener(() => SwitchEditorMode(true));
    }

    public void SwitchEditorMode(bool textureMode)
    {
        isTextureEditorMode = textureMode;
        if (groomingSliderPanelGO != null) groomingSliderPanelGO.SetActive(!textureMode);
        Transform canvasTransform = groomingSliderPanelGO != null ? groomingSliderPanelGO.transform.parent : FindObjectsByType<Canvas>(FindObjectsSortMode.None).FirstOrDefault()?.transform;
        if (canvasTransform != null) textureEditorManager.SetPanelActive(textureMode, canvasTransform, () => SwitchEditorMode(false));
        if (loadedModel != null) loadedModel.SetActive(!textureMode);
        HairCard[] allCards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        foreach (var card in allCards) {
            var mr = card.GetComponent<MeshRenderer>();
            if (mr != null) mr.enabled = !textureMode;
        }
    }

    public void BuildRuntimeGroomingUI()
    {
        Canvas canvas = FindObjectsByType<Canvas>(FindObjectsSortMode.None).FirstOrDefault();
        if (canvas == null)
        {
            GameObject canvasGO = new GameObject("GroomingCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvas = canvasGO.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            CanvasScaler scaler = canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
        }
        if (FindObjectsByType<EventSystem>(FindObjectsSortMode.None).Length == 0) new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        GameObject panelGO = new GameObject("GroomingPanel", typeof(RectTransform), typeof(Image), typeof(GraphicRaycaster));
        panelGO.transform.SetParent(canvas.transform, false);
        RectTransform panelRect = panelGO.GetComponent<RectTransform>();
        panelRect.anchorMin = new Vector2(1, 0);
        panelRect.anchorMax = new Vector2(1, 1);
        panelRect.pivot = new Vector2(1, 0.5f);
        panelRect.sizeDelta = new Vector2(560, 0);
        panelRect.anchoredPosition = new Vector2(-10, 0);
        activePanelImage = panelGO.GetComponent<Image>();
        activePanelImage.color = new Color(0.15f, 0.15f, 0.15f, 0.85f);
        VerticalLayoutGroup layout = panelGO.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(15, 15, 12, 12);
        layout.spacing = 4;
        layout.childControlWidth = true;
        layout.childControlHeight = false;
        groomingSliderPanelGO = panelGO;
        activeSliderPanel = panelGO;
        CreatePanelTabSwitcher(panelGO.transform);
        CreateModeToggleButton(panelGO.transform);
        CreateSliderUI(panelGO.transform, "Length", 0.0001f, 1.0f, currentLength, OnActualSliderLengthChanged, out lengthSlider, 38, 16);
        CreateSliderUI(panelGO.transform, "Width", 0.0005f, 0.05f, currentWidth, OnSliderWidthChanged, out widthSlider, 38, 16);
        CreateSliderUI(panelGO.transform, "Curl Frequency", -10f, 10f, currentCurlFrequency, OnSliderCurlFrequencyChanged, out curlFrequencySlider, 38, 16);
        CreateSliderUI(panelGO.transform, "Curl Diameter", 0f, 0.15f, currentCurlDiameter, OnSliderCurlDiameterChanged, out curlDiameterSlider, 38, 16);
        // Label strings here are load-bearing: CreateSliderUI names the GameObject
        // labelText + "_Slider", and PostRootContextRestore, PostAffectorManager and
        // GroomSessionResetCoordinator all switch on those exact names. Amplitude mirrors
        // Curl Diameter's range (a magnitude, min 0); frequency mirrors Curl Frequency (signed).
        // Amplitude tops out at 0.03, not the 0.15 it shipped with. This is an absolute local-space
        // displacement and the default card is 0.2 long by 0.01 wide, so 0.15 was fifteen card
        // widths of throw and the shape saturated within the first fifth of the slider. 0.03 puts
        // the usable range across the whole travel. Raise this number if you want more headroom.
        CreateSliderUI(panelGO.transform, "Wave Amplitude", 0f, 0.03f, currentWaveAmplitude, OnSliderWaveAmplitudeChanged, out waveAmplitudeSlider, 38, 16);
        CreateSliderUI(panelGO.transform, "Wave Frequency", -10f, 10f, currentWaveFrequency, OnSliderWaveFrequencyChanged, out waveFrequencySlider, 38, 16);
        // 0 = <> side to side, 1 = up/dn, anything between is a diagonal.
        CreateSliderUI(panelGO.transform, "Wave Direction", 0f, 1f, currentWaveDirection, OnSliderWaveDirectionChanged, out waveDirectionSlider, 38, 16);
        // 0 = flat ribbon, 0.5 = the profile the tool has always had, 1 = twice the arch.
        CreateSliderUI(panelGO.transform, "Arch", 0f, 1f, currentArch, OnSliderArchChanged, out archSlider, 38, 16);
        CreateSliderUI(panelGO.transform, "Segments", 4, 60, currentSegments, OnSliderSegmentsChanged, out segmentsSlider, 38, 16);
        CreateSliderUI(panelGO.transform, "Bend Angle", -360f, 360f, currentBend, OnSliderBendChanged, out bendSlider, 38, 16);
        CreateSliderUI(panelGO.transform, "Twist Angle", -360f, 360f, currentTwist, OnSliderTwistChanged, out twistSlider, 38, 16);
        CreateSliderUI(panelGO.transform, "Embed Depth", 0.0f, 0.1f, currentEmbedDepth, OnSliderEmbedDepthChanged, out depthSlider, 38, 16);
        CreateSliderUI(panelGO.transform, "Offset X", -360f, 360f, currentOffsetX, OnSliderOffsetXChanged, out offsetXSlider, 38, 16);
        CreateSliderUI(panelGO.transform, "Offset Y", -360f, 360f, currentOffsetY, OnSliderOffsetYChanged, out offsetYSlider, 38, 16);
        CreateSliderUI(panelGO.transform, "Offset Z", -360f, 360f, currentOffsetZ, OnSliderOffsetZChanged, out offsetZSlider, 38, 16);
        CreateSliderUI(panelGO.transform, "U Scale", -1.0f, 1.0f, currentUScale, OnSliderUScaleChanged, out uScaleSlider, 38, 16);
        CreateSliderUI(panelGO.transform, "V Scale", -1.0f, 1.0f, currentVScale, OnSliderVScaleChanged, out vScaleSlider, 38, 16);
        CreateSliderUI(panelGO.transform, "U Offset", 0.0f, 1.0f, currentUOffset, OnSliderUOffsetChanged, out uOffsetSlider, 38, 16);
        CreateSliderUI(panelGO.transform, "V Offset", 0.0f, 1.0f, currentVOffset, OnSliderVOffsetChanged, out vOffsetSlider, 38, 16);
    }

    void BuildGroupManagementUI()
    {
        Transform canvasTransform = activeSliderPanel != null ? activeSliderPanel.transform.parent : FindObjectsByType<Canvas>(FindObjectsSortMode.None).FirstOrDefault()?.transform;
        if (canvasTransform == null) return;
        GameObject existing = GameObject.Find("GroupManagerPanel");
        if (existing != null) Destroy(existing);
        GameObject groupPanelGO = new GameObject("GroupManagerPanel", typeof(RectTransform), typeof(Image), typeof(GraphicRaycaster));
        groupPanelGO.transform.SetParent(canvasTransform, false);
        RectTransform panelRect = groupPanelGO.GetComponent<RectTransform>();
        panelRect.anchorMin = new Vector2(0, 0);
        panelRect.anchorMax = new Vector2(0, 1);
        panelRect.pivot = new Vector2(0, 0.5f);
        // 300 could not fit a POST row's five columns once the remove button became "DEL":
        // the row overflowed and the last column was clipped by the panel edge.
        panelRect.sizeDelta = new Vector2(360, 0);
        panelRect.anchoredPosition = new Vector2(15, 0);
        groupPanelGO.GetComponent<Image>().color = new Color(0.15f, 0.15f, 0.15f, 0.85f);
        VerticalLayoutGroup vLayout = groupPanelGO.AddComponent<VerticalLayoutGroup>();
        vLayout.padding = new RectOffset(10, 10, 10, 10);
        vLayout.spacing = 8;
        vLayout.childControlWidth = true;
        vLayout.childControlHeight = false;
        GameObject titleGO = new GameObject("TitleText", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI));
        titleGO.transform.SetParent(groupPanelGO.transform, false);
        TMPro.TextMeshProUGUI titleTmp = titleGO.GetComponent<TMPro.TextMeshProUGUI>();
        titleTmp.text = "Hair Groups";
        titleTmp.fontSize = 22;
        titleTmp.fontStyle = TMPro.FontStyles.Bold;
        titleTmp.color = Color.white;
        titleGO.GetComponent<RectTransform>().sizeDelta = new Vector2(0, 35);
        GameObject newBtnGO = new GameObject("NewGroupButton", typeof(RectTransform), typeof(Image), typeof(Button));
        newBtnGO.transform.SetParent(groupPanelGO.transform, false);
        newBtnGO.GetComponent<RectTransform>().sizeDelta = new Vector2(0, 45);
        newBtnGO.GetComponent<Image>().color = new Color(0.2f, 0.5f, 0.8f);
        Button btn = newBtnGO.GetComponent<Button>();
        GameObject btnTextGO = new GameObject("Text", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI));
        btnTextGO.transform.SetParent(newBtnGO.transform, false);
        TMPro.TextMeshProUGUI btnTmp = btnTextGO.GetComponent<TMPro.TextMeshProUGUI>();
        btnTmp.text = "+ GROUP";
        btnTmp.fontSize = 18;
        btnTmp.alignment = TMPro.TextAlignmentOptions.Center;
        btnTmp.color = Color.white;
        btnTextGO.GetComponent<RectTransform>().anchorMin = Vector2.zero;
        btnTextGO.GetComponent<RectTransform>().anchorMax = Vector2.one;
        btnTextGO.GetComponent<RectTransform>().sizeDelta = Vector2.zero;
        btn.onClick.AddListener(() => {
            // Put down whatever was being edited before the new group arrives. Most of this
            // already happened by accident - NewGroupRootSelectionAuthority reacts to the new id
            // and tears down the POST and clumper, and the guide notices the group changed under
            // it - but an ARMED +POST/+CLUMPER/+GUIDE did not, and it survives with its old group
            // id, so the next click on the model drops a modifier into the group you just left
            // while card placement stays switched off. Doing it here rather than leaving it to
            // that authority also means it happens in the same frame as the click, before
            // anything else has read the half-torn-down state.
            ModifierContextExit.LeaveEverything(this);

            int newId = GetNextAvailableGroupId();
            allGroupIds.Add(newId);
            groupNames[newId] = "Group " + newId;
            groupUScales[newId] = 1.0f;
            groupVScales[newId] = 1.0f;
            groupUOffsets[newId] = 0.0f;
            groupVOffsets[newId] = 0.0f;
            // Starting a new group while a SOLO is live would otherwise drop you into a
            // group SOLO is hiding, so every hair you then place would be invisible with no
            // explanation. Creating a group is a deliberate "start something new", which is
            // the natural point to end the SOLO rather than to fight it.
            ResetSoloState();
            SelectGroup(newId);
        });
        GameObject scrollGO = new GameObject("GroupScrollView", typeof(RectTransform), typeof(ScrollRect), typeof(Image));
        scrollGO.transform.SetParent(groupPanelGO.transform, false);
        scrollGO.GetComponent<RectTransform>().sizeDelta = new Vector2(0, 600);
        scrollGO.GetComponent<Image>().color = new Color(0.1f, 0.1f, 0.1f, 0.5f);
        GameObject viewportGO = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
        viewportGO.transform.SetParent(scrollGO.transform, false);
        RectTransform viewportRect = viewportGO.GetComponent<RectTransform>();
        viewportRect.anchorMin = Vector2.zero;
        viewportRect.anchorMax = Vector2.one;
        viewportRect.sizeDelta = Vector2.zero;
        GameObject contentGO = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
        contentGO.transform.SetParent(viewportGO.transform, false);
        RectTransform contentRect = contentGO.GetComponent<RectTransform>();
        contentRect.anchorMin = new Vector2(0, 1);
        contentRect.anchorMax = new Vector2(1, 1);
        contentRect.pivot = new Vector2(0.5f, 1f);
        contentRect.sizeDelta = new Vector2(0, 0);
        VerticalLayoutGroup contentLayout = contentGO.GetComponent<VerticalLayoutGroup>();
        contentLayout.spacing = 6;
        contentLayout.padding = new RectOffset(5, 5, 5, 5);
        contentLayout.childControlWidth = true;
        contentLayout.childControlHeight = false;
        contentLayout.childForceExpandHeight = false;
        contentGO.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        ScrollRect scrollRect = scrollGO.GetComponent<ScrollRect>();
        scrollRect.content = contentRect;
        scrollRect.viewport = viewportRect;
        scrollRect.horizontal = false;
        scrollRect.vertical = true;
        groupListContentTransform = contentGO.transform;
        RefreshGroupListUI();
    }

    void SelectGroup(int id)
    {
        currentGroupId = id;
        currentUScale = groupUScales.ContainsKey(id) ? groupUScales[id] : 1.0f;
        currentVScale = groupVScales.ContainsKey(id) ? groupVScales[id] : 1.0f;
        currentUOffset = groupUOffsets.ContainsKey(id) ? groupUOffsets[id] : 0.0f;
        currentVOffset = groupVOffsets.ContainsKey(id) ? groupVOffsets[id] : 0.0f;
        groupUScales[id] = currentUScale;
        groupVScales[id] = currentVScale;
        groupUOffsets[id] = currentUOffset;
        groupVOffsets[id] = currentVOffset;
        // Sets every current* field from the group's root state and then pushes the whole
        // slider set - UV included, because the four UV fields were assigned just above.
        SyncShapeSlidersToGroupRoot(id);
        RefreshGroupListUI();
        if (flashGroupCoroutine != null) StopCoroutine(flashGroupCoroutine);
        flashGroupCoroutine = StartCoroutine(FlashActiveGroupRoutine(currentGroupId));
    }

    // Selecting a group only ever synced the UV sliders - Length/Width/Bend/Twist/Curl/etc kept
    // showing whatever a PREVIOUSLY-edited POST or group happened to leave them at, silently
    // misleading anyone who assumed the sliders reflected the newly-selected group. This gives
    // every shape slider the same "known resting point" guarantee SelectAffector already gives
    // when switching between POSTs. Also called (via SyncGroomingSlidersToCurrent) when leaving
    // POST/CLUMPER editing back to plain group context.
    static float MedianOf(List<HairCard.GroomState> states, Func<HairCard.GroomState, float> selector)
    {
        List<float> values = new List<float>(states.Count);
        foreach (HairCard.GroomState state in states) values.Add(selector(state));
        values.Sort();

        int middle = values.Count / 2;
        if (values.Count % 2 == 1) return values[middle];
        return (values[middle - 1] + values[middle]) * .5f;
    }

    public void SyncShapeSlidersToGroupRoot(int groupId)
    {
        if (rootStateAuthority == null) rootStateAuthority = FindFirstObjectByType<GroomRootStateAuthority>();
        GroomRootStateAuthority.RootState state = default;
        bool found = rootStateAuthority != null && rootStateAuthority.TryGetRootState(groupId, out state);

        if (!found)
        {
            // No stored root yet for this group (a freshly loaded project, or a group
            // that has never entered/exited a POST or CLUMPER) - recover it from the
            // cards themselves, which carry the real authored values.
            //
            // Every field is taken as the MEDIAN across the group's cards rather than
            // from one arbitrary card. With variance switched on, the first card found
            // is as likely as not to be an outlier, and adopting it would quietly make
            // every newly placed hair an outlier too. GetCanonicalState is the value
            // before POST/CLUMPER deltas, so modified cards do not skew it either.
            List<HairCard.GroomState> sampled = new List<HairCard.GroomState>();
            foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
            {
                if (card == null || card.groupId != groupId) continue;
                sampled.Add(card.GetCanonicalState());
            }

            if (sampled.Count > 0)
            {
                state = new GroomRootStateAuthority.RootState
                {
                    length = MedianOf(sampled, s => s.length),
                    width = MedianOf(sampled, s => s.width),
                    segments = Mathf.RoundToInt(MedianOf(sampled, s => s.segments)),
                    bend = MedianOf(sampled, s => s.bend),
                    twist = MedianOf(sampled, s => s.twist),
                    depth = MedianOf(sampled, s => s.depth),
                    x = MedianOf(sampled, s => s.x),
                    y = MedianOf(sampled, s => s.y),
                    z = MedianOf(sampled, s => s.z),
                    curlFrequency = MedianOf(sampled, s => s.curlFrequency),
                    curlDiameter = MedianOf(sampled, s => s.curlDiameter),
                    waveAmplitude = MedianOf(sampled, s => s.waveAmplitude),
                    waveFrequency = MedianOf(sampled, s => s.waveFrequency),
                    waveDirection = MedianOf(sampled, s => s.waveDirection),
                    arch = MedianOf(sampled, s => s.arch)
                };
                found = true;
            }
        }

        if (!found)
        {
            // Brand new, empty group - the same defaults GroomSessionResetCoordinator uses.
            state = new GroomRootStateAuthority.RootState
            {
                length = .2f, width = .01f, segments = 12, bend = 0f, twist = 0f, depth = .002f,
                x = 0f, y = 0f, z = 0f, curlFrequency = 0f, curlDiameter = 0f, waveAmplitude = 0f, waveFrequency = 0f, waveDirection = 1f, arch = HairCard.ArchNeutral
            };
        }

        currentLength = state.length;
        currentWidth = state.width;
        currentSegments = state.segments;
        currentBend = state.bend;
        currentTwist = state.twist;
        currentEmbedDepth = state.depth;
        currentOffsetX = state.x;
        currentOffsetY = state.y;
        currentOffsetZ = state.z;
        currentCurlFrequency = state.curlFrequency;
        currentCurlDiameter = state.curlDiameter;
        currentWaveAmplitude = state.waveAmplitude;
        currentWaveFrequency = state.waveFrequency;
        currentWaveDirection = state.waveDirection;
        currentArch = state.arch;

        PushAllGroomSliders();
    }

    // THE one place every grooming slider is pushed from its backing field.
    //
    // This list used to be split in two - the shape sliders here, the four UV sliders inline in
    // SelectGroup - and that split is exactly how it drifted: every parameter added since has
    // needed a hand edit in both, and a miss shows up as "I clicked the group and that one
    // slider kept its old value", which is easy to see and hard to attribute.
    //
    // Anything with a slider goes in here and nowhere else. A new parameter is then picked up
    // by group selection, by POST/CLUMPER exit and by the modifier-exit restore for free.
    //
    // SetValueWithoutNotify throughout, never .value: these are all UI-follows-state pushes,
    // and firing the change callbacks would write the values straight back out over the group.
    void PushAllGroomSliders()
    {
        if (lengthSlider != null) lengthSlider.SetValueWithoutNotify(currentLength);
        if (widthSlider != null) widthSlider.SetValueWithoutNotify(currentWidth);
        if (segmentsSlider != null) segmentsSlider.SetValueWithoutNotify(currentSegments);
        if (bendSlider != null) bendSlider.SetValueWithoutNotify(currentBend);
        if (twistSlider != null) twistSlider.SetValueWithoutNotify(currentTwist);
        if (depthSlider != null) depthSlider.SetValueWithoutNotify(currentEmbedDepth);
        if (offsetXSlider != null) offsetXSlider.SetValueWithoutNotify(currentOffsetX);
        if (offsetYSlider != null) offsetYSlider.SetValueWithoutNotify(currentOffsetY);
        if (offsetZSlider != null) offsetZSlider.SetValueWithoutNotify(currentOffsetZ);
        if (curlFrequencySlider != null) curlFrequencySlider.SetValueWithoutNotify(currentCurlFrequency);
        if (curlDiameterSlider != null) curlDiameterSlider.SetValueWithoutNotify(currentCurlDiameter);
        if (waveAmplitudeSlider != null) waveAmplitudeSlider.SetValueWithoutNotify(currentWaveAmplitude);
        if (waveFrequencySlider != null) waveFrequencySlider.SetValueWithoutNotify(currentWaveFrequency);
        if (waveDirectionSlider != null) waveDirectionSlider.SetValueWithoutNotify(currentWaveDirection);
        if (archSlider != null) archSlider.SetValueWithoutNotify(currentArch);
        if (uScaleSlider != null) uScaleSlider.SetValueWithoutNotify(currentUScale);
        if (vScaleSlider != null) vScaleSlider.SetValueWithoutNotify(currentVScale);
        if (uOffsetSlider != null) uOffsetSlider.SetValueWithoutNotify(currentUOffset);
        if (vOffsetSlider != null) vOffsetSlider.SetValueWithoutNotify(currentVOffset);
    }

    IEnumerator FlashActiveGroupRoutine(int activeId)
    {
        // The "which group is this?" flash briefly hides every other group, then restores.
        //
        // Two things were wrong with the old version. It restored by blanket-enabling EVERY
        // renderer in the scene, which silently cancelled SOLO the moment you clicked a
        // second group - the whole groom reappeared. And it ran at all while soloing, where
        // it is meaningless: the soloed group is already the only thing on screen, so the
        // flash could only ever reveal something SOLO was deliberately hiding.
        //
        // So: while SOLO is engaged the flash is skipped entirely, and the restore always
        // goes back through the authority rather than turning everything on.
        if (GroupSoloVisibilityAuthority.AnySolo) yield break;

        HairCard[] allCards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        foreach (var card in allCards) if (card != null && card.groupId != activeId) { var mr = card.GetComponent<MeshRenderer>(); if (mr != null) mr.enabled = false; }
        yield return new WaitForSeconds(0.5f);
        GroupSoloVisibilityAuthority.ApplyVisibility();
    }

    void RefreshGroupListUI()
    {
        if (groupListContentTransform == null) return;

        HairCard[] allCards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        Dictionary<int, int> groupCardCounts = new Dictionary<int, int>();
        foreach (var card in allCards)
        {
            if (!groupCardCounts.ContainsKey(card.groupId)) groupCardCounts[card.groupId] = 0;
            groupCardCounts[card.groupId]++;
        }

        // Most refreshes only change presentation (card count, active color, name, solo state).
        // Updating those rows in place preserves POST/modifier children and keeps the ScrollRect
        // from collapsing/re-expanding every time a hair card is placed.
        Dictionary<int, Transform> existingGroups = new Dictionary<int, Transform>();
        foreach (Transform child in groupListContentTransform)
        {
            if (child == null || !child.name.StartsWith("GroupItem_")) continue;
            if (int.TryParse(child.name.Substring("GroupItem_".Length), out int gid))
                existingGroups[gid] = child;
        }

        bool structureMatches = existingGroups.Count == allGroupIds.Count && allGroupIds.All(id => existingGroups.ContainsKey(id));
        if (structureMatches)
        {
            foreach (int id in allGroupIds)
            {
                Transform item = existingGroups[id];
                Image bg = item.GetComponent<Image>();
                if (bg != null)
                    bg.color = id == currentGroupId ? new Color(0.3f, 0.6f, 0.3f, 1f) : new Color(0.25f, 0.25f, 0.25f, 1f);

                Transform labelButton = item.Find("LabelButton");
                if (labelButton != null)
                {
                    Transform nameLabel = labelButton.Find("Label");
                    TMPro.TextMeshProUGUI nameTmp = nameLabel != null ? nameLabel.GetComponent<TMPro.TextMeshProUGUI>() : null;
                    if (nameTmp != null)
                        nameTmp.text = groupNames.ContainsKey(id) ? groupNames[id] : ("Group " + id);

                    Transform countLabel = labelButton.Find("CardCountLabel");
                    TMPro.TextMeshProUGUI countTmp = countLabel != null ? countLabel.GetComponent<TMPro.TextMeshProUGUI>() : null;
                    if (countTmp != null)
                    {
                        int count = groupCardCounts.ContainsKey(id) ? groupCardCounts[id] : 0;
                        countTmp.text = count + (count == 1 ? " card" : " cards");
                    }
                }

                Transform solo = item.Find("SoloButton");
                Image soloImage = solo != null ? solo.GetComponent<Image>() : null;
                if (soloImage != null)
                {
                    bool isSoloed = GroupSoloVisibilityAuthority.IsSoloed(id);
                    Color soloColor = new Color(0.35f, 0.35f, 0.35f);
                    if (isSoloed) soloColor = new Color(0.9f, 0.5f, 0.1f);
                    soloImage.color = soloColor;
                }
            }
            return;
        }

        // Group structure really changed (add/delete/load/reset), so a full rebuild is warranted.
        foreach (Transform child in groupListContentTransform) Destroy(child.gameObject);
        foreach (int id in allGroupIds.OrderBy(g => g))
        {
            int gid = id;
            int cardCount = groupCardCounts.ContainsKey(gid) ? groupCardCounts[gid] : 0;
            GameObject itemGO = new GameObject("GroupItem_" + gid, typeof(RectTransform), typeof(Image), typeof(HorizontalLayoutGroup));
            itemGO.transform.SetParent(groupListContentTransform, false);
            itemGO.GetComponent<RectTransform>().sizeDelta = new Vector2(0, 48);
            itemGO.GetComponent<Image>().color = (gid == currentGroupId) ? new Color(0.3f, 0.6f, 0.3f, 1f) : new Color(0.25f, 0.25f, 0.25f, 1f);
            HorizontalLayoutGroup rowLayout = itemGO.GetComponent<HorizontalLayoutGroup>();
            rowLayout.padding = new RectOffset(8, 8, 4, 4);
            rowLayout.spacing = 8;
            rowLayout.childControlWidth = false;
            rowLayout.childControlHeight = true;
            GameObject labelBtnGO = new GameObject("LabelButton", typeof(RectTransform), typeof(Button), typeof(CustomClickDetector));
            labelBtnGO.transform.SetParent(itemGO.transform, false);
            labelBtnGO.GetComponent<RectTransform>().sizeDelta = new Vector2(170, 40);
            labelBtnGO.GetComponent<Button>().onClick.AddListener(() => HandleGroupItemClick(gid));
            labelBtnGO.GetComponent<CustomClickDetector>().onRightClick = () => PromptDeleteGroup(gid);
            GameObject txtGO = new GameObject("Label", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI));
            txtGO.transform.SetParent(labelBtnGO.transform, false);
            TMPro.TextMeshProUGUI tmp = txtGO.GetComponent<TMPro.TextMeshProUGUI>();
            tmp.text = groupNames.ContainsKey(gid) ? groupNames[gid] : ("Group " + gid);
            tmp.fontSize = 14;
            tmp.fontStyle = TMPro.FontStyles.Bold;
            tmp.alignment = TMPro.TextAlignmentOptions.TopLeft;
            tmp.color = Color.white;
            RectTransform txtRect = txtGO.GetComponent<RectTransform>();
            txtRect.anchorMin = Vector2.zero;
            txtRect.anchorMax = Vector2.one;
            txtRect.offsetMin = new Vector2(2, 2);
            txtRect.offsetMax = new Vector2(-2, -2);
            GameObject countTxtGO = new GameObject("CardCountLabel", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI));
            countTxtGO.transform.SetParent(labelBtnGO.transform, false);
            TMPro.TextMeshProUGUI countTmp = countTxtGO.GetComponent<TMPro.TextMeshProUGUI>();
            countTmp.text = cardCount + (cardCount == 1 ? " card" : " cards");
            countTmp.fontSize = 11;
            countTmp.alignment = TMPro.TextAlignmentOptions.BottomLeft;
            countTmp.color = new Color(0.8f, 0.8f, 0.8f, 0.9f);
            RectTransform countRect = countTxtGO.GetComponent<RectTransform>();
            countRect.anchorMin = Vector2.zero;
            countRect.anchorMax = Vector2.one;
            countRect.offsetMin = new Vector2(2, 2);
            countRect.offsetMax = new Vector2(-2, -2);
            GameObject soloBtnGO = new GameObject("SoloButton", typeof(RectTransform), typeof(Image), typeof(Button));
            soloBtnGO.transform.SetParent(itemGO.transform, false);
            soloBtnGO.GetComponent<RectTransform>().sizeDelta = new Vector2(65, 36);
            bool isSoloed = GroupSoloVisibilityAuthority.IsSoloed(gid);
            Color soloIdleColor = new Color(0.35f, 0.35f, 0.35f);
            if (isSoloed) soloIdleColor = new Color(0.9f, 0.5f, 0.1f);
            soloBtnGO.GetComponent<Image>().color = soloIdleColor;
            Button soloBtn = soloBtnGO.GetComponent<Button>();
            GameObject soloTxtGO = new GameObject("Text", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI));
            soloTxtGO.transform.SetParent(soloBtnGO.transform, false);
            TMPro.TextMeshProUGUI soloTmp = soloTxtGO.GetComponent<TMPro.TextMeshProUGUI>();
            soloTmp.text = "SOLO";
            soloTmp.fontSize = 13;
            soloTmp.fontStyle = TMPro.FontStyles.Bold;
            soloTmp.alignment = TMPro.TextAlignmentOptions.Center;
            soloTmp.color = Color.white;
            soloTxtGO.GetComponent<RectTransform>().anchorMin = Vector2.zero;
            soloTxtGO.GetComponent<RectTransform>().anchorMax = Vector2.one;
            soloTxtGO.GetComponent<RectTransform>().sizeDelta = Vector2.zero;
            soloBtn.onClick.AddListener(() => ToggleGroupSolo(gid));
        }
    }

    void ToggleGroupSolo(int gid)
    {
        // GroupSoloVisibilityAuthority owns both the solo set AND renderer enablement now,
        // so this no longer walks the cards itself. groupSoloState is kept as a mirror only
        // because two other scripts still reflect into it by name.
        bool nowSoloed = GroupSoloVisibilityAuthority.Toggle(gid);
        groupSoloState[gid] = nowSoloed;
        RefreshGroupListUI();
    }

    // SOLO is session-only by design - it is never saved and never restored. Loading a
    // project, or resetting the session, must therefore come up with everything visible.
    public void ResetSoloState()
    {
        groupSoloState.Clear();
        GroupSoloVisibilityAuthority.ClearAll();
    }

    // Clicking a group's name means "I want to work on this group", and that is only true if
    // everything else lets go of the panel first.
    //
    // Some of it already worked, unevenly. A POST released itself, but only for a click on this
    // exact button and not for SOLO or the sidedness toggles next to it. A clumper released
    // itself for a click anywhere in the row. A guide released itself only if you clicked a
    // DIFFERENT group - clicking the row of the group you were already in left the guide selected,
    // its panel covering the groom sliders, and GroomingInputLock still held, so card placement
    // stayed off with nothing on screen saying why. An armed +POST/+CLUMPER/+GUIDE released
    // itself for nothing at all, because a click on the panel is deliberately not treated as a
    // placement. One call covers the lot, and covers it the same way every time.
    //
    // Before the double-click branch, not inside it: a rename is still a click on this group, and
    // the teardown commits any half-typed name on a DIFFERENT group rather than dropping it.
    void HandleGroupItemClick(int gid)
    {
        ModifierContextExit.LeaveEverything(this);

        float timeSinceLastClick = Time.time - lastGroupClickTime;
        if (lastClickedGroupId == gid && timeSinceLastClick < 0.4f) { PromptRenameGroup(gid); lastClickedGroupId = -1; }
        else { SelectGroup(gid); lastClickedGroupId = gid; lastGroupClickTime = Time.time; }
    }

    void PromptRenameGroup(int gid)
    {
        // Runtime inline rename - works in the standalone build as well as in the
        // editor. The group button's own name line turns into a live text field
        // with a blinking caret instead of opening an editor-only modal dialog.
        GroupNameInlineEditAuthority.BeginEdit(gid);
    }

    void PromptDeleteGroup(int gid)
    {
#if UNITY_EDITOR
        if (EditorUtility.DisplayDialog("Delete Group", "Are you sure you want to delete this group and all its hair cards?", "Yes", "No")) DeleteGroupAndCards(gid);
#endif
    }

    void DeleteGroupAndCards(int gid)
    {
        HairCard[] allCards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        foreach (var card in allCards) if (card.groupId == gid) Destroy(card.gameObject);
        allGroupIds.Remove(gid);
        groupNames.Remove(gid);
        groupSoloState.Remove(gid);
        // A deleted group must not keep a SOLO no card can satisfy - that would leave the
        // whole scene hidden with no button left to switch it back off.
        GroupSoloVisibilityAuthority.Forget(gid);
        groupUScales.Remove(gid);
        groupVScales.Remove(gid);
        groupUOffsets.Remove(gid);
        groupVOffsets.Remove(gid);
        if (currentGroupId == gid) { currentGroupId = allGroupIds.FirstOrDefault(); SelectGroup(currentGroupId); }
        RefreshGroupListUI();
    }

    int GetNextAvailableGroupId() { int id = 0; while (allGroupIds.Contains(id)) id++; return id; }

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
        slider.onValueChanged.AddListener((val) => { tmp.text = labelText + ": " + val.ToString("F3"); onValueChanged.Invoke(val); });
        createdSlider = slider;
        return rowGO;
    }

    // Is there a LIVE localized selection - a hotspot that some card in this group actually
    // carries weight for?
    //
    // hasSelectionHotspot on its own is not enough, and trusting it is why group-level slider
    // edits could silently do nothing at all. PostFreeCanonicalAuthority calls
    // SetSelectionWeight(0f) on every card of every group that has NO POSTs, every frame. So
    // the moment the last POST is removed from a group, every card's weight is pinned to zero -
    // while hasSelectionHotspot can still be true, because nothing on that path clears it
    // (ClearSelectionHotspot is only reached by ctrl+clicking empty space, or by
    // ClumperPostOwnershipAuthority, and that one only fires while a clumper is SELECTED).
    //
    // In that state every slider callback below routed to UpdateActiveCard(), which filters on
    // selectionWeight > 0f and therefore matched ZERO cards. The slider moved, current* updated,
    // and not one card was touched. No error, no visual change, nothing to attribute it to.
    //
    // Asking whether any card is actually weighted turns a stale flag into a harmless one.
    bool HasLiveSelection()
    {
        if (!hasSelectionHotspot) return false;

        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
        {
            if (card == null || card.groupId != currentGroupId) continue;
            if (card.selectionWeight > 0f) return true;
        }

        // Claimed, but nothing carries weight - so there is no selection to edit. Drop the flag
        // rather than re-scanning on every future slider event, and go through the real teardown
        // so the brush rows and highlight state are cleaned up too.
        ClearSelectionHotspot();
        return false;
    }

    void UpdateActiveCard()
    {
        if (hasSelectionHotspot)
        {
            HairCard[] allCards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
            foreach (HairCard card in allCards) if (card.groupId == currentGroupId && card.selectionWeight > 0f) card.SetParameters(currentLength, currentWidth, currentSegments, currentBend, currentTwist, currentOffsetX, currentOffsetY, currentOffsetZ, currentEmbedDepth, selectionStrength, currentUScale, currentVScale, currentUOffset, currentVOffset, currentCurlFrequency, currentCurlDiameter, currentWaveAmplitude, currentWaveFrequency, currentWaveDirection, currentArch);
        }
        else if (lastPlacedCard != null) lastPlacedCard.SetParameters(currentLength, currentWidth, currentSegments, currentBend, currentTwist, currentOffsetX, currentOffsetY, currentOffsetZ, currentEmbedDepth, 1f, currentUScale, currentVScale, currentUOffset, currentVOffset, currentCurlFrequency, currentCurlDiameter, currentWaveAmplitude, currentWaveFrequency, currentWaveDirection, currentArch);
    }

    void Update() { HandleCameraControls(); HandleGrooming(); }

    void OnDrawGizmos() { if (!hasSelectionHotspot) return; Gizmos.color = Color.yellow; Gizmos.DrawLine(selectionHitPoint, selectionHitPoint + (selectionHitNormal * 2.0f)); }

    public void ToggleGroomingMode(bool state) { isGroomingMode = state; }

    void HandleGrooming()
    {
        if (!isGroomingMode || Mouse.current == null || isTextureEditorMode) return;
        // SHIFT/ALT/CTRL are name characters and modifiers while a text box is open.
        if (GroupNameInlineEditAuthority.IsEnteringText) return;
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;
        bool isHoldingAlt = Keyboard.current != null && (Keyboard.current.leftAltKey.isPressed || Keyboard.current.rightAltKey.isPressed);
        bool isHoldingCtrl = Keyboard.current != null && Keyboard.current.ctrlKey.isPressed;
        bool isHoldingShift = Keyboard.current != null && (Keyboard.current.leftShiftKey.isPressed || Keyboard.current.rightShiftKey.isPressed);
        if (isHoldingAlt && Mouse.current.leftButton.wasPressedThisFrame)
        {
            Ray altRay = mainCamera.ScreenPointToRay(Mouse.current.position.ReadValue());
            if (Physics.Raycast(altRay, out RaycastHit altHit))
            {
                HairCard[] allCards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
                if (allCards.Length > 0) { HairCard nearestCard = allCards.OrderBy(c => Vector3.Distance(altHit.point, c.transform.position)).FirstOrDefault(); if (nearestCard != null) SelectGroup(nearestCard.groupId); }
            }
            return;
        }
        if (isHoldingShift && !wasHoldingShiftDrag) { wasHoldingShiftDrag = true; sessionPlacedCards.Clear(); }
        if (wasHoldingShiftDrag && !isHoldingShift)
        {
#if UNITY_EDITOR
            if (sessionPlacedCards.Count > 0 && EditorUtility.DisplayDialog("New Group", "Do you want to create a new group for the hair cards placed during this stroke?", "Yes", "No"))
            {
                int newId = GetNextAvailableGroupId();
                allGroupIds.Add(newId);
                groupNames[newId] = "Group " + newId;
                groupUScales[newId] = currentUScale;
                groupVScales[newId] = currentVScale;
                groupUOffsets[newId] = currentUOffset;
                groupVOffsets[newId] = currentVOffset;
                foreach (var card in sessionPlacedCards) if (card != null) { card.groupId = newId; card.SetParameters(card.length, card.width, card.segments, card.bendAngle, card.twistAngle, card.GetOffsetX(), card.GetOffsetY(), card.GetOffsetZ(), card.GetEmbedDepth(), 1f, currentUScale, currentVScale, currentUOffset, currentVOffset, card.curlFrequency, card.curlDiameter, card.waveAmplitude, card.waveFrequency, card.waveDirection, card.arch); }
                // Those cards just changed group. Same reasoning as the New Group button:
                // a shift-drag that promotes itself into a fresh group is the start of new
                // work, so end the SOLO rather than have the cards you just drew vanish
                // into a group SOLO is hiding.
                ResetSoloState();
                SelectGroup(newId);
            }
#endif
            wasHoldingShiftDrag = false;
            sessionPlacedCards.Clear();
            RefreshGroupListUI();
        }
        if (isHoldingCtrl && Mouse.current.leftButton.wasPressedThisFrame)
        {
            Ray ray = mainCamera.ScreenPointToRay(Mouse.current.position.ReadValue());
            if (Physics.Raycast(ray, out RaycastHit hit)) EnterSelectionMode(hit.point, hit.normal); else ClearSelectionHotspot();
            return;
        }
        bool shouldSpawn = isHoldingShift ? (Mouse.current.leftButton.isPressed && Time.time >= lastSpawnTime + spawnCooldown) : Mouse.current.leftButton.wasPressedThisFrame;
        if (shouldSpawn && !isSelectionMode)
        {
            Ray ray = mainCamera.ScreenPointToRay(Mouse.current.position.ReadValue());
            if (Physics.Raycast(ray, out RaycastHit hit)) { HairCard card = PinHairCard(hit.point, hit.normal); if (isHoldingShift && card != null) sessionPlacedCards.Add(card); lastSpawnTime = Time.time; }
        }
    }

    void EnterSelectionMode(Vector3 brushCenter, Vector3 hitNormal)
    {
        if (falloffRowGO != null) Destroy(falloffRowGO);
        if (strengthRowGO != null) Destroy(strengthRowGO);
        isSelectionMode = true;
        hasSelectionHotspot = true;
        selectionStrength = 0.25f;
        brushFalloffDistance = 0.25f;
        selectionHitPoint = brushCenter;
        selectionHitNormal = hitNormal;
        if (activePanelImage != null) activePanelImage.color = new Color(0.35f, 0.32f, 0.1f, 0.9f);
        HairCard[] groupCards = FindObjectsByType<HairCard>(FindObjectsSortMode.None).Where(c => c.groupId == currentGroupId).ToArray();
        if (groupCards.Length > 0)
        {
            var nearestCards = groupCards.OrderBy(card => Vector3.Distance(brushCenter, card.transform.position)).Take(6).ToList();
            float totalWeight = 0f, avgLength = 0f, avgWidth = 0f, avgBend = 0f, avgTwist = 0f, avgCurlFrequency = 0f, avgCurlDiameter = 0f, avgWaveAmplitude = 0f, avgWaveFrequency = 0f, avgWaveDirection = 0f, avgArch = 0f;
            int accumulatedSegments = 0;
            foreach (var card in nearestCards) { float dist = Vector3.Distance(brushCenter, card.transform.position); float weight = 1f / (dist + 0.0001f); totalWeight += weight; avgLength += card.length * weight; avgWidth += card.width * weight; avgBend += card.bendAngle * weight; avgTwist += card.twistAngle * weight; avgCurlFrequency += card.curlFrequency * weight; avgCurlDiameter += card.curlDiameter * weight; avgWaveAmplitude += card.waveAmplitude * weight; avgWaveFrequency += card.waveFrequency * weight; avgWaveDirection += card.waveDirection * weight; avgArch += card.arch * weight; accumulatedSegments += card.segments; }
            if (totalWeight > 0f)
            {
                currentLength = avgLength / totalWeight;
                currentWidth = avgWidth / totalWeight;
                currentBend = avgBend / totalWeight;
                currentTwist = avgTwist / totalWeight;
                currentCurlFrequency = avgCurlFrequency / totalWeight;
                currentCurlDiameter = avgCurlDiameter / totalWeight;
                currentWaveAmplitude = avgWaveAmplitude / totalWeight;
                currentWaveFrequency = avgWaveFrequency / totalWeight;
                currentWaveDirection = avgWaveDirection / totalWeight;
                currentArch = avgArch / totalWeight;
                currentSegments = Mathf.RoundToInt((float)accumulatedSegments / nearestCards.Count);
                if (lengthSlider != null) lengthSlider.SetValueWithoutNotify(currentLength);
                if (widthSlider != null) widthSlider.SetValueWithoutNotify(currentWidth);
                if (bendSlider != null) bendSlider.SetValueWithoutNotify(currentBend);
                if (twistSlider != null) twistSlider.SetValueWithoutNotify(currentTwist);
                if (curlFrequencySlider != null) curlFrequencySlider.SetValueWithoutNotify(currentCurlFrequency);
                if (curlDiameterSlider != null) curlDiameterSlider.SetValueWithoutNotify(currentCurlDiameter);
        if (waveAmplitudeSlider != null) waveAmplitudeSlider.SetValueWithoutNotify(currentWaveAmplitude);
        if (waveFrequencySlider != null) waveFrequencySlider.SetValueWithoutNotify(currentWaveFrequency);
        if (waveDirectionSlider != null) waveDirectionSlider.SetValueWithoutNotify(currentWaveDirection);
        if (archSlider != null) archSlider.SetValueWithoutNotify(currentArch);
                if (segmentsSlider != null) segmentsSlider.SetValueWithoutNotify(currentSegments);
            }
        }
        if (activeSliderPanel != null)
        {
            falloffRowGO = CreateSliderUI(activeSliderPanel.transform, "Falloff Dist", 0.001f, 1.0f, brushFalloffDistance, (val) => { brushFalloffDistance = val; RecomputeSelectionWeights(selectionHitPoint); }, out _, 38, 16);
            strengthRowGO = CreateSliderUI(activeSliderPanel.transform, "Strength", 0.0f, 1.0f, selectionStrength, (val) => { selectionStrength = val; UpdateActiveCard(); }, out _, 38, 16);
        }
        RecomputeSelectionWeights(brushCenter);
    }

    void ClearSelectionHotspot()
    {
        hasSelectionHotspot = false;
        isSelectionMode = false;
        if (activePanelImage != null) activePanelImage.color = new Color(0.15f, 0.15f, 0.15f, 0.85f);
        if (falloffRowGO != null) Destroy(falloffRowGO);
        if (strengthRowGO != null) Destroy(strengthRowGO);
        HairCard[] allCards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        foreach (HairCard card in allCards) { card.SetSelectionWeight(0f); card.UpdateVisualHighlight(); }
    }

    void RecomputeSelectionWeights(Vector3 brushCenter)
    {
        HairCard[] allCards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        foreach (HairCard card in allCards)
        {
            if (card.groupId != currentGroupId) { card.SetSelectionWeight(0f); continue; }
            float distance = Vector3.Distance(brushCenter, card.transform.position);
            if (distance <= brushFalloffDistance) { float weight = Mathf.Clamp01(1f - (distance / brushFalloffDistance)); card.SetSelectionWeight(weight); card.CaptureBaseState(card.length, card.width, card.segments, card.bendAngle, card.twistAngle, card.GetEmbedDepth(), card.GetOffsetX(), card.GetOffsetY(), card.GetOffsetZ(), card.curlFrequency, card.curlDiameter, card.waveAmplitude, card.waveFrequency, card.waveDirection, card.arch); }
            else card.SetSelectionWeight(0f);
        }
    }

    HairCard PinHairCard(Vector3 position, Vector3 normal)
    {
        HairCard placed = SpawnHairCard(position, normal, false);

        // SYMMETRY. Every placement mode in the project funnels through PinHairCard - the
        // legacy click and shift-drag here in HandleGrooming, and PLACE / PAINT / SPRAY / EVEN
        // in PlacementBrushModeAuthority, which reaches this method by reflection. Hooking it
        // once therefore covers all six without any of them needing to know symmetry exists.
        //
        // EVEN is the one caller that needs to know a mirror happened: it keeps a running list of
        // occupied roots to space against, and a mirror it did not account for would let it place
        // on top of one. It predicts the mirror by calling TryMirror itself with the same
        // arguments rather than being told, so nothing here has to report back.
        //
        // Note the mirrored card is spawned through SpawnHairCard, NOT through PinHairCard, so
        // it cannot itself trigger another mirror. TryMirror also declines points near the
        // midline, where a mirror would just stack a duplicate on top of the original.
        Vector3 mirroredPosition;
        Vector3 mirroredNormal;
        if (GroomSymmetryAuthority.TryMirror(position, normal, out mirroredPosition, out mirroredNormal))
            SpawnHairCard(mirroredPosition, mirroredNormal, true);

        RefreshGroupListUI();
        return placed;
    }

    // The actual spawn. `isMirrored` marks the card as living on the reflected side; see
    // HairCard.mirrored for what that does and, more importantly, why the mirror is a flag on
    // the card rather than a set of pre-negated numbers.
    HairCard SpawnHairCard(Vector3 position, Vector3 normal, bool isMirrored)
    {
        GameObject cardGO = new GameObject("HairCard_Strip", typeof(MeshFilter), typeof(MeshRenderer), typeof(HairCard));
        HairCard card = cardGO.GetComponent<HairCard>();

        // Set BEFORE SetPlacementData, which orients the transform and captures canonical
        // state - both of which read the flag.
        card.mirrored = isMirrored;

        card.SetPlacementData(position, normal, currentEmbedDepth, currentOffsetX, currentOffsetY, currentOffsetZ, currentGroupId);
        card.SetParameters(currentLength, currentWidth, currentSegments, currentBend, currentTwist, currentOffsetX, currentOffsetY, currentOffsetZ, currentEmbedDepth, 1f, currentUScale, currentVScale, currentUOffset, currentVOffset, currentCurlFrequency, currentCurlDiameter, currentWaveAmplitude, currentWaveFrequency, currentWaveDirection, currentArch);

        // Deliberately only the primary card becomes lastPlacedCard. That reference is what
        // the sliders steer when nothing is selected, and it would be surprising for a slider
        // to start driving the mirrored copy instead of the one you just painted.
        if (!isMirrored) lastPlacedCard = card;

        MeshRenderer mr = cardGO.GetComponent<MeshRenderer>();
        if (hairCardMaterial != null) mr.sharedMaterial = hairCardMaterial;
        // A card born into a group SOLO is hiding must not appear. New renderers default to
        // enabled, so without this the groom leaks back one strand at a time.
        mr.enabled = GroupSoloVisibilityAuthority.IsGroupVisible(card.groupId);
        return card;
    }

    void HandleCameraControls()
    {
        if (Mouse.current == null) return;

        // Guide point editing needs ALT plus right click to mean "remove this point", and orbiting
        // is on the right button and would otherwise run at the same moment - the point would go
        // and the view would swing while it went. So an orbit REFUSES TO START on a right press
        // made with ALT down while a guide is being shaped.
        //
        // Both halves of that matter. Tested on isPressed rather than the press edge, an orbit
        // already under way would freeze the instant ALT was touched; and applied whether or not
        // a guide is selected, ALT plus right would stop the camera everywhere in the app in
        // exchange for nothing, when holding ALT while dragging the view around is an ordinary
        // thing to be doing.
        bool altHeld = Keyboard.current != null &&
                       (Keyboard.current.leftAltKey.isPressed || Keyboard.current.rightAltKey.isPressed);

        // Over a panel the guide editor stands down and removes nothing, so suppressing the orbit
        // there would cost a drag and buy nothing.
        bool overUI = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
        if (Mouse.current.rightButton.wasPressedThisFrame)
            orbitSuppressed = altHeld && !overUI &&
                              GroupAddButtonPlacementAuthority.ArmedKind == GroupAddButtonPlacementAuthority.AddKind.None &&
                              GuideCurveManager.AnyGuideSelected;
        if (!Mouse.current.rightButton.isPressed) orbitSuppressed = false;

        if (Mouse.current.rightButton.isPressed && !orbitSuppressed)
        {
            float mouseX = Mouse.current.delta.x.ReadValue() * 0.1f;
            float mouseY = Mouse.current.delta.y.ReadValue() * 0.1f;
            cameraPivot.Rotate(Vector3.up, mouseX * rotateSpeed, Space.World);
            pitch -= mouseY * rotateSpeed;
            pitch = Mathf.Clamp(pitch, -89f, 89f);
            cameraPivot.eulerAngles = new Vector3(pitch, cameraPivot.eulerAngles.y, 0f);
        }
        if (Mouse.current.middleButton.isPressed)
        {
            float mouseX = Mouse.current.delta.x.ReadValue() * 0.1f;
            float mouseY = Mouse.current.delta.y.ReadValue() * 0.1f;
            cameraPivot.Translate(Vector3.left * mouseX * panSpeed, Space.Self);
            cameraPivot.Translate(Vector3.down * mouseY * panSpeed, Space.Self);
        }
        float scroll = Mouse.current.scroll.y.ReadValue();
        if (scroll != 0.0f)
        {
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;
            mainCamera.transform.Translate(Vector3.forward * (scroll * 0.001f) * zoomSpeed, Space.Self);
        }
    }

    public void SaveProject()
    {
#if UNITY_EDITOR
        double now = EditorApplication.timeSinceStartup;
        if (now < nextAllowedSaveDialogTime) return;

        string path = EditorUtility.SaveFilePanel("Save Hair Project", "", "HairProject", "json");
        nextAllowedSaveDialogTime = EditorApplication.timeSinceStartup + 0.75;
        if (string.IsNullOrEmpty(path)) return;

        HairProjectSaveData saveData = new HairProjectSaveData();
        saveData.modelPath = currentModelPath;
        saveData.sliderLength = currentLength;
        saveData.sliderWidth = currentWidth;
        saveData.sliderSegments = currentSegments;
        saveData.sliderBend = currentBend;
        saveData.sliderTwist = currentTwist;
        saveData.sliderEmbedDepth = currentEmbedDepth;
        saveData.sliderOffsetX = currentOffsetX;
        saveData.sliderOffsetY = currentOffsetY;
        saveData.sliderOffsetZ = currentOffsetZ;
        saveData.sliderUScale = currentUScale;
        saveData.sliderVScale = currentVScale;
        saveData.sliderUOffset = currentUOffset;
        saveData.sliderVOffset = currentVOffset;
        saveData.sliderCurlFrequency = currentCurlFrequency;
        saveData.sliderCurlDiameter = currentCurlDiameter;
        saveData.sliderWaveAmplitude = currentWaveAmplitude;
        saveData.sliderWaveFrequency = currentWaveFrequency;
        saveData.sliderWaveDirection = currentWaveDirection;
        saveData.sliderArch = currentArch;
        foreach (int id in allGroupIds)
        {
            GroupSaveData gData = new GroupSaveData();
            gData.groupId = id;
            gData.groupName = groupNames.ContainsKey(id) ? groupNames[id] : ("Group " + id);
            gData.uScale = groupUScales.ContainsKey(id) ? groupUScales[id] : 1.0f;
            gData.vScale = groupVScales.ContainsKey(id) ? groupVScales[id] : 1.0f;
            gData.uOffset = groupUOffsets.ContainsKey(id) ? groupUOffsets[id] : 0.0f;
            gData.vOffset = groupVOffsets.ContainsKey(id) ? groupVOffsets[id] : 0.0f;
            saveData.groups.Add(gData);
        }
        HairCard[] allCards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        foreach (var card in allCards)
        {
            HairCardSaveData cardData = new HairCardSaveData();
            cardData.posX = card.transform.position.x;
            cardData.posY = card.transform.position.y;
            cardData.posZ = card.transform.position.z;
            cardData.rotX = card.transform.rotation.x;
            cardData.rotY = card.transform.rotation.y;
            cardData.rotZ = card.transform.rotation.z;
            cardData.rotW = card.transform.rotation.w;
            cardData.length = card.length;
            cardData.width = card.width;
            cardData.segments = card.segments;
            cardData.bendAngle = card.bendAngle;
            cardData.twistAngle = card.twistAngle;
            cardData.flattenFactor = card.flattenFactor;
            cardData.embedDepth = card.GetEmbedDepth();
            cardData.offsetX = card.GetOffsetX();
            cardData.offsetY = card.GetOffsetY();
            cardData.offsetZ = card.GetOffsetZ();
            cardData.uScale = card.uScale;
            cardData.vScale = card.vScale;
            cardData.uOffset = card.uOffset;
            cardData.vOffset = card.vOffset;
            cardData.groupId = card.groupId;
            cardData.curlFrequency = card.curlFrequency;
            cardData.curlDiameter = card.curlDiameter;
            cardData.waveAmplitude = card.waveAmplitude;
            cardData.waveFrequency = card.waveFrequency;
            cardData.waveDirection = card.waveDirection;
            cardData.arch = card.arch;
            cardData.mirrored = card.mirrored;
            saveData.hairCards.Add(cardData);
        }
        string json = JsonUtility.ToJson(saveData, true);
        System.IO.File.WriteAllText(path, json);
        Debug.Log("Project saved successfully to: " + path);
#endif
    }

    public void LoadProject()
    {
#if UNITY_EDITOR
        string path = EditorUtility.OpenFilePanel("Open Hair Project", "", "json");
        if (string.IsNullOrEmpty(path)) return;
        string json = System.IO.File.ReadAllText(path);
        HairProjectSaveData saveData = JsonUtility.FromJson<HairProjectSaveData>(json);
        if (!string.IsNullOrEmpty(saveData.modelPath))
        {
            currentModelPath = saveData.modelPath;
            if (loadedModel != null) Destroy(loadedModel);
            loadedModel = CustomOBJImporter.Load(currentModelPath);
            if (loadedModel != null)
            {
                loadedModel.transform.position = Vector3.zero;
                loadedModel.transform.eulerAngles = new Vector3(0f, 180f, 0f);
                MeshRenderer[] renderers = loadedModel.GetComponentsInChildren<MeshRenderer>();
                if (renderers.Length > 0)
                {
                    Bounds combinedBounds = renderers[0].bounds;
                    for (int i = 1; i < renderers.Length; i++) combinedBounds.Encapsulate(renderers[i].bounds);
                    if (cameraPivot != null) cameraPivot.position = combinedBounds.center;
                }
            }
        }
        HairCard[] oldCards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        foreach (var card in oldCards) Destroy(card.gameObject);
        currentLength = saveData.sliderLength;
        currentWidth = saveData.sliderWidth;
        currentSegments = saveData.sliderSegments;
        currentBend = saveData.sliderBend;
        currentTwist = saveData.sliderTwist;
        currentEmbedDepth = saveData.sliderEmbedDepth;
        currentOffsetX = saveData.sliderOffsetX;
        currentOffsetY = saveData.sliderOffsetY;
        currentOffsetZ = saveData.sliderOffsetZ;
        currentUScale = saveData.sliderUScale != 0 ? saveData.sliderUScale : 1.0f;
        currentVScale = saveData.sliderVScale != 0 ? saveData.sliderVScale : 1.0f;
        currentUOffset = saveData.sliderUOffset;
        currentVOffset = saveData.sliderVOffset;
        currentCurlFrequency = saveData.sliderCurlFrequency;
        currentCurlDiameter = saveData.sliderCurlDiameter;
        currentWaveAmplitude = saveData.sliderWaveAmplitude;
        currentWaveFrequency = saveData.sliderWaveFrequency;
        currentWaveDirection = saveData.sliderWaveDirection;
        currentArch = saveData.sliderArch;
        allGroupIds.Clear();
        groupNames.Clear();
        groupUScales.Clear();
        groupVScales.Clear();
        groupUOffsets.Clear();
        groupVOffsets.Clear();
        foreach (var g in saveData.groups)
        {
            allGroupIds.Add(g.groupId);
            groupNames[g.groupId] = g.groupName;
            groupUScales[g.groupId] = g.uScale != 0 ? g.uScale : 1.0f;
            groupVScales[g.groupId] = g.vScale != 0 ? g.vScale : 1.0f;
            groupUOffsets[g.groupId] = g.uOffset;
            groupVOffsets[g.groupId] = g.vOffset;
        }
        foreach (var cData in saveData.hairCards)
        {
            GameObject cardGO = new GameObject("HairCard_Strip", typeof(MeshFilter), typeof(MeshRenderer), typeof(HairCard));
            HairCard card = cardGO.GetComponent<HairCard>();
            card.transform.position = new Vector3(cData.posX, cData.posY, cData.posZ);
            card.transform.rotation = new Quaternion(cData.rotX, cData.rotY, cData.rotZ, cData.rotW);
            card.groupId = cData.groupId;
            // Before SetParameters below, which rebuilds the mesh from it.
            card.mirrored = cData.mirrored;
            float u = cData.uScale != 0 ? cData.uScale : 1.0f;
            float v = cData.vScale != 0 ? cData.vScale : 1.0f;
            card.SetParameters(cData.length, cData.width, cData.segments, cData.bendAngle, cData.twistAngle, cData.offsetX, cData.offsetY, cData.offsetZ, cData.embedDepth, 1f, u, v, cData.uOffset, cData.vOffset, cData.curlFrequency, cData.curlDiameter, cData.waveAmplitude, cData.waveFrequency, cData.waveDirection, cData.arch);
            MeshRenderer mr = cardGO.GetComponent<MeshRenderer>();
            if (hairCardMaterial != null) mr.sharedMaterial = hairCardMaterial;
        }

        // Same rule as the enhanced loader: SOLO is session-only, so a load always comes up
        // with everything visible and every group live again. Cleared after the cards exist
        // so ApplyVisibility can reach them.
        ResetSoloState();

        if (uiContainer != null) uiContainer.SetActive(false);
        OnModelLoaded();
        if (activeSliderPanel == null) BuildRuntimeGroomingUI();
        BuildGroupManagementUI();
        isGroomingMode = true;

        // Come up with the first group properly selected, so the sliders show that
        // group's own settings and the next hair placed inherits them. See the same
        // step in RuntimeNavigationProjectIO.SelectLoadedGroup.
        if (rootStateAuthority == null) rootStateAuthority = FindFirstObjectByType<GroomRootStateAuthority>();
        if (rootStateAuthority != null) rootStateAuthority.ForgetStoredRoots();

        int firstGroupId = 0;
        if (allGroupIds.Count > 0) firstGroupId = allGroupIds.OrderBy(g => g).First();
        SelectGroup(firstGroupId);

        Debug.Log("Project loaded successfully from: " + path);
#endif
    }
}

public class CustomClickDetector : MonoBehaviour, IPointerClickHandler
{
    public System.Action onRightClick;
    public void OnPointerClick(PointerEventData eventData) { if (eventData.button == PointerEventData.InputButton.Right) onRightClick?.Invoke(); }
}

#if UNITY_EDITOR
public class EditorInputDialog : EditorWindow
{
    private string inputString = "";
    private string description = "";
    public static string Show(string title, string desc, string defaultText)
    {
        EditorInputDialog window = CreateInstance<EditorInputDialog>();
        window.titleContent = new GUIContent(title);
        window.description = desc;
        window.inputString = defaultText;
        window.ShowModalUtility();
        return window.inputString;
    }
    void OnGUI()
    {
        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField(description);
        EditorGUILayout.Space(5);
        inputString = EditorGUILayout.TextField(inputString);
        EditorGUILayout.Space(15);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("OK")) Close();
        if (GUILayout.Button("Cancel")) { inputString = ""; Close(); }
        EditorGUILayout.EndHorizontal();
    }
}
#endif