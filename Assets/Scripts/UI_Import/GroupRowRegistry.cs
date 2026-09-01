using System.Collections.Generic;
using UnityEngine;

// The group rows in the left panel, found once and shared.
//
// WHY. Fourteen separate authorities want this same list - the SS/DS and N+/N- buttons, the row
// order, the header stats, the UV mode, the clumper row mirror, the POST lifetime, and more - and
// every one of them was finding it for itself:
//
//     foreach (RectTransform row in FindObjectsByType<RectTransform>(FindObjectsInactive.Include, ...))
//         if (row.name.StartsWith("GroupItem_")) ...
//
// That is a walk of Unity's entire object registry, and `Transform.name` allocates a fresh
// managed string on EVERY access, so each sweep allocates one string per object in the scene.
// Hair cards are root GameObjects, so the registry grows with the groom: at forty thousand cards
// a single sweep touches a quarter of a million objects, and fourteen callers were each doing it
// on their own timer, several of them every frame.
//
// This makes it one sweep per interval, shared. The name is read once per object per sweep
// instead of once per object per caller per frame, and the parsed group id comes with it so no
// caller has to Substring and int.Parse it again either.
public static class GroupRowRegistry
{
    public struct Row
    {
        public RectTransform transform;
        public int groupId;
    }

    // Rows appear when a group is added and vanish when one is deleted, both of which call
    // Invalidate. This interval is the backstop for anything that builds a row without saying so.
    private const float RefreshInterval = .1f;

    private const string Prefix = "GroupItem_";

    private static readonly List<Row> rows = new List<Row>();
    private static float nextRefresh;
    private static bool valid;

    // Statics survive "Enter Play Mode -> Disable Domain Reload", so without this the second Play
    // session starts holding destroyed rows from the first.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        rows.Clear();
        nextRefresh = 0f;
        valid = false;
    }

    // Call when rows have just been built or destroyed, so the next read does not sit out the
    // rest of the interval. The panel is rebuilt on model and project load and edited on every
    // group add and delete, and all of those want to be seen immediately.
    public static void Invalidate()
    {
        valid = false;
        nextRefresh = 0f;
    }

    public static IReadOnlyList<Row> Rows
    {
        get
        {
            Refresh();
            return rows;
        }
    }

    private static void Refresh()
    {
        // A destroyed row leaves a null behind, and a caller iterating the cache would have to
        // guess whether that means "gone" or "not found yet". Rebuilding on the first null keeps
        // the list something callers can trust without asking.
        if (valid && Time.unscaledTime < nextRefresh)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].transform != null) continue;
                valid = false;
                break;
            }
            if (valid) return;
        }

        valid = true;
        nextRefresh = Time.unscaledTime + RefreshInterval;
        rows.Clear();

        RectTransform[] all = Object.FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < all.Length; i++)
        {
            RectTransform t = all[i];
            if (t == null) continue;

            // The one .name read. It is the expensive part of the whole sweep, which is why
            // this exists at all.
            string name = t.name;
            if (name.Length <= Prefix.Length) continue;
            if (!name.StartsWith(Prefix, System.StringComparison.Ordinal)) continue;

            int groupId;
            if (!int.TryParse(name.Substring(Prefix.Length), out groupId)) continue;

            rows.Add(new Row { transform = t, groupId = groupId });
        }
    }
}
