using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// Hooks the actual CLUMPER remove button so restoration happens synchronously after
// GroupClumperManager removes the modifier. This avoids relying on a later dictionary
// watcher and restores each affected card from its unclumped canonical GroomState.
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
                button.onClick.AddListener(() => RestoreGroup(capturedGroup));
            }
        }

        hooked.RemoveWhere(b => b == null);
    }

    static void RestoreGroup(int gid)
    {
        HairCard[] cards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        foreach (HairCard card in cards)
        {
            if (card == null || card.groupId != gid) continue;
            HairCard.GroomState state = card.GetCanonicalState();
            card.ApplyEvaluatedState(state);
            card.SetSelectionWeight(0f);
        }
    }
}
