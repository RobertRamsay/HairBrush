using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// One draggable row in the Texture UV Rect Workspace's list. Dropping this row onto another
// row asks the owning TextureUVRectWorkspace to move it into that row's slot; the workspace
// renumbers everything to match the new order and rebuilds both this list and the on-texture
// outlines/labels from scratch, so this component only ever deals with a single drag gesture
// at a time and never has to reconcile its own state against a stale id afterward.
public class UVRectSummaryRow : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler,
    IBeginDragHandler, IDragHandler, IEndDragHandler, IDropHandler
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

    public void Bind(TextureUVRectWorkspace owner, int id, Image backgroundImage, CanvasGroup group)
    {
        workspace = owner;
        rectId = id;
        background = backgroundImage;
        canvasGroup = group;
        pointerHovered = false;
        externallyHighlighted = false;
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

    public void OnBeginDrag(PointerEventData eventData)
    {
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
        transform.position = eventData.position;
    }

    public void OnEndDrag(PointerEventData eventData)
    {
        if (canvasGroup != null) canvasGroup.blocksRaycasts = true;
        pointerHovered = false;
        if (originalParent != null)
        {
            transform.SetParent(originalParent, false);
            transform.SetSiblingIndex(originalSiblingIndex);
        }
        ApplyNormalSkin();
    }

    public void OnDrop(PointerEventData eventData)
    {
        GameObject draggedGO = eventData.pointerDrag;
        if (draggedGO == null) return;
        UVRectSummaryRow draggedRow = draggedGO.GetComponent<UVRectSummaryRow>();
        if (draggedRow == null || draggedRow == this) return;

        workspace?.ReorderRectangle(draggedRow.rectId, rectId);
    }
}
