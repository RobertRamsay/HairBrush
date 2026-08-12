using UnityEngine;

// Current project-format modifier persistence for the active modifier stack.
// Clump is intentionally not restored/saved by the runtime stack for now.
public class ModifierPersistenceBridge : MonoBehaviour
{
    private float nextScan;
    private GroomVarianceController variance;
    private PostAffectorManager postAffectors;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<ModifierPersistenceBridge>() != null) return;
        GameObject go = new GameObject("ModifierPersistenceBridge");
        DontDestroyOnLoad(go);
        go.AddComponent<ModifierPersistenceBridge>();
    }

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + .2f;
        if (variance == null) variance = FindFirstObjectByType<GroomVarianceController>();
        if (postAffectors == null) postAffectors = FindFirstObjectByType<PostAffectorManager>();
        TryRestorePendingProject();
    }

    void TryRestorePendingProject()
    {
        HairProjectSaveData data = HairProjectSaveData.PendingModifierRestore;
        if (data == null || variance == null || postAffectors == null) return;
        int expected = data.hairCards != null ? data.hairCards.Count : 0;
        if (FindObjectsByType<HairCard>(FindObjectsSortMode.None).Length < expected) return;

        // Pre-v2 files stored already-modified visible cards and also stored the recipe
        // that produced them. Replaying that recipe double-applies variance/POST.
        if (data.formatVersion < CanonicalProjectStateBridge.CurrentFormatVersion)
        {
            HairProjectSaveData.PendingModifierRestore = null;
            return;
        }

        HairProjectSaveData.PendingModifierRestore = null;
        variance.ClearSavedSettings();
        postAffectors.ClearAll();
        if (data.groups != null)
            foreach (GroupSaveData g in data.groups)
                RestoreGroup(g);
    }

    public void PopulateGroupSave(GroupSaveData g)
    {
        if (variance != null) g.variances = variance.ExportGroupSettings(g.groupId);
        if (postAffectors != null) g.postAffectors = postAffectors.ExportGroup(g.groupId);

        // Leave any legacy clump DTO empty. Keeping the data class itself means old JSON can
        // still deserialize, but clump no longer participates in the current runtime workflow.
        g.clump = null;
    }

    public void RestoreGroup(GroupSaveData g)
    {
        if (variance != null) variance.ImportGroupSettings(g.groupId, g.variances);
        if (postAffectors != null) postAffectors.ImportGroup(g.groupId, g.postAffectors);
    }
}
