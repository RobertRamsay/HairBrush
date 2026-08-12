using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Live rendered triangle count for HairCard meshes. The runtime cards are triangle meshes,
// so this is the actual polygon count Unity is drawing rather than a segments estimate.
[DefaultExecutionOrder(1200)]
public class HairPolygonCounter : MonoBehaviour
{
    private GameObject boundPanel;
    private TextMeshProUGUI label;
    private float nextScan;
    private long lastCount = -1;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<HairPolygonCounter>() != null) return;
        GameObject go = new GameObject("HairPolygonCounter");
        DontDestroyOnLoad(go);
        go.AddComponent<HairPolygonCounter>();
    }

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + 0.15f;

        GameObject panel = GameObject.Find("GroupManagerPanel");
        if (panel == null)
        {
            boundPanel = null;
            label = null;
            return;
        }

        if (boundPanel != panel || label == null)
            Bind(panel);
        if (label == null) return;

        long polygons = CountHairPolygons();
        if (polygons == lastCount) return;
        lastCount = polygons;
        label.text = "POLYGONS: " + polygons.ToString("N0");
    }

    void Bind(GameObject panel)
    {
        boundPanel = panel;
        lastCount = -1;

        Transform existing = panel.transform.Find("HairPolygonCounterText");
        if (existing != null)
        {
            label = existing.GetComponent<TextMeshProUGUI>();
            return;
        }

        GameObject go = new GameObject("HairPolygonCounterText", typeof(RectTransform), typeof(LayoutElement), typeof(TextMeshProUGUI));
        go.transform.SetParent(panel.transform, false);

        LayoutElement layout = go.GetComponent<LayoutElement>();
        layout.preferredHeight = 22f;
        layout.minHeight = 22f;

        RectTransform rect = go.GetComponent<RectTransform>();
        rect.sizeDelta = new Vector2(0f, 22f);

        label = go.GetComponent<TextMeshProUGUI>();
        label.text = "POLYGONS: 0";
        label.fontSize = 13f;
        label.fontStyle = FontStyles.Bold;
        label.alignment = TextAlignmentOptions.MidlineLeft;
        label.color = new Color(0.78f, 0.82f, 0.86f, 1f);
        label.raycastTarget = false;

        Transform title = panel.transform.Find("TitleText");
        if (title != null)
            go.transform.SetSiblingIndex(title.GetSiblingIndex() + 1);
    }

    long CountHairPolygons()
    {
        long total = 0;
        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
        {
            if (card == null) continue;
            MeshFilter filter = card.GetComponent<MeshFilter>();
            Mesh mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null) continue;

            for (int sub = 0; sub < mesh.subMeshCount; sub++)
                total += (long)mesh.GetIndexCount(sub) / 3L;
        }
        return total;
    }
}
