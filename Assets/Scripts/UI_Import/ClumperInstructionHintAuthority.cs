using TMPro;
using UnityEngine;

// Keeps the left-side grooming instructions in sync with the available modifier gestures.
[DefaultExecutionOrder(9500)]
public class ClumperInstructionHintAuthority : MonoBehaviour
{
    private const string Hint = "TAB+CLICK on SURFACE to add a CLUMPER";
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
            if (!label.text.Contains("CTRL+CLICK on SURFACE") || label.text.Contains(Hint)) continue;

            string[] lines = label.text.Split('\n');
            int insertAfter = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("CTRL+CLICK on SURFACE"))
                {
                    insertAfter = i;
                    break;
                }
            }

            if (insertAfter < 0)
            {
                label.text += "\n" + Hint;
                return;
            }

            string[] updated = new string[lines.Length + 1];
            for (int i = 0; i <= insertAfter; i++) updated[i] = lines[i];
            updated[insertAfter + 1] = Hint;
            for (int i = insertAfter + 1; i < lines.Length; i++) updated[i + 1] = lines[i];
            label.text = string.Join("\n", updated);
            return;
        }
    }
}
