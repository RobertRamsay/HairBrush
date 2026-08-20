using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

// Keeps Bend/X/Y/Z length profiles in the same authoring scope as the controls around them.
// The existing curve editor still edits GroomShapeCurveRegistry, but while a POST is active
// this bridge temporarily presents that POST's private curve set in the registry. HairCard
// evaluates the real group-root curve through EvaluateRoot, so editing a POST never changes
// the upstream group profile or another POST's profile.
[DefaultExecutionOrder(3450)]
public class PostShapeCurveBridge : MonoBehaviour
{
    private sealed class CurveSet
    {
        public AnimationCurve bend;
        public AnimationCurve x;
        public AnimationCurve y;
        public AnimationCurve z;
    }

    private static PostShapeCurveBridge live;
    private static HairProjectSaveData queuedRestore;

    // Monotonic change stamp for HairCard's mesh-input dirty-check.
    //
    // EvaluateRoot and EvaluatePost do not merely read curve data - they CHOOSE which source
    // to read from, based on this bridge's presentation context. Selecting a POST swaps the
    // group's root curves for that POST's private set for every card in the group, and not one
    // field on any of those cards changes. Without this stamp a dirty-check would see nothing
    // move and the whole group would keep its old shape.
    //
    // Deliberately NOT bumped from SavePresentedRegistryToPost, which runs every frame while a
    // POST is presented: it only copies the registry into byPost, and any real change to the
    // registry already moves GroomShapeCurveRegistry's own stamp. Bumping there would move
    // this one every frame and disable the dirty-check for the entire scene.
    private static int epoch;

    public static int Epoch
    {
        get { return epoch; }
    }

    static void BumpEpoch()
    {
        unchecked { epoch++; }
    }

    private readonly Dictionary<int, CurveSet> byPost = new Dictionary<int, CurveSet>();
    private readonly Dictionary<int, CurveSet> rootWhilePost = new Dictionary<int, CurveSet>();
    private readonly Dictionary<int, int> legacyPostGroups = new Dictionary<int, int>();

    private PostAffectorManager posts;
    private ModelViewer viewer;
    private FieldInfo groupsField;
    private FieldInfo activeIdField;
    private FieldInfo activeGroupField;
    private FieldInfo loadedModelField;
    private GameObject lastLoadedModel;
    private Button resetButton;

    private int presentedPostId = -1;
    private int presentedGroupId = -1;
    private int restoreReadyFrames;
    private int legacyWaitFrames;
    private bool captureSwappedToRoot;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<PostShapeCurveBridge>() != null) return;
        GameObject go = new GameObject("PostShapeCurveBridge");
        DontDestroyOnLoad(go);
        go.AddComponent<PostShapeCurveBridge>();
    }

    void Awake()
    {
        live = this;
        BumpEpoch();
    }

    void OnDestroy()
    {
        if (live == this) live = null;
        BumpEpoch();
    }

    void Update()
    {
        EnsureRefs();
        CheckModelLifecycle();
        TryRestoreQueued();
        SyncPresentedCurveContext();
        BindResetButton();
        TryMigrateLegacyCurves();
    }

    void LateUpdate()
    {
        EnsureRefs();
        if (posts == null) return;

        // Curve graph input runs later in Update than this bridge on some canvases. Capture the
        // currently presented POST curve again here before rebuilding cards so graph drags are
        // visible in the very same frame.
        if (presentedPostId >= 0)
            SavePresentedRegistryToPost(presentedPostId, presentedGroupId);

        Dictionary<int, List<PostAffectorManager.PostAffector>> groups = GetGroups();
        HairCard[] cards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        foreach (HairCard card in cards)
        {
            if (card == null) continue;

            // Frozen by SOLO - leave the card's existing profile provenance and mesh alone.
            // The unconditional sweeps rebuild it on the frame SOLO releases the group.
            if (GroupSoloVisibilityAuthority.IsCardFrozen(card)) continue;

            int contributionsBefore = card.PostShapeProfileContributionCount;
            card.ClearPostShapeProfileContributions();

            if (groups != null && groups.TryGetValue(card.groupId, out List<PostAffectorManager.PostAffector> list) && list != null)
            {
                foreach (PostAffectorManager.PostAffector post in list)
                {
                    if (post == null) continue;
                    float w = SpatialWeight(card, post) * Mathf.Clamp01(post.weight);
                    if (w <= .000001f) continue;

                    // Ensure a newly-created POST starts as a snapshot of the current group
                    // profile. From that point onward its curves are independent.
                    GetOrCreatePost(post.id, post.groupId);
                    card.AddPostShapeProfileContribution(
                        post.id,
                        post.delta.bend * w,
                        post.delta.x * w,
                        post.delta.y * w,
                        post.delta.z * w);
                }
            }

            // PostAffectorManager already wrote the scalar evaluated state earlier in
            // LateUpdate. Regenerate only the mesh so the newly attached profile provenance
            // is applied without feeding anything back into canonical state.
            //
            // This rebuild used to be unconditional, which meant a project with NO POSTs at
            // all still paid a second full GenerateMesh() for every card in the scene, every
            // frame, to attach provenance that did not exist. A card that had no
            // contributions last frame and has none this frame cannot have changed here, so
            // there is nothing to rebuild. Any other combination - gained one, lost one,
            // still has some - still rebuilds exactly as before.
            if (contributionsBefore == 0 && card.PostShapeProfileContributionCount == 0) continue;

            card.GenerateMesh();
        }
    }

    public static float EvaluateRoot(int groupId, GroomShapeCurveChannel channel, float t)
    {
        t = Mathf.Clamp01(t);
        // Curl, Segment Density and Width have no per-POST override (see the enum's own
        // comments), so they must never go through the POST-editing snapshot path below - that
        // snapshot's CurveSet genuinely has no fields for any of them, and the channel switch
        // in GetCurve(CurveSet,...) would otherwise fall through to its default case and
        // silently evaluate the wrong (Z) curve for them.
        if (IsRootOnlyChannel(channel))
            return GroomShapeCurveRegistry.Evaluate(groupId, channel, t);
        if (live != null && live.rootWhilePost.TryGetValue(groupId, out CurveSet root) && root != null)
            return Mathf.Clamp01(GetCurve(root, channel).Evaluate(t));
        return GroomShapeCurveRegistry.Evaluate(groupId, channel, t);
    }

    // The channels with NO per-POST override. The snapshot CurveSet in this file has fields
    // for Bend/X/Y/Z only, and its GetCurve ends in `default: return set.z`, so routing any
    // other channel through the POST path silently evaluates the Z ANGLE curve. For Width that
    // would mean cards tapering to their Z profile, but only while a POST happened to be
    // selected in that group - the kind of bug that costs an afternoon to find.
    static bool IsRootOnlyChannel(GroomShapeCurveChannel channel)
    {
        return channel == GroomShapeCurveChannel.CurlFrequency
            || channel == GroomShapeCurveChannel.CurlDiameter
            || channel == GroomShapeCurveChannel.SegmentDensity
            || channel == GroomShapeCurveChannel.Width;
    }

    public static float EvaluatePost(int postId, GroomShapeCurveChannel channel, float t)
    {
        t = Mathf.Clamp01(t);

        // Nothing should call this with a root-only channel, but being wrong here yields a
        // WRONG CURVE rather than an exception, so refuse explicitly rather than trusting every
        // present and future caller to remember.
        if (IsRootOnlyChannel(channel)) return DefaultValue(channel, t);

        if (live == null) return DefaultValue(channel, t);

        // The active POST is presented directly in GroomShapeCurveRegistry so editor changes
        // can be consumed immediately, even before the next bridge sync.
        if (postId == live.presentedPostId && live.presentedGroupId >= 0)
            return GroomShapeCurveRegistry.Evaluate(live.presentedGroupId, channel, t);

        if (live.byPost.TryGetValue(postId, out CurveSet set) && set != null)
            return Mathf.Clamp01(GetCurve(set, channel).Evaluate(t));
        return DefaultValue(channel, t);
    }

    public static void BeginProjectCapture(HairProjectSaveData data)
    {
        if (live == null || data == null) return;
        live.SyncPresentedCurveContext();
        if (live.presentedPostId >= 0)
            live.SavePresentedRegistryToPost(live.presentedPostId, live.presentedGroupId);
        live.CapturePostCurves(data);

        // GroomShapeCurveAuthority.Capture runs immediately after this call and must see the
        // group root, never whichever POST happens to be selected in the UI.
        if (live.presentedPostId >= 0 && live.rootWhilePost.TryGetValue(live.presentedGroupId, out CurveSet root))
        {
            live.WriteSetToRegistry(live.presentedGroupId, root);
            live.captureSwappedToRoot = true;
        }
    }

    public static void EndProjectCapture()
    {
        if (live == null || !live.captureSwappedToRoot) return;
        live.captureSwappedToRoot = false;
        if (live.presentedPostId >= 0)
            live.WriteSetToRegistry(live.presentedGroupId, live.GetOrCreatePost(live.presentedPostId, live.presentedGroupId));
    }

    public static void QueueRestore(HairProjectSaveData data)
    {
        queuedRestore = data;
        if (live != null) live.restoreReadyFrames = 0;
    }

    void EnsureRefs()
    {
        if (viewer == null)
        {
            viewer = FindFirstObjectByType<ModelViewer>();
            if (viewer != null)
            {
                loadedModelField = typeof(ModelViewer).GetField("loadedModel", BindingFlags.Instance | BindingFlags.NonPublic);
                lastLoadedModel = loadedModelField?.GetValue(viewer) as GameObject;
            }
        }

        if (posts != null) return;
        posts = FindFirstObjectByType<PostAffectorManager>();
        if (posts == null) return;
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        groupsField = typeof(PostAffectorManager).GetField("groups", flags);
        activeIdField = typeof(PostAffectorManager).GetField("activeId", flags);
        activeGroupField = typeof(PostAffectorManager).GetField("activeGroup", flags);
    }

    void CheckModelLifecycle()
    {
        if (viewer == null || loadedModelField == null) return;
        GameObject current = loadedModelField.GetValue(viewer) as GameObject;
        if (current == lastLoadedModel) return;

        if (presentedPostId >= 0)
            RestorePresentedRoot();

        lastLoadedModel = current;
        byPost.Clear();
        rootWhilePost.Clear();
        legacyPostGroups.Clear();
        presentedPostId = -1;
        presentedGroupId = -1;
        restoreReadyFrames = 0;
        legacyWaitFrames = 0;
        BumpEpoch();
    }

    void TryRestoreQueued()
    {
        if (queuedRestore == null) return;
        int expectedCards = queuedRestore.hairCards != null ? queuedRestore.hairCards.Count : 0;
        if (FindObjectsByType<HairCard>(FindObjectsSortMode.None).Length < expectedCards) return;
        if (++restoreReadyFrames < 2) return;

        HairProjectSaveData data = queuedRestore;
        queuedRestore = null;
        restoreReadyFrames = 0;
        byPost.Clear();
        rootWhilePost.Clear();
        legacyPostGroups.Clear();
        BumpEpoch();

        if (data.groups != null)
        {
            foreach (GroupSaveData group in data.groups)
            {
                if (group == null || group.postAffectors == null) continue;
                foreach (PostAffectorSaveData post in group.postAffectors)
                {
                    if (post == null) continue;
                    if (HasSavedCurves(post))
                    {
                        byPost[post.id] = new CurveSet
                        {
                            bend = ImportCurve(GroomShapeCurveChannel.Bend, post.bendCurve),
                            x = ImportCurve(GroomShapeCurveChannel.X, post.xAngleCurve),
                            y = ImportCurve(GroomShapeCurveChannel.Y, post.yAngleCurve),
                            z = ImportCurve(GroomShapeCurveChannel.Z, post.zAngleCurve)
                        };
                    }
                    else
                    {
                        // Legacy projects had no POST-owned curve payload: their POST values
                        // were shaped by the group curve. Copy that group curve after the normal
                        // group restore has completed, preserving the old project's appearance.
                        legacyPostGroups[post.id] = group.groupId;
                    }
                }
            }
        }

        legacyWaitFrames = legacyPostGroups.Count > 0 ? 4 : 0;
    }

    void TryMigrateLegacyCurves()
    {
        if (legacyWaitFrames <= 0 || legacyPostGroups.Count == 0) return;
        legacyWaitFrames--;
        if (legacyWaitFrames > 0) return;

        foreach (KeyValuePair<int, int> pair in legacyPostGroups.ToArray())
            byPost[pair.Key] = CaptureRootSet(pair.Value);
        legacyPostGroups.Clear();

        // Rewrites byPost several frames after a legacy project load, changing what
        // EvaluatePost returns without touching the registry - so nothing else would notice.
        BumpEpoch();
    }

    void SyncPresentedCurveContext()
    {
        int activeId = GetActiveId();
        int activeGroup = GetActiveGroup();
        if (activeId < 0 || activeGroup < 0)
        {
            if (presentedPostId >= 0) RestorePresentedRoot();
            return;
        }

        if (activeId == presentedPostId && activeGroup == presentedGroupId)
        {
            SavePresentedRegistryToPost(activeId, activeGroup);
            return;
        }

        if (presentedPostId >= 0)
            RestorePresentedRoot();

        // At this point the registry contains the actual group root. Snapshot it before
        // presenting this POST's private curve set through the existing editor.
        rootWhilePost[activeGroup] = CaptureRegistrySet(activeGroup);
        CurveSet postSet = GetOrCreatePost(activeId, activeGroup);
        WriteSetToRegistry(activeGroup, postSet);
        presentedPostId = activeId;
        presentedGroupId = activeGroup;
        BumpEpoch();

        GroomShapeCurveEditor editor = FindFirstObjectByType<GroomShapeCurveEditor>();
        if (editor != null) editor.RefreshAll();
    }

    void RestorePresentedRoot()
    {
        if (presentedPostId < 0 || presentedGroupId < 0) return;
        SavePresentedRegistryToPost(presentedPostId, presentedGroupId);
        if (rootWhilePost.TryGetValue(presentedGroupId, out CurveSet root) && root != null)
            WriteSetToRegistry(presentedGroupId, root);
        rootWhilePost.Remove(presentedGroupId);
        presentedPostId = -1;
        presentedGroupId = -1;
        BumpEpoch();

        GroomShapeCurveEditor editor = FindFirstObjectByType<GroomShapeCurveEditor>();
        if (editor != null) editor.RefreshAll();
    }

    void SavePresentedRegistryToPost(int postId, int groupId)
    {
        if (postId < 0 || groupId < 0) return;
        byPost[postId] = CaptureRegistrySet(groupId);
    }

    CurveSet GetOrCreatePost(int postId, int groupId)
    {
        if (byPost.TryGetValue(postId, out CurveSet existing) && existing != null) return existing;

        // New POSTs inherit a snapshot of the group profile at creation/first use, so adding
        // a POST does not visually change the established grooming. Editing then diverges only
        // that POST's private copy.
        CurveSet created = CaptureRootSet(groupId);
        byPost[postId] = created;
        BumpEpoch();
        return created;
    }

    CurveSet CaptureRootSet(int groupId)
    {
        if (rootWhilePost.TryGetValue(groupId, out CurveSet root) && root != null)
            return CloneSet(root);
        return CaptureRegistrySet(groupId);
    }

    static CurveSet CaptureRegistrySet(int groupId)
    {
        return new CurveSet
        {
            bend = CloneCurve(GroomShapeCurveRegistry.GetCurve(groupId, GroomShapeCurveChannel.Bend)),
            x = CloneCurve(GroomShapeCurveRegistry.GetCurve(groupId, GroomShapeCurveChannel.X)),
            y = CloneCurve(GroomShapeCurveRegistry.GetCurve(groupId, GroomShapeCurveChannel.Y)),
            z = CloneCurve(GroomShapeCurveRegistry.GetCurve(groupId, GroomShapeCurveChannel.Z))
        };
    }

    void WriteSetToRegistry(int groupId, CurveSet set)
    {
        if (set == null) return;
        GroomShapeCurveRegistry.SetCurve(groupId, GroomShapeCurveChannel.Bend, CloneCurve(set.bend));
        GroomShapeCurveRegistry.SetCurve(groupId, GroomShapeCurveChannel.X, CloneCurve(set.x));
        GroomShapeCurveRegistry.SetCurve(groupId, GroomShapeCurveChannel.Y, CloneCurve(set.y));
        GroomShapeCurveRegistry.SetCurve(groupId, GroomShapeCurveChannel.Z, CloneCurve(set.z));
        GroomShapeCurveRegistry.RefreshGroup(groupId);
    }

    void CapturePostCurves(HairProjectSaveData data)
    {
        if (data.groups == null) return;
        foreach (GroupSaveData group in data.groups)
        {
            if (group == null || group.postAffectors == null) continue;
            foreach (PostAffectorSaveData post in group.postAffectors)
            {
                if (post == null) continue;
                CurveSet set = GetOrCreatePost(post.id, group.groupId);
                post.bendCurve = ExportCurve(set.bend);
                post.xAngleCurve = ExportCurve(set.x);
                post.yAngleCurve = ExportCurve(set.y);
                post.zAngleCurve = ExportCurve(set.z);
            }
        }
    }

    void BindResetButton()
    {
        if (viewer == null || viewer.groomingSliderPanelGO == null) return;
        Button found = viewer.groomingSliderPanelGO.GetComponentsInChildren<Button>(true)
            .FirstOrDefault(b => b != null && b.gameObject.name == "ResetButton");
        if (found == null) return;
        resetButton = found;
        resetButton.onClick.RemoveListener(ResetActivePostCurves);
        resetButton.onClick.AddListener(ResetActivePostCurves);
    }

    void ResetActivePostCurves()
    {
        int id = GetActiveId();
        int group = GetActiveGroup();
        if (id < 0 || group < 0) return;

        CurveSet defaults = new CurveSet
        {
            bend = GroomShapeCurveRegistry.CreateDefault(GroomShapeCurveChannel.Bend),
            x = GroomShapeCurveRegistry.CreateDefault(GroomShapeCurveChannel.X),
            y = GroomShapeCurveRegistry.CreateDefault(GroomShapeCurveChannel.Y),
            z = GroomShapeCurveRegistry.CreateDefault(GroomShapeCurveChannel.Z)
        };
        byPost[id] = defaults;
        if (id == presentedPostId && group == presentedGroupId)
        {
            WriteSetToRegistry(group, defaults);
            GroomShapeCurveEditor editor = FindFirstObjectByType<GroomShapeCurveEditor>();
            if (editor != null) editor.RefreshAll();
        }
    }

    Dictionary<int, List<PostAffectorManager.PostAffector>> GetGroups()
    {
        return groupsField?.GetValue(posts) as Dictionary<int, List<PostAffectorManager.PostAffector>>;
    }

    int GetActiveId()
    {
        return activeIdField?.GetValue(posts) is int value ? value : -1;
    }

    int GetActiveGroup()
    {
        return activeGroupField?.GetValue(posts) is int value ? value : -1;
    }

    static float SpatialWeight(HairCard card, PostAffectorManager.PostAffector post)
    {
        Vector3 p = card.GetSpawnHitPoint();
        if (p == Vector3.zero) p = card.transform.position;
        float d = Vector3.Distance(p, post.center);
        float radius = Mathf.Max(.001f, post.radius);
        float outer = radius + Mathf.Max(0f, post.falloff);
        if (d <= radius) return 1f;
        if (post.falloff <= .000001f || d >= outer) return 0f;
        return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(outer, radius, d));
    }

    static AnimationCurve GetCurve(CurveSet set, GroomShapeCurveChannel channel)
    {
        switch (channel)
        {
            case GroomShapeCurveChannel.Bend: return set.bend;
            case GroomShapeCurveChannel.X: return set.x;
            case GroomShapeCurveChannel.Y: return set.y;
            default: return set.z;
        }
    }

    static CurveSet CloneSet(CurveSet source)
    {
        return new CurveSet
        {
            bend = CloneCurve(source.bend),
            x = CloneCurve(source.x),
            y = CloneCurve(source.y),
            z = CloneCurve(source.z)
        };
    }

    static AnimationCurve CloneCurve(AnimationCurve source)
    {
        if (source == null) return null;
        AnimationCurve result = new AnimationCurve(source.keys);
        result.preWrapMode = source.preWrapMode;
        result.postWrapMode = source.postWrapMode;
        return result;
    }

    static List<GroomCurveKeySaveData> ExportCurve(AnimationCurve curve)
    {
        List<GroomCurveKeySaveData> result = new List<GroomCurveKeySaveData>();
        if (curve == null) return result;
        foreach (Keyframe key in curve.keys)
        {
            result.Add(new GroomCurveKeySaveData
            {
                time = key.time,
                value = key.value,
                inTangent = key.inTangent,
                outTangent = key.outTangent
            });
        }
        return result;
    }

    static AnimationCurve ImportCurve(GroomShapeCurveChannel channel, List<GroomCurveKeySaveData> saved)
    {
        if (saved == null || saved.Count < 2)
            return GroomShapeCurveRegistry.CreateDefault(channel);

        List<Keyframe> keys = new List<Keyframe>();
        foreach (GroomCurveKeySaveData item in saved)
        {
            if (item == null) continue;
            keys.Add(new Keyframe(
                Mathf.Clamp01(item.time),
                Mathf.Clamp01(item.value),
                Finite(item.inTangent) ? item.inTangent : 0f,
                Finite(item.outTangent) ? item.outTangent : 0f));
        }
        if (keys.Count < 2) return GroomShapeCurveRegistry.CreateDefault(channel);
        AnimationCurve curve = new AnimationCurve(keys.OrderBy(k => k.time).ToArray());
        curve.preWrapMode = WrapMode.ClampForever;
        curve.postWrapMode = WrapMode.ClampForever;
        return curve;
    }

    static bool HasSavedCurves(PostAffectorSaveData post)
    {
        return post.bendCurve != null && post.bendCurve.Count >= 2 &&
               post.xAngleCurve != null && post.xAngleCurve.Count >= 2 &&
               post.yAngleCurve != null && post.yAngleCurve.Count >= 2 &&
               post.zAngleCurve != null && post.zAngleCurve.Count >= 2;
    }

    static bool Finite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    static float DefaultValue(GroomShapeCurveChannel channel, float t)
    {
        return channel == GroomShapeCurveChannel.Bend ? t * t : 1f;
    }
}
