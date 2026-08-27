using UnityEngine;
using UnityEngine.EventSystems;

// The little "V" square at the right-hand end of a UV rect row. Turns that strip's root end
// over - see UVRectSaveData.flipV.
//
// WHY THIS IS NOT A BUTTON. Three separate things go wrong if it is one, and all three are
// invisible until someone comes back to a sheet they already marked up:
//
//   * UIThemeAuthority.StyleAllButtons walks EVERY Button in the scene every quarter second and
//     hands each new one the shared skin - sprite, and image.color = WHITE. It styles a given
//     Button once, so the damage is not a flicker: these rows are destroyed and rebuilt on every
//     draw, delete, reorder, import and material switch, and each rebuild makes brand new
//     Buttons that are painted white within a quarter second of appearing. The teal that says
//     "this strip is flipped" would therefore be visible only in the moment right after a
//     rebuild, and gone for good after that. Nothing here re-asserts it: the groom-panel FLIP V
//     button survives the same pass only because MaintainFlipRow repaints it 16 times a second.
//   * The same pass runs UITheme.ClampButtonSize, whose floor is 26 units. This control is 14
//     high inside an 18 high row on purpose. A 26 high raycast target centred on an 18 high row
//     overlaps the rows above and below it - so neighbouring toggles start swallowing each
//     other's presses.
//   * UIThemeAuthority never prunes its styled/interactable bookkeeping, keyed by Button, and
//     these rows churn constantly - so every rebuild leaks another dead key for the session.
//
// The five by-name exemptions in that pass are not the answer either. They are there for three
// unrelated reasons already - two reroll buttons that skin themselves, a START button that
// ClampButtonSize would blow up, and a full-screen dimmer that is only a Button so a click can
// dismiss it - and a sixth entry would be borrowing someone else's escape hatch to hide from a
// pass this control has no business being swept by in the first place.
//
// So it is an Image plus these handlers, and the theme pass never sees it.
//
// WHAT THE HANDLERS ARE FOR. The row underneath is draggable - that is how rectangles are
// reordered - and the input module resolves a drag by walking UP from whatever was hit to the
// first IDragHandler. Without the three no-op drag handlers below that walk reaches the ROW, so
// a press on this 18x14 square that drifts past the 10px drag threshold tears the row out of the
// list, drops it on its neighbour, and renumbers every rectangle in the atlas - which quietly
// repoints every group's UV RECTS range at different strips. See TextureUVRectWorkspace
// .RemoveRectangle for what that costs. They exist to stop the walk here.
//
// FLIP ON THE DOWN EDGE, not on click, for the same reason UVRectSummaryRow deletes on the down
// edge: a press that drifts is a drag, not a click, and a target this small is easy to drift on.
// It also sidesteps the input module's rule that a click only fires when the object that took
// the press is the same one that answers the release - which, with an IPointerDownHandler on the
// row above, it would not have been.
//
// The cost of stopping the walk is that the row's RIGHT-click delete does not reach these 18px.
// That is the trade: deleting still works along the whole rest of the row, and a flip that
// reordered the atlas instead would be far worse than a delete that needs the cursor moved.
public class UVRectFlipToggle : MonoBehaviour, IPointerDownHandler,
    IBeginDragHandler, IDragHandler, IEndDragHandler
{
    private UVRectSummaryRow owner;

    public void Bind(UVRectSummaryRow row)
    {
        owner = row;
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        if (eventData == null) return;
        if (eventData.button != PointerEventData.InputButton.Left) return;
        if (owner == null) return;

        // Under MAYA-NAV, ALT+LMB is the TUMBLE. Beginning one with the cursor a few pixels
        // inside this list would otherwise turn a strip over on the way past. UVRectSummaryRow
        // .OnBeginDrag guards the same key for the same reason, and uses this same predicate -
        // this is a PANEL click, where ALT is only reserved while MAYA-NAV is actually on.
        if (MayaNavigationAuthority.CameraGestureActive) return;

        // Asked of the row rather than done here, so this control never holds a rectangle id of
        // its own. Ids renumber on every delete and reorder, and a captured one goes stale
        // without any sign; the row already knows how to say "I no longer stand for anything".
        owner.RequestFlip();
    }

    // Deliberately empty. Their only job is to be found before the row is - see the class
    // comment. Anything that made them do something would be reimplementing the row's drag.
    public void OnBeginDrag(PointerEventData eventData) { }

    public void OnDrag(PointerEventData eventData) { }

    public void OnEndDrag(PointerEventData eventData) { }
}
