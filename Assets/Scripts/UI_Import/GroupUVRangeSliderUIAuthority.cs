using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Compact-layout companion for the existing PRE UV range controls.
// GROUP gets its own tighter mode/range layout for the 300px grooming panel.
// POST deliberately keeps the layout that is already working well.
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

        CompactGroupModeRow();
        CompactGroupRow();
        CompactPostRow();
    }

    void Resolve()
    {
        if (viewer == null) viewer = FindFirstObjectByType<ModelViewer>();
        if (postAuthority == null) postAuthority = FindFirstObjectByType<PostPredeterminedUVAuthority>();
        if (workspace == null) workspace = FindFirstObjectByType<TextureUVRectWorkspace>();
    }

    void CompactGroupModeRow()
    {
        RectTransform row = viewer.groomingSliderPanelGO.transform.Find("GroupUVMode_Row") as RectTransform;
        if (row == null) return;

        PrepareRow(row, 34f);

        Transform modeLabel = FindDirectText(row, "UV MODE");
        Transform modeButton = row.Find("GroupUVModeButton");
        Transform status = FindGroupStatusText(row);

        // The authored widths here used to total more than 500px. At the compact panel width
        // that made PREDETERMINED look like a giant button and pushed the status off-screen.
        Place(modeLabel, 0.00f, 0.18f, 0.08f, 0.92f);
        Place(modeButton, 0.19f, 0.62f, 0.08f, 0.92f);
        Place(status, 0.64f, 1.00f, 0.08f, 0.92f);

        MakeTextCompact(modeLabel, 9f, 12f);
        MakeButtonTextCompact(modeButton, 9f, 11f);
        MakeTextCompact(status, 8f, 10f);
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

        PrepareRow(row, 58f);

        TMP_InputField minInput = row.Find("MINInput")?.GetComponent<TMP_InputField>();
        TMP_InputField maxInput = row.Find("MAXInput")?.GetComponent<TMP_InputField>();
        TMP_InputField seedInput = row.Find("SEEDInput")?.GetComponent<TMP_InputField>();
        Transform arrow = FindDirectText(row, "→");
        Transform rectLabel = FindDirectText(row, "UV RECTS");
        Transform seedLabel = FindDirectText(row, "SEED");
        Transform slider = row.Find("UVRectRangeSlider");
        Transform random = row.Find("GroupUVRandomSeedButton");

        // MIN/MAX remain alive as the controller's state bridge; the visible two-thumb slider
        // carries those values instead, leaving much more horizontal travel for card selection.
        if (minInput != null && minInput.gameObject.activeSelf) minInput.gameObject.SetActive(false);
        if (maxInput != null && maxInput.gameObject.activeSelf) maxInput.gameObject.SetActive(false);
        if (arrow != null && arrow.gameObject.activeSelf) arrow.gameObject.SetActive(false);

        Place(rectLabel, 0.00f, 0.24f, 0.52f, 1.00f);
        // Right edge pulled in from 1.00 so the high handle (28px wide, centred on its value
        // position) clears the panel's right-edge scrollbar at max instead of sliding under it.
        Place(slider, 0.24f, 0.96f, 0.52f, 1.00f);
        // The seed line uses EXACT fixed geometry copied from the variance rows (SEED label
        // 38x24 at x4, seed field 78x24 at x47, RANDOMIZE 92x19 at x130), replacing the old
        // proportional shares - proportions could only ever approximate the variance layout,
        // and the whole point of this line is to read identically to those rows.
        PlaceFixed(seedLabel, 4f, 2f, 38f, 24f);
        if (seedInput != null) PlaceFixed(seedInput.transform, 47f, 2f, 78f, 24f);
        PlaceFixed(random, 130f, 4f, 92f, 19f);

        MakeTextCompact(rectLabel, 9f, 11f);
        MakeTextCompact(seedLabel, 9f, 10f);
        MakeInputCompact(seedInput);
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

        PrepareRow(row, 62f);

        TMP_InputField minInput = row.Find("MINInput")?.GetComponent<TMP_InputField>();
        TMP_InputField maxInput = row.Find("MAXInput")?.GetComponent<TMP_InputField>();
        TMP_InputField seedInput = row.Find("SEEDInput")?.GetComponent<TMP_InputField>();
        Transform arrow = FindDirectText(row, "→");
        Transform rectLabel = FindDirectText(row, "UV RECTS") ?? FindDirectText(row, "POST UV");
        Transform seedLabel = FindDirectText(row, "SEED");
        Transform random = FindDirectButton(row, "RANDOMIZE") ?? FindDirectButton(row, "R") ?? row.Find("RButton");

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
                postSlider.ShowTicks = false;
                postSlider.BuildVisuals();
                postSlider.onRangeChanged = OnPostRangeChanged;
            }
            else
            {
                // Preserve POST's existing uncluttered appearance; tick marks are a GROUP aid.
                postSlider.ShowTicks = false;
            }
        }

        Place(rectLabel, 0.00f, 0.26f, 0.52f, 1.00f);
        if (postSlider != null) Place(postSlider.transform, 0.26f, 0.96f, 0.52f, 1.00f);
        // Same exact fixed variance-row geometry as the GROUP block above.
        PlaceFixed(seedLabel, 4f, 2f, 38f, 24f);
        if (seedInput != null) PlaceFixed(seedInput.transform, 47f, 2f, 78f, 24f);
        PlaceFixed(random, 130f, 4f, 92f, 19f);

        MakeTextCompact(rectLabel, 10f, 13f);
        MakeTextCompact(seedLabel, 9f, 11f);
        MakeInputCompact(seedInput);

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

    static void PrepareRow(RectTransform row, float height)
    {
        if (row == null) return;

        HorizontalLayoutGroup layout = row.GetComponent<HorizontalLayoutGroup>();
        if (layout != null && layout.enabled) layout.enabled = false;

        LayoutElement element = row.GetComponent<LayoutElement>();
        if (element == null) element = row.gameObject.AddComponent<LayoutElement>();
        element.minHeight = height;
        element.preferredHeight = height;
        row.sizeDelta = new Vector2(row.sizeDelta.x, height);
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

    static Transform FindGroupStatusText(Transform row)
    {
        if (row == null) return null;
        foreach (Transform child in row)
        {
            TMP_Text text = child.GetComponent<TMP_Text>();
            if (text == null) continue;
            if (text.text == "NO UV RECTS" || text.text.EndsWith(" UV RECTS")) return child;
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

    // Exact pixel placement anchored to the row's bottom-left, for elements that must render
    // identically to their fixed-size counterparts in the variance rows rather than scale with
    // the row. x/y are offsets from the bottom-left corner; w/h are absolute sizes.
    static void PlaceFixed(Transform item, float x, float y, float w, float h)
    {
        if (item == null) return;
        RectTransform rect = item as RectTransform;
        if (rect == null) return;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.zero;
        rect.pivot = Vector2.zero;
        rect.sizeDelta = new Vector2(w, h);
        rect.anchoredPosition = new Vector2(x, y);
    }

    static void MakeTextCompact(Transform item, float minSize, float maxSize)
    {
        if (item == null) return;
        TMP_Text text = item.GetComponent<TMP_Text>();
        if (text == null) return;
        text.enableAutoSizing = true;
        text.fontSizeMin = minSize;
        text.fontSizeMax = maxSize;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Ellipsis;
    }

    static void MakeInputCompact(TMP_InputField input)
    {
        if (input == null || input.textComponent == null) return;
        TMP_Text text = input.textComponent;
        text.enableAutoSizing = true;
        text.fontSizeMin = 8f;
        text.fontSizeMax = 11f;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Ellipsis;
    }

    static void MakeButtonTextCompact(Transform button, float minSize = 8f, float maxSize = 11f)
    {
        if (button == null) return;
        TMP_Text text = button.GetComponentInChildren<TMP_Text>(true);
        if (text == null) return;
        text.enableAutoSizing = true;
        text.fontSizeMin = minSize;
        text.fontSizeMax = maxSize;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Ellipsis;
    }
}
