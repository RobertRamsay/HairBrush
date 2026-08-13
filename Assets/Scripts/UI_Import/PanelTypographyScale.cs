using System.Collections.Generic;
using TMPro;
using UnityEngine;

[DefaultExecutionOrder(12000)]
public class PanelTypographyScale : MonoBehaviour
{
    private readonly Dictionary<int, float> sizes = new Dictionary<int, float>();
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
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime;
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
