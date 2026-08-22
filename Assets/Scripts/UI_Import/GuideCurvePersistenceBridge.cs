using System;
using System.Collections.Generic;
using UnityEngine;

// Saves and restores GUIDE curves, the same way GroupClumperPersistenceBridge does for CLUMPER.
//
// Only the recipe is written. HairCard save data stays the authored, un-combed state, and the
// guide re-applies itself downstream on load, so a project saved with a guide at Amount 1 and
// one saved with the same guide at Amount 0 hold identical card data.
//
// Load order is the whole difficulty. The sequence is:
//
//   FromJson -> QueueRestore drops the OUTGOING project's guides immediately
//   LoadProjectEnhanced      model swapped, groups rebuilt, cards spawned, panel rebuilt
//   order 3900               canonical card + POST restore completes, generation counter ticks
//   here                     the saved guides are installed
//
// Both parts are load-bearing:
//
//   Clearing at parse time, not later, because GuideCurveManager is DontDestroyOnLoad. Group
//   ids get reused, so the outgoing project's guides would otherwise spend the settle window
//   combing the incoming project's hair from a contact point in geometry that no longer exists.
//
//   Waiting for the canonical generation, because guides deform the mesh downstream of the
//   card reconstruction. Installing them first means combing cards that are about to be
//   overwritten, and a wasted evaluation pass per frame until they are.
//
// Nothing else clears guides on this path. SessionModifierFreshStartAuthority used to, on both
// its branches, and that clear was deleted when guides became persistent: it polls, so it could
// land either side of the install, and there is no ordering against a poll that is safe both
// ways. The new-OBJ path is owned by GroomSessionResetCoordinator, which also cancels a restore
// left in flight, so a project abandoned half way through cannot comb the next model's hair.
[DefaultExecutionOrder(7010)]
public class GuideCurvePersistenceBridge : MonoBehaviour
{
    // Wall clock, not frames: a frame budget is three seconds on an empty scene and three minutes
    // on a heavy groom, which is the one thing a timeout must not be. Twenty seconds is far longer
    // than the two restores this one waits on take even on a heavy groom.
    //
    // realtimeSinceStartup, not unscaledTime. unscaledTime is sampled once per frame, and the
    // deadline is set inside the LOAD PROJECT click - the same frame that was stretched by however
    // long the user spent in the modal file dialog. Read that way, a leisurely browse spends the
    // whole budget before the load has even begun.
    private const float RestoreTimeBudget = 20f;

    private static HairProjectSaveData pendingRestore;
    private static int queuedCanonicalGeneration;
    private static float restoreDeadline;

    private GuideCurveManager manager;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<GuideCurvePersistenceBridge>() != null) return;
        GameObject go = new GameObject("GuideCurvePersistenceBridge");
        DontDestroyOnLoad(go);
        go.AddComponent<GuideCurvePersistenceBridge>();
    }

    // Play mode can begin with these statics still holding the previous session's values when
    // Reload Domain is switched off, and a stale pendingRestore would install a previous
    // session's guides over the first project opened in this one.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetOnEnterPlayMode()
    {
        pendingRestore = null;
        queuedCanonicalGeneration = 0;
        restoreDeadline = 0f;
    }

    // Called by GroomSessionResetCoordinator when a fresh OBJ replaces the session. The session a
    // queued restore belonged to is over; installing it later would drop the previous project's
    // guides onto whatever model has taken its place.
    public static void CancelPendingRestore()
    {
        pendingRestore = null;
        restoreDeadline = 0f;
    }

    static int HighestPendingId()
    {
        int highest = 0;
        if (pendingRestore == null || pendingRestore.groups == null) return highest;

        foreach (GroupSaveData group in pendingRestore.groups)
        {
            if (group == null || group.guides == null) continue;
            foreach (GuideCurveSaveData saved in group.guides)
            {
                if (saved != null && saved.id > highest) highest = saved.id;
            }
        }
        return highest;
    }

    // ------------------------------------------------------------------------------- save

    public static void Capture(HairProjectSaveData data)
    {
        if (data == null || data.groups == null) return;

        GuideCurveManager manager = FindFirstObjectByType<GuideCurveManager>();

        // Guide ids are unique across every group, so a runtime guide has to be matched against
        // the whole incoming payload, not just the ids landing in its own group.
        HashSet<int> pendingIds = new HashSet<int>();
        CollectPendingIds(pendingIds);

        foreach (GroupSaveData group in data.groups)
        {
            if (group == null) continue;

            // Always rewrite the list, even with no manager in the scene. Leaving whatever
            // was there would let a stale payload ride along into the new file.
            if (group.guides == null) group.guides = new List<GuideCurveSaveData>();
            else group.guides.Clear();

            // A load that has not finished settling has already emptied the manager but has not
            // installed its guides yet. The runtime is not the whole truth at that moment, so
            // the payload waiting to be installed goes in first and anything the user has added
            // since is appended. Taking only one of the two would either write the settle gap to
            // disk or silently drop a guide the app said it had accepted.
            CopyFromPendingRestore(group);

            if (manager == null) continue;

            List<GuideCurveManager.GuideCurve> runtime = manager.GetGroupGuides(group.groupId);
            runtime.Sort((a, b) => a.id.CompareTo(b.id));

            foreach (GuideCurveManager.GuideCurve guide in runtime)
            {
                if (guide == null) continue;
                if (pendingIds.Contains(guide.id)) continue;
                group.guides.Add(ToSave(guide));
            }
        }
    }

    static void CollectPendingIds(HashSet<int> into)
    {
        if (pendingRestore == null || pendingRestore.groups == null) return;

        foreach (GroupSaveData group in pendingRestore.groups)
        {
            if (group == null || group.guides == null) continue;
            foreach (GuideCurveSaveData saved in group.guides)
            {
                if (saved != null) into.Add(saved.id);
            }
        }
    }

    static void CopyFromPendingRestore(GroupSaveData group)
    {
        if (pendingRestore == null || pendingRestore.groups == null) return;

        foreach (GroupSaveData source in pendingRestore.groups)
        {
            if (source == null || source.groupId != group.groupId) continue;
            if (source.guides == null) return;

            foreach (GuideCurveSaveData saved in source.guides)
            {
                if (saved == null) continue;
                group.guides.Add(Clone(saved));
            }
            return;
        }
    }

    // The pending payload stays owned by the load in flight, so the outgoing file gets copies.
    static GuideCurveSaveData Clone(GuideCurveSaveData s)
    {
        return new GuideCurveSaveData
        {
            id = s.id,
            contactX = s.contactX, contactY = s.contactY, contactZ = s.contactZ,
            normalX = s.normalX, normalY = s.normalY, normalZ = s.normalZ,
            frameX = s.frameX, frameY = s.frameY, frameZ = s.frameZ, frameW = s.frameW,
            midX = s.midX, midY = s.midY, midZ = s.midZ,
            endX = s.endX, endY = s.endY, endZ = s.endZ,
            amount = s.amount,
            radius = s.radius,
            falloff = s.falloff
        };
    }

    static GuideCurveSaveData ToSave(GuideCurveManager.GuideCurve guide)
    {
        return new GuideCurveSaveData
        {
            id = guide.id,
            contactX = guide.contact.x,
            contactY = guide.contact.y,
            contactZ = guide.contact.z,
            normalX = guide.normal.x,
            normalY = guide.normal.y,
            normalZ = guide.normal.z,
            frameX = guide.frame.x,
            frameY = guide.frame.y,
            frameZ = guide.frame.z,
            frameW = guide.frame.w,
            midX = guide.midLocal.x,
            midY = guide.midLocal.y,
            midZ = guide.midLocal.z,
            endX = guide.endLocal.x,
            endY = guide.endLocal.y,
            endZ = guide.endLocal.z,
            amount = guide.amount,
            radius = guide.radius,
            falloff = guide.falloff
        };
    }

    // ------------------------------------------------------------------------------- load

    public static void QueueRestore(HairProjectSaveData data)
    {
        pendingRestore = data;
        queuedCanonicalGeneration = CanonicalProjectStateBridge.CompletedRestoreGeneration;
        restoreDeadline = Time.realtimeSinceStartup + RestoreTimeBudget;

        ClearRuntimeImmediately();
    }

    static void ClearRuntimeImmediately()
    {
        GuideCurveManager manager = FindFirstObjectByType<GuideCurveManager>();
        if (manager != null)
        {
            manager.ClearAll();

            // ClearAll restarts the id allocator at 1, which is right for an empty session and
            // wrong here: the ids in the payload are about to come back. Without this, a guide
            // placed during the settle window is handed an id the restore already owns, and the
            // save-side merge then cannot tell the two apart.
            manager.ReserveGuideIdsAbove(HighestPendingId());
        }

        // ClearAll destroys the slider panel and empties the collections, but the left-panel
        // rows are child objects of the group items and are only reaped on the next scan.
        // Take them now so the outgoing project's rows are never visible over the incoming one.
        foreach (RectTransform row in FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (row == null) continue;
            if (row.name.StartsWith("GuideCurve_", StringComparison.Ordinal)) Destroy(row.gameObject);
        }
    }

    void Update()
    {
        if (pendingRestore == null) return;

        if (manager == null) manager = FindFirstObjectByType<GuideCurveManager>();
        if (manager == null) return;

        // Expired loads install anyway, so the cheap checks below are skipped rather than the
        // whole restore being thrown away. Dropping it would be the very outcome the timeout
        // exists to avoid: the guides were emptied at parse time, so abandoning the payload
        // leaves the session looking as though they were never in the file, and the next save
        // writes that back over the file. A guide that arrives looking wrong can be deleted;
        // one that was silently discarded cannot be recovered.
        bool expired = Time.realtimeSinceStartup > restoreDeadline;
        if (expired)
        {
            Debug.LogWarning("HairBrush: the project load did not settle within " +
                             RestoreTimeBudget + "s, so GUIDE curves are being applied now " +
                             "rather than waiting any longer. Check them before saving.");
        }
        else
        {
            // Statics first, then the scene scan: this runs every frame of the settle window,
            // and FindObjectsByType allocates the whole card array each time it is reached.
            if (HairProjectSaveData.PendingModifierRestore != null) return;

            // Current-format projects signal when the card reconstruction is finished. Guides
            // are downstream of it, so they wait rather than comb cards about to be rewritten.
            if (pendingRestore.formatVersion >= CanonicalProjectStateBridge.CurrentFormatVersion &&
                CanonicalProjectStateBridge.CompletedRestoreGeneration <= queuedCanonicalGeneration)
                return;

            int expected = pendingRestore.hairCards != null ? pendingRestore.hairCards.Count : 0;
            if (FindObjectsByType<HairCard>(FindObjectsSortMode.None).Length < expected) return;
        }

        HairProjectSaveData data = pendingRestore;
        pendingRestore = null;
        restoreDeadline = 0f;
        Restore(data);
    }

    void Restore(HairProjectSaveData data)
    {
        List<GuideCurveManager.GuideCurve> restored = new List<GuideCurveManager.GuideCurve>();
        HashSet<int> usedIds = new HashSet<int>();
        int nextId = 1;

        if (data.groups != null)
        {
            foreach (GroupSaveData group in data.groups)
            {
                if (group == null || group.guides == null) continue;

                foreach (GuideCurveSaveData saved in group.guides)
                {
                    if (saved == null) continue;
                    restored.Add(FromSave(saved, group.groupId, usedIds, ref nextId));
                }
            }
        }

        manager.ReplaceAll(restored);
    }

    static GuideCurveManager.GuideCurve FromSave(GuideCurveSaveData saved, int groupId,
        HashSet<int> usedIds, ref int nextId)
    {
        Vector3 normal = new Vector3(saved.normalX, saved.normalY, saved.normalZ);
        if (normal.sqrMagnitude > .000001f) normal = normal.normalized;
        else normal = Vector3.up;

        // A quaternion that is not a rotation - all zeros from a hand-edited or truncated
        // file - would silently collapse every offset to the origin. Rebuilding from the
        // normal loses the roll, which is the lesser of the two failures by a wide margin.
        Quaternion frame = new Quaternion(saved.frameX, saved.frameY, saved.frameZ, saved.frameW);
        float magnitude = Mathf.Sqrt(frame.x * frame.x + frame.y * frame.y +
                                     frame.z * frame.z + frame.w * frame.w);
        if (magnitude > .0001f)
        {
            frame = new Quaternion(frame.x / magnitude, frame.y / magnitude,
                                   frame.z / magnitude, frame.w / magnitude);
        }
        else
        {
            frame = GuideCurveManager.BuildInitialFrame(normal);
        }

        return new GuideCurveManager.GuideCurve
        {
            id = ClaimId(saved.id, usedIds, ref nextId),
            groupId = groupId,
            contact = new Vector3(saved.contactX, saved.contactY, saved.contactZ),
            normal = normal,
            frame = frame,
            midLocal = new Vector3(saved.midX, saved.midY, saved.midZ),
            endLocal = new Vector3(saved.endX, saved.endY, saved.endZ),
            amount = Mathf.Clamp01(saved.amount),
            radius = Mathf.Max(.001f, saved.radius),
            falloff = Mathf.Max(0f, saved.falloff)
        };
    }

    // Ids are unique across every group, because that is how the runtime allocates them and
    // how the rows are named. A file with a collision, or with a zero from a hand edit, gets
    // the next free number rather than a duplicate that FindGuide would resolve arbitrarily.
    static int ClaimId(int requested, HashSet<int> usedIds, ref int nextId)
    {
        if (requested > 0 && usedIds.Add(requested))
        {
            if (requested >= nextId) nextId = requested + 1;
            return requested;
        }

        while (usedIds.Contains(nextId)) nextId++;
        int id = nextId++;
        usedIds.Add(id);
        return id;
    }
}
