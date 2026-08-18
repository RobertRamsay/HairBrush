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

    // Runtime UI can occasionally deliver the same button action twice around a blocking native
    // dialog closing - not just in the same frame, but on a later frame too if the user spent real
    // time interacting with the dialog (e.g. renaming the file) before it closed. A wall-clock
    // cooldown catches that regardless of which frame the duplicate lands on, the same fix already
    // used for the Save Project dialog (see ModelViewer.SaveProject's nextAllowedSaveDialogTime).
    static double nextAllowedExportDialogTime = 0.0;

    public static void ExportInteractive()
    {
        double now = Time.realtimeSinceStartupAsDouble;
        if (now < nextAllowedExportDialogTime) return;
        nextAllowedExportDialogTime = now + 0.75;

        HairCard[] cards = FindExportCards();
        if (cards.Length == 0)
        {
#if UNITY_EDITOR
            EditorUtility.DisplayDialog("Export Hair OBJ", "There are no hair cards to export.", "OK");
#else
            Debug.LogWarning("Export Hair OBJ: there are no hair cards to export.");
#endif
            return;
        }

        ExportSpace space;
        ModelViewer viewer = UnityEngine.Object.FindFirstObjectByType<ModelViewer>();
        GameObject modelRoot = GetLoadedModel(viewer);
        ImportedOBJMetadata metadata = modelRoot != null ? modelRoot.GetComponent<ImportedOBJMetadata>() : null;

#if UNITY_EDITOR
        int choice = EditorUtility.DisplayDialogComplex(
            "Export Hair OBJ",
            "Which coordinate space should the hair use?\n\nMATCH ORIGINAL reverses the model's import handedness conversion, recentering, normalization scale and editor orientation so the hair aligns with the original source OBJ.\n\nCURRENT SCALE exports exactly as the hair currently exists in the editor.",
            "MATCH ORIGINAL",
            "CANCEL",
            "CURRENT SCALE");

        if (choice == 1) return;
        space = choice == 0 ? ExportSpace.OriginalImportedOBJSpace : ExportSpace.CurrentEditorSpace;

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
#else
        // The interactive space-choice dialog has no native Windows equivalent here, so a build
        // always exports at the current scale - the space the hair actually looks like on screen.
        space = ExportSpace.CurrentEditorSpace;
#endif

        string defaultName = "HairCards.obj";
        if (metadata != null && !string.IsNullOrEmpty(metadata.sourcePath))
            defaultName = Path.GetFileNameWithoutExtension(metadata.sourcePath) + "_Hair.obj";

        string path;
#if UNITY_EDITOR
        path = EditorUtility.SaveFilePanel("Export Hair Cards OBJ", "", defaultName, "obj");
#else
        path = RuntimeFileDialog.SaveFile("Export Hair Cards OBJ", "OBJ Files\0*.obj\0All Files\0*.*\0\0", defaultName, "obj");
#endif
        nextAllowedExportDialogTime = Time.realtimeSinceStartupAsDouble + 0.75;
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            WriteOBJ(path, cards, space, modelRoot, metadata);
            int groupCount = cards.Select(c => c.groupId).Distinct().Count();
            Debug.Log($"Exported {cards.Length} hair cards as {groupCount} grouped OBJ objects to: {path} ({space})");
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
#if UNITY_EDITOR
            EditorUtility.DisplayDialog("Export Hair OBJ", "Export failed:\n" + ex.Message, "OK");
#endif
        }
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
        sb.AppendLine("# One OBJ object per Hair Group; cards inside each group are combined.");
        sb.AppendLine("# Space: " + space);
        if (space == ExportSpace.OriginalImportedOBJSpace && metadata != null)
        {
            sb.AppendLine("# Source: " + metadata.sourcePath);
            sb.AppendLine("# Reversed import scale: " + metadata.appliedScale.ToString("R", ci));
            sb.AppendLine("# Reversed import handedness: " + metadata.sourceXMirroredOnImport);
        }

        int vertexBase = 1;
        int uvBase = 1;
        int normalBase = 1;
        int lastGroup = int.MinValue;
        bool reverseWindingForSource = space == ExportSpace.OriginalImportedOBJSpace && metadata != null && metadata.sourceXMirroredOnImport;

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
                sb.AppendLine("o HairGroup_" + card.groupId);
                sb.AppendLine("g HairGroup_" + card.groupId);
                lastGroup = card.groupId;
            }

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
                    Vector3 n = ConvertDirection(worldNormal, space, modelRoot, metadata).normalized;
                    sb.Append("vn ").Append(n.x.ToString("R", ci)).Append(' ')
                      .Append(n.y.ToString("R", ci)).Append(' ')
                      .Append(n.z.ToString("R", ci)).AppendLine();
                }
            }

            for (int i = 0; i + 2 < triangles.Length; i += 3)
            {
                int a = triangles[i];
                int b = reverseWindingForSource ? triangles[i + 2] : triangles[i + 1];
                int c = reverseWindingForSource ? triangles[i + 1] : triangles[i + 2];

                sb.Append("f ");
                AppendFaceVertex(sb, a, vertexBase, uvBase, normalBase, hasUV, hasNormals);
                sb.Append(' ');
                AppendFaceVertex(sb, b, vertexBase, uvBase, normalBase, hasUV, hasNormals);
                sb.Append(' ');
                AppendFaceVertex(sb, c, vertexBase, uvBase, normalBase, hasUV, hasNormals);
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
        sb.Append(v);
        if (hasUV && hasNormals) sb.Append('/').Append(uvBase + localIndex).Append('/').Append(normalBase + localIndex);
        else if (hasUV) sb.Append('/').Append(uvBase + localIndex);
        else if (hasNormals) sb.Append("//").Append(normalBase + localIndex);
    }

    static Vector3 ConvertPosition(Vector3 world, ExportSpace space, GameObject modelRoot, ImportedOBJMetadata metadata)
    {
        if (space != ExportSpace.OriginalImportedOBJSpace || modelRoot == null || metadata == null)
            return world;

        Vector3 importedLocal = modelRoot.transform.InverseTransformPoint(world);
        if (metadata.sourceXMirroredOnImport) importedLocal.x = -importedLocal.x;
        return importedLocal + metadata.originalCenter;
    }

    static Vector3 ConvertDirection(Vector3 worldDirection, ExportSpace space, GameObject modelRoot, ImportedOBJMetadata metadata)
    {
        if (space != ExportSpace.OriginalImportedOBJSpace || modelRoot == null)
            return worldDirection;

        Vector3 importedLocal = modelRoot.transform.InverseTransformDirection(worldDirection);
        if (metadata != null && metadata.sourceXMirroredOnImport) importedLocal.x = -importedLocal.x;
        return importedLocal;
    }
}
