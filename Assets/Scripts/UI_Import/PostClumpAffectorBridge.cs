using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

// POST-local clump operator.
// Evaluation order is intentionally: evaluated Length/Width -> Bend/Twist/angles -> CLUMP attractor.
// It never writes back to HairCard canonical state, so it cannot accumulate frame-to-frame.
[DefaultExecutionOrder(3600)]
public class PostClumpAffectorBridge : MonoBehaviour
{
    [Serializable]
    private class ClumpSettings
    {
        public float point = .9f;
        public float amount = 0f;
    }

    private class CardClump
    {
        public Vector3 weightedTarget;
        public float targetWeight;
        public float combinedStrength;
    }

    public static HairProjectSaveData PendingRestore;

    private const float DefaultPoint = .9f;
    private const float DefaultAmount = 0f;
    private const float MaxPoint = 1.5f;

    private readonly Dictionary<int, ClumpSettings> settingsByPost = new();

    private PostAffectorManager posts;
    private ModelViewer viewer;
    private FieldInfo groupsField;
    private FieldInfo activeIdField;
    private FieldInfo hasSelectionField;
    private MethodInfo createSliderMethod;

    private HairProjectSaveData pendingSeen;

    private int uiPostId = -1;
    private GameObject pointRow;
    private GameObject amountRow;
    private Slider pointSlider;
    private Slider amountSlider;

    private GameObject gizmoRoot;
    private LineRenderer gizmoLine;
    private GameObject gizmoMarker;
    private Material gizmoMaterial;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<PostClumpAffectorBridge>() != null) return;
        GameObject go = new GameObject("PostClumpAffectorBridge");
        DontDestroyOnLoad(go);
        go.AddComponent<PostClumpAffectorBridge>();
    }

    void Update()
    {
        Resolve();
        if (posts == null || viewer == null) return;

        HandlePendingRestore();
        SyncLivePosts();
        MaintainUIAndGizmo();
    }

    void LateUpdate()
    {
        Resolve();
        if (posts == null || viewer == null) return;
        ApplyPostClumps();
    }

    void OnDestroy()
    {
        DestroyEditorUI();
        DestroyGizmo();
        if (gizmoMaterial != null) Destroy(gizmoMaterial);
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

    Dictionary<int, List<PostAffectorManager.PostAffector>> GetGroups()
    {
        return groupsField?.GetValue(posts) as Dictionary<int, List<PostAffectorManager.PostAffector>>;
    }

    IEnumerable<PostAffectorManager.PostAffector> AllPosts()
    {
        Dictionary<int, List<PostAffectorManager.PostAffector>> groups = GetGroups();
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
        if (id < 0) return null;
        return AllPosts().FirstOrDefault(p => p.id == id);
    }

    bool HasSelection()
    {
        return hasSelectionField != null && hasSelectionField.GetValue(viewer) is bool selected && selected;
    }

    ClumpSettings GetSettings(int postId)
    {
        if (!settingsByPost.TryGetValue(postId, out ClumpSettings settings))
        {
            settings = new ClumpSettings();
            settingsByPost[postId] = settings;
        }
        return settings;
    }

    void SyncLivePosts()
    {
        HashSet<int> live = new(AllPosts().Select(p => p.id));
        foreach (int id in live) GetSettings(id);

        foreach (int dead in settingsByPost.Keys.Where(id => !live.Contains(id)).ToArray())
            settingsByPost.Remove(dead);
    }

    void HandlePendingRestore()
    {
        if (PendingRestore != null && PendingRestore != pendingSeen)
        {
            pendingSeen = PendingRestore;
            settingsByPost.Clear();
            DestroyEditorUI();
        }

        if (pendingSeen == null) return;

        // ModifierPersistenceBridge imports the POST rows first. Restore clump settings only
        // after that handoff has finished so post IDs have their final loaded values.
        if (HairProjectSaveData.PendingModifierRestore != null) return;

        if (pendingSeen.groups != null)
        {
            foreach (GroupSaveData group in pendingSeen.groups)
            {
                if (group?.postAffectors == null) continue;
                foreach (PostAffectorSaveData saved in group.postAffectors)
                {
                    if (saved == null) continue;
                    ClumpSettings settings = GetSettings(saved.id);
                    settings.amount = Mathf.Clamp01(saved.clumpAmount);
                    settings.point = saved.clumpPoint;

                    // Projects saved before POST clump existed deserialize these fields as zero.
                    // With zero amount, treat a zero point as the new neutral/default .9 position.
                    if (settings.amount <= .000001f && settings.point <= .000001f)
                        settings.point = DefaultPoint;
                    settings.point = Mathf.Clamp(settings.point, 0f, MaxPoint);
                }
            }
        }

        PendingRestore = null;
        pendingSeen = null;
    }

    public void PopulateSave(List<PostAffectorSaveData> savedPosts)
    {
        if (savedPosts == null) return;
        foreach (PostAffectorSaveData saved in savedPosts)
        {
            if (saved == null) continue;
            ClumpSettings settings = GetSettings(saved.id);
            saved.clumpPoint = settings.point;
            saved.clumpAmount = settings.amount;
        }
    }

    void MaintainUIAndGizmo()
    {
        PostAffectorManager.PostAffector active = ActivePost();
        if (active == null || !HasSelection())
        {
            DestroyEditorUI();
            HideGizmo();
            return;
        }

        ClumpSettings settings = GetSettings(active.id);
        if (uiPostId != active.id)
        {
            DestroyEditorUI();
            uiPostId = active.id;
        }

        EnsureEditorUI(active.id, settings);
        SyncEditorUI(settings);
        UpdateGizmo(active, settings);
    }

    void EnsureEditorUI(int postId, ClumpSettings settings)
    {
        if (viewer.groomingSliderPanelGO == null || createSliderMethod == null) return;
        Transform panel = viewer.groomingSliderPanelGO.transform;

        if (pointRow == null)
        {
            UnityAction<float> onPoint = value =>
            {
                if (!settingsByPost.TryGetValue(postId, out ClumpSettings current)) return;
                current.point = Mathf.Clamp(value, 0f, MaxPoint);
            };
            object[] args = { panel, "CLUMP Point", 0f, MaxPoint, settings.point, onPoint, null, 38f, 16 };
            pointRow = createSliderMethod.Invoke(viewer, args) as GameObject;
            pointSlider = args[6] as Slider;
        }

        if (amountRow == null)
        {
            UnityAction<float> onAmount = value =>
            {
                if (!settingsByPost.TryGetValue(postId, out ClumpSettings current)) return;
                current.amount = Mathf.Clamp01(value);
            };
            object[] args = { panel, "CLUMP Amount", 0f, 1f, settings.amount, onAmount, null, 38f, 16 };
            amountRow = createSliderMethod.Invoke(viewer, args) as GameObject;
            amountSlider = args[6] as Slider;
        }

        // The UI mirrors the deformation stack: authored shape/orientation first, CLUMP last.
        Transform anchor = panel.Find("Offset Z_Row");
        if (anchor == null) anchor = panel.Find("Twist Angle_Row");
        if (anchor != null)
        {
            int insert = Mathf.Min(anchor.GetSiblingIndex() + 1, panel.childCount - 1);
            if (pointRow != null) pointRow.transform.SetSiblingIndex(insert);
            if (amountRow != null) amountRow.transform.SetSiblingIndex(Mathf.Min(insert + 1, panel.childCount - 1));
        }
    }

    void SyncEditorUI(ClumpSettings settings)
    {
        if (pointSlider != null && !Mathf.Approximately(pointSlider.value, settings.point))
            pointSlider.SetValueWithoutNotify(settings.point);
        if (amountSlider != null && !Mathf.Approximately(amountSlider.value, settings.amount))
            amountSlider.SetValueWithoutNotify(settings.amount);

        if (pointRow != null)
        {
            TextMeshProUGUI label = pointRow.GetComponentInChildren<TextMeshProUGUI>(true);
            if (label != null) label.text = "CLUMP Point: " + settings.point.ToString("F3");
        }
        if (amountRow != null)
        {
            TextMeshProUGUI label = amountRow.GetComponentInChildren<TextMeshProUGUI>(true);
            if (label != null) label.text = "CLUMP Amount: " + settings.amount.ToString("F3");
        }
    }

    void DestroyEditorUI()
    {
        if (pointRow != null) Destroy(pointRow);
        if (amountRow != null) Destroy(amountRow);
        pointRow = null;
        amountRow = null;
        pointSlider = null;
        amountSlider = null;
        uiPostId = -1;
    }

    void ApplyPostClumps()
    {
        List<PostAffectorManager.PostAffector> livePosts = AllPosts().ToList();
        if (livePosts.Count == 0) return;

        HairCard[] cards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        Dictionary<int, float> representativeLengths = new();
        Dictionary<HairCard, CardClump> perCard = new();

        foreach (PostAffectorManager.PostAffector post in livePosts)
        {
            ClumpSettings settings = GetSettings(post.id);
            if (settings.amount <= .000001f) continue;

            if (!representativeLengths.TryGetValue(post.id, out float representativeLength))
            {
                representativeLength = RepresentativeLength(post, cards);
                representativeLengths[post.id] = representativeLength;
            }

            Vector3 normal = post.normal.sqrMagnitude > .000001f ? post.normal.normalized : Vector3.up;
            Vector3 target = post.center + normal * (representativeLength * settings.point);

            foreach (HairCard card in cards)
            {
                if (card == null || card.groupId != post.groupId) continue;
                float spatial = SpatialWeight(card, post);
                float influence = spatial * Mathf.Clamp01(post.weight) * settings.amount;
                if (influence <= .000001f) continue;

                if (!perCard.TryGetValue(card, out CardClump aggregate))
                {
                    aggregate = new CardClump();
                    perCard[card] = aggregate;
                }

                aggregate.weightedTarget += target * influence;
                aggregate.targetWeight += influence;
                aggregate.combinedStrength = 1f - ((1f - aggregate.combinedStrength) * (1f - Mathf.Clamp01(influence)));
            }
        }

        foreach ((HairCard card, CardClump aggregate) in perCard)
        {
            if (aggregate.targetWeight <= .000001f || aggregate.combinedStrength <= .000001f) continue;
            Vector3 blendedTarget = aggregate.weightedTarget / aggregate.targetWeight;
            PostClumpMeshDeformer.Apply(card, blendedTarget, aggregate.combinedStrength);
        }
    }

    float RepresentativeLength(PostAffectorManager.PostAffector post, HairCard[] cards)
    {
        List<float> lengths = cards
            .Where(card => card != null && card.groupId == post.groupId && SpatialWeight(card, post) > .000001f)
            .Select(card => Mathf.Max(.001f, card.length))
            .OrderBy(value => value)
            .ToList();

        if (lengths.Count == 0)
            return Mathf.Max(.001f, post.baseline.length + post.delta.length);

        // 90th percentile gives an "average maximum": long-hair representative without
        // letting one unusually long card push the attraction target far away.
        int index = Mathf.Clamp(Mathf.CeilToInt((lengths.Count - 1) * .9f), 0, lengths.Count - 1);
        return lengths[index];
    }

    float SpatialWeight(HairCard card, PostAffectorManager.PostAffector post)
    {
        Vector3 root = card.GetSpawnHitPoint();
        if (root == Vector3.zero) root = card.transform.position;
        float distance = Vector3.Distance(root, post.center);
        float radius = Mathf.Max(.001f, post.radius);
        float falloff = Mathf.Max(0f, post.falloff);
        float outer = radius + falloff;
        if (distance <= radius) return 1f;
        if (falloff <= .000001f || distance >= outer) return 0f;
        return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(outer, radius, distance));
    }

    void UpdateGizmo(PostAffectorManager.PostAffector post, ClumpSettings settings)
    {
        EnsureGizmo();
        if (gizmoRoot == null || gizmoLine == null || gizmoMarker == null) return;

        HairCard[] cards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        float representativeLength = RepresentativeLength(post, cards);
        Vector3 normal = post.normal.sqrMagnitude > .000001f ? post.normal.normalized : Vector3.up;
        Vector3 target = post.center + normal * (representativeLength * settings.point);

        gizmoRoot.SetActive(true);
        gizmoLine.SetPosition(0, post.center);
        gizmoLine.SetPosition(1, target);
        float lineWidth = Mathf.Clamp(representativeLength * .012f, .0015f, .004f);
        gizmoLine.startWidth = lineWidth;
        gizmoLine.endWidth = lineWidth;

        gizmoMarker.transform.position = target;
        float markerSize = Mathf.Clamp(representativeLength * .055f, .006f, .018f);
        gizmoMarker.transform.localScale = Vector3.one * markerSize;
    }

    void EnsureGizmo()
    {
        if (gizmoRoot != null) return;

        gizmoRoot = new GameObject("PostClumpAttractionGizmo");
        DontDestroyOnLoad(gizmoRoot);

        gizmoLine = gizmoRoot.AddComponent<LineRenderer>();
        gizmoLine.useWorldSpace = true;
        gizmoLine.positionCount = 2;
        gizmoLine.numCapVertices = 4;
        gizmoLine.textureMode = LineTextureMode.Stretch;

        Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
        if (shader != null)
        {
            gizmoMaterial = new Material(shader) { name = "PostClumpGizmoMaterial" };
            Color color = new Color(.20f, .88f, 1f, 1f);
            if (gizmoMaterial.HasProperty("_BaseColor")) gizmoMaterial.SetColor("_BaseColor", color);
            if (gizmoMaterial.HasProperty("_Color")) gizmoMaterial.SetColor("_Color", color);
            gizmoLine.material = gizmoMaterial;
            gizmoLine.startColor = color;
            gizmoLine.endColor = color;
        }

        gizmoMarker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        gizmoMarker.name = "PostClumpAttractionPoint";
        gizmoMarker.transform.SetParent(gizmoRoot.transform, false);
        Collider collider = gizmoMarker.GetComponent<Collider>();
        if (collider != null) Destroy(collider);
        MeshRenderer markerRenderer = gizmoMarker.GetComponent<MeshRenderer>();
        if (markerRenderer != null && gizmoMaterial != null) markerRenderer.sharedMaterial = gizmoMaterial;
    }

    void HideGizmo()
    {
        if (gizmoRoot != null) gizmoRoot.SetActive(false);
    }

    void DestroyGizmo()
    {
        if (gizmoRoot != null) Destroy(gizmoRoot);
        gizmoRoot = null;
        gizmoLine = null;
        gizmoMarker = null;
    }
}

// Rebuilds only a clumped card's visible mesh after every authored/evaluated shape control
// has already been resolved. Clump is a final directional attractor: it steers each existing
// shaped segment toward the target while preserving that segment's original length.
public static class PostClumpMeshDeformer
{
    public static void Apply(HairCard card, Vector3 targetWorld, float strength)
    {
        if (card == null || card.segments < 1 || strength <= .000001f) return;
        MeshFilter filter = card.GetComponent<MeshFilter>();
        if (filter == null) return;
        Mesh mesh = filter.mesh;
        if (mesh == null) return;

        int segments = Mathf.Max(1, card.segments);
        int ringCount = segments + 1;
        int numVertices = ringCount * 2;
        Vector3[] vertices = new Vector3[numVertices];
        Vector2[] uvs = new Vector2[numVertices];
        int[] triangles = new int[segments * 6];
        Vector3[] authoredCenters = new Vector3[ringCount];
        Vector3[] authoredHalfSpans = new Vector3[ringCount];
        Vector3[] clumpedCenters = new Vector3[ringCount];
        Quaternion[] steering = new Quaternion[ringCount];

        float segmentHeight = card.length / segments;
        float halfWidth = card.width * .5f;
        Vector3 targetLocal = card.transform.InverseTransformPoint(targetWorld);

        // First build the fully authored/evaluated card shape: Length -> Bend -> Twist.
        // The HairCard transform already contains the card angle/offset orientation; converting
        // the world target into local space means the attraction happens after that orientation too.
        for (int i = 0; i <= segments; i++)
        {
            float t = (float)i / segments;
            float z = i * segmentHeight;
            float currentWidth = halfWidth * card.flattenFactor;
            Vector3 left = new Vector3(-currentWidth, 0f, z);
            Vector3 right = new Vector3(currentWidth, 0f, z);

            Quaternion authoredRotation = Quaternion.Euler(card.bendAngle * (t * t), 0f, card.twistAngle * t);
            left = authoredRotation * left;
            right = authoredRotation * right;
            authoredCenters[i] = (left + right) * .5f;
            authoredHalfSpans[i] = (right - left) * .5f;
            steering[i] = Quaternion.identity;
        }

        // Then gather the already-shaped centreline. Each segment retains its authored arc
        // length; only its direction is progressively steered toward the attraction point.
        clumpedCenters[0] = authoredCenters[0];
        for (int i = 1; i <= segments; i++)
        {
            float t = (float)i / segments;
            Vector3 authoredSegment = authoredCenters[i] - authoredCenters[i - 1];
            float segmentLength = authoredSegment.magnitude;
            if (segmentLength <= .000001f)
            {
                clumpedCenters[i] = clumpedCenters[i - 1];
                continue;
            }

            Vector3 authoredDirection = authoredSegment / segmentLength;
            Vector3 toTarget = targetLocal - clumpedCenters[i - 1];
            Vector3 targetDirection = toTarget.sqrMagnitude > .000001f ? toTarget.normalized : authoredDirection;
            float influence = Mathf.Clamp01(strength * t * t);
            Vector3 steeredDirection = Vector3.Slerp(authoredDirection, targetDirection, influence);
            if (steeredDirection.sqrMagnitude <= .000001f) steeredDirection = authoredDirection;
            steeredDirection.Normalize();

            clumpedCenters[i] = clumpedCenters[i - 1] + steeredDirection * segmentLength;
            steering[i] = Quaternion.FromToRotation(authoredDirection, steeredDirection);
        }

        for (int i = 0; i <= segments; i++)
        {
            float t = (float)i / segments;
            int index = i * 2;
            Vector3 halfSpan = steering[i] * authoredHalfSpans[i];
            vertices[index] = clumpedCenters[i] - halfSpan;
            vertices[index + 1] = clumpedCenters[i] + halfSpan;

            float baseULeft = card.uScale < 0f ? 1f : 0f;
            float baseURight = card.uScale < 0f ? 0f : 1f;
            float finalULeft = baseULeft * Mathf.Abs(card.uScale) + card.uOffset;
            float finalURight = baseURight * Mathf.Abs(card.uScale) + card.uOffset;
            float absVScale = Mathf.Abs(card.vScale);
            float baseV = (1f - t) * absVScale;
            if (card.vScale < 0f) baseV = absVScale - baseV;
            float finalV = baseV + card.vOffset;
            uvs[index] = new Vector2(finalULeft, finalV);
            uvs[index + 1] = new Vector2(finalURight, finalV);
        }

        int triIndex = 0;
        for (int i = 0; i < segments; i++)
        {
            int root = i * 2;
            triangles[triIndex++] = root;
            triangles[triIndex++] = root + 2;
            triangles[triIndex++] = root + 1;
            triangles[triIndex++] = root + 1;
            triangles[triIndex++] = root + 2;
            triangles[triIndex++] = root + 3;
        }

        mesh.Clear();
        mesh.vertices = vertices;
        mesh.uv = uvs;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
    }
}