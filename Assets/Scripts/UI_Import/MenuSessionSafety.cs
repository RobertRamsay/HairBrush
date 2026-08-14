using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Keeps Menu navigation reversible and makes cancelled file dialogs true no-ops.
// RuntimeNavigationProjectIO historically clears cards/UI before opening Load Model;
// this authority rebinds that button to ModelViewer's transactional LoadModel path.
[DefaultExecutionOrder(9800)]
public class MenuSessionSafety : MonoBehaviour
{
    private ModelViewer viewer;
    private Button boundLoadButton;
    private GameObject resumeButton;
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
        EnsureResumeButton();
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

    void EnsureResumeButton()
    {
        if (resumeButton != null || viewer.uiContainer == null) return;

        Transform parent = viewer.loadButton != null && viewer.loadButton.transform.parent != null
            ? viewer.loadButton.transform.parent
            : viewer.uiContainer.transform;

        Transform existing = parent.Find("ResumeGroomButton_Runtime");
        if (existing != null)
        {
            resumeButton = existing.gameObject;
            Button existingButton = resumeButton.GetComponent<Button>();
            if (existingButton != null)
            {
                existingButton.onClick.RemoveAllListeners();
                existingButton.onClick.AddListener(ResumeGroom);
            }
            return;
        }

        resumeButton = new GameObject(
            "ResumeGroomButton_Runtime",
            typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        resumeButton.transform.SetParent(parent, false);

        RectTransform rect = resumeButton.GetComponent<RectTransform>();
        rect.sizeDelta = new Vector2(0f, 48f);
        LayoutElement layout = resumeButton.GetComponent<LayoutElement>();
        layout.preferredHeight = 48f;
        layout.minHeight = 48f;
        resumeButton.GetComponent<Image>().color = new Color(.20f, .50f, .82f, 1f);

        GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textGO.transform.SetParent(resumeButton.transform, false);
        RectTransform textRect = textGO.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;
        TextMeshProUGUI text = textGO.GetComponent<TextMeshProUGUI>();
        text.text = "RESUME GROOM";
        text.fontSize = 16f;
        text.fontStyle = FontStyles.Bold;
        text.alignment = TextAlignmentOptions.Center;
        text.color = Color.white;
        text.raycastTarget = false;

        resumeButton.GetComponent<Button>().onClick.AddListener(ResumeGroom);
    }

    void UpdateResumeVisibility()
    {
        if (resumeButton == null) return;
        bool hasSession = GetLoadedModel() != null || FindObjectsByType<HairCard>(FindObjectsSortMode.None).Length > 0;
        bool menuOpen = viewer.uiContainer != null && viewer.uiContainer.activeInHierarchy;
        resumeButton.SetActive(hasSession && menuOpen);
    }

    void ResumeGroom()
    {
        if (viewer == null) return;

        if (viewer.uiContainer != null) viewer.uiContainer.SetActive(false);

        GameObject model = GetLoadedModel();
        if (model != null)
        {
            model.SetActive(true);
            foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>(true))
                if (renderer != null) renderer.enabled = true;
        }

        if (viewer.groomingSliderPanelGO != null)
            viewer.groomingSliderPanelGO.SetActive(true);
        else
        {
            GameObject groom = FindNamed("GroomingPanel");
            if (groom != null) groom.SetActive(true);
        }

        GameObject groups = FindNamed("GroupManagerPanel");
        if (groups != null) groups.SetActive(true);

        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
        {
            if (card == null) continue;
            card.gameObject.SetActive(true);
            MeshRenderer mr = card.GetComponent<MeshRenderer>();
            if (mr != null) mr.enabled = true;
        }

        // Restore ModelViewer's normal grooming interaction path as well as visibility.
        viewer.ToggleGroomingMode(true);
    }

    GameObject GetLoadedModel()
    {
        FieldInfo field = typeof(ModelViewer).GetField("loadedModel", BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(viewer) as GameObject;
    }

    static GameObject FindNamed(string name)
    {
        foreach (Transform t in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (t != null && t.name == name) return t.gameObject;
        return null;
    }
}
