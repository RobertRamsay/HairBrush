using System;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Left group-panel presentation authority. Keeps POST/CLUMPER row structure untouched while
// giving each Hair Group a compact, readable identity block:
//
//   GROUP 0              (unnamed)
//   6 cards
//   1,944 polys
//
// or, after the existing double-click rename gesture:
//
//   G0_Spike
//   6 cards
//   1,944 polys
//
// ModelViewer's private groupNames dictionary stores only the friendly suffix ("Spike"). The
// numeric group id remains the real identity used by HairCards, POST, CLUMPER, UVs and saving.
[DefaultExecutionOrder(9000)]
public class GroupPanelPostHintStats : MonoBehaviour
{
    private const float HeaderHeight = 66f;
    private const float HeaderControlHeight = 54f;
    private const float UtilityButtonHeight = 24f;
    private const float UtilityButtonGap = 5f;
    private const float UtilityRightInset = 8f;
    private const float UtilityBottomInset = 6f;
    private const float UVButtonWidth = 74f;
    private const float SoloButtonWidth = 52f;

    private GameObject boundPanel;
    private TextMeshProUGUI hint;
    private ModelViewer viewer;
    private FieldInfo groupNamesField;
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<GroupPanelPostHintStats>() != null) return;
        GameObject go = new GameObject("GroupPanelPostHintStats");
        DontDestroyOnLoad(go);
        go.AddComponent<GroupPanelPostHintStats>();
    }

    void LateUpdate()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + .10f;

        ResolveViewer();

        GameObject panel = GameObject.Find("GroupManagerPanel");
        if (panel == null)
        {
            boundPanel = null;
            hint = null;
            return;
        }

        if (boundPanel != panel || hint == null)
            Bind(panel);

        MaintainHintOrder(panel.transform);
        UpdateGroupHeaders();
    }

    void ResolveViewer()
    {
        if (viewer != null && groupNamesField != null) return;
        viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer != null)
            groupNamesField = typeof(ModelViewer).GetField("groupNames", BindingFlags.Instance | BindingFlags.NonPublic);
    }

    void Bind(GameObject panel)
    {
        boundPanel = panel;
        Transform existing = panel.transform.Find("PostCreateHint");
        if (existing != null)
        {
            hint = existing.GetComponent<TextMeshProUGUI>();
            ApplyHintStyle();
            return;
        }

        GameObject go = new GameObject("PostCreateHint", typeof(RectTransform), typeof(LayoutElement), typeof(TextMeshProUGUI));
        go.transform.SetParent(panel.transform, false);

        LayoutElement layout = go.GetComponent<LayoutElement>();
        layout.preferredHeight = 60f;
        layout.minHeight = 60f;

        RectTransform rect = go.GetComponent<RectTransform>();
        rect.sizeDelta = new Vector2(0f, 60f);

        hint = go.GetComponent<TextMeshProUGUI>();
        ApplyHintStyle();

        MaintainHintOrder(panel.transform);
    }

    void ApplyHintStyle()
    {
        if (hint == null) return;
        hint.text = "CTRL+CLICK on SURFACE to add a POST EFFECT\nSPACE+CLICK on SURFACE to move selected POST\nCTRL+CLICK in SPACE to deactivate";
        hint.fontSize = 11f;
        hint.fontStyle = FontStyles.Bold;
        hint.alignment = TextAlignmentOptions.MidlineLeft;
        hint.color = new Color(.72f, .78f, .86f, 1f);
        hint.textWrappingMode = TextWrappingModes.Normal;
        hint.raycastTarget = false;

        LayoutElement layout = hint.GetComponent<LayoutElement>();
        if (layout != null)
        {
            layout.preferredHeight = 60f;
            layout.minHeight = 60f;
        }
        hint.rectTransform.sizeDelta = new Vector2(0f, 60f);
    }

    void MaintainHintOrder(Transform panel)
    {
        if (hint == null || panel == null) return;
        Transform polys = panel.Find("HairPolygonCounterText");
        Transform title = panel.Find("TitleText");
        int target = polys != null ? polys.GetSiblingIndex() + 1 : title != null ? title.GetSiblingIndex() + 1 : 0;
        if (hint.transform.GetSiblingIndex() != target)
            hint.transform.SetSiblingIndex(target);
    }

    void UpdateGroupHeaders()
    {
        Dictionary<int, int> cardsByGroup = new();
        Dictionary<int, long> polysByGroup = new();

        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
        {
            if (card == null) continue;
            int gid = card.groupId;
            cardsByGroup[gid] = cardsByGroup.TryGetValue(gid, out int count) ? count + 1 : 1;

            long polys = 0;
            MeshFilter filter = card.GetComponent<MeshFilter>();
            Mesh mesh = filter != null ? filter.sharedMesh : null;
            if (mesh != null)
                for (int sub = 0; sub < mesh.subMeshCount; sub++)
                    polys += (long)mesh.GetIndexCount(sub) / 3L;

            polysByGroup[gid] = (polysByGroup.TryGetValue(gid, out long existing) ? existing : 0L) + polys;
        }

        Dictionary<int, string> groupNames = GetGroupNames();

        foreach (RectTransform item in FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (item == null || !item.name.StartsWith("GroupItem_", StringComparison.Ordinal)) continue;
            if (!int.TryParse(item.name.Substring("GroupItem_".Length), out int gid)) continue;

            Transform labelButton = item.Find("LabelButton");
            if (labelButton == null) continue;

            Transform nameLabel = labelButton.Find("Label");
            Transform countLabel = labelButton.Find("CardCountLabel");
            TextMeshProUGUI nameText = nameLabel != null ? nameLabel.GetComponent<TextMeshProUGUI>() : null;
            TextMeshProUGUI statsText = countLabel != null ? countLabel.GetComponent<TextMeshProUGUI>() : null;
            if (nameText == null || statsText == null) continue;

            string friendly = string.Empty;
            if (groupNames != null && groupNames.TryGetValue(gid, out string stored))
            {
                friendly = NormalizeFriendlyName(gid, stored);
                if (!string.Equals(stored ?? string.Empty, friendly, StringComparison.Ordinal))
                    groupNames[gid] = friendly;
            }

            int cardCount = cardsByGroup.TryGetValue(gid, out int c) ? c : 0;
            long polyCount = polysByGroup.TryGetValue(gid, out long p) ? p : 0L;
            string cardWord = cardCount == 1 ? "card" : "cards";
            string polyWord = polyCount == 1 ? "poly" : "polys";

            nameText.text = string.IsNullOrWhiteSpace(friendly)
                ? "GROUP " + gid
                : "G" + gid + "_" + friendly;
            statsText.text = cardCount.ToString("N0") + " " + cardWord + "\n" +
                             polyCount.ToString("N0") + " " + polyWord;

            ApplyHeaderLayout(item, labelButton, nameText, statsText);
        }
    }

    Dictionary<int, string> GetGroupNames()
    {
        ResolveViewer();
        return viewer != null && groupNamesField != null
            ? groupNamesField.GetValue(viewer) as Dictionary<int, string>
            : null;
    }

    static string NormalizeFriendlyName(int gid, string stored)
    {
        string value = (stored ?? string.Empty).Trim();
        if (value.Length == 0) return string.Empty;

        string legacy = "Group " + gid;
        if (string.Equals(value, legacy, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, legacy + " (Default)", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "Default", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "(Default)", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        string renderedPrefix = "G" + gid + "_";
        if (value.StartsWith(renderedPrefix, StringComparison.OrdinalIgnoreCase))
            value = value.Substring(renderedPrefix.Length).Trim();

        return value;
    }

    static void ApplyHeaderLayout(
        RectTransform item,
        Transform labelButton,
        TextMeshProUGUI nameText,
        TextMeshProUGUI statsText)
    {
        item.sizeDelta = new Vector2(item.sizeDelta.x, HeaderHeight);

        // This header needs two vertical lanes: the name gets the full top width while
        // UV/SOLO occupy only the lower-right half. The original HorizontalLayoutGroup
        // permanently reserved their width beside the name, so disable it and position the
        // three header controls explicitly inside the existing GroupItem rect.
        HorizontalLayoutGroup row = item.GetComponent<HorizontalLayoutGroup>();
        if (row != null) row.enabled = false;

        RectTransform labelRect = labelButton as RectTransform;
        if (labelRect != null)
        {
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.pivot = new Vector2(.5f, .5f);
            labelRect.offsetMin = new Vector2(8f, 6f);
            labelRect.offsetMax = new Vector2(-8f, -6f);
        }

        // LabelButton historically relied on its child TMP labels to provide the raycast
        // surface. The tidy header deliberately makes those labels non-raycastable, so give
        // the button its own invisible hit graphic. This restores both normal selection and
        // ModelViewer's existing double-click-to-rename gesture without putting the text back
        // in the event path.
        Image labelHit = labelButton.GetComponent<Image>();
        if (labelHit == null) labelHit = labelButton.gameObject.AddComponent<Image>();
        labelHit.color = new Color(0f, 0f, 0f, 0f);
        labelHit.raycastTarget = true;

        Button labelControl = labelButton.GetComponent<Button>();
        if (labelControl != null) labelControl.targetGraphic = labelHit;

        // The coloured group strip itself should select too. Child controls (UV/SOLO) still
        // receive their own pointer clicks first, while otherwise-empty header space forwards
        // to the same LabelButton callback used by clicking the name.
        Image itemHit = item.GetComponent<Image>();
        if (itemHit != null) itemHit.raycastTarget = true;
        GroupHeaderBackgroundClickProxy proxy = item.GetComponent<GroupHeaderBackgroundClickProxy>();
        if (proxy == null) proxy = item.gameObject.AddComponent<GroupHeaderBackgroundClickProxy>();
        proxy.labelButton = labelControl;

        RectTransform titleRect = nameText.rectTransform;
        titleRect.anchorMin = new Vector2(0f, 1f);
        titleRect.anchorMax = new Vector2(1f, 1f);
        titleRect.pivot = new Vector2(.5f, 1f);
        titleRect.offsetMin = new Vector2(2f, -23f);
        titleRect.offsetMax = new Vector2(-2f, -2f);
        nameText.fontSize = 14f;
        nameText.fontStyle = FontStyles.Bold;
        nameText.alignment = TextAlignmentOptions.TopLeft;
        nameText.color = Color.white;
        nameText.textWrappingMode = TextWrappingModes.NoWrap;
        nameText.overflowMode = TextOverflowModes.Ellipsis;
        nameText.raycastTarget = false;

        RectTransform statsRect = statsText.rectTransform;
        statsRect.anchorMin = new Vector2(0f, 0f);
        statsRect.anchorMax = new Vector2(1f, 0f);
        statsRect.pivot = new Vector2(.5f, 0f);
        statsRect.offsetMin = new Vector2(2f, 2f);
        statsRect.offsetMax = new Vector2(-2f, 33f);
        statsText.fontSize = 10.5f;
        statsText.fontStyle = FontStyles.Normal;
        statsText.alignment = TextAlignmentOptions.BottomLeft;
        statsText.color = new Color(.82f, .82f, .82f, .95f);
        statsText.textWrappingMode = TextWrappingModes.NoWrap;
        statsText.overflowMode = TextOverflowModes.Ellipsis;
        statsText.lineSpacing = -8f;
        statsText.raycastTarget = false;

        Transform solo = item.Find("SoloButton");
        if (solo is RectTransform soloRect)
        {
            soloRect.anchorMin = new Vector2(1f, 0f);
            soloRect.anchorMax = new Vector2(1f, 0f);
            soloRect.pivot = new Vector2(1f, 0f);
            soloRect.anchoredPosition = new Vector2(-UtilityRightInset, UtilityBottomInset);
            soloRect.sizeDelta = new Vector2(SoloButtonWidth, UtilityButtonHeight);
        }

        Transform uv = item.Find("GroupUVModeButton");
        if (uv is RectTransform uvRect)
        {
            uvRect.anchorMin = new Vector2(1f, 0f);
            uvRect.anchorMax = new Vector2(1f, 0f);
            uvRect.pivot = new Vector2(1f, 0f);
            uvRect.anchoredPosition = new Vector2(-(UtilityRightInset + SoloButtonWidth + UtilityButtonGap), UtilityBottomInset);
            uvRect.sizeDelta = new Vector2(UVButtonWidth, UtilityButtonHeight);
        }
    }
}

// The group row already has the green/grey Image that visually reads as the tab, but the
// original interaction only lived on the narrower LabelButton. Forward clicks on otherwise
// empty row space into that existing callback. UV and SOLO remain independent child Buttons.
public class GroupHeaderBackgroundClickProxy : MonoBehaviour, IPointerClickHandler
{
    [NonSerialized] public Button labelButton;

    public void OnPointerClick(PointerEventData eventData)
    {
        if (eventData == null || eventData.button != PointerEventData.InputButton.Left) return;
        if (labelButton == null) return;
        labelButton.onClick.Invoke();
        eventData.Use();
    }
}
