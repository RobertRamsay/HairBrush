using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Hard guarantee for CLUMPER removal: reset the affected group's live meshes on pointer-down,
// before Button.onClick removes the modifier record on pointer-up. This mirrors the proven
// zero-amount behaviour and prevents the last deformed mesh from surviving deletion.
[DefaultExecutionOrder(5180)]
public class ClumperZeroBeforeRemoveAuthority : MonoBehaviour
{
    private GroupClumperManager manager;
    private FieldInfo byGroupField;
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<ClumperZeroBeforeRemoveAuthority>() != null) return;
        GameObject go = new GameObject("ClumperZeroBeforeRemoveAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<ClumperZeroBeforeRemoveAuthority>();
    }

    void Update()
    {
        Resolve();
        if (manager == null || byGroupField == null) return;
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + .08f;
        HookRemoveButtons();
    }

    void Resolve()
    {
        if (manager != null) return;
        manager = FindFirstObjectByType<GroupClumperManager>();
        if (manager == null) return;
        byGroupField = typeof(GroupClumperManager).GetField("byGroup", BindingFlags.Instance | BindingFlags.NonPublic);
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
                if (button == null || button.gameObject.name != "[-]") continue;
                ClumperZeroBeforeRemoveHook hook = button.GetComponent<ClumperZeroBeforeRemoveHook>();
                if (hook == null) hook = button.gameObject.AddComponent<ClumperZeroBeforeRemoveHook>();
                hook.Configure(manager, byGroupField, gid);
            }
        }
    }
}

public class ClumperZeroBeforeRemoveHook : MonoBehaviour, IPointerDownHandler
{
    private GroupClumperManager manager;
    private FieldInfo byGroupField;
    private int groupId;

    public void Configure(GroupClumperManager owner, FieldInfo field, int gid)
    {
        manager = owner;
        byGroupField = field;
        groupId = gid;
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        if (eventData == null || eventData.button != PointerEventData.InputButton.Left) return;
        if (manager == null || byGroupField == null) return;

        var byGroup = byGroupField.GetValue(manager) as Dictionary<int, GroupClumperManager.GroupClumper>;
        if (byGroup != null && byGroup.TryGetValue(groupId, out GroupClumperManager.GroupClumper clumper) && clumper != null)
        {
            // Match the state that visibly resets when a fresh CLUMPER starts at zero.
            clumper.amount = 0f;
            clumper.lastTopologyHash = 0;
            if (clumper.leaders != null) clumper.leaders.Clear();
        }

        HairCard[] cards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        int reset = 0;
        foreach (HairCard card in cards)
        {
            if (card == null || card.groupId != groupId) continue;

            // Group CLUMPER only changes final mesh vertices. Regenerating from the card's
            // current evaluated parameters restores the exact unclumped layer: plain authored
            // state when there are no POSTs, or POST-evaluated state when POSTs are present.
            card.ClearClumpModifier();
            card.GenerateMesh();
            reset++;
        }

        Debug.Log("CLUMPER remove pre-reset for group " + groupId + ": regenerated " + reset + " HairCards before modifier deletion.");
    }
}
