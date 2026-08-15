using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

// CLUMPER is a final live-mesh pass. Track the modifier dictionary itself rather than the
// transient UI row so removal is detected regardless of which control destroyed/rebuilt the
// row. When a group loses its CLUMPER, restore every HairCard in that group from the explicit
// PRE_CLUMP layer snapshot. This is intentionally after all clumper mesh authorities.
[DefaultExecutionOrder(5290)]
public class ClumperRemovalMeshRestoreAuthority : MonoBehaviour
{
    private GroupClumperManager manager;
    private FieldInfo byGroupField;
    private readonly HashSet<int> previousGroups = new HashSet<int>();
    private bool initialized;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<ClumperRemovalMeshRestoreAuthority>() != null) return;
        GameObject go = new GameObject("ClumperRemovalMeshRestoreAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<ClumperRemovalMeshRestoreAuthority>();
    }

    void LateUpdate()
    {
        Resolve();
        if (manager == null || byGroupField == null) return;

        var byGroup = byGroupField.GetValue(manager) as Dictionary<int, GroupClumperManager.GroupClumper>;
        if (byGroup == null) return;

        if (!initialized)
        {
            SyncCurrent(byGroup);
            initialized = true;
            return;
        }

        List<int> removed = null;
        foreach (int gid in previousGroups)
        {
            if (byGroup.ContainsKey(gid)) continue;
            if (removed == null) removed = new List<int>();
            removed.Add(gid);
        }

        if (removed != null)
        {
            foreach (int gid in removed)
                RestoreWholeGroup(gid);
        }

        SyncCurrent(byGroup);
    }

    static void RestoreWholeGroup(int gid)
    {
        // First restore the evaluated state that existed immediately before CLUMPER.
        // With POSTs this is SOURCE + POST; without POSTs it is the plain authored card.
        ModifierEvaluationSnapshots.RestorePreClumpGroup(gid);

        // Rebuild every mesh explicitly as a belt-and-braces step. CLUMPER only writes mesh
        // vertices, so GenerateMesh guarantees no final-pass vertex deformation survives.
        HairCard[] cards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        int restored = 0;
        foreach (HairCard card in cards)
        {
            if (card == null || card.groupId != gid) continue;
            card.ClearClumpModifier();
            card.GenerateMesh();
            card.SetSelectionWeight(0f);
            restored++;
        }

        Debug.Log("CLUMPER removed from group " + gid + ": restored " + restored + " HairCard meshes.");
    }

    void SyncCurrent(Dictionary<int, GroupClumperManager.GroupClumper> byGroup)
    {
        previousGroups.Clear();
        foreach (int gid in byGroup.Keys) previousGroups.Add(gid);
    }

    void Resolve()
    {
        if (manager != null) return;
        manager = FindFirstObjectByType<GroupClumperManager>();
        if (manager == null) return;
        byGroupField = typeof(GroupClumperManager).GetField("byGroup", BindingFlags.Instance | BindingFlags.NonPublic);
        initialized = false;
    }
}
