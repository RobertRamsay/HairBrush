using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

// POST-local authoring uses ModelViewer's selection hotspot/selectionWeight machinery.
// The important lifecycle boundary is not "active POST disappeared"; it is
// "this group went from one-or-more POSTs to zero POSTs". At that exact transition
// restore genuine group-root control so subsequent sliders affect every card in the group.
[DefaultExecutionOrder(3600)]
public class FinalPostGroupControlRestore : MonoBehaviour
{
    private PostAffectorManager manager;
    private ModelViewer viewer;
    private FieldInfo groupsField;
    private MethodInfo clearSelectionMethod;
    private readonly Dictionary<int, int> previousCounts = new Dictionary<int, int>();
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<FinalPostGroupControlRestore>() != null) return;
        GameObject go = new GameObject("FinalPostGroupControlRestore");
        DontDestroyOnLoad(go);
        go.AddComponent<FinalPostGroupControlRestore>();
    }

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + 0.05f;
        Resolve();
        if (manager == null || viewer == null || groupsField == null) return;

        object raw = groupsField.GetValue(manager);
        if (raw == null) return;

        // Avoid coupling to PostAffectorManager's private generic type here.
        IDictionary dict = raw as IDictionary;
        if (dict == null) return;

        Dictionary<int, int> current = new Dictionary<int, int>();
        foreach (DictionaryEntry entry in dict)
        {
            if (!(entry.Key is int gid)) continue;
            int count = 0;
            if (entry.Value is ICollection collection) count = collection.Count;
            current[gid] = count;
        }

        HashSet<int> gids = new HashSet<int>(previousCounts.Keys);
        foreach (int gid in current.Keys) gids.Add(gid);

        foreach (int gid in gids)
        {
            int before = previousCounts.TryGetValue(gid, out int b) ? b : 0;
            int now = current.TryGetValue(gid, out int n) ? n : 0;
            if (before > 0 && now == 0)
                RestoreWholeGroupControl(gid);
        }

        previousCounts.Clear();
        foreach (var kv in current) previousCounts[kv.Key] = kv.Value;
    }

    void RestoreWholeGroupControl(int gid)
    {
        // The normal ModelViewer teardown clears yellow/local edit mode and removes
        // falloff/weight UI. Invoke it even if PostAffectorManager already cleared activeId.
        clearSelectionMethod?.Invoke(viewer, null);

        // Belt-and-suspenders: no card in a POST-free group may retain local selection weight.
        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
        {
            if (card != null && card.groupId == gid)
                card.selectionWeight = 0f;
        }

        // Keep the group that just lost its final POST as the active group; do not let
        // cleanup silently bounce ownership elsewhere.
        viewer.currentGroupId = gid;
    }

    void Resolve()
    {
        if (manager == null)
        {
            manager = FindFirstObjectByType<PostAffectorManager>();
            if (manager != null)
                groupsField = typeof(PostAffectorManager).GetField("groups", BindingFlags.Instance | BindingFlags.NonPublic);
        }

        if (viewer == null)
        {
            viewer = FindFirstObjectByType<ModelViewer>();
            if (viewer != null)
                clearSelectionMethod = typeof(ModelViewer).GetMethod("ClearSelectionHotspot", BindingFlags.Instance | BindingFlags.NonPublic);
        }
    }
}
