using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
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

        // Up to 6 characters, chosen by the user by double-clicking the row's button. Empty is
        // the normal state and means "no name": the row falls back to "POST n", numbered by
        // position, which is what every POST was called before this existed.
        public string label = "";

        // RELATIVE (false, the default and everything that existed before this) adds this POST's
        // delta on top of whatever each card's own base happens to be, so per-card differences
        // and variance survive underneath it. ABSOLUTE (true) replaces the base instead: at full
        // weight the card takes baseline + delta exactly, and the falloff ring blends back out
        // to the base. Cards inside an ABSOLUTE POST therefore converge on one value - flattening
        // the spread is what an override IS, and is the reason it is not the default.
        public bool absolute = false;

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
    private int handledRebuildFrame = -1;
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

        // ESC is NOT read here. TMP_InputField handles Escape itself, in the input module at
        // execution order 0 - long before this Update at 3300 - by restoring the original text
        // and ending the edit, which arrives as onEndEdit with wasCanceled set. Reading it here
        // as well would have been a second consumer of the same press: an armed +POST placement
        // also cancels on ESC, and one press would have closed the rename AND disarmed the
        // placement.
        //
        // What IS needed is a sweep for a rename whose row died underneath it. Several paths
        // destroy POST rows without going through RebuildGroupRows - ModelViewer rebuilding the
        // group list, GroomSessionResetCoordinator clearing the modifier managers by reflection,
        // PostGroupLifetimeAuthority purging a group - and each would leave renamingId set on a
        // destroyed field. That state is not merely untidy: the next commit would read "" from
        // the dead field and wipe a name that was already saved, and the POST could never be
        // renamed again because BeginRename refuses to restart the id it thinks is open.
        if (renamingId >= 0 && renameField == null) CancelRename();

        DetectGroupRootSelection();
        DetectCtrlClick();
        MaintainActiveAuthoring();

        // Last, so it sees the selection the three calls above have settled on rather than the
        // one this frame started with.
        MaintainSelectionPaint();

        // The POST rows live in the group list, so ModelViewer's rebuild destroys them too. Left
        // to this scan alone they were gone for up to 0.12s after every group add or delete and
        // then popped back - the same glitch from the other end. Rebuilding them on the rebuild
        // frame puts them back before anything is drawn. This runs at 3300, after the group list
        // is rebuilt at order 0, and before the styling passes at 3600 and later.
        bool rebuilt = RuntimeUIRebuildSignal.TryConsume(ref handledRebuildFrame);
        if (rebuilt || Time.unscaledTime >= nextUIScan)
        {
            nextUIScan = Time.unscaledTime + .12f;
            EnsureRowsAndOrder();

            // Also on the scan, not only when the selection moves. UIThemeAuthority styles every
            // Slider in the scene exactly once, the first time it sees it, and that pass repaints
            // a WEIGHT slider's fill and handle in the shared theme colours - so a row built after
            // the last selection change would sit there bright until the next one. It only writes
            // once, so this pass wins from then on, and every write here is guarded by a compare.
            PaintAffectorRows();
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

    // ---- who owns the panel, said out loud ------------------------------------------------
    //
    // Which POST is selected was already tracked; what was missing is that NOTHING kept the UI
    // in step with it after the first time. The panel went yellow in ModelViewer.EnterSelectionMode
    // - the Ctrl+click that CREATES a POST - and nowhere else, so selecting an existing POST from
    // its row left the panel grey while that POST owned every slider on it. And a POST row was
    // painted as selected once, in BuildRow at the moment it was created; RebuildGroupRows only
    // ever re-parents and re-indexes existing rows, so the highlight never moved off a row again.
    // Create three POSTs and all three rows read as selected, including while the GROUP root is
    // the thing being edited.
    //
    // Two callers, one pass. The selection change drives it so the panel and the rows turn over
    // on the frame the user clicks; the 0.12s row scan repeats it so a row built later, or a
    // slider UIThemeAuthority has since restyled, is brought back into line. Every write is
    // guarded by a compare, so the repeat costs a handful of colour reads and nothing else.
    private const int NoPaintedSelection = int.MinValue;
    private int paintedActiveId = NoPaintedSelection;

    private static readonly Color RowSelectedColour = new Color(.18f, .24f, .34f, .98f);
    private static readonly Color RowIdleColour = new Color(.12f, .14f, .18f, .98f);
    private static readonly Color WeightFillSelectedColour = new Color(.28f, .58f, .95f, 1f);
    private static readonly Color WeightFillIdleColour = new Color(.24f, .30f, .38f, 1f);
    private static readonly Color WeightHandleSelectedColour = Color.white;
    private static readonly Color WeightHandleIdleColour = new Color(.55f, .58f, .62f, 1f);

    void MaintainSelectionPaint()
    {
        if (paintedActiveId == activeId) return;
        paintedActiveId = activeId;

        // The panel is yellow while ANY post is selected and grey the moment the group root
        // takes it back - including via DetectGroupRootSelection above, which clears activeId
        // by hand and is the route that left the panel yellow over a group edit.
        if (viewer != null) viewer.SetGroomPanelModifierAccent(activeId >= 0);

        PaintAffectorRows();
    }

    // Repaints every POST row in the left panel to match the current selection.
    void PaintAffectorRows()
    {
        foreach (RectTransform row in FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (row == null || !row.name.StartsWith("PostAffector_", StringComparison.Ordinal)) continue;

            int rowId;
            if (!TryReadRowAffectorId(row.name, out rowId)) continue;

            bool selected = rowId == activeId && activeId >= 0;

            Image background = row.GetComponent<Image>();
            if (background != null)
            {
                Color wanted = RowIdleColour;
                if (selected)
                {
                    wanted = RowSelectedColour;
                }
                if (background.color != wanted) background.color = wanted;
            }

            PaintWeightSlider(row, selected);

            // The mode button is painted from the affector itself rather than from anything the
            // row remembers, so a row rebuilt by a project load, or restyled by UIThemeAuthority
            // on its one pass, comes back saying what the POST actually is.
            PostAffector affector;
            int number;
            if (!TryFindAffector(rowId, out affector, out number)) continue;

            PaintModeButton(row, affector.absolute);

            // The caption too, so a rename and a renumber both land without anything having to
            // remember to ask for them - and so a row rebuilt by a project load comes back with
            // the name that project saved.
            Transform select = row.Find(SelectButtonName);
            if (select == null) continue;

            // Not while it is being renamed: the caption is hidden behind the edit field, and
            // writing to it under the user's typing is how a rename ends up half-applied.
            if (renamingId == affector.id) continue;

            TextMeshProUGUI caption = select.GetComponentInChildren<TextMeshProUGUI>(true);
            if (caption == null) continue;

            string wantedCaption = DisplayLabel(affector, number);
            if (caption.text != wantedCaption) caption.text = wantedCaption;
        }
    }

    // The affector with this id, and its 1-based position in its group - which is the number a
    // row shows when it has no name.
    bool TryFindAffector(int id, out PostAffector affector, out int number)
    {
        affector = null;
        number = 0;
        if (id < 0) return false;

        foreach (KeyValuePair<int, List<PostAffector>> pair in groups)
        {
            if (pair.Value == null) continue;

            int position = 1;
            foreach (PostAffector a in pair.Value)
            {
                if (a != null && a.id == id)
                {
                    affector = a;
                    number = position;
                    return true;
                }
                position++;
            }
        }
        return false;
    }

    // The WEIGHT slider stays usable whichever POST is selected - it is that POST's amount, and
    // wanting to trim one from the group root is ordinary. It only stops LOOKING like the live
    // control, which is the whole of the complaint: a bright blue bar under the group while the
    // group is what you are editing reads as "this is what you are holding", and it is not.
    void PaintWeightSlider(Transform row, bool selected)
    {
        Transform slider = row.Find("WeightSlider");
        if (slider == null) return;

        Transform fill = slider.Find("Fill Area/Fill");
        if (fill != null)
        {
            Image image = fill.GetComponent<Image>();
            if (image != null)
            {
                Color wanted = WeightFillIdleColour;
                if (selected)
                {
                    wanted = WeightFillSelectedColour;
                }
                if (image.color != wanted) image.color = wanted;
            }
        }

        Transform handle = slider.Find("Handle Slide Area/Handle");
        if (handle != null)
        {
            Image image = handle.GetComponent<Image>();
            if (image != null)
            {
                Color wanted = WeightHandleIdleColour;
                if (selected)
                {
                    wanted = WeightHandleSelectedColour;
                }
                if (image.color != wanted) image.color = wanted;
            }
        }
    }

    // "PostAffector_{group}_{id}" - the id is what is after the LAST underscore. Split rather
    // than a substring from the second underscore, because a group id is not a fixed width.
    static bool TryReadRowAffectorId(string rowName, out int id)
    {
        id = -1;
        int cut = rowName.LastIndexOf('_');
        if (cut < 0 || cut >= rowName.Length - 1) return false;
        return int.TryParse(rowName.Substring(cut + 1), out id);
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

            // In place. This used to DESTROY every POST row in the group and let the 0.12s scan
            // build them back, for nothing more than a number changing on one of them. It is a
            // narrower path than it looks - PostAffectorUXFix hides the right-hand WEIGHT row,
            // and the row's own slider writes a.weight before selectionStrength, so the compare
            // above is usually true - but every frame it does fire is a frame the rows are torn
            // down and rebuilt, and nothing about a changed weight needs a new row.
            RefreshRowWeights(active.groupId);
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
                result = EvaluateForCard(card, list, result);

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

    // Folds every POST on the group over the card's base, in list order.
    //
    // The two modes differ in what they do with the base, which is why this is a fold rather
    // than the sum it used to be:
    //
    //   RELATIVE  result += delta * w        the base is kept and offset. Two overlapping
    //                                        relative POSTs both contribute; order is irrelevant
    //                                        because addition commutes.
    //   ABSOLUTE  result = lerp(result, baseline + delta, w)
    //                                        the base is REPLACED, blended out across the
    //                                        falloff. At w = 1 the card is exactly the POST's
    //                                        authored values, which is what makes it an override:
    //                                        every card in range converges on the same numbers
    //                                        and whatever spread they had is gone.
    //
    // Order matters once an ABSOLUTE is involved, and list order is creation order: a later
    // ABSOLUTE overrides an earlier POST's contribution in proportion to its own weight, and a
    // RELATIVE created after an ABSOLUTE offsets the overridden value. That is the only reading
    // that lets the two be mixed at all, and it is stable - the list is rebuilt in the same
    // order from the save file.
    ControlState EvaluateForCard(HairCard card, List<PostAffector> list, ControlState baseState)
    {
        ControlState result = baseState;

        foreach (PostAffector a in list)
        {
            float w = SpatialWeight(card, a) * Mathf.Clamp01(a.weight);
            if (w <= .000001f) continue;

            if (a.absolute)
            {
                result = Lerp(result, Add(a.baseline, a.delta), w);
            }
            else
            {
                result = Add(result, Scale(a.delta, w));
            }
        }

        return result;
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
                if (row == null)
                {
                    row = BuildRow(parent, a, number).transform;

                    // Built with BuildRow's placeholder widths; PostAffectorUXFix owns the real
                    // ones and UIThemeAuthority owns the skin. Both in this frame, not theirs.
                    RuntimeUIRebuildSignal.Mark();
                }
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
        Color rowColour = RowIdleColour;
        if (a.id == activeId)
        {
            rowColour = RowSelectedColour;
        }
        row.GetComponent<Image>().color = rowColour;
        HorizontalLayoutGroup layout = row.GetComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(6, 6, 4, 4);
        layout.spacing = 5f;
        layout.childControlWidth = false;
        layout.childControlHeight = true;

        GameObject select = AddButton(row.transform, DisplayLabel(a, number), 72f);

        // AddButton names the object after its caption. A caption that can be renamed is a name
        // that can change, and the refresh pass finds this button by name, so it is pinned here -
        // the same reason the mode button is built by hand rather than through AddButton.
        select.name = SelectButtonName;
        select.GetComponent<Button>().onClick.AddListener(() => SelectAffector(a));

        // Double-click to rename, the same gesture that renames a group. The Button's own click
        // still fires first and selects the POST, which is what happens on a group row too.
        PostRowDoubleClick relay = select.AddComponent<PostRowDoubleClick>();
        relay.onDoubleClick = () => BeginRename(a);

        // REL / ABS. Built here with a FIXED object name rather than through AddButton, which
        // names the object after its caption - a caption that changes is a name that changes,
        // and the repaint pass finds this button by name.
        BuildModeButton(row.transform, a);
        // 8pt in 48px: "WEIGHT" measures well inside the box, so TMP never wraps it onto a
        // second line. At 10pt/45px it did, which is what produced the "WEIG / HT" split.
        TextMeshProUGUI wt = AddText(row.transform, "WEIGHT", 8, 48f);
        wt.alignment = TextAlignmentOptions.Center;

        // Named, like every other child of this row, because PostAffectorUXFix sets the column
        // widths by name - it used to do it by child index, and adding one button silently moved
        // every width onto the wrong column.
        wt.gameObject.name = WeightLabelName;

        // A starting width only - PostAffectorUXFix.CompactPostRows decides the real one every
        // 0.05s, and its budget is what keeps DEL on the panel.
        Slider slider = AddWeightSlider(row.transform, a.weight, 78f);
        TextMeshProUGUI value = AddText(row.transform, a.weight.ToString("F2"), 10, 30f);
        value.alignment = TextAlignmentOptions.Center;

        // Both texts on this row are called "Text" by AddText. The one that carries a live value
        // needs to be findable from outside the closure below, so that changing the weight can
        // update the row in place instead of destroying and rebuilding it.
        value.gameObject.name = WeightValueName;
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

        // THE CLICK GLITCH. This used to be RebuildGroupRows, which destroys every POST row in
        // the group and lets the next scan rebuild them - so clicking a POST destroyed the very
        // button being clicked, out from under the pointer, along with all its siblings. Unity
        // then had a pointer-down on a dead object, the rows vanished for a frame or more, and
        // the group list re-laid-out twice. Nothing about a selection needs a new row: the paint
        // pass recolours the rows that are already there.
        PaintAffectorRows();
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

    // Weight slider and its readout, for every row in one group, without touching the rows
    // themselves. The value label is found by name because it is captured in a closure at build
    // time and there is otherwise no way back to it.
    void RefreshRowWeights(int gid)
    {
        foreach (RectTransform row in FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (row == null || !row.name.StartsWith("PostAffector_" + gid + "_", StringComparison.Ordinal)) continue;

            int rowId;
            if (!TryReadRowAffectorId(row.name, out rowId)) continue;

            PostAffector affector;
            int number;
            if (!TryFindAffector(rowId, out affector, out number)) continue;

            Slider slider = row.GetComponentInChildren<Slider>(true);
            if (slider != null && !Mathf.Approximately(slider.value, affector.weight))
                slider.SetValueWithoutNotify(affector.weight);

            Transform value = row.Find(WeightValueName);
            if (value == null) continue;

            TextMeshProUGUI text = value.GetComponent<TextMeshProUGUI>();
            if (text == null) continue;

            string wanted = affector.weight.ToString("F2");
            if (text.text != wanted) text.text = wanted;
        }
    }

    // Still the right answer when the SET of rows changes - a POST created, deleted, or a whole
    // group imported - because the rows are renumbered by position and the list they are built
    // from has changed shape. It is NOT the right answer for anything that only changes how a
    // row looks or what it reads; those two callers now refresh in place.
    void RebuildGroupRows(int gid)
    {
        // A rename open on a row that is about to be destroyed would take the user's typing with
        // it. Take what has been typed so far first.
        CommitRename();

        foreach (RectTransform r in FindObjectsByType<RectTransform>(FindObjectsSortMode.None)
            .Where(r => r.name.StartsWith("PostAffector_" + gid + "_")))
            Destroy(r.gameObject);
        nextUIScan = 0f;
        RuntimeUIRebuildSignal.Mark();
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

    private const string SelectButtonName = "PostSelectButton";
    private const string WeightValueName = "PostWeightValue";
    private const string WeightLabelName = "PostWeightLabel";
    private const string ModeButtonName = "PostModeButton";
    private const float ModeButtonWidth = 40f;

    private static readonly Color ModeRelativeColour = new Color(.20f, .25f, .32f, 1f);
    private static readonly Color ModeAbsoluteColour = new Color(.46f, .38f, .12f, 1f);

    void BuildModeButton(Transform parent, PostAffector a)
    {
        GameObject go = new GameObject(ModeButtonName, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(ModeButtonWidth, 25f);

        TextMeshProUGUI label = AddText(go.transform, "REL", 10, ModeButtonWidth);
        RectTransform labelRect = label.rectTransform;
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;
        label.alignment = TextAlignmentOptions.Center;
        label.raycastTarget = false;

        go.GetComponent<Button>().onClick.AddListener(() => ToggleAbsolute(a));

        PaintModeButton(go.transform, a.absolute);
    }

    void ToggleAbsolute(PostAffector a)
    {
        if (a == null) return;
        a.absolute = !a.absolute;

        // Straight away rather than on the next scan: this is a button the user is watching, and
        // ApplyAll re-evaluates every card in the group on this same frame's LateUpdate, so the
        // model changes now whether or not the label has caught up.
        PaintAffectorRows();

        if (a.absolute)
        {
            StatusToast.Show("POST set to ABSOLUTE - it replaces the group values inside its radius, blending out across the falloff.", false, 4f);
        }
        else
        {
            StatusToast.Show("POST set to RELATIVE - it offsets whatever the group values are, so per-card spread survives underneath.", false, 4f);
        }
    }

    void PaintModeButton(Transform row, bool absolute)
    {
        Transform button = row.Find(ModeButtonName);
        if (button == null) return;

        Image image = button.GetComponent<Image>();
        if (image != null)
        {
            Color wanted = ModeRelativeColour;
            if (absolute)
            {
                wanted = ModeAbsoluteColour;
            }
            if (image.color != wanted) image.color = wanted;
        }

        TextMeshProUGUI label = button.GetComponentInChildren<TextMeshProUGUI>(true);
        if (label != null)
        {
            string wanted = "REL";
            if (absolute)
            {
                wanted = "ABS";
            }
            if (label.text != wanted) label.text = wanted;
        }
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

    // ---- renaming --------------------------------------------------------------------------
    //
    // Double-click a POST's button and type over it. Six characters, committed on ENTER or on
    // losing focus, abandoned on ESC.
    //
    // The field is parented to the row rather than to the button, and the button is hidden while
    // it is open, so the layout group keeps the row's shape and nothing shifts as the caption
    // becomes an edit box. It is also why the rename has to be committed before RebuildGroupRows
    // destroys the row underneath it.
    //
    // No separate "is the user typing" flag is needed for the shortcut keys: this is a real
    // TMP_InputField and GroupNameInlineEditAuthority.IsEnteringText already reports any focused
    // one, which is what GroomShortcutKeyAuthority and the other hotkeys ask.
    private int renamingId = -1;
    private TMP_InputField renameField;
    private GameObject renameHiddenCaption;

    void BeginRename(PostAffector a)
    {
        if (a == null) return;

        // A second double-click on the row already being renamed should not restart it and lose
        // what is in the box.
        if (renamingId == a.id) return;

        CommitRename();

        RectTransform row = FindRow(a);
        if (row == null) return;

        Transform button = row.Find(SelectButtonName);
        if (button == null) return;

        GameObject fieldObject = new GameObject("PostRenameField", typeof(RectTransform), typeof(Image), typeof(TMP_InputField));
        fieldObject.transform.SetParent(row, false);
        fieldObject.transform.SetSiblingIndex(button.GetSiblingIndex());

        RectTransform fieldRect = fieldObject.GetComponent<RectTransform>();
        fieldRect.sizeDelta = ((RectTransform)button).sizeDelta;

        // No LayoutElement, deliberately: the row's HorizontalLayoutGroup has childControlWidth
        // off, so it lays out on the rect above. Matching the button exactly - same rect, same
        // sibling index, button hidden - is what keeps the row from moving as the caption becomes
        // an edit box.
        fieldObject.GetComponent<Image>().color = new Color(.08f, .09f, .11f, 1f);

        // TMP needs a real text child and a viewport to put a caret in; without them the caret
        // and the selection highlight are drawn at the origin of the canvas.
        GameObject textArea = new GameObject("Text Area", typeof(RectTransform), typeof(RectMask2D));
        textArea.transform.SetParent(fieldObject.transform, false);
        RectTransform areaRect = textArea.GetComponent<RectTransform>();
        areaRect.anchorMin = Vector2.zero;
        areaRect.anchorMax = Vector2.one;
        areaRect.offsetMin = new Vector2(4f, 1f);
        areaRect.offsetMax = new Vector2(-4f, -1f);

        GameObject textObject = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textObject.transform.SetParent(textArea.transform, false);
        RectTransform textRect = textObject.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;

        TextMeshProUGUI text = textObject.GetComponent<TextMeshProUGUI>();
        text.fontSize = 10f;
        text.color = Color.white;
        text.alignment = TextAlignmentOptions.Center;
        text.richText = false;

        TMP_InputField field = fieldObject.GetComponent<TMP_InputField>();
        field.textViewport = areaRect;
        field.textComponent = text;
        field.characterLimit = LabelCharacterLimit;
        field.lineType = TMP_InputField.LineType.SingleLine;
        field.richText = false;
        field.text = a.label;
        field.caretWidth = 2;
        field.customCaretColor = true;
        field.caretColor = Color.white;

        // Set rather than assumed. It is TMP's default, but "ESC abandons the edit" is a promise
        // this code makes, and a promise resting on somebody else's default is one version bump
        // from being broken quietly.
        field.restoreOriginalTextOnEscape = true;

        renamingId = a.id;
        renameField = field;
        renameHiddenCaption = button.gameObject;
        renameHiddenCaption.SetActive(false);

        // One listener, not onSubmit plus onDeselect. onEndEdit covers both ways an edit can
        // finish - ENTER and losing focus - and it is the only one that also tells us WHICH,
        // through wasCanceled, so ESC can abandon rather than commit.
        field.onEndEdit.AddListener(_ => HandleEndEdit());

        field.Select();
        field.ActivateInputField();
        field.caretPosition = field.text.Length;
    }

    // TMP raises onEndEdit for both ways an edit finishes, and from INSIDE its own deselect
    // handling - so the EventSystem is already mid-selection-change when this runs. That is what
    // insideEndEditCallback is for: touching the selection again from here logs "Attempting to
    // select while already selecting" and does nothing. Same guard, for the same reason, as
    // GroupNameInlineEditAuthority.HandleEndEdit.
    private bool insideEndEditCallback;

    void HandleEndEdit()
    {
        insideEndEditCallback = true;

        bool cancelled = renameField != null && renameField.wasCanceled;
        if (cancelled)
        {
            CancelRename();
        }
        else
        {
            CommitRename();
        }

        insideEndEditCallback = false;
    }

    // ENTER, or clicking away. Empty is a legitimate answer and means "no name" - which is how a
    // POST is un-renamed back to its number.
    void CommitRename()
    {
        if (renamingId < 0) return;

        // A commit with no field to read is not a commit. The row can be destroyed out from
        // under an open rename by half a dozen paths, and reading "" off a dead reference and
        // writing it to the affector would ERASE a name that was already saved - the user having
        // done nothing but load a project with the box open. Nothing to read means nothing to
        // write; the sweep in Update catches the state either way.
        if (renameField == null)
        {
            CancelRename();
            return;
        }

        int id = renamingId;
        string typed = renameField.text;

        // Cleared FIRST. The teardown below can re-enter through the listener - deactivating a
        // focused field ends the edit - and re-entering with the state still set would commit
        // twice and destroy an object that is already going.
        renamingId = -1;

        PostAffector affector;
        int number;
        if (TryFindAffector(id, out affector, out number)) affector.label = NormaliseLabel(typed);

        EndRename();

        // Puts the caption back to whatever the commit just decided, name or number.
        PaintAffectorRows();
    }

    void CancelRename()
    {
        if (renamingId < 0) return;
        renamingId = -1;
        EndRename();
        PaintAffectorRows();
    }

    void EndRename()
    {
        if (renameField != null)
        {
            renameField.onEndEdit.RemoveAllListeners();

            // Only from outside TMP's own callback. Inside it the EventSystem is already moving
            // the selection somewhere else and has guarded itself against exactly this.
            if (!insideEndEditCallback
                && EventSystem.current != null
                && EventSystem.current.currentSelectedGameObject == renameField.gameObject)
            {
                EventSystem.current.SetSelectedGameObject(null);
            }

            Destroy(renameField.gameObject);
            renameField = null;
        }

        if (renameHiddenCaption != null)
        {
            renameHiddenCaption.SetActive(true);
            renameHiddenCaption = null;
        }
    }

    RectTransform FindRow(PostAffector a)
    {
        string wanted = RowName(a.groupId, a.id);
        foreach (RectTransform row in FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (row != null && row.name == wanted) return row;
        }
        return null;
    }

    // ---- names ---------------------------------------------------------------------------

    public const int LabelCharacterLimit = 6;

    // Six characters is the whole design of this: it is what fits the row's button at the size
    // the button already is, so a named POST cannot push the WEIGHT slider or DEL off the end of
    // the row. The field refuses the seventh character rather than accepting it and eliding it,
    // so what you type is what you get.
    static string NormaliseLabel(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";

        // Angle brackets and control characters go first. The row caption is an ordinary TMP
        // label with rich text ON, so "<b>Hi" would render as a bold "Hi" on the row and as the
        // literal five characters in the edit box - the same reason GroupNameInlineEditAuthority
        // sanitises group names. Stripped before the length check, so what survives is six real
        // characters rather than six minus whatever was thrown away.
        StringBuilder builder = new StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c == '<' || c == '>') continue;
            if (char.IsControl(c)) continue;
            builder.Append(c);
        }

        string trimmed = builder.ToString().Trim();
        if (trimmed.Length > LabelCharacterLimit)
        {
            trimmed = trimmed.Substring(0, LabelCharacterLimit);
        }
        return trimmed;
    }

    // What the row's button says: the user's name when there is one, and otherwise the ordinal
    // it has always shown. The ordinal is positional, so deleting POST 1 renumbers the rest -
    // which is exactly the ambiguity a name is there to remove.
    static string DisplayLabel(PostAffector a, int number)
    {
        if (a != null && !string.IsNullOrEmpty(a.label)) return a.label;
        return "POST " + number;
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

    static ControlState Lerp(ControlState a, ControlState b, float t) => new ControlState
    {
        length = Mathf.Lerp(a.length, b.length, t), width = Mathf.Lerp(a.width, b.width, t),
        segments = Mathf.Lerp(a.segments, b.segments, t),
        bend = Mathf.Lerp(a.bend, b.bend, t), twist = Mathf.Lerp(a.twist, b.twist, t),
        depth = Mathf.Lerp(a.depth, b.depth, t),
        x = Mathf.Lerp(a.x, b.x, t), y = Mathf.Lerp(a.y, b.y, t), z = Mathf.Lerp(a.z, b.z, t),
        uScale = Mathf.Lerp(a.uScale, b.uScale, t), vScale = Mathf.Lerp(a.vScale, b.vScale, t),
        uOffset = Mathf.Lerp(a.uOffset, b.uOffset, t), vOffset = Mathf.Lerp(a.vOffset, b.vOffset, t),
        curlFrequency = Mathf.Lerp(a.curlFrequency, b.curlFrequency, t),
        curlDiameter = Mathf.Lerp(a.curlDiameter, b.curlDiameter, t),
        waveAmplitude = Mathf.Lerp(a.waveAmplitude, b.waveAmplitude, t),
        waveFrequency = Mathf.Lerp(a.waveFrequency, b.waveFrequency, t),
        waveDirection = Mathf.Lerp(a.waveDirection, b.waveDirection, t),
        arch = Mathf.Lerp(a.arch, b.arch, t)
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
                absolute = a.absolute,
                label = a.label,
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

        // Whatever was being typed is going with the project that is being torn down. Cancel
        // rather than commit: there is nothing left to commit it onto.
        CancelRename();

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
                // Absent from every project saved before this existed, and a missing bool
                // deserializes to false - which is RELATIVE, which is what those projects were.
                absolute = d.absolute,
                // Null rather than empty in a project written before the field existed, and
                // every reader here treats both the same - but normalise once, at the door.
                label = NormaliseLabel(d.label),
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


// Double-click on a POST row's button, for the rename. Separate from CustomClickDetector in
// ModelViewer, which only carries right-click and is bound to group rows.
//
// The Button's own onClick still runs on the first click and selects the POST. That is the same
// thing a group row does when you double-click its name to rename it, so the gesture behaves the
// way the one it is copied from behaves.
public class PostRowDoubleClick : MonoBehaviour, IPointerClickHandler
{
    public System.Action onDoubleClick;

    public void OnPointerClick(PointerEventData eventData)
    {
        if (eventData.button != PointerEventData.InputButton.Left) return;
        if (eventData.clickCount < 2) return;
        if (onDoubleClick != null) onDoubleClick();
    }
}
