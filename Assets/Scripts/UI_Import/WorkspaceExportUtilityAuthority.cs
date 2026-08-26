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
            GameObject button = CreateButton(top, "ExportOBJButton", BuildEdition.ExportLabel, new Color(.20f, .38f, .62f));
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

        Transform save = row.Find("SAVEPROJButton");
        if (save == null) save = FindButtonByLabel(row, "SAVE PROJ");
        if (save == null)
        {
            GameObject button = CreateButton(row, "SAVEPROJButton", "SAVE PROJ", new Color(.20f, .50f, .30f), true);
            button.GetComponent<Button>().onClick.AddListener(InvokeSaveProject);
        }

        // BY NAME FIRST, label only as a fallback, and the demo label has to be in that
        // fallback too. This scan runs every .1s and creates the button whenever it fails to
        // find one: keying the search on a literal "EXPORT OBJ" while the button that was
        // created reads "EXPORT OBJ (PRO)" would miss it ten times a second and stack up a new
        // export button on every tick. The name never changes, so it is the reliable half.
        Transform export = row.Find("EXPORTOBJButton");
        if (export == null) export = FindButtonByLabel(row, BuildEdition.ExportProLabel);
        if (export == null) export = FindButtonByLabel(row, BuildEdition.ExportDemoLabel);
        if (export == null)
        {
            GameObject button = CreateButton(row, "EXPORTOBJButton", BuildEdition.ExportLabel, new Color(.20f, .38f, .62f), true);
            button.GetComponent<Button>().onClick.AddListener(HairObjExporter.ExportInteractive);
        }

        // Texture mode has a 560px panel with horizontal padding + spacing. Let the three
        // utilities share whatever width is actually available instead of forcing 165px each.
        HorizontalLayoutGroup layout = row.GetComponent<HorizontalLayoutGroup>();
        if (layout != null)
        {
            layout.childControlWidth = true;
            layout.childForceExpandWidth = true;
            layout.spacing = 8f;
        }

        foreach (Transform child in row)
        {
            LayoutElement le = child.GetComponent<LayoutElement>();
            if (le != null)
            {
                le.minWidth = 0f;
                le.preferredWidth = 0f;
                le.flexibleWidth = 1f;
            }

            TextMeshProUGUI text = child.GetComponentInChildren<TextMeshProUGUI>(true);
            if (text != null)
            {
                text.textWrappingMode = TextWrappingModes.NoWrap;
                text.overflowMode = TextOverflowModes.Ellipsis;
                text.fontSize = 14f;
            }
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
            le.minWidth = 0f;
            le.preferredWidth = 0f;
            le.flexibleWidth = 1f;
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
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Ellipsis;
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
