using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// SAVE PROJ and EXPORT OBJ are workspace utilities, not groom modifiers. Keep them available
// in Groom, CLUMPER and Texture Editor regardless of which editable controls currently own
// the rest of the right panel.
[DefaultExecutionOrder(9400)]
public class WorkspaceExportUtilityAuthority : MonoBehaviour
{
    private ModelViewer viewer;
    private MethodInfo saveProjectMethod;
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<WorkspaceExportUtilityAuthority>() != null) return;
        GameObject go = new GameObject("WorkspaceExportUtilityAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<WorkspaceExportUtilityAuthority>();
    }

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + .1f;

        if (viewer == null)
        {
            viewer = FindFirstObjectByType<ModelViewer>();
            if (viewer != null)
                saveProjectMethod = typeof(ModelViewer).GetMethod("SaveProject", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        }
        if (viewer == null) return;

        EnsureGroomAndClumperUtilities();
        EnsureTextureUtilities();
    }

    void EnsureGroomAndClumperUtilities()
    {
        if (viewer.groomingSliderPanelGO == null) return;
        Transform top = viewer.groomingSliderPanelGO.transform.Find("TopControlsRow");
        if (top == null) return;

        // CLUMPER used to hide the ordinary groom panel. This row is explicitly persistent.
        if (!top.gameObject.activeSelf) top.gameObject.SetActive(true);

        Transform save = top.Find("SaveProjectButton");
        if (save == null)
        {
            GameObject button = CreateButton(top, "SaveProjectButton", "SAVE PROJ", new Color(.20f, .50f, .30f));
            button.GetComponent<Button>().onClick.AddListener(InvokeSaveProject);
        }
        else if (!save.gameObject.activeSelf) save.gameObject.SetActive(true);

        Transform export = top.Find("ExportOBJButton");
        if (export == null)
        {
            GameObject button = CreateButton(top, "ExportOBJButton", "EXPORT OBJ", new Color(.20f, .38f, .62f));
            button.GetComponent<Button>().onClick.AddListener(HairObjExporter.ExportInteractive);
        }
        else if (!export.gameObject.activeSelf) export.gameObject.SetActive(true);

        Transform reset = top.Find("ResetButton");
        if (reset != null && !reset.gameObject.activeSelf) reset.gameObject.SetActive(true);

        HorizontalLayoutGroup layout = top.GetComponent<HorizontalLayoutGroup>();
        if (layout != null)
        {
            layout.childControlWidth = true;
            layout.childForceExpandWidth = true;
        }
    }

    void EnsureTextureUtilities()
    {
        Transform panel = FindNamed("TextureEditorPanel");
        if (panel == null) return;
        Transform row = panel.Find("ModeRow");
        if (row == null) return;

        Transform save = FindButtonByLabel(row, "SAVE PROJ");
        if (save == null)
        {
            GameObject button = CreateButton(row, "SAVEPROJButton", "SAVE PROJ", new Color(.20f, .50f, .30f), true);
            button.GetComponent<Button>().onClick.AddListener(InvokeSaveProject);
        }

        Transform export = FindButtonByLabel(row, "EXPORT OBJ");
        if (export == null)
        {
            GameObject button = CreateButton(row, "EXPORTOBJButton", "EXPORT OBJ", new Color(.20f, .38f, .62f), true);
            button.GetComponent<Button>().onClick.AddListener(HairObjExporter.ExportInteractive);
        }

        // Three compact utilities fit the same 560px texture-editor panel cleanly.
        foreach (Transform child in row)
        {
            LayoutElement le = child.GetComponent<LayoutElement>();
            if (le == null) continue;
            le.minWidth = 160f;
            le.preferredWidth = 165f;
        }
    }

    void InvokeSaveProject()
    {
        if (viewer == null) viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer == null) return;
        if (saveProjectMethod == null)
            saveProjectMethod = typeof(ModelViewer).GetMethod("SaveProject", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        saveProjectMethod?.Invoke(viewer, null);
    }

    static GameObject CreateButton(Transform parent, string name, string label, Color color, bool addLayoutElement = false)
    {
        GameObject go = addLayoutElement
            ? new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement))
            : new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 40f);
        go.GetComponent<Image>().color = color;

        if (addLayoutElement)
        {
            LayoutElement le = go.GetComponent<LayoutElement>();
            le.minWidth = 160f;
            le.preferredWidth = 165f;
            le.preferredHeight = 64f;
        }

        GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textGO.transform.SetParent(go.transform, false);
        RectTransform rt = textGO.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        TextMeshProUGUI text = textGO.GetComponent<TextMeshProUGUI>();
        text.text = label;
        text.fontSize = 14f;
        text.fontStyle = FontStyles.Bold;
        text.alignment = TextAlignmentOptions.Center;
        text.color = Color.white;
        text.raycastTarget = false;
        return go;
    }

    static Transform FindButtonByLabel(Transform parent, string label)
    {
        foreach (Transform child in parent)
        {
            TextMeshProUGUI text = child.GetComponentInChildren<TextMeshProUGUI>(true);
            if (text != null && text.text == label) return child;
        }
        return null;
    }

    static Transform FindNamed(string name)
    {
        foreach (Transform t in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (t != null && t.name == name) return t;
        return null;
    }
}
