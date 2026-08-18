using UnityEngine;
using System.IO;
using System.Collections.Generic;
using System.Globalization;

// Import metadata is kept on the model root so exported hair can be transformed back into
// the exact coordinate space of the source OBJ (before handedness conversion, recentering
// and normalization).
public class ImportedOBJMetadata : MonoBehaviour
{
    public Vector3 originalCenter;
    public float appliedScale = 1f;
    public string sourcePath;
    public bool sourceXMirroredOnImport;
    public bool hasUV0;
}

public static class CustomOBJImporter
{
    public static GameObject Load(string path)
    {
        if (!File.Exists(path))
        {
            Debug.LogError("File not found: " + path);
            return null;
        }

        List<Vector3> sourcePositions = new List<Vector3>();
        List<Vector2> sourceUVs = new List<Vector2>();
        List<int> triangles = new List<int>();
        bool usedSourceUV = false;

        // OBJ indexes position/UV/normal independently per face-vertex, but Unity's Mesh needs
        // one flat, parallel array per attribute - the same position can legitimately carry a
        // different UV on different faces (e.g. a UV seam), so vertices are deduplicated here by
        // the (position, UV) pair actually used, not by position alone. That's also why UVs were
        // never coming through before: the old parser reused position indices directly into a
        // single vertex list and never read vt lines or the UV index in each face reference at all.
        List<Vector3> dedupPositions = new List<Vector3>();
        List<Vector2> dedupUVs = new List<Vector2>();
        Dictionary<long, int> dedupMap = new Dictionary<long, int>();

        string[] lines = File.ReadAllLines(path);

        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;

            string[] parts = line.Split(new char[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            if (parts[0] == "v")
            {
                float x = float.Parse(parts[1], CultureInfo.InvariantCulture);
                float y = float.Parse(parts[2], CultureInfo.InvariantCulture);
                float z = float.Parse(parts[3], CultureInfo.InvariantCulture);
                sourcePositions.Add(new Vector3(x, y, z));
            }
            else if (parts[0] == "vt")
            {
                float u = float.Parse(parts[1], CultureInfo.InvariantCulture);
                float v = parts.Length > 2 ? float.Parse(parts[2], CultureInfo.InvariantCulture) : 0f;
                sourceUVs.Add(new Vector2(u, v));
            }
            else if (parts[0] == "f")
            {
                List<int> faceIndices = new List<int>();
                for (int i = 1; i < parts.Length; i++)
                {
                    string[] vertexData = parts[i].Split('/');
                    int posIndex = int.Parse(vertexData[0]) - 1;
                    int uvIndex = -1;
                    if (vertexData.Length > 1 && vertexData[1].Length > 0)
                        uvIndex = int.Parse(vertexData[1]) - 1;

                    if (uvIndex >= 0 && uvIndex < sourceUVs.Count)
                        usedSourceUV = true;

                    // Pack both indices into one key - posIndex alone isn't enough since the same
                    // position can appear with a different UV on another face.
                    long key = ((long)(posIndex + 1) << 32) | (uint)(uvIndex + 1);
                    if (!dedupMap.TryGetValue(key, out int localIndex))
                    {
                        localIndex = dedupPositions.Count;
                        dedupPositions.Add(sourcePositions[posIndex]);
                        dedupUVs.Add(uvIndex >= 0 && uvIndex < sourceUVs.Count ? sourceUVs[uvIndex] : Vector2.zero);
                        dedupMap[key] = localIndex;
                    }
                    faceIndices.Add(localIndex);
                }

                // Import converts OBJ/right-handed coordinates to Unity by mirroring X.
                // A reflection reverses winding, so reverse each generated triangle here to
                // keep the source face orientation/normals intact in Unity.
                for (int i = 1; i < faceIndices.Count - 1; i++)
                {
                    triangles.Add(faceIndices[0]);
                    triangles.Add(faceIndices[i + 1]);
                    triangles.Add(faceIndices[i]);
                }
            }
        }

        Vector3 sourceCenter = Vector3.zero;
        if (sourcePositions.Count > 0)
        {
            Bounds sourceBounds = new Bounds(sourcePositions[0], Vector3.zero);
            for (int i = 1; i < sourcePositions.Count; i++) sourceBounds.Encapsulate(sourcePositions[i]);
            sourceCenter = sourceBounds.center;
        }

        // The ModelViewer keeps its existing 180-degree Y viewing orientation. Mirroring X
        // here is the handedness conversion that cancels that orientation's apparent X flip,
        // leaving the editor with the expected left/right layout while changing handedness on Z.
        List<Vector3> vertices = new List<Vector3>(dedupPositions.Count);
        Vector3 convertedCenter = new Vector3(-sourceCenter.x, sourceCenter.y, sourceCenter.z);
        for (int i = 0; i < dedupPositions.Count; i++)
        {
            Vector3 source = dedupPositions[i];
            Vector3 converted = new Vector3(-source.x, source.y, source.z);
            vertices.Add(converted - convertedCenter);
        }

        Mesh mesh = new Mesh();
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.vertices = vertices.ToArray();
        mesh.uv = dedupUVs.ToArray();
        mesh.triangles = triangles.ToArray();
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        GameObject go = new GameObject(Path.GetFileNameWithoutExtension(path));
        go.transform.position = convertedCenter;

        ImportedOBJMetadata metadata = go.AddComponent<ImportedOBJMetadata>();
        metadata.originalCenter = sourceCenter;
        metadata.appliedScale = 1f;
        metadata.sourcePath = path;
        metadata.sourceXMirroredOnImport = true;
        metadata.hasUV0 = usedSourceUV;

        MeshFilter mf = go.AddComponent<MeshFilter>();
        go.AddComponent<MeshRenderer>();
        MeshCollider mc = go.AddComponent<MeshCollider>();
        mf.mesh = mesh;

        float currentWidth = mesh.bounds.size.x;
        if (currentWidth > 0f)
        {
            float targetWidth = 0.33f;
            if (currentWidth > 2.0f)
            {
                float scaleFactor = targetWidth / currentWidth;
                go.transform.localScale = new Vector3(scaleFactor, scaleFactor, scaleFactor);
                metadata.appliedScale = scaleFactor;
                Debug.Log($"Auto-scaled, centered and handedness-converted imported model. Original width: {currentWidth:F2}, applied scale factor: {scaleFactor:F4}");
            }
        }

        mc.sharedMesh = null;
        mc.sharedMesh = mesh;

        // Ignore source/imported materials. HairBrush owns a single predictable viewport
        // appearance for imported heads and optionally adds a user-selected albedo afterwards.
        ImportedHeadAppearance.ApplyDefaultMaterial(go);

        return go;
    }
}
