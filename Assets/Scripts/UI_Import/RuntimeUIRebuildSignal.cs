using UnityEngine;

// "The panel was just rebuilt - whoever styles it, do it NOW rather than at your next poll."
//
// THE PROBLEM THIS SOLVES
//
// Runtime UI in this project is built plain and corrected afterwards by a chain of polling
// authorities, each on its own timer:
//
//   PostAffectorUXFix          Update      3600    every .05s   POST row column widths
//   ModifierCoreLock           Update      5000    every .08s   interactable / row alpha
//   GroupPanelPostHintStats    LateUpdate  9000    every .10s   group header layout, ordering
//   GroupPanelRowOrderAuthority LateUpdate 10000   every .12s   row order
//   UIThemeAuthority           LateUpdate  50000   every .25s   the shared button and slider skin
//
// That is a sound arrangement - the pieces are built by a dozen scripts at a dozen moments and
// no one of them could know the whole answer - but it has one visible cost. ModelViewer's group
// list is destroyed and rebuilt whenever a group is added or deleted, and the fresh rows are
// raw: a 170x40 label button, flat grey, unskinned. They are then DRAWN that way for up to a
// quarter of a second, until each authority's timer comes round and corrects them one after
// another. What the user sees is the old-looking button format flashing up before the real one
// settles - a rebuild that stutters instead of just changing.
//
// THE FIX
//
// Nothing about the polling changes. The authorities simply also run on the frame something was
// rebuilt, so the correction lands in the SAME frame as the build - and since Unity renders
// after LateUpdate, the first frame those rows are ever presented they are already correct.
// Every one of the five above runs late enough in the frame (Update at 3600+, or LateUpdate) to
// catch a rebuild made in an Update or a UI button callback, which is where they all come from.
//
// A mark made too late for one of them - after its slot in the frame - is picked up on the very
// next frame instead, because each authority remembers which mark it has acted on rather than
// asking whether the mark is from this exact frame. One frame of raw row is the worst case; a
// quarter of a second was the old best case.
//
// This is not a general "something changed" bus and must not become one. It says only that UI
// OBJECTS WERE CREATED OR DESTROYED and therefore need the styling passes. Data changes - a
// slider moved, a weight edited - are not marks; those authorities already handle them on their
// own terms, and marking every data change would put all five passes on every frame.
public static class RuntimeUIRebuildSignal
{
    // The frame of the most recent rebuild. -1 means nothing has been marked this session.
    private static int lastMarkedFrame = -1;

    // Statics survive "Enter Play Mode -> Disable Domain Reload", and a frame number from the
    // previous run is a number from the future as often as not.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        lastMarkedFrame = -1;
    }

    // Call immediately AFTER building or destroying runtime UI objects, not before - the
    // authorities that answer this run later in the same frame and need the objects to exist.
    public static void Mark()
    {
        lastMarkedFrame = Time.frameCount;
    }

    // True once per mark, per caller. Each authority keeps its own handled-frame field, so a
    // single rebuild is seen by all of them and by none of them twice.
    //
    // Used as: bool rebuilt = RuntimeUIRebuildSignal.TryConsume(ref handledRebuildFrame);
    //          if (!rebuilt && Time.unscaledTime < nextScan) return;
    //
    // Consumed BEFORE the timer test in that idiom, deliberately: a mark has to be marked as
    // handled even on a frame the timer would have allowed the pass anyway, or the authority
    // runs a second time on the next frame for a rebuild it has already dealt with.
    public static bool TryConsume(ref int handledFrame)
    {
        if (lastMarkedFrame < 0) return false;
        if (handledFrame >= lastMarkedFrame) return false;

        handledFrame = lastMarkedFrame;
        return true;
    }
}
