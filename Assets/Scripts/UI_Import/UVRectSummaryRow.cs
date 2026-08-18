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

    public void Bind(TextureUVRectWorkspace owner, int id, Image backgroundImage, CanvasGroup group)
    {
        workspace = owner;
        rectId = id;
        background = backgroundImage;
        canvasGroup = group;
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
        if (sprite == null) return;
        background.sprite = sprite;
        background.type = Image.Type.Sliced;
        background.color = Color.white;
    }

    public void OnPointerEnter(PointerEventData eventData) => ApplyHoverSkin();
    public void OnPointerExit(PointerEventData eventData) => ApplyNormalSkin();

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
    }

    public void OnDrag(PointerEventData eventData)
    {
        transform.position = eventData.position;
    }

    public void OnEndDrag(PointerEventData eventData)
    {
        if (canvasGroup != null) canvasGroup.blocksRaycasts = true;
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
