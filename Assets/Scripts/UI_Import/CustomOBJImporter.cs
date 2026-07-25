using UnityEngine;
using System.IO;
using System.Collections.Generic;
using System.Globalization;

public static class CustomOBJImporter
{
    public static GameObject Load(string path)
    {
        if (!File.Exists(path))
        {
            Debug.LogError("File not found: " + path);
            return null;
        }

        List<Vector3> vertices = new List<Vector3>();
        List<int> triangles = new List<int>();

        string[] lines = File.ReadAllLines(path);

        foreach (string line in lines)
        {
            // Skip empty lines or comments
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;

            string[] parts = line.Split(new char[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            if (parts[0] == "v") // Vertex position
            {
                // Parse coordinates (CultureInfo ensures periods are always treated as decimals)
                float x = float.Parse(parts[1], CultureInfo.InvariantCulture);
                float y = float.Parse(parts[2], CultureInfo.InvariantCulture);
                float z = float.Parse(parts[3], CultureInfo.InvariantCulture);

                // OBJ is typically Right-Handed (Z is forward), Unity is Left-Handed. 
                // Inverting X or Z is common. We'll leave it 1:1 for now.
                vertices.Add(new Vector3(x, y, z));
            }
            else if (parts[0] == "f") // Face definitions
            {
                // OBJ faces can be n-gons (triangles, quads, etc.). 
                // Format: f v1/vt1/vn1 v2/vt2/vn2 ...
                List<int> faceIndices = new List<int>();

                for (int i = 1; i < parts.Length; i++)
                {
                    string[] vertexData = parts[i].Split('/');
                    // OBJ indices are 1-based, Unity arrays are 0-based
                    int vIndex = int.Parse(vertexData[0]) - 1;
                    faceIndices.Add(vIndex);
                }

                // Triangulate any polygon using a basic triangle fan (0-1-2, 0-2-3, etc.)
                for (int i = 1; i < faceIndices.Count - 1; i++)
                {
                    triangles.Add(faceIndices[0]);
                    triangles.Add(faceIndices[i]);
                    triangles.Add(faceIndices[i + 1]);
                }
            }
        }

        // Calculate the center of the vertices to recenter the pivot
        Vector3 center = Vector3.zero;
        if (vertices.Count > 0)
        {
            Bounds tempBounds = new Bounds(vertices[0], Vector3.zero);
            for (int i = 1; i < vertices.Count; i++)
            {
                tempBounds.Encapsulate(vertices[i]);
            }
            center = tempBounds.center;

            // Shift all vertices so the bounding box center becomes the local origin (0,0,0)
            for (int i = 0; i < vertices.Count; i++)
            {
                vertices[i] -= center;
            }
        }

        // Build the Unity Mesh
        Mesh mesh = new Mesh();
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32; // Supports >65k vertices
        mesh.vertices = vertices.ToArray();
        mesh.triangles = triangles.ToArray();

        // Let Unity calculate normals so lighting works correctly
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        // Construct the GameObject
        GameObject go = new GameObject(Path.GetFileNameWithoutExtension(path));

        // Offset the GameObject position to compensate for the recentered pivot, 
        // keeping the model visually positioned right where you clicked/imported it.
        go.transform.position = center;

        MeshFilter mf = go.AddComponent<MeshFilter>();
        MeshRenderer mr = go.AddComponent<MeshRenderer>();
        MeshCollider mc = go.AddComponent<MeshCollider>();

        mf.mesh = mesh;

        // Automatically detect scale bounds and normalize using your target width of 0.33
        float currentWidth = mesh.bounds.size.x;
        if (currentWidth > 0f)
        {
            float targetWidth = 0.33f;

            if (currentWidth > 2.0f)
            {
                float scaleFactor = targetWidth / currentWidth;
                go.transform.localScale = new Vector3(scaleFactor, scaleFactor, scaleFactor);
                Debug.Log($"Auto-scaled and centered imported model. Original width: {currentWidth:F2}, applied scale factor: {scaleFactor:F4}");
            }
        }

        // Assign and force-refresh the mesh collider for runtime raycasting
        mc.sharedMesh = null;
        mc.sharedMesh = mesh;

        // Detect the active render pipeline to apply the correct standard material
        Shader defaultShader;
        if (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null)
        {
            // Use the standard URP shader
            defaultShader = Shader.Find("Universal Render Pipeline/Lit");

            // Fallback for HDRP if URP isn't found
            if (defaultShader == null) defaultShader = Shader.Find("HDRP/Lit");
        }
        else
        {
            // Use the classic Built-in Standard shader
            defaultShader = Shader.Find("Standard");
        }

        if (defaultShader != null)
        {
            mr.material = new Material(defaultShader);
        }
        else
        {
            Debug.LogWarning("Could not find a standard shader for the current render pipeline.");
        }

        return go;
    }
}