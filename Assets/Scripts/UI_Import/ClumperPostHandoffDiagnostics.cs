using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

// TEMPORARY DIAGNOSTIC v2 - delete once the clumper/POST handoff is confirmed fixed.
//
// v1 only proved WHICH selection state was stranded. v2 also proves WHY an individual POST
// stops affecting cards, which is the "only POST 3 responds" symptom.
//
// Two independent log lines, each printed only when its own content changes:
//
//   HANDOFF ...   the selection pair (hotspot / activeId) plus clumper and brush state.
//   POSTS   ...   one entry per affector in the current group:
//                 id=<id> w=<weight> |d|=<delta magnitude> r/f/reach hit=<cards reached>
//
// How to read the POSTS line while a POST refuses to respond:
//
//   |d|=0.0000           The delta was WIPED. Something overwrote ModelViewer.current* with
//                        the group root while that POST was active, so MaintainActiveAuthoring
//                        recomputed delta = (root - baseline) = 0.
//   |d|>0 but hit=0      The delta survives but the affector reaches no cards. Its centre or
//                        radius was re-authored from ModelViewer.selectionHitPoint /
//                        brushRadius, which MaintainActiveAuthoring copies into the active
//                        POST EVERY frame it is selected.
//   |d|>0 and hit>0      POST is evaluating correctly and the loss is downstream of ApplyAll
//                        (something later in LateUpdate is overwriting the mesh).
//
// DELTA WIPE events print separately as warnings, with before/after values, because that
// transition is the single most useful event and is easy to miss in a scrolling console.
[DefaultExecutionOrder(9900)]
public class ClumperPostHandoffDiagnostics : MonoBehaviour
{
    private ModelViewer viewer = null;
    private PostAffectorManager posts = null;
    private GroupClumperManager clumpers = null;

    private FieldInfo hasSelectionField = null;
    private FieldInfo selectionModeField = null;
    private FieldInfo activeIdField = null;
    private FieldInfo activeGroupField = null;
    private FieldInfo postGroupsField = null;
    private FieldInfo hitPointField = null;
    private FieldInfo cardMeshField = null;
    private FieldInfo cardBaseVerticesField = null;

    private string lastHandoffLine = "";
    private string lastPostsLine = "";
    private readonly Dictionary<int, float> lastDeltaMagnitude = new Dictionary<int, float>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<ClumperPostHandoffDiagnostics>() != null) return;
        GameObject go = new GameObject("ClumperPostHandoffDiagnostics");
        DontDestroyOnLoad(go);
        go.AddComponent<ClumperPostHandoffDiagnostics>();
    }

    void LateUpdate()
    {
        Resolve();
        if (viewer == null || posts == null) return;

        int currentGroup = viewer.currentGroupId;
        List<HairCard> groupCards = new List<HairCard>();
        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
        {
            if (card == null || card.groupId != currentGroup) continue;
            groupCards.Add(card);
        }

        LogHandoff(currentGroup, groupCards);
        LogPosts(currentGroup, groupCards);
        LogMesh(currentGroup, groupCards);
    }

    // v3. The v2 run proved POST evaluation is healthy: activeId is bound, the delta tracks the
    // slider, and hundreds of cards are inside every affector's reach - yet nothing moves, and
    // the clump shape does not even relax when the clumper is deleted. That means the failure is
    // downstream of ApplyAll, in the card-state -> mesh chain. This isolates which link.
    //
    // Three columns, sampled on a few stable cards:
    //
    //   want=   canonical + POST effect, i.e. what ApplyAll SHOULD have written into the card.
    //   have=   the card's actual live field.
    //   sig=    HairCard.generatedMeshSignature. Set at the TOP of GenerateMesh, before the
    //           clump-override early-return, so it moves whenever GenerateMesh is entered at all.
    //   vhash=  a hash of the live MeshFilter.mesh vertices. This is what is on screen.
    //
    // Reading it:
    //   want != have            ApplyAll is not writing card state. PostAffectorManager.LateUpdate
    //                           is dying before ApplyAll (very likely an exception in
    //                           UpdateCanonicalBases - check the console with errors UNFILTERED).
    //   want == have, sig same  ApplyEvaluatedState is not calling GenerateMesh, or GenerateMesh
    //                           is bailing at its first line (mesh == null || segments < 1).
    //   sig moves, vhash same   GenerateMesh is entered but returns at the clump-override guard,
    //                           or something rewrites the mesh afterwards. Check activeClump.
    //   vhash moves, no visuals The mesh is updating and the problem is rendering/bounds.
    private float nextMeshScan = 0f;
    private string lastMeshLine = "";

    void LogMesh(int currentGroup, List<HairCard> groupCards)
    {
        if (Time.unscaledTime < nextMeshScan) return;
        nextMeshScan = Time.unscaledTime + .25f;
        if (groupCards.Count == 0) return;

        List<PostAffectorManager.PostAffector> list = null;
        if (postGroupsField != null)
        {
            var groups = postGroupsField.GetValue(posts) as Dictionary<int, List<PostAffectorManager.PostAffector>>;
            if (groups != null) groups.TryGetValue(currentGroup, out list);
        }

        // Stable sample: lowest instance IDs, so the same cards are reported every time.
        groupCards.Sort(CompareByInstanceId);

        StringBuilder builder = new StringBuilder();
        builder.Append("MESH group=").Append(currentGroup)
               .Append(" activeClump=").Append(GroupClumperManager.HasActiveClumper(currentGroup));

        int sampled = 0;
        foreach (HairCard card in groupCards)
        {
            if (sampled >= 3) break;
            MeshFilter filter = card.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null) continue;
            sampled++;

            HairCard.GroomState canonical = card.GetCanonicalState();

            float wantLength = canonical.length;
            float wantBend = canonical.bend;
            float wantX = canonical.x;
            if (list != null)
            {
                foreach (PostAffectorManager.PostAffector a in list)
                {
                    if (a == null) continue;
                    float w = SpatialWeight(card, a) * Mathf.Clamp01(a.weight);
                    if (w <= .000001f) continue;
                    wantLength += a.delta.length * w;
                    wantBend += a.delta.bend * w;
                    wantX += a.delta.x * w;
                }
            }

            // v4. The v3 run showed sig moving every frame (so GenerateMesh IS running and IS
            // producing new source vertices) while vhash on the MeshFilter never moved at all.
            // With activeClump=False the clump-override early-return cannot fire, so GenerateMesh
            // must be reaching "mesh.vertices = baseVertices". The only way both can be true is
            // if HairCard's own private mesh reference is no longer the mesh the MeshFilter
            // renders. These three columns separate the possibilities:
            //
            //   baseHash   HairCard.baseVertices - what GenerateMesh just computed.
            //   cardMesh   HairCard.mesh - the Mesh object HairCard writes into. id + hash.
            //   filtMesh   MeshFilter.sharedMesh - the Mesh object actually rendered. id + hash.
            //
            //   baseHash moves, cardHash frozen   -> GenerateMesh returned before the write.
            //   cardHash moves, filtHash frozen   -> the two Mesh objects have diverged. The card
            //                                        is updating an orphan nobody renders.
            //   cardId != filtId                  -> same thing, proven by identity.
            //   all three move                    -> the pipeline is fine, problem is rendering.
            Mesh cardMesh = null;
            if (cardMeshField != null) cardMesh = cardMeshField.GetValue(card) as Mesh;

            Vector3[] baseVertices = null;
            if (cardBaseVerticesField != null) baseVertices = cardBaseVerticesField.GetValue(card) as Vector3[];

            int cardMeshId = 0;
            int cardHash = 0;
            if (cardMesh != null)
            {
                cardMeshId = cardMesh.GetInstanceID();
                cardHash = VertexHash(cardMesh);
            }

            builder.Append("\n  card#").Append(card.GetInstanceID())
                   .Append(" wantLen=").Append(wantLength.ToString("F5"))
                   .Append(" haveLen=").Append(card.length.ToString("F5"))
                   .Append(" haveBend=").Append(card.bendAngle.ToString("F3"))
                   .Append(" sig=").Append(card.GetGeneratedMeshSignature())
                   .Append(" baseHash=").Append(ArrayHash(baseVertices))
                   .Append(" cardId=").Append(cardMeshId)
                   .Append(" cardHash=").Append(cardHash)
                   .Append(" filtId=").Append(filter.sharedMesh.GetInstanceID())
                   .Append(" filtHash=").Append(VertexHash(filter.sharedMesh))
                   .Append(" vcount=").Append(filter.sharedMesh.vertexCount);
        }

        string line = builder.ToString();
        if (line == lastMeshLine) return;
        lastMeshLine = line;
        Debug.Log(line);
    }

    static int CompareByInstanceId(HairCard a, HairCard b)
    {
        return a.GetInstanceID().CompareTo(b.GetInstanceID());
    }

    static int ArrayHash(Vector3[] vertices)
    {
        if (vertices == null) return 0;
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + vertices.Length;
            for (int i = 0; i < vertices.Length; i++)
            {
                hash = hash * 31 + vertices[i].x.GetHashCode();
                hash = hash * 31 + vertices[i].y.GetHashCode();
                hash = hash * 31 + vertices[i].z.GetHashCode();
            }
            return hash;
        }
    }

    static int VertexHash(Mesh mesh)
    {
        Vector3[] vertices = mesh.vertices;
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + vertices.Length;
            for (int i = 0; i < vertices.Length; i++)
            {
                hash = hash * 31 + vertices[i].x.GetHashCode();
                hash = hash * 31 + vertices[i].y.GetHashCode();
                hash = hash * 31 + vertices[i].z.GetHashCode();
            }
            return hash;
        }
    }

    void LogHandoff(int currentGroup, List<HairCard> groupCards)
    {
        bool hotspot = ReadViewerBool(hasSelectionField);
        bool selectionMode = ReadViewerBool(selectionModeField);
        int activeId = ReadPostInt(activeIdField);
        int activeGroup = ReadPostInt(activeGroupField);

        int clumperCount = 0;
        int selectedClumper = -1;
        float clumperAmount = 0f;
        if (clumpers != null)
        {
            List<GroupClumperManager.GroupClumper> list = clumpers.GetGroupClumpers(currentGroup);
            clumperCount = list.Count;
            foreach (GroupClumperManager.GroupClumper c in list)
            {
                if (c != null && c.amount > clumperAmount) clumperAmount = c.amount;
            }
            GroupClumperManager.GroupClumper selected = clumpers.GetSelectedClumper();
            if (selected != null) selectedClumper = selected.id;
        }

        float maxSelectionWeight = 0f;
        foreach (HairCard card in groupCards)
        {
            if (card.selectionWeight > maxSelectionWeight) maxSelectionWeight = card.selectionWeight;
        }

        Vector3 hitPoint = Vector3.zero;
        if (hitPointField != null && hitPointField.GetValue(viewer) is Vector3 hp) hitPoint = hp;

        string line =
            "HANDOFF hotspot=" + hotspot +
            " selMode=" + selectionMode +
            " activeId=" + activeId +
            " activeGroup=" + activeGroup +
            " curGroup=" + currentGroup +
            " clumpers=" + clumperCount +
            " clumpAmount=" + clumperAmount.ToString("F3") +
            " selClumper=" + selectedClumper +
            " cards=" + groupCards.Count +
            " maxSelWeight=" + maxSelectionWeight.ToString("F3") +
            " hitPoint=" + hitPoint.ToString("F4") +
            " brushRadius=" + viewer.brushRadius.ToString("F4") +
            " brushFalloff=" + viewer.brushFalloffDistance.ToString("F4") +
            " strength=" + viewer.selectionStrength.ToString("F3");

        if (line == lastHandoffLine) return;
        lastHandoffLine = line;
        Debug.Log(line);
    }

    void LogPosts(int currentGroup, List<HairCard> groupCards)
    {
        if (postGroupsField == null) return;
        var groups = postGroupsField.GetValue(posts) as Dictionary<int, List<PostAffectorManager.PostAffector>>;
        if (groups == null) return;

        List<PostAffectorManager.PostAffector> list = null;
        if (!groups.TryGetValue(currentGroup, out list) || list == null)
        {
            string emptyLine = "POSTS group=" + currentGroup + " (none)";
            if (emptyLine == lastPostsLine) return;
            lastPostsLine = emptyLine;
            Debug.Log(emptyLine);
            return;
        }

        StringBuilder builder = new StringBuilder();
        builder.Append("POSTS group=").Append(currentGroup);

        foreach (PostAffectorManager.PostAffector affector in list)
        {
            if (affector == null) continue;

            float magnitude = DeltaMagnitude(affector.delta);
            float reach = Mathf.Max(.001f, affector.radius) + Mathf.Max(0f, affector.falloff);

            int hitCards = 0;
            foreach (HairCard card in groupCards)
            {
                if (SpatialWeight(card, affector) > .000001f) hitCards++;
            }

            builder.Append("  [id=").Append(affector.id)
                   .Append(" w=").Append(affector.weight.ToString("F3"))
                   .Append(" |d|=").Append(magnitude.ToString("F4"))
                   .Append(" r=").Append(affector.radius.ToString("F4"))
                   .Append(" f=").Append(affector.falloff.ToString("F4"))
                   .Append(" reach=").Append(reach.ToString("F4"))
                   .Append(" hit=").Append(hitCards)
                   .Append("]");

            ReportDeltaWipe(affector, magnitude);
        }

        string line = builder.ToString();
        if (line == lastPostsLine) return;
        lastPostsLine = line;
        Debug.Log(line);
    }

    // A delta collapsing to zero is the single most diagnostic event available.
    void ReportDeltaWipe(PostAffectorManager.PostAffector affector, float magnitude)
    {
        float previous = 0f;
        bool known = lastDeltaMagnitude.TryGetValue(affector.id, out previous);
        lastDeltaMagnitude[affector.id] = magnitude;

        if (!known) return;
        if (previous <= .0001f) return;
        if (magnitude > .0001f) return;

        Debug.LogWarning("POST DELTA WIPED  id=" + affector.id +
                         "  was |d|=" + previous.ToString("F4") +
                         "  now |d|=" + magnitude.ToString("F4") +
                         "  baseline.bend=" + affector.baseline.bend.ToString("F3") +
                         "  viewer.currentBend=" + viewer.currentBend.ToString("F3") +
                         "  -> ModelViewer.current* was overwritten with the group root while " +
                         "this POST was active, so delta recomputed as (root - baseline).");
    }

    static float DeltaMagnitude(PostAffectorManager.ControlState d)
    {
        float total = 0f;
        total += Mathf.Abs(d.length);
        total += Mathf.Abs(d.width);
        total += Mathf.Abs(d.segments);
        total += Mathf.Abs(d.bend);
        total += Mathf.Abs(d.twist);
        total += Mathf.Abs(d.depth);
        total += Mathf.Abs(d.x);
        total += Mathf.Abs(d.y);
        total += Mathf.Abs(d.z);
        total += Mathf.Abs(d.uScale);
        total += Mathf.Abs(d.vScale);
        total += Mathf.Abs(d.uOffset);
        total += Mathf.Abs(d.vOffset);
        total += Mathf.Abs(d.curlFrequency);
        total += Mathf.Abs(d.curlDiameter);
        return total;
    }

    // Mirrors PostAffectorManager.SpatialWeight exactly.
    static float SpatialWeight(HairCard card, PostAffectorManager.PostAffector a)
    {
        Vector3 p = card.GetSpawnHitPoint();
        if (p == Vector3.zero) p = card.transform.position;
        float d = Vector3.Distance(p, a.center);
        float radius = Mathf.Max(.001f, a.radius);
        float outer = radius + Mathf.Max(0f, a.falloff);
        if (d <= radius) return 1f;
        if (a.falloff <= .000001f || d >= outer) return 0f;
        return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(outer, radius, d));
    }

    void Resolve()
    {
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;

        if (viewer == null)
        {
            viewer = FindFirstObjectByType<ModelViewer>();
            if (viewer != null)
            {
                hasSelectionField = typeof(ModelViewer).GetField("hasSelectionHotspot", flags);
                selectionModeField = typeof(ModelViewer).GetField("isSelectionMode", flags);
                hitPointField = typeof(ModelViewer).GetField("selectionHitPoint", flags);
            }
        }

        if (posts == null)
        {
            posts = FindFirstObjectByType<PostAffectorManager>();
            if (posts != null)
            {
                activeIdField = typeof(PostAffectorManager).GetField("activeId", flags);
                activeGroupField = typeof(PostAffectorManager).GetField("activeGroup", flags);
                postGroupsField = typeof(PostAffectorManager).GetField("groups", flags);
            }
        }

        if (clumpers == null) clumpers = FindFirstObjectByType<GroupClumperManager>();

        if (cardMeshField == null)
            cardMeshField = typeof(HairCard).GetField("mesh", flags);
        if (cardBaseVerticesField == null)
            cardBaseVerticesField = typeof(HairCard).GetField("baseVertices", flags);
    }

    bool ReadViewerBool(FieldInfo field)
    {
        if (field == null) return false;
        object value = field.GetValue(viewer);
        if (value is bool result) return result;
        return false;
    }

    int ReadPostInt(FieldInfo field)
    {
        if (field == null) return -999;
        object value = field.GetValue(posts);
        if (value is int result) return result;
        return -999;
    }
}
