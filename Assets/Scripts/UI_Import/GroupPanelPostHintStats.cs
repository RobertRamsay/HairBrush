using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Small, non-structural UX layer for the left group panel. It deliberately updates
// existing group rows in-place so it does not disturb POST row ordering or scroll layout.
[DefaultExecutionOrder(9000)]
public class GroupPanelPostHintStats : MonoBehaviour
{
    private GameObject boundPanel;
    private TextMeshProUGUI hint;
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
        UpdateGroupStats();
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

    void UpdateGroupStats()
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

        foreach (RectTransform item in FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (!item.name.StartsWith("GroupItem_")) continue;
            if (!int.TryParse(item.name.Substring("GroupItem_".Length), out int gid)) continue;

            Transform labelButton = item.Find("LabelButton");
            Transform countLabel = labelButton != null ? labelButton.Find("CardCountLabel") : null;
            TextMeshProUGUI text = countLabel != null ? countLabel.GetComponent<TextMeshProUGUI>() : null;
            if (text == null) continue;

            int cardCount = cardsByGroup.TryGetValue(gid, out int c) ? c : 0;
            long polyCount = polysByGroup.TryGetValue(gid, out long p) ? p : 0L;
            string cardWord = cardCount == 1 ? "card" : "cards";
            string polyWord = polyCount == 1 ? "poly" : "polys";
            text.text = cardCount.ToString("N0") + " " + cardWord + "  •  " + polyCount.ToString("N0") + " " + polyWord;
        }
    }
}
