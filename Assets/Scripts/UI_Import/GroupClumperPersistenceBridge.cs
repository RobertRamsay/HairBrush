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
    private FieldInfo selectedClumperIdField;
    private FieldInfo nextClumperIdField;
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

        foreach (GroupSaveData group in data.groups)
        {
            if (group == null) continue;

            if (group.clumpers == null) group.clumpers = new List<GroupClumperSaveData>();
            else group.clumpers.Clear();
            group.clumper = null;

            List<GroupClumperManager.GroupClumper> runtime = manager.GetGroupClumpers(group.groupId);
            runtime.Sort((a, b) => a.id.CompareTo(b.id));

            foreach (GroupClumperManager.GroupClumper c in runtime)
            {
                if (c == null) continue;
                GroupClumperSaveData saved = ToSave(c);
                group.clumpers.Add(saved);

                // Keep one legacy payload as a graceful fallback for older HairBrush builds.
                if (group.clumper == null) group.clumper = saved;
            }
        }
    }

    static GroupClumperSaveData ToSave(GroupClumperManager.GroupClumper c)
    {
        return new GroupClumperSaveData
        {
            id = c.id,
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

    public static void QueueRestore(HairProjectSaveData data)
    {
        pendingRestore = data;
        queuedCanonicalGeneration = CanonicalProjectStateBridge.CompletedRestoreGeneration;

        // Critical: GroupClumperManager is DontDestroyOnLoad. Never let a clumper from the
        // outgoing project touch cards belonging to the incoming project while restore settles.
        ClearRuntimeImmediately();
    }

    // A model swap that is NOT a project load ends the session this restore belonged to. Without
    // this, loading an OBJ while a project is still settling leaves the payload parked behind the
    // card-count gate, which the user reopens by hand-placing that many cards on the new model -
    // at which point the old project's clumpers install onto it, at the old project's centres.
    public static void CancelPendingRestore()
    {
        pendingRestore = null;
    }

    static void ClearRuntimeImmediately()
    {
        GroupClumperManager manager = FindFirstObjectByType<GroupClumperManager>();
        if (manager == null) return;
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        FieldInfo groupsField = typeof(GroupClumperManager).GetField("byGroup", flags);
        if (groupsField?.GetValue(manager) is IDictionary dict) dict.Clear();
        typeof(GroupClumperManager).GetField("selectedGroup", flags)?.SetValue(manager, -1);
        typeof(GroupClumperManager).GetField("selectedClumperId", flags)?.SetValue(manager, -1);
        typeof(GroupClumperManager).GetField("nextClumperId", flags)?.SetValue(manager, 1);
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
        selectedClumperIdField = typeof(GroupClumperManager).GetField("selectedClumperId", flags);
        nextClumperIdField = typeof(GroupClumperManager).GetField("nextClumperId", flags);
        destroyControlsMethod = typeof(GroupClumperManager).GetMethod("DestroyControls", flags);
        rebuildRowsSoonMethod = typeof(GroupClumperManager).GetMethod("RebuildRowsSoon", flags);
    }

    void Restore(HairProjectSaveData data)
    {
        IDictionary dict = byGroupField.GetValue(manager) as IDictionary;
        if (dict == null) return;
        dict.Clear();

        HashSet<int> usedIds = new HashSet<int>();
        int nextId = 1;

        if (data.groups != null)
        {
            foreach (GroupSaveData group in data.groups)
            {
                if (group == null) continue;

                // New projects store every clumper. Older project JSON has no list field,
                // so fall back to its single clumper payload without requiring a migration.
                bool hasMultiPayload = group.clumpers != null && group.clumpers.Count > 0;
                List<GroupClumperSaveData> saves = hasMultiPayload
                    ? group.clumpers
                    : group.clumper != null
                        ? new List<GroupClumperSaveData> { group.clumper }
                        : null;
                if (saves == null) continue;

                List<GroupClumperManager.GroupClumper> restored = new List<GroupClumperManager.GroupClumper>();
                foreach (GroupClumperSaveData s in saves)
                {
                    if (s == null || !s.enabled) continue;

                    Vector3 normal = new Vector3(s.normalX, s.normalY, s.normalZ);
                    GroupClumperManager.GroupClumper c = new GroupClumperManager.GroupClumper
                    {
                        id = ClaimId(s.id, usedIds, ref nextId),
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
                    restored.Add(c);
                }

                if (restored.Count > 0)
                    dict[group.groupId] = restored;
            }
        }

        // Restored modifiers are available but never actively selected on load. Keep the
        // runtime ID allocator above every restored ID so newly-created points cannot collide.
        selectedGroupField?.SetValue(manager, -1);
        selectedClumperIdField?.SetValue(manager, -1);
        nextClumperIdField?.SetValue(manager, Mathf.Max(1, nextId));
        destroyControlsMethod?.Invoke(manager, null);
        rebuildRowsSoonMethod?.Invoke(manager, null);
    }

    static int ClaimId(int requested, HashSet<int> usedIds, ref int nextId)
    {
        if (requested > 0 && usedIds.Add(requested))
        {
            if (requested >= nextId) nextId = requested + 1;
            return requested;
        }

        while (usedIds.Contains(nextId)) nextId++;
        int id = nextId++;
        usedIds.Add(id);
        return id;
    }
}
