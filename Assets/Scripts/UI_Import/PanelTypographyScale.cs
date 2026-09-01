using System.Collections.Generic;
using TMPro;
using UnityEngine;

[DefaultExecutionOrder(12000)]
public class PanelTypographyScale : MonoBehaviour
{
    private readonly Dictionary<int, float> sizes = new Dictionary<int, float>();
    private const float ScanInterval = .25f;
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<PanelTypographyScale>() != null) return;
        GameObject go = new GameObject("PanelTypographyScale");
        DontDestroyOnLoad(go);
        go.AddComponent<PanelTypographyScale>();
    }

    void LateUpdate()
    {
        // The interval was missing: `nextScan = Time.unscaledTime` sets the next scan to NOW,
        // so the guard above was always already satisfied and the throttle never engaged once.
        // Two GameObject.Find calls plus two GetComponentsInChildren<TextMeshProUGUI>(true)
        // walks were running every frame, for a restyle whose answer changes only when a panel
        // is rebuilt.
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + ScanInterval;
        ScalePanel(GameObject.Find("GroupManagerPanel"));
        ScalePanel(GameObject.Find("GroomingPanel"));
    }

    void ScalePanel(GameObject panel)
    {
        if (panel == null) return;
        TextMeshProUGUI[] texts = panel.GetComponentsInChildren<TextMeshProUGUI>(true);
        foreach (TextMeshProUGUI text in texts)
        {
            if (text == null) continue;
            int id = text.GetInstanceID();
            float target;
            if (!sizes.TryGetValue(id, out target))
            {
                float original = text.fontSize;
                target = original + (original <= 13f ? 2f : 1f);
                sizes[id] = target;
            }
            if (!Mathf.Approximately(text.fontSize, target)) text.fontSize = target;
        }
    }
}
