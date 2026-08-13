using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Final authority for the UV MODE row. Keeps one stable label string and one click surface,
// then disables the earlier compatibility helper that decorated the text every few frames.
[DefaultExecutionOrder(6700)]
public class GroupUVModeInteractionAuthority : MonoBehaviour
{
    private GroupPredeterminedUVController controller;
    private ModelViewer viewer;
    private MethodInfo toggleModeMethod;
    private GameObject boundRow;
    private Button rowButton;
    private Image rowImage;
    private TextMeshProUGUI modeText;
    private Button nestedButton;
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<GroupUVModeInteractionAuthority>() != null) return;
        GameObject go = new GameObject("GroupUVModeInteractionAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<GroupUVModeInteractionAuthority>();
    }

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + .04f;
        Resolve();
        BindRow();
        Sync();
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

    void BindRow()
    {
        if (viewer == null || viewer.groomingSliderPanelGO == null || controller == null || toggleModeMethod == null) return;
        Transform row = viewer.groomingSliderPanelGO.transform.Find("GroupUVMode_Row");
        if (row == null || row.gameObject == boundRow) return;

        boundRow = row.gameObject;
        nestedButton = row.Find("GroupUVModeButton")?.GetComponent<Button>();
        if (nestedButton == null) nestedButton = row.GetComponentInChildren<Button>(true);
        modeText = nestedButton != null ? nestedButton.GetComponentInChildren<TextMeshProUGUI>(true) : null;

        rowImage = boundRow.GetComponent<Image>();
        if (rowImage == null) rowImage = boundRow.AddComponent<Image>();
        rowImage.raycastTarget = true;

        rowButton = boundRow.GetComponent<Button>();
        if (rowButton == null) rowButton = boundRow.AddComponent<Button>();
        rowButton.targetGraphic = rowImage;
        rowButton.onClick.RemoveAllListeners();
        rowButton.onClick.AddListener(ToggleCurrentGroup);

        DisableNestedButton();

        GroupUVModeInteractionFix old = FindFirstObjectByType<GroupUVModeInteractionFix>();
        if (old != null) old.enabled = false;
    }

    void Sync()
    {
        if (boundRow == null || rowButton == null || modeText == null) return;
        DisableNestedButton();

        bool predetermined = modeText.text != null && modeText.text.Contains("PREDETERMINED");
        modeText.text = predetermined ? "PREDETERMINED" : "ADJUSTABLE";
        rowButton.interactable = HasAuthoredRects();
        rowImage.color = predetermined
            ? new Color(.12f, .28f, .42f, .98f)
            : new Color(.11f, .13f, .16f, .98f);
    }

    void ToggleCurrentGroup()
    {
        Resolve();
        if (controller == null || viewer == null || toggleModeMethod == null) return;
        toggleModeMethod.Invoke(controller, new object[] { viewer.currentGroupId });
        nextScan = 0f;
    }

    void DisableNestedButton()
    {
        if (nestedButton == null) return;
        nestedButton.interactable = false;
        Image img = nestedButton.GetComponent<Image>();
        if (img != null) img.raycastTarget = false;
        foreach (Graphic graphic in nestedButton.GetComponentsInChildren<Graphic>(true))
            graphic.raycastTarget = false;
    }

    bool HasAuthoredRects()
    {
        TextureUVRectWorkspace workspace = FindFirstObjectByType<TextureUVRectWorkspace>();
        if (workspace == null) return false;
        var rects = workspace.ExportDefinitions();
        return rects != null && rects.Count > 0;
    }
}
