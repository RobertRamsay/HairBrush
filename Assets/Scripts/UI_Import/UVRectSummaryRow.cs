using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// One draggable row in the Texture UV Rect Workspace's list. Dropping this row onto another
// row asks the owning TextureUVRectWorkspace to move it into that row's slot; the workspace
// renumbers everything to match the new order and rebuilds both this list and the on-texture
// outlines/labels from scratch, so this component only ever deals with a single drag gesture
// at a time and never has to reconcile its own state against a stale id afterward.
//
// Right clicking the row deletes its rectangle. Same gesture as right clicking the rectangle
// itself on the texture, so there is one thing to remember rather than two.
public class UVRectSummaryRow : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler,
    IBeginDragHandler, IDragHandler, IEndDragHandler, IDropHandler, IPointerDownHandler
{
    private TextureUVRectWorkspace workspace;
    private int rectId;
    private Image background;
    private CanvasGroup canvasGroup;
    private Canvas rootCanvas;
    private Transform originalParent;
    private int originalSiblingIndex;
    private bool pointerHovered;
    private bool externallyHighlighted;
    private bool retired;

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

    // Right click deletes this row's rectangle. The left button is deliberately left alone: a
    // row is draggable for re-ordering, and a left press here is the start of that gesture at
    // least as often as it is a click.
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
        if (canvasGroup != null) canvasGroup.blocksRaycasts = false;
    }

    // Left button only, all four of them. The right button drives delete, and the input module
    // runs its drag machinery for every button, so without this a right press with any drift in
    // it would tear the row out of the list and drop it somewhere.
    public void OnBeginDrag(PointerEventData eventData)
    {
        if (eventData == null || eventData.button != PointerEventData.InputButton.Left) return;

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
        transform.position = eventData.position;
    }

    public void OnEndDrag(PointerEventData eventData)
    {
        if (eventData == null || eventData.button != PointerEventData.InputButton.Left) return;
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

        // Either row can have been invalidated between the drag starting and this drop, by a
        // delete that has not had its frame to rebuild the list yet.
        if (rectId < 0 || draggedRow.rectId < 0) return;

        workspace?.ReorderRectangle(draggedRow.rectId, rectId);
    }
}
