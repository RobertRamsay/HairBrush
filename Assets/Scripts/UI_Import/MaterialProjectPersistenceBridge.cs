using System;
using System.Collections;
using System.IO;
using System.Reflection;
using UnityEngine;

// Persists the runtime hair-material editor without coupling project IO to its private UI model.
// Saved texture files are references only. If a referenced file is unavailable on load, the
// cloned HairCard_dithSdr material simply keeps that slot's built-in/default texture.
[DefaultExecutionOrder(4300)]
public class MaterialProjectPersistenceBridge : MonoBehaviour
{
    public static HairProjectSaveData PendingRestore;

    private HairProjectSaveData pending;
    private int settleFrames;

    private const string AlbedoProperty = "_Albedo";
    private const string NormalProperty = "_Normal";
    private const string OpacityProperty = "_OpacityMask";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Spawn()
    {
        if (FindFirstObjectByType<MaterialProjectPersistenceBridge>() != null) return;
        GameObject go = new GameObject("MaterialProjectPersistenceBridge");
        DontDestroyOnLoad(go);
        go.AddComponent<MaterialProjectPersistenceBridge>();
    }

    public static void Capture(HairProjectSaveData data)
    {
        if (data == null) return;
        data.hairMaterials ??= new System.Collections.Generic.List<HairMaterialSaveData>();
        data.groupMaterials ??= new System.Collections.Generic.List<GroupMaterialSaveData>();
        data.hairMaterials.Clear();
        data.groupMaterials.Clear();

        MaterialEditorManager editor = FindFirstObjectByType<MaterialEditorManager>();
        if (editor == null) return;

        Type type = typeof(MaterialEditorManager);
        FieldInfo materialsField = type.GetField("materials", BindingFlags.Instance | BindingFlags.NonPublic);
        FieldInfo groupsField = type.GetField("groupMaterial", BindingFlags.Instance | BindingFlags.NonPublic);

        if (materialsField?.GetValue(editor) is IList materials)
        {
            foreach (object entry in materials)
            {
                if (entry == null) continue;
                Type et = entry.GetType();
                data.hairMaterials.Add(new HairMaterialSaveData
                {
                    name = et.GetField("name")?.GetValue(entry) as string,
                    albedoPath = et.GetField("albedoPath")?.GetValue(entry) as string,
                    normalPath = et.GetField("normalPath")?.GetValue(entry) as string,
                    opacityPath = et.GetField("opacityPath")?.GetValue(entry) as string
                });
            }
        }

        if (groupsField?.GetValue(editor) is IDictionary groups)
        {
            foreach (DictionaryEntry pair in groups)
            {
                if (pair.Key is int groupId && pair.Value is int materialIndex)
                    data.groupMaterials.Add(new GroupMaterialSaveData { groupId = groupId, materialIndex = materialIndex });
            }
        }
    }

    private void Update()
    {
        if (PendingRestore != null)
        {
            pending = PendingRestore;
            PendingRestore = null;
            settleFrames = 0;
        }
        if (pending == null) return;

        MaterialEditorManager editor = FindFirstObjectByType<MaterialEditorManager>();
        if (editor == null) return;

        FieldInfo sourceField = typeof(MaterialEditorManager).GetField("sourceMaterial", BindingFlags.Instance | BindingFlags.NonPublic);
        if (!(sourceField?.GetValue(editor) is Material source) || source == null) return;

        int expectedCards = pending.hairCards != null ? pending.hairCards.Count : 0;
        if (FindObjectsByType<HairCard>(FindObjectsSortMode.None).Length < expectedCards) return;

        // Let the normal project loader finish creating cards/groups before material assignment wins.
        if (++settleFrames < 2) return;

        Restore(editor, source, pending);
        pending = null;
    }

    private static void Restore(MaterialEditorManager editor, Material source, HairProjectSaveData data)
    {
        Type type = typeof(MaterialEditorManager);
        FieldInfo materialsField = type.GetField("materials", BindingFlags.Instance | BindingFlags.NonPublic);
        FieldInfo groupsField = type.GetField("groupMaterial", BindingFlags.Instance | BindingFlags.NonPublic);
        FieldInfo selectedField = type.GetField("selectedMaterialIndex", BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo createEntry = type.GetMethod("CreateEntry", BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo apply = type.GetMethod("ApplyAssignments", BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo preview = type.GetMethod("UpdatePreviewForSelectedMaterial", BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo refresh = type.GetMethod("RefreshPanel", BindingFlags.Instance | BindingFlags.NonPublic);

        if (!(materialsField?.GetValue(editor) is IList materials) || createEntry == null) return;

        // Destroy only runtime clones. The source/template asset remains untouched.
        foreach (object oldEntry in materials)
        {
            if (oldEntry == null) continue;
            Material oldMaterial = oldEntry.GetType().GetField("material")?.GetValue(oldEntry) as Material;
            if (oldMaterial != null && oldMaterial != source) Destroy(oldMaterial);
        }
        materials.Clear();

        bool hasSavedMaterials = data.hairMaterials != null && data.hairMaterials.Count > 0;
        int count = hasSavedMaterials ? data.hairMaterials.Count : 1;
        for (int i = 0; i < count; i++)
        {
            HairMaterialSaveData saved = hasSavedMaterials ? data.hairMaterials[i] : null;
            string name = !string.IsNullOrWhiteSpace(saved?.name) ? saved.name : "Mat " + (i + 1);
            object entry = createEntry.Invoke(editor, new object[] { name, source });
            if (entry == null) continue;

            if (saved != null)
            {
                // Texture2D's final constructor argument is "linear". Albedo is colour data,
                // while normal/opacity are data textures and must stay linear.
                TryRestoreTexture(entry, AlbedoProperty, "albedoPath", saved.albedoPath, false);
                TryRestoreTexture(entry, NormalProperty, "normalPath", saved.normalPath, true);
                TryRestoreTexture(entry, OpacityProperty, "opacityPath", saved.opacityPath, true);
            }
            materials.Add(entry);
        }

        if (materials.Count == 0)
            materials.Add(createEntry.Invoke(editor, new object[] { "Mat 1", source }));

        if (groupsField?.GetValue(editor) is IDictionary groups)
        {
            groups.Clear();
            if (data.groupMaterials != null)
            {
                foreach (GroupMaterialSaveData saved in data.groupMaterials)
                {
                    if (saved == null) continue;
                    int index = Mathf.Clamp(saved.materialIndex, 0, materials.Count - 1);
                    groups[saved.groupId] = index;
                }
            }

            ModelViewer viewer = FindFirstObjectByType<ModelViewer>();
            int selected = 0;
            if (viewer != null && groups.Contains(viewer.currentGroupId))
                selected = (int)groups[viewer.currentGroupId];
            selectedField?.SetValue(editor, Mathf.Clamp(selected, 0, materials.Count - 1));
        }
        else selectedField?.SetValue(editor, 0);

        apply?.Invoke(editor, null);
        preview?.Invoke(editor, null);
        refresh?.Invoke(editor, null);
    }

    private static void TryRestoreTexture(object entry, string propertyName, string pathFieldName, string path, bool linear)
    {
        // Missing references deliberately mean: keep the default texture inherited from sourceMaterial.
        if (entry == null || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            if (!string.IsNullOrWhiteSpace(path))
                Debug.LogWarning("Hair material texture not found; using default for " + propertyName + ": " + path);
            return;
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear);
            texture.name = Path.GetFileNameWithoutExtension(path);
            if (!texture.LoadImage(bytes, false))
            {
                Destroy(texture);
                Debug.LogWarning("Could not decode saved hair texture; using default for " + propertyName + ": " + path);
                return;
            }

            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;

            Type et = entry.GetType();
            Material material = et.GetField("material")?.GetValue(entry) as Material;
            if (material == null || !material.HasProperty(propertyName))
            {
                Destroy(texture);
                return;
            }

            material.SetTexture(propertyName, texture);
            et.GetField(pathFieldName)?.SetValue(entry, path);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("Could not restore saved hair texture; using default for " + propertyName + ": " + ex.Message);
        }
    }
}