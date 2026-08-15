using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// Hooks the actual CLUMPER remove button so restoration happens synchronously after
// GroupClumperManager removes the modifier. Restore the explicit PRE_CLUMP snapshot rather
// than canonical state: if POSTs exist, their evaluated result must remain visible.
[DefaultExecutionOrder(5270)]
public class ClumperRemoveImmediateRestoreAuthority : MonoBehaviour
{
    private readonly HashSet<Button> hooked = new HashSet<Button>();
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<ClumperRemoveImmediateRestoreAuthority>() != null) return;
        GameObject go = new GameObject("ClumperRemoveImmediateRestoreAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<ClumperRemoveImmediateRestoreAuthority>();
    }

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + .08f;
        HookRemoveButtons();
    }

    void HookRemoveButtons()
    {
        RectTransform[] rows = FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (RectTransform row in rows)
        {
            if (row == null || !row.name.StartsWith("GroupClumper_")) continue;
            if (!int.TryParse(row.name.Substring("GroupClumper_".Length), out int gid)) continue;

            Button[] buttons = row.GetComponentsInChildren<Button>(true);
            foreach (Button button in buttons)
            {
                if (button == null || button.gameObject.name != "[-]" || hooked.Contains(button)) continue;
                hooked.Add(button);
                int capturedGroup = gid;
                button.onClick.AddListener(() => ModifierEvaluationSnapshots.RestorePreClumpGroup(capturedGroup));
            }
        }

        hooked.RemoveWhere(b => b == null);
    }
}
