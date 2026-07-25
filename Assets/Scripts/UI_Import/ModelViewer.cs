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

    // Group Management
    public int currentGroupId = 0;
    private HashSet<int> allGroupIds = new HashSet<int>() { 0 };
    private Dictionary<int, string> groupNames = new Dictionary<int, string>() { { 0, "Group 0 (Default)" } };
    private Dictionary<int, bool> groupSoloState = new Dictionary<int, bool>();
    private List<HairCard> sessionPlacedCards = new List<HairCard>(); // Tracks cards placed during current Shift drag session
    private Transform groupListContentTransform;
    private bool wasHoldingShiftDrag = false;
    private Coroutine flashGroupCoroutine;
    private float lastGroupClickTime = 0f;
    private int lastClickedGroupId = -1;

    // Brush & Selection State Tracking
    [Header("Brush Settings")]
    public float brushRadius = 0.2f;
    public float brushFalloffDistance = 0.05f;
    public float selectionStrength = 0.25f;
    private bool isSelectionMode = false;
    private bool hasSelectionHotspot = false;
    private bool isRelativeMode = false;
    private Vector3 selectionHitPoint;
    private Vector3 selectionHitNormal;

    private GameObject falloffRowGO;
    private GameObject strengthRowGO;

    private UnityEngine.UI.Slider lengthSlider;
    private UnityEngine.UI.Slider widthSlider;
    private UnityEngine.UI.Slider segmentsSlider;
    private UnityEngine.UI.Slider bendSlider;
    private UnityEngine.UI.Slider twistSlider;
    private UnityEngine.UI.Slider depthSlider;
    private UnityEngine.UI.Slider offsetXSlider;
    private UnityEngine.UI.Slider offsetYSlider;
    private UnityEngine.UI.Slider offsetZSlider;

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

            if (uiContainer != null)
            {
                uiContainer.SetActive(false);
            }

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
        if (hasSelectionHotspot) UpdateActiveCard();
        else ApplyGroupUpdate(c => c.SetParameters(Mathf.Max(0.001f, isRelativeMode ? c.length + delta : val), c.width, c.segments, c.bendAngle, c.twistAngle, c.GetOffsetX(), c.GetOffsetY(), c.GetOffsetZ(), c.GetEmbedDepth(), 1f));
    }

    public void OnSliderWidthChanged(float val)
    {
        float delta = val - currentWidth;
        currentWidth = val;
        if (hasSelectionHotspot) UpdateActiveCard();
        else ApplyGroupUpdate(c => c.SetParameters(c.length, Mathf.Max(0.0005f, isRelativeMode ? c.width + delta : val), c.segments, c.bendAngle, c.twistAngle, c.GetOffsetX(), c.GetOffsetY(), c.GetOffsetZ(), c.GetEmbedDepth(), 1f));
    }

    public void OnSliderSegmentsChanged(float val)
    {
        int targetSegs = Mathf.RoundToInt(val);
        int deltaSegs = targetSegs - currentSegments;
        currentSegments = targetSegs;
        if (hasSelectionHotspot) UpdateActiveCard();
        else ApplyGroupUpdate(c => c.SetParameters(c.length, c.width, Mathf.Clamp(isRelativeMode ? c.segments + deltaSegs : targetSegs, 4, 36), c.bendAngle, c.twistAngle, c.GetOffsetX(), c.GetOffsetY(), c.GetOffsetZ(), c.GetEmbedDepth(), 1f));
    }

    public void OnSliderBendChanged(float val)
    {
        float delta = val - currentBend;
        currentBend = val;
        if (hasSelectionHotspot) UpdateActiveCard();
        else ApplyGroupUpdate(c => c.SetParameters(c.length, c.width, c.segments, isRelativeMode ? c.bendAngle + delta : val, c.twistAngle, c.GetOffsetX(), c.GetOffsetY(), c.GetOffsetZ(), c.GetEmbedDepth(), 1f));
    }

    public void OnSliderTwistChanged(float val)
    {
        float delta = val - currentTwist;
        currentTwist = val;
        if (hasSelectionHotspot) UpdateActiveCard();
        else ApplyGroupUpdate(c => c.SetParameters(c.length, c.width, c.segments, c.bendAngle, isRelativeMode ? c.twistAngle + delta : val, c.GetOffsetX(), c.GetOffsetY(), c.GetOffsetZ(), c.GetEmbedDepth(), 1f));
    }

    public void OnSliderEmbedDepthChanged(float val)
    {
        float delta = val - currentEmbedDepth;
        currentEmbedDepth = val;
        if (hasSelectionHotspot) UpdateActiveCard();
        else ApplyGroupUpdate(c => c.SetParameters(c.length, c.width, c.segments, c.bendAngle, c.twistAngle, c.GetOffsetX(), c.GetOffsetY(), c.GetOffsetZ(), Mathf.Max(0f, isRelativeMode ? c.GetEmbedDepth() + delta : val), 1f));
    }

    public void OnSliderOffsetXChanged(float val)
    {
        float delta = val - currentOffsetX;
        currentOffsetX = val;
        if (hasSelectionHotspot) UpdateActiveCard();
        else ApplyGroupUpdate(c => c.SetParameters(c.length, c.width, c.segments, c.bendAngle, c.twistAngle, isRelativeMode ? c.GetOffsetX() + delta : val, c.GetOffsetY(), c.GetOffsetZ(), c.GetEmbedDepth(), 1f));
    }

    public void OnSliderOffsetYChanged(float val)
    {
        float delta = val - currentOffsetY;
        currentOffsetY = val;
        if (hasSelectionHotspot) UpdateActiveCard();
        else ApplyGroupUpdate(c => c.SetParameters(c.length, c.width, c.segments, c.bendAngle, c.twistAngle, c.GetOffsetX(), isRelativeMode ? c.GetOffsetY() + delta : val, c.GetOffsetZ(), c.GetEmbedDepth(), 1f));
    }

    public void OnSliderOffsetZChanged(float val)
    {
        float delta = val - currentOffsetZ;
        currentOffsetZ = val;
        if (hasSelectionHotspot) UpdateActiveCard();
        else ApplyGroupUpdate(c => c.SetParameters(c.length, c.width, c.segments, c.bendAngle, c.twistAngle, c.GetOffsetX(), c.GetOffsetY(), isRelativeMode ? c.GetOffsetZ() + delta : val, c.GetEmbedDepth(), 1f));
    }

    void ResetAllSliders()
    {
        currentLength = 0.2f;
        currentWidth = 0.01f;
        currentSegments = 12;
        currentBend = 0f;
        currentTwist = 0f;
        currentEmbedDepth = 0.002f;
        currentOffsetX = 0f;
        currentOffsetY = 0f;
        currentOffsetZ = 0f;

        if (lengthSlider != null) lengthSlider.value = currentLength;
        if (widthSlider != null) widthSlider.value = currentWidth;
        if (segmentsSlider != null) segmentsSlider.value = currentSegments;
        if (bendSlider != null) bendSlider.value = currentBend;
        if (twistSlider != null) twistSlider.value = currentTwist;
        if (depthSlider != null) depthSlider.value = currentEmbedDepth;
        if (offsetXSlider != null) offsetXSlider.value = currentOffsetX;
        if (offsetYSlider != null) offsetYSlider.value = currentOffsetY;
        if (offsetZSlider != null) offsetZSlider.value = currentOffsetZ;

        UpdateActiveCard();
    }

    void ApplyGroupUpdate(System.Action<HairCard> updateAction)
    {
        HairCard[] allCards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        foreach (HairCard card in allCards)
        {
            if (card.groupId == currentGroupId)
            {
                updateAction(card);
            }
        }
    }

    void CreateModeToggleButton(Transform parent)
    {
        GameObject containerGO = new GameObject("TopControlsRow", typeof(RectTransform));
        containerGO.transform.SetParent(parent, false);
        RectTransform containerRect = containerGO.GetComponent<RectTransform>();
        containerRect.sizeDelta = new Vector2(0, 45);

        HorizontalLayoutGroup hLayout = containerGO.AddComponent<HorizontalLayoutGroup>();
        hLayout.spacing = 10;
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
        modeTmp.fontSize = 18;
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
        Image saveProjImg = saveProjBtnGO.GetComponent<Image>();
        saveProjImg.color = new Color(0.2f, 0.5f, 0.3f);
        Button saveProjBtn = saveProjBtnGO.GetComponent<Button>();

        GameObject saveProjTxtGO = new GameObject("Text", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI));
        saveProjTxtGO.transform.SetParent(saveProjBtnGO.transform, false);
        TMPro.TextMeshProUGUI saveProjTmp = saveProjTxtGO.GetComponent<TMPro.TextMeshProUGUI>();
        saveProjTmp.text = "SAVE PROJ";
        saveProjTmp.fontSize = 16;
        saveProjTmp.fontStyle = TMPro.FontStyles.Bold;
        saveProjTmp.alignment = TMPro.TextAlignmentOptions.Center;
        saveProjTmp.color = Color.white;
        saveProjTxtGO.GetComponent<RectTransform>().anchorMin = Vector2.zero;
        saveProjTxtGO.GetComponent<RectTransform>().anchorMax = Vector2.one;
        saveProjTxtGO.GetComponent<RectTransform>().sizeDelta = Vector2.zero;

        saveProjBtn.onClick.AddListener(() => {
            SaveProject();
        });

        GameObject resetBtnGO = new GameObject("ResetButton", typeof(RectTransform), typeof(Image), typeof(Button));
        resetBtnGO.transform.SetParent(containerGO.transform, false);
        Image resetImg = resetBtnGO.GetComponent<Image>();
        resetImg.color = new Color(0.6f, 0.2f, 0.2f);
        Button resetBtn = resetBtnGO.GetComponent<Button>();

        GameObject resetTxtGO = new GameObject("Text", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI));
        resetTxtGO.transform.SetParent(resetBtnGO.transform, false);
        TMPro.TextMeshProUGUI resetTmp = resetTxtGO.GetComponent<TMPro.TextMeshProUGUI>();
        resetTmp.text = "RESET";
        resetTmp.fontSize = 18;
        resetTmp.fontStyle = TMPro.FontStyles.Bold;
        resetTmp.alignment = TMPro.TextAlignmentOptions.Center;
        resetTmp.color = Color.white;
        resetTxtGO.GetComponent<RectTransform>().anchorMin = Vector2.zero;
        resetTxtGO.GetComponent<RectTransform>().anchorMax = Vector2.one;
        resetTxtGO.GetComponent<RectTransform>().sizeDelta = Vector2.zero;

        resetBtn.onClick.AddListener(() => {
            ResetAllSliders();
        });
    }

    public void BuildRuntimeGroomingUI()
    {
        Canvas canvas = FindObjectsByType<Canvas>(FindObjectsSortMode.None).FirstOrDefault();
        if (canvas == null)
        {
            GameObject canvasGO = new GameObject("GroomingCanvas", typeof(Canvas), typeof(UnityEngine.UI.CanvasScaler), typeof(UnityEngine.UI.GraphicRaycaster));
            canvas = canvasGO.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            UnityEngine.UI.CanvasScaler scaler = canvasGO.GetComponent<UnityEngine.UI.CanvasScaler>();
            scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
        }

        if (FindObjectsByType<EventSystem>(FindObjectsSortMode.None).Length == 0)
        {
            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        }

        GameObject panelGO = new GameObject("GroomingPanel", typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.GraphicRaycaster));
        panelGO.transform.SetParent(canvas.transform, false);

        RectTransform panelRect = panelGO.GetComponent<RectTransform>();
        panelRect.anchorMin = new Vector2(1, 0);
        panelRect.anchorMax = new Vector2(1, 1);
        panelRect.pivot = new Vector2(1, 0.5f);
        panelRect.sizeDelta = new Vector2(560, 0);
        panelRect.anchoredPosition = new Vector2(-10, 0);

        activePanelImage = panelGO.GetComponent<UnityEngine.UI.Image>();
        activePanelImage.color = new Color(0.15f, 0.15f, 0.15f, 0.85f);

        VerticalLayoutGroup layout = panelGO.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(15, 15, 15, 15);
        layout.spacing = 8;
        layout.childControlWidth = true;
        layout.childControlHeight = false;

        activeSliderPanel = panelGO;

        CreateModeToggleButton(panelGO.transform);

        CreateSliderUI(panelGO.transform, "Length", 0.0005f, 1.0f, currentLength, OnActualSliderLengthChanged, out lengthSlider, 50, 20);
        CreateSliderUI(panelGO.transform, "Width", 0.0005f, 0.05f, currentWidth, OnSliderWidthChanged, out widthSlider, 50, 20);
        CreateSliderUI(panelGO.transform, "Segments", 4, 36, currentSegments, OnSliderSegmentsChanged, out segmentsSlider, 50, 20);
        CreateSliderUI(panelGO.transform, "Bend Angle", -360f, 360f, currentBend, OnSliderBendChanged, out bendSlider, 50, 20);
        CreateSliderUI(panelGO.transform, "Twist Angle", -360f, 360f, currentTwist, OnSliderTwistChanged, out twistSlider, 50, 20);
        CreateSliderUI(panelGO.transform, "Embed Depth", 0.0f, 0.1f, currentEmbedDepth, OnSliderEmbedDepthChanged, out depthSlider, 50, 20);
        CreateSliderUI(panelGO.transform, "Offset X", -360f, 360f, currentOffsetX, OnSliderOffsetXChanged, out offsetXSlider, 50, 20);
        CreateSliderUI(panelGO.transform, "Offset Y", -360f, 360f, currentOffsetY, OnSliderOffsetYChanged, out offsetYSlider, 50, 20);
        CreateSliderUI(panelGO.transform, "Offset Z", -360f, 360f, currentOffsetZ, OnSliderOffsetZChanged, out offsetZSlider, 50, 20);
    }

    void BuildGroupManagementUI()
    {
        Transform canvasTransform = activeSliderPanel != null ? activeSliderPanel.transform.parent : FindObjectsByType<Canvas>(FindObjectsSortMode.None).FirstOrDefault()?.transform;
        if (canvasTransform == null) return;

        GameObject groupPanelGO = new GameObject("GroupManagerPanel", typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.GraphicRaycaster));
        groupPanelGO.transform.SetParent(canvasTransform, false);

        RectTransform panelRect = groupPanelGO.GetComponent<RectTransform>();
        panelRect.anchorMin = new Vector2(0, 0);
        panelRect.anchorMax = new Vector2(0, 1);
        panelRect.pivot = new Vector2(0, 0.5f);
        panelRect.sizeDelta = new Vector2(300, 0);
        panelRect.anchoredPosition = new Vector2(15, 0);

        Image bgImage = groupPanelGO.GetComponent<Image>();
        bgImage.color = new Color(0.15f, 0.15f, 0.15f, 0.85f);

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
            int newId = GetNextAvailableGroupId();
            allGroupIds.Add(newId);
            groupNames[newId] = "Group " + newId;
            SelectGroup(newId);
        });

        GameObject scrollGO = new GameObject("GroupScrollView", typeof(RectTransform), typeof(ScrollRect), typeof(Image));
        scrollGO.transform.SetParent(groupPanelGO.transform, false);
        RectTransform scrollRectTransform = scrollGO.GetComponent<RectTransform>();
        scrollRectTransform.sizeDelta = new Vector2(0, 600);
        scrollGO.GetComponent<Image>().color = new Color(0.1f, 0.1f, 0.1f, 0.5f);

        GameObject viewportGO = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
        viewportGO.transform.SetParent(scrollGO.transform, false);
        RectTransform viewportRect = viewportGO.GetComponent<RectTransform>();
        viewportRect.anchorMin = Vector2.zero;
        viewportRect.anchorMax = Vector2.one;
        viewportRect.sizeDelta = Vector2.zero;

        GameObject contentGO = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup));
        contentGO.transform.SetParent(viewportGO.transform, false);
        RectTransform contentRect = contentGO.GetComponent<RectTransform>();
        contentRect.anchorMin = new Vector2(0, 1);
        contentRect.anchorMax = new Vector2(1, 1);
        contentRect.pivot = new Vector2(0.5f, 1f);
        contentRect.sizeDelta = new Vector2(0, 300);

        VerticalLayoutGroup contentLayout = contentGO.GetComponent<VerticalLayoutGroup>();
        contentLayout.spacing = 8;
        contentLayout.padding = new RectOffset(5, 5, 5, 5);
        contentLayout.childControlWidth = true;
        contentLayout.childControlHeight = false;

        ScrollRect scrollRect = scrollGO.GetComponent<ScrollRect>();
        scrollRect.content = contentRect;
        scrollRect.viewport = viewportRect;
        scrollRect.horizontal = false;

        groupListContentTransform = contentGO.transform;
        RefreshGroupListUI();
    }

    void SelectGroup(int id)
    {
        currentGroupId = id;
        RefreshGroupListUI();

        if (flashGroupCoroutine != null)
        {
            StopCoroutine(flashGroupCoroutine);
        }
        flashGroupCoroutine = StartCoroutine(FlashActiveGroupRoutine(currentGroupId));
    }

    IEnumerator FlashActiveGroupRoutine(int activeId)
    {
        HairCard[] allCards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);

        foreach (var card in allCards)
        {
            if (card.groupId != activeId)
            {
                var mr = card.GetComponent<MeshRenderer>();
                if (mr != null) mr.enabled = false;
            }
        }

        yield return new WaitForSeconds(0.5f);

        foreach (var card in allCards)
        {
            if (card != null)
            {
                var mr = card.GetComponent<MeshRenderer>();
                if (mr != null) mr.enabled = true;
            }
        }
    }

    void RefreshGroupListUI()
    {
        if (groupListContentTransform == null) return;

        foreach (Transform child in groupListContentTransform)
        {
            Destroy(child.gameObject);
        }

        // Count how many cards belong to each group for display purposes
        HairCard[] allCards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        Dictionary<int, int> groupCardCounts = new Dictionary<int, int>();
        foreach (var card in allCards)
        {
            if (!groupCardCounts.ContainsKey(card.groupId)) groupCardCounts[card.groupId] = 0;
            groupCardCounts[card.groupId]++;
        }

        foreach (int id in allGroupIds.OrderBy(g => g))
        {
            int gid = id;
            int cardCount = groupCardCounts.ContainsKey(gid) ? groupCardCounts[gid] : 0;

            GameObject itemGO = new GameObject("GroupItem_" + gid, typeof(RectTransform), typeof(Image), typeof(HorizontalLayoutGroup));
            itemGO.transform.SetParent(groupListContentTransform, false);
            itemGO.GetComponent<RectTransform>().sizeDelta = new Vector2(0, 48); // Slightly taller to accommodate card count text

            Image img = itemGO.GetComponent<Image>();
            img.color = (gid == currentGroupId) ? new Color(0.3f, 0.6f, 0.3f, 1f) : new Color(0.25f, 0.25f, 0.25f, 1f);

            HorizontalLayoutGroup rowLayout = itemGO.GetComponent<HorizontalLayoutGroup>();
            rowLayout.padding = new RectOffset(8, 8, 4, 4);
            rowLayout.spacing = 8;
            rowLayout.childControlWidth = false;
            rowLayout.childControlHeight = true;

            // Main clickable area for group selection/renaming
            GameObject labelBtnGO = new GameObject("LabelButton", typeof(RectTransform), typeof(Button), typeof(CustomClickDetector));
            labelBtnGO.transform.SetParent(itemGO.transform, false);
            RectTransform labelRect = labelBtnGO.GetComponent<RectTransform>();
            labelRect.sizeDelta = new Vector2(170, 40);

            Button itemBtn = labelBtnGO.GetComponent<Button>();
            itemBtn.onClick.AddListener(() => {
                HandleGroupItemClick(gid);
            });

            CustomClickDetector detector = labelBtnGO.GetComponent<CustomClickDetector>();
            detector.onRightClick = () => {
                PromptDeleteGroup(gid);
            };

            // Group Name Text
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

            // Card Count Subtext under name
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

            // SOLO Button for individual group isolation
            GameObject soloBtnGO = new GameObject("SoloButton", typeof(RectTransform), typeof(Image), typeof(Button));
            soloBtnGO.transform.SetParent(itemGO.transform, false);
            RectTransform soloRect = soloBtnGO.GetComponent<RectTransform>();
            soloRect.sizeDelta = new Vector2(65, 36);

            Image soloImg = soloBtnGO.GetComponent<Image>();
            bool isSoloed = groupSoloState.ContainsKey(gid) && groupSoloState[gid];
            soloImg.color = isSoloed ? new Color(0.9f, 0.5f, 0.1f) : new Color(0.35f, 0.35f, 0.35f);

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

            soloBtn.onClick.AddListener(() => {
                ToggleGroupSolo(gid);
            });
        }
    }

    void ToggleGroupSolo(int gid)
    {
        bool currentState = groupSoloState.ContainsKey(gid) && groupSoloState[gid];
        groupSoloState[gid] = !currentState;

        bool anySoloActive = groupSoloState.Values.Any(s => s);

        HairCard[] allCards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        foreach (var card in allCards)
        {
            var mr = card.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                if (anySoloActive)
                {
                    bool cardSoloed = groupSoloState.ContainsKey(card.groupId) && groupSoloState[card.groupId];
                    mr.enabled = cardSoloed;
                }
                else
                {
                    mr.enabled = true;
                }
            }
        }

        RefreshGroupListUI();
    }

    void HandleGroupItemClick(int gid)
    {
        float timeSinceLastClick = Time.time - lastGroupClickTime;
        if (lastClickedGroupId == gid && timeSinceLastClick < 0.4f)
        {
            PromptRenameGroup(gid);
            lastClickedGroupId = -1;
        }
        else
        {
            SelectGroup(gid);
            lastClickedGroupId = gid;
            lastGroupClickTime = Time.time;
        }
    }

    void PromptRenameGroup(int gid)
    {
#if UNITY_EDITOR
        string currentName = groupNames.ContainsKey(gid) ? groupNames[gid] : ("Group " + gid);
        string newName = EditorInputDialog.Show("Rename Group", "Enter new name for group:", currentName);
        if (!string.IsNullOrEmpty(newName))
        {
            groupNames[gid] = newName;
            RefreshGroupListUI();
        }
#endif
    }

    void PromptDeleteGroup(int gid)
    {
        bool confirm = EditorUtility.DisplayDialog("Delete Group", "Are you sure you want to delete this group and all its hair cards?", "Yes", "No");
        if (confirm)
        {
            DeleteGroupAndCards(gid);
        }
    }

    void DeleteGroupAndCards(int gid)
    {
        HairCard[] allCards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        foreach (var card in allCards)
        {
            if (card.groupId == gid)
            {
                Destroy(card.gameObject);
            }
        }

        allGroupIds.Remove(gid);
        if (groupNames.ContainsKey(gid)) groupNames.Remove(gid);
        if (groupSoloState.ContainsKey(gid)) groupSoloState.Remove(gid);

        if (currentGroupId == gid)
        {
            currentGroupId = allGroupIds.FirstOrDefault();
        }
        RefreshGroupListUI();
    }

    int GetNextAvailableGroupId()
    {
        int id = 0;
        while (allGroupIds.Contains(id))
        {
            id++;
        }
        return id;
    }

    GameObject CreateSliderUI(Transform parent, string labelText, float min, float max, float defaultValue, UnityEngine.Events.UnityAction<float> onValueChanged, out UnityEngine.UI.Slider createdSlider, float rowHeight = 50f, int fontSize = 20)
    {
        GameObject rowGO = new GameObject(labelText + "_Row", typeof(RectTransform));
        rowGO.transform.SetParent(parent, false);
        RectTransform rowRect = rowGO.GetComponent<RectTransform>();
        rowRect.sizeDelta = new Vector2(0, rowHeight);

        VerticalLayoutGroup rowLayout = rowGO.AddComponent<VerticalLayoutGroup>();
        rowLayout.spacing = -2;
        rowLayout.childControlWidth = true;
        rowLayout.childControlHeight = false;

        GameObject textGO = new GameObject(labelText + "_Text", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI));
        textGO.transform.SetParent(rowGO.transform, false);
        TMPro.TextMeshProUGUI tmp = textGO.GetComponent<TMPro.TextMeshProUGUI>();
        tmp.text = labelText + ": " + defaultValue.ToString("F3");
        tmp.fontSize = fontSize;
        tmp.color = Color.white;

        GameObject sliderGO = new GameObject(labelText + "_Slider", typeof(RectTransform), typeof(UnityEngine.UI.Slider));
        sliderGO.transform.SetParent(rowGO.transform, false);
        RectTransform sliderRect = sliderGO.GetComponent<RectTransform>();
        sliderRect.sizeDelta = new Vector2(0, 26);

        Slider slider = sliderGO.GetComponent<Slider>();
        slider.minValue = min;
        slider.maxValue = max;
        slider.value = defaultValue;

        GameObject backgroundGO = new GameObject("Background", typeof(RectTransform), typeof(Image));
        backgroundGO.transform.SetParent(sliderGO.transform, false);
        backgroundGO.GetComponent<Image>().color = new Color(0.3f, 0.3f, 0.3f);
        RectTransform bgRect = backgroundGO.GetComponent<RectTransform>();
        bgRect.anchorMin = new Vector2(0, 0.25f);
        bgRect.anchorMax = new Vector2(1, 0.75f);
        bgRect.sizeDelta = Vector2.zero;

        GameObject fillAreaGO = new GameObject("Fill Area", typeof(RectTransform));
        fillAreaGO.transform.SetParent(sliderGO.transform, false);
        RectTransform fillAreaRect = fillAreaGO.GetComponent<RectTransform>();
        fillAreaRect.anchorMin = new Vector2(0, 0.25f);
        fillAreaRect.anchorMax = new Vector2(1, 0.75f);
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
        slider.handleRect.sizeDelta = new Vector2(30, 0);

        slider.onValueChanged.AddListener((val) => {
            tmp.text = labelText + ": " + val.ToString("F3");
            onValueChanged.Invoke(val);
        });

        createdSlider = slider;
        return rowGO;
    }

    void UpdateActiveCard()
    {
        if (hasSelectionHotspot)
        {
            HairCard[] allCards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
            foreach (HairCard card in allCards)
            {
                if (card.groupId == currentGroupId && card.selectionWeight > 0f)
                {
                    card.SetParameters(currentLength, currentWidth, currentSegments, currentBend, currentTwist, currentOffsetX, currentOffsetY, currentOffsetZ, currentEmbedDepth, selectionStrength);
                }
            }
        }
        else if (lastPlacedCard != null)
        {
            lastPlacedCard.SetParameters(currentLength, currentWidth, currentSegments, currentBend, currentTwist, currentOffsetX, currentOffsetY, currentOffsetZ, currentEmbedDepth, 1f);
        }
    }

    void Update()
    {
        HandleCameraControls();
        HandleGrooming();
    }

    void OnDrawGizmos()
    {
        if (!hasSelectionHotspot) return;

        Gizmos.color = Color.yellow;
        Gizmos.DrawLine(selectionHitPoint, selectionHitPoint + (selectionHitNormal * 2.0f));
    }

    public void ToggleGroomingMode(bool state)
    {
        isGroomingMode = state;
    }

    void HandleGrooming()
    {
        if (!isGroomingMode || Mouse.current == null) return;
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
                if (allCards.Length > 0)
                {
                    HairCard nearestCard = allCards.OrderBy(c => Vector3.Distance(altHit.point, c.transform.position)).FirstOrDefault();
                    if (nearestCard != null)
                    {
                        SelectGroup(nearestCard.groupId);
                        Debug.Log("Alt-clicked: Picked group " + nearestCard.groupId);
                    }
                }
            }
            return;
        }

        if (isHoldingShift && !wasHoldingShiftDrag)
        {
            wasHoldingShiftDrag = true;
            sessionPlacedCards.Clear(); // Reset session tracking for this shift drag
        }

        if (wasHoldingShiftDrag && !isHoldingShift)
        {
            // If cards were placed during this shift drag session, prompt to group them into a new group
            if (sessionPlacedCards.Count > 0 && EditorUtility.DisplayDialog("New Group", "Do you want to create a new group for the hair cards placed during this stroke?", "Yes", "No"))
            {
                int newId = GetNextAvailableGroupId();
                allGroupIds.Add(newId);
                groupNames[newId] = "Group " + newId;

                // Move all cards placed during this stroke into the new group
                foreach (var card in sessionPlacedCards)
                {
                    if (card != null)
                    {
                        card.groupId = newId;
                    }
                }

                SelectGroup(newId);
            }
            wasHoldingShiftDrag = false;
            sessionPlacedCards.Clear();
            RefreshGroupListUI();
        }

        if (isHoldingCtrl && Mouse.current.leftButton.wasPressedThisFrame)
        {
            Ray ray = mainCamera.ScreenPointToRay(Mouse.current.position.ReadValue());
            if (Physics.Raycast(ray, out RaycastHit hit))
            {
                EnterSelectionMode(hit.point, hit.normal);
            }
            else
            {
                ClearSelectionHotspot();
            }
            return;
        }

        bool shouldSpawn = false;

        if (isHoldingShift)
        {
            if (Mouse.current.leftButton.isPressed && Time.time >= lastSpawnTime + spawnCooldown)
            {
                shouldSpawn = true;
            }
        }
        else
        {
            if (Mouse.current.leftButton.wasPressedThisFrame)
            {
                shouldSpawn = true;
            }
        }

        if (shouldSpawn && !isSelectionMode)
        {
            Ray ray = mainCamera.ScreenPointToRay(Mouse.current.position.ReadValue());
            if (Physics.Raycast(ray, out RaycastHit hit))
            {
                HairCard card = PinHairCard(hit.point, hit.normal);
                if (isHoldingShift && card != null)
                {
                    sessionPlacedCards.Add(card); // Track card placed during shift stroke
                }
                lastSpawnTime = Time.time;
            }
        }
    }

    void EnterSelectionMode(Vector3 brushCenter, Vector3 hitNormal)
    {
        if (falloffRowGO != null) Destroy(falloffRowGO);
        if (strengthRowGO != null) Destroy(strengthRowGO);

        isSelectionMode = true;
        hasSelectionHotspot = true;
        selectionStrength = 0.25f;
        selectionHitPoint = brushCenter;
        selectionHitNormal = hitNormal;

        if (activePanelImage != null)
        {
            activePanelImage.color = new Color(0.35f, 0.32f, 0.1f, 0.9f);
        }

        HairCard[] groupCards = FindObjectsByType<HairCard>(FindObjectsSortMode.None).Where(c => c.groupId == currentGroupId).ToArray();
        if (groupCards.Length > 0)
        {
            var nearestCards = groupCards
                .OrderBy(card => Vector3.Distance(brushCenter, card.transform.position))
                .Take(6)
                .ToList();

            float totalWeight = 0f;
            float avgLength = 0f, avgWidth = 0f, avgBend = 0f, avgTwist = 0f;
            int accumulatedSegments = 0;

            foreach (var card in nearestCards)
            {
                float dist = Vector3.Distance(brushCenter, card.transform.position);
                float weight = 1f / (dist + 0.0001f);

                totalWeight += weight;
                avgLength += card.length * weight;
                avgWidth += card.width * weight;
                avgBend += card.bendAngle * weight;
                avgTwist += card.twistAngle * weight;
                accumulatedSegments += card.segments;
            }

            if (totalWeight > 0f)
            {
                currentLength = avgLength / totalWeight;
                currentWidth = avgWidth / totalWeight;
                currentBend = avgBend / totalWeight;
                currentTwist = avgTwist / totalWeight;
                currentSegments = Mathf.RoundToInt((float)accumulatedSegments / nearestCards.Count);

                if (lengthSlider != null) lengthSlider.SetValueWithoutNotify(currentLength);
                if (widthSlider != null) widthSlider.SetValueWithoutNotify(currentWidth);
                if (bendSlider != null) bendSlider.SetValueWithoutNotify(currentBend);
                if (twistSlider != null) twistSlider.SetValueWithoutNotify(currentTwist);
                if (segmentsSlider != null) segmentsSlider.SetValueWithoutNotify(currentSegments);
            }
        }

        if (activeSliderPanel != null)
        {
            falloffRowGO = CreateSliderUI(activeSliderPanel.transform, "Falloff Dist", 0.001f, 0.1f, brushFalloffDistance, (val) => {
                brushFalloffDistance = val;
                RecomputeSelectionWeights(selectionHitPoint);
            }, out _, 50, 20);

            strengthRowGO = CreateSliderUI(activeSliderPanel.transform, "Strength", 0.0f, 1.0f, selectionStrength, (val) => {
                selectionStrength = val;
                UpdateActiveCard();
            }, out _, 50, 20);
        }

        RecomputeSelectionWeights(brushCenter);
    }

    void ClearSelectionHotspot()
    {
        hasSelectionHotspot = false;
        isSelectionMode = false;

        if (activePanelImage != null)
        {
            activePanelImage.color = new Color(0.15f, 0.15f, 0.15f, 0.85f);
        }

        if (falloffRowGO != null) Destroy(falloffRowGO);
        if (strengthRowGO != null) Destroy(strengthRowGO);

        HairCard[] allCards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        foreach (HairCard card in allCards)
        {
            card.SetSelectionWeight(0f);
            card.UpdateVisualHighlight();
        }
    }

    void RecomputeSelectionWeights(Vector3 brushCenter)
    {
        HairCard[] allCards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        foreach (HairCard card in allCards)
        {
            if (card.groupId != currentGroupId)
            {
                card.SetSelectionWeight(0f);
                continue;
            }

            float distance = Vector3.Distance(brushCenter, card.transform.position);
            if (distance <= brushFalloffDistance)
            {
                float weight = Mathf.Clamp01(1f - (distance / brushFalloffDistance));
                card.SetSelectionWeight(weight);
                card.CaptureBaseState(card.length, card.width, card.segments, card.bendAngle, card.twistAngle, card.GetEmbedDepth(), card.GetOffsetX(), card.GetOffsetY(), card.GetOffsetZ());
            }
            else
            {
                card.SetSelectionWeight(0f);
            }
        }
    }

    HairCard PinHairCard(Vector3 position, Vector3 normal)
    {
        GameObject cardGO = new GameObject("HairCard_Strip", typeof(MeshFilter), typeof(MeshRenderer), typeof(HairCard));
        HairCard card = cardGO.GetComponent<HairCard>();

        card.SetPlacementData(position, normal, currentEmbedDepth, currentOffsetX, currentOffsetY, currentOffsetZ, currentGroupId);
        card.SetParameters(currentLength, currentWidth, currentSegments, currentBend, currentTwist, currentOffsetX, currentOffsetY, currentOffsetZ, currentEmbedDepth);

        lastPlacedCard = card;

        MeshRenderer mr = cardGO.GetComponent<MeshRenderer>();
        if (hairCardMaterial != null)
        {
            mr.sharedMaterial = hairCardMaterial;
        }

        RefreshGroupListUI(); // Update card count UI immediately on spawn
        return card;
    }

    void HandleCameraControls()
    {
        if (Mouse.current == null) return;

        if (Mouse.current.rightButton.isPressed)
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
            mainCamera.transform.Translate(Vector3.forward * (scroll * 0.001f) * zoomSpeed, Space.Self);
        }
    }

    public void SaveProject()
    {
#if UNITY_EDITOR
        string path = EditorUtility.SaveFilePanel("Save Hair Project", "", "HairProject", "json");
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

        foreach (int id in allGroupIds)
        {
            GroupSaveData gData = new GroupSaveData();
            gData.groupId = id;
            gData.groupName = groupNames.ContainsKey(id) ? groupNames[id] : ("Group " + id);
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
            cardData.groupId = card.groupId;

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
            }
        }

        HairCard[] oldCards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        foreach (var card in oldCards)
        {
            Destroy(card.gameObject);
        }

        currentLength = saveData.sliderLength;
        currentWidth = saveData.sliderWidth;
        currentSegments = saveData.sliderSegments;
        currentBend = saveData.sliderBend;
        currentTwist = saveData.sliderTwist;
        currentEmbedDepth = saveData.sliderEmbedDepth;
        currentOffsetX = saveData.sliderOffsetX;
        currentOffsetY = saveData.sliderOffsetY;
        currentOffsetZ = saveData.sliderOffsetZ;

        allGroupIds.Clear();
        groupNames.Clear();
        foreach (var g in saveData.groups)
        {
            allGroupIds.Add(g.groupId);
            groupNames[g.groupId] = g.groupName;
        }

        foreach (var cData in saveData.hairCards)
        {
            GameObject cardGO = new GameObject("HairCard_Strip", typeof(MeshFilter), typeof(MeshRenderer), typeof(HairCard));
            HairCard card = cardGO.GetComponent<HairCard>();

            card.transform.position = new Vector3(cData.posX, cData.posY, cData.posZ);
            card.transform.rotation = new Quaternion(cData.rotX, cData.rotY, cData.rotZ, cData.rotW);
            
            card.groupId = cData.groupId;
            card.SetParameters(cData.length, cData.width, cData.segments, cData.bendAngle, cData.twistAngle, cData.offsetX, cData.offsetY, cData.offsetZ, cData.embedDepth, 1f);

            MeshRenderer mr = cardGO.GetComponent<MeshRenderer>();
            if (hairCardMaterial != null)
            {
                mr.sharedMaterial = hairCardMaterial;
            }
        }

        if (uiContainer != null) uiContainer.SetActive(false);
        OnModelLoaded();
        if (activeSliderPanel == null) BuildRuntimeGroomingUI();
        BuildGroupManagementUI();
        isGroomingMode = true;
        Debug.Log("Project loaded successfully from: " + path);
#endif
    }
}

public class CustomClickDetector : MonoBehaviour, IPointerClickHandler
{
    public System.Action onRightClick;

    public void OnPointerClick(PointerEventData eventData)
    {
        if (eventData.button == PointerEventData.InputButton.Right)
        {
            onRightClick?.Invoke();
        }
    }
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
        if (GUILayout.Button("OK"))
        {
            Close();
        }
        if (GUILayout.Button("Cancel"))
        {
            inputString = "";
            Close();
        }
        EditorGUILayout.EndHorizontal();
    }
}
#endif