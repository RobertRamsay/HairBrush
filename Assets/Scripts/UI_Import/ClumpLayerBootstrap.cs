using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

// Keeps the clump prototype self-contained: no scene/prefab edit is required.
// Once ModelViewer has built its runtime Canvas this installs a small CLUMP button.
public class ClumpLayerBootstrap : MonoBehaviour
{
    private bool installed;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        GameObject go = new GameObject("ClumpLayerBootstrap");
        DontDestroyOnLoad(go);
        go.AddComponent<ClumpLayerBootstrap>();
    }

    void Update()
    {
        if (installed) return;
        ModelViewer viewer = FindFirstObjectByType<ModelViewer>();
        Canvas canvas = FindObjectsByType<Canvas>(FindObjectsSortMode.None).FirstOrDefault();
        if (viewer == null || canvas == null || viewer.groomingSliderPanelGO == null) return;

        ClumpLayerManager manager = viewer.GetComponent<ClumpLayerManager>();
        if (manager == null) manager = viewer.gameObject.AddComponent<ClumpLayerManager>();
        manager.Init(viewer);
        CreateButton(canvas.transform, manager);
        installed = true;
    }

    void CreateButton(Transform canvas, ClumpLayerManager manager)
    {
        GameObject go = new GameObject("ClumpLayerButton", typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(canvas, false);
        RectTransform r = go.GetComponent<RectTransform>();
        r.anchorMin = r.anchorMax = new Vector2(1f, 1f);
        r.pivot = new Vector2(1f, 1f);
        r.anchoredPosition = new Vector2(-580f, -12f);
        r.sizeDelta = new Vector2(120f, 38f);
        go.GetComponent<Image>().color = new Color(0.12f, 0.35f, 0.16f, 0.95f);
        go.GetComponent<Button>().onClick.AddListener(manager.ToggleForCurrentGroup);

        GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textGO.transform.SetParent(go.transform, false);
        RectTransform tr = textGO.GetComponent<RectTransform>();
        tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one; tr.sizeDelta = Vector2.zero;
        TextMeshProUGUI text = textGO.GetComponent<TextMeshProUGUI>();
        text.text = "CLUMP"; text.fontSize = 15; text.fontStyle = FontStyles.Bold;
        text.alignment = TextAlignmentOptions.Center; text.color = Color.white;
    }
}
