using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

// POST-local authoring uses ModelViewer's selection hotspot/selectionWeight machinery.
// When the final POST disappears, release BOTH legacy selection state and the POST
// manager's cached per-card authority. Otherwise its LateUpdate can keep writing the
// old cached base state over normal group slider edits.
[DefaultExecutionOrder(3600)]
public class FinalPostGroupControlRestore : MonoBehaviour
{
    private PostAffectorManager manager;
    private ModelViewer viewer;
    private FieldInfo groupsField;
    private FieldInfo cardStatesField;
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

        IDictionary dict = groupsField.GetValue(manager) as IDictionary;
        if (dict == null) return;

        Dictionary<int, int> current = new Dictionary<int, int>();
        foreach (DictionaryEntry entry in dict)
        {
            if (!(entry.Key is int gid)) continue;
            int count = entry.Value is ICollection collection ? collection.Count : 0;
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
        // First remove ModelViewer's local-selection mode/weights.
        clearSelectionMethod?.Invoke(viewer, null);
        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
        {
            if (card != null && card.groupId == gid)
                card.SetSelectionWeight(0f);
        }

        // Critical: PostAffectorManager caches a CardState.baseState per HairCard and its
        // LateUpdate writes that state back every frame. With no POST left, those cached
        // entries must be released so ordinary ModelViewer group edits become canonical.
        // IDictionary.Remove(object) works without depending on the manager's private
        // CardState generic type.
        IDictionary cachedStates = cardStatesField?.GetValue(manager) as IDictionary;
        if (cachedStates != null)
        {
            HairCard[] groupCards = FindObjectsByType<HairCard>(FindObjectsSortMode.None)
                .Where(c => c != null && c.groupId == gid).ToArray();
            foreach (HairCard card in groupCards)
                cachedStates.Remove(card);
        }

        viewer.currentGroupId = gid;
    }

    void Resolve()
    {
        if (manager == null)
        {
            manager = FindFirstObjectByType<PostAffectorManager>();
            if (manager != null)
            {
                BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                groupsField = typeof(PostAffectorManager).GetField("groups", flags);
                cardStatesField = typeof(PostAffectorManager).GetField("cardStates", flags);
            }
        }

        if (viewer == null)
        {
            viewer = FindFirstObjectByType<ModelViewer>();
            if (viewer != null)
                clearSelectionMethod = typeof(ModelViewer).GetMethod("ClearSelectionHotspot", BindingFlags.Instance | BindingFlags.NonPublic);
        }
    }
}
