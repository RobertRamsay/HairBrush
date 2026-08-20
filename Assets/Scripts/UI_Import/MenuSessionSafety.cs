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
        boundLoadButton.onClick.RemoveAllListeners();
        boundLoadButton.onClick.AddListener(() =>
        {
            MethodInfo load = typeof(ModelViewer).GetMethod("LoadModel", BindingFlags.Instance | BindingFlags.NonPublic);
            load?.Invoke(viewer, null);
        });
    }

    void BindAuthoredResumeButton()
    {
        if (resumeButton != null) return;

        // First bind the actual authored scene object by name. It is intentionally inactive.
        foreach (Transform t in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (t == null || t.name != "Button_Resume") continue;
            Button b = t.GetComponent<Button>();
            if (b != null)
            {
                BindResume(b);
                return;
            }
        }

        // Fallback for renamed scene objects: support both TMP and legacy Text labels.
        foreach (Button button in FindObjectsByType<Button>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (button == null) continue;
            bool isResume = false;
            TextMeshProUGUI tmp = button.GetComponentInChildren<TextMeshProUGUI>(true);
            if (tmp != null && tmp.text.Trim().ToUpperInvariant() == "RESUME") isResume = true;
            Text legacy = button.GetComponentInChildren<Text>(true);
            if (legacy != null && legacy.text.Trim().ToUpperInvariant() == "RESUME") isResume = true;
            if (!isResume) continue;
            BindResume(button);
            return;
        }
    }

    void BindResume(Button button)
    {
        resumeButton = button;
        resumeButton.onClick.RemoveAllListeners();
        resumeButton.onClick.AddListener(ResumeGroom);
        resumeButton.gameObject.SetActive(false);
    }

    void UpdateResumeVisibility()
    {
        if (resumeButton == null) return;
        bool hasSession = GetLoadedModel() != null || FindObjectsByType<HairCard>(FindObjectsSortMode.None).Length > 0;
        bool menuOpen = viewer.uiContainer != null && viewer.uiContainer.activeInHierarchy;
        resumeButton.gameObject.SetActive(hasSession && menuOpen);
    }

    void ResumeGroom()
    {
        if (viewer == null) return;
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

        // The loop above blanket-enables every renderer to undo whatever the menu hid, which
        // on its own quietly cancelled SOLO every time the user opened and resumed. Handing
        // visibility straight back to the authority re-hides the non-soloed groups if a SOLO
        // is still live, and is a no-op when none is.
        GroupSoloVisibilityAuthority.ApplyVisibility();

        viewer.OnModelLoaded();

        GameObject groom = viewer.groomingSliderPanelGO != null ? viewer.groomingSliderPanelGO : FindNamed("GroomingPanel");
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

        MethodInfo selectGroup = typeof(ModelViewer).GetMethod("SelectGroup", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (selectGroup != null) selectGroup.Invoke(viewer, new object[] { viewer.currentGroupId });
        else FindFirstObjectByType<GroomRootStateAuthority>()?.RestoreRootToViewer(viewer.currentGroupId);

        viewer.ToggleGroomingMode(true);
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
