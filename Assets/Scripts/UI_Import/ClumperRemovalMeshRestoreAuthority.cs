using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

// CLUMPER deforms the live HairCard mesh as a final display pass. When a clumper is removed,
// there is no later clumper evaluation to overwrite that last deformed mesh, so explicitly
// rebuild the affected group's cards from their current evaluated HairCard parameters.
[DefaultExecutionOrder(5265)]
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
            previousGroups.Clear();
            foreach (int gid in byGroup.Keys) previousGroups.Add(gid);
            initialized = true;
            return;
        }

        if (previousGroups.Count > 0)
        {
            List<int> removed = null;
            foreach (int gid in previousGroups)
            {
                if (byGroup.ContainsKey(gid)) continue;
                if (removed == null) removed = new List<int>();
                removed.Add(gid);
            }

            if (removed != null)
            {
                HairCard[] cards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
                foreach (int gid in removed)
                {
                    foreach (HairCard card in cards)
                    {
                        if (card == null || card.groupId != gid) continue;
                        card.GenerateMesh();
                    }
                }
            }
        }

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
