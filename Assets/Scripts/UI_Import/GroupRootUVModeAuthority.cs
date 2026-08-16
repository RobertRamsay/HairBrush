using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// UV source is group routing metadata, not a localized groom override. Surface the mode on
// each Hair Group root row and hide the duplicate UV MODE row from the right grooming stack.
// Detailed PREDETERMINED range/seed controls remain in the right panel when that mode is active.
[DefaultExecutionOrder(6800)]
public class GroupRootUVModeAuthority : MonoBehaviour
{
    private ModelViewer viewer;
    private GroupPredeterminedUVController controller;
    private MethodInfo toggleModeMethod;
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<GroupRootUVModeAuthority>() != null) return;
        GameObject go = new GameObject("GroupRootUVModeAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<GroupRootUVModeAuthority>();
    }

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + .08f;
        Resolve();
        if (viewer == null || controller == null || toggleModeMethod == null) return;

        HideRightModeRow();
        MaintainGroupButtons();
    }

    void Resolve()
    {
        if (viewer == null) viewer = FindFirstObjectByType<ModelViewer>();
        if (controller == null)
        {
            controller = FindFirstObjectByType<GroupPredeterminedUVController>();
            if (controller != null)
                toggleModeMethod = typeof(GroupPredeterminedUVController).GetMethod("ToggleMode", BindingFlags.Instance | BindingFlags.NonPublic);
        }
    }

    void HideRightModeRow()
    {
        if (viewer == null || viewer.groomingSliderPanelGO == null) return;
        Transform row = FindDeep(viewer.groomingSliderPanelGO.transform, "GroupUVMode_Row");
        if (row != null && row.gameObject.activeSelf) row.gameObject.SetActive(false);
    }

    void MaintainGroupButtons()
    {
        foreach (RectTransform row in FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (row == null || !row.name.StartsWith("GroupItem_")) continue;
            if (!int.TryParse(row.name.Substring("GroupItem_".Length), out int gid)) continue;

            Transform label = row.Find("LabelButton");
            Transform solo = row.Find("SoloButton");
            if (label == null || solo == null) continue;

            // Make enough room for a compact group-level UV source button.
            RectTransform labelRT = label as RectTransform;
            if (labelRT != null && labelRT.sizeDelta.x > 120f)
                labelRT.sizeDelta = new Vector2(112f, labelRT.sizeDelta.y);

            RectTransform soloRT = solo as RectTransform;
            if (soloRT != null && soloRT.sizeDelta.x > 54f)
                soloRT.sizeDelta = new Vector2(52f, soloRT.sizeDelta.y);

            HorizontalLayoutGroup h = row.GetComponent<HorizontalLayoutGroup>();
            if (h != null) h.spacing = 5f;

            Transform existing = row.Find("GroupUVModeButton");
            Button button;
            TextMeshProUGUI text;
            if (existing == null)
            {
                GameObject go = new GameObject("GroupUVModeButton", typeof(RectTransform), typeof(Image), typeof(Button));
                go.transform.SetParent(row, false);
                go.GetComponent<RectTransform>().sizeDelta = new Vector2(74f, 36f);
                button = go.GetComponent<Button>();
                button.onClick.AddListener(() => Toggle(gid));

                GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
                textGO.transform.SetParent(go.transform, false);
                RectTransform tr = textGO.GetComponent<RectTransform>();
                tr.anchorMin = Vector2.zero;
                tr.anchorMax = Vector2.one;
                tr.offsetMin = Vector2.zero;
                tr.offsetMax = Vector2.zero;
                text = textGO.GetComponent<TextMeshProUGUI>();
                text.fontSize = 10f;
                text.fontStyle = FontStyles.Bold;
                text.alignment = TextAlignmentOptions.Center;
                text.color = Color.white;
                text.raycastTarget = false;
            }
            else
            {
                button = existing.GetComponent<Button>();
                text = existing.GetComponentInChildren<TextMeshProUGUI>(true);
            }

            bool predetermined = IsPredetermined(gid);
            if (text != null) text.text = predetermined ? "UV: PRE" : "UV: ADJ";
            Image image = button != null ? button.GetComponent<Image>() : null;
            if (image != null)
                image.color = predetermined ? new Color(.20f, .50f, .80f, 1f) : new Color(.28f, .28f, .28f, 1f);

            // Keep the utility button before SOLO so the group row reads Name | UV | SOLO.
            if (existing == null)
                goBeforeSolo(row, go: row.Find("GroupUVModeButton"), solo: solo);
        }
    }

    void Toggle(int gid)
    {
        if (controller == null || toggleModeMethod == null) return;
        toggleModeMethod.Invoke(controller, new object[] { gid });
        nextScan = 0f;
    }

    bool IsPredetermined(int gid)
    {
        GroupSaveData snapshot = new GroupSaveData { groupId = gid };
        controller.PopulateGroupSave(snapshot);
        return snapshot.usePredeterminedUVs;
    }

    static void goBeforeSolo(RectTransform row, Transform go, Transform solo)
    {
        if (row == null || go == null || solo == null) return;
        go.SetSiblingIndex(Mathf.Clamp(solo.GetSiblingIndex(), 0, row.childCount - 1));
    }

    static Transform FindDeep(Transform root, string name)
    {
        if (root == null) return null;
        if (root.name == name) return root;
        for (int i = 0; i < root.childCount; i++)
        {
            Transform found = FindDeep(root.GetChild(i), name);
            if (found != null) return found;
        }
        return null;
    }
}
