using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// One draggable row in the Texture UV Rect Workspace's list. Dropping this row onto another row
// asks the owning TextureUVRectWorkspace to move it into that row's slot; the workspace
// renumbers everything to match the new order and rebuilds both this list and the on-texture
// outlines/labels from scratch, so this component only ever deals with one drag at a time.
//
// It does NOT get to assume its id survives that. A renumber invalidates every live row through
// Invalidate() - RemoveRectangle and ReorderRectangle both call RetireSummaryRows before the
// rebuild that follows a frame later - so `retired` is the test every entry point makes before
// acting on rectId, including the OnEndDrag branch that destroys this row rather than putting a
// dead duplicate back into a list that has moved on without it.
//
// Right clicking the row deletes its rectangle. Same gesture as right clicking the rectangle
// itself on the texture, so there is one thing to remember rather than two - with one gap: the
// FLIP V square at the row's right-hand end takes the press first and does not pass it on, so
// deleting needs the cursor anywhere else along the row. See UVRectFlipToggle for why that
// trade is worth making.
public class UVRectSummaryRow : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler,
    IBeginDragHandler, IDragHandler, IEndDragHandler, IDropHandler, IPointerDownHandler
{
    private TextureUVRectWorkspace workspace;
    private int rectId;
    private Image background;
    private CanvasGroup canvasGroup;
    private Canvas rootCanvas;
    // Whether OnBeginDrag actually accepted this drag. The input module runs its drag machinery
    // whether or not the handler did anything, so this is what the rest of the chain - OnDrag,
    // OnEndDrag, and the OnDrop on the TARGET row - tests instead of assuming a delivered event
    // means an accepted one. Initialised here.
    private bool dragBegan = false;
    private Transform originalParent;
    private int originalSiblingIndex;
    private bool pointerHovered;
    private bool externallyHighlighted;
    private bool retired;

    // The two things a FLIP V toggle repaints. Held here rather than looked up by name on demand
    // so that a retired row cannot be repainted through a reference the workspace still holds -
    // Invalidate drops both.
    private TextMeshProUGUI summaryLabel;
    private Image flipImage;

    public void Bind(TextureUVRectWorkspace owner, int id, Image backgroundImage, CanvasGroup group)
    {
        workspace = owner;
        rectId = id;
        background = backgroundImage;
        canvasGroup = group;
        pointerHovered = false;
        externallyHighlighted = false;
        retired = false;
        ApplyNormalSkin();
    }

    // Separate from Bind because the flip button is built after the row is, and Bind is the call
    // that marks the row live. Both are made from CreateSummaryRow, one after the other.
    public void BindFlip(TextMeshProUGUI label, Image flipButtonImage)
    {
        summaryLabel = label;
        flipImage = flipButtonImage;
    }

    // The flip toggle sitting on this row asks HERE rather than calling the workspace itself, so
    // that there is exactly one place holding this row's rectangle id. Ids renumber on every
    // delete and reorder, and Invalidate is what says this row has stopped standing for one -
    // a toggle with its own captured copy would go on flipping a strip that had moved.
    public void RequestFlip()
    {
        if (retired || workspace == null) return;
        workspace.ToggleRectFlipV(rectId);
    }

    // A flip changes no row's existence and no row's order, so the workspace repaints the one
    // row that changed instead of rebuilding the list underneath the press that caused it.
    public void SetFlipVisual(bool flipped, string labelText)
    {
        if (retired) return;
        if (summaryLabel != null && summaryLabel.text != labelText) summaryLabel.text = labelText;
        if (flipImage != null) flipImage.color = TextureUVRectWorkspace.FlipButtonColour(flipped);
    }

    void ApplyNormalSkin()
    {
        if (background == null) return;
        Sprite sprite = UITheme.FineEdgeSprite;
        if (sprite == null) return;
        background.sprite = sprite;
        background.type = Image.Type.Sliced;
        background.color = Color.white;
    }

    void ApplyHoverSkin()
    {
        if (background == null) return;
        Sprite sprite = UITheme.FineGlowSprite;
        if (sprite != null)
        {
            background.sprite = sprite;
            background.type = Image.Type.Sliced;
        }
        // A colour tint on top of the glow sprite, not just the sprite swap alone - the sprite
        // difference by itself was too subtle to reliably notice.
        background.color = new Color(1f, .82f, .35f, 1f);
    }

    // Combines the row's own mouse-hover state with a highlight driven externally by the
    // workspace (when the corresponding on-texture rectangle is hovered instead), so neither
    // source can stomp on a highlight the other source still wants active.
    void RefreshSkin()
    {
        if (pointerHovered || externallyHighlighted) ApplyHoverSkin();
        else ApplyNormalSkin();
    }

    // Called by TextureUVRectWorkspace when this row's rectangle is hovered on the texture
    // instead of directly on this row - the other half of the two-way hover sync.
    public void SetExternalHighlight(bool on)
    {
        externallyHighlighted = on;
        RefreshSkin();
    }

    public void OnPointerEnter(PointerEventData eventData)
    {
        pointerHovered = true;
        RefreshSkin();
        workspace?.SetHoveredRect(rectId);
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        pointerHovered = false;
        RefreshSkin();
        workspace?.ClearHoveredRect(rectId);
    }

    // Right click deletes this row's rectangle - anywhere on the row except the FLIP V square at
    // its right-hand end, which carries its own IPointerDownHandler and so takes the press before
    // this ever sees it. The left button is deliberately left alone here: a row is draggable for
    // re-ordering, and a left press is the start of that gesture at least as often as a click.
    //
    // On the DOWN edge, not on click. A click needs the pointer to stay within the input
    // module's drag threshold between press and release, and a right press that drifts past it
    // becomes a right DRAG instead - which, with IBeginDragHandler on this same component, used
    // to reorder the list rather than delete anything. That is the same edge the on-texture
    // delete fires on, so both halves of the gesture behave identically.
    public void OnPointerDown(PointerEventData eventData)
    {
        if (eventData == null) return;
        if (eventData.button != PointerEventData.InputButton.Right) return;
        if (workspace == null) return;

        // Under MAYA-NAV, ALT+RMB is the DOLLY. Starting a zoom with the cursor a few pixels
        // inside this list deletes the row it landed on - and a delete renumbers every rectangle
        // after it, so every group pointing at "rect 4" quietly points at something else.
        // TextureUVRectWorkspace.HandleRightClickDelete guards the on-texture half of this same
        // gesture, and deliberately uses the OTHER predicate: that one is a viewport click, where
        // ALT is reserved in both modes, and this one is a panel click, where it is not. Same
        // gesture, two layers, two correct answers - do not "fix" one to match the other.
        if (MayaNavigationAuthority.CameraGestureActive) return;

        // This row is about to stop standing for anything, so drop its highlight claims before
        // asking. OnPointerExit will never arrive - the GameObject is destroyed by the rebuild.
        pointerHovered = false;
        externallyHighlighted = false;

        // true: the rebuild has to wait until this frame's pointer dispatch is over. The list
        // this row lives in is the hierarchy this callback is currently executing under.
        workspace.RemoveRectangle(rectId, true);
    }

    // Called by the workspace on every surviving row the moment the rectangle set changes, when
    // the row list itself cannot be rebuilt yet. Without it, a row goes on answering to the id
    // it was built with, which after a delete belongs to a different rectangle - so a second
    // right click, or a drop from a drag already in flight, would act on the wrong one.
    public void Invalidate()
    {
        workspace = null;
        rectId = -1;
        retired = true;
        pointerHovered = false;
        externallyHighlighted = false;

        // Dropped for the same reason the id is: after a delete or a reorder this row's number
        // belongs to a different rectangle, so repainting it would put another strip's FLIPPED
        // on it. RequestFlip above is already refusing by then - `retired` is the one test, and
        // blocksRaycasts=false below is the belt to its braces.
        summaryLabel = null;
        flipImage = null;
        if (canvasGroup != null) canvasGroup.blocksRaycasts = false;
    }

    // Left button only, all four of them. The right button drives delete, and the input module
    // runs its drag machinery for every button, so without this a right press with any drift in
    // it would tear the row out of the list and drop it somewhere.
    public void OnBeginDrag(PointerEventData eventData)
    {
        if (eventData == null || eventData.button != PointerEventData.InputButton.Left) return;

        // An ALT+LMB tumble begun over this list would tear the row out and drop it somewhere
        // else, and a reorder renumbers every rectangle exactly as the delete above does. The
        // camera does not move either - navSuppressedLeft has already killed it - so the user gets
        // a silently reordered atlas and nothing they asked for.
        //
        // Refusing HERE is not enough on its own, which is the whole reason dragBegan exists
        // below. The input module does not ask whether OnBeginDrag did anything: it sets
        // pointerDrag at the press and dragging in ProcessDrag regardless, then delivers OnDrag
        // and, on release, OnDrop to whatever is under the cursor. So a refusal that only returned
        // from this method would leave the drag running with the row never reparented - still
        // moving under the cursor, still landing on a target row, still reordering the atlas.
        if (MayaNavigationAuthority.CameraGestureActive) return;

        dragBegan = true;
        originalParent = transform.parent;
        originalSiblingIndex = transform.GetSiblingIndex();
        if (rootCanvas == null) rootCanvas = GetComponentInParent<Canvas>();

        // Dropping raycasts through this row while it's being dragged is what lets the row
        // underneath the pointer receive its own OnPointerEnter/OnDrop - without this, the
        // dragged row would just keep blocking itself as the closest raycast target.
        if (canvasGroup != null) canvasGroup.blocksRaycasts = false;

        if (rootCanvas != null) transform.SetParent(rootCanvas.transform, true);
        transform.SetAsLastSibling();
        ApplyHoverSkin();

        // The dragged row is about to move away from under the cursor's original spot, so its
        // own hover-driven on-texture flash no longer applies for the duration of the drag.
        workspace?.ClearHoveredRect(rectId);
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (eventData == null || eventData.button != PointerEventData.InputButton.Left) return;

        // OnBeginDrag refused this one. Moving the row anyway would drag it out of the list with
        // none of the setup that makes a drag reversible - no saved parent, no saved sibling
        // index - so OnEndDrag would have nothing to put back and the row would simply stay where
        // the cursor left it.
        if (!dragBegan) return;

        transform.position = eventData.position;
    }

    public void OnEndDrag(PointerEventData eventData)
    {
        if (eventData == null || eventData.button != PointerEventData.InputButton.Left) return;

        // Cleared here rather than in OnDrop, because OnDrop is delivered to the TARGET row and
        // may not be delivered at all - a drag released over empty space ends with no drop.
        bool began = dragBegan;
        dragBegan = false;
        if (!began) return;

        if (canvasGroup != null) canvasGroup.blocksRaycasts = true;
        pointerHovered = false;

        // Retired while this drag was in flight. OnBeginDrag reparented the row to the root
        // canvas, so it was no longer a child of the list and the rebuild could not destroy it
        // with the others. Putting it back would insert a dead duplicate - stale label, no
        // rectangle behind it - into a list that has already moved on without it.
        if (retired)
        {
            Destroy(gameObject);
            return;
        }

        if (originalParent != null)
        {
            transform.SetParent(originalParent, false);
            transform.SetSiblingIndex(originalSiblingIndex);
        }
        ApplyNormalSkin();
    }

    public void OnDrop(PointerEventData eventData)
    {
        if (eventData == null || eventData.button != PointerEventData.InputButton.Left) return;
        GameObject draggedGO = eventData.pointerDrag;
        if (draggedGO == null) return;
        UVRectSummaryRow draggedRow = draggedGO.GetComponent<UVRectSummaryRow>();
        if (draggedRow == null || draggedRow == this) return;

        // The reorder lives HERE, on the target row, not on the dragged one - so this is the test
        // that actually decides whether the atlas gets renumbered. A drag the source refused never
        // set dragBegan, and must not be completed by whatever it happens to be released over.
        if (!draggedRow.dragBegan) return;

        // Either row can have been invalidated between the drag starting and this drop, by a
        // delete that has not had its frame to rebuild the list yet.
        if (rectId < 0 || draggedRow.rectId < 0) return;

        workspace?.ReorderRectangle(draggedRow.rectId, rectId);
    }
}
