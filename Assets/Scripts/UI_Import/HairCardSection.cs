using UnityEngine;

// The shape of a hair card's CROSS SECTION, and the one place that knows how many points it has.
//
// TENT (the original, and what every project made before this loads as) is three points per row:
//     left edge at y=0, centre raised to +ridge, right edge at y=0.
// An open surface with no underside, so a card seen from behind shows a back face - which is why
// double-sided rendering is the default and why N+ / N- exists at all: an open surface lit from
// the wrong side looks wrong, and flipping it is the only repair available.
//
// DIAMOND closes that section with a fourth point at -ridge. Every face then has an outward
// normal, so the lighting is right from any angle with no flipping, backface culling drops the
// two faces pointing away, and single-sided is simply correct rather than a compromise.
//
//     TENT                DIAMOND
//        T                   T
//       / \                 / \
//      L---R               L   R
//                           \ /
//                            B
//
// WHAT IT COSTS. Be honest about this: the diamond is not an optimisation. Double-sided is a cull
// state on the shader, not extra geometry, so it is nearly free - and the diamond pays twice the
// triangles (eight per segment against four) to retire it. Culling discards the far pair after
// vertex processing, so the fragment cost lands about where it already was. What the diamond buys
// is correct normals everywhere, for a doubled vertex cost and a doubled polygon count. That is a
// quality argument, not a speed one.
//
// WHY THIS FILE EXISTS AT ALL. Four separate places built the three-column section, each with its
// own copy of the topology written out longhand: HairCard.GenerateMesh,
// ThreeColumnClumperMeshAuthority.BuildCleanMesh, GuideDeformation.Apply and
// ModifierNeutralizeBeforeDeleteAuthority.WriteCleanThreeColumnMesh. A second profile could not be
// added to four hand-written copies without them drifting, and they have drifted before - the
// clumper reconstruction silently lost curl and ignored the density profile, twice, because it
// held its own copy of a loop. They now share this.
//
// COLUMN ORDER IS PART OF THE CONTRACT: 0 left, 1 top, 2 right, and 3 bottom when it exists. The
// first three keep the meaning they have always had, so every piece of code that reads index+0,
// +1 or +2 still means what it meant - and the fourth is only ever seen by code that loops over
// Columns.
public static class HairCardSection
{
    public enum Profile
    {
        Tent = 0,
        Diamond = 1
    }

    // Global, not per group. Per group was considered and rejected: the clumper and guide
    // reconstructions rebuild whole groups at a time and would have to carry the profile through
    // every one of them, and a groom with both profiles in it has no consistent answer for
    // whether double-sided should be on.
    private static Profile current = Profile.Tent;

    // One error, not one per triangle per row per card per frame. See AddTriangle.
    private static bool warnedUndersized;

    // Statics survive "Enter Play Mode -> Disable Domain Reload", and a profile left on Diamond
    // when play stopped would rebuild the next session's cards before anything loaded a project.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        current = Profile.Tent;
        warnedUndersized = false;
    }

    public static Profile Current
    {
        get { return current; }
    }

    public static bool IsDiamond
    {
        get { return current == Profile.Diamond; }
    }

    public static int Columns
    {
        get
        {
            if (current == Profile.Diamond) return 4;
            return 3;
        }
    }

    // Four edges round the loop instead of two across the tent, and six indices to a quad.
    public static int IndicesPerSegment
    {
        get { return EdgeCount * 6; }
    }

    private static int EdgeCount
    {
        get
        {
            if (current == Profile.Diamond) return 4;
            return 2;
        }
    }

    // A closed section whose two centre points meet is not a flat card - it is two coincident
    // surfaces, and they z-fight. ARCH at 0 asks for exactly that, so the diamond keeps a floor
    // proportional to the card's own width rather than an absolute distance, which would read as
    // a thick card on a narrow one and no card at all on a wide one.
    private const float MinimumRidgeFraction = .06f;

    // Applied to the magnitude, not the value: N- negates the ridge to turn a convex tent
    // concave, and a symmetric diamond does not care which way that sign points.
    public static float ResolveRidge(float ridge, float span)
    {
        if (current != Profile.Diamond) return ridge;

        float floor = Mathf.Abs(span) * MinimumRidgeFraction;
        return Mathf.Max(Mathf.Abs(ridge), floor);
    }

    // One row of the section, written straight into a mesh's arrays.
    //
    // Everything the two callers did by hand is here: build the points in the section's own
    // space, bank them into the curl, add the curl and wave displacement, then place them on the
    // spine with the path-following frame.
    public static void WriteRow(
        Vector3[] vertices, Vector2[] uvs, int index,
        Vector3 sectionOrigin, Quaternion bank, Vector3 offset,
        Vector3 spinePoint, Quaternion sectionFrame,
        float span, float ridge,
        float uLeft, float uRight, float v)
    {
        if (vertices == null) return;

        float resolvedRidge = ResolveRidge(ridge, span);
        float uCentre = (uLeft + uRight) * .5f;

        Place(vertices, index + 0, sectionOrigin, bank, offset, spinePoint, sectionFrame, new Vector3(-span, 0f, 0f));
        Place(vertices, index + 1, sectionOrigin, bank, offset, spinePoint, sectionFrame, new Vector3(0f, resolvedRidge, 0f));
        Place(vertices, index + 2, sectionOrigin, bank, offset, spinePoint, sectionFrame, new Vector3(span, 0f, 0f));

        if (uvs != null)
        {
            uvs[index + 0] = new Vector2(uLeft, v);
            uvs[index + 1] = new Vector2(uCentre, v);
            uvs[index + 2] = new Vector2(uRight, v);
        }

        if (current != Profile.Diamond) return;

        Place(vertices, index + 3, sectionOrigin, bank, offset, spinePoint, sectionFrame, new Vector3(0f, -resolvedRidge, 0f));

        // The back pair mirrors the front pair's U rather than continuing around the loop. Run
        // U from 0 to 1 all the way round and each visible side shows half a hair strip; mirrored,
        // whichever pair of faces is turned toward the camera shows a whole one - which is what
        // double-sided gave you, and the reason the change is invisible in the texture.
        if (uvs != null) uvs[index + 3] = new Vector2(uCentre, v);
    }

    private static void Place(
        Vector3[] vertices, int index,
        Vector3 sectionOrigin, Quaternion bank, Vector3 offset,
        Vector3 spinePoint, Quaternion sectionFrame,
        Vector3 local)
    {
        Vector3 point = sectionOrigin + bank * local + offset;
        vertices[index] = spinePoint + sectionFrame * (point - sectionOrigin);
    }

    // The strip between every pair of rows, one quad per edge of the section.
    //
    // The tent's two edges produce exactly the four triangles, in exactly the order, that the
    // hand-written version produced - so a tent card's mesh is unchanged, index for index.
    // `vertices` is what lets each quad be split the better of its two ways. Optional: a caller
    // that has no positions to hand gets the alternating fallback, which is still symmetric and
    // still correct, just blind to the card's actual shape.
    public static void BuildTriangles(int segments, bool flipWinding, int[] triangles, Vector3[] vertices = null)
    {
        if (triangles == null) return;

        int columns = Columns;
        int edges = EdgeCount;
        int triIndex = 0;

        for (int i = 0; i < segments; i++)
        {
            int row = i * columns;
            int next = row + columns;

            for (int edge = 0; edge < edges; edge++)
            {
                int a = edge;

                // The last edge of a diamond closes back onto column 0. The tent has no such
                // edge, which is the whole difference between an open surface and a closed one.
                int b = edge + 1;
                if (b >= columns) b = 0;

                if (UseNearDiagonal(vertices, row + a, next + a, next + b, row + b, edge))
                {
                    AddTriangle(triangles, ref triIndex, row + a, next + a, row + b, flipWinding);
                    AddTriangle(triangles, ref triIndex, row + b, next + a, next + b, flipWinding);
                }
                else
                {
                    // The other diagonal of the same quad, wound the same way round it, so the
                    // face orientation - and therefore which side the surface is lit from - is
                    // untouched. Only which two triangles span the quad changes.
                    AddTriangle(triangles, ref triIndex, row + a, next + a, next + b, flipWinding);
                    AddTriangle(triangles, ref triIndex, row + a, next + b, row + b, flipWinding);
                }
            }
        }
    }

    // Which of a quad's two diagonals to split it on. True keeps the historical one, B to D.
    //
    // WHY THIS IS NOT A FIXED RULE ANY MORE. Every quad used to take the same diagonal, and that
    // makes the MESH asymmetric even when the CARD is perfectly symmetric: the tent's left and
    // right panels are mirror images, but with one shared rule the triangles fanning into the
    // ridge vertex are not, so RecalculateNormals hands that vertex a normal tilted off centre.
    // Measured against the true surface on a straight symmetric card, the ridge normal came out
    // 13.9 degrees off under TENT and 84.5 under DIAMOND - and the same way on every card in the
    // groom, so it does not read as noise. It reads as the whole head lit slightly wrong from one
    // side. A bent, tapered diamond reached 155 degrees.
    //
    // A GLOBAL FLIP CANNOT FIX THAT. Reversing every quad together leaves the two halves still
    // agreeing with each other and leans the error the other way; it measures the same.
    //
    // SHORTEST DIAGONAL is the fix, and it is self-correcting rather than a rule to maintain. On
    // a symmetric card the two panels' diagonals are mirror-equal lengths, so the shorter one on
    // the left is the mirrored one on the right and symmetry falls out for free; on a twisted or
    // waved card it picks the split that folds least, which the fixed rule could not do at all.
    // Measured: worst quad fold on a heavily twisted card 13.9 degrees fixed, 12.9 shortest.
    //
    // THE TIE IS THE INTERESTING CASE, and getting it wrong puts the bug straight back. On a
    // straight card the two diagonals are EXACTLY equal, so a bare comparison picks the same side
    // every time and degenerates to the old fixed rule - on the commonest card shape there is.
    // Alternating by edge index breaks the tie the symmetric way: under the mirror the section
    // maps left to right and top to top, so each edge maps onto its neighbour REVERSED, and a
    // reversed quad wants the other diagonal. With the tie-break in, every symmetric card
    // measures 0.00 degrees of asymmetry, straight ones included.
    private static bool UseNearDiagonal(Vector3[] vertices, int a, int b, int c, int d, int edge)
    {
        bool alternate = (edge & 1) == 0;
        if (vertices == null) return alternate;
        if (a >= vertices.Length || b >= vertices.Length || c >= vertices.Length || d >= vertices.Length) return alternate;

        float bd = (vertices[b] - vertices[d]).sqrMagnitude;
        float ac = (vertices[a] - vertices[c]).sqrMagnitude;

        // Relative, because a hair card is millimetres across and these are squared lengths -
        // an absolute epsilon would call every quad on a fine card a tie, or none of them.
        float scale = Mathf.Max(bd, ac);
        if (Mathf.Abs(bd - ac) <= scale * .000001f) return alternate;

        return bd < ac;
    }

    private static void AddTriangle(int[] triangles, ref int index, int a, int b, int c, bool flipWinding)
    {
        // A builder that sized its array with the old hardcoded `segments * 12` instead of
        // IndicesPerSegment would fill half the strip and leave the tail zeroed - a card whose
        // geometry simply stops halfway up, with every remaining triangle a (0,0,0) degenerate.
        // Silently returning would hide exactly the mistake this refactor exists to prevent, so
        // it says so once and then stops writing rather than throwing on every row of every
        // card for the rest of the session.
        if (index + 2 >= triangles.Length)
        {
            if (!warnedUndersized)
            {
                warnedUndersized = true;
                Debug.LogError("HairCardSection: triangle array is too small for this profile. "
                    + "Size it with HairCardSection.IndicesPerSegment, not a hardcoded 12.");
            }
            return;
        }

        if (flipWinding)
        {
            triangles[index++] = a;
            triangles[index++] = c;
            triangles[index++] = b;
            return;
        }

        triangles[index++] = a;
        triangles[index++] = b;
        triangles[index++] = c;
    }

    // ---- switching -------------------------------------------------------------------------

    // Set by the panel toggle and by a project load. Rebuilds every card, because the mesh is a
    // pure function of the parameters and this changes the function.
    public static void SetProfile(Profile profile, bool rebuild)
    {
        if (current == profile) return;
        current = profile;

        // The cull state has to move with the geometry. GroupSidednessAuthority's own scan
        // would get there within a tenth of a second, which on a groom of any size is long
        // enough to see the wrong one.
        GroupSidednessAuthority.ReapplyAll();

        if (!rebuild) return;
        RebuildAllCards();
    }

    // ---- persistence -----------------------------------------------------------------------
    //
    // The profile belongs to the GROOM, not to the machine: a card built as a diamond and a
    // card built as a tent are different geometry, and a project reopened on another machine
    // has to come back the shape it was saved as. That is why this is in the project file and
    // not in the settings ini beside MAYA-NAV and GUIDES ON TOP.

    // DELIBERATELY NOT reset by GroomSessionResetCoordinator when a new model is imported,
    // unlike the group maps beside it. Import a head while working in DIAMOND and the new
    // groom starts in DIAMOND. The setting is saved with the project, so that new groom's
    // first save carries a profile the user did not pick for THAT file - but they did pick it,
    // in this session, and the panel button says CARD: DIAMOND the whole time. Snapping back to
    // TENT on every import would be the more surprising of the two.
    public static void Capture(HairProjectSaveData data)
    {
        if (data == null) return;
        data.cardSectionProfile = (int)current;
    }

    // Applied immediately, not deferred like the other restore bridges.
    //
    // Those defer because they act on cards, and the cards of a loaded project do not exist on
    // the frame load returns. This is the opposite case: it decides what those cards are BUILT
    // as. Two frames later would mean the whole groom generated as tents and then rebuilt -
    // a visible flip on load, and a full mesh rebuild of every card for nothing.
    //
    // A project written before this existed has no field and decodes to 0, which is Tent.
    //
    // TOUCHES NOTHING IN THE SCENE, deliberately. This runs inside OnAfterDeserialize, where
    // every other line only parks a static flag, and UndoHistoryAuthority parses a snapshot on
    // its way to THROWING THE WHOLE SCENE AWAY - so a rebuild here is a full regeneration of
    // every card in the groom, discarded one frame later. Nothing needs it: the profile is
    // part of HairCard's mesh-input hash, so any card that outlives the load notices on its own
    // next re-assertion, and GroupSidednessAuthority's sweep re-applies the cull within a tenth
    // of a second. It also keeps a serialization callback free of FindObjectsByType, which is
    // main-thread only.
    public static void Restore(HairProjectSaveData data)
    {
        if (data == null) return;

        Profile profile = Profile.Tent;
        if (data.cardSectionProfile == (int)Profile.Diamond) profile = Profile.Diamond;

        current = profile;
    }

    public static void RebuildAllCards()
    {
        foreach (HairCard card in Object.FindObjectsByType<HairCard>(FindObjectsSortMode.None))
        {
            if (card == null) continue;

            // GenerateMesh directly, not the dirty-checked wrapper. The hash does cover the
            // profile, so the guarded path would rebuild too - but it would also skip any card
            // a modifier currently owns, and this is the one moment where every card in the
            // scene genuinely has to be rewritten before it is next drawn.
            card.GenerateMesh();
        }
    }
}
