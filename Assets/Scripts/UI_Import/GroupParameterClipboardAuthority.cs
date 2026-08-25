using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

// COPY PARAMS / PASTE PARAMS: one group's settings onto another.
//
// Everything a group owns travels except the modifiers. POSTs, clumpers and guides are anchored
// to points on the model, so copying them would put another group's hair-shaping in places that
// mean nothing on the target. What does travel is the shape block, the eleven profile curves,
// all thirteen variance channels, single or double sided, normal flip, and the UV settings.
//
// WHERE A GROUP'S SHAPE ACTUALLY LIVES
//
// Nowhere, is the short answer, and that shapes this whole file. There is no per-group slider
// block in the project format - the save file has ONE global block - so a group's shape exists
// only on its cards, with GroomRootStateAuthority keeping an opportunistic cache of it.
//
// Rather than re-derive that, both buttons select their own group first and then read or write
// through the panel. ModelViewer.SyncShapeSlidersToGroupRoot already knows how to recover a
// group's real settings - stored root, else the median of the group's own cards, else defaults -
// so selecting the source group and reading viewer.current* afterwards reuses that answer
// instead of writing a second, quietly different one. It also matches what a person would do
// anyway: click the group you are copying from.
public static class GroupParameterClipboardAuthority
{
    // Session-only: nothing here is written to a project file. It does survive opening another
    // project, deliberately - the clip holds plain numbers, not references to anything that could
    // go stale, and carrying a group's settings from one file into the next is useful rather than
    // surprising. What it must not survive is a domain reload, hence the reset below.
    private sealed class Clip
    {
        public GroomRootStateAuthority.RootState shape;
        public List<VarianceChannelSaveData> variances;
        public Dictionary<GroomShapeCurveChannel, List<GroomCurveKeySaveData>> curves;
        public bool singleSided;
        public bool normalFlipped;
        public bool predeterminedUVs;
        public int uvMinId;
        public int uvMaxId;
        public int uvSeed;
        public int sourceGroupId;
    }

    private static Clip clip;

    public static bool HasCopy
    {
        get { return clip != null; }
    }

    private static readonly GroomShapeCurveChannel[] Channels =
    {
        GroomShapeCurveChannel.Bend,
        GroomShapeCurveChannel.X,
        GroomShapeCurveChannel.Y,
        GroomShapeCurveChannel.Z,
        GroomShapeCurveChannel.CurlFrequency,
        GroomShapeCurveChannel.CurlDiameter,
        GroomShapeCurveChannel.SegmentDensity,
        GroomShapeCurveChannel.Width,
        GroomShapeCurveChannel.WaveAmplitude,
        GroomShapeCurveChannel.WaveFrequency,
        GroomShapeCurveChannel.WaveDirection,
    };

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetOnEnterPlayMode()
    {
        // A clipboard surviving "Disable Domain Reload" would offer to paste a group that no
        // longer exists, from a session that has ended.
        clip = null;
    }

    // ------------------------------------------------------------------------------- copy

    public static void Copy(int groupId)
    {
        ModelViewer viewer = Object.FindFirstObjectByType<ModelViewer>();
        if (viewer == null) return;

        // A selected POST swallows panel edits into its own delta, and - worse for a copy - the
        // curve registry holds the POST's private Bend/X/Y/Z curves while it is presented, so
        // Export would hand back the POST's shape as if it were the group's.
        LeaveModifierContext(viewer);
        SelectGroup(viewer, groupId);

        Clip fresh = new Clip();
        fresh.sourceGroupId = groupId;
        fresh.shape = ReadViewerRoot(viewer);

        GroomVarianceController variance = Object.FindFirstObjectByType<GroomVarianceController>();
        if (variance != null) fresh.variances = variance.ExportGroupSettings(groupId);

        fresh.curves = new Dictionary<GroomShapeCurveChannel, List<GroomCurveKeySaveData>>();
        foreach (GroomShapeCurveChannel channel in Channels)
        {
            fresh.curves[channel] = GroomShapeCurveRegistry.Export(groupId, channel);
        }

        fresh.singleSided = GroupSidednessAuthority.IsSingleSided(groupId);
        fresh.normalFlipped = GroupNormalFlipAuthority.IsFlipped(groupId);
        ReadGroupUV(groupId, fresh);

        clip = fresh;
        StatusToast.Show("Copied group " + groupId + " parameters. PASTE is now available.");
    }

    // ------------------------------------------------------------------------------ paste

    public static void Paste(int groupId)
    {
        if (clip == null)
        {
            StatusToast.Show("Nothing copied yet. Press COPY on a group first.");
            return;
        }

        ModelViewer viewer = Object.FindFirstObjectByType<ModelViewer>();
        if (viewer == null) return;

        LeaveModifierContext(viewer);
        SelectGroup(viewer, groupId);

        // The order below is not arbitrary; each step depends on the one before it.

        // 1. Curves first. They are mesh inputs only, so having them in place means the card
        //    sweep in step 3 rebuilds each card once with the final profiles instead of twice.
        foreach (GroomShapeCurveChannel channel in Channels)
        {
            List<GroomCurveKeySaveData> keys;
            if (!clip.curves.TryGetValue(channel, out keys)) continue;
            GroomShapeCurveRegistry.Import(groupId, channel, keys);
        }

        // 2. Sidedness and normal flip. The flip is picked up on the next mesh rebuild rather
        //    than applied directly, so setting it before the sweep gets it for free.
        GroupSidednessAuthority.SetSingleSided(groupId, clip.singleSided);
        GroupNormalFlipAuthority.SetFlipped(groupId, clip.normalFlipped);

        // 3. The shape block: panel, root cache, then the cards.
        WriteViewerRoot(viewer, clip.shape);
        WriteAdjustableUV(viewer, groupId, clip.shape);
        StoreRootState(groupId, clip.shape);
        ApplyShapeToCards(groupId, clip.shape);

        // 4. UV mode and range. In PREDETERMINED mode this rewrites every card's UVs
        //    canonically, so it has to have the last word over step 3.
        WriteGroupUV(groupId, clip);

        // 5. Variance last. It re-varies every card around the group's ROOT, which step 3 has
        //    only just written - run earlier, every card would be varied around the numbers the
        //    target group had before the paste.
        GroomVarianceController variance = Object.FindFirstObjectByType<GroomVarianceController>();
        if (variance != null && clip.variances != null) variance.ImportGroupSettings(groupId, clip.variances);

        GroomShapeCurveRegistry.RefreshGroup(groupId);
        GroomShapeCurveEditor editor = Object.FindFirstObjectByType<GroomShapeCurveEditor>();
        if (editor != null) editor.RefreshAll();

        viewer.SyncShapeSlidersToGroupRoot(groupId);

        StatusToast.Show("Pasted group " + clip.sourceGroupId + " parameters onto group " + groupId + ".");
    }

    // --------------------------------------------------------------------------- helpers

    // A POST or CLUMPER still being edited would eat everything written below, and a live brush
    // selection would make SetParameters lerp the write against each card's weight instead of
    // applying it.
    static void LeaveModifierContext(ModelViewer viewer)
    {
        // FIRST, before anything below touches it. ReleasePostSelection's last act is to clear
        // hasSelectionHotspot, and that is the flag this teardown tests to decide whether there
        // is a brush selection to tear down - run afterwards it always reads false and never
        // does anything.
        //
        // It matters because ReleasePostSelection clears only that one flag and leaves
        // isSelectionMode set, and ModelViewer refuses to place hair while THAT is set. A COPY
        // pressed with a brush selection live would otherwise switch card placement off for the
        // rest of the session with nothing on screen to explain it, and nothing self-heals:
        // HasLiveSelection returns early on hasSelectionHotspot before it can clean up.
        ClearSelectionHotspot(viewer);

        PostAffectorManager posts = Object.FindFirstObjectByType<PostAffectorManager>();
        if (posts != null) posts.ReleasePostSelection();

        GroupClumperManager clumpers = Object.FindFirstObjectByType<GroupClumperManager>();
        if (clumpers != null) clumpers.ClearSelection();

        GuideCurveManager guides = Object.FindFirstObjectByType<GuideCurveManager>();
        if (guides != null) guides.ClearSelection();

        // And only now can the curves be trusted. See EnsurePresentationReleased.
        PostShapeCurveBridge.EnsurePresentationReleased();
    }

    static void ClearSelectionHotspot(ModelViewer viewer)
    {
        if (viewer == null) return;

        // Only when there is something to tear down. Run unconditionally it would zero brush
        // weights nobody asked it to touch.
        FieldInfo flag = typeof(ModelViewer).GetField("hasSelectionHotspot",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (flag == null || !(flag.GetValue(viewer) is bool active) || !active) return;

        MethodInfo clear = typeof(ModelViewer).GetMethod("ClearSelectionHotspot",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (clear != null) clear.Invoke(viewer, null);
    }

    // Always succeeds - the fallback covers the only way it could not - so callers do not test
    // it. Kept as a separate step because getting the group current is a precondition for
    // everything either button does, and burying it in the caller reads as if it were optional.
    static void SelectGroup(ModelViewer viewer, int groupId)
    {
        MethodInfo select = typeof(ModelViewer).GetMethod("SelectGroup",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (select == null)
        {
            // SyncShapeSlidersToGroupRoot does not touch the four UV fields - SelectGroup writes
            // those itself from the group dictionaries - so the fallback has to do the same or
            // it would read the previously selected group's UVs.
            viewer.currentGroupId = groupId;
            viewer.currentUScale = GroupFloat(viewer, "groupUScales", groupId, 1f);
            viewer.currentVScale = GroupFloat(viewer, "groupVScales", groupId, 1f);
            viewer.currentUOffset = GroupFloat(viewer, "groupUOffsets", groupId, 0f);
            viewer.currentVOffset = GroupFloat(viewer, "groupVOffsets", groupId, 0f);
            viewer.SyncShapeSlidersToGroupRoot(groupId);
            return;
        }

        select.Invoke(viewer, new object[] { groupId });
    }

    static GroomRootStateAuthority.RootState ReadViewerRoot(ModelViewer viewer)
    {
        return new GroomRootStateAuthority.RootState
        {
            length = viewer.currentLength,
            width = viewer.currentWidth,
            segments = viewer.currentSegments,
            bend = viewer.currentBend,
            twist = viewer.currentTwist,
            depth = viewer.currentEmbedDepth,
            x = viewer.currentOffsetX,
            y = viewer.currentOffsetY,
            z = viewer.currentOffsetZ,
            uScale = viewer.currentUScale,
            vScale = viewer.currentVScale,
            uOffset = viewer.currentUOffset,
            vOffset = viewer.currentVOffset,
            curlFrequency = viewer.currentCurlFrequency,
            curlDiameter = viewer.currentCurlDiameter,
            waveAmplitude = viewer.currentWaveAmplitude,
            waveFrequency = viewer.currentWaveFrequency,
            waveDirection = viewer.currentWaveDirection,
            arch = viewer.currentArch
        };
    }

    static void WriteViewerRoot(ModelViewer viewer, GroomRootStateAuthority.RootState s)
    {
        viewer.currentLength = s.length;
        viewer.currentWidth = s.width;
        viewer.currentSegments = s.segments;
        viewer.currentBend = s.bend;
        viewer.currentTwist = s.twist;
        viewer.currentEmbedDepth = s.depth;
        viewer.currentOffsetX = s.x;
        viewer.currentOffsetY = s.y;
        viewer.currentOffsetZ = s.z;
        viewer.currentUScale = s.uScale;
        viewer.currentVScale = s.vScale;
        viewer.currentUOffset = s.uOffset;
        viewer.currentVOffset = s.vOffset;
        viewer.currentCurlFrequency = s.curlFrequency;
        viewer.currentCurlDiameter = s.curlDiameter;
        viewer.currentWaveAmplitude = s.waveAmplitude;
        viewer.currentWaveFrequency = s.waveFrequency;
        viewer.currentWaveDirection = s.waveDirection;
        viewer.currentArch = s.arch;
    }

    // Without this the paste is undone on the very next frame. GroomRootStateAuthority runs at
    // order -1100 and, for a group that carries a modifier, writes its STORED root back over
    // whatever is on the panel. Storing the pasted block first makes that restore a no-op.
    static void StoreRootState(int groupId, GroomRootStateAuthority.RootState state)
    {
        GroomRootStateAuthority authority = Object.FindFirstObjectByType<GroomRootStateAuthority>();
        if (authority == null) return;

        FieldInfo field = typeof(GroomRootStateAuthority).GetField("roots",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(authority) is Dictionary<int, GroomRootStateAuthority.RootState> roots)
            roots[groupId] = state;
    }

    // The four adjustable-UV values live in ModelViewer dictionaries as well as on the cards,
    // and GroupPredeterminedUVController reads them back out when a group leaves PREDETERMINED
    // mode. Left stale, switching the target group back to ADJUSTABLE would restore the UVs it
    // had before the paste.
    static void WriteAdjustableUV(ModelViewer viewer, int groupId, GroomRootStateAuthority.RootState s)
    {
        SetGroupFloat(viewer, "groupUScales", groupId, s.uScale);
        SetGroupFloat(viewer, "groupVScales", groupId, s.vScale);
        SetGroupFloat(viewer, "groupUOffsets", groupId, s.uOffset);
        SetGroupFloat(viewer, "groupVOffsets", groupId, s.vOffset);
    }

    static float GroupFloat(ModelViewer viewer, string fieldName, int groupId, float fallback)
    {
        FieldInfo field = typeof(ModelViewer).GetField(fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(viewer) is Dictionary<int, float> map && map.TryGetValue(groupId, out float value))
            return value;
        return fallback;
    }

    static void SetGroupFloat(ModelViewer viewer, string fieldName, int groupId, float value)
    {
        FieldInfo field = typeof(ModelViewer).GetField(fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(viewer) is Dictionary<int, float> map) map[groupId] = value;
    }

    // Written straight onto the cards rather than through ModelViewer's slider handlers, for
    // two reasons: those handlers apply their value as a DELTA when the panel is in REL mode,
    // and they divert to the single active card whenever a brush selection is live. Neither is
    // wanted here, and both are private state this component would otherwise have to reach into
    // and put back. GroomSessionResetCoordinator.ResetCurrentGroup writes the group the same way.
    static void ApplyShapeToCards(int groupId, GroomRootStateAuthority.RootState s)
    {
        foreach (HairCard card in Object.FindObjectsByType<HairCard>(FindObjectsSortMode.None))
        {
            if (card == null || card.groupId != groupId) continue;

            // SetParameters interpolates against the card's base state while its brush weight
            // is above zero, so a card still under the brush would take a fraction of the paste.
            // Not restored afterwards, deliberately: LeaveModifierContext has already torn the
            // brush selection down, so zero is where these are meant to end up. This is the
            // backstop for a weight left behind by something that did not go through it.
            card.SetSelectionWeight(0f);
            card.SetParameters(
                Mathf.Max(.0001f, s.length), Mathf.Max(.0005f, s.width),
                Mathf.Clamp(s.segments, 4, 60), s.bend, s.twist,
                s.x, s.y, s.z, s.depth, 1f,
                s.uScale, s.vScale, s.uOffset, s.vOffset,
                s.curlFrequency, s.curlDiameter,
                s.waveAmplitude, s.waveFrequency, s.waveDirection, s.arch);
        }
    }

    // ------------------------------------------------------------------------------ UV mode

    static void ReadGroupUV(int groupId, Clip into)
    {
        GroupPredeterminedUVController controller = Object.FindFirstObjectByType<GroupPredeterminedUVController>();
        if (controller == null) return;

        // PopulateGroupSave is the controller's own public reader and fills all four at once.
        // The same throwaway-payload trick GroupRootUVModeAuthority already uses to ask this.
        GroupSaveData probe = new GroupSaveData { groupId = groupId };
        controller.PopulateGroupSave(probe);

        into.predeterminedUVs = probe.usePredeterminedUVs;
        into.uvMinId = probe.uvRectMinId;
        into.uvMaxId = probe.uvRectMaxId;
        into.uvSeed = probe.uvRectSeed;
    }

    static void WriteGroupUV(int groupId, Clip from)
    {
        GroupPredeterminedUVController controller = Object.FindFirstObjectByType<GroupPredeterminedUVController>();
        if (controller == null) return;

        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;

        GroupSaveData current = new GroupSaveData { groupId = groupId };
        controller.PopulateGroupSave(current);

        // Mode first, range second. ToggleMode's turn-on branch has a "the user never chose a
        // range" heuristic that replaces 1..1 with the full set of rectangles whenever more than
        // one exists - so a group deliberately pinned to rectangle 1 would silently arrive using
        // all of them, but only when the target happened to be in ADJUSTABLE mode beforehand.
        // Writing the range afterwards makes the paste land the same way either way.
        //
        // ToggleMode is a toggle, not a setter, so it is only called when the two disagree.
        if (current.usePredeterminedUVs != from.predeterminedUVs)
        {
            MethodInfo toggle = typeof(GroupPredeterminedUVController).GetMethod("ToggleMode", flags);
            if (toggle != null) toggle.Invoke(controller, new object[] { groupId });
        }

        // Min and max, TWICE. SetRangeValue normalises after every single write, and part of
        // normalising is swapping the pair when min ends up above max - which it does on the
        // first write whenever the pasted min is above the target's current max. One more round
        // settles it from either direction: 1..1 taking 3..6 goes 1..3, then 3..6.
        MethodInfo setRange = typeof(GroupPredeterminedUVController).GetMethod("SetRangeValue", flags);
        if (setRange != null)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                setRange.Invoke(controller, new object[] { groupId, true, from.uvMinId.ToString() });
                setRange.Invoke(controller, new object[] { groupId, false, from.uvMaxId.ToString() });
            }
        }

        MethodInfo setSeed = typeof(GroupPredeterminedUVController).GetMethod("SetSeed", flags);
        if (setSeed != null) setSeed.Invoke(controller, new object[] { groupId, from.uvSeed.ToString() });

        // The controller skips re-applying a card whose assignment signature is unchanged, so
        // without clearing that the cards keep the rectangles they already had.
        MethodInfo clearApplied = typeof(GroupPredeterminedUVController).GetMethod("ClearAppliedForGroup", flags);
        if (clearApplied != null) clearApplied.Invoke(controller, new object[] { groupId });

        MethodInfo forceApply = typeof(GroupPredeterminedUVController).GetMethod("ForceApplyGroup", flags);
        if (forceApply != null) forceApply.Invoke(controller, new object[] { groupId });
    }
}
