using System.Reflection;
using UnityEngine;

// Puts every modifier down and gets back to editing the group root.
//
// Eight different things in this project can be "the thing you are currently editing" - a POST, a
// clumper, a guide, a brush selection, an armed +POST/+CLUMPER/+GUIDE placement, an open curve
// popup, a half-typed group rename, and the curve registry swap - and each caller that needs to
// leave them assembles its own subset. There are six such subsets: NewGroupRootSelectionAuthority,
// ModifierEmptySpaceExitAuthority, TextureEditorPostExitGuard, ClumperSelectionExitAuthority,
// TextureUVRectWorkspace.DeselectGroomContext, and this one, which started life private inside
// GroupParameterClipboardAuthority. No two of them are the same, and the gaps between them are
// where modes get stranded.
//
// This file is NOT yet the single source of truth - the other five still hand-roll theirs, and
// each is entangled with the reactive authority it lives next to, so folding them in is its own
// piece of work. What this is, is the widest and most carefully ordered of the six, and the one
// to reach for from anything new.
//
// THE ORDER IS THE HARD PART. Three constraints, each of which has already been paid for once:
//
// 1. ClearSelectionHotspot BEFORE ReleasePostSelection. ReleasePostSelection's last act is to
//    clear hasSelectionHotspot, and that is the flag the hotspot teardown tests to decide whether
//    there is anything to tear down - run afterwards it always reads false and does nothing. That
//    matters because ReleasePostSelection clears only that one flag and leaves isSelectionMode
//    set, and ModelViewer refuses to place hair while THAT is set. Getting this backwards switches
//    card placement off for the rest of the session with nothing on screen to explain it, and
//    nothing self-heals: ModelViewer.HasLiveSelection returns early on hasSelectionHotspot before
//    it can clean up.
//
// 2. The curve presentation LAST. PostShapeCurveBridge decides what to present by reading the
//    active POST id, so releasing it before the POST is deselected just re-presents the POST.
//
// 3. ClumperControls and ClumperScrollHost are two separate objects. GroupClumperManager's own
//    ClearSelection destroys the first and not the second, and a stranded ClumperScrollHost leaves
//    the whole grooming panel hidden behind it.
public static class ModifierContextExit
{
    // Everything that counts as "something is selected". This is the teardown COPY and PASTE have
    // always done, plus the ClumperScrollHost destroy they were quietly missing.
    static void LeaveSelections(ModelViewer viewer)
    {
        // FIRST. See constraint 1 above.
        ClearSelectionHotspot(viewer);

        PostAffectorManager posts = Object.FindFirstObjectByType<PostAffectorManager>();
        if (posts != null) posts.ReleasePostSelection();

        GroupClumperManager clumpers = Object.FindFirstObjectByType<GroupClumperManager>();
        if (clumpers != null) clumpers.ClearSelection();

        // Unconditional, which is wider than the other exits - they only look for the host when
        // they know a clumper was selected. Deliberate: a host standing without a selection is
        // the stranded state this method exists to clear, not a reason to leave it standing. It
        // is safe to destroy one that is not there to be destroyed, and ClumperControlsScrollFix
        // puts the groom rows back the frame after it finds its controls gone.
        GameObject clumperScrollHost = GameObject.Find("ClumperScrollHost");
        if (clumperScrollHost != null) Object.Destroy(clumperScrollHost);

        GuideCurveManager guides = Object.FindFirstObjectByType<GuideCurveManager>();
        if (guides != null) guides.ClearSelection();
    }

    // LeaveSelections plus the curve registry swap. What COPY and PASTE call.
    public static void LeaveModifierContext(ModelViewer viewer)
    {
        LeaveSelections(viewer);

        // And only now can the curves be trusted. See constraint 2 above.
        PostShapeCurveBridge.EnsurePresentationReleased();
    }

    // The wide one: everything above, plus the three things that are modes without being
    // selections. What a click on + GROUP or on a group's name row calls, because "take me back
    // to this group" has to mean all of it or it means nothing - a guide left selected keeps
    // holding GroomingInputLock and card placement stays off, and an armed placement left armed
    // still points at the group you just left.
    public static void LeaveEverything(ModelViewer viewer)
    {
        // A rename in progress is text the person typed, so it is committed rather than dropped.
        // Usually a no-op by the time this runs: both callers fire from Button.onClick, which is
        // pointer-UP, and the EventSystem already moved focus off the input field on pointer-DOWN,
        // which commits it. Kept for the paths that do not go through a pointer at all.
        GroupNameInlineEditAuthority.CommitActiveEdit();

        // Before the selections. Both an armed placement and a selected guide hold
        // GroomingInputLock, and Disarm asks for it back on a delay rather than immediately. The
        // guide's own hold is not released here at all - GuideCurveHandleAuthority notices next
        // frame that nothing is selected and lets go then - and the restore refuses while any
        // holder remains, so the order on this line changes nothing except which one asks first.
        GroupAddButtonPlacementAuthority.CancelArmed();

        LeaveSelections(viewer);

        // Both orderings work - Destroy is deferred to the end of the frame, so the editor is
        // still alive either way - but closing first is what the reader expects and costs nothing.
        GroomShapeCurveAuthority curves = Object.FindFirstObjectByType<GroomShapeCurveAuthority>();
        if (curves != null) curves.ClosePopup();

        PostShapeCurveBridge.EnsurePresentationReleased();
    }

    // ModelViewer.ClearSelectionHotspot is private, and this is the project's established way in.
    static void ClearSelectionHotspot(ModelViewer viewer)
    {
        if (viewer == null) return;

        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

        FieldInfo hotspotFlag = typeof(ModelViewer).GetField("hasSelectionHotspot", Flags);
        bool hotspot = hotspotFlag != null && hotspotFlag.GetValue(viewer) is bool live && live;

        if (hotspot)
        {
            // The full teardown, which also zeroes every card's brush weight. Gated on the flag
            // because run when there is no selection it would zero weights nobody asked it to
            // touch.
            MethodInfo clear = typeof(ModelViewer).GetMethod("ClearSelectionHotspot", Flags);
            if (clear != null) clear.Invoke(viewer, null);
            return;
        }

        // No hotspot, but isSelectionMode can still be stuck on, and that alone is enough to make
        // ModelViewer refuse to place hair. ReleasePostSelection clears the hotspot flag and
        // leaves this one, and the exit guards that would normally follow up only fire on a POST
        // id going from set to unset - so a ReleasePostSelection that runs when the id is ALREADY
        // unset strands it with nothing left to notice.
        //
        // PostAffectorSurfaceMoveUX heals the version of this that it caused itself, on the frame
        // it stops editing a POST, but it cannot see the ones it did not cause; and
        // ModelViewer.HasLiveSelection, which would otherwise be the safety net, returns early on
        // the hotspot flag before it can clean anything up. Since this method is the "put me back
        // to normal" gesture, it repairs the flag - and only the flag, with no weight sweep,
        // because with no hotspot there was never a brush selection to zero.
        FieldInfo modeFlag = typeof(ModelViewer).GetField("isSelectionMode", Flags);
        if (modeFlag == null) return;
        if (!(modeFlag.GetValue(viewer) is bool stuck) || !stuck) return;
        modeFlag.SetValue(viewer, false);
    }
}
