using UnityEngine;
using System.IO;
using System.Collections.Generic;
using System.Globalization;

// Import metadata is kept on the model root so exported hair can be transformed back into
// the exact coordinate space of the source OBJ (before handedness conversion, recentering
// and normalization).
//
// It is also persisted into the project file - see HairProjectSaveData.importMetadata. That is
// not redundancy: this component is REBUILT on every load by re-importing the OBJ, so it always
// describes today's import rule, never the rule the project was authored under. Without a record
// of what the file was written against, changing the rule silently moves every model out from
// under its own groom.
public class ImportedOBJMetadata : MonoBehaviour
{
    // The measure the normalisation was taken on. Recorded rather than assumed so the measure can
    // be changed later without guessing what an existing project used: a file that names its own
    // rule can be migrated, a file that does not cannot.
    //
    // Bounds width was the original measure and is kept deliberately. It is the more fragile
    // choice - it depends on whether ears are modelled and whether the mesh carries a neck stub -
    // but changing the RULE (always normalise) and the MEASURE in one step would move the working
    // scale of every project that already normalised, not just the ones that did not. Changing
    // the measure later is now a one-line change plus a migration that already exists.
    public const string NormaliseByBoundsWidth = "boundsWidthX";

    public Vector3 originalCenter;
    public float appliedScale = 1f;
    public string sourcePath;
    public bool sourceXMirroredOnImport;
    public bool hasUV0;

    public string normalisationMode = NormaliseByBoundsWidth;
    public float normalisationTarget;
    public float measuredExtent;

    // Identity of the source geometry, independent of the import rule: hashed from the raw OBJ
    // vertex stream before recentering, mirroring or scaling. modelPath is a bare absolute path
    // with no identity of its own, and a project whose model file has been replaced or swapped
    // loads every world-space card against geometry that was never authored for it.
    public int meshHash;
}

public static class CustomOBJImporter
{
    // The working width every imported model is normalised to. This was already the target of the
    // old conditional rescale; what changed is that it now applies to every model rather than only
    // to ones wider than 2.0.
    public const float NormalisedWidth = 0.33f;

    // Identity of the source geometry, hashed from the raw OBJ vertex stream before any of the
    // import conversions. Deliberately taken on sourcePositions rather than the deduplicated
    // vertex list: dedup order follows face order, so it is stable for a given file but would
    // change if the parser ever changed, and this needs to identify the OBJ, not the importer.
    //
    // Same FNV-1a mixing the deterministic card hashes use, over the same ten-thousandth
    // rounding, so a model re-exported with float noise below that threshold still matches.
    static int ComputeMeshHash(List<Vector3> sourcePositions, int triangleIndexCount)
    {
        unchecked
        {
            uint hash = 2166136261u;
            Mix(ref hash, sourcePositions.Count);
            Mix(ref hash, triangleIndexCount);
            for (int i = 0; i < sourcePositions.Count; i++)
            {
                Vector3 p = sourcePositions[i];
                Mix(ref hash, Mathf.RoundToInt(p.x * 10000f));
                Mix(ref hash, Mathf.RoundToInt(p.y * 10000f));
                Mix(ref hash, Mathf.RoundToInt(p.z * 10000f));
            }
            hash ^= hash >> 16;
            hash *= 0x7feb352du;
            hash ^= hash >> 15;
            hash *= 0x846ca68bu;
            hash ^= hash >> 16;
            return (int)(hash & 0x7fffffff);
        }
    }

    static void Mix(ref uint hash, int value)
    {
        unchecked
        {
            hash ^= (uint)value;
            hash *= 16777619u;
        }
    }

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
        metadata.normalisationMode = ImportedOBJMetadata.NormaliseByBoundsWidth;
        metadata.normalisationTarget = NormalisedWidth;
        metadata.meshHash = ComputeMeshHash(sourcePositions, triangles.Count);

        MeshFilter mf = go.AddComponent<MeshFilter>();
        go.AddComponent<MeshRenderer>();
        MeshCollider mc = go.AddComponent<MeshCollider>();
        mf.mesh = mesh;

        // Normalisation is unconditional. It used to fire only above a source width of 2.0, which is
        // a cliff rather than a rule: a head authored at 1.9 units and one at 2.1 landed at working
        // scales an order of magnitude apart, for a reason invisible to whoever authored them. Every
        // absolute value in the app - card length and width, embed depth, every clumper, POST and
        // guide radius, every falloff, the brush radii - is a raw world distance compared against
        // Vector3.Distance, so a consistent working scale is what makes a groom mean the same thing
        // on the next model. It is also what keeps a REMAP between two heads a question of shape
        // rather than of units.
        //
        // Existing projects are not stranded by this: the applied factor and the measure it was
        // taken on now travel in the project file, and RuntimeNavigationProjectIO reconciles an
        // authored scale against the one this produces today - see MigrateImportScale.
        float currentWidth = mesh.bounds.size.x;
        metadata.measuredExtent = currentWidth;
        if (currentWidth > .000001f)
        {
            float scaleFactor = NormalisedWidth / currentWidth;
            go.transform.localScale = new Vector3(scaleFactor, scaleFactor, scaleFactor);
            metadata.appliedScale = scaleFactor;
            Debug.Log($"Normalised, centered and handedness-converted imported model. Source width: {currentWidth:F4}, applied scale factor: {scaleFactor:F4}, mesh hash: {metadata.meshHash}");
        }

        mc.sharedMesh = null;
        mc.sharedMesh = mesh;

        // Ignore source/imported materials. HairBrush owns a single predictable viewport
        // appearance for imported heads and optionally adds a user-selected albedo afterwards.
        ImportedHeadAppearance.ApplyDefaultMaterial(go);

        return go;
    }
}
