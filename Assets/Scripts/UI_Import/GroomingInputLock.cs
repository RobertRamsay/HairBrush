using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

// One owner of "card placement is switched off right now", shared by every modifier that needs it.
//
// Two things already switch grooming input off while they are busy: an armed +POST/+CLUMPER/+GUIDE
// placement, and a selected GUIDE being shaped by its drag handles. Each used to capture
// isGroomingMode for itself and restore its own copy, which is correct in isolation and wrong the
// moment they overlap - and they overlap on the most ordinary path there is.
//
// Place a guide with the +GUIDE button: the placement holds grooming off, captures TRUE, then
// hands the new guide straight to the handle editor, which captures the CURRENT value - false,
// because the placement is still holding it - and from then on believes card placement was off to
// begin with. Deselect the guide and grooming is restored to false. Card placing never comes back
// for the rest of the session, with nothing on screen to explain it. The reverse order fails the
// same way: select a guide, place a POST from the button, and whichever restore lands second wins
// with a value captured while the other was suppressing.
//
// A shared lock removes the class of bug rather than the instance. The FIRST holder captures the
// real pre-suppression state; later holders join without re-capturing; the state is handed back
// only once the last holder has let go. Holders still choose their own moment to release - the
// placement waits for the mouse button to come up so a Paint-mode hold cannot plant a trail of
// cards out of the tail of its own click - and a holder arriving during someone else's pending
// release simply keeps the lock held, with the original captured value intact.
public static class GroomingInputLock
{
    private static readonly HashSet<string> holders = new HashSet<string>();
    private static bool captured;
    private static bool wasEnabled;

    public static bool AnyHold
    {
        get { return holders.Count > 0; }
    }

    // Idempotent: safe to call every frame, which is what callers should do. isGroomingMode gets
    // switched back on from outside - a project load re-enables it, and ModifierGestureReservation
    // restores it after any TAB or SPACE click (it now checks AnyHold first, but only because it
    // was taught to) - so a one-shot toggle would be quietly undone under a holder still busy.
    public static void Hold(string owner, ModelViewer viewer)
    {
        if (viewer == null || string.IsNullOrEmpty(owner)) return;

        if (!captured)
        {
            captured = true;
            wasEnabled = ReadGrooming(viewer);
        }

        holders.Add(owner);
        viewer.ToggleGroomingMode(false);
    }

    public static void Release(string owner)
    {
        if (string.IsNullOrEmpty(owner)) return;
        holders.Remove(owner);
    }

    public static bool Holds(string owner)
    {
        if (string.IsNullOrEmpty(owner)) return false;
        return holders.Contains(owner);
    }

    // Hands grooming back, but only once nobody is holding any more. Callers with a deferred
    // release call this on their own schedule; the first one to find the lock empty performs the
    // restore and the rest find nothing left to do.
    //
    // Returns true when the restore actually happened OR is no longer this caller's business, so
    // a caller can clear its pending flag either way and stop asking.
    public static bool TryRestore(ModelViewer viewer)
    {
        if (holders.Count > 0) return false;
        if (!captured) return true;

        // A null viewer means the restore cannot be performed, NOT that it is no longer owed.
        // Clearing the capture here would drop it silently and leave card placement off.
        if (viewer == null) return false;

        captured = false;
        viewer.ToggleGroomingMode(wasEnabled);
        return true;
    }

    // Play mode can begin with these statics still populated when Reload Domain is turned off,
    // and a holder left over from the previous session would keep card placement off from the
    // very first frame with nothing on screen to explain it.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetOnEnterPlayMode()
    {
        ForceClear();
    }

    public static void ForceClear()
    {
        holders.Clear();
        captured = false;
        wasEnabled = false;
    }

    static bool ReadGrooming(ModelViewer viewer)
    {
        FieldInfo field = typeof(ModelViewer).GetField("isGroomingMode",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (field == null) return true;
        object value = field.GetValue(viewer);
        if (value is bool flag) return flag;
        return true;
    }
}
