using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// PRE/ADJ is always a group-wide routing choice.
//
// In PRE mode a POST may optionally override only the predetermined rectangle range/seed
// inside its spatial influence. Rectangle IDs resolve against the atlas cuts belonging to the
// material assigned to that card's group.
[DefaultExecutionOrder(3450)]
public class PostPredeterminedUVAuthority : MonoBehaviour
{
    private sealed class LocalSettings
    {
        public int minId = 1;
        public int maxId = 1;
        public int seed;
    }

    private static HairProjectSaveData pendingRestore;

    private readonly Dictionary<int, LocalSettings> byPost = new();

    private PostAffectorManager posts;
    private GroupPredeterminedUVController groupUV;
    private TextureUVRectWorkspace workspace;
    private ModelViewer viewer;
    private FieldInfo activeIdField;
    private FieldInfo activeGroupField;
    private FieldInfo groupsField;
    private FieldInfo hasSelectionField;

    private int restoreGraceFrames;
    private int emptyPostFrames;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<PostPredeterminedUVAuthority>() == null)
        {
            GameObject go = new GameObject("PostPredeterminedUVAuthority");
            DontDestroyOnLoad(go);
            go.AddComponent<PostPredeterminedUVAuthority>();
        }

        if (FindFirstObjectByType<PostPredeterminedUVUIAuthority>() == null)
        {
            GameObject ui = new GameObject("PostPredeterminedUVUIAuthority");
            DontDestroyOnLoad(ui);
            ui.AddComponent<PostPredeterminedUVUIAuthority>();
        }
    }

    public static void Capture(HairProjectSaveData data)
    {
        if (data?.groups == null) return;
        PostPredeterminedUVAuthority authority = FindFirstObjectByType<PostPredeterminedUVAuthority>();
        if (authority == null) return;
        authority.CaptureInto(data);
    }

    public static void QueueRestore(HairProjectSaveData data)
    {
        pendingRestore = data;
    }

    void Update()
    {
        Resolve();
        RestorePending();
        MaintainLifetime();
    }

    void LateUpdate()
    {
        Resolve();
        RestorePending();
        ApplyLocalPredeterminedUVs();
    }

    void Resolve()
    {
        if (viewer == null)
        {
            viewer = FindFirstObjectByType<ModelViewer>();
            if (viewer != null)
                hasSelectionField = typeof(ModelViewer).GetField("hasSelectionHotspot", BindingFlags.Instance | BindingFlags.NonPublic);
        }

        if (groupUV == null) groupUV = FindFirstObjectByType<GroupPredeterminedUVController>();
        if (workspace == null) workspace = FindFirstObjectByType<TextureUVRectWorkspace>();

        if (posts == null)
        {
            posts = FindFirstObjectByType<PostAffectorManager>();
            if (posts != null)
            {
                BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                Type type = typeof(PostAffectorManager);
                activeIdField = type.GetField("activeId", flags);
                activeGroupField = type.GetField("activeGroup", flags);
                groupsField = type.GetField("groups", flags);
            }
        }
    }

    void CaptureInto(HairProjectSaveData data)
    {
        foreach (GroupSaveData group in data.groups)
        {
            if (group == null) continue;
            if (group.postPredeterminedUVs == null) group.postPredeterminedUVs = new List<PostPredeterminedUVSaveData>();
            else group.postPredeterminedUVs.Clear();

            if (group.postAffectors == null) continue;
            foreach (PostAffectorSaveData post in group.postAffectors)
            {
                if (post == null || !byPost.TryGetValue(post.id, out LocalSettings local) || local == null) continue;
                group.postPredeterminedUVs.Add(new PostPredeterminedUVSaveData
                {
                    postId = post.id,
                    minId = local.minId,
                    maxId = local.maxId,
                    seed = local.seed
                });
            }
        }
    }

    void RestorePending()
    {
        if (pendingRestore == null) return;
        HairProjectSaveData data = pendingRestore;
        pendingRestore = null;

        byPost.Clear();
        if (data.groups != null)
        {
            foreach (GroupSaveData group in data.groups)
            {
                if (group?.postPredeterminedUVs == null) continue;
                foreach (PostPredeterminedUVSaveData saved in group.postPredeterminedUVs)
                {
                    if (saved == null || saved.postId <= 0) continue;
                    byPost[saved.postId] = new LocalSettings
                    {
                        minId = Mathf.Max(1, saved.minId),
                        maxId = Mathf.Max(1, saved.maxId),
                        seed = saved.seed
                    };
                }
            }
        }

        restoreGraceFrames = 120;
        emptyPostFrames = 0;
    }

    void MaintainLifetime()
    {
        if (restoreGraceFrames > 0) restoreGraceFrames--;

        Dictionary<int, List<PostAffectorManager.PostAffector>> groups = GetGroups();
        if (groups == null) return;

        HashSet<int> live = new HashSet<int>(groups.Values
            .Where(list => list != null)
            .SelectMany(list => list)
            .Where(post => post != null)
            .Select(post => post.id));

        if (live.Count == 0)
        {
            if (restoreGraceFrames > 0) return;
            if (++emptyPostFrames >= 3) byPost.Clear();
            return;
        }

        emptyPostFrames = 0;
        if (restoreGraceFrames > 0) return;
        foreach (int dead in byPost.Keys.Where(id => !live.Contains(id)).ToArray())
            byPost.Remove(dead);
    }

    void ApplyLocalPredeterminedUVs()
    {
        Dictionary<int, List<PostAffectorManager.PostAffector>> groups = GetGroups();
        if (groups == null || groups.Count == 0 || byPost.Count == 0 || groupUV == null) return;

        Dictionary<int, GroupSaveData> groupSettings = new Dictionary<int, GroupSaveData>();
        Dictionary<int, List<UVRectSaveData>> rectsByGroup = new Dictionary<int, List<UVRectSaveData>>();
        Dictionary<int, List<UVRectSaveData>> allowedByPost = new Dictionary<int, List<UVRectSaveData>>();

        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
        {
            if (card == null || !groups.TryGetValue(card.groupId, out List<PostAffectorManager.PostAffector> list) || list == null) continue;

            if (!groupSettings.TryGetValue(card.groupId, out GroupSaveData group))
            {
                group = ReadGroupUV(card.groupId);
                groupSettings[card.groupId] = group;
            }
            if (group == null || !group.usePredeterminedUVs) continue;

            if (!rectsByGroup.TryGetValue(card.groupId, out List<UVRectSaveData> rects))
            {
                rects = GetRectsForGroup(card.groupId);
                rectsByGroup[card.groupId] = rects;
            }
            if (rects == null || rects.Count == 0) continue;

            PostAffectorManager.PostAffector chosen = null;
            LocalSettings chosenSettings = null;
            float bestWeight = 0f;

            foreach (PostAffectorManager.PostAffector post in list)
            {
                if (post == null || !byPost.TryGetValue(post.id, out LocalSettings local) || local == null) continue;

                float weight = SpatialWeight(card, post) * Mathf.Clamp01(post.weight);
                if (weight <= .000001f) continue;
                if (weight < StablePostThreshold(card, post)) continue;

                if (chosen == null || weight > bestWeight + .000001f ||
                    (Mathf.Abs(weight - bestWeight) <= .000001f && post.id > chosen.id))
                {
                    chosen = post;
                    chosenSettings = local;
                    bestWeight = weight;
                }
            }

            if (chosen == null || chosenSettings == null) continue;

            // Built once per POST rather than once per card. The allowed set depends only on the
            // POST's range and its group's rectangles, neither of which changes inside this loop,
            // and the loop runs over every card in the scene every LateUpdate - so working it out
            // per card was several list allocations and a sort per card per frame.
            if (!allowedByPost.TryGetValue(chosen.id, out List<UVRectSaveData> allowed))
            {
                allowed = BuildAllowedRects(chosenSettings, rects);
                allowedByPost[chosen.id] = allowed;
            }
            if (!TryResolveRect(card, chosenSettings, allowed, out UVRectSaveData rect)) continue;

            card.uScale = Mathf.Max(.000001f, rect.uMax - rect.uMin);

            // Both flips, through the same helper the group path uses. The strip's own flipV is
            // plainly the POST's business - it is the same strip. The GROUP's flip is too, and
            // that is the less obvious half: a POST overrides which rectangle a card draws, not
            // which way up the group's hair sits, so without group.uvFlipV here a POST patch
            // would come out inverted against the hair immediately around it.
            card.vScale = GroupPredeterminedUVController.SignedVScale(rect, group.uvFlipV);
            card.uOffset = rect.uMin;
            card.vOffset = rect.vMin;
            card.GenerateMesh();
        }
    }

    public bool TryGetActiveContext(out int postId, out int groupId, out int minId, out int maxId, out int seed)
    {
        postId = -1;
        groupId = -1;
        minId = 1;
        maxId = 1;
        seed = 0;

        Resolve();
        PostAffectorManager.PostAffector active = GetActive();
        if (active == null || groupUV == null) return false;

        GroupSaveData group = ReadGroupUV(active.groupId);
        if (group == null || !group.usePredeterminedUVs) return false;

        postId = active.id;
        groupId = active.groupId;
        if (byPost.TryGetValue(active.id, out LocalSettings local) && local != null)
        {
            minId = local.minId;
            maxId = local.maxId;
            seed = local.seed;
        }
        else
        {
            minId = group.uvRectMinId;
            maxId = group.uvRectMaxId;
            seed = group.uvRectSeed;
        }
        return true;
    }

    public void SetActiveRange(bool setMin, string value)
    {
        if (!TryGetActiveContext(out int postId, out int groupId, out int minId, out int maxId, out int seed)) return;
        if (!int.TryParse(value, out int parsed)) parsed = setMin ? minId : maxId;
        if (setMin) minId = parsed;
        else maxId = parsed;
        NormalizeRange(groupId, ref minId, ref maxId);
        byPost[postId] = new LocalSettings { minId = minId, maxId = maxId, seed = seed };
    }

    public void SetActiveSeed(string value)
    {
        if (!TryGetActiveContext(out int postId, out _, out int minId, out int maxId, out int seed)) return;
        if (!int.TryParse(value, out int parsed)) parsed = seed;
        byPost[postId] = new LocalSettings { minId = minId, maxId = maxId, seed = parsed };
    }

    public void RandomizeActiveSeed()
    {
        if (!TryGetActiveContext(out int postId, out _, out int minId, out int maxId, out _)) return;
        byPost[postId] = new LocalSettings
        {
            minId = minId,
            maxId = maxId,
            seed = UnityEngine.Random.Range(0, 1000000)
        };
    }

    void NormalizeRange(int groupId, ref int minId, ref int maxId)
    {
        List<UVRectSaveData> rects = GetRectsForGroup(groupId);
        if (rects.Count == 0)
        {
            minId = Mathf.Max(1, minId);
            maxId = Mathf.Max(minId, maxId);
            return;
        }

        int availableMin = rects.Min(rect => rect.id);
        int availableMax = rects.Max(rect => rect.id);
        minId = Mathf.Clamp(minId, availableMin, availableMax);
        maxId = Mathf.Clamp(maxId, availableMin, availableMax);
        if (minId > maxId)
        {
            int swap = minId;
            minId = maxId;
            maxId = swap;
        }
    }

    GroupSaveData ReadGroupUV(int groupId)
    {
        if (groupUV == null) return null;
        GroupSaveData probe = new GroupSaveData { groupId = groupId };
        groupUV.PopulateGroupSave(probe);
        return probe;
    }

    List<UVRectSaveData> GetRectsForGroup(int groupId)
    {
        if (MaterialUVRectAuthority.TryGetRectsForGroup(groupId, out List<UVRectSaveData> materialRects))
            return materialRects.Where(rect => rect != null).OrderBy(rect => rect.id).ToList();

        if (workspace == null) workspace = FindFirstObjectByType<TextureUVRectWorkspace>();
        return workspace != null
            ? workspace.ExportDefinitions().Where(rect => rect != null).OrderBy(rect => rect.id).ToList()
            : new List<UVRectSaveData>();
    }

    // The rectangles this POST's range actually selects, worked out once for the whole pass.
    List<UVRectSaveData> BuildAllowedRects(LocalSettings settings, List<UVRectSaveData> allRects)
    {
        List<UVRectSaveData> allowed = allRects
            .Where(item => item != null && item.id >= settings.minId && item.id <= settings.maxId)
            .OrderBy(item => item.id)
            .ToList();
        if (allowed.Count > 0) return allowed;

        // Same fallback GroupPredeterminedUVController.ApplyGroup makes, and for the same reason:
        // the rectangle set can shrink underneath this range while it is stored - a rect deleted
        // with a right click, AUTO replacing the set - and a range that then selects nothing would
        // leave the POST's override silently dead with no way to notice or recover it.
        //
        // Clamped for this pass only, never written back, so restoring the rectangles restores
        // the range the user actually authored along with them.
        List<UVRectSaveData> live = allRects.Where(item => item != null).ToList();
        if (live.Count == 0) return allowed;

        int lowest = live.Min(item => item.id);
        int highest = live.Max(item => item.id);
        int lo = Mathf.Clamp(settings.minId, lowest, highest);
        int hi = Mathf.Clamp(settings.maxId, lowest, highest);
        if (lo > hi)
        {
            int swap = lo;
            lo = hi;
            hi = swap;
        }

        return live.Where(item => item.id >= lo && item.id <= hi).OrderBy(item => item.id).ToList();
    }

    bool TryResolveRect(HairCard card, LocalSettings settings, List<UVRectSaveData> allowed, out UVRectSaveData rect)
    {
        rect = null;
        if (allowed == null || allowed.Count == 0) return false;

        int pick = PositiveMod(StableCardHash(card, card.groupId, settings.seed), allowed.Count);
        rect = allowed[pick];
        return rect != null;
    }

    Dictionary<int, List<PostAffectorManager.PostAffector>> GetGroups()
    {
        return posts != null && groupsField != null
            ? groupsField.GetValue(posts) as Dictionary<int, List<PostAffectorManager.PostAffector>>
            : null;
    }

    PostAffectorManager.PostAffector GetActive()
    {
        if (posts == null || activeIdField == null || activeGroupField == null || viewer == null) return null;
        bool selected = hasSelectionField != null && hasSelectionField.GetValue(viewer) is bool b && b;
        if (!selected) return null;

        int id = activeIdField.GetValue(posts) is int activeId ? activeId : -1;
        int group = activeGroupField.GetValue(posts) is int activeGroup ? activeGroup : -1;
        if (id < 0 || group < 0) return null;

        Dictionary<int, List<PostAffectorManager.PostAffector>> groups = GetGroups();
        return groups != null && groups.TryGetValue(group, out List<PostAffectorManager.PostAffector> list)
            ? list.FirstOrDefault(post => post != null && post.id == id)
            : null;
    }

    static float SpatialWeight(HairCard card, PostAffectorManager.PostAffector post)
    {
        Vector3 point = card.GetSpawnHitPoint();
        if (point == Vector3.zero) point = card.transform.position;
        float distance = Vector3.Distance(point, post.center);
        float radius = Mathf.Max(.001f, post.radius);
        float outer = radius + Mathf.Max(0f, post.falloff);
        if (distance <= radius) return 1f;
        if (post.falloff <= .000001f || distance >= outer) return 0f;
        return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(outer, radius, distance));
    }

    static float StablePostThreshold(HairCard card, PostAffectorManager.PostAffector post)
    {
        // Identity, not placement - see HairCard.identityPoint. Which cards a POST covers is a
        // per-card dice roll, so it has to be as stable under a groom-wide move as the variance
        // is; SpatialWeight above deliberately still uses the real spawn point, because coverage
        // DISTANCE is geometry rather than randomness.
        Vector3 p = card.GetIdentityPoint();
        if (p == Vector3.zero) p = card.transform.position;
        unchecked
        {
            uint hash = 2166136261u;
            Mix(ref hash, Mathf.RoundToInt(p.x * 10000f));
            Mix(ref hash, Mathf.RoundToInt(p.y * 10000f));
            Mix(ref hash, Mathf.RoundToInt(p.z * 10000f));
            Mix(ref hash, card.groupId);
            Mix(ref hash, post.id * 3571);
            hash ^= hash >> 16;
            hash *= 0x7feb352du;
            hash ^= hash >> 15;
            hash *= 0x846ca68bu;
            hash ^= hash >> 16;
            return (hash & 0x00FFFFFFu) / 16777216f;
        }
    }

    static int StableCardHash(HairCard card, int groupId, int seed)
    {
        // Identity, not placement - and this hash mixes the NORMAL in as well, so it re-rolls on
        // any groom-wide move even if positions were somehow preserved. See HairCard.identityPoint.
        Vector3 p = card.GetIdentityPoint();
        if (p == Vector3.zero) p = card.transform.position;
        Vector3 n = card.GetIdentityNormal();
        unchecked
        {
            uint hash = 2166136261u;
            Mix(ref hash, Mathf.RoundToInt(p.x * 10000f));
            Mix(ref hash, Mathf.RoundToInt(p.y * 10000f));
            Mix(ref hash, Mathf.RoundToInt(p.z * 10000f));
            Mix(ref hash, Mathf.RoundToInt(n.x * 10000f));
            Mix(ref hash, Mathf.RoundToInt(n.y * 10000f));
            Mix(ref hash, Mathf.RoundToInt(n.z * 10000f));
            Mix(ref hash, groupId);
            Mix(ref hash, seed);
            return (int)(hash & 0x7fffffff);
        }
    }

    static void Mix(ref uint hash, int value)
    {
        unchecked
        {
            hash ^= (uint)value;
            hash *= 16777619u;
        }
    }

    static int PositiveMod(int value, int modulus)
    {
        if (modulus <= 0) return 0;
        int result = value % modulus;
        return result < 0 ? result + modulus : result;
    }
}

// The group UV controller owns its row at execution order 6000. This tiny UI companion runs
// immediately after it so POST can replace the disabled group PRE row with a local PRE row
// without changing the group controller's ownership rules.
[DefaultExecutionOrder(6105)]
public class PostPredeterminedUVUIAuthority : MonoBehaviour
{
    private PostPredeterminedUVAuthority authority;
    private ModelViewer viewer;
    private GameObject row;
    private TMP_InputField minInput;
    private TMP_InputField maxInput;
    private TMP_InputField seedInput;
    private Button randomButton;
    private Transform hiddenGroupRow;

    void Update()
    {
        if (authority == null) authority = FindFirstObjectByType<PostPredeterminedUVAuthority>();
        if (viewer == null) viewer = FindFirstObjectByType<ModelViewer>();
        if (authority == null || viewer == null || viewer.groomingSliderPanelGO == null)
        {
            DestroyRow();
            return;
        }

        if (!authority.TryGetActiveContext(out _, out _, out int minId, out int maxId, out int seed))
        {
            DestroyRow();
            return;
        }

        Transform panel = viewer.groomingSliderPanelGO.transform;
        Transform groupRow = FindDirectOrDeep(panel, "GroupUVPredetermined_Row");
        if (groupRow != null)
        {
            hiddenGroupRow = groupRow;
            if (groupRow.gameObject.activeSelf) groupRow.gameObject.SetActive(false);
        }

        if (row == null || row.transform.parent != panel)
            BuildRow(panel);
        if (row == null) return;

        if (hiddenGroupRow != null && hiddenGroupRow.parent == panel)
            row.transform.SetSiblingIndex(Mathf.Min(hiddenGroupRow.GetSiblingIndex() + 1, panel.childCount - 1));

        if (minInput != null && !minInput.isFocused && minInput.text != minId.ToString())
            minInput.SetTextWithoutNotify(minId.ToString());
        if (maxInput != null && !maxInput.isFocused && maxInput.text != maxId.ToString())
            maxInput.SetTextWithoutNotify(maxId.ToString());
        if (seedInput != null && !seedInput.isFocused && seedInput.text != seed.ToString())
            seedInput.SetTextWithoutNotify(seed.ToString());
    }

    void BuildRow(Transform parent)
    {
        DestroyRow(false);

        row = new GameObject("PostPredeterminedUV_Row", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        row.transform.SetParent(parent, false);
        row.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 38f);

        HorizontalLayoutGroup layout = row.GetComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(0, 0, 3, 3);
        layout.spacing = 6f;
        layout.childControlWidth = false;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;

        TextMeshProUGUI label = AddText(row.transform, "POST UV", 13f, 88f, TextAlignmentOptions.MidlineLeft);
        label.fontStyle = FontStyles.Bold;
        minInput = AddIntInput(row.transform, "MIN", 64f);
        AddText(row.transform, "→", 13f, 22f, TextAlignmentOptions.Center);
        maxInput = AddIntInput(row.transform, "MAX", 64f);
        AddText(row.transform, "SEED", 11f, 48f, TextAlignmentOptions.Center);
        seedInput = AddIntInput(row.transform, "SEED", 105f);
        GameObject randomGO = AddButton(row.transform, "R", 42f, 30f);
        randomButton = randomGO.GetComponent<Button>();

        minInput.onEndEdit.AddListener(value => authority?.SetActiveRange(true, value));
        maxInput.onEndEdit.AddListener(value => authority?.SetActiveRange(false, value));
        seedInput.onEndEdit.AddListener(value => authority?.SetActiveSeed(value));
        randomButton.onClick.AddListener(() => authority?.RandomizeActiveSeed());
    }

    void DestroyRow(bool restoreGroupRow = true)
    {
        if (row != null) Destroy(row);
        row = null;
        minInput = null;
        maxInput = null;
        seedInput = null;
        randomButton = null;

        if (restoreGroupRow && hiddenGroupRow != null)
        {
            hiddenGroupRow.gameObject.SetActive(true);
        }
        hiddenGroupRow = null;
    }

    static Transform FindDirectOrDeep(Transform root, string name)
    {
        if (root == null) return null;
        Transform direct = root.Find(name);
        if (direct != null) return direct;
        foreach (Transform child in root)
        {
            if (child.name == name) return child;
            Transform nested = FindDirectOrDeep(child, name);
            if (nested != null) return nested;
        }
        return null;
    }

    static GameObject AddButton(Transform parent, string label, float width, float height)
    {
        GameObject go = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(width, height);
        go.GetComponent<Image>().color = new Color(.20f, .30f, .42f, 1f);

        GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textGO.transform.SetParent(go.transform, false);
        RectTransform rect = textGO.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        TextMeshProUGUI text = textGO.GetComponent<TextMeshProUGUI>();
        text.text = label;
        text.fontSize = 12f;
        text.fontStyle = FontStyles.Bold;
        text.alignment = TextAlignmentOptions.Center;
        text.color = Color.white;
        text.raycastTarget = false;
        return go;
    }

    static TextMeshProUGUI AddText(Transform parent, string value, float fontSize, float width, TextAlignmentOptions alignment)
    {
        GameObject go = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(width, 30f);
        TextMeshProUGUI text = go.GetComponent<TextMeshProUGUI>();
        text.text = value;
        text.fontSize = fontSize;
        text.alignment = alignment;
        text.color = Color.white;
        text.raycastTarget = false;
        return text;
    }

    static TMP_InputField AddIntInput(Transform parent, string placeholder, float width)
    {
        GameObject go = new GameObject(placeholder + "Input", typeof(RectTransform), typeof(Image), typeof(TMP_InputField));
        go.transform.SetParent(parent, false);
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(width, 30f);
        Image inputBg = go.GetComponent<Image>();
        if (UITheme.FineEdgeSprite != null)
        {
            inputBg.sprite = UITheme.FineEdgeSprite;
            inputBg.type = Image.Type.Sliced;
            inputBg.color = Color.white;
        }
        else
        {
            inputBg.color = new Color(.16f, .18f, .22f, 1f);
        }

        GameObject textArea = new GameObject("Text Area", typeof(RectTransform), typeof(RectMask2D));
        textArea.transform.SetParent(go.transform, false);
        RectTransform areaRect = textArea.GetComponent<RectTransform>();
        areaRect.anchorMin = Vector2.zero;
        areaRect.anchorMax = Vector2.one;
        areaRect.offsetMin = new Vector2(4f, 2f);
        areaRect.offsetMax = new Vector2(-4f, -2f);

        GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textGO.transform.SetParent(textArea.transform, false);
        RectTransform textRect = textGO.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;
        TextMeshProUGUI text = textGO.GetComponent<TextMeshProUGUI>();
        text.fontSize = 12f;
        text.alignment = TextAlignmentOptions.Center;
        text.color = Color.white;

        TMP_InputField input = go.GetComponent<TMP_InputField>();
        input.textViewport = areaRect;
        input.textComponent = text;
        input.contentType = TMP_InputField.ContentType.IntegerNumber;
        input.lineType = TMP_InputField.LineType.SingleLine;
        return input;
    }
}
