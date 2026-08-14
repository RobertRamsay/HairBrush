using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

// Stateless POST clumping.
// The card whose root is nearest the POST marker becomes that POST's anchor.
// Other cards inside the POST radius/falloff magnetise toward the anchor centreline.
// This is mesh-only: it never writes HairCard canonical state or selection weights.
[DefaultExecutionOrder(5000)]
public class NearestCardClumpController : MonoBehaviour
{
    private readonly Dictionary<int, float> strengthByPost = new();
    private readonly HashSet<HairCard> clumpedLastFrame = new();

    private PostAffectorManager posts;
    private ModelViewer viewer;
    private FieldInfo groupsField;
    private FieldInfo activeIdField;
    private FieldInfo hasSelectionField;
    private MethodInfo createSliderMethod;

    private GameObject sliderRow;
    private Slider slider;
    private int uiPostId = -1;

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

        // Hard non-accumulation rule: first restore every mesh touched last frame from
        // the current evaluated HairCard parameters. This also guarantees that Clump=0
        // and deleting a POST immediately return the ordinary groom result.
        foreach (HairCard card in clumpedLastFrame.ToArray())
        {
            if (card != null) card.GenerateMesh();
        }
        clumpedLastFrame.Clear();

        List<PostAffectorManager.PostAffector> live = AllPosts()
            .Where(p => p != null && GetStrength(p.id) > 0.0001f)
            .ToList();
        if (live.Count == 0) return;

        HairCard[] allCards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        HashSet<int> affectedGroups = new(live.Select(p => p.groupId));

        // Freeze one clean, pre-clump snapshot for every card in participating groups.
        // HairCard.GenerateMesh derives solely from the current evaluated groom values,
        // so no displayed clump result is ever used as the next frame's input.
        Dictionary<HairCard, Vector3[]> clean = new();
        foreach (HairCard card in allCards)
        {
            if (card == null || !affectedGroups.Contains(card.groupId)) continue;
            card.GenerateMesh();
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

            List<(PostAffectorManager.PostAffector post, HairCard anchor, float influence)> influences = new();
            foreach (PostAffectorManager.PostAffector post in live)
            {
                if (post.groupId != card.groupId) continue;
                if (!anchorByPost.TryGetValue(post.id, out HairCard anchor) || anchor == null || anchor == card) continue;

                float spatial = SpatialWeight(card, post);
                float influence = spatial * Mathf.Clamp01(post.weight) * GetStrength(post.id);
                if (influence > 0.0001f)
                    influences.Add((post, anchor, Mathf.Clamp01(influence)));
            }

            if (influences.Count == 0) continue;
            ApplyClump(card, sourceClean, clean, influences);
            clumpedLastFrame.Add(card);
        }
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
        UpdateLabel(wanted);
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

    void UpdateLabel(float value)
    {
        if (sliderRow == null) return;
        TextMeshProUGUI label = sliderRow.GetComponentInChildren<TextMeshProUGUI>(true);
        if (label != null) label.text = "Clump: " + value.ToString("F3");
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
            if (d2 < bestD2)
            {
                bestD2 = d2;
                best = card;
            }
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

    static void ApplyClump(
        HairCard source,
        Vector3[] sourceClean,
        Dictionary<HairCard, Vector3[]> clean,
        List<(PostAffectorManager.PostAffector post, HairCard anchor, float influence)> influences)
    {
        MeshFilter mf = source.GetComponent<MeshFilter>();
        if (mf == null || mf.mesh == null) return;

        Vector3[] vertices = (Vector3[])sourceClean.Clone();
        int rows = vertices.Length / 2;
        if (rows < 2) return;

        for (int row = 1; row < rows; row++)
        {
            float t = (float)row / (rows - 1);
            // Root is fixed; matching/magnetism grows along the strand toward the tip.
            float alongLength = t * t * (3f - 2f * t);

            int li = row * 2;
            int ri = li + 1;
            Vector3 left = vertices[li];
            Vector3 right = vertices[ri];
            Vector3 ownCenter = (left + right) * 0.5f;
            Vector3 halfSpan = (right - left) * 0.5f;

            Vector3 weightedTarget = Vector3.zero;
            float total = 0f;
            float combined = 0f;

            foreach (var entry in influences)
            {
                if (!clean.TryGetValue(entry.anchor, out Vector3[] anchorClean)) continue;
                float w = Mathf.Clamp01(entry.influence * alongLength);
                if (w <= 0f) continue;
                Vector3 anchorWorld = SampleCentreWorld(entry.anchor, anchorClean, t);
                Vector3 anchorLocal = source.transform.InverseTransformPoint(anchorWorld);
                weightedTarget += anchorLocal * w;
                total += w;
                combined = 1f - ((1f - combined) * (1f - w));
            }

            if (total <= 0f || combined <= 0f) continue;
            Vector3 target = weightedTarget / total;
            Vector3 newCenter = Vector3.Lerp(ownCenter, target, Mathf.Clamp01(combined));
            vertices[li] = newCenter - halfSpan;
            vertices[ri] = newCenter + halfSpan;
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
