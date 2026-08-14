using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Groom workspace counterpart to TextureEditorManager's single destination button.
// The authored/runtime Groom UI still builds a two-tab row; this authority collapses it
// to one centered TEXTURE MODE button without touching the mode-switch callback itself.
[DefaultExecutionOrder(9400)]
public class SingleModeSwitchAuthority : MonoBehaviour
{
    private GameObject lastRow;
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<SingleModeSwitchAuthority>() != null) return;
        GameObject go = new GameObject("SingleModeSwitchAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<SingleModeSwitchAuthority>();
    }

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + .25f;

        GameObject groomPanel = FindNamed("GroomingPanel");
        if (groomPanel == null || !groomPanel.activeInHierarchy) return;

        Transform row = groomPanel.transform.Find("PanelTabRow");
        if (row == null) return;
        if (lastRow == row.gameObject && row.childCount == 2 && !row.GetChild(0).gameObject.activeSelf) return;

        Transform groomTab = row.Find("GroomTabButton");
        Transform textureTab = row.Find("TexTabButton");
        if (textureTab == null) return;

        if (groomTab != null) groomTab.gameObject.SetActive(false);

        HorizontalLayoutGroup layout = row.GetComponent<HorizontalLayoutGroup>();
        if (layout != null)
        {
            layout.childControlWidth = false;
            layout.childForceExpandWidth = false;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.spacing = 0f;
        }

        LayoutElement le = textureTab.GetComponent<LayoutElement>();
        if (le == null) le = textureTab.gameObject.AddComponent<LayoutElement>();
        le.preferredWidth = 300f;
        le.minWidth = 300f;
        le.flexibleWidth = 0f;

        Image image = textureTab.GetComponent<Image>();
        if (image != null) image.color = new Color(.20f, .50f, .82f, 1f);

        Button button = textureTab.GetComponent<Button>();
        if (button != null) button.interactable = true;

        TextMeshProUGUI label = textureTab.GetComponentInChildren<TextMeshProUGUI>(true);
        if (label != null) label.text = "TEXTURE MODE";

        lastRow = row.gameObject;
    }

    static GameObject FindNamed(string name)
    {
        foreach (Transform t in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (t != null && t.name == name) return t.gameObject;
        return null;
    }
}
