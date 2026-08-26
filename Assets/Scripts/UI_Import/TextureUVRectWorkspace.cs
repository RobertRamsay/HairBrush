using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// Turns Texture Editor into a dedicated 2D UV authoring workspace.
// Entering the workspace clears transient groom/POST selection, hides the group panel,
// locks the main camera to a front-on orthographic texture view, and lets the user draw
// normalized UV rectangles directly over the texture preview. Rectangle definitions are
// project data; cards/groups will consume them in the later UV-assignment layer.
[DefaultExecutionOrder(9200)]
public class TextureUVRectWorkspace : MonoBehaviour
{
    private readonly List<UVRectSaveData> rectangles = new();
    private readonly Dictionary<int, LineRenderer> rectangleLines = new();
    private readonly Dictionary<int, TextMeshPro> rectangleLabels = new();
    private readonly Dictionary<int, UVRectSummaryRow> summaryRows = new();

    private ModelViewer viewer;
    private GameObject texturePanel;
    private GameObject previewPlane;
    private GameObject groupPanel;
    private bool groupPanelWasActive;

    private GameObject section;
    private TextMeshProUGUI summaryText;
    private Transform summaryListRoot;
    private bool summaryListDirty;
    private TextMeshProUGUI drawButtonText;
    private Image drawButtonImage;

    private GameObject visualRoot;
    private LineRenderer draftLine;
    private LineRenderer planeOutlineLine;
    private Material lineMaterial;
    private Material outlineMaterial;
    private Material hoverLineMaterial;
    private int hoveredRectId = -1;

    private bool wasActive;
    private bool drawMode;
    private bool dragging;
    private Vector2 dragStartUV;
    private Vector2 dragCurrentUV;
    private int nextRectId = 1;

    private bool cameraCaptured;
    private Vector3 savedPivotPosition;
    private Quaternion savedPivotRotation;
    private Vector3 savedCameraPosition;
    private Quaternion savedCameraRotation;
    private bool savedOrthographic;
    private float savedOrthographicSize;
    private float savedFieldOfView;

    private FieldInfo hasSelectionField;
    private FieldInfo isSelectionModeField;
    private FieldInfo selectionHitPointField;
    private FieldInfo selectionHitNormalField;
    private FieldInfo loadedModelField;
    private GameObject lastLoadedModel;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<TextureUVRectWorkspace>() != null) return;
        GameObject go = new GameObject("TextureUVRectWorkspace");
        DontDestroyOnLoad(go);
        go.AddComponent<TextureUVRectWorkspace>();
    }

    void Update()
    {
        Resolve();
        HandleModelLifecycle();
        RestorePendingProjectData();

        bool active = texturePanel != null && texturePanel.activeInHierarchy;
        if (active && !wasActive) EnterWorkspace();
        else if (!active && wasActive) ExitWorkspace();
        wasActive = active;

        if (!active) return;

        ResolvePreviewPlane();
        EnsureWorkspaceUI();
        PreparePreviewPlane();
        EnforceWorkspaceCamera();
        EnsureVisualRoot();
        visualRoot.SetActive(true);
        UpdateOutlineVisual();
        HandleDrawInput();
        UpdateTextureHoverDetection();
        HandleRightClickDelete();
        UpdateHoverPulse();
        UpdateSummary();

        // Deferred from ReorderRectangle - see its comment for why the row list can't safely
        // be rebuilt in the same frame as the drop event that triggered the reorder.
        if (summaryListDirty)
        {
            summaryListDirty = false;
            RebuildSummaryList();
        }
    }

    void LateUpdate()
    {
        if (wasActive)
            EnforceWorkspaceCamera();
    }

    void OnDestroy()
    {
        if (wasActive) ExitWorkspace();
        if (lineMaterial != null) Destroy(lineMaterial);
        if (outlineMaterial != null) Destroy(outlineMaterial);
        if (hoverLineMaterial != null) Destroy(hoverLineMaterial);
    }

    void Resolve()
    {
        if (viewer == null)
        {
            viewer = FindFirstObjectByType<ModelViewer>();
            if (viewer != null)
            {
                BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                Type type = typeof(ModelViewer);
                hasSelectionField = type.GetField("hasSelectionHotspot", flags);
                isSelectionModeField = type.GetField("isSelectionMode", flags);
                selectionHitPointField = type.GetField("selectionHitPoint", flags);
                selectionHitNormalField = type.GetField("selectionHitNormal", flags);
                loadedModelField = type.GetField("loadedModel", flags);
                lastLoadedModel = loadedModelField?.GetValue(viewer) as GameObject;
            }
        }

        if (texturePanel == null)
            texturePanel = FindInactiveGameObject("TextureEditorPanel");

        if (groupPanel == null)
            groupPanel = FindInactiveGameObject("GroupManagerPanel");
    }

    void HandleModelLifecycle()
    {
        if (viewer == null || loadedModelField == null) return;
        GameObject current = loadedModelField.GetValue(viewer) as GameObject;
        if (current == lastLoadedModel) return;

        bool projectRestorePending = HairProjectSaveData.PendingUVRectRestore != null;
        lastLoadedModel = current;

        // A new OBJ is a brand-new session. A project load also replaces the model, but
        // its UV rectangles are already waiting in PendingUVRectRestore and must survive.
        if (current != null && !projectRestorePending)
            ClearDefinitions();
    }

    void ResolvePreviewPlane()
    {
        if (previewPlane == null)
            previewPlane = FindInactiveGameObject("HairTexturePreviewPlane");
    }

    static GameObject FindInactiveGameObject(string objectName)
    {
        Transform found = FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .FirstOrDefault(t => t != null && t.name == objectName);
        return found != null ? found.gameObject : null;
    }

    void EnterWorkspace()
    {
        DeselectGroomContext();

        groupPanel = FindInactiveGameObject("GroupManagerPanel");
        if (groupPanel != null)
        {
            groupPanelWasActive = groupPanel.activeSelf;
            groupPanel.SetActive(false);
        }

        CaptureCamera();
        ResolvePreviewPlane();
        PreparePreviewPlane();
        EnforceWorkspaceCamera();
        EnsureVisualRoot();
        RefreshRectangleVisuals();
        RefreshSummary();
    }

    void ExitWorkspace()
    {
        drawMode = false;
        dragging = false;
        UpdateDrawButton();
        HideDraft();
        if (visualRoot != null) visualRoot.SetActive(false);

        if (groupPanel != null && groupPanelWasActive)
            groupPanel.SetActive(true);

        RestoreCamera();
    }

    void DeselectGroomContext()
    {
        if (viewer == null) return;

        hasSelectionField?.SetValue(viewer, false);
        isSelectionModeField?.SetValue(viewer, false);
        selectionHitPointField?.SetValue(viewer, Vector3.zero);
        selectionHitNormalField?.SetValue(viewer, Vector3.zero);
        viewer.lastPlacedCard = null;

        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
            if (card != null) card.SetSelectionWeight(0f);

        // A selected GUIDE is groom context too, and this is the one that was being left behind.
        // Its handle rings draw with ZTest Always now, so unlike everything else in the viewport
        // they are not hidden by the opaque texture preview plane this workspace parks the camera
        // in front of - a guide left selected on the way in would float its points over the UV
        // workspace. It also holds GroomingInputLock for as long as it stays selected, which is
        // card placement switched off waiting for you back in groom mode.
        GuideCurveManager guides = FindFirstObjectByType<GuideCurveManager>();
        if (guides != null) guides.ClearSelection();

        if (EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(null);
    }

    void CaptureCamera()
    {
        if (viewer == null || viewer.mainCamera == null || cameraCaptured) return;

        if (viewer.cameraPivot != null)
        {
            savedPivotPosition = viewer.cameraPivot.position;
            savedPivotRotation = viewer.cameraPivot.rotation;
        }

        Camera camera = viewer.mainCamera;
        savedCameraPosition = camera.transform.position;
        savedCameraRotation = camera.transform.rotation;
        savedOrthographic = camera.orthographic;
        savedOrthographicSize = camera.orthographicSize;
        savedFieldOfView = camera.fieldOfView;
        cameraCaptured = true;
    }

    void RestoreCamera()
    {
        if (!cameraCaptured || viewer == null || viewer.mainCamera == null) return;

        if (viewer.cameraPivot != null)
        {
            viewer.cameraPivot.position = savedPivotPosition;
            viewer.cameraPivot.rotation = savedPivotRotation;
        }

        Camera camera = viewer.mainCamera;
        camera.transform.position = savedCameraPosition;
        camera.transform.rotation = savedCameraRotation;
        camera.orthographic = savedOrthographic;
        camera.orthographicSize = savedOrthographicSize;
        camera.fieldOfView = savedFieldOfView;
        cameraCaptured = false;
    }

    void PreparePreviewPlane()
    {
        if (previewPlane == null) return;

        previewPlane.transform.rotation = Quaternion.identity;

        float textureAspect = 1f;
        MeshRenderer renderer = previewPlane.GetComponent<MeshRenderer>();
        Material material = renderer != null ? renderer.sharedMaterial : null;
        Texture texture = null;
        if (material != null)
        {
            if (material.HasProperty("_BaseMap")) texture = material.GetTexture("_BaseMap");
            if (texture == null) texture = material.mainTexture;
        }
        if (texture != null && texture.height > 0)
            textureAspect = Mathf.Clamp((float)texture.width / texture.height, .2f, 5f);

        float height = 1.6f;
        float width = height * textureAspect;
        if (width > 2.15f)
        {
            width = 2.15f;
            height = width / textureAspect;
        }

        previewPlane.transform.localScale = new Vector3(width, height, 1f);
        previewPlane.SetActive(true);

        Collider collider = previewPlane.GetComponent<Collider>();
        if (collider != null) collider.enabled = true;
    }

    void EnforceWorkspaceCamera()
    {
        if (viewer == null || viewer.mainCamera == null || previewPlane == null) return;

        Camera camera = viewer.mainCamera;
        float screenAspect = Screen.height > 0 ? Mathf.Max(.5f, (float)Screen.width / Screen.height) : 1.777f;
        Vector3 planeScale = previewPlane.transform.lossyScale;
        float sizeForHeight = planeScale.y / (2f * .82f);
        float sizeForWidth = planeScale.x / (2f * screenAspect * .62f);
        float orthoSize = Mathf.Max(.62f, Mathf.Max(sizeForHeight, sizeForWidth));

        if (viewer.cameraPivot != null)
        {
            viewer.cameraPivot.position = Vector3.zero;
            viewer.cameraPivot.rotation = Quaternion.identity;
        }

        camera.orthographic = true;
        camera.orthographicSize = orthoSize;
        camera.transform.position = new Vector3(0f, 0f, -3f);
        camera.transform.rotation = Quaternion.identity;
    }

    void EnsureWorkspaceUI()
    {
        if (texturePanel == null) return;
        if (section != null && section.transform.parent == texturePanel.transform) return;

        // A hot script-reload while staying in Play mode resets this MonoBehaviour's own
        // fields (section becomes null again) but leaves the scene's GameObjects untouched, so
        // without this the panel would just keep reusing whatever dimensions/wiring it was
        // built with on the FIRST run of this method - stale layout values (heights, spacing,
        // even entire new features) would never actually show up until a full Stop/Start of
        // Play mode. Destroying and falling through to a full rebuild below fixes that for good.
        Transform existing = texturePanel.transform.Find("UVWorkspaceSection");
        if (existing != null) Destroy(existing.gameObject);

        section = new GameObject("UVWorkspaceSection", typeof(RectTransform), typeof(LayoutElement), typeof(VerticalLayoutGroup));
        section.transform.SetParent(texturePanel.transform, false);
        LayoutElement sectionLayout = section.GetComponent<LayoutElement>();
        sectionLayout.preferredHeight = 781f;
        sectionLayout.minHeight = 781f;
        VerticalLayoutGroup vertical = section.GetComponent<VerticalLayoutGroup>();
        vertical.spacing = 4f;
        vertical.padding = new RectOffset(0, 0, 2, 2);
        vertical.childControlWidth = true;
        vertical.childControlHeight = false;

        TextMeshProUGUI title = AddText(section.transform, "UV RECT WORKSPACE", 15f, 20f, FontStyles.Bold);
        title.alignment = TextAlignmentOptions.MidlineLeft;

        TextMeshProUGUI instructions = AddText(section.transform,
            "Draw boxes over the texture. Rect IDs become the future predetermined UV-card choices.", 11f, 32f, FontStyles.Normal);
        instructions.alignment = TextAlignmentOptions.TopLeft;
        instructions.textWrappingMode = TextWrappingModes.Normal;

        GameObject buttons = new GameObject("Buttons", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(LayoutElement));
        buttons.transform.SetParent(section.transform, false);
        LayoutElement buttonsLayout = buttons.GetComponent<LayoutElement>();
        buttonsLayout.preferredHeight = 75f;
        buttonsLayout.minHeight = 75f;
        // section's own VerticalLayoutGroup has childControlHeight=false, so it never actually
        // resizes this RectTransform itself - it only reads LayoutElement.preferredHeight to
        // position whatever comes after it. Without this, "Buttons" renders at Unity's raw
        // default RectTransform size (100) rather than the 75 the LayoutElement declares.
        buttons.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 75f);
        VerticalLayoutGroup buttonsGroup = buttons.GetComponent<VerticalLayoutGroup>();
        buttonsGroup.spacing = 6f;
        buttonsGroup.childControlWidth = true;
        buttonsGroup.childForceExpandWidth = true;
        buttonsGroup.childControlHeight = true;
        buttonsGroup.childForceExpandHeight = false;

        GameObject row1 = AddButtonRow(buttons.transform, "Row1");
        GameObject drawButton = AddButton(row1.transform, "DRAW UV RECT", ToggleDrawMode);
        drawButtonImage = drawButton.GetComponent<Image>();
        drawButtonText = drawButton.GetComponentInChildren<TextMeshProUGUI>(true);
        AddButton(row1.transform, "UNDO LAST", UndoLastRectangle);

        GameObject row2 = AddButtonRow(buttons.transform, "Row2");
        AddButton(row2.transform, "CLEAR", ClearDefinitions);

        GameObject listSpacer = new GameObject("ListSpacer", typeof(RectTransform), typeof(LayoutElement));
        listSpacer.transform.SetParent(section.transform, false);
        listSpacer.GetComponent<LayoutElement>().preferredHeight = 10f;
        // Same reason as "Buttons" above.
        listSpacer.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 10f);

        summaryText = AddText(section.transform, "SummaryHeader", 12.5f, 20f, FontStyles.Normal);
        summaryText.gameObject.name = "SummaryHeader";
        summaryText.alignment = TextAlignmentOptions.TopLeft;
        summaryText.textWrappingMode = TextWrappingModes.Normal;

        summaryListRoot = BuildSummaryScrollList(section.transform);

        int tabIndex = texturePanel.transform.Find("PanelTabRow") != null
            ? texturePanel.transform.Find("PanelTabRow").GetSiblingIndex() + 1
            : 0;
        section.transform.SetSiblingIndex(tabIndex);

        UpdateDrawButton();
        RefreshSummary();
    }

    TextMeshProUGUI AddText(Transform parent, string text, float fontSize, float height, FontStyles style)
    {
        GameObject go = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, height);
        TextMeshProUGUI tmp = go.GetComponent<TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = fontSize;
        tmp.fontStyle = style;
        tmp.color = Color.white;
        tmp.raycastTarget = false;
        return tmp;
    }

    // Scrollable viewport so the list keeps working once AUTO produces more rectangles than
    // fit in the fixed-height panel - a plain stacked list would just overflow past its box.
    // A visible scrollbar matters here specifically because a partially-cut-off last row at
    // the bottom of the box otherwise reads as a clipping bug rather than "scroll for more."
    Transform BuildSummaryScrollList(Transform parent)
    {
        GameObject scrollGO = new GameObject("SummaryList", typeof(RectTransform), typeof(Image), typeof(ScrollRect), typeof(RectMask2D), typeof(LayoutElement));
        scrollGO.transform.SetParent(parent, false);
        LayoutElement scrollLayout = scrollGO.GetComponent<LayoutElement>();
        scrollLayout.preferredHeight = 600f;
        scrollLayout.minHeight = 600f;
        // section's own VerticalLayoutGroup has childControlHeight=false, so LayoutElement alone
        // never actually resizes this RectTransform - without this it renders at Unity's raw
        // default (100) instead of the 600 declared above. Same fix as "Buttons"/"ListSpacer".
        scrollGO.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 600f);
        Image scrollBackground = scrollGO.GetComponent<Image>();
        scrollBackground.color = Color.clear;
        scrollBackground.raycastTarget = true;
        // A hard mask edge made the last visible row look chopped off mid-line; a soft vertical
        // fade reads as "content continues here" instead.
        RectMask2D scrollMask = scrollGO.GetComponent<RectMask2D>();
        scrollMask.softness = new Vector2Int(0, 27);

        GameObject contentGO = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
        contentGO.transform.SetParent(scrollGO.transform, false);
        RectTransform contentRect = contentGO.GetComponent<RectTransform>();
        contentRect.anchorMin = new Vector2(0f, 1f);
        contentRect.anchorMax = new Vector2(1f, 1f);
        contentRect.pivot = new Vector2(.5f, 1f);
        contentRect.anchoredPosition = Vector2.zero;
        // 12px narrower than full width - reserves a gutter on the right for the scrollbar
        // below so it doesn't sit on top of the rows.
        contentRect.sizeDelta = new Vector2(-12f, 0f);
        VerticalLayoutGroup contentLayout = contentGO.GetComponent<VerticalLayoutGroup>();
        contentLayout.spacing = 4f;
        contentLayout.padding = new RectOffset(2, 2, 2, 2);
        contentLayout.childControlWidth = true;
        contentLayout.childForceExpandWidth = true;
        contentLayout.childControlHeight = true;
        ContentSizeFitter fitter = contentGO.GetComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

        GameObject scrollbarGO = new GameObject("Scrollbar", typeof(RectTransform), typeof(Image), typeof(Scrollbar));
        scrollbarGO.transform.SetParent(scrollGO.transform, false);
        RectTransform scrollbarRect = scrollbarGO.GetComponent<RectTransform>();
        scrollbarRect.anchorMin = new Vector2(1f, 0f);
        scrollbarRect.anchorMax = new Vector2(1f, 1f);
        scrollbarRect.pivot = new Vector2(1f, .5f);
        scrollbarRect.sizeDelta = new Vector2(8f, 0f);
        scrollbarRect.anchoredPosition = Vector2.zero;
        Image scrollbarBackground = scrollbarGO.GetComponent<Image>();
        scrollbarBackground.color = new Color(1f, 1f, 1f, .06f);

        GameObject handleGO = new GameObject("Handle", typeof(RectTransform), typeof(Image));
        handleGO.transform.SetParent(scrollbarGO.transform, false);
        RectTransform handleRect = handleGO.GetComponent<RectTransform>();
        handleRect.anchorMin = Vector2.zero;
        handleRect.anchorMax = Vector2.one;
        handleRect.sizeDelta = Vector2.zero;
        Image handleImage = handleGO.GetComponent<Image>();
        handleImage.color = new Color(.45f, .85f, .92f, .85f);

        Scrollbar scrollbar = scrollbarGO.GetComponent<Scrollbar>();
        scrollbar.direction = Scrollbar.Direction.BottomToTop;
        scrollbar.handleRect = handleRect;
        scrollbar.targetGraphic = handleImage;

        ScrollRect scroll = scrollGO.GetComponent<ScrollRect>();
        scroll.content = contentRect;
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 18f;
        scroll.verticalScrollbar = scrollbar;
        scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;

        return contentGO.transform;
    }

    GameObject AddButtonRow(Transform parent, string rowName)
    {
        GameObject row = new GameObject(rowName, typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        row.transform.SetParent(parent, false);
        row.GetComponent<LayoutElement>().preferredHeight = 34.5f;
        HorizontalLayoutGroup layout = row.GetComponent<HorizontalLayoutGroup>();
        layout.spacing = 8f;
        layout.childControlWidth = true;
        layout.childForceExpandWidth = true;
        layout.childControlHeight = true;
        return row;
    }

    GameObject AddButton(Transform parent, string label, UnityEngine.Events.UnityAction action)
    {
        GameObject go = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 32.2f);
        Image image = go.GetComponent<Image>();
        image.color = new Color(.20f, .25f, .32f, 1f);
        Button button = go.GetComponent<Button>();
        button.onClick.AddListener(action);

        GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textGO.transform.SetParent(go.transform, false);
        RectTransform rect = textGO.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        TextMeshProUGUI text = textGO.GetComponent<TextMeshProUGUI>();
        text.text = label;
        text.fontSize = 12.5f;
        text.fontStyle = FontStyles.Bold;
        text.alignment = TextAlignmentOptions.Center;
        text.color = Color.white;
        text.raycastTarget = false;
        return go;
    }

    void ToggleDrawMode()
    {
        drawMode = !drawMode;
        dragging = false;
        HideDraft();
        UpdateDrawButton();
    }

    void UpdateDrawButton()
    {
        if (drawButtonText != null)
            drawButtonText.text = drawMode ? "DRAWING: ON" : "DRAW UV RECT";
        if (drawButtonImage != null)
            drawButtonImage.color = drawMode ? new Color(.20f, .50f, .80f, 1f) : new Color(.20f, .25f, .32f, 1f);
    }

    void HandleDrawInput()
    {
        if (!drawMode || Mouse.current == null || previewPlane == null || viewer == null || viewer.mainCamera == null) return;

        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            drawMode = false;
            dragging = false;
            HideDraft();
            UpdateDrawButton();
            return;
        }

        bool pointerOverUI = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();

        // ALT is reserved, so an ALT+LMB press must not draw a rectangle - in either mode.
        //
        // Note what this is NOT about: the camera does not actually move in here. HandleCameraControls
        // has no texture-mode gate and does run, but EnforceWorkspaceCamera in this file's own
        // LateUpdate hard-resets the pivot and the camera transform every frame, and LateUpdate
        // always follows Update - so any motion is erased in the frame it happened. This guard is
        // about the CLICK, not the camera: under MAYA-NAV the user reaching for a tumble gets no
        // view change either way, and without this they would also get a UV rectangle dragged out
        // and committed on release.
        //
        // A drag ALREADY UNDER WAY is left alone rather than cancelled. Tested as a plain return
        // it only refuses to START one, so brushing ALT part-way through drawing a rectangle no
        // longer throws the rectangle away - which it would have done in both modes, and which is
        // neither of the things ALT is supposed to do.
        if (MayaNavigationAuthority.AltReserved && !dragging) return;

        if (!dragging && Mouse.current.leftButton.wasPressedThisFrame)
        {
            if (pointerOverUI) return;
            if (!TryGetPlaneUV(out Vector2 uv)) return;
            dragging = true;
            dragStartUV = uv;
            dragCurrentUV = uv;
            EnsureDraftLine();
            UpdateLine(draftLine, RectFromPoints(dragStartUV, dragCurrentUV));
            return;
        }

        if (!dragging) return;

        if (Mouse.current.leftButton.isPressed && TryGetPlaneUV(out Vector2 current))
        {
            dragCurrentUV = current;
            UpdateLine(draftLine, RectFromPoints(dragStartUV, dragCurrentUV));
        }

        if (Mouse.current.leftButton.wasReleasedThisFrame)
        {
            dragging = false;
            Rect rect = RectFromPoints(dragStartUV, dragCurrentUV);
            HideDraft();
            if (rect.width < .01f || rect.height < .01f) return;

            UVRectSaveData saved = new UVRectSaveData
            {
                id = nextRectId++,
                uMin = rect.xMin,
                vMin = rect.yMin,
                uMax = rect.xMax,
                vMax = rect.yMax
            };
            rectangles.Add(saved);
            CreateOrUpdateRectangleVisual(saved);
            RefreshSummary();
        }
    }

    bool TryGetPlaneUV(out Vector2 uv)
    {
        uv = Vector2.zero;
        Ray ray = viewer.mainCamera.ScreenPointToRay(Mouse.current.position.ReadValue());
        RaycastHit[] hits = Physics.RaycastAll(ray, Mathf.Infinity, ~0, QueryTriggerInteraction.Ignore);
        RaycastHit? planeHit = null;
        float closest = float.MaxValue;
        foreach (RaycastHit hit in hits)
        {
            if (hit.collider == null || hit.collider.gameObject != previewPlane) continue;
            if (hit.distance >= closest) continue;
            closest = hit.distance;
            planeHit = hit;
        }
        if (!planeHit.HasValue) return false;

        Vector3 local = previewPlane.transform.InverseTransformPoint(planeHit.Value.point);
        uv = new Vector2(Mathf.Clamp01(local.x + .5f), Mathf.Clamp01(local.y + .5f));
        return true;
    }

    static Rect RectFromPoints(Vector2 a, Vector2 b)
    {
        float xMin = Mathf.Min(a.x, b.x);
        float xMax = Mathf.Max(a.x, b.x);
        float yMin = Mathf.Min(a.y, b.y);
        float yMax = Mathf.Max(a.y, b.y);
        return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
    }

    // Runs every frame the workspace is active (independent of draw mode) so pointing at an
    // already-drawn rectangle on the texture highlights it, the same way hovering its row does.
    void UpdateTextureHoverDetection()
    {
        if (dragging || previewPlane == null || viewer == null || viewer.mainCamera == null || Mouse.current == null)
        {
            if (hoveredRectId >= 0) ClearHoveredRect(hoveredRectId);
            return;
        }
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
        {
            if (hoveredRectId >= 0) ClearHoveredRect(hoveredRectId);
            return;
        }
        if (!TryGetPlaneUV(out Vector2 uv))
        {
            if (hoveredRectId >= 0) ClearHoveredRect(hoveredRectId);
            return;
        }

        int found = -1;
        // Checked back-to-front so overlapping rectangles favor whichever was drawn most
        // recently - the one actually visible "on top" - matching where the person is pointing.
        for (int i = rectangles.Count - 1; i >= 0; i--)
        {
            UVRectSaveData r = rectangles[i];
            if (r == null) continue;
            if (uv.x >= r.uMin && uv.x <= r.uMax && uv.y >= r.vMin && uv.y <= r.vMax)
            {
                found = r.id;
                break;
            }
        }

        if (found >= 0) SetHoveredRect(found);
        else if (hoveredRectId >= 0) ClearHoveredRect(hoveredRectId);
    }

    // Right click on a rectangle in the texture view deletes it.
    //
    // Deliberately NOT inside HandleDrawInput. That method returns as soon as draw mode is off,
    // and the whole reason to delete a rectangle is to clear room for one you are about to draw
    // by hand - most often straight after AUTO has filled the texture with its own guesses, when
    // draw mode has never been switched on at all.
    //
    // hoveredRectId is the hit test. UpdateTextureHoverDetection ran immediately before this in
    // Update and already did the raycast, the UI check and the topmost-wins containment walk, so
    // re-deriving any of it here would only give a second answer that could disagree with the
    // rectangle the person can see flashing under their cursor.
    void HandleRightClickDelete()
    {
        if (dragging || Mouse.current == null) return;

        // ALT is reserved, so an ALT+RMB press must not delete a rectangle. Under MAYA-NAV that
        // press is someone reaching for the dolly; the rectangle under the cursor would go, with
        // nothing on screen to say so.
        //
        // As with the guard in HandleDrawInput, this is about the click rather than the camera -
        // EnforceWorkspaceCamera pins the view every LateUpdate, so nothing moves in here anyway.
        // It has to be repeated here because this method is deliberately called from Update
        // separately, so the guard in HandleDrawInput does not cover it.
        if (MayaNavigationAuthority.AltReserved) return;

        if (!Mouse.current.rightButton.wasPressedThisFrame) return;
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;
        if (hoveredRectId < 0) return;

        RemoveRectangle(hoveredRectId, false);
    }

    void EnsureHoverLineMaterial()
    {
        if (hoverLineMaterial != null) return;
        Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
        if (shader == null) return;
        hoverLineMaterial = new Material(shader) { name = "TextureUVRectHoverLineMaterial" };
    }

    // Gentle pulse rather than a flat highlight colour, so "flash" reads as an active cue you
    // can spot at a glance rather than just another static outline colour among the others.
    void UpdateHoverPulse()
    {
        if (hoveredRectId < 0 || hoverLineMaterial == null) return;
        float pulse = .5f + .5f * Mathf.Sin(Time.unscaledTime * 7f);
        Color color = Color.Lerp(new Color(1f, .1f, .8f, 1f), Color.white, pulse);
        if (hoverLineMaterial.HasProperty("_BaseColor")) hoverLineMaterial.SetColor("_BaseColor", color);
        if (hoverLineMaterial.HasProperty("_Color")) hoverLineMaterial.SetColor("_Color", color);
    }

    // Shared entry point for both hover directions: UpdateTextureHoverDetection calls this when
    // the mouse is over a drawn rectangle, and UVRectSummaryRow calls this when its row is
    // hovered - either source drives the same highlight state, so they can never fight.
    public void SetHoveredRect(int id)
    {
        if (hoveredRectId == id) return;
        ClearHoverVisual();
        hoveredRectId = id;
        ApplyHoverVisual(id);
    }

    public void ClearHoveredRect(int id)
    {
        if (hoveredRectId != id) return;
        ClearHoverVisual();
        hoveredRectId = -1;
    }

    void ApplyHoverVisual(int id)
    {
        EnsureHoverLineMaterial();
        if (rectangleLines.TryGetValue(id, out LineRenderer line) && line != null)
        {
            if (hoverLineMaterial != null) line.material = hoverLineMaterial;
            // Noticeably thicker than the normal .006f outline width - a colour pulse alone was
            // too easy to miss next to the plane outline's own similarly bright amber colour.
            line.startWidth = .016f;
            line.endWidth = .016f;
        }
        if (summaryRows.TryGetValue(id, out UVRectSummaryRow row) && row != null)
            row.SetExternalHighlight(true);
    }

    void ClearHoverVisual()
    {
        if (hoveredRectId < 0) return;
        if (rectangleLines.TryGetValue(hoveredRectId, out LineRenderer line) && line != null)
        {
            if (lineMaterial != null) line.material = lineMaterial;
            line.startWidth = .006f;
            line.endWidth = .006f;
        }
        if (summaryRows.TryGetValue(hoveredRectId, out UVRectSummaryRow row) && row != null)
            row.SetExternalHighlight(false);
    }

    void EnsureVisualRoot()
    {
        if (visualRoot != null) return;
        visualRoot = new GameObject("TextureUVRectVisuals");
        DontDestroyOnLoad(visualRoot);
    }

    void EnsureLineMaterial()
    {
        if (lineMaterial != null) return;
        Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
        if (shader == null) return;
        lineMaterial = new Material(shader) { name = "TextureUVRectLineMaterial" };
        Color color = new Color(.18f, .90f, 1f, 1f);
        if (lineMaterial.HasProperty("_BaseColor")) lineMaterial.SetColor("_BaseColor", color);
        if (lineMaterial.HasProperty("_Color")) lineMaterial.SetColor("_Color", color);
    }

    // Distinct from the cyan rectangle outlines so the plane's own edges never get mistaken
    // for a drawn UV rect.
    void EnsureOutlineMaterial()
    {
        if (outlineMaterial != null) return;
        Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
        if (shader == null) return;
        outlineMaterial = new Material(shader) { name = "TextureUVRectPlaneOutlineMaterial" };
        Color color = new Color(1f, .78f, .28f, .9f);
        if (outlineMaterial.HasProperty("_BaseColor")) outlineMaterial.SetColor("_BaseColor", color);
        if (outlineMaterial.HasProperty("_Color")) outlineMaterial.SetColor("_Color", color);
    }

    // Traces the full 0..1 UV bounds of the preview plane so the texture's actual edges are
    // always visible, independent of any drawn rectangles. Re-derived from UVToWorld every call
    // (not just on creation) so it stays glued to the plane after TextureWorkspacePolishFix's
    // centring pass, the same way CreateOrUpdateRectangleVisual stays glued for drawn rects.
    void UpdateOutlineVisual()
    {
        if (previewPlane == null) return;
        EnsureVisualRoot();
        EnsureOutlineMaterial();

        if (planeOutlineLine == null)
        {
            GameObject go = new GameObject("TexturePlaneOutline");
            go.transform.SetParent(visualRoot.transform, false);
            planeOutlineLine = go.AddComponent<LineRenderer>();
            planeOutlineLine.useWorldSpace = true;
            planeOutlineLine.positionCount = 5;
            planeOutlineLine.loop = false;
            planeOutlineLine.numCapVertices = 2;
            planeOutlineLine.startWidth = .008f;
            planeOutlineLine.endWidth = .008f;
        }
        if (outlineMaterial != null) planeOutlineLine.material = outlineMaterial;

        planeOutlineLine.gameObject.SetActive(true);
        planeOutlineLine.SetPosition(0, UVToWorld(new Vector2(0f, 0f)));
        planeOutlineLine.SetPosition(1, UVToWorld(new Vector2(1f, 0f)));
        planeOutlineLine.SetPosition(2, UVToWorld(new Vector2(1f, 1f)));
        planeOutlineLine.SetPosition(3, UVToWorld(new Vector2(0f, 1f)));
        planeOutlineLine.SetPosition(4, UVToWorld(new Vector2(0f, 0f)));
    }

    void EnsureDraftLine()
    {
        EnsureVisualRoot();
        EnsureLineMaterial();
        if (draftLine != null)
        {
            draftLine.gameObject.SetActive(true);
            return;
        }

        GameObject go = new GameObject("UVRectDraft");
        go.transform.SetParent(visualRoot.transform, false);
        draftLine = ConfigureLine(go.AddComponent<LineRenderer>());
    }

    LineRenderer ConfigureLine(LineRenderer line)
    {
        EnsureLineMaterial();
        line.useWorldSpace = true;
        line.positionCount = 5;
        line.loop = false;
        line.numCapVertices = 2;
        line.startWidth = .006f;
        line.endWidth = .006f;
        if (lineMaterial != null) line.material = lineMaterial;
        return line;
    }

    void HideDraft()
    {
        if (draftLine != null) draftLine.gameObject.SetActive(false);
    }

    void CreateOrUpdateRectangleVisual(UVRectSaveData data)
    {
        if (data == null || previewPlane == null) return;
        EnsureVisualRoot();

        if (!rectangleLines.TryGetValue(data.id, out LineRenderer line) || line == null)
        {
            GameObject go = new GameObject("UVRect_" + data.id);
            go.transform.SetParent(visualRoot.transform, false);
            line = ConfigureLine(go.AddComponent<LineRenderer>());
            rectangleLines[data.id] = line;
        }

        line.gameObject.SetActive(true);
        Rect rect = Rect.MinMaxRect(data.uMin, data.vMin, data.uMax, data.vMax);
        UpdateLine(line, rect);
        UpdateRectangleLabel(data, rect);
    }

    // Small in-view number so the texture matches the numbered list below it. Sits just inside
    // the rectangle's near (bottom-left) corner rather than centred, so it stays legible even on
    // very thin/sliver rectangles where a centred label could spill outside the rect entirely.
    void UpdateRectangleLabel(UVRectSaveData data, Rect rect)
    {
        if (!rectangleLabels.TryGetValue(data.id, out TextMeshPro label) || label == null)
        {
            GameObject go = new GameObject("UVRectLabel_" + data.id, typeof(TextMeshPro));
            go.transform.SetParent(visualRoot.transform, false);
            label = go.GetComponent<TextMeshPro>();
            label.alignment = TextAlignmentOptions.Center;
            label.fontSize = .25f;
            label.color = Color.white;
            label.outlineColor = Color.black;
            label.outlineWidth = .2f;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.raycastTarget = false;
            rectangleLabels[data.id] = label;
        }

        label.text = data.id.ToString();
        label.gameObject.SetActive(true);

        float insetU = Mathf.Min(.02f, rect.width * .25f);
        float insetV = Mathf.Min(.02f, rect.height * .25f);
        Vector3 anchor = UVToWorld(new Vector2(rect.xMin + insetU, rect.yMin + insetV));
        label.transform.position = anchor;
        label.transform.rotation = previewPlane.transform.rotation;
    }

    void UpdateLine(LineRenderer line, Rect rect)
    {
        if (line == null || previewPlane == null) return;
        line.SetPosition(0, UVToWorld(new Vector2(rect.xMin, rect.yMin)));
        line.SetPosition(1, UVToWorld(new Vector2(rect.xMax, rect.yMin)));
        line.SetPosition(2, UVToWorld(new Vector2(rect.xMax, rect.yMax)));
        line.SetPosition(3, UVToWorld(new Vector2(rect.xMin, rect.yMax)));
        line.SetPosition(4, UVToWorld(new Vector2(rect.xMin, rect.yMin)));
    }

    Vector3 UVToWorld(Vector2 uv)
    {
        Vector3 local = new Vector3(uv.x - .5f, uv.y - .5f, -.01f);
        return previewPlane.transform.TransformPoint(local);
    }

    void RefreshRectangleVisuals()
    {
        // Whatever was hovered is about to be destroyed and recreated below, so any stored
        // reference to it is stale regardless of source (texture hover or row hover).
        hoveredRectId = -1;

        EnsureVisualRoot();
        foreach (LineRenderer line in rectangleLines.Values)
            if (line != null) Destroy(line.gameObject);
        rectangleLines.Clear();
        foreach (TextMeshPro label in rectangleLabels.Values)
            if (label != null) Destroy(label.gameObject);
        rectangleLabels.Clear();

        if (previewPlane == null) return;
        foreach (UVRectSaveData data in rectangles)
            CreateOrUpdateRectangleVisual(data);
    }

    void UndoLastRectangle()
    {
        if (rectangles.Count == 0) return;
        UVRectSaveData last = rectangles[rectangles.Count - 1];
        rectangles.RemoveAt(rectangles.Count - 1);
        if (last != null && rectangleLines.TryGetValue(last.id, out LineRenderer line))
        {
            if (line != null) Destroy(line.gameObject);
            rectangleLines.Remove(last.id);
        }
        if (last != null && rectangleLabels.TryGetValue(last.id, out TextMeshPro label))
        {
            if (label != null) Destroy(label.gameObject);
            rectangleLabels.Remove(last.id);
        }
        RefreshSummary();
    }

    // Deletes one rectangle by id. Both right click routes land here: on the texture itself via
    // HandleRightClickDelete, and on a list row via UVRectSummaryRow.OnPointerDown.
    //
    // Ids are renumbered 1..N afterwards, for the same reason ReorderRectangle renumbers. The id
    // is printed on the row AND on the on-texture label AND is what a group's predetermined UV
    // range is expressed in, so a hole in the sequence reads as a rectangle that failed to draw
    // rather than one that was deleted on purpose. The cost is that a group pointing at "rect 4"
    // now points at whatever moved into slot 4, which is exactly what dragging a row to re-order
    // has always done here.
    //
    // deferRows exists for the row caller only. That row is a GameObject inside the very list
    // this is about to destroy, and it is mid pointer-event dispatch when it asks - see the
    // comment at the end of ReorderRectangle for what happens if the list is rebuilt underneath
    // a callback that is still running.
    public void RemoveRectangle(int id, bool deferRows)
    {
        int index = -1;
        for (int i = 0; i < rectangles.Count; i++)
        {
            if (rectangles[i] == null) continue;
            if (rectangles[i].id != id) continue;
            index = i;
            break;
        }
        if (index < 0) return;

        // Before anything is destroyed. ClearHoveredRect restores the normal outline material and
        // width on the LineRenderer it is holding, and the row skin on the row it is holding, and
        // RefreshRectangleVisuals below is about to destroy the first of those.
        if (hoveredRectId == id) ClearHoveredRect(id);

        rectangles.RemoveAt(index);

        for (int i = 0; i < rectangles.Count; i++)
        {
            if (rectangles[i] == null) continue;
            rectangles[i].id = i + 1;
        }
        nextRectId = rectangles.Count + 1;

        RefreshRectangleVisuals();
        UpdateSummary();

        if (deferRows)
        {
            // The rebuild is a frame away but the ids stop meaning what they meant right now, so
            // the rows cannot be left standing until then. RebuildSummaryList retires them too;
            // this is the same call made early because the rebuild is not happening yet.
            RetireSummaryRows();
            summaryListDirty = true;
        }
        else
        {
            summaryListDirty = false;
            RebuildSummaryList();
        }

        // Same commit AUTO makes after ImportDefinitions. Selection polling in
        // MaterialUVRectAuthority would notice the change a frame later anyway, but only while a
        // material is selected, and a delete is worth writing through immediately either way.
        MaterialUVRectAuthority.StoreSelectedWorkspaceNow();

        // A plain right click arms nothing in the undo history by itself - it refuses to, so that
        // orbiting the model does not pay for a full serialize every time. This is a real edit,
        // so it says so.
        UndoHistoryAuthority.NotifyEdit();
    }

    public void ClearDefinitions()
    {
        rectangles.Clear();
        nextRectId = 1;
        foreach (LineRenderer line in rectangleLines.Values)
            if (line != null) Destroy(line.gameObject);
        rectangleLines.Clear();
        foreach (TextMeshPro label in rectangleLabels.Values)
            if (label != null) Destroy(label.gameObject);
        rectangleLabels.Clear();
        RefreshSummary();
    }

    public List<UVRectSaveData> ExportDefinitions()
    {
        return rectangles
            .Where(r => r != null)
            .Select(r => new UVRectSaveData
            {
                id = r.id,
                uMin = r.uMin,
                vMin = r.vMin,
                uMax = r.uMax,
                vMax = r.vMax
            })
            .ToList();
    }

    public void ImportDefinitions(List<UVRectSaveData> source)
    {
        rectangles.Clear();
        nextRectId = 1;

        if (source != null)
        {
            foreach (UVRectSaveData item in source)
            {
                if (item == null) continue;
                float uMin = Mathf.Clamp01(Mathf.Min(item.uMin, item.uMax));
                float uMax = Mathf.Clamp01(Mathf.Max(item.uMin, item.uMax));
                float vMin = Mathf.Clamp01(Mathf.Min(item.vMin, item.vMax));
                float vMax = Mathf.Clamp01(Mathf.Max(item.vMin, item.vMax));
                if (uMax - uMin < .001f || vMax - vMin < .001f) continue;

                int id = item.id > 0 ? item.id : nextRectId;
                rectangles.Add(new UVRectSaveData
                {
                    id = id,
                    uMin = uMin,
                    vMin = vMin,
                    uMax = uMax,
                    vMax = vMax
                });
                nextRectId = Mathf.Max(nextRectId, id + 1);
            }
        }

        if (wasActive)
        {
            ResolvePreviewPlane();
            RefreshRectangleVisuals();
        }
        RefreshSummary();
    }

    void RestorePendingProjectData()
    {
        HairProjectSaveData pending = HairProjectSaveData.PendingUVRectRestore;
        if (pending == null) return;
        HairProjectSaveData.PendingUVRectRestore = null;
        ImportDefinitions(pending.uvRects);
    }

    // Cheap per-frame header text only - the row list itself is rebuilt explicitly at the
    // specific points the data actually changes (see RefreshSummary), not every frame, since
    // destroying and recreating draggable row GameObjects 60 times a second would both waste
    // work and constantly interrupt any hover/drag state a person is mid-interaction with.
    void UpdateSummary()
    {
        if (summaryText == null) return;

        // The delete hint lives here rather than in the instructions paragraph above because the
        // section's height budget is exact: 781 is 4 of padding plus 20 of spacing plus the six
        // children's declared heights with nothing left over, so a third line of instructions
        // would push the bottom of the scroll list out of the section.
        if (rectangles.Count == 0)
        {
            summaryText.text = "UV Rects: 0  •  click DRAW UV RECT, then drag on the texture";
        }
        else
        {
            summaryText.text = "UV Rects: " + rectangles.Count + "  (DRAG to ORDER, RMB to DELETE)";
        }
    }

    // Header text plus the row list, for the specific moments the data actually changed
    // (a rect was drawn/undone/cleared/imported). Safe to call synchronously from anywhere
    // that is NOT itself a row's drag/drop event callback - see ReorderRectangle for why that
    // one case needs to defer instead.
    void RefreshSummary()
    {
        UpdateSummary();
        RebuildSummaryList();
    }

    // Tells every live row that the id it was built with has stopped meaning what it meant.
    //
    // Destroying the list's children is NOT enough on its own, which is why this exists. A row
    // that is mid-drag was reparented onto the root canvas by OnBeginDrag, so it is not a child
    // of the list any more and the loop below walks straight past it - and then OnEndDrag puts
    // it back into a list that has since been rebuilt without it, as a dead duplicate row still
    // answering to a number that now belongs to a different rectangle. Retiring it here is what
    // makes it destroy itself instead.
    void RetireSummaryRows()
    {
        foreach (UVRectSummaryRow row in summaryRows.Values)
        {
            if (row != null) row.Invalidate();
        }
        summaryRows.Clear();
    }

    void RebuildSummaryList()
    {
        if (summaryListRoot == null) return;

        RetireSummaryRows();
        for (int i = summaryListRoot.childCount - 1; i >= 0; i--)
            Destroy(summaryListRoot.GetChild(i).gameObject);
        summaryRows.Clear();

        List<UVRectSaveData> ordered = rectangles.OrderBy(r => r.id).ToList();
        foreach (UVRectSaveData r in ordered)
        {
            string text = "#" + r.id + "  U " + r.uMin.ToString("F3") + "–" + r.uMax.ToString("F3") +
                "  V " + r.vMin.ToString("F3") + "–" + r.vMax.ToString("F3");
            CreateSummaryRow(r.id, text);
        }
    }

    void CreateSummaryRow(int id, string text)
    {
        GameObject rowGO = new GameObject("Row_" + id, typeof(RectTransform), typeof(Image), typeof(CanvasGroup), typeof(LayoutElement), typeof(UVRectSummaryRow));
        rowGO.transform.SetParent(summaryListRoot, false);
        LayoutElement rowLayout = rowGO.GetComponent<LayoutElement>();
        rowLayout.preferredHeight = 18f;
        rowLayout.minHeight = 18f;

        Image rowImage = rowGO.GetComponent<Image>();
        rowImage.raycastTarget = true;

        GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textGO.transform.SetParent(rowGO.transform, false);
        RectTransform textRect = textGO.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(8f, 0f);
        textRect.offsetMax = new Vector2(-6f, 0f);
        TextMeshProUGUI label = textGO.GetComponent<TextMeshProUGUI>();
        label.text = text;
        label.fontSize = 11.5f;
        label.color = Color.white;
        label.alignment = TextAlignmentOptions.MidlineLeft;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.overflowMode = TextOverflowModes.Ellipsis;
        label.raycastTarget = false;

        UVRectSummaryRow row = rowGO.GetComponent<UVRectSummaryRow>();
        row.Bind(this, id, rowImage, rowGO.GetComponent<CanvasGroup>());
        summaryRows[id] = row;
    }

    // Called by UVRectSummaryRow.OnDrop. Moves the dragged rectangle into the dropped-on
    // rectangle's slot and renumbers everything sequentially so ids stay contiguous and match
    // the new visual order (both here and in the on-texture labels).
    public void ReorderRectangle(int draggedId, int dropOntoId)
    {
        if (draggedId == dropOntoId) return;

        List<UVRectSaveData> ordered = rectangles.OrderBy(r => r.id).ToList();
        int fromIndex = ordered.FindIndex(r => r.id == draggedId);
        if (fromIndex < 0) return;

        UVRectSaveData moved = ordered[fromIndex];
        ordered.RemoveAt(fromIndex);
        int insertIndex = ordered.FindIndex(r => r.id == dropOntoId);
        if (insertIndex < 0) insertIndex = ordered.Count;
        ordered.Insert(insertIndex, moved);

        for (int i = 0; i < ordered.Count; i++)
            ordered[i].id = i + 1;

        rectangles.Clear();
        rectangles.AddRange(ordered);
        nextRectId = rectangles.Count + 1;

        // Safe to do immediately - these are separate on-texture GameObjects, not the row
        // GameObject currently mid-dispatch for this very OnDrop callback.
        RefreshRectangleVisuals();
        UpdateSummary();

        // The row list itself IS the GameObject hierarchy this callback is executing under, so
        // destroying and recreating it here would kill the dragged row before OnEndDrag finishes
        // running on it. Deferred to next frame's Update, once this frame's event dispatch is done.
        summaryListDirty = true;
    }
}
