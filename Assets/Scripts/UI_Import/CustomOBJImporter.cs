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

        List<Vector3> sourceVertices = new List<Vector3>();
        List<int> triangles = new List<int>();

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
                sourceVertices.Add(new Vector3(x, y, z));
            }
            else if (parts[0] == "f")
            {
                List<int> faceIndices = new List<int>();
                for (int i = 1; i < parts.Length; i++)
                {
                    string[] vertexData = parts[i].Split('/');
                    int vIndex = int.Parse(vertexData[0]) - 1;
                    faceIndices.Add(vIndex);
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
        if (sourceVertices.Count > 0)
        {
            Bounds sourceBounds = new Bounds(sourceVertices[0], Vector3.zero);
            for (int i = 1; i < sourceVertices.Count; i++) sourceBounds.Encapsulate(sourceVertices[i]);
            sourceCenter = sourceBounds.center;
        }

        // The ModelViewer keeps its existing 180-degree Y viewing orientation. Mirroring X
        // here is the handedness conversion that cancels that orientation's apparent X flip,
        // leaving the editor with the expected left/right layout while changing handedness on Z.
        List<Vector3> vertices = new List<Vector3>(sourceVertices.Count);
        Vector3 convertedCenter = new Vector3(-sourceCenter.x, sourceCenter.y, sourceCenter.z);
        for (int i = 0; i < sourceVertices.Count; i++)
        {
            Vector3 source = sourceVertices[i];
            Vector3 converted = new Vector3(-source.x, source.y, source.z);
            vertices.Add(converted - convertedCenter);
        }

        Mesh mesh = new Mesh();
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.vertices = vertices.ToArray();
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

        MeshFilter mf = go.AddComponent<MeshFilter>();
        MeshRenderer mr = go.AddComponent<MeshRenderer>();
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

        // A dedicated head material (with its own texture) can be provided at
        // Resources/BodyShaderHead.mat - if present, imported models use it directly rather
        // than the generic untextured fallback below. This is specifically for the base head
        // model; a future pass can generalise this per-import once there's more than one.
        Material headMaterial = Resources.Load<Material>("BodyShaderHead");
        if (headMaterial != null)
        {
            mr.material = headMaterial;
            return go;
        }

        Shader defaultShader;
        if (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null)
        {
            defaultShader = Shader.Find("Universal Render Pipeline/Lit");
            if (defaultShader == null) defaultShader = Shader.Find("HDRP/Lit");
        }
        else
        {
            defaultShader = Shader.Find("Standard");
        }

        if (defaultShader != null) mr.material = new Material(defaultShader);
        else Debug.LogWarning("Could not find a standard shader for the current render pipeline.");

        return go;
    }
}
