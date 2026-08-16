using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Adds a compact scope toggle directly to each CLUMPER row without coupling surface-island
// logic into GroupClumperManager's modifier lifecycle. ALL preserves current behaviour;
// CONTIG restricts deformation/leaders to the connected surface island under the clumper.
[DefaultExecutionOrder(5240)]
public class ClumperContiguousScopeAuthority : MonoBehaviour
{
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<ClumperContiguousScopeAuthority>() != null) return;
        GameObject go = new GameObject("ClumperContiguousScopeAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<ClumperContiguousScopeAuthority>();
    }

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + .12f;

        foreach (RectTransform row in FindObjectsByType<RectTransform>(FindObjectsSortMode.None))
        {
            if (row == null || !row.name.StartsWith("GroupClumper_")) continue;
            if (!int.TryParse(row.name.Substring("GroupClumper_".Length), out int gid)) continue;
            EnsureButton(row, gid);
        }
    }

    void EnsureButton(RectTransform row, int gid)
    {
        Transform existing = row.Find("ContiguousScope");
        if (existing != null)
        {
            UpdateLabel(existing.gameObject, gid);
            return;
        }

        GameObject go = new GameObject("ContiguousScope", typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(row, false);
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(62f, 25f);
        go.GetComponent<Image>().color = new Color(.20f, .25f, .32f);

        GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textGO.transform.SetParent(go.transform, false);
        RectTransform tr = textGO.GetComponent<RectTransform>();
        tr.anchorMin = Vector2.zero;
        tr.anchorMax = Vector2.one;
        tr.offsetMin = Vector2.zero;
        tr.offsetMax = Vector2.zero;
        TextMeshProUGUI text = textGO.GetComponent<TextMeshProUGUI>();
        text.fontSize = 9f;
        text.alignment = TextAlignmentOptions.Center;
        text.color = Color.white;
        text.raycastTarget = false;

        go.GetComponent<Button>().onClick.AddListener(() =>
        {
            SurfaceIslandScope.SetClumperContiguous(gid, !SurfaceIslandScope.IsClumperContiguous(gid));
            UpdateLabel(go, gid);
        });

        UpdateLabel(go, gid);
    }

    static void UpdateLabel(GameObject go, int gid)
    {
        if (go == null) return;
        bool contiguous = SurfaceIslandScope.IsClumperContiguous(gid);
        TextMeshProUGUI text = go.GetComponentInChildren<TextMeshProUGUI>(true);
        if (text != null) text.text = contiguous ? "CONTIG" : "ALL";
        Image image = go.GetComponent<Image>();
        if (image != null)
            image.color = contiguous ? new Color(.20f, .55f, .35f) : new Color(.20f, .25f, .32f);
    }
}
