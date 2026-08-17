using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

// Owns predetermined UV rectangles per hair material. The Texture UV workspace remains the
// editor for one set at a time; switching material stores the outgoing cuts and loads the
// incoming material's cuts. Group/PRE consumers ask this authority for the cuts belonging to
// the material actually assigned to that group rather than reading one project-global list.
[DefaultExecutionOrder(9180)]
public class MaterialUVRectAuthority : MonoBehaviour
{
    private static HairProjectSaveData pendingRestore;

    private readonly Dictionary<int, List<UVRectSaveData>> rectsByMaterial = new();

    private MaterialEditorManager editor;
    private TextureUVRectWorkspace workspace;
    private FieldInfo materialsField;
    private FieldInfo groupMaterialField;
    private FieldInfo selectedMaterialField;

    private int trackedSelected = -1;
    private int trackedFirstMaterialId;
    private int lastWorkspaceSignature = int.MinValue;
    private int pendingSettleFrames;
    private bool importingWorkspace;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<MaterialUVRectAuthority>() != null) return;
        GameObject go = new GameObject("MaterialUVRectAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<MaterialUVRectAuthority>();
    }

    public static void Capture(HairProjectSaveData data)
    {
        if (data == null) return;
        MaterialUVRectAuthority authority = FindFirstObjectByType<MaterialUVRectAuthority>();
        if (authority == null) return;
        authority.CaptureInto(data);
    }

    public static void QueueRestore(HairProjectSaveData data)
    {
        pendingRestore = data;
        MaterialUVRectAuthority authority = FindFirstObjectByType<MaterialUVRectAuthority>();
        if (authority != null) authority.pendingSettleFrames = 0;
    }

    // True means material routing is available even when the returned list is deliberately
    // empty. Callers should only fall back to the old global workspace when this returns false.
    public static bool TryGetRectsForGroup(int groupId, out List<UVRectSaveData> rects)
    {
        rects = new List<UVRectSaveData>();
        MaterialUVRectAuthority authority = FindFirstObjectByType<MaterialUVRectAuthority>();
        if (authority == null) return false;
        authority.Resolve();
        if (authority.editor == null) return false;

        // Never expose the previous project's material atlas while the material list is being
        // rebuilt. Returning true+empty tells PRE consumers to wait rather than fall back global.
        if (pendingRestore != null) return true;

        authority.SyncMaterialGeneration();
        authority.SyncWorkspaceSelection();
        rects = authority.GetRectsForMaterial(authority.GetMaterialIndexForGroup(groupId));
        return true;
    }

    public static bool TryGetSelectedRects(out List<UVRectSaveData> rects)
    {
        rects = new List<UVRectSaveData>();
        MaterialUVRectAuthority authority = FindFirstObjectByType<MaterialUVRectAuthority>();
        if (authority == null) return false;
        authority.Resolve();
        if (authority.editor == null) return false;
        if (pendingRestore != null) return true;
        authority.SyncMaterialGeneration();
        authority.SyncWorkspaceSelection();
        int selected = authority.GetSelectedMaterialIndex();
        rects = authority.GetRectsForMaterial(selected);
        return true;
    }

    // AUTO can commit immediately instead of waiting one Update for signature polling.
    public static void StoreSelectedWorkspaceNow()
    {
        if (pendingRestore != null) return;
        MaterialUVRectAuthority authority = FindFirstObjectByType<MaterialUVRectAuthority>();
        if (authority == null) return;
        authority.Resolve();
        if (authority.editor == null || authority.workspace == null) return;
        authority.SyncMaterialGeneration();
        int selected = authority.GetSelectedMaterialIndex();
        authority.StoreWorkspaceFor(selected);
        authority.trackedSelected = selected;
    }

    void Update()
    {
        Resolve();
        if (editor == null || workspace == null) return;

        SyncMaterialGeneration();
        if (TryRestorePending()) return;
        SyncWorkspaceSelection();
    }

    void Resolve()
    {
        if (editor == null)
        {
            editor = FindFirstObjectByType<MaterialEditorManager>();
            if (editor != null)
            {
                BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                System.Type type = typeof(MaterialEditorManager);
                materialsField = type.GetField("materials", flags);
                groupMaterialField = type.GetField("groupMaterial", flags);
                selectedMaterialField = type.GetField("selectedMaterialIndex", flags);
            }
        }

        if (workspace == null)
            workspace = FindFirstObjectByType<TextureUVRectWorkspace>();
    }

    IList GetMaterials()
    {
        return materialsField?.GetValue(editor) as IList;
    }

    int GetSelectedMaterialIndex()
    {
        IList materials = GetMaterials();
        if (materials == null || materials.Count == 0) return -1;
        int selected = selectedMaterialField?.GetValue(editor) is int value ? value : 0;
        return Mathf.Clamp(selected, 0, materials.Count - 1);
    }

    int GetMaterialIndexForGroup(int groupId)
    {
        IList materials = GetMaterials();
        if (materials == null || materials.Count == 0) return -1;

        int index = 0;
        if (groupMaterialField?.GetValue(editor) is IDictionary groups && groups.Contains(groupId))
        {
            object raw = groups[groupId];
            if (raw is int assigned) index = assigned;
        }
        return Mathf.Clamp(index, 0, materials.Count - 1);
    }

    int FirstMaterialInstanceId(IList materials)
    {
        if (materials == null || materials.Count == 0 || materials[0] == null) return 0;
        Material material = materials[0].GetType().GetField("material")?.GetValue(materials[0]) as Material;
        return material != null ? material.GetInstanceID() : 0;
    }

    void SyncMaterialGeneration()
    {
        IList materials = GetMaterials();
        if (materials == null || materials.Count == 0) return;

        int firstId = FirstMaterialInstanceId(materials);
        if (trackedFirstMaterialId != 0 && firstId != 0 && firstId != trackedFirstMaterialId)
        {
            // MaterialEditorManager.Init/project restore rebuilt the material collection. A
            // same-sized list is still a new generation, so don't leak cuts from the old session.
            rectsByMaterial.Clear();
            trackedSelected = -1;
            lastWorkspaceSignature = int.MinValue;
        }
        if (firstId != 0) trackedFirstMaterialId = firstId;

        for (int i = 0; i < materials.Count; i++)
            if (!rectsByMaterial.ContainsKey(i)) rectsByMaterial[i] = new List<UVRectSaveData>();

        foreach (int stale in rectsByMaterial.Keys.Where(index => index < 0 || index >= materials.Count).ToArray())
            rectsByMaterial.Remove(stale);
    }

    void SyncWorkspaceSelection()
    {
        if (workspace == null || pendingRestore != null) return;
        int selected = GetSelectedMaterialIndex();
        if (selected < 0) return;

        if (trackedSelected < 0)
        {
            trackedSelected = selected;

            // On first attachment to a legacy/live session, preserve whatever was already in
            // the old global workspace as this material's starting set rather than erasing it.
            List<UVRectSaveData> current = workspace.ExportDefinitions();
            if (rectsByMaterial.TryGetValue(selected, out List<UVRectSaveData> stored) &&
                stored.Count == 0 && current.Count > 0)
                rectsByMaterial[selected] = Clone(current);
            else
                ImportMaterialIntoWorkspace(selected);
            return;
        }

        if (selected != trackedSelected)
        {
            StoreWorkspaceFor(trackedSelected);
            trackedSelected = selected;
            ImportMaterialIntoWorkspace(selected);
            return;
        }

        if (importingWorkspace) return;
        List<UVRectSaveData> live = workspace.ExportDefinitions();
        int signature = Signature(live);
        if (signature == lastWorkspaceSignature) return;

        rectsByMaterial[selected] = Clone(live);
        lastWorkspaceSignature = signature;
    }

    void StoreWorkspaceFor(int materialIndex)
    {
        if (workspace == null || materialIndex < 0) return;
        List<UVRectSaveData> live = workspace.ExportDefinitions();
        rectsByMaterial[materialIndex] = Clone(live);
        lastWorkspaceSignature = Signature(live);
    }

    void ImportMaterialIntoWorkspace(int materialIndex)
    {
        if (workspace == null || materialIndex < 0) return;
        if (!rectsByMaterial.TryGetValue(materialIndex, out List<UVRectSaveData> source))
            source = new List<UVRectSaveData>();

        importingWorkspace = true;
        try
        {
            List<UVRectSaveData> copy = Clone(source);
            workspace.ImportDefinitions(copy);
            lastWorkspaceSignature = Signature(copy);
        }
        finally { importingWorkspace = false; }
    }

    List<UVRectSaveData> GetRectsForMaterial(int materialIndex)
    {
        if (materialIndex < 0) return new List<UVRectSaveData>();
        if (!rectsByMaterial.TryGetValue(materialIndex, out List<UVRectSaveData> source))
            return new List<UVRectSaveData>();
        return Clone(source).OrderBy(rect => rect.id).ToList();
    }

    void CaptureInto(HairProjectSaveData data)
    {
        Resolve();
        if (editor == null) return;
        SyncMaterialGeneration();

        int selected = GetSelectedMaterialIndex();
        if (workspace != null && selected >= 0) StoreWorkspaceFor(selected);

        if (data.hairMaterials == null) return;
        for (int i = 0; i < data.hairMaterials.Count; i++)
        {
            HairMaterialSaveData saved = data.hairMaterials[i];
            if (saved == null) continue;
            saved.uvRects = GetRectsForMaterial(i);
        }
    }

    bool TryRestorePending()
    {
        if (pendingRestore == null) return false;
        HairProjectSaveData data = pendingRestore;
        IList materials = GetMaterials();
        if (materials == null || materials.Count == 0) return true;

        int expectedMaterials = data.hairMaterials != null && data.hairMaterials.Count > 0
            ? data.hairMaterials.Count : 1;
        int expectedCards = data.hairCards != null ? data.hairCards.Count : 0;
        if (materials.Count != expectedMaterials ||
            FindObjectsByType<HairCard>(FindObjectsSortMode.None).Length < expectedCards)
        {
            pendingSettleFrames = 0;
            return true;
        }

        // MaterialProjectPersistenceBridge runs earlier (4300) and itself waits for card
        // reconstruction. A few stable frames guarantee we read the rebuilt material list.
        if (++pendingSettleFrames < 4) return true;

        rectsByMaterial.Clear();
        bool hasPerMaterialCuts = data.hairMaterials != null &&
            data.hairMaterials.Any(item => item != null && item.uvRects != null && item.uvRects.Count > 0);

        for (int i = 0; i < materials.Count; i++)
        {
            List<UVRectSaveData> source = null;
            if (data.hairMaterials != null && i < data.hairMaterials.Count && data.hairMaterials[i] != null)
                source = data.hairMaterials[i].uvRects;

            // Old projects had one global list. Copy it to every material so their visual
            // behaviour remains identical until the user authors material-specific cuts.
            if (!hasPerMaterialCuts && (source == null || source.Count == 0))
                source = data.uvRects;

            rectsByMaterial[i] = Clone(source);
        }

        trackedFirstMaterialId = FirstMaterialInstanceId(materials);
        trackedSelected = GetSelectedMaterialIndex();
        pendingRestore = null;
        pendingSettleFrames = 0;
        ImportMaterialIntoWorkspace(trackedSelected);
        return false;
    }

    static List<UVRectSaveData> Clone(IEnumerable<UVRectSaveData> source)
    {
        if (source == null) return new List<UVRectSaveData>();
        return source.Where(item => item != null).Select(item => new UVRectSaveData
        {
            id = item.id,
            uMin = item.uMin,
            vMin = item.vMin,
            uMax = item.uMax,
            vMax = item.vMax
        }).ToList();
    }

    static int Signature(IEnumerable<UVRectSaveData> source)
    {
        unchecked
        {
            int hash = 17;
            if (source == null) return hash;
            foreach (UVRectSaveData rect in source.Where(item => item != null).OrderBy(item => item.id))
            {
                hash = hash * 31 + rect.id;
                hash = hash * 31 + Mathf.RoundToInt(rect.uMin * 100000f);
                hash = hash * 31 + Mathf.RoundToInt(rect.vMin * 100000f);
                hash = hash * 31 + Mathf.RoundToInt(rect.uMax * 100000f);
                hash = hash * 31 + Mathf.RoundToInt(rect.vMax * 100000f);
            }
            return hash;
        }
    }
}
