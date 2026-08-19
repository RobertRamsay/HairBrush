using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// Owns card placement input so SHIFT can be a pure mode-cycle key instead of a held paint
// modifier. Existing ModelViewer camera controls remain untouched; only its legacy placement
// branch is suppressed for the frame while grooming itself stays enabled.
[DefaultExecutionOrder(-5000)]
public class PlacementBrushModeAuthority : MonoBehaviour
{
    public enum PlacementMode { Place = 0, Paint = 1, Spray = 2, Erase = 3 }

    private ModelViewer viewer;
    private FieldInfo groomingModeField;
    private FieldInfo textureModeField;
    private FieldInfo selectionModeField;
    private FieldInfo selectionHotspotField;
    private FieldInfo loadedModelField;
    private MethodInfo pinHairCardMethod;
    private MethodInfo enterSelectionModeMethod;
    private MethodInfo clearSelectionHotspotMethod;
    private MethodInfo selectGroupMethod;
    private MethodInfo refreshGroupListMethod;

    private PlacementMode mode = PlacementMode.Place;
    private float brushRadius = .035f;
    private float sprayFalloff = .55f;
    private float nextActionTime;
    private float nextUIScan;
    private bool restorePending;
    private bool restoreSelectionState;

    private GameObject modeRow;
    private GameObject radiusRow;
    private GameObject falloffRow;
    private Button modeButton;
    private TextMeshProUGUI modeText;
    private Slider radiusSlider;
    private Slider falloffSlider;
    private Slider boundSegmentsSlider;

    private LineRenderer brushPreview;
    private Material brushMaterial;

    private const float ActionInterval = .05f;
    private const int CircleSegments = 64;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<PlacementBrushModeAuthority>() != null) return;
        GameObject go = new GameObject("PlacementBrushModeAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<PlacementBrushModeAuthority>();
    }

    void Update()
    {
        Resolve();
        if (viewer == null) return;

        // Keeps the displayed slider value correct if brushRadius was changed from outside this
        // script (e.g. the [ ] hotkey) rather than through the slider itself - same pattern
        // SelectionBrushScaleTuning already uses for the Ctrl+Click selection radius.
        if (radiusSlider != null && !Mathf.Approximately(radiusSlider.value, brushRadius))
            radiusSlider.SetValueWithoutNotify(brushRadius);

        if (Time.unscaledTime >= nextUIScan)
        {
            nextUIScan = Time.unscaledTime + .15f;
            EnsureUI();
            EnsureSegmentMinimum();
        }

        bool groomingEnabled = GetBool(groomingModeField);
        bool selectionWasActive = GetBool(selectionModeField);
        restoreSelectionState = selectionWasActive;
        restorePending = false;

        if (!groomingEnabled || GetLoadedModel() == null || GetBool(textureModeField))
        {
            HideBrushPreview();
            return;
        }

        if (Keyboard.current == null || Mouse.current == null) return;

        bool shiftPressed = Keyboard.current.leftShiftKey.wasPressedThisFrame || Keyboard.current.rightShiftKey.wasPressedThisFrame;
        if (shiftPressed)
        {
            CycleMode();
            HideBrushPreview();
            SuppressLegacyPlacement(selectionWasActive);
            return;
        }

        bool alt = Keyboard.current.leftAltKey.isPressed || Keyboard.current.rightAltKey.isPressed;
        bool ctrl = Keyboard.current.ctrlKey.isPressed;
        bool tab = Keyboard.current.tabKey.isPressed;
        bool space = Keyboard.current.spaceKey.isPressed;

        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
        {
            HideBrushPreview();
            return;
        }

        if (alt && Mouse.current.leftButton.wasPressedThisFrame)
        {
            if (RaycastCursor(out RaycastHit hit)) SelectNearestGroup(hit.point);
            HideBrushPreview();
            SuppressLegacyPlacement(selectionWasActive);
            return;
        }

        if (ctrl && Mouse.current.leftButton.wasPressedThisFrame)
        {
            bool selected = false;
            if (RaycastCursor(out RaycastHit hit))
            {
                enterSelectionModeMethod?.Invoke(viewer, new object[] { hit.point, hit.normal });
                selected = true;
            }
            else
            {
                clearSelectionHotspotMethod?.Invoke(viewer, null);
            }
            SuppressLegacyPlacement(selected);
            HideBrushPreview();
            return;
        }

        if (tab || space || selectionWasActive)
        {
            HideBrushPreview();
            SuppressLegacyPlacement(selectionWasActive);
            return;
        }

        RaycastHit centerHit;
        bool hasSurface = RaycastCursor(out centerHit);
        if ((mode == PlacementMode.Spray || mode == PlacementMode.Erase) && hasSurface)
            ShowBrushPreview(centerHit.point, centerHit.normal);
        else
            HideBrushPreview();

        bool act = false;
        switch (mode)
        {
            case PlacementMode.Place:
                act = Mouse.current.leftButton.wasPressedThisFrame;
                break;
            case PlacementMode.Paint:
            case PlacementMode.Spray:
            case PlacementMode.Erase:
                act = Mouse.current.leftButton.isPressed && Time.unscaledTime >= nextActionTime;
                break;
        }

        if (act && hasSurface)
        {
            nextActionTime = Time.unscaledTime + ActionInterval;
            switch (mode)
            {
                case PlacementMode.Place:
                case PlacementMode.Paint:
                    PlaceCard(centerHit.point, centerHit.normal);
                    break;
                case PlacementMode.Spray:
                    SprayOne(centerHit);
                    break;
                case PlacementMode.Erase:
                    EraseAt(centerHit.point);
                    break;
            }
        }

        // Block only ModelViewer's old placement branch for this frame. Grooming remains
        // enabled, so normal card creation/state and modifier systems stay live.
        SuppressLegacyPlacement(selectionWasActive);
    }

    void SuppressLegacyPlacement(bool stateToRestore)
    {
        if (viewer == null || selectionModeField == null) return;
        restoreSelectionState = stateToRestore;
        restorePending = true;
        selectionModeField.SetValue(viewer, true);
    }

    void LateUpdate()
    {
        if (!restorePending || viewer == null || selectionModeField == null) return;
        selectionModeField.SetValue(viewer, restoreSelectionState);
        restorePending = false;
    }

    void Resolve()
    {
        if (viewer != null) return;
        viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer == null) return;

        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        System.Type t = typeof(ModelViewer);
        groomingModeField = t.GetField("isGroomingMode", flags);
        textureModeField = t.GetField("isTextureEditorMode", flags);
        selectionModeField = t.GetField("isSelectionMode", flags);
        selectionHotspotField = t.GetField("hasSelectionHotspot", flags);
        loadedModelField = t.GetField("loadedModel", flags);
        pinHairCardMethod = t.GetMethod("PinHairCard", flags);
        enterSelectionModeMethod = t.GetMethod("EnterSelectionMode", flags);
        clearSelectionHotspotMethod = t.GetMethod("ClearSelectionHotspot", flags);
        selectGroupMethod = t.GetMethod("SelectGroup", flags);
        refreshGroupListMethod = t.GetMethod("RefreshGroupListUI", flags);
    }

    bool GetBool(FieldInfo field)
    {
        return viewer != null && field != null && field.GetValue(viewer) is bool b && b;
    }

    GameObject GetLoadedModel()
    {
        return viewer != null && loadedModelField != null ? loadedModelField.GetValue(viewer) as GameObject : null;
    }

    bool RaycastCursor(out RaycastHit hit)
    {
        hit = default;
        if (viewer == null || viewer.mainCamera == null || Mouse.current == null) return false;
        Ray ray = viewer.mainCamera.ScreenPointToRay(Mouse.current.position.ReadValue());
        return Physics.Raycast(ray, out hit);
    }

    void PlaceCard(Vector3 point, Vector3 normal)
    {
        pinHairCardMethod?.Invoke(viewer, new object[] { point, normal });
    }

    void SprayOne(RaycastHit centerHit)
    {
        Vector3 n = centerHit.normal.sqrMagnitude > .000001f ? centerHit.normal.normalized : Vector3.up;
        Vector3 tangent = Vector3.Cross(n, Vector3.up);
        if (tangent.sqrMagnitude < .000001f) tangent = Vector3.Cross(n, Vector3.right);
        tangent.Normalize();
        Vector3 bitangent = Vector3.Cross(n, tangent).normalized;

        float radius = Mathf.Max(.001f, brushRadius);
        for (int attempt = 0; attempt < 6; attempt++)
        {
            float radial01 = Mathf.Sqrt(Random.value);
            float angle = Random.value * Mathf.PI * 2f;
            float acceptance = Mathf.Lerp(1f, 1f - radial01, Mathf.Clamp01(sprayFalloff));
            if (Random.value > acceptance) continue;

            Vector3 offset = (tangent * Mathf.Cos(angle) + bitangent * Mathf.Sin(angle)) * (radial01 * radius);
            Vector3 approximate = centerHit.point + offset;
            float lift = radius + .05f;
            Ray surfaceRay = new Ray(approximate + n * lift, -n);
            if (!Physics.Raycast(surfaceRay, out RaycastHit hit, lift * 2f)) continue;

            PlaceCard(hit.point, hit.normal);
            return;
        }
    }

    void EraseAt(Vector3 center)
    {
        HairCard[] cards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        bool removed = false;
        for (int i = 0; i < cards.Length; i++)
        {
            HairCard card = cards[i];
            if (card == null || card.groupId != viewer.currentGroupId) continue;
            Vector3 root = card.GetSpawnHitPoint();
            if (root == Vector3.zero) root = card.transform.position;
            if (Vector3.Distance(root, center) > brushRadius) continue;

            if (viewer.lastPlacedCard == card) viewer.lastPlacedCard = null;
            Destroy(card.gameObject);
            removed = true;
        }
        if (removed) refreshGroupListMethod?.Invoke(viewer, null);
    }

    void SelectNearestGroup(Vector3 point)
    {
        HairCard[] cards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        HairCard nearest = null;
        float best = float.PositiveInfinity;
        foreach (HairCard card in cards)
        {
            if (card == null) continue;
            float d2 = (card.transform.position - point).sqrMagnitude;
            if (d2 < best) { best = d2; nearest = card; }
        }
        if (nearest != null) selectGroupMethod?.Invoke(viewer, new object[] { nearest.groupId });
    }

    void CycleMode()
    {
        SetMode((PlacementMode)(((int)mode + 1) % 4));
    }

    void SetMode(PlacementMode next)
    {
        mode = next;
        nextActionTime = 0f;
        UpdateModeUI();
    }

    void EnsureSegmentMinimum()
    {
        if (viewer == null || viewer.groomingSliderPanelGO == null) return;
        Slider[] sliders = viewer.groomingSliderPanelGO.GetComponentsInChildren<Slider>(true);
        foreach (Slider slider in sliders)
        {
            if (slider == null || slider.name != "Segments_Slider") continue;
            slider.minValue = 1f;
            slider.maxValue = 60f;
            slider.wholeNumbers = true;
            if (boundSegmentsSlider != slider)
            {
                boundSegmentsSlider = slider;
                slider.onValueChanged.AddListener(ApplySegmentOverride);
            }
            break;
        }
    }

    void ApplySegmentOverride(float value)
    {
        if (viewer == null || GetBool(selectionHotspotField)) return;
        int target = Mathf.Clamp(Mathf.RoundToInt(value), 1, 60);
        viewer.currentSegments = target;
        HairCard[] cards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        foreach (HairCard card in cards)
            if (card != null && card.groupId == viewer.currentGroupId)
                card.SetSegments(target);
    }

    void EnsureUI()
    {
        if (viewer == null || viewer.groomingSliderPanelGO == null) return;
        Transform panel = viewer.groomingSliderPanelGO.transform;

        Transform existing = panel.Find("PlacementModeRow");
        if (existing != null)
        {
            modeRow = existing.gameObject;
            if (modeButton == null) modeButton = existing.GetComponentInChildren<Button>(true);
            if (modeText == null && modeButton != null) modeText = modeButton.GetComponentInChildren<TextMeshProUGUI>(true);
            if (radiusRow == null)
            {
                Transform t = panel.Find("PlacementRadius_Row");
                if (t != null) radiusRow = t.gameObject;
            }
            if (falloffRow == null)
            {
                Transform t = panel.Find("SprayFalloff_Row");
                if (t != null) falloffRow = t.gameObject;
            }
            UpdateModeUI();
            return;
        }

        modeRow = new GameObject("PlacementModeRow", typeof(RectTransform), typeof(Image), typeof(Button));
        modeRow.transform.SetParent(panel, false);
        modeRow.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 38f);
        modeRow.GetComponent<Image>().color = ModeColor(mode);
        modeButton = modeRow.GetComponent<Button>();
        modeButton.onClick.AddListener(CycleMode);

        GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textGO.transform.SetParent(modeRow.transform, false);
        RectTransform tr = textGO.GetComponent<RectTransform>();
        tr.anchorMin = Vector2.zero;
        tr.anchorMax = Vector2.one;
        tr.offsetMin = Vector2.zero;
        tr.offsetMax = Vector2.zero;
        modeText = textGO.GetComponent<TextMeshProUGUI>();
        modeText.fontSize = 15f;
        modeText.fontStyle = FontStyles.Bold;
        modeText.alignment = TextAlignmentOptions.Center;
        modeText.color = Color.white;
        modeText.textWrappingMode = TextWrappingModes.NoWrap;
        modeText.raycastTarget = false;

        Transform top = panel.Find("TopControlsRow");
        if (top != null) modeRow.transform.SetSiblingIndex(Mathf.Min(top.GetSiblingIndex() + 1, panel.childCount - 1));

        radiusRow = CreateBrushSlider(panel, "PlacementRadius_Row", "Brush Radius", .002f, .20f, brushRadius, v => brushRadius = v, out radiusSlider);
        falloffRow = CreateBrushSlider(panel, "SprayFalloff_Row", "Spray Falloff", 0f, 1f, sprayFalloff, v => sprayFalloff = v, out falloffSlider);

        int afterMode = modeRow.transform.GetSiblingIndex() + 1;
        radiusRow.transform.SetSiblingIndex(Mathf.Min(afterMode, panel.childCount - 1));
        falloffRow.transform.SetSiblingIndex(Mathf.Min(afterMode + 1, panel.childCount - 1));
        UpdateModeUI();
    }

    GameObject CreateBrushSlider(Transform parent, string name, string label, float min, float max, float value, UnityEngine.Events.UnityAction<float> changed, out Slider slider)
    {
        GameObject row = new GameObject(name, typeof(RectTransform), typeof(VerticalLayoutGroup));
        row.transform.SetParent(parent, false);
        row.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 38f);
        VerticalLayoutGroup layout = row.GetComponent<VerticalLayoutGroup>();
        layout.spacing = 1f;
        layout.childControlWidth = true;
        layout.childControlHeight = false;

        GameObject labelGO = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        labelGO.transform.SetParent(row.transform, false);
        labelGO.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 17f);
        TextMeshProUGUI tmp = labelGO.GetComponent<TextMeshProUGUI>();
        tmp.fontSize = 14f;
        tmp.color = Color.white;
        tmp.text = label + ": " + value.ToString("F3");

        GameObject sliderGO = new GameObject(label.Replace(" ", "") + "_Slider", typeof(RectTransform), typeof(Slider));
        sliderGO.transform.SetParent(row.transform, false);
        sliderGO.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 17f);
        slider = sliderGO.GetComponent<Slider>();
        slider.minValue = min;
        slider.maxValue = max;
        slider.value = value;

        GameObject background = new GameObject("Background", typeof(RectTransform), typeof(Image));
        background.transform.SetParent(sliderGO.transform, false);
        background.GetComponent<Image>().color = new Color(.28f, .28f, .28f);
        RectTransform bg = background.GetComponent<RectTransform>();
        bg.anchorMin = new Vector2(0f, .3f);
        bg.anchorMax = new Vector2(1f, .7f);
        bg.offsetMin = Vector2.zero;
        bg.offsetMax = Vector2.zero;

        GameObject fillArea = new GameObject("Fill Area", typeof(RectTransform));
        fillArea.transform.SetParent(sliderGO.transform, false);
        RectTransform fa = fillArea.GetComponent<RectTransform>();
        fa.anchorMin = new Vector2(0f, .3f);
        fa.anchorMax = new Vector2(1f, .7f);
        fa.offsetMin = Vector2.zero;
        fa.offsetMax = Vector2.zero;

        GameObject fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
        fill.transform.SetParent(fillArea.transform, false);
        fill.GetComponent<Image>().color = new Color(.2f, .6f, 1f);
        slider.fillRect = fill.GetComponent<RectTransform>();
        slider.fillRect.anchorMin = Vector2.zero;
        slider.fillRect.anchorMax = Vector2.zero;
        slider.fillRect.sizeDelta = Vector2.zero;

        GameObject handleArea = new GameObject("Handle Slide Area", typeof(RectTransform));
        handleArea.transform.SetParent(sliderGO.transform, false);
        RectTransform ha = handleArea.GetComponent<RectTransform>();
        ha.anchorMin = Vector2.zero;
        ha.anchorMax = Vector2.one;
        ha.offsetMin = Vector2.zero;
        ha.offsetMax = Vector2.zero;

        GameObject handle = new GameObject("Handle", typeof(RectTransform), typeof(Image));
        handle.transform.SetParent(handleArea.transform, false);
        handle.GetComponent<Image>().color = Color.white;
        slider.handleRect = handle.GetComponent<RectTransform>();
        slider.handleRect.sizeDelta = new Vector2(18f, 0f);

        slider.onValueChanged.AddListener(v =>
        {
            tmp.text = label + ": " + v.ToString("F3");
            changed?.Invoke(v);
        });
        return row;
    }

    void UpdateModeUI()
    {
        if (modeText != null) modeText.text = "PLACEMENT: " + mode.ToString().ToUpperInvariant() + "   [SHIFT]";
        if (modeRow != null)
        {
            Image image = modeRow.GetComponent<Image>();
            if (image != null) image.color = ModeColor(mode);
        }
        if (radiusRow != null) radiusRow.SetActive(mode == PlacementMode.Spray || mode == PlacementMode.Erase);
        if (falloffRow != null) falloffRow.SetActive(mode == PlacementMode.Spray);
    }

    static Color ModeColor(PlacementMode value)
    {
        switch (value)
        {
            case PlacementMode.Paint: return new Color(.18f, .48f, .30f);
            case PlacementMode.Spray: return new Color(.58f, .40f, .15f);
            case PlacementMode.Erase: return new Color(.62f, .20f, .20f);
            default: return new Color(.20f, .42f, .68f);
        }
    }

    void EnsureBrushPreview()
    {
        if (brushPreview != null) return;
        GameObject go = new GameObject("PlacementBrushPreview");
        go.transform.SetParent(transform, false);
        brushPreview = go.AddComponent<LineRenderer>();
        brushPreview.loop = true;
        brushPreview.useWorldSpace = true;
        brushPreview.positionCount = CircleSegments;
        brushPreview.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        brushPreview.receiveShadows = false;

        Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
        if (shader != null)
        {
            brushMaterial = new Material(shader) { name = "HairBrushPlacementPreview" };
            brushPreview.material = brushMaterial;
        }
    }

    void ShowBrushPreview(Vector3 point, Vector3 normal)
    {
        EnsureBrushPreview();
        if (brushPreview == null) return;

        Vector3 n = normal.sqrMagnitude > .000001f ? normal.normalized : Vector3.up;
        Vector3 tangent = Vector3.Cross(n, Vector3.up);
        if (tangent.sqrMagnitude < .000001f) tangent = Vector3.Cross(n, Vector3.right);
        tangent.Normalize();
        Vector3 bitangent = Vector3.Cross(n, tangent).normalized;
        float radius = Mathf.Max(.001f, brushRadius);
        Vector3 center = point + n * .001f;

        for (int i = 0; i < CircleSegments; i++)
        {
            float angle = (i / (float)CircleSegments) * Mathf.PI * 2f;
            brushPreview.SetPosition(i, center + (tangent * Mathf.Cos(angle) + bitangent * Mathf.Sin(angle)) * radius);
        }

        Color c = mode == PlacementMode.Erase ? new Color(1f, .25f, .18f, .95f) : new Color(.25f, .85f, 1f, .95f);
        brushPreview.startColor = c;
        brushPreview.endColor = c;
        brushPreview.widthMultiplier = Mathf.Clamp(radius * .025f, .00035f, .0025f);
        brushPreview.enabled = true;
    }

    void HideBrushPreview()
    {
        if (brushPreview != null) brushPreview.enabled = false;
    }

    void OnDestroy()
    {
        if (brushMaterial != null) Destroy(brushMaterial);
    }
}
