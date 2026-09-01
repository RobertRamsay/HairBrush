using System.Collections.Generic;
using UnityEngine;

// Reusable connected-surface scope for HairBrush.
// CustomOBJImporter preserves source position indices, so triangle connectivity can be
// flood-filled once from shared vertex indices and then queried cheaply by raycast hit.
public static class SurfaceIslandScope
{
    private sealed class MeshCache
    {
        public Mesh mesh;
        public int[] triangleIsland;
        public int islandCount;
    }

    private static readonly Dictionary<Mesh, MeshCache> caches = new Dictionary<Mesh, MeshCache>();
    private static readonly Dictionary<int, bool> clumperContiguous = new Dictionary<int, bool>();

    public static bool IsClumperContiguous(int groupId)
    {
        return clumperContiguous.TryGetValue(groupId, out bool enabled) && enabled;
    }

    public static void SetClumperContiguous(int groupId, bool enabled)
    {
        clumperContiguous[groupId] = enabled;
    }

    public static bool TryGetIsland(RaycastHit hit, out int islandId)
    {
        islandId = -1;
        MeshCollider collider = hit.collider as MeshCollider;
        if (collider == null || collider.sharedMesh == null || hit.triangleIndex < 0) return false;
        MeshCache cache = GetCache(collider.sharedMesh);
        if (cache == null || hit.triangleIndex >= cache.triangleIsland.Length) return false;
        islandId = cache.triangleIsland[hit.triangleIndex];
        return islandId >= 0;
    }

    public static bool TryGetIslandAtWorldPoint(Vector3 point, Vector3 normal, out int islandId)
    {
        islandId = -1;
        Vector3 n = normal.sqrMagnitude > .000001f ? normal.normalized : Vector3.up;
        const float probe = .025f;

        // Probe both directions because imported winding/collider orientation should not
        // matter to island lookup and roots may sit slightly embedded in the surface.
        Ray a = new Ray(point + n * probe, -n);
        if (Physics.Raycast(a, out RaycastHit hitA, probe * 2f + .002f) && TryGetIsland(hitA, out islandId))
            return true;

        Ray b = new Ray(point - n * probe, n);
        if (Physics.Raycast(b, out RaycastHit hitB, probe * 2f + .002f) && TryGetIsland(hitB, out islandId))
            return true;

        return false;
    }

    public static bool TryGetCardIsland(HairCard card, out int islandId)
    {
        islandId = -1;
        if (card == null) return false;
        HairCardSurfaceIsland tag = card.GetComponent<HairCardSurfaceIsland>();
        if (tag != null && tag.islandId >= 0)
        {
            islandId = tag.islandId;
            return true;
        }

        if (!TryGetIslandAtWorldPoint(card.GetSpawnHitPoint(), card.GetSurfaceNormal(), out islandId)) return false;
        if (tag == null) tag = card.gameObject.AddComponent<HairCardSurfaceIsland>();
        tag.islandId = islandId;
        return true;
    }

    public static bool SameIsland(HairCard card, int islandId)
    {
        return islandId >= 0 && TryGetCardIsland(card, out int cardIsland) && cardIsland == islandId;
    }

    private static MeshCache GetCache(Mesh mesh)
    {
        if (mesh == null) return null;
        if (caches.TryGetValue(mesh, out MeshCache existing) && existing != null) return existing;

        int[] triangles = mesh.triangles;
        int triangleCount = triangles.Length / 3;
        MeshCache cache = new MeshCache
        {
            mesh = mesh,
            triangleIsland = new int[triangleCount],
            islandCount = 0
        };
        for (int i = 0; i < cache.triangleIsland.Length; i++) cache.triangleIsland[i] = -1;

        // Vertex -> touching triangles. Sharing any source vertex means the triangles belong
        // to the same contiguous component. This is ideal for the OBJ importer because it
        // keeps source position indices intact while ignoring UV/normal seam duplication.
        Dictionary<int, List<int>> byVertex = new Dictionary<int, List<int>>();
        for (int tri = 0; tri < triangleCount; tri++)
        {
            for (int corner = 0; corner < 3; corner++)
            {
                int vertex = triangles[tri * 3 + corner];
                if (!byVertex.TryGetValue(vertex, out List<int> touching))
                {
                    touching = new List<int>();
                    byVertex[vertex] = touching;
                }
                touching.Add(tri);
            }
        }

        Queue<int> queue = new Queue<int>();
        for (int seed = 0; seed < triangleCount; seed++)
        {
            if (cache.triangleIsland[seed] >= 0) continue;
            int island = cache.islandCount++;
            cache.triangleIsland[seed] = island;
            queue.Enqueue(seed);

            while (queue.Count > 0)
            {
                int tri = queue.Dequeue();
                for (int corner = 0; corner < 3; corner++)
                {
                    int vertex = triangles[tri * 3 + corner];
                    if (!byVertex.TryGetValue(vertex, out List<int> touching)) continue;
                    foreach (int neighbour in touching)
                    {
                        if (cache.triangleIsland[neighbour] >= 0) continue;
                        cache.triangleIsland[neighbour] = island;
                        queue.Enqueue(neighbour);
                    }
                }
            }
        }

        caches[mesh] = cache;
        Debug.Log("HairBrush surface islands: cached " + cache.islandCount + " contiguous island(s) across " + triangleCount + " triangles for " + mesh.name + ".");
        return cache;
    }
}

// Persisted directly on each card so future tools (Paint/Spray/Erase/POST/proxies) can use
// the same scope without repeating mesh queries.
public class HairCardSurfaceIsland : MonoBehaviour
{
    public int islandId = -1;
}

// Tags newly-created and project-loaded cards lazily from their stored surface root.
[DefaultExecutionOrder(-3500)]
public class SurfaceIslandCardTagAuthority : MonoBehaviour
{
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<SurfaceIslandCardTagAuthority>() != null) return;
        GameObject go = new GameObject("SurfaceIslandCardTagAuthority");
        Object.DontDestroyOnLoad(go);
        go.AddComponent<SurfaceIslandCardTagAuthority>();
    }

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + .15f;

        // ONLY WHEN THE SET OF CARDS HAS CHANGED.
        //
        // A tag, once written, never becomes wrong on its own - it is resolved from the card's
        // root against the model, and neither moves without the card being rebuilt. So the only
        // way a new untagged card appears is for one to be created, which moves RegistryVersion.
        //
        // The sweep was running six times a second regardless: forty thousand GetComponent
        // calls to re-confirm tags that were all already written. And for any card whose island
        // could NOT be resolved it is far worse than that - TryGetCardIsland fires two
        // Physics.Raycasts and only writes the tag on success, so an unresolvable card is
        // retried forever. On a model with no collider, or roots that have drifted off the
        // surface, that is eighty thousand raycasts every 0.15 seconds, permanently, and it
        // presents as a hitch six times a second that nothing in the UI explains.
        if (lastRegistryVersion == HairCard.RegistryVersion) return;
        lastRegistryVersion = HairCard.RegistryVersion;

        IReadOnlyList<HairCard> cards = HairCard.All;
        for (int i = 0; i < cards.Count; i++)
        {
            HairCard card = cards[i];
            if (card == null) continue;
            HairCardSurfaceIsland tag = card.GetComponent<HairCardSurfaceIsland>();
            if (tag != null && tag.islandId >= 0) continue;
            SurfaceIslandScope.TryGetCardIsland(card, out _);
        }
    }

    private int lastRegistryVersion = -1;
}
