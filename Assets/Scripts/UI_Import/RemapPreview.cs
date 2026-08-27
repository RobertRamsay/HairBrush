using System.Collections.Generic;
using UnityEngine;

// Moving the groom onto the new head, reversibly.
//
// Two steps per anchor. The spline puts a point NEAR the new scalp; the projection puts it ON it.
// The warp on its own is not enough - it interpolates its markers exactly and everything between
// them approximately, and "approximately on a surface" is a root floating in the air.
//
// The projection casts OUTWARD FROM INSIDE the head, which is the whole trick. Cast inward from
// outside and the ray reaches an ear, a jaw, or the far side of the skull before it reaches the
// scalp - near the ears the warped normal is close to tangent to other geometry, and that is
// exactly where hair lives. From an interior origin the first surface crossed along the outward
// normal is the scalp by construction: an ear is further out along that ray and cannot be hit
// first. One raycast, no hit-list walking, no thickness heuristic.
//
// Bob's first instinct was to walk the hit list and take the face before the last inner one. That
// is nearly right and fails on its anchor: trace inward near an ear and you get ear outer, ear
// inner, scalp, far-side exit, so last-minus-one is correct only until the ray also leaves through
// the other ear or a neck stub. Ordinal position in the list is not stable. The generalisation
// that DOES hold is thickness - pair the faces into solid spans and take the entry of the longest,
// since an ear is millimetres thick and a head is centimetres - and that is kept below as the
// fallback for the case the inside-out cast cannot serve: a mesh with no interior to start from.
public class RemapProjectionReport
{
    public int cards;
    public int guides;
    public int clumpers;
    public int postAffectors;
    public int projected;
    public int usedFallback;
    public int failed;
    public float largestMove;

    public override string ToString()
    {
        return cards + " cards, " + guides + " guides, " + clumpers + " clumpers, " + postAffectors + " POSTs; "
            + projected + " landed, " + usedFallback + " needed the fallback, " + failed + " could not be placed; "
            + "largest move " + (largestMove * 1000f).ToString("F1") + "mm";
    }
}

// The spline and the projection behind the mapping interface the anchor passes already speak.
//
// MapPoint does the warp AND the surface projection, so every caller - cards, guides, clumpers,
// POSTs - gets a point that is genuinely on the new head without knowing any of this exists. The
// result is cached against its input because MapPoint, MapNormal and LocalScale are three separate
// calls about the same anchor and the raycast should happen once.
public class TpsAnchorMapping : GroomAnchorMapping
{
    private ThinPlateSpline3D spline;
    private int layerMask;
    private float headSize = 1f;

    private bool hasCache;
    private Vector3 cacheKey;
    private Vector3 cacheNormalKey;
    private Vector3 cachePoint;
    private Vector3 cacheNormal;

    public RemapProjectionReport Report = new RemapProjectionReport();

    public TpsAnchorMapping(ThinPlateSpline3D solved, int targetLayerMask, float targetHeadSize)
    {
        spline = solved;
        layerMask = targetLayerMask;
        headSize = targetHeadSize;
        if (headSize < .000001f) headSize = 1f;
    }

    // The pair every caller in this file uses. One resolve, one raycast, both answers.
    public override void MapAnchor(Vector3 worldPoint, Vector3 worldNormal, out Vector3 movedPoint, out Vector3 movedNormal)
    {
        Resolve(worldPoint, worldNormal);
        movedPoint = cachePoint;
        movedNormal = cacheNormal;
    }

    // Point-only entry points, kept because the base class defines them. They have to guess a
    // normal, and world up is the guess - which is close enough on a scalp and badly wrong on a
    // jaw. Nothing in the remap path calls these; MapAnchor exists so nothing has to.
    public override Vector3 MapPoint(Vector3 worldPoint)
    {
        Resolve(worldPoint, Vector3.up);
        return cachePoint;
    }

    public override Vector3 MapNormal(Vector3 worldPoint, Vector3 worldNormal)
    {
        Resolve(worldPoint, worldNormal);
        return cacheNormal;
    }

    public override float LocalScale(Vector3 worldPoint)
    {
        return spline.LocalScale(worldPoint);
    }

    void Resolve(Vector3 worldPoint, Vector3 worldNormal)
    {
        if (hasCache && (cacheKey - worldPoint).sqrMagnitude < 1e-12f && (cacheNormalKey - worldNormal).sqrMagnitude < 1e-12f) return;

        Vector3 warped = spline.Map(worldPoint);
        // Through the Jacobian, not through Map. A normal is a covector and the warp is
        // nonlinear; carried the wrong way it tilts off the surface under any shear, and a
        // head-to-head warp is mostly shear.
        //
        // And it is the ANCHOR'S OWN normal, not a fixed axis. This used to pass Vector3.up,
        // which is very nearly right on a scalp - up IS roughly the scalp normal - and completely
        // wrong on a jaw, a cheek or under a chin, where the surface faces sideways or down. The
        // projection below casts along this direction, so a groom's scalp landed correctly while
        // its beard was projected along a ray pointing at nothing in particular.
        Vector3 warpedNormal = spline.MapNormal(worldPoint, worldNormal);

        Vector3 point;
        Vector3 normal;
        if (ProjectFromInside(warped, warpedNormal, layerMask, headSize, out point, out normal))
        {
            Report.projected++;
        }
        else if (ProjectByThickness(warped, warpedNormal, layerMask, headSize, out point, out normal))
        {
            Report.usedFallback++;
        }
        else
        {
            // Nothing was hit either way. The warped position is kept so the card still exists
            // somewhere sensible and can be found and fixed, rather than collapsing to the origin.
            point = warped;
            normal = warpedNormal;
            Report.failed++;
        }

        float moved = Vector3.Distance(worldPoint, point);
        if (moved > Report.largestMove) Report.largestMove = moved;

        hasCache = true;
        cacheKey = worldPoint;
        cacheNormalKey = worldNormal;
        cachePoint = point;
        cacheNormal = normal;
    }

    // Push in, cast out, take the first hit, require a back face.
    //
    // A front face means the origin was still outside the mesh, which is the self-correcting
    // signal: grow the push and try again. The push is adaptive rather than a fixed fraction of
    // model size so thin geometry is not punched straight through on the first attempt.
    public static bool ProjectFromInside(Vector3 point, Vector3 normal, int layerMask, float headSize, out Vector3 hitPoint, out Vector3 hitNormal)
    {
        hitPoint = point;
        hitNormal = normal;

        Vector3 n = normal;
        if (n.sqrMagnitude < .000001f) n = Vector3.up;
        n = n.normalized;

        // Cast-from-inside cannot land on an ear, because an ear is further out along the ray
        // than the scalp is. It CAN land on the wrong surface where "inside" stops being
        // unambiguous - the neck-to-jaw junction, under a chin - and a beard lives exactly there.
        // So a landing whose surface faces nothing like the direction it was sought in is kept as
        // a candidate rather than accepted, and the search carries on for a better one.
        float bestAgreement = -2f;
        Vector3 bestPoint = point;
        Vector3 bestNormal = n;
        bool found = false;

        float push = headSize * .02f;
        for (int attempt = 0; attempt < 7; attempt++)
        {
            Vector3 origin = point - n * push;
            RaycastHit hit;
            if (Physics.Raycast(origin, n, out hit, push + headSize, layerMask))
            {
                // Hit from behind: the triangle's outward normal runs with the ray.
                if (Vector3.Dot(hit.normal, n) > 0f)
                {
                    float agreement = Vector3.Dot(hit.normal.normalized, n);
                    if (agreement > bestAgreement)
                    {
                        bestAgreement = agreement;
                        bestPoint = hit.point;
                        bestNormal = hit.normal;
                        found = true;
                    }
                    // Within about 75 degrees is a surface genuinely facing the way the anchor
                    // was pointing. Anything shallower is a grazing hit on a fold and is worth
                    // one more push to try to beat.
                    if (agreement > .26f)
                    {
                        hitPoint = hit.point;
                        hitNormal = hit.normal;
                        return true;
                    }
                }
            }
            push = push * 2f;
        }

        if (found)
        {
            hitPoint = bestPoint;
            hitNormal = bestNormal;
            return true;
        }
        return false;
    }

    // The general form of "the face before the last inner one", anchored on thickness rather than
    // on position in the list. Used when there is no interior to cast from - an open neck hole, a
    // scan that is a shell rather than a solid.
    public static bool ProjectByThickness(Vector3 point, Vector3 normal, int layerMask, float headSize, out Vector3 hitPoint, out Vector3 hitNormal)
    {
        hitPoint = point;
        hitNormal = normal;

        Vector3 n = normal;
        if (n.sqrMagnitude < .000001f) n = Vector3.up;
        n = n.normalized;

        Vector3 origin = point + n * headSize;
        RaycastHit[] hits = Physics.RaycastAll(origin, -n, headSize * 3f, layerMask);
        if (hits == null || hits.Length == 0) return false;

        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        bool inside = false;
        float spanStart = 0f;
        Vector3 spanPoint = point;
        Vector3 spanNormal = n;
        float bestSpan = -1f;
        float lastDistance = -1f;

        for (int i = 0; i < hits.Length; i++)
        {
            // Shared triangle edges produce doubled hits at the same distance, which would flip
            // the enter/exit parity and shuffle every span after them.
            if (Mathf.Abs(hits[i].distance - lastDistance) < .00001f) continue;
            lastDistance = hits[i].distance;

            bool entering = Vector3.Dot(hits[i].normal, -n) > 0f;
            if (entering && !inside)
            {
                inside = true;
                spanStart = hits[i].distance;
                spanPoint = hits[i].point;
                spanNormal = hits[i].normal;
                continue;
            }
            if (!entering && inside)
            {
                inside = false;
                float thickness = hits[i].distance - spanStart;
                if (thickness <= bestSpan) continue;
                bestSpan = thickness;
                hitPoint = spanPoint;
                hitNormal = spanNormal;
            }
        }

        return bestSpan > 0f;
    }
}

// What a preview replaced, so CANCEL can put it back exactly.
public class RemapPreviewSnapshot
{
    public List<HairCard> cards = new List<HairCard>();
    public List<Vector3> cardPoints = new List<Vector3>();
    public List<Vector3> cardNormals = new List<Vector3>();
    public List<float> cardDepths = new List<float>();
    public List<float> cardOffsetX = new List<float>();
    public List<float> cardOffsetY = new List<float>();
    public List<float> cardOffsetZ = new List<float>();
    public List<int> cardGroups = new List<int>();

    public List<GuideCurveManager.GuideCurve> guides = new List<GuideCurveManager.GuideCurve>();
    public List<Vector3> guideContacts = new List<Vector3>();
    public List<Vector3> guideNormals = new List<Vector3>();
    public List<Quaternion> guideFrames = new List<Quaternion>();
    public List<Vector3[]> guideNodes = new List<Vector3[]>();
    public List<float> guideRadii = new List<float>();
    public List<float> guideFalloffs = new List<float>();

    public List<GroupClumperManager.GroupClumper> clumpers = new List<GroupClumperManager.GroupClumper>();
    public List<Vector3> clumperCentres = new List<Vector3>();
    public List<Vector3> clumperNormals = new List<Vector3>();
    public List<float> clumperRadii = new List<float>();
    public List<float> clumperFalloffs = new List<float>();

    public List<int> postGroups = new List<int>();
    public List<List<PostAffectorSaveData>> postPayloads = new List<List<PostAffectorSaveData>>();
}

public static class RemapPreview
{
    // A fraction of the mean marker spacing, so it means the same thing on any size of head.
    // Small: the markers are the user's own statements about where things go, and a lambda large
    // enough to visibly disobey them would be answering a question nobody asked.
    private const float LambdaFraction = .002f;

    public static bool Run(
        List<RemapMarker> markers,
        int targetLayer,
        float targetHeadSize,
        out RemapPreviewSnapshot snapshot,
        out RemapProjectionReport report,
        out string failure)
    {
        snapshot = new RemapPreviewSnapshot();
        report = new RemapProjectionReport();
        failure = string.Empty;

        List<Vector3> source = new List<Vector3>();
        List<Vector3> target = new List<Vector3>();
        foreach (RemapMarker marker in markers)
        {
            if (marker == null || !marker.Paired) continue;
            source.Add(marker.sourcePoint);
            target.Add(marker.targetPoint);
        }

        if (source.Count < 4)
        {
            failure = "at least four matched marker pairs are needed to solve a warp";
            return false;
        }

        ThinPlateSpline3D spline = ThinPlateSpline3D.Solve(source, target, ThinPlateSpline3D.SuggestedLambda(source, LambdaFraction));
        if (!spline.Valid)
        {
            failure = "the markers do not define a warp - they are coincident or nearly all in one plane";
            return false;
        }

        TpsAnchorMapping mapping = new TpsAnchorMapping(spline, 1 << targetLayer, targetHeadSize);

        // Global, and it must be restored. Left on, the grooming click raycast would let a user
        // place hair on the inside of the skull.
        bool previousBackfaces = Physics.queriesHitBackfaces;
        Physics.queriesHitBackfaces = true;
        try
        {
            MoveCards(mapping, snapshot);
            MoveGuides(mapping, snapshot);
            MoveClumpers(mapping, snapshot);
            MovePostAffectors(mapping, snapshot);
        }
        finally
        {
            Physics.queriesHitBackfaces = previousBackfaces;
        }

        report = mapping.Report;
        return true;
    }

    static void MoveCards(TpsAnchorMapping mapping, RemapPreviewSnapshot snapshot)
    {
        foreach (HairCard card in Object.FindObjectsByType<HairCard>(FindObjectsSortMode.None))
        {
            if (card == null) continue;

            Vector3 point = card.GetSpawnHitPoint();
            Vector3 normal = card.GetSurfaceNormal();

            snapshot.cards.Add(card);
            snapshot.cardPoints.Add(point);
            snapshot.cardNormals.Add(normal);
            snapshot.cardDepths.Add(card.GetEmbedDepth());
            snapshot.cardOffsetX.Add(card.GetOffsetX());
            snapshot.cardOffsetY.Add(card.GetOffsetY());
            snapshot.cardOffsetZ.Add(card.GetOffsetZ());
            snapshot.cardGroups.Add(card.groupId);

            // Frozen at the pre-warp values, and BEFORE the anchor moves. Five deterministic sites
            // key a card's variance, its predetermined UV rectangle and its clump leadership to
            // this pair; without the freeze the first visible act of a remap is scrambling the
            // randomisation of every card in the project.
            card.SetIdentity(point, normal, 1f);

            Vector3 moved;
            Vector3 movedNormal;
            mapping.MapAnchor(point, normal, out moved, out movedNormal);

            // Embed depth and the card's own length and width are deliberately NOT scaled. They
            // are authored intent rather than a consequence of where the head is, and scaling
            // depth alone would change ClumperDeterministicLeaderAuthority's key even with
            // identity frozen. Modifier radii below are different: those are reach, and reach has
            // to follow the geometry.
            card.SetPlacementData(moved, movedNormal, card.GetEmbedDepth(), card.GetOffsetX(), card.GetOffsetY(), card.GetOffsetZ(), card.groupId);

            // The island tag caches a flood-fill answer derived by raycasting from the old spawn
            // point and is never invalidated. Left behind, every moved card keeps a topology id
            // belonging to a mesh it is no longer on.
            HairCardSurfaceIsland island = card.GetComponent<HairCardSurfaceIsland>();
            if (island != null) Object.Destroy(island);

            mapping.Report.cards++;
        }
    }

    static void MoveGuides(TpsAnchorMapping mapping, RemapPreviewSnapshot snapshot)
    {
        GuideCurveManager manager = Object.FindFirstObjectByType<GuideCurveManager>();
        if (manager == null) return;

        List<GuideCurveManager.GuideCurve> guides = manager.GetAllGuides();
        if (guides == null) return;

        foreach (GuideCurveManager.GuideCurve guide in guides)
        {
            if (guide == null) continue;

            snapshot.guides.Add(guide);
            snapshot.guideContacts.Add(guide.contact);
            snapshot.guideNormals.Add(guide.normal);
            snapshot.guideFrames.Add(guide.frame);
            snapshot.guideNodes.Add(guide.nodesLocal.ToArray());
            snapshot.guideRadii.Add(guide.radius);
            snapshot.guideFalloffs.Add(guide.falloff);

            Vector3 oldNormal = guide.normal;
            float scale = mapping.LocalScale(guide.contact);
            Vector3 moved;
            Vector3 movedNormal;
            mapping.MapAnchor(guide.contact, guide.normal, out moved, out movedNormal);

            guide.contact = moved;
            guide.normal = movedNormal;
            // Transported, never rebuilt. Reconstructing the frame from the new normal rolls the
            // saved shape about its own axis, which is the entire reason a quaternion is carried
            // instead of derived - and the exact-reversal case names its own axis rather than
            // letting FromToRotation pick one arbitrarily.
            guide.frame = TransportFrame(guide.frame, oldNormal, movedNormal);

            // Nodes are offsets in that frame - lengths, not positions. Running them through
            // MapPoint would treat a local offset as a world point.
            for (int i = 0; i < guide.nodesLocal.Count; i++) guide.nodesLocal[i] = guide.nodesLocal[i] * scale;
            guide.radius = guide.radius * scale;
            guide.falloff = guide.falloff * scale;

            mapping.Report.guides++;
        }
    }

    static Quaternion TransportFrame(Quaternion frame, Vector3 from, Vector3 to)
    {
        Vector3 a = from;
        Vector3 b = to;
        if (a.sqrMagnitude < .000001f || b.sqrMagnitude < .000001f) return frame;
        a = a.normalized;
        b = b.normalized;

        float dot = Vector3.Dot(a, b);
        if (dot > .9999f) return frame;
        if (dot < -.9999f)
        {
            // Exactly reversed: every perpendicular axis is an equally correct rotation, so
            // FromToRotation picks one at random and the shape cartwheels. Naming one keeps it
            // stable frame to frame.
            Vector3 axis = Vector3.Cross(a, Vector3.up);
            if (axis.sqrMagnitude < .000001f) axis = Vector3.Cross(a, Vector3.right);
            return Quaternion.AngleAxis(180f, axis.normalized) * frame;
        }
        return Quaternion.FromToRotation(a, b) * frame;
    }

    static void MoveClumpers(TpsAnchorMapping mapping, RemapPreviewSnapshot snapshot)
    {
        GroupClumperManager manager = Object.FindFirstObjectByType<GroupClumperManager>();
        if (manager == null) return;

        List<GroupClumperManager.GroupClumper> clumpers = manager.GetAllClumpers();
        if (clumpers == null) return;

        foreach (GroupClumperManager.GroupClumper clumper in clumpers)
        {
            if (clumper == null) continue;

            snapshot.clumpers.Add(clumper);
            snapshot.clumperCentres.Add(clumper.center);
            snapshot.clumperNormals.Add(clumper.normal);
            snapshot.clumperRadii.Add(clumper.radius);
            snapshot.clumperFalloffs.Add(clumper.falloff);

            float scale = mapping.LocalScale(clumper.center);
            Vector3 moved;
            Vector3 movedNormal;
            mapping.MapAnchor(clumper.center, clumper.normal, out moved, out movedNormal);

            clumper.center = moved;
            clumper.normal = movedNormal;
            clumper.radius = clumper.radius * scale;
            clumper.falloff = clumper.falloff * scale;
            manager.Invalidate(clumper);

            mapping.Report.clumpers++;
        }
    }

    // POSTs have no public enumeration of the live objects, but they do have a save round trip,
    // and that is enough: export, move the payload, import it back.
    static void MovePostAffectors(TpsAnchorMapping mapping, RemapPreviewSnapshot snapshot)
    {
        PostAffectorManager manager = Object.FindFirstObjectByType<PostAffectorManager>();
        if (manager == null) return;

        HashSet<int> groupIds = new HashSet<int>();
        foreach (HairCard card in Object.FindObjectsByType<HairCard>(FindObjectsSortMode.None))
        {
            if (card == null) continue;
            groupIds.Add(card.groupId);
        }

        foreach (int groupId in groupIds)
        {
            List<PostAffectorSaveData> payload = manager.ExportGroup(groupId);
            if (payload == null || payload.Count == 0) continue;

            snapshot.postGroups.Add(groupId);
            snapshot.postPayloads.Add(ClonePosts(payload));

            foreach (PostAffectorSaveData post in payload)
            {
                if (post == null) continue;
                Vector3 centre = new Vector3(post.centerX, post.centerY, post.centerZ);
                Vector3 normal = new Vector3(post.normalX, post.normalY, post.normalZ);

                float scale = mapping.LocalScale(centre);
                Vector3 moved;
                Vector3 movedNormal;
                mapping.MapAnchor(centre, normal, out moved, out movedNormal);

                post.centerX = moved.x;
                post.centerY = moved.y;
                post.centerZ = moved.z;
                post.normalX = movedNormal.x;
                post.normalY = movedNormal.y;
                post.normalZ = movedNormal.z;
                post.radius = post.radius * scale;
                post.falloff = post.falloff * scale;

                mapping.Report.postAffectors++;
            }

            manager.ImportGroup(groupId, payload);
        }
    }

    static List<PostAffectorSaveData> ClonePosts(List<PostAffectorSaveData> source)
    {
        List<PostAffectorSaveData> copy = new List<PostAffectorSaveData>();
        foreach (PostAffectorSaveData post in source)
        {
            if (post == null) continue;
            copy.Add(JsonUtility.FromJson<PostAffectorSaveData>(JsonUtility.ToJson(post)));
        }
        return copy;
    }

    // Put every anchor back where it was. A preview the user cannot walk out of is not a preview.
    public static void Revert(RemapPreviewSnapshot snapshot)
    {
        if (snapshot == null) return;

        for (int i = 0; i < snapshot.cards.Count; i++)
        {
            HairCard card = snapshot.cards[i];
            if (card == null) continue;
            // Identity stays frozen at these same values, so nothing re-rolls on the way back
            // either.
            card.SetPlacementData(snapshot.cardPoints[i], snapshot.cardNormals[i], snapshot.cardDepths[i],
                snapshot.cardOffsetX[i], snapshot.cardOffsetY[i], snapshot.cardOffsetZ[i], snapshot.cardGroups[i]);
            HairCardSurfaceIsland island = card.GetComponent<HairCardSurfaceIsland>();
            if (island != null) Object.Destroy(island);
        }

        for (int i = 0; i < snapshot.guides.Count; i++)
        {
            GuideCurveManager.GuideCurve guide = snapshot.guides[i];
            if (guide == null) continue;
            guide.contact = snapshot.guideContacts[i];
            guide.normal = snapshot.guideNormals[i];
            guide.frame = snapshot.guideFrames[i];
            guide.nodesLocal.Clear();
            guide.nodesLocal.AddRange(snapshot.guideNodes[i]);
            guide.radius = snapshot.guideRadii[i];
            guide.falloff = snapshot.guideFalloffs[i];
        }

        GroupClumperManager clumperManager = Object.FindFirstObjectByType<GroupClumperManager>();
        for (int i = 0; i < snapshot.clumpers.Count; i++)
        {
            GroupClumperManager.GroupClumper clumper = snapshot.clumpers[i];
            if (clumper == null) continue;
            clumper.center = snapshot.clumperCentres[i];
            clumper.normal = snapshot.clumperNormals[i];
            clumper.radius = snapshot.clumperRadii[i];
            clumper.falloff = snapshot.clumperFalloffs[i];
            if (clumperManager != null) clumperManager.Invalidate(clumper);
        }

        PostAffectorManager postManager = Object.FindFirstObjectByType<PostAffectorManager>();
        if (postManager != null)
        {
            for (int i = 0; i < snapshot.postGroups.Count; i++)
                postManager.ImportGroup(snapshot.postGroups[i], snapshot.postPayloads[i]);
        }
    }
}
