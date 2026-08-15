using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

// Persists only the CLUMPER recipe. HairCard save data remains the upstream authored state.
// Load order is explicit: clear stale runtime clumpers -> Group/card canonical restore -> POST restore
// -> canonical completion signal -> install CLUMPER recipes last.
[DefaultExecutionOrder(7000)]
public class GroupClumperPersistenceBridge : MonoBehaviour
{
    private static HairProjectSaveData pendingRestore;
    private static int queuedCanonicalGeneration;

    private GroupClumperManager manager;
    private FieldInfo byGroupField;
    private FieldInfo selectedGroupField;
    private MethodInfo destroyControlsMethod;
    private MethodInfo rebuildRowsSoonMethod;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<GroupClumperPersistenceBridge>() != null) return;
        GameObject go = new GameObject("GroupClumperPersistenceBridge");
        DontDestroyOnLoad(go);
        go.AddComponent<GroupClumperPersistenceBridge>();
    }

    public static void Capture(HairProjectSaveData data)
    {
        if (data?.groups == null) return;
        GroupClumperManager manager = FindFirstObjectByType<GroupClumperManager>();
        if (manager == null) return;

        FieldInfo field = typeof(GroupClumperManager).GetField("byGroup", BindingFlags.Instance | BindingFlags.NonPublic);
        IDictionary dict = field?.GetValue(manager) as IDictionary;
        if (dict == null) return;

        foreach (GroupSaveData group in data.groups)
        {
            if (group == null) continue;
            group.clumper = null;
            if (!dict.Contains(group.groupId)) continue;
            GroupClumperManager.GroupClumper c = dict[group.groupId] as GroupClumperManager.GroupClumper;
            if (c == null) continue;

            group.clumper = new GroupClumperSaveData
            {
                enabled = true,
                mode = (int)c.mode,
                centerX = c.center.x,
                centerY = c.center.y,
                centerZ = c.center.z,
                normalX = c.normal.x,
                normalY = c.normal.y,
                normalZ = c.normal.z,
                amount = c.amount,
                count = c.count,
                seed = c.seed,
                radius = c.radius,
                falloff = c.falloff
            };
        }
    }

    public static void QueueRestore(HairProjectSaveData data)
    {
        pendingRestore = data;
        queuedCanonicalGeneration = CanonicalProjectStateBridge.CompletedRestoreGeneration;

        // Critical: GroupClumperManager is DontDestroyOnLoad. Never let a clumper from the
        // outgoing project touch cards belonging to the incoming project while restore settles.
        ClearRuntimeImmediately();
    }

    static void ClearRuntimeImmediately()
    {
        GroupClumperManager manager = FindFirstObjectByType<GroupClumperManager>();
        if (manager == null) return;
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        FieldInfo groupsField = typeof(GroupClumperManager).GetField("byGroup", flags);
        if (groupsField?.GetValue(manager) is IDictionary dict) dict.Clear();
        typeof(GroupClumperManager).GetField("selectedGroup", flags)?.SetValue(manager, -1);
        typeof(GroupClumperManager).GetMethod("DestroyControls", flags)?.Invoke(manager, null);
        typeof(GroupClumperManager).GetMethod("RebuildRowsSoon", flags)?.Invoke(manager, null);

        foreach (RectTransform row in FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (row != null && row.name.StartsWith("GroupClumper_")) Destroy(row.gameObject);
    }

    void Update()
    {
        Resolve();
        if (pendingRestore == null || manager == null || byGroupField == null) return;

        int expected = pendingRestore.hairCards != null ? pendingRestore.hairCards.Count : 0;
        if (FindObjectsByType<HairCard>(FindObjectsSortMode.None).Length < expected) return;
        if (HairProjectSaveData.PendingModifierRestore != null) return;

        // Current-format projects have a canonical completion signal. Do not let CLUMPER
        // participate until Group -> POST has finished reconstructing the authored cards.
        if (pendingRestore.formatVersion >= CanonicalProjectStateBridge.CurrentFormatVersion &&
            CanonicalProjectStateBridge.CompletedRestoreGeneration <= queuedCanonicalGeneration)
            return;

        HairProjectSaveData data = pendingRestore;
        pendingRestore = null;
        Restore(data);
    }

    void Resolve()
    {
        if (manager != null) return;
        manager = FindFirstObjectByType<GroupClumperManager>();
        if (manager == null) return;
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        byGroupField = typeof(GroupClumperManager).GetField("byGroup", flags);
        selectedGroupField = typeof(GroupClumperManager).GetField("selectedGroup", flags);
        destroyControlsMethod = typeof(GroupClumperManager).GetMethod("DestroyControls", flags);
        rebuildRowsSoonMethod = typeof(GroupClumperManager).GetMethod("RebuildRowsSoon", flags);
    }

    void Restore(HairProjectSaveData data)
    {
        IDictionary dict = byGroupField.GetValue(manager) as IDictionary;
        if (dict == null) return;
        dict.Clear();

        if (data.groups != null)
        {
            foreach (GroupSaveData group in data.groups)
            {
                GroupClumperSaveData s = group?.clumper;
                if (s == null || !s.enabled) continue;

                Vector3 normal = new Vector3(s.normalX, s.normalY, s.normalZ);
                GroupClumperManager.GroupClumper c = new GroupClumperManager.GroupClumper
                {
                    groupId = group.groupId,
                    center = new Vector3(s.centerX, s.centerY, s.centerZ),
                    normal = normal.sqrMagnitude > .000001f ? normal.normalized : Vector3.up,
                    mode = (GroupClumperManager.ClumpMode)Mathf.Clamp(s.mode, 0, 2),
                    amount = Mathf.Clamp01(s.amount),
                    count = Mathf.Clamp(s.count, 1, 24),
                    seed = s.seed,
                    radius = Mathf.Max(.001f, s.radius),
                    falloff = Mathf.Max(0f, s.falloff),
                    leaders = new List<HairCard>(),
                    lastTopologyHash = 0
                };
                dict[group.groupId] = c;
            }
        }

        // Restored modifiers are available but never actively selected on load.
        selectedGroupField?.SetValue(manager, -1);
        destroyControlsMethod?.Invoke(manager, null);
        rebuildRowsSoonMethod?.Invoke(manager, null);
    }
}