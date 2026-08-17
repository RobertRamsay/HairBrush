using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Replaces the numeric UV rectangle min/max fields with one discrete two-handle range slider.
// The existing hidden inputs/controller remain the data authority, so project persistence and
// assignment behaviour are unchanged. One pointer gesture owns exactly one handle until release.
[DefaultExecutionOrder(6600)]
public class GroupUVRangeSliderUI : MonoBehaviour
{
    private GroupPredeterminedUVController controller;
    private ModelViewer viewer;
    private TextureUVRectWorkspace workspace;
    private MethodInfo setRangeValueMethod;

    private GameObject boundRow;
    private TMP_InputField minInput;
    private TMP_InputField maxInput;
    private DualIntRangeSlider rangeSlider;
    private int lastLow = int.MinValue;
    private int lastHigh = int.MinValue;
    private int lastAvailableMin = int.MinValue;
    private int lastAvailableMax = int.MinValue;
    private bool lastInteractable;
    private bool haveRangeState;
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<GroupUVRangeSliderUI>() != null) return;
        GameObject go = new GameObject("GroupUVRangeSliderUI");
        DontDestroyOnLoad(go);
        go.AddComponent<GroupUVRangeSliderUI>();
    }

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + .04f;

        Resolve();
        BindRow();
        SyncRange();
    }

    void Resolve()
    {
        if (controller == null)
        {
            controller = FindFirstObjectByType<GroupPredeterminedUVController>();
            if (controller != null)
                setRangeValueMethod = typeof(GroupPredeterminedUVController).GetMethod(
                    "SetRangeValue", BindingFlags.Instance | BindingFlags.NonPublic);
        }
        if (viewer == null) viewer = FindFirstObjectByType<ModelViewer>();
        if (workspace == null) workspace = FindFirstObjectByType<TextureUVRectWorkspace>();
    }

    void BindRow()
    {
        if (viewer == null || viewer.groomingSliderPanelGO == null || controller == null || setRangeValueMethod == null) return;

        Transform row = viewer.groomingSliderPanelGO.transform.Find("GroupUVPredetermined_Row");
        if (row == null || row.gameObject == boundRow) return;

        boundRow = row.gameObject;
        minInput = row.Find("MINInput")?.GetComponent<TMP_InputField>();
        maxInput = row.Find("MAXInput")?.GetComponent<TMP_InputField>();
        if (minInput == null || maxInput == null) return;

        // Hide the old visible min -> max widgets but keep the inputs alive as the controller's
        // state/persistence bridge. They continue to receive SetTextWithoutNotify updates.
        Transform minT = minInput.transform;
        Transform maxT = maxInput.transform;
        int minIndex = minT.GetSiblingIndex();
        Transform arrow = minIndex + 1 < row.childCount ? row.GetChild(minIndex + 1) : null;
        minT.gameObject.SetActive(false);
        maxT.gameObject.SetActive(false);
        if (arrow != null && arrow != maxT) arrow.gameObject.SetActive(false);

        Transform old = row.Find("UVRectRangeSlider");
        if (old != null) Destroy(old.gameObject);

        GameObject sliderGO = new GameObject("UVRectRangeSlider", typeof(RectTransform), typeof(LayoutElement), typeof(Image));
        sliderGO.transform.SetParent(row, false);
        sliderGO.transform.SetSiblingIndex(Mathf.Min(minIndex, row.childCount - 1));
        RectTransform sliderRect = sliderGO.GetComponent<RectTransform>();
        sliderRect.sizeDelta = new Vector2(220f, 30f);
        LayoutElement le = sliderGO.GetComponent<LayoutElement>();
        le.preferredWidth = 220f;
        le.minWidth = 160f;
        le.preferredHeight = 30f;
        Image hit = sliderGO.GetComponent<Image>();
        hit.color = new Color(0f, 0f, 0f, .01f);
        hit.raycastTarget = true;

        rangeSlider = sliderGO.AddComponent<DualIntRangeSlider>();
        rangeSlider.ShowTicks = true;
        rangeSlider.BuildVisuals();
        rangeSlider.onRangeChanged = OnRangeChanged;
        lastLow = int.MinValue;
        lastHigh = int.MinValue;
        lastAvailableMin = int.MinValue;
        lastAvailableMax = int.MinValue;
        haveRangeState = false;
    }

    void SyncRange()
    {
        if (rangeSlider == null || minInput == null || maxInput == null) return;
        if (rangeSlider.IsDragging) return;

        List<UVRectSaveData> rects = null;
        if (viewer != null && MaterialUVRectAuthority.TryGetRectsForGroup(viewer.currentGroupId, out List<UVRectSaveData> materialRects))
            rects = materialRects?.Where(r => r != null).OrderBy(r => r.id).ToList();

        if (rects == null)
        {
            if (workspace == null) workspace = FindFirstObjectByType<TextureUVRectWorkspace>();
            rects = workspace != null
                ? workspace.ExportDefinitions()?.Where(r => r != null).OrderBy(r => r.id).ToList()
                : null;
        }
        if (rects == null || rects.Count == 0) return;

        int availableMin = rects.Min(r => r.id);
        int availableMax = rects.Max(r => r.id);
        if (!int.TryParse(minInput.text, out int low)) low = availableMin;
        if (!int.TryParse(maxInput.text, out int high)) high = availableMax;
        low = Mathf.Clamp(low, availableMin, availableMax);
        high = Mathf.Clamp(high, low, availableMax);

        bool interactable = minInput.interactable && maxInput.interactable;
        bool changed = !haveRangeState ||
                       availableMin != lastAvailableMin ||
                       availableMax != lastAvailableMax ||
                       low != lastLow ||
                       high != lastHigh ||
                       interactable != lastInteractable;
        if (!changed) return;

        rangeSlider.Configure(availableMin, availableMax, low, high, interactable);
        lastAvailableMin = availableMin;
        lastAvailableMax = availableMax;
        lastLow = low;
        lastHigh = high;
        lastInteractable = interactable;
        haveRangeState = true;
    }

    void OnRangeChanged(int low, int high)
    {
        if (controller == null || viewer == null || setRangeValueMethod == null) return;

        int groupId = viewer.currentGroupId;
        if (low != lastLow)
            setRangeValueMethod.Invoke(controller, new object[] { groupId, true, low.ToString() });
        if (high != lastHigh)
            setRangeValueMethod.Invoke(controller, new object[] { groupId, false, high.ToString() });

        lastLow = low;
        lastHigh = high;
        haveRangeState = true;
        nextScan = 0f;
    }
}

// Lightweight runtime two-thumb discrete slider. It deliberately does not use two overlapping
// Unity Sliders: when their handles coincide Unity's raycast order makes one handle effectively
// impossible to select. Here pointer-down chooses one owner and that owner stays fixed for the drag.
public class DualIntRangeSlider : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
{
    public Action<int, int> onRangeChanged;

    private RectTransform root;
    private RectTransform track;
    private RectTransform selected;
    private RectTransform ticksRoot;
    private RectTransform lowHandle;
    private RectTransform highHandle;
    private TextMeshProUGUI lowText;
    private TextMeshProUGUI highText;

    private int minValue = 1;
    private int maxValue = 1;
    private int lowValue = 1;
    private int highValue = 1;
    private bool isInteractable = true;
    private int activeHandle; // -1 low, +1 high, 0 none
    private bool alternateOverlap;
    private bool showTicks = true;
    private int ticksMin = int.MinValue;
    private int ticksMax = int.MinValue;

    public bool IsDragging => activeHandle != 0;

    public bool ShowTicks
    {
        get => showTicks;
        set
        {
            if (showTicks == value) return;
            showTicks = value;
            ticksMin = int.MinValue;
            ticksMax = int.MinValue;
            if (ticksRoot != null)
            {
                ticksRoot.gameObject.SetActive(showTicks);
                if (showTicks) RebuildTicks();
            }
        }
    }

    public void BuildVisuals()
    {
        root = GetComponent<RectTransform>();

        GameObject trackGO = new GameObject("Track", typeof(RectTransform), typeof(Image));
        trackGO.transform.SetParent(transform, false);
        track = trackGO.GetComponent<RectTransform>();
        track.anchorMin = new Vector2(0f, .5f);
        track.anchorMax = new Vector2(1f, .5f);
        track.offsetMin = new Vector2(10f, -2f);
        track.offsetMax = new Vector2(-10f, 2f);
        trackGO.GetComponent<Image>().color = new Color(.26f, .28f, .31f, 1f);
        trackGO.GetComponent<Image>().raycastTarget = false;

        GameObject selectedGO = new GameObject("SelectedRange", typeof(RectTransform), typeof(Image));
        selectedGO.transform.SetParent(track, false);
        selected = selectedGO.GetComponent<RectTransform>();
        selected.anchorMin = new Vector2(0f, 0f);
        selected.anchorMax = new Vector2(1f, 1f);
        selected.offsetMin = Vector2.zero;
        selected.offsetMax = Vector2.zero;
        selectedGO.GetComponent<Image>().color = new Color(.20f, .50f, .80f, 1f);
        selectedGO.GetComponent<Image>().raycastTarget = false;

        GameObject ticksGO = new GameObject("Ticks", typeof(RectTransform));
        ticksGO.transform.SetParent(transform, false);
        ticksRoot = ticksGO.GetComponent<RectTransform>();
        ticksRoot.anchorMin = Vector2.zero;
        ticksRoot.anchorMax = Vector2.one;
        ticksRoot.offsetMin = new Vector2(10f, 0f);
        ticksRoot.offsetMax = new Vector2(-10f, 0f);
        ticksRoot.gameObject.SetActive(showTicks);

        lowHandle = CreateHandle("LowHandle", out lowText);
        highHandle = CreateHandle("HighHandle", out highText);
    }

    RectTransform CreateHandle(string name, out TextMeshProUGUI text)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(transform, false);
        RectTransform rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0f, .5f);
        rt.sizeDelta = new Vector2(28f, 24f);
        go.GetComponent<Image>().color = new Color(.88f, .90f, .93f, 1f);
        go.GetComponent<Image>().raycastTarget = false;

        GameObject textGO = new GameObject("Value", typeof(RectTransform), typeof(TextMeshProUGUI));
        textGO.transform.SetParent(go.transform, false);
        RectTransform tr = textGO.GetComponent<RectTransform>();
        tr.anchorMin = Vector2.zero;
        tr.anchorMax = Vector2.one;
        tr.offsetMin = Vector2.zero;
        tr.offsetMax = Vector2.zero;
        text = textGO.GetComponent<TextMeshProUGUI>();
        text.fontSize = 10f;
        text.fontStyle = FontStyles.Bold;
        text.alignment = TextAlignmentOptions.Center;
        text.color = new Color(.08f, .09f, .10f, 1f);
        text.raycastTarget = false;
        return rt;
    }

    public void Configure(int min, int max, int low, int high, bool interactable)
    {
        minValue = min;
        maxValue = Mathf.Max(min, max);
        lowValue = Mathf.Clamp(low, minValue, maxValue);
        highValue = Mathf.Clamp(high, lowValue, maxValue);
        isInteractable = interactable;
        RebuildTicks();
        RefreshVisuals();
    }

    void RebuildTicks()
    {
        if (ticksRoot == null) return;
        ticksRoot.gameObject.SetActive(showTicks);
        if (!showTicks) return;
        if (ticksMin == minValue && ticksMax == maxValue && ticksRoot.childCount > 0) return;

        ticksMin = minValue;
        ticksMax = maxValue;
        for (int i = ticksRoot.childCount - 1; i >= 0; i--)
            Destroy(ticksRoot.GetChild(i).gameObject);

        if (maxValue <= minValue)
        {
            AddTick(minValue, true);
            return;
        }

        int span = maxValue - minValue;
        int step = span <= 31 ? 1 : Mathf.CeilToInt(span / 31f);
        for (int value = minValue; value < maxValue; value += step)
            AddTick(value, value == minValue || (value - minValue) % 5 == 0);
        AddTick(maxValue, true);
    }

    void AddTick(int value, bool major)
    {
        if (ticksRoot == null) return;
        float n = ValueNormalized(value);
        GameObject go = new GameObject("Tick_" + value, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(ticksRoot, false);
        RectTransform rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(n, .5f);
        rt.pivot = new Vector2(.5f, .5f);
        rt.sizeDelta = new Vector2(major ? 2f : 1f, major ? 10f : 7f);
        rt.anchoredPosition = Vector2.zero;
        Image image = go.GetComponent<Image>();
        image.color = major
            ? new Color(.82f, .84f, .88f, .85f)
            : new Color(.70f, .72f, .76f, .60f);
        image.raycastTarget = false;
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        if (!isInteractable || root == null) return;
        float n = PointerNormalized(eventData);
        float lowN = ValueNormalized(lowValue);
        float highN = ValueNormalized(highValue);

        if (lowValue == highValue)
        {
            if (n < lowN - .001f) activeHandle = -1;
            else if (n > highN + .001f) activeHandle = 1;
            else
            {
                alternateOverlap = !alternateOverlap;
                activeHandle = alternateOverlap ? -1 : 1;
            }
        }
        else
        {
            activeHandle = Mathf.Abs(n - lowN) <= Mathf.Abs(n - highN) ? -1 : 1;
        }

        if (activeHandle < 0) lowHandle.SetAsLastSibling();
        else highHandle.SetAsLastSibling();
        ApplyPointer(eventData);
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (!isInteractable || activeHandle == 0) return;
        ApplyPointer(eventData);
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        activeHandle = 0;
    }

    void ApplyPointer(PointerEventData eventData)
    {
        int value = NormalizedValue(PointerNormalized(eventData));
        int oldLow = lowValue;
        int oldHigh = highValue;

        if (activeHandle < 0) lowValue = Mathf.Clamp(value, minValue, highValue);
        else if (activeHandle > 0) highValue = Mathf.Clamp(value, lowValue, maxValue);

        if (oldLow == lowValue && oldHigh == highValue) return;
        RefreshVisuals();
        onRangeChanged?.Invoke(lowValue, highValue);
    }

    float PointerNormalized(PointerEventData eventData)
    {
        if (root == null) return 0f;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(root, eventData.position, eventData.pressEventCamera, out Vector2 local))
            return 0f;
        Rect rect = root.rect;
        if (rect.width <= .001f) return 0f;
        float left = rect.xMin + 10f;
        float right = rect.xMax - 10f;
        return Mathf.InverseLerp(left, right, local.x);
    }

    float ValueNormalized(int value)
    {
        return maxValue == minValue ? .5f : Mathf.InverseLerp(minValue, maxValue, value);
    }

    int NormalizedValue(float n)
    {
        return Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(minValue, maxValue, Mathf.Clamp01(n))), minValue, maxValue);
    }

    void RefreshVisuals()
    {
        if (root == null || track == null || lowHandle == null || highHandle == null) return;
        float lowN = ValueNormalized(lowValue);
        float highN = ValueNormalized(highValue);

        lowHandle.anchorMin = lowHandle.anchorMax = new Vector2(lowN, .5f);
        highHandle.anchorMin = highHandle.anchorMax = new Vector2(highN, .5f);
        lowHandle.anchoredPosition = Vector2.zero;
        highHandle.anchoredPosition = Vector2.zero;

        selected.anchorMin = new Vector2(lowN, 0f);
        selected.anchorMax = new Vector2(highN, 1f);
        selected.offsetMin = Vector2.zero;
        selected.offsetMax = Vector2.zero;

        if (lowText != null) lowText.text = lowValue.ToString();
        if (highText != null) highText.text = highValue.ToString();

        float alpha = isInteractable ? 1f : .45f;
        if (lowHandle.GetComponent<Image>() != null) lowHandle.GetComponent<Image>().color = new Color(.88f, .90f, .93f, alpha);
        if (highHandle.GetComponent<Image>() != null) highHandle.GetComponent<Image>().color = new Color(.88f, .90f, .93f, alpha);
    }
}
