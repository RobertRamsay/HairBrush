using System.Collections.Generic;
using UnityEngine;

// One throttled, shared answer to "is there a GameObject called X in the scene, active or not".
//
// WHY THIS EXISTS. Four authorities each carried a private copy of this:
//
//     foreach (Transform t in FindObjectsByType<Transform>(FindObjectsInactive.Include, ...))
//         if (t.name == objectName) return t.gameObject;
//
// and called it from an ungated Update or LateUpdate. That is a walk of Unity's entire object
// registry, and - worse - `Transform.name` is a native marshal that allocates a fresh managed
// string on EVERY access. At forty thousand cards the scene holds roughly a quarter of a million
// objects, so each call allocated a quarter of a million strings, and six such calls ran per
// frame.
//
// The reason it never settled down is the part worth remembering: they were all looking for
// TEXTURE WORKSPACE panels, which are built lazily on first entry to that mode. In a pure
// grooming session those objects do not exist, so the `if (cached == null)` guard never latched
// and every one of them ran to completion, over every object, forever. A negative result has to
// be cached as carefully as a positive one.
//
// This turns six sweeps a frame into one sweep per interval, shared, resolving every name anyone
// has ever asked for in the same pass.
public static class RuntimeNamedObjectCache
{
    // Short enough that a panel appearing is picked up within a frame or three, long enough that
    // the sweep is 6 times a second rather than 360. Invalidate() covers the case where even
    // that is too slow.
    private const float SweepInterval = .15f;

    // Every name anyone has asked about. Small and fixed in practice - a handful of panel names -
    // and never cleared, because a name asked once will be asked again next frame.
    private static readonly HashSet<string> wanted = new HashSet<string>();

    // The last answer for each name, INCLUDING null. Caching the misses is the whole point.
    private static readonly Dictionary<string, GameObject> resolved = new Dictionary<string, GameObject>();

    private static float nextSweep;

    // Statics survive "Enter Play Mode -> Disable Domain Reload", so without this the second
    // Play session starts holding destroyed objects from the first.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        wanted.Clear();
        resolved.Clear();
        nextSweep = 0f;
    }

    // Call when something has just BUILT objects that a lookup is waiting for, so the next Find
    // does not sit out the rest of the interval. Cheap - it only moves a float.
    public static void Invalidate()
    {
        nextSweep = 0f;
    }

    public static GameObject Find(string objectName)
    {
        if (string.IsNullOrEmpty(objectName)) return null;

        wanted.Add(objectName);

        GameObject cached;
        bool haveEntry = resolved.TryGetValue(objectName, out cached);

        // A live hit is served without any sweep at all, and without touching .name - the
        // reference is still the object it was. This is the steady state once a panel is up.
        if (haveEntry && cached != null) return cached;

        if (Time.unscaledTime < nextSweep)
        {
            // Inside the window. Serve the cached answer, miss included - that is what stops a
            // name which is not in the scene from costing a sweep per frame forever.
            if (haveEntry) return cached;
            return null;
        }

        Sweep();

        resolved.TryGetValue(objectName, out cached);
        return cached;
    }

    // One pass, resolving every wanted name at once.
    private static void Sweep()
    {
        nextSweep = Time.unscaledTime + SweepInterval;

        // Clear every entry first so a name whose object has been destroyed goes back to null
        // rather than keeping a stale hit. Entries, not the dictionary, so the keys stay put.
        foreach (string name in wanted) resolved[name] = null;

        Transform[] transforms = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < transforms.Length; i++)
        {
            Transform t = transforms[i];
            if (t == null) continue;

            // One .name read per object per SWEEP, where it used to be one per object per
            // lookup per frame. Still the expensive part, which is why the sweep is throttled.
            string name = t.name;
            if (!wanted.Contains(name)) continue;

            GameObject existing;
            if (resolved.TryGetValue(name, out existing) && existing != null) continue;

            resolved[name] = t.gameObject;
        }
    }
}
