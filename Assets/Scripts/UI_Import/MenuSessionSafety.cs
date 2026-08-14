using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Keeps Menu navigation reversible and makes cancelled file dialogs true no-ops.
// Uses the authored RESUME button in the menu rather than creating runtime UI.
[DefaultExecutionOrder(9800)]
public class MenuSessionSafety : MonoBehaviour
{
    private ModelViewer viewer;
    private Button boundLoadButton;
    private Button resumeButton;
    private float nextScan;
    private bool resumeInitialised;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<MenuSessionSafety>() != null) return;
        GameObject go = new GameObject("MenuSessionSafety");
        DontDestroyOnLoad(go);
        go.AddComponent<MenuSessionSafety>();
    }

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + .12f;

        if (viewer == null) viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer == null) return;

        BindSafeLoadModel();
        BindAuthoredResumeButton();
        UpdateResumeVisibility();
    }

    void BindSafeLoadModel()
    {
        if (viewer.loadButton == null || boundLoadButton == viewer.loadButton) return;
        boundLoadButton = viewer.loadButton;

        // ModelViewer.LoadModel opens the picker first and returns immediately on Cancel.
        // Only after a valid path is returned does it replace the model/session.
        boundLoadButton.onClick.RemoveAllListeners();
        boundLoadButton.onClick.AddListener(() =>
        {
            MethodInfo load = typeof(ModelViewer).GetMethod("LoadModel", BindingFlags.Instance | BindingFlags.NonPublic);
            load?.Invoke(viewer, null);
        });
    }

    void BindAuthoredResumeButton()
    {
        if (resumeButton != null || viewer.uiContainer == null) return;

        foreach (Button button in viewer.uiContainer.GetComponentsInChildren<Button>(true))
        {
            if (button == null) continue;
            TextMeshProUGUI label = button.GetComponentInChildren<TextMeshProUGUI>(true);
            if (label == null || label.text.Trim().ToUpperInvariant() != "RESUME") continue;

            resumeButton = button;
            resumeButton.onClick.RemoveAllListeners();
            resumeButton.onClick.AddListener(ResumeGroom);

            // The authored button can also be disabled in the scene; keep this as a runtime guard.
            resumeButton.gameObject.SetActive(false);
            resumeInitialised = true;
            break;
        }
    }

    void UpdateResumeVisibility()
    {
        if (resumeButton == null) return;
        bool hasSession = GetLoadedModel() != null || FindObjectsByType<HairCard>(FindObjectsSortMode.None).Length > 0;
        bool menuOpen = viewer.uiContainer != null && viewer.uiContainer.activeInHierarchy;
        bool shouldShow = hasSession && menuOpen;

        if (!resumeInitialised || resumeButton.gameObject.activeSelf != shouldShow)
            resumeButton.gameObject.SetActive(shouldShow);
        resumeInitialised = true;
    }

    void ResumeGroom()
    {
        if (viewer == null) return;

        // Leave every alternate workspace first. ToggleGroomingMode alone only changes one bool.
        SetViewerField("isTextureEditorMode", false);

        GameObject model = GetLoadedModel();
        if (model != null)
        {
            model.SetActive(true);
            foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>(true))
                if (renderer != null) renderer.enabled = true;
        }

        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
        {
            if (card == null) continue;
            card.gameObject.SetActive(true);
            Renderer renderer = card.GetComponent<Renderer>();
            if (renderer != null) renderer.enabled = true;
        }

        // Re-enter through the same setup path used after a successful model load.
        viewer.OnModelLoaded();

        GameObject groom = viewer.groomingSliderPanelGO != null
            ? viewer.groomingSliderPanelGO
            : FindNamed("GroomingPanel");
        if (groom == null)
        {
            viewer.BuildRuntimeGroomingUI();
            groom = viewer.groomingSliderPanelGO;
        }
        if (groom != null) groom.SetActive(true);

        GameObject groups = FindNamed("GroupManagerPanel");
        if (groups == null)
        {
            InvokeViewer("BuildGroupManagementUI");
            groups = FindNamed("GroupManagerPanel");
        }
        if (groups != null) groups.SetActive(true);

        // Restore the selected group through ModelViewer's normal group-selection authority.
        MethodInfo selectGroup = typeof(ModelViewer).GetMethod(
            "SelectGroup", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (selectGroup != null)
            selectGroup.Invoke(viewer, new object[] { viewer.currentGroupId });
        else
            FindFirstObjectByType<GroomRootStateAuthority>()?.RestoreRootToViewer(viewer.currentGroupId);

        viewer.ToggleGroomingMode(true);

        // Hide the menu last, after the editor has been made live again.
        if (viewer.uiContainer != null) viewer.uiContainer.SetActive(false);
    }

    GameObject GetLoadedModel()
    {
        FieldInfo field = typeof(ModelViewer).GetField("loadedModel", BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(viewer) as GameObject;
    }

    void SetViewerField(string name, object value)
    {
        FieldInfo field = typeof(ModelViewer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        field?.SetValue(viewer, value);
    }

    void InvokeViewer(string name)
    {
        MethodInfo method = typeof(ModelViewer).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        method?.Invoke(viewer, null);
    }

    static GameObject FindNamed(string name)
    {
        foreach (Transform t in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (t != null && t.name == name) return t.gameObject;
        return null;
    }
}
