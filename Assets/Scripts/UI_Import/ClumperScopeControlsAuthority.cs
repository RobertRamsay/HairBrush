using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Makes CLUMPER surface scope an explicit editor option instead of relying on the
// compact left modifier row, where the old CONTIG/ALL control can be clipped by width.
// The actual mesh authority already reads SurfaceIslandScope every frame, so this UI is
// only a clear, discoverable control for that existing behaviour.
[DefaultExecutionOrder(5252)]
public class ClumperScopeControlsAuthority : MonoBehaviour
{
    private GameObject boundControls;
    private GameObject scopeRow;
    private int boundGroup = -1;
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<ClumperScopeControlsAuthority>() != null) return;
        GameObject go = new GameObject("ClumperScopeControlsAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<ClumperScopeControlsAuthority>();
    }

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + .05f;

        GroupClumperManager manager = FindFirstObjectByType<GroupClumperManager>();
        ModelViewer viewer = FindFirstObjectByType<ModelViewer>();
        if (manager == null || viewer == null || viewer.groomingSliderPanelGO == null)
        {
            ClearBinding();
            return;
        }

        Transform controls = viewer.groomingSliderPanelGO.transform.Find("ClumperControls");
        if (controls == null)
        {
            // ClumperControlsScrollFix reparents this into its scroll content while active.
            controls = FindDeep(viewer.groomingSliderPanelGO.transform, "ClumperControls");
        }
        if (controls == null)
        {
            ClearBinding();
            return;
        }

        int gid = viewer.currentGroupId;
        if (boundControls != controls.gameObject || boundGroup != gid || scopeRow == null)
        {
            boundControls = controls.gameObject;
            boundGroup = gid;
            BuildOrBind(controls, gid);
        }

        Sync(gid);
    }

    void BuildOrBind(Transform controls, int gid)
    {
        Transform existing = controls.Find("ClumperScopeRow");
        if (existing != null)
        {
            scopeRow = existing.gameObject;
            PositionRow(controls, existing);
            return;
        }

        scopeRow = new GameObject("ClumperScopeRow", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        scopeRow.transform.SetParent(controls, false);
        scopeRow.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 34f);

        HorizontalLayoutGroup layout = scopeRow.GetComponent<HorizontalLayoutGroup>();
        layout.spacing = 5f;
        layout.padding = new RectOffset(0, 0, 3, 3);
        layout.childControlWidth = false;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;

        AddLabel(scopeRow.transform, "SCOPE", 70f);
        AddScopeButton(scopeRow.transform, "ALL", false, gid);
        AddScopeButton(scopeRow.transform, "CONTIG", true, gid);

        PositionRow(controls, scopeRow.transform);
    }

    static void PositionRow(Transform controls, Transform row)
    {
        Transform mode = controls.Find("ModeRow");
        if (mode == null) return;
        row.SetSiblingIndex(Mathf.Min(mode.GetSiblingIndex() + 1, controls.childCount - 1));
    }

    void AddScopeButton(Transform parent, string label, bool contiguous, int gid)
    {
        GameObject go = new GameObject(contiguous ? "ScopeContiguous" : "ScopeAll", typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(contiguous ? 105f : 82f, 28f);

        Image image = go.GetComponent<Image>();
        image.color = new Color(.20f, .25f, .32f, 1f);

        Button button = go.GetComponent<Button>();
        button.onClick.AddListener(() =>
        {
            SurfaceIslandScope.SetClumperContiguous(boundGroup >= 0 ? boundGroup : gid, contiguous);
            Sync(boundGroup >= 0 ? boundGroup : gid);
        });

        GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textGO.transform.SetParent(go.transform, false);
        RectTransform tr = textGO.GetComponent<RectTransform>();
        tr.anchorMin = Vector2.zero;
        tr.anchorMax = Vector2.one;
        tr.offsetMin = Vector2.zero;
        tr.offsetMax = Vector2.zero;

        TextMeshProUGUI text = textGO.GetComponent<TextMeshProUGUI>();
        text.text = label;
        text.fontSize = 11f;
        text.fontStyle = FontStyles.Bold;
        text.alignment = TextAlignmentOptions.Center;
        text.color = Color.white;
        text.raycastTarget = false;
    }

    static void AddLabel(Transform parent, string label, float width)
    {
        GameObject textGO = new GameObject("ScopeLabel", typeof(RectTransform), typeof(TextMeshProUGUI));
        textGO.transform.SetParent(parent, false);
        textGO.GetComponent<RectTransform>().sizeDelta = new Vector2(width, 28f);
        TextMeshProUGUI text = textGO.GetComponent<TextMeshProUGUI>();
        text.text = label;
        text.fontSize = 11f;
        text.fontStyle = FontStyles.Bold;
        text.alignment = TextAlignmentOptions.MidlineLeft;
        text.color = new Color(.82f, .86f, .90f, 1f);
        text.raycastTarget = false;
    }

    void Sync(int gid)
    {
        if (scopeRow == null || gid < 0) return;
        bool contiguous = SurfaceIslandScope.IsClumperContiguous(gid);

        Transform all = scopeRow.transform.Find("ScopeAll");
        Transform contig = scopeRow.transform.Find("ScopeContiguous");
        SetSelected(all, !contiguous);
        SetSelected(contig, contiguous);
    }

    static void SetSelected(Transform control, bool selected)
    {
        if (control == null) return;
        Image image = control.GetComponent<Image>();
        if (image != null)
            image.color = selected
                ? new Color(.20f, .55f, .35f, 1f)
                : new Color(.20f, .25f, .32f, 1f);
    }

    static Transform FindDeep(Transform root, string name)
    {
        if (root == null) return null;
        foreach (Transform child in root)
        {
            if (child.name == name) return child;
            Transform nested = FindDeep(child, name);
            if (nested != null) return nested;
        }
        return null;
    }

    void ClearBinding()
    {
        boundControls = null;
        scopeRow = null;
        boundGroup = -1;
    }
}
