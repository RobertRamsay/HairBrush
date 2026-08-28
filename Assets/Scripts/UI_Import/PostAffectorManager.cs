using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// Ctrl+Click creates a persistent localized post-affector for the active group.
// Evaluation is deterministic: canonical/authored card state -> POST affectors -> rendered card.
// Evaluated values are never fed back into canonical state.
[DefaultExecutionOrder(3300)]
public class PostAffectorManager : MonoBehaviour
{
    [Serializable]
    public class PostAffector
    {
        public int id;
        public int groupId;
        public Vector3 center;
        public Vector3 normal;
        // Only reached by an affector built outside CreateAffector; the creation path
        // overwrites both. Kept in step with the real defaults so the two can never disagree.
        public float radius = PostGroupLifetimeAuthority.DefaultPostRadius;
        public float falloff = PostGroupLifetimeAuthority.DefaultPostFalloff;
        [Range(0f, 1f)] public float weight = 1f;
        public ControlState baseline;
        public ControlState delta;
    }

    [Serializable]
    public struct ControlState
    {
        public float length, width, bend, twist, depth;
        public float segments;
        public float x, y, z;
        public float uScale, vScale, uOffset, vOffset;
        public float curlFrequency, curlDiameter;
        public float waveAmplitude, waveFrequency, waveDirection;
        public float arch;
    }

    private class CardState
    {
        public ControlState baseState;
        public ControlState lastFinal;
        public bool hasFinal;
    }

    private readonly Dictionary<int, List<PostAffector>> groups = new();
    private readonly Dictionary<HairCard, CardState> cardStates = new();
    private readonly Dictionary<int, bool> predeterminedUVByGroup = new();

    private ModelViewer viewer;
    private GroupPredeterminedUVController uvRouting;
    private FieldInfo hasSelectionField;
    private FieldInfo hitPointField;
    private FieldInfo hitNormalField;
    private FieldInfo strengthRowField;
    private int nextId = 1;
    private int activeId = -1;
    private int activeGroup = -1;

    // The right-hand groom panel is SHARED property. POST is only entitled to absorb a change
    // the user made while this POST owned that panel. These fields remember exactly what
    // PostAffectorManager last handed to, or accepted from, the panel, so a write by any OTHER
    // authority is recognisable as foreign instead of being read straight back in as an edit.
    private ControlState lastPanelControls = new ControlState();
    private bool hasPanelControls = false;
    private GroomRootStateAuthority rootAuthority = null;
    private GroupClumperManager clumperManager = null;
    private float nextUIScan;
    private int lastCreatedFrame = -1;
    private int predeterminedUVCacheFrame = -1;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<PostAffectorManager>() != null) return;
        GameObject go = new GameObject("PostAffectorManager");
        DontDestroyOnLoad(go);
        go.AddComponent<PostAffectorManager>();
    }

    // The live manager, so ModelViewer's group-root edit path can reach it without a scene scan
    // on every slider tick. Initialised to null here and cleared in OnDestroy.
    private static PostAffectorManager instance = null;

    public static PostAffectorManager Instance
    {
        get { return instance; }
    }

    void Awake()
    {
        instance = this;
    }

    void OnDestroy()
    {
        if (instance == this) instance = null;
    }

    // Called for every card a GROUP ROOT slider is about to write, immediately before the write.
    //
    // ModelViewer's group edit builds each card's new state out of the card's RENDERED fields -
    // either as a pass-through ("keep the width it has") or as a relative step ("bend + delta") -
    // and SetParameters promotes whatever it is handed straight into canonical. For a card inside
    // a POST the rendered fields are base + that POST's contribution. So a root edit made with a
    // POST live would bake the POST's contribution into the base, and ApplyAll would then add the
    // same contribution again on top: the POST doubling on every slider tick, on EVERY channel
    // rather than only the one being dragged, compounding for as long as the drag lasts.
    //
    // Re-asserting canonical onto the rendered fields first makes the edit read the base - which
    // is what the panel is showing and what the user means to move. ApplyAll puts the POST back
    // on top in the same frame's LateUpdate, so nothing is lost and nothing is counted twice.
    //
    // This is what makes the group root editable with POSTs in place. Before it, the root had to
    // be locked (ModifierCoreLock) because there was no way to edit it that did not corrupt the
    // POSTs sitting on it.
    public void PrepareCardForRootEdit(HairCard card)
    {
        if (card == null) return;

        // While a POST is being AUTHORED the panel belongs to that POST, not to the root: the
        // edit is meant to become its delta, the preview writes are meant to show base + delta,
        // and UpdateCanonicalBases restores canonical underneath them every frame. Rebasing here
        // would fight all three. Nothing about POST authoring changes.
        if (GetActive() != null && HasSelection()) return;

        if (!groups.TryGetValue(card.groupId, out List<PostAffector> list)) return;
        if (list == null || list.Count == 0) return;

        // Rebased for EVERY card in a group that has POSTs, with no test on what the POSTs
        // currently hold.
        //
        // The obvious optimisation - skip when this card's summed POST delta is zero - is
        // WRONG, and wrong in the exact way this method exists to prevent. A POST's delta is
        // not the only thing riding on top of canonical: PostVarianceAffectorBridge runs at
        // 3500, after ApplyAll, and adds a rendered-only scatter layer driven by the POST's own
        // VAR amounts, which AbsorbPanelEdit never writes into delta. A POST with zero delta and
        // a VAR amount is an ordinary thing to have - author one, drag VAR, go back to the group
        // - and on that POST the delta test skips the rebase, the group edit reads the scatter,
        // SetParameters promotes it into canonical, and the bridge adds it again. It grows by one
        // scatter per drag frame and is written to disk by CanonicalizeForSave.
        //
        // So the test is "does this group have POSTs", which cannot miss a layer. It costs
        // nothing on the cards no POST reaches: their rendered state already equals canonical,
        // so this writes the values that are already there, and with regenerateMesh false there
        // is no rebuild behind it either.
        card.ApplyEvaluatedState(ToGroomState(ReadCanonical(card)), false);
    }

    void Update()
    {
        EnsureViewer();
        if (viewer == null) return;

        DetectGroupRootSelection();
        DetectCtrlClick();
        MaintainActiveAuthoring();

        if (Time.unscaledTime >= nextUIScan)
        {
            nextUIScan = Time.unscaledTime + .12f;
            EnsureRowsAndOrder();
            RenameLegacyStrengthToWeight();
        }
    }

    void LateUpdate()
    {
        EnsureViewer();
        if (viewer == null) return;
        UpdateCanonicalBases();
        ApplyAll();
    }

    void EnsureViewer()
    {
        if (viewer != null) return;
        viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer == null) return;
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Type t = typeof(ModelViewer);
        hasSelectionField = t.GetField("hasSelectionHotspot", flags);
        hitPointField = t.GetField("selectionHitPoint", flags);
        hitNormalField = t.GetField("selectionHitNormal", flags);
        strengthRowField = t.GetField("strengthRowGO", flags);
    }

    void DetectGroupRootSelection()
    {
        if (activeId < 0 || EventSystem.current == null) return;
        GameObject selected = EventSystem.current.currentSelectedGameObject;
        if (selected == null || selected.name != "LabelButton") return;
        Transform item = selected.transform.parent;
        if (item == null || !item.name.StartsWith("GroupItem_")) return;

        activeId = -1;
        activeGroup = -1;
        SetField(hasSelectionField, false);
    }

    void DetectCtrlClick()
    {
        if (Mouse.current == null || Keyboard.current == null) return;
        if (!Keyboard.current.ctrlKey.isPressed || !Mouse.current.leftButton.wasPressedThisFrame) return;

        // CTRL+SHIFT+LMB is the group pick, not POST creation. Nothing else here would have
        // stopped it: this authority sweeps the scene on its own and never asks ModelViewer
        // whether the click was already claimed, so without this test every group pick made
        // while a selection was live would leave a POST behind it on the way past.
        if (Keyboard.current.shiftKey.isPressed) return;

        // ALT is reserved for the camera, and CTRL+ALT+LMB is a hand shape Maya users make
        // constantly - it is a camera gesture in Maya itself. With a selection live, which is the
        // state you are in immediately after any CTRL+click, tumbling with CTRL still resting
        // under the hand would plant a POST at the cursor and make it active as the view swung.
        if (MayaNavigationAuthority.AltReserved) return;

        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;
        if (!HasSelection()) return;
        if (lastCreatedFrame == Time.frameCount) return;
        lastCreatedFrame = Time.frameCount;
        CreateAffector(viewer.currentGroupId, GetVector(hitPointField), GetVector(hitNormalField));
    }

    void CreateAffector(int groupId, Vector3 center, Vector3 normal)
    {
        // Same hook SelectAffector has. Without it, ctrl+clicking while a GUIDE is selected
        // leaves both live: the guide panel keeps the groom panel hidden and keeps advertising
        // "SPACE + CLICK moves this guide" while the new POST owns that gesture. The clumper
        // never latches this because ClumperPostOwnershipAuthority re-clears every frame; guide
        // selection is one-shot, so it has to be closed at every entry point instead.
        GuideCurveManager createGuides = FindFirstObjectByType<GuideCurveManager>();
        if (createGuides != null) createGuides.ClearSelection();

        if (!groups.TryGetValue(groupId, out List<PostAffector> list))
        {
            list = new List<PostAffector>();
            groups[groupId] = list;
        }

        PostAffector a = new PostAffector
        {
            id = nextId++,
            groupId = groupId,
            center = center,
            normal = normal.sqrMagnitude > .000001f ? normal.normalized : Vector3.up,
            radius = Mathf.Clamp(viewer.brushRadius, .001f, .25f),
            falloff = Mathf.Clamp(viewer.brushFalloffDistance, 0f, .25f),
            weight = 1f,
            baseline = ReadControls(),
            delta = new ControlState()
        };
        list.Add(a);
        activeId = a.id;
        activeGroup = groupId;
        viewer.selectionStrength = 1f;
        lastPanelControls = a.baseline;
        hasPanelControls = true;

        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None).Where(c => c.groupId == groupId))
        {
            if (!cardStates.ContainsKey(card))
            {
                ControlState canonical = ReadCanonical(card);
                cardStates[card] = new CardState { baseState = canonical, lastFinal = canonical, hasFinal = false };
            }
        }

        RebuildGroupRows(groupId);
    }

    // ModelViewer.hasSelectionHotspot and PostAffectorManager.activeId are two halves of ONE
    // selection. Every teardown path in the project clears them together EXCEPT this method,
    // which used to clear activeId while leaving the hotspot latched true. That stranded pair
    // is unrecoverable: DetectGroupRootSelection returns early on activeId < 0, and all three
    // exit guards (PostSelectionExitAuthority, PostRootContextRestore, PostDeleteExitGuard)
    // latch on the activeId >= 0 -> < 0 transition, which has already happened. Nothing is
    // left that can clear the hotspot.
    //
    // The result is exactly the reported symptom: ModifierCoreLock unlocks the groom sliders
    // because it reads hasSelectionHotspot as "editing a POST", so the handles drag and the
    // labels update - but there is no active affector for the drag to become a delta on, so
    // ApplyAll re-evaluates the identical result every frame and the geometry never moves.
    //
    // ClumperPostOwnershipAuthority (order 5190) drives activeId to -1 every frame a clumper
    // is selected, which is what makes the clumper the trigger for this.
    private int orphanHotspotFrames = 0;

    void MaintainActiveAuthoring()
    {
        PostAffector active = GetActive();
        if (active == null)
        {
            // Do not react instantly: on a Ctrl+Click frame ModelViewer sets the hotspot at
            // execution order 0 and DetectCtrlClick creates the affector later in this same
            // Update, so a one-frame hotspot-without-affector window is legitimate. Only a
            // state that survives several frames is a genuine orphan.
            if (HasSelection())
            {
                orphanHotspotFrames++;
                if (orphanHotspotFrames >= 3)
                {
                    orphanHotspotFrames = 0;
                    ReleasePostSelection();
                }
            }
            else
            {
                orphanHotspotFrames = 0;
            }
            return;
        }

        orphanHotspotFrames = 0;

        if (!HasSelection() || viewer.currentGroupId != active.groupId)
        {
            ReleasePostSelection();
            return;
        }

        // CLUMPER and POST are mutually exclusive edit contexts. ClumperPostOwnershipAuthority
        // enforces that at execution order 5190 - AFTER this Update at 3300. That one-frame gap
        // was fatal. On the frame a clumper row is clicked, POST is still "active" here, the
        // groom panel has already been repointed at the group ROOT, and the delta line at the
        // bottom of this method converted that root reading into this POST's delta - destroying
        // the edit permanently, because delta is the only record of it. Release the context HERE,
        // before a single control value is read, so the gap does not exist.
        if (IsClumperSelectedForGroup(active.groupId))
        {
            ReleasePostSelection();
            return;
        }

        active.center = GetVector(hitPointField);
        active.normal = GetVector(hitNormalField);
        active.radius = Mathf.Clamp(viewer.brushRadius, .001f, .25f);
        active.falloff = Mathf.Clamp(viewer.brushFalloffDistance, 0f, .25f);

        if (!Mathf.Approximately(viewer.selectionStrength, active.weight))
        {
            active.weight = Mathf.Clamp01(viewer.selectionStrength);
            RebuildGroupRows(active.groupId);
        }

        AbsorbPanelEdit(active);
    }

    // "What does the panel say now" is not the same question as "what did the user just author".
    // Absorb a control change ONLY when it is a real edit:
    //
    //   unchanged since we last looked  -> nothing to absorb, keep the stored delta.
    //   changed, and the new reading is this group's own stored ROOT while the POST still holds
    //                                      a real shape delta
    //                                   -> a FOREIGN restore. ModelViewer.SyncShapeSlidersToGroupRoot,
    //                                      GroomRootStateAuthority.RestoreRootToViewer, the CLUMPER
    //                                      teardown and the menu/texture exit guards all write the
    //                                      group root into these same fields. Put this POST's own
    //                                      authored values back on the panel and KEEP the delta.
    //   changed any other way           -> the user moved a slider. Absorb it.
    //
    // Without the middle case a single foreign write annihilates the POST's edit for good, and
    // every later "why can I not edit my POSTs any more" symptom follows from that one frame.
    void AbsorbPanelEdit(PostAffector active)
    {
        ControlState current = ReadControls();

        if (!hasPanelControls)
        {
            lastPanelControls = current;
            hasPanelControls = true;
            return;
        }

        if (SameControls(current, lastPanelControls)) return;

        if (IsForeignRootRestore(active, current))
        {
            ApplyControls(Add(active.baseline, active.delta));
            lastPanelControls = ReadControls();
            return;
        }

        active.delta = Subtract(current, active.baseline);
        lastPanelControls = current;
    }

    bool IsForeignRootRestore(PostAffector active, ControlState current)
    {
        // A POST with no shape delta has nothing to lose, so never fight the panel over one.
        if (!HasShapeDelta(active.delta)) return false;

        if (rootAuthority == null) rootAuthority = FindFirstObjectByType<GroomRootStateAuthority>();
        if (rootAuthority == null) return false;

        GroomRootStateAuthority.RootState root = default;
        if (!rootAuthority.TryGetRootState(active.groupId, out root)) return false;

        // Compare the SHAPE channels only: SyncShapeSlidersToGroupRoot rewrites those eleven and
        // deliberately leaves the UV channels alone, so a UV-only difference must not disqualify
        // an otherwise obvious root restore.
        return SameShape(current, FromRoot(root));
    }

    bool IsClumperSelectedForGroup(int groupId)
    {
        if (clumperManager == null) clumperManager = FindFirstObjectByType<GroupClumperManager>();
        if (clumperManager == null) return false;

        GroupClumperManager.GroupClumper selected = clumperManager.GetSelectedClumper();
        if (selected == null) return false;
        return selected.groupId == groupId;
    }

    static ControlState FromRoot(GroomRootStateAuthority.RootState r)
    {
        ControlState s = new ControlState();
        s.length = r.length;
        s.width = r.width;
        s.segments = r.segments;
        s.bend = r.bend;
        s.twist = r.twist;
        s.depth = r.depth;
        s.x = r.x;
        s.y = r.y;
        s.z = r.z;
        s.uScale = r.uScale;
        s.vScale = r.vScale;
        s.uOffset = r.uOffset;
        s.vOffset = r.vOffset;
        s.curlFrequency = r.curlFrequency;
        s.curlDiameter = r.curlDiameter;
        s.waveAmplitude = r.waveAmplitude;
        s.waveFrequency = r.waveFrequency;
        s.waveDirection = r.waveDirection;
        s.arch = r.arch;
        return s;
    }

    const float ControlEpsilon = .000001f;

    static bool Near(float a, float b)
    {
        return Mathf.Abs(a - b) <= ControlEpsilon;
    }

    static bool SameShape(ControlState a, ControlState b)
    {
        if (!Near(a.length, b.length)) return false;
        if (!Near(a.width, b.width)) return false;
        if (!Near(a.segments, b.segments)) return false;
        if (!Near(a.bend, b.bend)) return false;
        if (!Near(a.twist, b.twist)) return false;
        if (!Near(a.depth, b.depth)) return false;
        if (!Near(a.x, b.x)) return false;
        if (!Near(a.y, b.y)) return false;
        if (!Near(a.z, b.z)) return false;
        if (!Near(a.curlFrequency, b.curlFrequency)) return false;
        if (!Near(a.curlDiameter, b.curlDiameter)) return false;
        // Omitting these would make a wave-only edit compare EQUAL to the previous shape, so
        // the POST would decide it had no delta and annihilate it. Same failure the project
        // already documented for other channels.
        if (!Near(a.waveAmplitude, b.waveAmplitude)) return false;
        if (!Near(a.waveFrequency, b.waveFrequency)) return false;
        if (!Near(a.waveDirection, b.waveDirection)) return false;
        if (!Near(a.arch, b.arch)) return false;
        return true;
    }

    static bool SameControls(ControlState a, ControlState b)
    {
        if (!SameShape(a, b)) return false;
        if (!Near(a.uScale, b.uScale)) return false;
        if (!Near(a.vScale, b.vScale)) return false;
        if (!Near(a.uOffset, b.uOffset)) return false;
        if (!Near(a.vOffset, b.vOffset)) return false;
        return true;
    }

    static bool HasShapeDelta(ControlState d)
    {
        ControlState zero = new ControlState();
        return !SameShape(d, zero);
    }

    // The single, atomic way to leave POST editing. Both halves of the selection go down
    // together so the orphaned-hotspot state above can never be created again, and the normal
    // exit guards still see the activeId >= 0 -> < 0 transition they key off.
    //
    // Public so CLUMPER can call it when it releases a group (GroupClumperManager.RemoveClumper).
    // A modifier that is being deleted must hand the edit context back explicitly rather than
    // leaving it for whichever guard happens to notice first.
    public void ReleasePostSelection()
    {
        activeId = -1;
        activeGroup = -1;
        orphanHotspotFrames = 0;
        hasPanelControls = false;
        SetField(hasSelectionField, false);
    }

    // Canonical state is the only upstream source of truth. While a POST is actively
    // authored, ModelViewer's legacy selection path may still call SetParameters on cards;
    // restore canonical immediately so those preview writes cannot pollute the group root.
    void UpdateCanonicalBases()
    {
        bool editingPost = GetActive() != null && HasSelection();

        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
        {
            // Frozen by SOLO: leave the cached base exactly where it is. Canonical state on
            // the card is untouched, so the frame SOLO releases the group this resumes and
            // re-reads it. See GroupSoloVisibilityAuthority.
            if (GroupSoloVisibilityAuthority.IsCardFrozen(card)) continue;

            if (!cardStates.TryGetValue(card, out CardState state))
            {
                ControlState canonical = ReadCanonical(card);
                state = new CardState { baseState = canonical, lastFinal = canonical, hasFinal = false };
                cardStates[card] = state;
            }

            if (editingPost)
            {
                WriteCanonicalOnly(card, state.baseState);
            }
            else
            {
                state.baseState = ReadCanonical(card);
            }
        }

        foreach (HairCard dead in cardStates.Keys.Where(c => c == null).ToArray())
            cardStates.Remove(dead);
    }

    void ApplyAll()
    {
        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
        {
            // This is the single biggest cost in the whole per-frame budget: every card
            // reached here gets a full procedural GenerateMesh() rebuild via
            // WriteEvaluatedCard -> ApplyEvaluatedState. Skipping the cards SOLO has hidden
            // is what makes soloing actually cheap rather than merely invisible.
            if (GroupSoloVisibilityAuthority.IsCardFrozen(card)) continue;

            if (!cardStates.TryGetValue(card, out CardState state))
            {
                ControlState canonical = ReadCanonical(card);
                state = new CardState { baseState = canonical, lastFinal = canonical, hasFinal = false };
                cardStates[card] = state;
            }

            ControlState result = state.baseState;
            if (groups.TryGetValue(card.groupId, out List<PostAffector> list))
                result = Add(result, EffectForCard(card, list));

            // UV MODE is group routing, not a POST-local property. PREDETERMINED therefore
            // hard-routes the final UVs from the card's canonical group assignment and ignores
            // any older Adjustable UV delta stored inside POST. The delta is retained so it can
            // become active again if the whole group is later switched back to ADJUSTABLE.
            if (UsesPredeterminedUVs(card.groupId))
            {
                ControlState canonicalUV = ReadCanonical(card);
                CopyUV(ref result, canonicalUV);
            }

            WriteEvaluatedCard(card, result);
            state.lastFinal = result;
            state.hasFinal = true;
        }
    }

    ControlState EffectForCard(HairCard card, List<PostAffector> list)
    {
        ControlState effect = new ControlState();
        foreach (PostAffector a in list)
        {
            float w = SpatialWeight(card, a) * Mathf.Clamp01(a.weight);
            if (w > .000001f) effect = Add(effect, Scale(a.delta, w));
        }
        return effect;
    }

    float SpatialWeight(HairCard card, PostAffector a)
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

    void WriteEvaluatedCard(HairCard card, ControlState s)
    {
        card.ApplyEvaluatedState(ToGroomState(s));
    }

    void WriteCanonicalOnly(HairCard card, ControlState s)
    {
        // While a POST is selected, preserve PREDETERMINED's per-card canonical rectangle.
        // This prevents the legacy POST baseline cache from writing an old Adjustable UV state
        // back into canonical data during active authoring.
        if (UsesPredeterminedUVs(card.groupId))
        {
            ControlState canonicalUV = ReadCanonical(card);
            CopyUV(ref s, canonicalUV);
        }
        card.SetCanonicalState(ToGroomState(s), false);
    }

    bool UsesPredeterminedUVs(int groupId)
    {
        if (predeterminedUVCacheFrame != Time.frameCount)
        {
            predeterminedUVCacheFrame = Time.frameCount;
            predeterminedUVByGroup.Clear();
        }

        if (predeterminedUVByGroup.TryGetValue(groupId, out bool cached)) return cached;

        if (uvRouting == null) uvRouting = FindFirstObjectByType<GroupPredeterminedUVController>();
        bool predetermined = false;
        if (uvRouting != null)
        {
            GroupSaveData probe = new GroupSaveData { groupId = groupId };
            uvRouting.PopulateGroupSave(probe);
            predetermined = probe.usePredeterminedUVs;
        }

        predeterminedUVByGroup[groupId] = predetermined;
        return predetermined;
    }

    static void CopyUV(ref ControlState target, ControlState source)
    {
        target.uScale = source.uScale;
        target.vScale = source.vScale;
        target.uOffset = source.uOffset;
        target.vOffset = source.vOffset;
    }

    HairCard.GroomState ToGroomState(ControlState s)
    {
        return new HairCard.GroomState
        {
            length = Mathf.Max(.0001f, s.length),
            width = Mathf.Max(.0005f, s.width),
            segments = Mathf.Clamp(Mathf.RoundToInt(s.segments), 4, 60),
            bend = s.bend,
            twist = s.twist,
            depth = Mathf.Max(0f, s.depth),
            x = s.x,
            y = s.y,
            z = s.z,
            uScale = s.uScale,
            vScale = s.vScale,
            uOffset = s.uOffset,
            vOffset = s.vOffset,
            curlFrequency = s.curlFrequency,
            curlDiameter = Mathf.Max(0f, s.curlDiameter),
            waveAmplitude = Mathf.Max(0f, s.waveAmplitude),
            waveFrequency = s.waveFrequency,
            waveDirection = Mathf.Clamp01(s.waveDirection),
            arch = Mathf.Max(0f, s.arch)
        };
    }

    void EnsureRowsAndOrder()
    {
        RectTransform[] all = FindObjectsByType<RectTransform>(FindObjectsSortMode.None);
        foreach (RectTransform groupItem in all.Where(r => r.name.StartsWith("GroupItem_")))
        {
            if (!int.TryParse(groupItem.name.Substring("GroupItem_".Length), out int gid)) continue;
            Transform parent = groupItem.parent;
            if (parent == null) continue;

            List<PostAffector> list = groups.TryGetValue(gid, out List<PostAffector> found) ? found : null;
            int insert = groupItem.GetSiblingIndex() + 1;
            if (list == null) continue;

            int number = 1;
            foreach (PostAffector a in list)
            {
                string rowName = RowName(gid, a.id);
                Transform row = parent.Find(rowName);
                if (row == null) row = BuildRow(parent, a, number).transform;
                row.SetSiblingIndex(insert++);
                number++;
            }
        }
    }

    GameObject BuildRow(Transform parent, PostAffector a, int number)
    {
        GameObject row = new GameObject(RowName(a.groupId, a.id), typeof(RectTransform), typeof(Image), typeof(HorizontalLayoutGroup));
        row.transform.SetParent(parent, false);
        row.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 34f);
        row.GetComponent<Image>().color = a.id == activeId ? new Color(.18f, .24f, .34f, .98f) : new Color(.12f, .14f, .18f, .98f);
        HorizontalLayoutGroup layout = row.GetComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(6, 6, 4, 4);
        layout.spacing = 5f;
        layout.childControlWidth = false;
        layout.childControlHeight = true;

        GameObject select = AddButton(row.transform, "POST " + number, 72f);
        select.GetComponent<Button>().onClick.AddListener(() => SelectAffector(a));
        // 8pt in 48px: "WEIGHT" measures well inside the box, so TMP never wraps it onto a
        // second line. At 10pt/45px it did, which is what produced the "WEIG / HT" split.
        TextMeshProUGUI wt = AddText(row.transform, "WEIGHT", 8, 48f);
        wt.alignment = TextAlignmentOptions.Center;

        Slider slider = AddWeightSlider(row.transform, a.weight, 128f);
        TextMeshProUGUI value = AddText(row.transform, a.weight.ToString("F2"), 10, 30f);
        value.alignment = TextAlignmentOptions.Center;
        slider.onValueChanged.AddListener(v =>
        {
            a.weight = Mathf.Clamp01(v);
            value.text = a.weight.ToString("F2");
            if (a.id == activeId)
            {
                viewer.selectionStrength = a.weight;
                RenameLegacyStrengthToWeight();
            }
        });

        // "DEL" instead of "[-]", 40px wide so the three glyphs sit inside the button with a
        // little breathing room at either end. AddButton names the GameObject after its label,
        // so ModifierNeutralizeBeforeDeleteAuthority now looks for this name too.
        GameObject remove = AddButton(row.transform, "DEL", 40f);
        remove.GetComponent<Button>().onClick.AddListener(() => RemoveAffector(a));
        return row;
    }

    void SelectAffector(PostAffector a)
    {
        // A selected GUIDE hides the whole groom panel behind its own controls, so a POST
        // selected underneath it would be edited and repositioned while the panel still named
        // the guide. Same hook GroupClumperManager.SelectClumper has.
        GuideCurveManager guides = FindFirstObjectByType<GuideCurveManager>();
        if (guides != null) guides.ClearSelection();

        activeId = a.id;
        activeGroup = a.groupId;
        viewer.currentGroupId = a.groupId;
        SetField(hasSelectionField, true);
        SetField(hitPointField, a.center);
        SetField(hitNormalField, a.normal);
        viewer.brushRadius = a.radius;
        viewer.brushFalloffDistance = a.falloff;
        viewer.selectionStrength = a.weight;
        ApplyControls(Add(a.baseline, a.delta));
        SyncVisibleSlidersToViewer();

        // This POST now owns the panel and the panel now shows exactly its authored values.
        // Record that so AbsorbPanelEdit's very first comparison next frame is against what we
        // just wrote, not against whatever the previous context left behind.
        lastPanelControls = ReadControls();
        hasPanelControls = true;

        RebuildGroupRows(a.groupId);
    }

    // ApplyControls above updates ModelViewer.current* (the underlying data), but the actual
    // Slider UI widgets don't automatically follow a C# field change - without this, switching
    // directly from one POST to another left every slider showing wherever it happened to sit
    // from whatever was being edited before, silently misleading anyone who starts dragging
    // from there into thinking that position reflects the newly-selected POST's actual value.
    // This gives a clean, known resting point every time a POST is selected.
    void SyncVisibleSlidersToViewer()
    {
        GameObject panel = viewer.groomingSliderPanelGO;
        if (panel == null) return;

        foreach (Slider slider in panel.GetComponentsInChildren<Slider>(true))
        {
            if (slider == null) continue;
            switch (slider.gameObject.name)
            {
                case "Length_Slider": slider.SetValueWithoutNotify(viewer.currentLength); break;
                case "Width_Slider": slider.SetValueWithoutNotify(viewer.currentWidth); break;
                case "Segments_Slider": slider.SetValueWithoutNotify(viewer.currentSegments); break;
                case "Bend Angle_Slider": slider.SetValueWithoutNotify(viewer.currentBend); break;
                case "Twist Angle_Slider": slider.SetValueWithoutNotify(viewer.currentTwist); break;
                case "Embed Depth_Slider": slider.SetValueWithoutNotify(viewer.currentEmbedDepth); break;
                case "Offset X_Slider":
                case "Angle X_Slider": slider.SetValueWithoutNotify(viewer.currentOffsetX); break;
                case "Offset Y_Slider":
                case "Angle Y_Slider": slider.SetValueWithoutNotify(viewer.currentOffsetY); break;
                case "Offset Z_Slider":
                case "Angle Z_Slider": slider.SetValueWithoutNotify(viewer.currentOffsetZ); break;
                case "U Scale_Slider": slider.SetValueWithoutNotify(viewer.currentUScale); break;
                case "V Scale_Slider": slider.SetValueWithoutNotify(viewer.currentVScale); break;
                case "U Offset_Slider": slider.SetValueWithoutNotify(viewer.currentUOffset); break;
                case "V Offset_Slider": slider.SetValueWithoutNotify(viewer.currentVOffset); break;
                case "Curl Frequency_Slider": slider.SetValueWithoutNotify(viewer.currentCurlFrequency); break;
                case "Curl Diameter_Slider": slider.SetValueWithoutNotify(viewer.currentCurlDiameter); break;
                case "Wave Amplitude_Slider": slider.SetValueWithoutNotify(viewer.currentWaveAmplitude); break;
                case "Wave Frequency_Slider": slider.SetValueWithoutNotify(viewer.currentWaveFrequency); break;
                case "Wave Direction_Slider": slider.SetValueWithoutNotify(viewer.currentWaveDirection); break;
                case "Arch_Slider": slider.SetValueWithoutNotify(viewer.currentArch); break;
            }
        }
    }

    void RemoveAffector(PostAffector a)
    {
        if (groups.TryGetValue(a.groupId, out List<PostAffector> list))
        {
            list.RemoveAll(x => x.id == a.id);
            if (list.Count == 0) groups.Remove(a.groupId);
        }
        if (activeId == a.id)
        {
            activeId = -1;
            activeGroup = -1;
            SetField(hasSelectionField, false);
        }
        RebuildGroupRows(a.groupId);
        ApplyAll();
    }

    void RebuildGroupRows(int gid)
    {
        foreach (RectTransform r in FindObjectsByType<RectTransform>(FindObjectsSortMode.None)
            .Where(r => r.name.StartsWith("PostAffector_" + gid + "_")))
            Destroy(r.gameObject);
        nextUIScan = 0f;
    }

    void RenameLegacyStrengthToWeight()
    {
        GameObject row = strengthRowField?.GetValue(viewer) as GameObject;
        if (row == null) return;
        TextMeshProUGUI label = row.GetComponentInChildren<TextMeshProUGUI>(true);
        if (label != null) label.text = "WEIGHT: " + viewer.selectionStrength.ToString("F3");
        Slider slider = row.GetComponentInChildren<Slider>(true);
        if (slider != null && !Mathf.Approximately(slider.value, viewer.selectionStrength))
            slider.SetValueWithoutNotify(viewer.selectionStrength);
    }

    GameObject AddButton(Transform parent, string text, float width)
    {
        GameObject go = new GameObject(text, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(width, 25f);
        go.GetComponent<Image>().color = new Color(.20f, .25f, .32f);
        TextMeshProUGUI t = AddText(go.transform, text, 10, width);
        RectTransform tr = t.rectTransform;
        tr.anchorMin = Vector2.zero;
        tr.anchorMax = Vector2.one;
        tr.offsetMin = Vector2.zero;
        tr.offsetMax = Vector2.zero;
        t.raycastTarget = false;
        return go;
    }

    TextMeshProUGUI AddText(Transform parent, string text, int size, float width)
    {
        GameObject go = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(width, 25f);
        TextMeshProUGUI t = go.GetComponent<TextMeshProUGUI>();
        t.text = text;
        t.fontSize = size;
        t.color = Color.white;
        t.alignment = TextAlignmentOptions.Center;
        return t;
    }

    Slider AddWeightSlider(Transform parent, float value, float width)
    {
        GameObject go = new GameObject("WeightSlider", typeof(RectTransform), typeof(Slider));
        go.transform.SetParent(parent, false);
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(width, 24f);
        Slider s = go.GetComponent<Slider>();
        s.minValue = 0f;
        s.maxValue = 1f;
        s.value = value;

        GameObject bg = new GameObject("Background", typeof(RectTransform), typeof(Image));
        bg.transform.SetParent(go.transform, false);
        RectTransform br = bg.GetComponent<RectTransform>();
        br.anchorMin = new Vector2(0, .42f);
        br.anchorMax = new Vector2(1, .58f);
        br.offsetMin = br.offsetMax = Vector2.zero;
        bg.GetComponent<Image>().color = new Color(.24f, .24f, .24f);

        GameObject fillArea = new GameObject("Fill Area", typeof(RectTransform));
        fillArea.transform.SetParent(go.transform, false);
        RectTransform far = fillArea.GetComponent<RectTransform>();
        far.anchorMin = new Vector2(0, .35f);
        far.anchorMax = new Vector2(1, .65f);
        far.offsetMin = new Vector2(4, 0);
        far.offsetMax = new Vector2(-4, 0);

        GameObject fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
        fill.transform.SetParent(fillArea.transform, false);
        RectTransform fr = fill.GetComponent<RectTransform>();
        fr.anchorMin = Vector2.zero;
        fr.anchorMax = Vector2.one;
        fr.offsetMin = fr.offsetMax = Vector2.zero;
        fill.GetComponent<Image>().color = new Color(.28f, .58f, .95f);
        s.fillRect = fr;

        GameObject ha = new GameObject("Handle Slide Area", typeof(RectTransform));
        ha.transform.SetParent(go.transform, false);
        RectTransform har = ha.GetComponent<RectTransform>();
        har.anchorMin = Vector2.zero;
        har.anchorMax = Vector2.one;
        har.offsetMin = new Vector2(5, 0);
        har.offsetMax = new Vector2(-5, 0);

        GameObject h = new GameObject("Handle", typeof(RectTransform), typeof(Image));
        h.transform.SetParent(ha.transform, false);
        RectTransform hr = h.GetComponent<RectTransform>();
        // Half the previous height. 16 made the handle taller than the row's track looked
        // designed for; 8 reads as a notch on the bar rather than a bar of its own.
        hr.sizeDelta = new Vector2(9, 8);
        h.GetComponent<Image>().color = Color.white;
        s.handleRect = hr;
        return s;
    }

    string RowName(int gid, int id) => "PostAffector_" + gid + "_" + id;
    PostAffector GetActive() => groups.TryGetValue(activeGroup, out List<PostAffector> list) ? list.FirstOrDefault(a => a.id == activeId) : null;
    bool HasSelection() => hasSelectionField != null && hasSelectionField.GetValue(viewer) is bool b && b;
    Vector3 GetVector(FieldInfo f) => f != null && f.GetValue(viewer) is Vector3 v ? v : Vector3.zero;
    void SetField(FieldInfo f, object value) { if (f != null) f.SetValue(viewer, value); }

    ControlState ReadControls() => new ControlState
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

    ControlState ReadCanonical(HairCard c)
    {
        HairCard.GroomState s = c.GetCanonicalState();
        return new ControlState
        {
            length = s.length,
            width = s.width,
            segments = s.segments,
            bend = s.bend,
            twist = s.twist,
            depth = s.depth,
            x = s.x,
            y = s.y,
            z = s.z,
            uScale = s.uScale,
            vScale = s.vScale,
            uOffset = s.uOffset,
            vOffset = s.vOffset,
            curlFrequency = s.curlFrequency,
            curlDiameter = s.curlDiameter,
            waveAmplitude = s.waveAmplitude,
            waveFrequency = s.waveFrequency,
            waveDirection = s.waveDirection,
            arch = s.arch
        };
    }

    void ApplyControls(ControlState s)
    {
        viewer.currentLength = s.length;
        viewer.currentWidth = s.width;
        viewer.currentSegments = Mathf.RoundToInt(s.segments);
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

    static ControlState Add(ControlState a, ControlState b) => new ControlState
    {
        length = a.length + b.length, width = a.width + b.width, segments = a.segments + b.segments,
        bend = a.bend + b.bend, twist = a.twist + b.twist, depth = a.depth + b.depth,
        x = a.x + b.x, y = a.y + b.y, z = a.z + b.z,
        uScale = a.uScale + b.uScale, vScale = a.vScale + b.vScale,
        uOffset = a.uOffset + b.uOffset, vOffset = a.vOffset + b.vOffset,
        curlFrequency = a.curlFrequency + b.curlFrequency, curlDiameter = a.curlDiameter + b.curlDiameter,
        waveAmplitude = a.waveAmplitude + b.waveAmplitude, waveFrequency = a.waveFrequency + b.waveFrequency,
        waveDirection = a.waveDirection + b.waveDirection, arch = a.arch + b.arch
    };

    static ControlState Subtract(ControlState a, ControlState b) => new ControlState
    {
        length = a.length - b.length, width = a.width - b.width, segments = a.segments - b.segments,
        bend = a.bend - b.bend, twist = a.twist - b.twist, depth = a.depth - b.depth,
        x = a.x - b.x, y = a.y - b.y, z = a.z - b.z,
        uScale = a.uScale - b.uScale, vScale = a.vScale - b.vScale,
        uOffset = a.uOffset - b.uOffset, vOffset = a.vOffset - b.vOffset,
        curlFrequency = a.curlFrequency - b.curlFrequency, curlDiameter = a.curlDiameter - b.curlDiameter,
        waveAmplitude = a.waveAmplitude - b.waveAmplitude, waveFrequency = a.waveFrequency - b.waveFrequency,
        waveDirection = a.waveDirection - b.waveDirection, arch = a.arch - b.arch
    };

    static ControlState Scale(ControlState a, float s) => new ControlState
    {
        length = a.length * s, width = a.width * s, segments = a.segments * s,
        bend = a.bend * s, twist = a.twist * s, depth = a.depth * s,
        x = a.x * s, y = a.y * s, z = a.z * s,
        uScale = a.uScale * s, vScale = a.vScale * s,
        uOffset = a.uOffset * s, vOffset = a.vOffset * s,
        curlFrequency = a.curlFrequency * s, curlDiameter = a.curlDiameter * s,
        waveAmplitude = a.waveAmplitude * s, waveFrequency = a.waveFrequency * s,
        waveDirection = a.waveDirection * s, arch = a.arch * s
    };

    public List<PostAffectorSaveData> ExportGroup(int groupId)
    {
        List<PostAffectorSaveData> result = new List<PostAffectorSaveData>();
        if (!groups.TryGetValue(groupId, out List<PostAffector> list)) return result;
        foreach (PostAffector a in list)
        {
            result.Add(new PostAffectorSaveData
            {
                id = a.id,
                centerX = a.center.x, centerY = a.center.y, centerZ = a.center.z,
                normalX = a.normal.x, normalY = a.normal.y, normalZ = a.normal.z,
                radius = a.radius, falloff = a.falloff, weight = a.weight,
                baseline = ToSave(a.baseline), delta = ToSave(a.delta)
            });
        }
        return result;
    }

    public void ClearAll()
    {
        EnsureViewer();
        groups.Clear();
        cardStates.Clear();
        predeterminedUVByGroup.Clear();
        predeterminedUVCacheFrame = -1;
        uvRouting = null;
        activeId = -1;
        activeGroup = -1;
        nextId = 1;

        // A saved-project load can begin while a POST is selected. Clear the shared
        // selection hotspot as part of POST teardown so the radial marker cannot survive
        // into the newly loaded project/model.
        SetField(hasSelectionField, false);
        SetField(hitPointField, Vector3.zero);
        SetField(hitNormalField, Vector3.zero);

        foreach (RectTransform r in FindObjectsByType<RectTransform>(FindObjectsSortMode.None).Where(r => r.name.StartsWith("PostAffector_")))
            Destroy(r.gameObject);
        nextUIScan = 0f;
    }

    public void ImportGroup(int groupId, List<PostAffectorSaveData> data)
    {
        groups.Remove(groupId);
        if (data == null || data.Count == 0)
        {
            RebuildGroupRows(groupId);
            return;
        }

        List<PostAffector> list = new List<PostAffector>();
        foreach (PostAffectorSaveData d in data)
        {
            PostAffector a = new PostAffector
            {
                id = d.id,
                groupId = groupId,
                center = new Vector3(d.centerX, d.centerY, d.centerZ),
                normal = new Vector3(d.normalX, d.normalY, d.normalZ),
                radius = d.radius,
                falloff = d.falloff,
                weight = d.weight,
                baseline = FromSave(d.baseline),
                delta = FromSave(d.delta)
            };
            list.Add(a);
            nextId = Mathf.Max(nextId, a.id + 1);
        }
        groups[groupId] = list;

        // Format-v2 projects save canonical/upstream card state already. Do not subtract
        // POST effects during import: that old recovery path turns a valid base into
        // "base - POST", then the normal evaluator applies POST again and can create a
        // double-state handoff when several affectors are restored.
        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None).Where(c => c.groupId == groupId))
        {
            ControlState canonical = ReadCanonical(card);
            cardStates[card] = new CardState { baseState = canonical, lastFinal = canonical, hasFinal = false };
        }
        RebuildGroupRows(groupId);
    }

    static PostAffectorControlSaveData ToSave(ControlState s) => new PostAffectorControlSaveData
    {
        length = s.length, width = s.width, segments = s.segments, bend = s.bend, twist = s.twist,
        depth = s.depth, x = s.x, y = s.y, z = s.z,
        uScale = s.uScale, vScale = s.vScale, uOffset = s.uOffset, vOffset = s.vOffset,
        curlFrequency = s.curlFrequency, curlDiameter = s.curlDiameter,
        waveAmplitude = s.waveAmplitude, waveFrequency = s.waveFrequency,
        waveDirection = s.waveDirection, arch = s.arch
    };

    static ControlState FromSave(PostAffectorControlSaveData s) => s == null ? new ControlState() : new ControlState
    {
        length = s.length, width = s.width, segments = s.segments, bend = s.bend, twist = s.twist,
        depth = s.depth, x = s.x, y = s.y, z = s.z,
        uScale = s.uScale, vScale = s.vScale, uOffset = s.uOffset, vOffset = s.vOffset,
        curlFrequency = s.curlFrequency, curlDiameter = s.curlDiameter,
        waveAmplitude = s.waveAmplitude, waveFrequency = s.waveFrequency,
        waveDirection = s.waveDirection, arch = s.arch
    };
}
