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

    private ModelViewer viewer;
    private GameObject texturePanel;
    private GameObject previewPlane;
    private GameObject groupPanel;
    private bool groupPanelWasActive;

    private GameObject section;
    private TextMeshProUGUI summaryText;
    private TextMeshProUGUI drawButtonText;
    private Image drawButtonImage;

    private GameObject visualRoot;
    private LineRenderer draftLine;
    private Material lineMaterial;

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
        HandleDrawInput();
        UpdateSummary();
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
        UpdateSummary();
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

        Transform existing = texturePanel.transform.Find("UVWorkspaceSection");
        if (existing != null)
        {
            section = existing.gameObject;
            summaryText = section.transform.Find("Summary")?.GetComponent<TextMeshProUGUI>();
            Transform draw = section.transform.Find("Buttons/Row1/DRAW UV RECT");
            drawButtonImage = draw != null ? draw.GetComponent<Image>() : null;
            drawButtonText = draw != null ? draw.GetComponentInChildren<TextMeshProUGUI>(true) : null;
            return;
        }

        section = new GameObject("UVWorkspaceSection", typeof(RectTransform), typeof(LayoutElement), typeof(VerticalLayoutGroup));
        section.transform.SetParent(texturePanel.transform, false);
        LayoutElement sectionLayout = section.GetComponent<LayoutElement>();
        sectionLayout.preferredHeight = 366f;
        sectionLayout.minHeight = 366f;
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
        buttonsLayout.preferredHeight = 66f;
        buttonsLayout.minHeight = 66f;
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

        summaryText = AddText(section.transform, "Summary", 12.5f, 226f, FontStyles.Normal);
        summaryText.gameObject.name = "Summary";
        summaryText.alignment = TextAlignmentOptions.TopLeft;
        summaryText.textWrappingMode = TextWrappingModes.Normal;

        int tabIndex = texturePanel.transform.Find("PanelTabRow") != null
            ? texturePanel.transform.Find("PanelTabRow").GetSiblingIndex() + 1
            : 0;
        section.transform.SetSiblingIndex(tabIndex);

        UpdateDrawButton();
        UpdateSummary();
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

    GameObject AddButtonRow(Transform parent, string rowName)
    {
        GameObject row = new GameObject(rowName, typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        row.transform.SetParent(parent, false);
        row.GetComponent<LayoutElement>().preferredHeight = 30f;
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
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 28f);
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
            UpdateSummary();
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
            label.enableWordWrapping = false;
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
        UpdateSummary();
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
        UpdateSummary();
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
        UpdateSummary();
    }

    void RestorePendingProjectData()
    {
        HairProjectSaveData pending = HairProjectSaveData.PendingUVRectRestore;
        if (pending == null) return;
        HairProjectSaveData.PendingUVRectRestore = null;
        ImportDefinitions(pending.uvRects);
    }

    void UpdateSummary()
    {
        if (summaryText == null) return;
        if (rectangles.Count == 0)
        {
            summaryText.text = "UV Rects: 0  •  click DRAW UV RECT, then drag on the texture";
            return;
        }

        List<UVRectSaveData> ordered = rectangles.OrderBy(r => r.id).ToList();
        string rows = string.Join("\n", ordered.Select(r =>
            "#" + r.id + "  U " + r.uMin.ToString("F3") + "–" + r.uMax.ToString("F3") +
            "  V " + r.vMin.ToString("F3") + "–" + r.vMax.ToString("F3")));
        summaryText.text = "UV Rects: " + rectangles.Count + "\n" + rows;
    }
}
