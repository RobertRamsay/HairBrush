using TMPro;
using UnityEngine;

// Keeps the left-side grooming instructions in sync with the available modifier and placement gestures.
[DefaultExecutionOrder(9500)]
public class ClumperInstructionHintAuthority : MonoBehaviour
{
    private const string ClumperHint = "TAB+CLICK on SURFACE to add a CLUMPER";
    private const string PlacementHint = "SHIFT cycles PLACE / PAINT / SPRAY / ERASE";
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<ClumperInstructionHintAuthority>() != null) return;
        GameObject go = new GameObject("ClumperInstructionHintAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<ClumperInstructionHintAuthority>();
    }

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + .25f;

        TextMeshProUGUI[] labels = FindObjectsByType<TextMeshProUGUI>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (TextMeshProUGUI label in labels)
        {
            if (label == null || string.IsNullOrEmpty(label.text)) continue;
            if (!label.text.Contains("CTRL+CLICK on SURFACE")) continue;

            bool changed = false;
            if (!label.text.Contains(ClumperHint))
            {
                label.text += "\n" + ClumperHint;
                changed = true;
            }
            if (!label.text.Contains(PlacementHint))
            {
                label.text += "\n" + PlacementHint;
                changed = true;
            }
            if (changed) return;
        }
    }
}
