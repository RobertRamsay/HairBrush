using System.Collections.Generic;
using TMPro;
using UnityEngine;

// Keeps the compact CLUMPER row summary in sync with the actual modifier state.
// GroupClumperManager owns the data; this is deliberately presentation-only so changing
// SINGLE / EVEN / POINT in the right panel is reflected on the left in the same frame.
[DefaultExecutionOrder(5290)]
public class ClumperRowModeMirror : MonoBehaviour
{
    private GroupClumperManager manager;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<ClumperRowModeMirror>() != null) return;
        GameObject go = new GameObject("ClumperRowModeMirror");
        DontDestroyOnLoad(go);
        go.AddComponent<ClumperRowModeMirror>();
    }

    void LateUpdate()
    {
        Resolve();
        if (manager == null) return;

        // THROTTLED. `manager` is an auto-spawned singleton so the guard above never fires, and
        // below it is a walk of Unity's whole object registry reading a name off every object -
        // one managed string allocated per object - which ran every frame whether or not a
        // clumper existed anywhere. The rows it mirrors are edited by hand, so four times a
        // second is well inside what anyone can see.
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + ScanInterval;

        foreach (RectTransform row in FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (row == null || !row.name.StartsWith("GroupClumper_")) continue;

            // The row is named "GroupClumper_{groupId}_{clumperId}". Parsing everything after
            // the prefix produced "0_1", int.TryParse failed, and this loop `continue`d on EVERY
            // row - which is why the SINGLE / EVEN / POINT summary never changed when the mode
            // was switched in the right panel. Match on the CLUMPER id, not the group: a group
            // can hold several clumpers and they do not have to share a mode.
            string[] parts = row.name.Split('_');
            if (parts.Length < 3) continue;
            if (!int.TryParse(parts[2], out int clumperId)) continue;

            GroupClumperManager.GroupClumper clumper = null;
            foreach (GroupClumperManager.GroupClumper candidate in manager.GetAllClumpers())
            {
                if (candidate != null && candidate.id == clumperId) { clumper = candidate; break; }
            }
            if (clumper == null) continue;

            string wanted = ModeShort(clumper.mode);
            TextMeshProUGUI[] texts = row.GetComponentsInChildren<TextMeshProUGUI>(true);
            foreach (TextMeshProUGUI text in texts)
            {
                if (text == null) continue;
                // The CLUMPER button label and remove button are not the summary label.
                if (text.text == "CLUMPER" || text.text == "DEL" || text.text == "[-]") continue;
                if (text.text == "SINGLE" || text.text == "EVEN" || text.text == "POINT")
                {
                    if (text.text != wanted) text.text = wanted;
                    break;
                }
            }
        }
    }

    void Resolve()
    {
        if (manager != null) return;
        manager = FindFirstObjectByType<GroupClumperManager>();
    }

    static string ModeShort(GroupClumperManager.ClumpMode mode)
    {
        return mode switch
        {
            GroupClumperManager.ClumpMode.Singular => "SINGLE",
            GroupClumperManager.ClumpMode.DispersedEvenly => "EVEN",
            _ => "POINT"
        };
    }

    private const float ScanInterval = .25f;
    private float nextScan;
}
