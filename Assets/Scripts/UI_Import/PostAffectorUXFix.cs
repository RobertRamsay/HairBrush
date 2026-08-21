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
        // Lands on the same default SelectionBrushScaleTuning and PostGroupLifetimeAuthority
        // use. This is the third copy of the legacy-0.25 translation; they have to agree, or
        // whichever runs first on a given frame decides the falloff.
        if (Mathf.Approximately(viewer.brushFalloffDistance, .25f))
            viewer.brushFalloffDistance = PostGroupLifetimeAuthority.DefaultPostFalloff;

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
                // Same legacy-0.25 translation as above, applied to already-stored POSTs, so
                // it lands on the same default rather than a second, different number.
                if (Mathf.Approximately(post.falloff, .25f)) post.falloff = PostGroupLifetimeAuthority.DefaultPostFalloff;
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

            // THIS is what actually decides the POST row's column widths. BuildRow's numbers are
            // overwritten here every .05s, so any sizing change has to be made in this method or
            // it silently has no effect.
            //
            // Panel is 360 wide with 10px VerticalLayoutGroup padding either side -> 340 usable.
            // Minus this row's 4+4 padding and 4 gaps of 4 -> 316 for the five columns. The 300
            // below leaves a little slack at the right so DEL never touches the panel edge.
            SetWidth(row.GetChild(0), 58f);  // POST n
            SetWidth(row.GetChild(1), 52f);  // WEIGHT label
            SetWidth(row.GetChild(2), 116f); // slider
            SetWidth(row.GetChild(3), 30f);  // numeric value
            SetWidth(row.GetChild(4), 44f);  // DEL

            ForceSingleLineWeightLabel(row.GetChild(1));
        }
    }

    // "WEIGHT" kept breaking to "WEIGH / T". Widening the box alone was not enough to be sure of
    // it across font assets and canvas scales, so pin the size here (this component wins) and use
    // TMP's <nobr> tag, which forbids the break outright rather than hoping the box is wide
    // enough. Using the tag avoids TextMeshPro's wrapping API, which has been renamed between
    // versions and would tie this file to one of them.
    static void ForceSingleLineWeightLabel(Transform child)
    {
        if (child == null) return;
        TMPro.TextMeshProUGUI label = child.GetComponent<TMPro.TextMeshProUGUI>();
        if (label == null) return;
        if (label.fontSize > 8f) label.fontSize = 8f;
        if (label.text == "WEIGHT") label.text = "<nobr>WEIGHT</nobr>";
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
