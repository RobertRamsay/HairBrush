using System.Collections;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

// Small compatibility/UI layer for the persistent Ctrl+Click POST affectors.
// Keeps the legacy ModelViewer selection UI from leaking into the new modifier model.
[DefaultExecutionOrder(3600)]
public class PostAffectorUXFix : MonoBehaviour
{
    private ModelViewer viewer;
    private PostAffectorManager manager;
    private FieldInfo strengthRowField;
    private FieldInfo groupsField;
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<PostAffectorUXFix>() != null) return;
        GameObject go = new GameObject("PostAffectorUXFix");
        DontDestroyOnLoad(go);
        go.AddComponent<PostAffectorUXFix>();
    }

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + .05f;

        if (viewer == null)
        {
            viewer = FindFirstObjectByType<ModelViewer>();
            if (viewer != null)
                strengthRowField = typeof(ModelViewer).GetField("strengthRowGO", BindingFlags.Instance | BindingFlags.NonPublic);
        }

        if (manager == null)
        {
            manager = FindFirstObjectByType<PostAffectorManager>();
            if (manager != null)
                groupsField = typeof(PostAffectorManager).GetField("groups", BindingFlags.Instance | BindingFlags.NonPublic);
        }

        if (viewer == null || manager == null) return;

        // ModelViewer's legacy Ctrl selection still seeds Falloff at 0.25 on each click.
        // In the POST workflow that is far too broad, so translate only that legacy seed.
        if (Mathf.Approximately(viewer.brushFalloffDistance, .25f))
            viewer.brushFalloffDistance = .05f;

        ClampLegacyPostFalloffs();
        HideRightSideWeight();
        CompactPostRows();
    }

    void ClampLegacyPostFalloffs()
    {
        IDictionary groups = groupsField?.GetValue(manager) as IDictionary;
        if (groups == null) return;

        foreach (DictionaryEntry entry in groups)
        {
            IEnumerable list = entry.Value as IEnumerable;
            if (list == null) continue;
            foreach (object item in list)
            {
                PostAffectorManager.PostAffector post = item as PostAffectorManager.PostAffector;
                if (post == null) continue;
                if (Mathf.Approximately(post.falloff, .25f)) post.falloff = .05f;
            }
        }
    }

    void HideRightSideWeight()
    {
        GameObject row = strengthRowField?.GetValue(viewer) as GameObject;
        if (row != null && row.activeSelf)
            row.SetActive(false);
    }

    void CompactPostRows()
    {
        foreach (RectTransform row in FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (!row.name.StartsWith("PostAffector_")) continue;
            if (row.childCount < 5) continue;

            HorizontalLayoutGroup layout = row.GetComponent<HorizontalLayoutGroup>();
            if (layout != null)
            {
                layout.padding = new RectOffset(4, 4, 4, 4);
                layout.spacing = 4f;
                layout.childControlWidth = false;
                layout.childForceExpandWidth = false;
            }

            SetWidth(row.GetChild(0), 58f); // POST n
            SetWidth(row.GetChild(1), 40f); // WEIGHT label
            SetWidth(row.GetChild(2), 88f); // slider
            SetWidth(row.GetChild(3), 28f); // numeric value
            SetWidth(row.GetChild(4), 30f); // [-]
        }
    }

    static void SetWidth(Transform child, float width)
    {
        RectTransform rect = child as RectTransform;
        if (rect == null) return;
        rect.sizeDelta = new Vector2(width, rect.sizeDelta.y);

        LayoutElement le = child.GetComponent<LayoutElement>();
        if (le != null)
        {
            le.minWidth = width;
            le.preferredWidth = width;
            le.flexibleWidth = 0f;
        }
    }
}
