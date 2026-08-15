using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

// Exports the CURRENT evaluated HairCard meshes. Because GroupClumperManager writes its
// final result directly into each MeshFilter, exporting the live mesh naturally captures
// Group -> POST -> CLUMPER exactly as displayed without baking that result back into save data.
public static class HairObjExporter
{
    public enum ExportSpace
    {
        CurrentEditorSpace,
        OriginalImportedOBJSpace
    }

    public static void ExportInteractive()
    {
#if UNITY_EDITOR
        HairCard[] cards = FindExportCards();
        if (cards.Length == 0)
        {
            EditorUtility.DisplayDialog("Export Hair OBJ", "There are no hair cards to export.", "OK");
            return;
        }

        int choice = EditorUtility.DisplayDialogComplex(
            "Export Hair OBJ",
            "Which coordinate space should the hair use?\n\nMATCH ORIGINAL reverses the model's import recentering, normalization scale and editor orientation so the hair aligns with the original source OBJ.\n\nCURRENT SCALE exports exactly as the hair currently exists in the editor.",
            "MATCH ORIGINAL",
            "CANCEL",
            "CURRENT SCALE");

        if (choice == 1) return;
        ExportSpace space = choice == 0 ? ExportSpace.OriginalImportedOBJSpace : ExportSpace.CurrentEditorSpace;

        ModelViewer viewer = UnityEngine.Object.FindFirstObjectByType<ModelViewer>();
        GameObject modelRoot = GetLoadedModel(viewer);
        ImportedOBJMetadata metadata = modelRoot != null ? modelRoot.GetComponent<ImportedOBJMetadata>() : null;

        if (space == ExportSpace.OriginalImportedOBJSpace && (modelRoot == null || metadata == null))
        {
            bool currentInstead = EditorUtility.DisplayDialog(
                "Original Import Transform Unavailable",
                "This model was loaded before import metadata was recorded, so its exact original OBJ coordinate space cannot be reconstructed.\n\nExport at the current editor scale instead?",
                "CURRENT SCALE",
                "CANCEL");
            if (!currentInstead) return;
            space = ExportSpace.CurrentEditorSpace;
        }

        string defaultName = "HairCards.obj";
        if (metadata != null && !string.IsNullOrEmpty(metadata.sourcePath))
            defaultName = Path.GetFileNameWithoutExtension(metadata.sourcePath) + "_Hair.obj";

        string path = EditorUtility.SaveFilePanel("Export Hair Cards OBJ", "", defaultName, "obj");
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            WriteOBJ(path, cards, space, modelRoot, metadata);
            Debug.Log($"Exported {cards.Length} hair cards to: {path} ({space})");
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            EditorUtility.DisplayDialog("Export Hair OBJ", "Export failed:\n" + ex.Message, "OK");
        }
#else
        Debug.LogWarning("OBJ export currently uses the Unity Editor file browser.");
#endif
    }

    static HairCard[] FindExportCards()
    {
        return UnityEngine.Object.FindObjectsByType<HairCard>(FindObjectsSortMode.None)
            .Where(c => c != null && c.GetComponent<MeshFilter>() != null)
            .OrderBy(c => c.groupId)
            .ThenBy(c => Mathf.RoundToInt(c.GetSpawnHitPoint().x * 100000f))
            .ThenBy(c => Mathf.RoundToInt(c.GetSpawnHitPoint().y * 100000f))
            .ThenBy(c => Mathf.RoundToInt(c.GetSpawnHitPoint().z * 100000f))
            .ToArray();
    }

    static GameObject GetLoadedModel(ModelViewer viewer)
    {
        if (viewer == null) return null;
        FieldInfo field = typeof(ModelViewer).GetField("loadedModel", BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(viewer) as GameObject;
    }

    static void WriteOBJ(string path, HairCard[] cards, ExportSpace space, GameObject modelRoot, ImportedOBJMetadata metadata)
    {
        CultureInfo ci = CultureInfo.InvariantCulture;
        StringBuilder sb = new StringBuilder(Mathf.Max(8192, cards.Length * 1500));
        sb.AppendLine("# HairStrandDesigner2 hair-card export");
        sb.AppendLine("# Space: " + space);
        if (space == ExportSpace.OriginalImportedOBJSpace && metadata != null)
        {
            sb.AppendLine("# Source: " + metadata.sourcePath);
            sb.AppendLine("# Reversed import scale: " + metadata.appliedScale.ToString("R", ci));
        }

        int vertexBase = 1;
        int uvBase = 1;
        int normalBase = 1;
        int cardIndex = 0;
        int lastGroup = int.MinValue;

        foreach (HairCard card in cards)
        {
            MeshFilter mf = card.GetComponent<MeshFilter>();
            Mesh mesh = mf != null ? mf.sharedMesh : null;
            if (mesh == null || mesh.vertexCount == 0) continue;

            Vector3[] vertices = mesh.vertices;
            Vector2[] uvs = mesh.uv;
            Vector3[] normals = mesh.normals;
            int[] triangles = mesh.triangles;
            bool hasUV = uvs != null && uvs.Length == vertices.Length;
            bool hasNormals = normals != null && normals.Length == vertices.Length;

            if (card.groupId != lastGroup)
            {
                sb.AppendLine();
                sb.AppendLine("g HairGroup_" + card.groupId);
                lastGroup = card.groupId;
            }
            sb.AppendLine("o HairCard_" + card.groupId + "_" + cardIndex++);

            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 world = card.transform.TransformPoint(vertices[i]);
                Vector3 p = ConvertPosition(world, space, modelRoot, metadata);
                sb.Append("v ").Append(p.x.ToString("R", ci)).Append(' ')
                  .Append(p.y.ToString("R", ci)).Append(' ')
                  .Append(p.z.ToString("R", ci)).AppendLine();
            }

            if (hasUV)
            {
                for (int i = 0; i < uvs.Length; i++)
                {
                    Vector2 uv = uvs[i];
                    sb.Append("vt ").Append(uv.x.ToString("R", ci)).Append(' ')
                      .Append(uv.y.ToString("R", ci)).AppendLine();
                }
            }

            if (hasNormals)
            {
                for (int i = 0; i < normals.Length; i++)
                {
                    Vector3 worldNormal = card.transform.TransformDirection(normals[i]).normalized;
                    Vector3 n = ConvertDirection(worldNormal, space, modelRoot).normalized;
                    sb.Append("vn ").Append(n.x.ToString("R", ci)).Append(' ')
                      .Append(n.y.ToString("R", ci)).Append(' ')
                      .Append(n.z.ToString("R", ci)).AppendLine();
                }
            }

            for (int i = 0; i + 2 < triangles.Length; i += 3)
            {
                AppendFaceVertex(sb, triangles[i], vertexBase, uvBase, normalBase, hasUV, hasNormals);
                sb.Append(' ');
                AppendFaceVertex(sb, triangles[i + 1], vertexBase, uvBase, normalBase, hasUV, hasNormals);
                sb.Append(' ');
                AppendFaceVertex(sb, triangles[i + 2], vertexBase, uvBase, normalBase, hasUV, hasNormals);
                sb.AppendLine();
            }

            vertexBase += vertices.Length;
            if (hasUV) uvBase += uvs.Length;
            if (hasNormals) normalBase += normals.Length;
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    static void AppendFaceVertex(StringBuilder sb, int localIndex, int vertexBase, int uvBase, int normalBase, bool hasUV, bool hasNormals)
    {
        int v = vertexBase + localIndex;
        sb.Append("f ").Append(v);
        if (hasUV && hasNormals) sb.Append('/').Append(uvBase + localIndex).Append('/').Append(normalBase + localIndex);
        else if (hasUV) sb.Append('/').Append(uvBase + localIndex);
        else if (hasNormals) sb.Append("//").Append(normalBase + localIndex);
    }

    static Vector3 ConvertPosition(Vector3 world, ExportSpace space, GameObject modelRoot, ImportedOBJMetadata metadata)
    {
        if (space != ExportSpace.OriginalImportedOBJSpace || modelRoot == null || metadata == null)
            return world;

        // ModelViewer deliberately puts the normalized/recentered imported model at world zero
        // and rotates it 180 degrees. InverseTransformPoint reverses that editor transform and
        // the import scale; adding the original source center reverses vertex recentering.
        return modelRoot.transform.InverseTransformPoint(world) + metadata.originalCenter;
    }

    static Vector3 ConvertDirection(Vector3 worldDirection, ExportSpace space, GameObject modelRoot)
    {
        if (space != ExportSpace.OriginalImportedOBJSpace || modelRoot == null)
            return worldDirection;
        return modelRoot.transform.InverseTransformDirection(worldDirection);
    }
}
