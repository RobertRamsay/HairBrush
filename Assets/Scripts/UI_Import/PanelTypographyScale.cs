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

    // The labels this is currently correcting, found on the periodic rescan and re-asserted
    // every frame in between.
    private readonly List<TextMeshProUGUI> tracked = new List<TextMeshProUGUI>();

    // FINDING the labels is throttled. CORRECTING them is not, and the two must stay separate.
    //
    // This authority does not own the only word on font size - it bumps whatever size a label
    // was built with, and several other authorities write their own sizes to the same labels on
    // their own timers: the group header stats, the POST row labels. So it is in a permanent
    // argument with them, and it only looks settled because it used to re-assert every frame and
    // therefore always got the last word.
    //
    // Throttling the whole method - which is what a first pass at the per-frame cost did - let
    // the other writer's size stand for up to a quarter of a second before being corrected, and
    // that is long enough to see. It reads as the text flickering between two sizes, which is
    // exactly what it is.
    //
    // So the expensive half is throttled and the cheap half is not. GameObject.Find plus two
    // GetComponentsInChildren walks happen four times a second; re-asserting a float on a cached
    // list happens every frame and costs a compare per label.
    void LateUpdate()
    {
        if (Time.unscaledTime >= nextScan)
        {
            nextScan = Time.unscaledTime + ScanInterval;
            Rescan();
        }

        for (int i = 0; i < tracked.Count; i++)
        {
            TextMeshProUGUI text = tracked[i];
            if (text == null) continue;

            float target;
            if (!sizes.TryGetValue(text.GetInstanceID(), out target)) continue;
            if (!Mathf.Approximately(text.fontSize, target)) text.fontSize = target;
        }
    }

    void Rescan()
    {
        tracked.Clear();
        Collect(GameObject.Find("GroupManagerPanel"));
        Collect(GameObject.Find("GroomingPanel"));

        // Drop remembered sizes for labels that no longer exist. Group rows are rebuilt often,
        // so without this the dictionary grows for the whole session.
        //
        // Only the DEAD ones, and that distinction is load-bearing: a target is derived once,
        // from the size the label was BUILT with. Dropping a live entry would re-derive it from
        // the already-bumped size and bump it again, and the text would grow a point every time
        // it happened.
        if (sizes.Count <= tracked.Count * 2) return;

        live.Clear();
        for (int i = 0; i < tracked.Count; i++)
        {
            if (tracked[i] != null) live.Add(tracked[i].GetInstanceID());
        }

        stale.Clear();
        foreach (KeyValuePair<int, float> entry in sizes)
        {
            if (!live.Contains(entry.Key)) stale.Add(entry.Key);
        }
        for (int i = 0; i < stale.Count; i++) sizes.Remove(stale[i]);
    }

    private readonly HashSet<int> live = new HashSet<int>();
    private readonly List<int> stale = new List<int>();

    void Collect(GameObject panel)
    {
        if (panel == null) return;
        TextMeshProUGUI[] texts = panel.GetComponentsInChildren<TextMeshProUGUI>(true);
        foreach (TextMeshProUGUI text in texts)
        {
            if (text == null) continue;
            tracked.Add(text);

            int id = text.GetInstanceID();
            if (sizes.ContainsKey(id)) continue;

            float original = text.fontSize;
            sizes[id] = original + (original <= 13f ? 2f : 1f);
        }
    }
}
