using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

// Deterministic POST clump. Each frame we first force PostAffectorManager to rebuild the
// current unclumped POST result, snapshot those vertices, then apply one fixed lerp toward
// the anchor card. This keeps Clump non-accumulating while Bend/Twist/etc remain live.
[DefaultExecutionOrder(5000)]
public class NearestCardClumpController : MonoBehaviour
{
    private readonly Dictionary<int, float> strengthByPost = new();

    private PostAffectorManager posts;
    private ModelViewer viewer;
    private FieldInfo groupsField;
    private FieldInfo activeIdField;
    private FieldInfo hasSelectionField;
    private MethodInfo createSliderMethod;
    private MethodInfo applyAllMethod;

    private GameObject sliderRow;
    private Slider slider;
    private int uiPostId = -1;
    private bool hadClumpLastFrame;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<NearestCardClumpController>() != null) return;
        GameObject go = new GameObject("NearestCardClumpController");
        DontDestroyOnLoad(go);
        go.AddComponent<NearestCardClumpController>();
    }

    void Update()
    {
        Resolve();
        if (viewer == null || posts == null) return;
        SyncLivePosts();
        MaintainUI();
    }

    void LateUpdate()
    {
        Resolve();
        if (viewer == null || posts == null) return;

        List<PostAffectorManager.PostAffector> live = AllPosts()
            .Where(p => p != null && GetStrength(p.id) > 0.0001f)
            .ToList();

        // Whether clump is active or has just been turned off/deleted, begin from a fresh
        // authoritative POST evaluation. This erases last frame's mesh-only clump result.
        if (live.Count > 0 || hadClumpLastFrame)
            applyAllMethod?.Invoke(posts, null);

        if (live.Count == 0)
        {
            hadClumpLastFrame = false;
            return;
        }

        HairCard[] allCards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        HashSet<int> affectedGroups = new(live.Select(p => p.groupId));

        // ApplyAll above has just regenerated the current Bend/Twist/Length/etc result.
        // Freeze that clean result for THIS FRAME ONLY so anchors and neighbours all sample
        // the same upstream pose and never sample already-clumped geometry.
        Dictionary<HairCard, Vector3[]> clean = new();
        foreach (HairCard card in allCards)
        {
            if (card == null || !affectedGroups.Contains(card.groupId)) continue;
            MeshFilter mf = card.GetComponent<MeshFilter>();
            if (mf != null && mf.mesh != null)
                clean[card] = (Vector3[])mf.mesh.vertices.Clone();
        }

        Dictionary<int, HairCard> anchorByPost = new();
        foreach (PostAffectorManager.PostAffector post in live)
        {
            HairCard anchor = FindAnchor(post, allCards);
            if (anchor != null && clean.ContainsKey(anchor))
                anchorByPost[post.id] = anchor;
        }

        foreach (HairCard card in allCards)
        {
            if (card == null || !clean.TryGetValue(card, out Vector3[] sourceClean)) continue;

            List<(HairCard anchor, float influence)> influences = new();
            foreach (PostAffectorManager.PostAffector post in live)
            {
                if (post.groupId != card.groupId) continue;
                if (!anchorByPost.TryGetValue(post.id, out HairCard anchor) || anchor == null || anchor == card) continue;

                float influence = SpatialWeight(card, post) * Mathf.Clamp01(post.weight) * GetStrength(post.id);
                if (influence > 0.0001f)
                    influences.Add((anchor, Mathf.Clamp01(influence)));
            }

            if (influences.Count > 0)
                ApplyFixedLerp(card, sourceClean, clean, influences);
        }

        hadClumpLastFrame = true;
    }

    void Resolve()
    {
        if (posts == null)
        {
            posts = FindFirstObjectByType<PostAffectorManager>();
            if (posts != null)
            {
                BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                groupsField = typeof(PostAffectorManager).GetField("groups", flags);
                activeIdField = typeof(PostAffectorManager).GetField("activeId", flags);
                applyAllMethod = typeof(PostAffectorManager).GetMethod("ApplyAll", flags);
            }
        }

        if (viewer == null)
        {
            viewer = FindFirstObjectByType<ModelViewer>();
            if (viewer != null)
            {
                BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                hasSelectionField = typeof(ModelViewer).GetField("hasSelectionHotspot", flags);
                createSliderMethod = typeof(ModelViewer).GetMethod("CreateSliderUI", flags);
            }
        }
    }

    Dictionary<int, List<PostAffectorManager.PostAffector>> Groups()
    {
        return groupsField?.GetValue(posts) as Dictionary<int, List<PostAffectorManager.PostAffector>>;
    }

    IEnumerable<PostAffectorManager.PostAffector> AllPosts()
    {
        Dictionary<int, List<PostAffectorManager.PostAffector>> groups = Groups();
        if (groups == null) yield break;
        foreach (List<PostAffectorManager.PostAffector> list in groups.Values)
            if (list != null)
                foreach (PostAffectorManager.PostAffector post in list)
                    if (post != null) yield return post;
    }

    PostAffectorManager.PostAffector ActivePost()
    {
        if (activeIdField == null) return null;
        int id = activeIdField.GetValue(posts) is int value ? value : -1;
        return id < 0 ? null : AllPosts().FirstOrDefault(p => p.id == id);
    }

    bool InPostMode()
    {
        return hasSelectionField != null && hasSelectionField.GetValue(viewer) is bool selected && selected;
    }

    float GetStrength(int postId)
    {
        return strengthByPost.TryGetValue(postId, out float value) ? value : 0f;
    }

    void SetStrength(int postId, float value)
    {
        strengthByPost[postId] = Mathf.Clamp01(value);
    }

    void SyncLivePosts()
    {
        HashSet<int> live = new(AllPosts().Select(p => p.id));
        foreach (int id in live)
            if (!strengthByPost.ContainsKey(id)) strengthByPost[id] = 0f;
        foreach (int dead in strengthByPost.Keys.Where(id => !live.Contains(id)).ToArray())
            strengthByPost.Remove(dead);
    }

    void MaintainUI()
    {
        PostAffectorManager.PostAffector active = ActivePost();
        if (viewer.groomingSliderPanelGO == null || createSliderMethod == null || !InPostMode() || active == null)
        {
            DestroyUI();
            return;
        }

        if (sliderRow != null && uiPostId != active.id)
            DestroyUI();

        if (sliderRow == null)
        {
            uiPostId = active.id;
            float current = GetStrength(active.id);
            int capturedId = active.id;
            UnityAction<float> changed = value => SetStrength(capturedId, value);
            object[] args = { viewer.groomingSliderPanelGO.transform, "Clump", 0f, 1f, current, changed, null, 44f, 16 };
            sliderRow = createSliderMethod.Invoke(viewer, args) as GameObject;
            slider = args[6] as Slider;
            PlaceSlider();
        }

        float wanted = GetStrength(active.id);
        if (slider != null && !Mathf.Approximately(slider.value, wanted))
            slider.SetValueWithoutNotify(wanted);

        TextMeshProUGUI label = sliderRow != null ? sliderRow.GetComponentInChildren<TextMeshProUGUI>(true) : null;
        if (label != null) label.text = "Clump: " + wanted.ToString("F3");
    }

    void PlaceSlider()
    {
        if (sliderRow == null || viewer.groomingSliderPanelGO == null) return;
        Transform panel = viewer.groomingSliderPanelGO.transform;
        Transform anchor = panel.Find("Twist Angle_Row");
        if (anchor == null) anchor = panel.Find("Bend Angle_Row");
        if (anchor != null)
            sliderRow.transform.SetSiblingIndex(Mathf.Min(anchor.GetSiblingIndex() + 1, panel.childCount - 1));
    }

    void DestroyUI()
    {
        if (sliderRow != null) Destroy(sliderRow);
        sliderRow = null;
        slider = null;
        uiPostId = -1;
    }

    static HairCard FindAnchor(PostAffectorManager.PostAffector post, HairCard[] cards)
    {
        HairCard best = null;
        float bestD2 = float.PositiveInfinity;
        foreach (HairCard card in cards)
        {
            if (card == null || card.groupId != post.groupId) continue;
            float d2 = (RootWorld(card) - post.center).sqrMagnitude;
            if (d2 < bestD2) { bestD2 = d2; best = card; }
        }
        return best;
    }

    static float SpatialWeight(HairCard card, PostAffectorManager.PostAffector post)
    {
        float distance = Vector3.Distance(RootWorld(card), post.center);
        float radius = Mathf.Max(0.0001f, post.radius);
        float falloff = Mathf.Max(0f, post.falloff);
        if (distance <= radius) return 1f;
        if (falloff <= 0.0001f || distance >= radius + falloff) return 0f;
        return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(radius + falloff, radius, distance));
    }

    static Vector3 RootWorld(HairCard card)
    {
        Vector3 root = card.GetSpawnHitPoint();
        return root == Vector3.zero ? card.transform.position : root;
    }

    static void ApplyFixedLerp(
        HairCard source,
        Vector3[] sourceClean,
        Dictionary<HairCard, Vector3[]> clean,
        List<(HairCard anchor, float influence)> influences)
    {
        MeshFilter mf = source.GetComponent<MeshFilter>();
        if (mf == null || mf.mesh == null || sourceClean == null) return;

        Vector3[] vertices = (Vector3[])sourceClean.Clone();
        int rows = vertices.Length / 2;
        if (rows < 2) return;

        for (int row = 1; row < rows; row++)
        {
            float t = (float)row / (rows - 1);
            float alongLength = t * t * (3f - 2f * t);
            int li = row * 2;
            int ri = li + 1;

            Vector3 left = sourceClean[li];
            Vector3 right = sourceClean[ri];
            Vector3 baselineCenter = (left + right) * 0.5f;
            Vector3 halfSpan = (right - left) * 0.5f;

            Vector3 weightedTarget = Vector3.zero;
            float targetWeight = 0f;
            float combined = 0f;

            foreach (var entry in influences)
            {
                if (!clean.TryGetValue(entry.anchor, out Vector3[] anchorClean)) continue;
                float w = Mathf.Clamp01(entry.influence * alongLength);
                if (w <= 0f) continue;

                Vector3 anchorWorld = SampleCentreWorld(entry.anchor, anchorClean, t);
                Vector3 anchorLocal = source.transform.InverseTransformPoint(anchorWorld);
                weightedTarget += anchorLocal * w;
                targetWeight += w;
                combined = 1f - ((1f - combined) * (1f - w));
            }

            if (targetWeight <= 0f || combined <= 0f) continue;
            Vector3 target = weightedTarget / targetWeight;
            Vector3 center = Vector3.Lerp(baselineCenter, target, Mathf.Clamp01(combined));
            vertices[li] = center - halfSpan;
            vertices[ri] = center + halfSpan;
        }

        mf.mesh.vertices = vertices;
        mf.mesh.RecalculateNormals();
        mf.mesh.RecalculateBounds();
    }

    static Vector3 SampleCentreWorld(HairCard card, Vector3[] vertices, float t)
    {
        int rows = vertices.Length / 2;
        if (rows <= 0) return card.transform.position;
        if (rows == 1) return card.transform.TransformPoint((vertices[0] + vertices[1]) * 0.5f);

        float rowF = Mathf.Clamp01(t) * (rows - 1);
        int a = Mathf.Clamp(Mathf.FloorToInt(rowF), 0, rows - 1);
        int b = Mathf.Min(a + 1, rows - 1);
        float f = rowF - a;
        Vector3 ca = (vertices[a * 2] + vertices[a * 2 + 1]) * 0.5f;
        Vector3 cb = (vertices[b * 2] + vertices[b * 2 + 1]) * 0.5f;
        return card.transform.TransformPoint(Vector3.Lerp(ca, cb, f));
    }
}
