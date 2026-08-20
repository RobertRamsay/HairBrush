using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Modifier deletion is a two-phase operation:
// 1) On pointer-down, force the modifier to its neutral value and write that neutral result.
// 2) Button.onClick then removes the modifier record/UI normally.
// This prevents the last evaluated mesh/state from being stranded on existing HairCards.
[DefaultExecutionOrder(5400)]
public class ModifierNeutralizeBeforeDeleteAuthority : MonoBehaviour
{
    private GroupClumperManager clumperManager;
    private FieldInfo clumperGroupsField;
    private PostAffectorManager postManager;
    private FieldInfo postGroupsField;
    private MethodInfo postApplyAllMethod;
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<ModifierNeutralizeBeforeDeleteAuthority>() != null) return;
        GameObject go = new GameObject("ModifierNeutralizeBeforeDeleteAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<ModifierNeutralizeBeforeDeleteAuthority>();
    }

    void Update()
    {
        Resolve();
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + .08f;
        HookButtons();
    }

    void Resolve()
    {
        if (clumperManager == null)
        {
            clumperManager = FindFirstObjectByType<GroupClumperManager>();
            if (clumperManager != null)
                clumperGroupsField = typeof(GroupClumperManager).GetField("byGroup", BindingFlags.Instance | BindingFlags.NonPublic);
        }

        if (postManager == null)
        {
            postManager = FindFirstObjectByType<PostAffectorManager>();
            if (postManager != null)
            {
                postGroupsField = typeof(PostAffectorManager).GetField("groups", BindingFlags.Instance | BindingFlags.NonPublic);
                postApplyAllMethod = typeof(PostAffectorManager).GetMethod("ApplyAll", BindingFlags.Instance | BindingFlags.NonPublic);
            }
        }
    }

    void HookButtons()
    {
        RectTransform[] rows = FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (RectTransform row in rows)
        {
            if (row == null) continue;

            // Clumper rows are named "GroupClumper_{groupId}_{clumperId}". Parsing everything
            // after the prefix gave "0_1", int.TryParse failed, and NO clumper row was ever
            // hooked - so the "go to 0, then be removed" step has never actually run for a
            // CLUMPER. Split properly, and carry the clumper id through so the right one is
            // neutralised when a group holds more than one.
            if (row.name.StartsWith("GroupClumper_"))
            {
                string[] parts = row.name.Split('_');
                if (parts.Length >= 3 && int.TryParse(parts[1], out int clumpGid) && int.TryParse(parts[2], out int clumpId))
                    HookRow(row, ModifierDeleteNeutralizeHook.Kind.Clumper, clumpGid, clumpId);
                continue;
            }

            if (row.name.StartsWith("PostAffector_"))
            {
                string[] parts = row.name.Split('_');
                if (parts.Length >= 3 && int.TryParse(parts[1], out int postGid) && int.TryParse(parts[2], out int postId))
                    HookRow(row, ModifierDeleteNeutralizeHook.Kind.Post, postGid, postId);
            }
        }
    }

    void HookRow(RectTransform row, ModifierDeleteNeutralizeHook.Kind kind, int gid, int id)
    {
        foreach (Button button in row.GetComponentsInChildren<Button>(true))
        {
            // The remove button's GameObject is named after its label. That label became "DEL";
            // "[-]" stays accepted so a row built by an older build is still hooked. Without this
            // the whole pre-delete neutralize step would silently stop running.
            if (button == null) continue;
            string buttonName = button.gameObject.name;
            if (buttonName != "DEL" && buttonName != "[-]") continue;
            ModifierDeleteNeutralizeHook hook = button.GetComponent<ModifierDeleteNeutralizeHook>();
            if (hook == null) hook = button.gameObject.AddComponent<ModifierDeleteNeutralizeHook>();
            hook.Configure(kind, gid, id, clumperManager, clumperGroupsField, postManager, postGroupsField, postApplyAllMethod);
        }
    }
}

public class ModifierDeleteNeutralizeHook : MonoBehaviour, IPointerDownHandler
{
    public enum Kind { Clumper, Post }

    private Kind kind;
    private int groupId;
    private int modifierId;
    private GroupClumperManager clumperManager;
    private FieldInfo clumperGroupsField;
    private PostAffectorManager postManager;
    private FieldInfo postGroupsField;
    private MethodInfo postApplyAllMethod;

    public void Configure(Kind k, int gid, int id, GroupClumperManager cm, FieldInfo cg, PostAffectorManager pm, FieldInfo pg, MethodInfo apply)
    {
        kind = k;
        groupId = gid;
        modifierId = id;
        clumperManager = cm;
        clumperGroupsField = cg;
        postManager = pm;
        postGroupsField = pg;
        postApplyAllMethod = apply;
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        if (eventData == null || eventData.button != PointerEventData.InputButton.Left) return;
        if (kind == Kind.Clumper) NeutralizeClumper();
        else NeutralizePost();
    }

    void NeutralizeClumper()
    {
        if (clumperManager == null || clumperGroupsField == null) return;
        var groups = clumperGroupsField.GetValue(clumperManager) as Dictionary<int, List<GroupClumperManager.GroupClumper>>;
        if (groups == null || !groups.TryGetValue(groupId, out List<GroupClumperManager.GroupClumper> list) || list == null) return;

        // Take the clumper this button belongs to. Picking the first in the group silently
        // neutralised the wrong one whenever a group held more than one clumper.
        GroupClumperManager.GroupClumper target = null;
        foreach (GroupClumperManager.GroupClumper c in list)
        {
            if (c == null) continue;
            if (modifierId >= 0 && c.id != modifierId) continue;
            target = c;
            break;
        }
        if (target == null) return;

        target.amount = 0f;
        target.lastTopologyHash = 0;
        if (target.leaders != null) target.leaders.Clear();

        HairCard[] cards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        int reset = 0;
        foreach (HairCard card in cards)
        {
            if (card == null || card.groupId != groupId) continue;
            WriteCleanThreeColumnMesh(card);
            card.ClearClumpModifier();
            reset++;
        }
        Debug.Log("CLUMPER pre-delete neutralized group " + groupId + ": reset " + reset + " HairCards.");
    }

    void NeutralizePost()
    {
        if (postManager == null || postGroupsField == null || postApplyAllMethod == null) return;
        var groups = postGroupsField.GetValue(postManager) as Dictionary<int, List<PostAffectorManager.PostAffector>>;
        if (groups == null || !groups.TryGetValue(groupId, out List<PostAffectorManager.PostAffector> list) || list == null) return;

        PostAffectorManager.PostAffector target = null;
        foreach (PostAffectorManager.PostAffector a in list)
        {
            if (a != null && a.id == modifierId) { target = a; break; }
        }
        if (target == null) return;

        // POST has two sources for its live weight while selected: the affector's own weight
        // and ModelViewer.selectionStrength. Pointer-down and pointer-up can be on different
        // frames, so if only target.weight is cleared, MaintainActiveAuthoring() will copy the
        // old viewer strength straight back into it before the click actually removes POST.
        target.weight = 0f;

        FieldInfo activeIdField = typeof(PostAffectorManager).GetField("activeId", BindingFlags.Instance | BindingFlags.NonPublic);
        int activeId = activeIdField != null && activeIdField.GetValue(postManager) is int value ? value : -1;
        if (activeId == modifierId)
        {
            ModelViewer viewer = FindFirstObjectByType<ModelViewer>();
            if (viewer != null) viewer.selectionStrength = 0f;
        }

        // Write the neutral POST result immediately. If pointer-up happens on a later frame,
        // the selected POST remains at zero because selectionStrength was neutralized too.
        postApplyAllMethod.Invoke(postManager, null);
        Debug.Log("POST pre-delete neutralized group " + groupId + ", modifier " + modifierId + "; held at zero until deletion.");
    }

    static void WriteCleanThreeColumnMesh(HairCard card)
    {
        const int columns = HairCard.CrossSectionColumns;
        int segments = Mathf.Clamp(card.segments, 1, 60);
        Vector3[] vertices = new Vector3[(segments + 1) * columns];
        float halfWidth = Mathf.Max(.0005f, card.width) * .5f;
        float ridge = card.GetCrossSectionRidgeHeight();

        // Segment density remap, spine and path-following section frames come straight
        // from HairCard, so this reconstruction cannot drift from GenerateMesh. It once
        // had its own copy that predated Curl and Segment Density entirely and used a
        // hardcoded bend-only rotation ignoring X/Y/Z offsets and the Bend profile
        // curve. Same fix as ThreeColumnClumperMeshAuthority.BuildCleanMesh.
        float cardLength = Mathf.Max(.0001f, card.length);
        float[] segmentT = new float[segments + 1];
        Vector3[] segmentSpine = new Vector3[segments + 1];
        Quaternion[] segmentFrame = new Quaternion[segments + 1];
        HairCard.BuildSegmentFrames(card, segments, cardLength, segmentT, segmentSpine, segmentFrame);

        for (int i = 0; i <= segments; i++)
        {
            float t = segmentT[i];
            float z = t * cardLength;
            float span = halfWidth * card.flattenFactor;
            int index = i * columns;

            // HairCard.EvaluateCurl is the shared coil definition, so the neutralised
            // mesh keeps both the offset and the bank roll identical to GenerateMesh.
            // The bank shapes the section, then the offset moves it.
            Vector3 curlOffset;
            Quaternion bankRotation;
            HairCard.EvaluateCurl(card.groupId, card.curlFrequency, card.curlDiameter, t, out curlOffset, out bankRotation);

            Vector3 sectionOrigin = new Vector3(0f, 0f, z);
            Vector3 left = sectionOrigin + bankRotation * new Vector3(-span, 0f, 0f) + curlOffset;
            Vector3 center = sectionOrigin + bankRotation * new Vector3(0f, ridge, 0f) + curlOffset;
            Vector3 right = sectionOrigin + bankRotation * new Vector3(span, 0f, 0f) + curlOffset;

            Vector3 spinePoint = segmentSpine[i];
            Quaternion sectionFrame = segmentFrame[i];
            vertices[index] = spinePoint + sectionFrame * (left - sectionOrigin);
            vertices[index + 1] = spinePoint + sectionFrame * (center - sectionOrigin);
            vertices[index + 2] = spinePoint + sectionFrame * (right - sectionOrigin);
        }

        // Same rule as ThreeColumnClumperMeshAuthority.WriteFullMesh: HairCard.GetLiveMesh(),
        // never MeshFilter.mesh. That getter instantiates a duplicate and strands the card
        // writing into an orphan for the rest of the session.
        Mesh live = card.GetLiveMesh();
        if (live == null || live.vertexCount != vertices.Length) return;
        live.vertices = vertices;
        live.RecalculateNormals();
        live.RecalculateBounds();
    }
}
