using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Keeps the PREDETERMINED UV controls usable in the compact 300px grooming panel.
// The underlying GroupPredeterminedUVController remains the source of truth; this authority
// lays its generated controls out compactly and restores the missing two-handle range slider.
[DefaultExecutionOrder(9620)]
public class GroupUVRangeSliderUIAuthority : MonoBehaviour
{
    private ModelViewer viewer;
    private RectTransform boundRow;
    private TMP_InputField minInput;
    private TMP_InputField maxInput;
    private TMP_InputField seedInput;
    private Button randomButton;
    private CompactIntRangeSlider rangeSlider;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<GroupUVRangeSliderUIAuthority>() != null) return;
        GameObject go = new GameObject("GroupUVRangeSliderUIAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<GroupUVRangeSliderUIAuthority>();
    }

    void LateUpdate()
    {
        if (viewer == null) viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer == null || viewer.groomingSliderPanelGO == null) return;

        RectTransform liveRow = viewer.groomingSliderPanelGO.transform.Find("GroupUVPredetermined_Row") as RectTransform;
        if (liveRow == null) return;
        if (boundRow != liveRow || rangeSlider == null)
            BindRow(liveRow);

        SyncSlider();
    }

    void BindRow(RectTransform row)
    {
        boundRow = row;
        minInput = row.Find("MINInput")?.GetComponent<TMP_InputField>();
        maxInput = row.Find("MAXInput")?.GetComponent<TMP_InputField>();
        seedInput = row.Find("SEEDInput")?.GetComponent<TMP_InputField>();
        randomButton = row.Find("GroupUVRandomSeedButton")?.GetComponent<Button>();
        rangeSlider = null;
        if (minInput == null || maxInput == null) return;

        HorizontalLayoutGroup oldLayout = row.GetComponent<HorizontalLayoutGroup>();
        if (oldLayout != null) oldLayout.enabled = false;

        LayoutElement rowLayout = row.GetComponent<LayoutElement>();
        if (rowLayout == null) rowLayout = row.gameObject.AddComponent<LayoutElement>();
        rowLayout.minHeight = 62f;
        rowLayout.preferredHeight = 62f;
        row.sizeDelta = new Vector2(row.sizeDelta.x, 62f);

        Transform rectLabel = FindDirectText(row, "UV RECTS");
        Transform arrow = FindDirectText(row, "→");
        Transform seedLabel = FindDirectText(row, "SEED");

        // Top line fits wholly inside the narrow panel. Numeric values remain editable while
        // the visual slider underneath makes the selected inclusive rectangle range obvious.
        Place(rectLabel, 0.00f, 0.205f, 0.52f, 1.00f);
        Place(minInput.transform, 0.205f, 0.325f, 0.52f, 1.00f);
        Place(arrow, 0.325f, 0.375f, 0.52f, 1.00f);
        Place(maxInput.transform, 0.375f, 0.495f, 0.52f, 1.00f);
        Place(seedLabel, 0.515f, 0.635f, 0.52f, 1.00f);
        if (seedInput != null) Place(seedInput.transform, 0.635f, 0.865f, 0.52f, 1.00f);
        if (randomButton != null) Place(randomButton.transform, 0.885f, 1.00f, 0.52f, 1.00f);

        MakeTextCompact(rectLabel);
        MakeTextCompact(arrow);
        MakeTextCompact(seedLabel);
        MakeInputCompact(minInput);
        MakeInputCompact(maxInput);
        MakeInputCompact(seedInput);
        if (randomButton != null)
            MakeTextCompact(randomButton.GetComponentInChildren<TextMeshProUGUI>(true)?.transform);

        Transform existing = row.Find("UVRectRangeSlider");
        if (existing != null) Destroy(existing.gameObject);
        rangeSlider = BuildRangeSlider(row);
        rangeSlider.Changed += OnSliderChanged;
    }

    void SyncSlider()
    {
        if (rangeSlider == null || minInput == null || maxInput == null) return;

        int minLimit = 1;
        int maxLimit = 1;
        if (MaterialUVRectAuthority.TryGetRectsForGroup(viewer.currentGroupId, out List<UVRectSaveData> rects) && rects != null)
        {
            List<UVRectSaveData> valid = rects.Where(r => r != null).ToList();
            if (valid.Count > 0)
            {
                minLimit = valid.Min(r => r.id);
                maxLimit = valid.Max(r => r.id);
            }
        }

        int minValue = ParseInput(minInput, minLimit);
        int maxValue = ParseInput(maxInput, maxLimit);
        rangeSlider.SetRange(minLimit, maxLimit, minValue, maxValue);
        rangeSlider.Interactable = minInput.interactable && maxInput.interactable && maxLimit >= minLimit;
    }

    void OnSliderChanged(int minValue, int maxValue)
    {
        if (minInput == null || maxInput == null || !minInput.interactable || !maxInput.interactable) return;

        string minText = minValue.ToString();
        string maxText = maxValue.ToString();
        minInput.SetTextWithoutNotify(minText);
        maxInput.SetTextWithoutNotify(maxText);

        // Reuse the controller's existing listeners so save state, card reassignment and
        // normalization stay in one place instead of duplicating UV ownership logic here.
        minInput.onEndEdit.Invoke(minText);
        maxInput.onEndEdit.Invoke(maxText);
    }

    static int ParseInput(TMP_InputField input, int fallback)
    {
        return input != null && int.TryParse(input.text, out int value) ? value : fallback;
    }

    static Transform FindDirectText(Transform row, string value)
    {
        foreach (Transform child in row)
        {
            TextMeshProUGUI text = child.GetComponent<TextMeshProUGUI>();
            if (text != null && text.text == value) return child;
        }
        return null;
    }

    static void Place(Transform item, float xMin, float xMax, float yMin, float yMax)
    {
        if (item == null) return;
        RectTransform rect = item as RectTransform;
        if (rect == null) return;
        rect.anchorMin = new Vector2(xMin, yMin);
        rect.anchorMax = new Vector2(xMax, yMax);
        rect.pivot = new Vector2(.5f, .5f);
        rect.offsetMin = new Vector2(1f, 1f);
        rect.offsetMax = new Vector2(-1f, -1f);
        rect.anchoredPosition = Vector2.zero;
    }

    static void MakeTextCompact(Transform item)
    {
        if (item == null) return;
        TextMeshProUGUI text = item.GetComponent<TextMeshProUGUI>();
        if (text == null) return;
        text.enableAutoSizing = true;
        text.fontSizeMin = 8f;
        text.fontSizeMax = Mathf.Min(text.fontSize > 0f ? text.fontSize : 12f, 12f);
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Ellipsis;
    }

    static void MakeInputCompact(TMP_InputField input)
    {
        if (input == null) return;
        TMP_Text text = input.textComponent;
        if (text == null) return;
        text.enableAutoSizing = true;
        text.fontSizeMin = 8f;
        text.fontSizeMax = 11f;
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Ellipsis;
    }

    static CompactIntRangeSlider BuildRangeSlider(RectTransform row)
    {
        GameObject rootGO = new GameObject("UVRectRangeSlider", typeof(RectTransform), typeof(Image), typeof(CompactIntRangeSlider));
        rootGO.transform.SetParent(row, false);
        RectTransform root = rootGO.GetComponent<RectTransform>();
        root.anchorMin = new Vector2(.01f, .08f);
        root.anchorMax = new Vector2(.99f, .40f);
        root.offsetMin = Vector2.zero;
        root.offsetMax = Vector2.zero;
        Image hitArea = rootGO.GetComponent<Image>();
        hitArea.color = new Color(0f, 0f, 0f, 0f);
        hitArea.raycastTarget = true;

        GameObject trackGO = new GameObject("Track", typeof(RectTransform), typeof(Image));
        trackGO.transform.SetParent(root, false);
        RectTransform track = trackGO.GetComponent<RectTransform>();
        track.anchorMin = new Vector2(0f, .5f);
        track.anchorMax = new Vector2(1f, .5f);
        track.sizeDelta = new Vector2(0f, 4f);
        track.anchoredPosition = Vector2.zero;
        trackGO.GetComponent<Image>().color = new Color(.25f, .25f, .25f, 1f);
        trackGO.GetComponent<Image>().raycastTarget = false;

        GameObject fillGO = new GameObject("SelectedRange", typeof(RectTransform), typeof(Image));
        fillGO.transform.SetParent(root, false);
        RectTransform fill = fillGO.GetComponent<RectTransform>();
        fill.anchorMin = new Vector2(0f, .5f);
        fill.anchorMax = new Vector2(1f, .5f);
        fill.sizeDelta = new Vector2(0f, 4f);
        fill.anchoredPosition = Vector2.zero;
        fillGO.GetComponent<Image>().color = new Color(.20f, .55f, .92f, 1f);
        fillGO.GetComponent<Image>().raycastTarget = false;

        RectTransform minHandle = BuildHandle(root, "MinHandle");
        RectTransform maxHandle = BuildHandle(root, "MaxHandle");

        CompactIntRangeSlider slider = rootGO.GetComponent<CompactIntRangeSlider>();
        slider.Bind(fill, minHandle, maxHandle);
        return slider;
    }

    static RectTransform BuildHandle(RectTransform parent, string name)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        RectTransform rect = go.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, .5f);
        rect.anchorMax = new Vector2(0f, .5f);
        rect.pivot = new Vector2(.5f, .5f);
        rect.sizeDelta = new Vector2(9f, 18f);
        go.GetComponent<Image>().color = new Color(.93f, .93f, .93f, 1f);
        go.GetComponent<Image>().raycastTarget = false;
        return rect;
    }
}

// Minimal integer range control with two handles. It deliberately has no ownership knowledge;
// it only reports an inclusive min/max pair back to the generated PRE UV inputs.
public class CompactIntRangeSlider : MonoBehaviour, IPointerDownHandler, IDragHandler
{
    public event System.Action<int, int> Changed;

    private RectTransform rect;
    private RectTransform fill;
    private RectTransform minHandle;
    private RectTransform maxHandle;
    private int minLimit = 1;
    private int maxLimit = 1;
    private int minValue = 1;
    private int maxValue = 1;
    private bool dragMin = true;
    private bool interactable = true;

    public bool Interactable
    {
        get => interactable;
        set
        {
            interactable = value;
            float alpha = value ? 1f : .45f;
            SetAlpha(fill, alpha);
            SetAlpha(minHandle, alpha);
            SetAlpha(maxHandle, alpha);
        }
    }

    public void Bind(RectTransform selectedFill, RectTransform lowHandle, RectTransform highHandle)
    {
        rect = transform as RectTransform;
        fill = selectedFill;
        minHandle = lowHandle;
        maxHandle = highHandle;
        RefreshVisuals();
    }

    public void SetRange(int availableMin, int availableMax, int selectedMin, int selectedMax)
    {
        minLimit = availableMin;
        maxLimit = Mathf.Max(availableMin, availableMax);
        minValue = Mathf.Clamp(selectedMin, minLimit, maxLimit);
        maxValue = Mathf.Clamp(selectedMax, minLimit, maxLimit);
        if (minValue > maxValue)
        {
            int swap = minValue;
            minValue = maxValue;
            maxValue = swap;
        }
        RefreshVisuals();
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        if (!interactable) return;
        float pointer = PointerNormalized(eventData);
        float low = ValueNormalized(minValue);
        float high = ValueNormalized(maxValue);
        dragMin = Mathf.Abs(pointer - low) <= Mathf.Abs(pointer - high);
        ApplyPointer(pointer);
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (!interactable) return;
        ApplyPointer(PointerNormalized(eventData));
    }

    void ApplyPointer(float normalized)
    {
        int value = maxLimit <= minLimit
            ? minLimit
            : Mathf.RoundToInt(Mathf.Lerp(minLimit, maxLimit, Mathf.Clamp01(normalized)));

        int oldMin = minValue;
        int oldMax = maxValue;
        if (dragMin) minValue = Mathf.Min(value, maxValue);
        else maxValue = Mathf.Max(value, minValue);
        if (oldMin == minValue && oldMax == maxValue) return;

        RefreshVisuals();
        Changed?.Invoke(minValue, maxValue);
    }

    float PointerNormalized(PointerEventData eventData)
    {
        if (rect == null) return 0f;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, eventData.position, eventData.pressEventCamera, out Vector2 local);
        return rect.rect.width <= .001f ? 0f : Mathf.InverseLerp(rect.rect.xMin, rect.rect.xMax, local.x);
    }

    float ValueNormalized(int value)
    {
        return maxLimit <= minLimit ? .5f : Mathf.InverseLerp(minLimit, maxLimit, value);
    }

    void RefreshVisuals()
    {
        float low = ValueNormalized(minValue);
        float high = ValueNormalized(maxValue);
        if (fill != null)
        {
            fill.anchorMin = new Vector2(low, .5f);
            fill.anchorMax = new Vector2(high, .5f);
            fill.offsetMin = Vector2.zero;
            fill.offsetMax = Vector2.zero;
        }
        PositionHandle(minHandle, low);
        PositionHandle(maxHandle, high);
    }

    static void PositionHandle(RectTransform handle, float normalized)
    {
        if (handle == null) return;
        handle.anchorMin = new Vector2(normalized, .5f);
        handle.anchorMax = new Vector2(normalized, .5f);
        handle.anchoredPosition = Vector2.zero;
    }

    static void SetAlpha(RectTransform target, float alpha)
    {
        if (target == null) return;
        Image image = target.GetComponent<Image>();
        if (image == null) return;
        Color c = image.color;
        c.a = alpha;
        image.color = c;
    }
}
