using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Compact-layout companion for the existing PRE UV range controls.
// GROUP keeps the original DualIntRangeSlider created by GroupUVRangeSliderUI.
// POST gets the same two-handle slider instead of falling back to the old MIN -> MAX text row.
// This class only owns layout/UX; the group/post UV authorities remain the data source of truth.
[DefaultExecutionOrder(9620)]
public class GroupUVRangeSliderUIAuthority : MonoBehaviour
{
    private ModelViewer viewer;
    private PostPredeterminedUVAuthority postAuthority;
    private TextureUVRectWorkspace workspace;

    private RectTransform boundGroupRow;
    private RectTransform boundPostRow;
    private DualIntRangeSlider postSlider;

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
        Resolve();
        if (viewer == null || viewer.groomingSliderPanelGO == null) return;

        CompactGroupRow();
        CompactPostRow();
    }

    void Resolve()
    {
        if (viewer == null) viewer = FindFirstObjectByType<ModelViewer>();
        if (postAuthority == null) postAuthority = FindFirstObjectByType<PostPredeterminedUVAuthority>();
        if (workspace == null) workspace = FindFirstObjectByType<TextureUVRectWorkspace>();
    }

    void CompactGroupRow()
    {
        RectTransform row = viewer.groomingSliderPanelGO.transform.Find("GroupUVPredetermined_Row") as RectTransform;
        if (row == null)
        {
            boundGroupRow = null;
            return;
        }

        if (boundGroupRow != row)
            boundGroupRow = row;

        PrepareRow(row);

        TMP_InputField minInput = row.Find("MINInput")?.GetComponent<TMP_InputField>();
        TMP_InputField maxInput = row.Find("MAXInput")?.GetComponent<TMP_InputField>();
        TMP_InputField seedInput = row.Find("SEEDInput")?.GetComponent<TMP_InputField>();
        Transform arrow = FindDirectText(row, "→");
        Transform rectLabel = FindDirectText(row, "UV RECTS");
        Transform seedLabel = FindDirectText(row, "SEED");
        Transform slider = row.Find("UVRectRangeSlider");
        Transform random = row.Find("GroupUVRandomSeedButton");

        // The controller still reads/writes these hidden fields. They simply stop consuming UI space.
        if (minInput != null && minInput.gameObject.activeSelf) minInput.gameObject.SetActive(false);
        if (maxInput != null && maxInput.gameObject.activeSelf) maxInput.gameObject.SetActive(false);
        if (arrow != null && arrow.gameObject.activeSelf) arrow.gameObject.SetActive(false);

        Place(rectLabel, 0.00f, 0.22f, 0.52f, 1.00f);
        Place(slider,    0.22f, 1.00f, 0.52f, 1.00f);
        Place(seedLabel, 0.00f, 0.18f, 0.00f, 0.48f);
        if (seedInput != null) Place(seedInput.transform, 0.18f, 0.83f, 0.00f, 0.48f);
        Place(random, 0.85f, 1.00f, 0.00f, 0.48f);

        MakeTextCompact(rectLabel, 10f, 13f);
        MakeTextCompact(seedLabel, 9f, 11f);
        MakeInputCompact(seedInput);
        MakeButtonTextCompact(random);
    }

    void CompactPostRow()
    {
        RectTransform row = viewer.groomingSliderPanelGO.transform.Find("PostPredeterminedUV_Row") as RectTransform;
        if (row == null)
        {
            boundPostRow = null;
            postSlider = null;
            return;
        }

        if (boundPostRow != row)
        {
            boundPostRow = row;
            postSlider = null;
        }

        PrepareRow(row);

        TMP_InputField minInput = row.Find("MINInput")?.GetComponent<TMP_InputField>();
        TMP_InputField maxInput = row.Find("MAXInput")?.GetComponent<TMP_InputField>();
        TMP_InputField seedInput = row.Find("SEEDInput")?.GetComponent<TMP_InputField>();
        Transform arrow = FindDirectText(row, "→");
        Transform rectLabel = FindDirectText(row, "UV RECTS") ?? FindDirectText(row, "POST UV");
        Transform seedLabel = FindDirectText(row, "SEED");
        Transform random = FindDirectButton(row, "R");

        if (rectLabel != null)
        {
            TextMeshProUGUI labelText = rectLabel.GetComponent<TextMeshProUGUI>();
            if (labelText != null && labelText.text != "UV RECTS") labelText.text = "UV RECTS";
        }

        if (minInput != null && minInput.gameObject.activeSelf) minInput.gameObject.SetActive(false);
        if (maxInput != null && maxInput.gameObject.activeSelf) maxInput.gameObject.SetActive(false);
        if (arrow != null && arrow.gameObject.activeSelf) arrow.gameObject.SetActive(false);

        if (postSlider == null)
        {
            Transform existing = row.Find("UVRectRangeSlider");
            if (existing != null)
            {
                postSlider = existing.GetComponent<DualIntRangeSlider>();
                if (postSlider == null) Destroy(existing.gameObject);
            }

            if (postSlider == null)
            {
                GameObject sliderGO = new GameObject("UVRectRangeSlider", typeof(RectTransform), typeof(Image));
                sliderGO.transform.SetParent(row, false);
                Image hit = sliderGO.GetComponent<Image>();
                hit.color = new Color(0f, 0f, 0f, .01f);
                hit.raycastTarget = true;

                postSlider = sliderGO.AddComponent<DualIntRangeSlider>();
                postSlider.BuildVisuals();
                postSlider.onRangeChanged = OnPostRangeChanged;
            }
        }

        Place(rectLabel, 0.00f, 0.22f, 0.52f, 1.00f);
        if (postSlider != null) Place(postSlider.transform, 0.22f, 1.00f, 0.52f, 1.00f);
        Place(seedLabel, 0.00f, 0.18f, 0.00f, 0.48f);
        if (seedInput != null) Place(seedInput.transform, 0.18f, 0.83f, 0.00f, 0.48f);
        Place(random, 0.85f, 1.00f, 0.00f, 0.48f);

        MakeTextCompact(rectLabel, 10f, 13f);
        MakeTextCompact(seedLabel, 9f, 11f);
        MakeInputCompact(seedInput);
        MakeButtonTextCompact(random);

        SyncPostSlider();
    }

    void SyncPostSlider()
    {
        if (postSlider == null || postAuthority == null || postSlider.IsDragging) return;
        if (!postAuthority.TryGetActiveContext(out _, out int groupId, out int minId, out int maxId, out _)) return;

        List<UVRectSaveData> rects = GetRectsForGroup(groupId);
        if (rects.Count == 0) return;

        int availableMin = rects.Min(r => r.id);
        int availableMax = rects.Max(r => r.id);
        minId = Mathf.Clamp(minId, availableMin, availableMax);
        maxId = Mathf.Clamp(maxId, minId, availableMax);
        postSlider.Configure(availableMin, availableMax, minId, maxId, true);
    }

    void OnPostRangeChanged(int low, int high)
    {
        if (postAuthority == null) return;
        postAuthority.SetActiveRange(true, low.ToString());
        postAuthority.SetActiveRange(false, high.ToString());
    }

    List<UVRectSaveData> GetRectsForGroup(int groupId)
    {
        if (MaterialUVRectAuthority.TryGetRectsForGroup(groupId, out List<UVRectSaveData> materialRects) && materialRects != null)
            return materialRects.Where(r => r != null).OrderBy(r => r.id).ToList();

        if (workspace == null) workspace = FindFirstObjectByType<TextureUVRectWorkspace>();
        return workspace != null
            ? workspace.ExportDefinitions().Where(r => r != null).OrderBy(r => r.id).ToList()
            : new List<UVRectSaveData>();
    }

    static void PrepareRow(RectTransform row)
    {
        if (row == null) return;

        HorizontalLayoutGroup layout = row.GetComponent<HorizontalLayoutGroup>();
        if (layout != null && layout.enabled) layout.enabled = false;

        LayoutElement element = row.GetComponent<LayoutElement>();
        if (element == null) element = row.gameObject.AddComponent<LayoutElement>();
        element.minHeight = 62f;
        element.preferredHeight = 62f;
        row.sizeDelta = new Vector2(row.sizeDelta.x, 62f);
    }

    static Transform FindDirectText(Transform row, string value)
    {
        if (row == null) return null;
        foreach (Transform child in row)
        {
            TextMeshProUGUI text = child.GetComponent<TextMeshProUGUI>();
            if (text != null && text.text == value) return child;
        }
        return null;
    }

    static Transform FindDirectButton(Transform row, string label)
    {
        if (row == null) return null;
        foreach (Transform child in row)
        {
            Button button = child.GetComponent<Button>();
            if (button == null) continue;
            TMP_Text text = child.GetComponentInChildren<TMP_Text>(true);
            if (text != null && text.text == label) return child;
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

    static void MakeTextCompact(Transform item, float minSize, float maxSize)
    {
        if (item == null) return;
        TMP_Text text = item.GetComponent<TMP_Text>();
        if (text == null) return;
        text.enableAutoSizing = true;
        text.fontSizeMin = minSize;
        text.fontSizeMax = maxSize;
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Ellipsis;
    }

    static void MakeInputCompact(TMP_InputField input)
    {
        if (input == null || input.textComponent == null) return;
        TMP_Text text = input.textComponent;
        text.enableAutoSizing = true;
        text.fontSizeMin = 8f;
        text.fontSizeMax = 11f;
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Ellipsis;
    }

    static void MakeButtonTextCompact(Transform button)
    {
        if (button == null) return;
        TMP_Text text = button.GetComponentInChildren<TMP_Text>(true);
        if (text == null) return;
        text.enableAutoSizing = true;
        text.fontSizeMin = 8f;
        text.fontSizeMax = 11f;
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Ellipsis;
    }
}
