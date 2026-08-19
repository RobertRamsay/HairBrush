using System.Collections;
using System.Reflection;
using TMPro;
using UnityEngine;

// Keeps the compact CLUMPER row summary in sync with the actual modifier state.
// GroupClumperManager owns the data; this is deliberately presentation-only so changing
// SINGLE / EVEN / POINT in the right panel is reflected on the left in the same frame.
[DefaultExecutionOrder(5290)]
public class ClumperRowModeMirror : MonoBehaviour
{
    private GroupClumperManager manager;
    private FieldInfo byGroupField;

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
        if (manager == null || byGroupField == null) return;
        if (byGroupField.GetValue(manager) is not IDictionary dict) return;

        foreach (RectTransform row in FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (row == null || !row.name.StartsWith("GroupClumper_")) continue;
            if (!int.TryParse(row.name.Substring("GroupClumper_".Length), out int groupId)) continue;
            if (!dict.Contains(groupId)) continue;

            GroupClumperManager.GroupClumper clumper = dict[groupId] as GroupClumperManager.GroupClumper;
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
        if (manager == null) return;
        byGroupField = typeof(GroupClumperManager).GetField("byGroup", BindingFlags.Instance | BindingFlags.NonPublic);
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
}
