using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Gives the group UV source selector one unambiguous interaction surface.
// The UV assignment controller owns the state; this helper only hardens the runtime UI
// so the complete UV MODE row behaves like a real toggle instead of text that may be
// obscured by dynamically rebuilt layout/raycast state.
[DefaultExecutionOrder(6500)]
public class GroupUVModeInteractionFix : MonoBehaviour
{
    private GroupPredeterminedUVController controller;
    private ModelViewer viewer;
    private MethodInfo toggleModeMethod;
    private GameObject boundRow;
    private Button rowButton;
    private Image rowImage;
    private Button sourceButton;
    private TextMeshProUGUI sourceText;
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<GroupUVModeInteractionFix>() != null) return;
        GameObject go = new GameObject("GroupUVModeInteractionFix");
        DontDestroyOnLoad(go);
        go.AddComponent<GroupUVModeInteractionFix>();
    }

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + .05f;

        Resolve();
        BindCurrentRow();
        SyncVisualState();
    }

    void Resolve()
    {
        if (controller == null)
        {
            controller = FindFirstObjectByType<GroupPredeterminedUVController>();
            if (controller != null)
                toggleModeMethod = typeof(GroupPredeterminedUVController).GetMethod(
                    "ToggleMode", BindingFlags.Instance | BindingFlags.NonPublic);
        }
        if (viewer == null) viewer = FindFirstObjectByType<ModelViewer>();
    }

    void BindCurrentRow()
    {
        if (viewer == null || viewer.groomingSliderPanelGO == null || controller == null || toggleModeMethod == null) return;

        Transform row = viewer.groomingSliderPanelGO.transform.Find("GroupUVMode_Row");
        if (row == null || row.gameObject == boundRow) return;

        boundRow = row.gameObject;
        sourceButton = row.GetComponentInChildren<Button>(true);
        sourceText = sourceButton != null ? sourceButton.GetComponentInChildren<TextMeshProUGUI>(true) : null;

        // Make the complete row the hit target. This also avoids a narrow text-only-feeling
        // interaction and survives the right-panel layout being regenerated.
        rowImage = boundRow.GetComponent<Image>();
        if (rowImage == null) rowImage = boundRow.AddComponent<Image>();
        rowImage.raycastTarget = true;
        rowImage.color = new Color(.11f, .13f, .16f, .98f);

        rowButton = boundRow.GetComponent<Button>();
        if (rowButton == null) rowButton = boundRow.AddComponent<Button>();
        rowButton.targetGraphic = rowImage;
        rowButton.transition = Selectable.Transition.ColorTint;
        rowButton.onClick.RemoveAllListeners();
        rowButton.onClick.AddListener(ToggleCurrentGroup);

        if (sourceButton != null)
        {
            // The parent row is the single click authority. Leaving the nested button active
            // can consume the pointer event before it bubbles to the row.
            sourceButton.interactable = false;
            Image sourceImage = sourceButton.GetComponent<Image>();
            if (sourceImage != null) sourceImage.raycastTarget = false;
            foreach (Graphic graphic in sourceButton.GetComponentsInChildren<Graphic>(true))
                graphic.raycastTarget = false;
        }

        CanvasGroup cg = boundRow.GetComponent<CanvasGroup>();
        if (cg == null) cg = boundRow.AddComponent<CanvasGroup>();
        cg.blocksRaycasts = true;
        cg.interactable = true;
    }

    void ToggleCurrentGroup()
    {
        Resolve();
        if (controller == null || viewer == null || toggleModeMethod == null) return;

        // UV source selection is per-group metadata. It is safe to switch from the group
        // panel even when the group contains structural POST modifiers.
        toggleModeMethod.Invoke(controller, new object[] { viewer.currentGroupId });
        nextScan = 0f;
    }

    void SyncVisualState()
    {
        if (boundRow == null || rowButton == null || sourceText == null) return;

        // The controller remains authoritative for whether a predetermined source is actually
        // available. If it has disabled its nested button because there are no authored rects,
        // preserve that rule at the row level.
        bool haveRects = HasAuthoredRects();
        rowButton.interactable = haveRects;

        string raw = sourceText.text ?? string.Empty;
        bool predetermined = raw.Contains("PREDETERMINED");
        sourceText.text = predetermined ? "PREDETERMINED  ⇄" : "ADJUSTABLE  ⇄";

        if (rowImage != null)
            rowImage.color = predetermined
                ? new Color(.12f, .28f, .42f, .98f)
                : new Color(.11f, .13f, .16f, .98f);
    }

    bool HasAuthoredRects()
    {
        TextureUVRectWorkspace workspace = FindFirstObjectByType<TextureUVRectWorkspace>();
        return workspace != null && workspace.ExportDefinitions() != null && workspace.ExportDefinitions().Count > 0;
    }
}
