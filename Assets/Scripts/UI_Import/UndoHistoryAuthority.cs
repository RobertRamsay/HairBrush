using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

// Session undo and redo. CTRL+Z steps back, CTRL+Y or CTRL+SHIFT+Z steps forward, up to a
// hundred steps. Nothing here is written to a project file: the history is the current
// session's, and opening or importing anything starts it over.
//
// WHY SNAPSHOTS RATHER THAN COMMANDS
//
// The usual way to build undo is to have every action record how to reverse itself. That is
// the wrong shape for this codebase. Session state is spread across forty-odd authorities
// that each mutate it independently - painting, sliders, profile curves, variance, POST,
// CLUMPER, GUIDE, UV rects, materials, group naming - and a command per action means a hook
// in every one of them, plus a new hook every time a feature is added. The first modifier
// that forgot to record itself would leave undo quietly lying about what it had restored.
//
// The project already knows how to write the whole session down and read it back: that is
// SAVE PROJ. A snapshot here is exactly the payload a save writes, so undo covers precisely
// what a save covers, by construction. A new feature becomes undoable the moment it becomes
// saveable, with nothing added here.
//
// WHAT IT COSTS, AND WHY THAT IS ACCEPTABLE
//
// A snapshot is the save JSON, gzipped. JSON of mostly-similar floats compresses hard, so a
// heavy groom lands in the low hundreds of kilobytes and a hundred of them fit in a budget
// smaller than one uncompressed copy. Snapshots are taken after an action finishes and the
// input goes quiet, never per frame, so the cost falls in the pause after a brush stroke
// rather than during it.
//
// WHAT COUNTS AS ONE STEP
//
// An action, not a change. The trigger is input - a mouse release, a key - followed by a
// quiet period. A paint drag is therefore one step however many cards it lays down, a slider
// drag is one step however far it travels, and a burst of clicks inside the quiet window
// coalesces. Restoring is done by replaying the snapshot through the same three methods a
// project load uses, minus the model reload and the panel teardown, so an undo does not
// re-import the OBJ or flash the panels.
[DefaultExecutionOrder(9600)]
public class UndoHistoryAuthority : MonoBehaviour
{
    public const int MaxSteps = 100;

    // A ceiling on the compressed history as well as on the count, because the count alone is
    // not a memory bound: a hundred steps of a five-card test and a hundred steps of a finished
    // groom are three orders of magnitude apart.
    private const long MaxHistoryBytes = 96L * 1024L * 1024L;

    // How long the input has to stay quiet before the action is considered finished. Long
    // enough that a slider nudged twice in quick succession is one step, short enough that it
    // has always happened by the time anyone reaches for CTRL+Z.
    private const float QuietSeconds = .3f;

    // A snapshot cannot be taken while a restore is still landing: the bridges that rebuild
    // POST, CLUMPER, GUIDE and canonical card state settle over several frames, and a capture
    // in the middle of that would record a half-restored session as if the user had authored it.
    private const float SettleTimeout = 8f;

    private sealed class Step
    {
        public byte[] payload;
        public ulong hash;

        // Hashed separately from the payload so a step that leaves the materials alone - which
        // is nearly every step - can switch the material restore off. That restore re-reads
        // every texture off disk and rebuilds it, and it does not destroy the textures it
        // replaces, so running it a hundred times is a hundred times the file I/O and a leak
        // of every texture but the last.
        public ulong materialHash;
    }

    private readonly List<Step> undoSteps = new List<Step>();
    private readonly List<Step> redoSteps = new List<Step>();
    private Step baseline;
    private long historyBytes;

    private ModelViewer viewer;
    private RuntimeNavigationProjectIO io;
    private GameObject lastModel;

    private bool armed;
    private float armedAt;
    private bool restoring;
    private bool baselinePending;

    // Set by RuntimeNavigationProjectIO when it opens a project or imports a model. Watching
    // ModelViewer.loadedModel for a change is not enough on its own: a project whose modelPath
    // is empty replaces the entire session without touching the model object at all, and the
    // history would survive into content it does not describe.
    private static bool sessionReplaced;

    public static void NotifySessionReplaced()
    {
        sessionReplaced = true;
    }

    // True while a step is being replayed. Read by NewGroupRootSelectionAuthority, which
    // otherwise reads a group id reappearing in allGroupIds as a fresh + GROUP and resets
    // everything this step has just restored to it.
    public static bool Restoring { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<UndoHistoryAuthority>() != null) return;
        GameObject go = new GameObject(nameof(UndoHistoryAuthority));
        DontDestroyOnLoad(go);
        go.AddComponent<UndoHistoryAuthority>();
    }

    void Awake()
    {
        undoSteps.Clear();
        redoSteps.Clear();
        baseline = null;
        historyBytes = 0;
        viewer = null;
        io = null;
        lastModel = null;
        armed = false;
        armedAt = 0f;
        restoring = false;
        Restoring = false;
    }

    void Update()
    {
        if (!Resolve()) return;

        // Loading a model or a project replaces the session outright. Its history belongs to a
        // session that no longer exists, and stepping back into it would drop the old project's
        // cards onto the new one.
        GameObject model = CurrentModel();
        if (model != lastModel || sessionReplaced)
        {
            lastModel = model;
            sessionReplaced = false;
            Clear();

            // NOT captured here, and not on the next frame either. A load runs start to finish
            // inside the click that triggered it, so right now the scene still holds the outgoing
            // project's cards - Destroy is deferred to the end of the frame - and the incoming
            // one has not settled: the loader re-applies each group's variance over the cards
            // immediately and CanonicalProjectStateBridge only puts the real per-card values back
            // two or more frames later. A baseline taken from either moment is a picture of the
            // wrong session, and it is the state the first CTRL+Z would return to.
            baselinePending = true;
            StartCoroutine(CaptureBaselineWhenSettled());
            return;
        }

        if (restoring || baselinePending) return;

        // Not gated on the model. A project whose OBJ has moved loads its cards, groups and
        // modifiers and leaves loadedModel null; the session is real and editable, so it gets
        // a history like any other. Capture returns null when there is genuinely nothing yet.
        if (baseline == null && model == null && FindFirstObjectByType<HairCard>() == null) return;

        HandleHotkeys();
        MaintainCapture();
    }

    IEnumerator CaptureBaselineWhenSettled()
    {
        float deadline = Time.unscaledTime + SettleTimeout;
        CanonicalProjectStateBridge canonical = FindFirstObjectByType<CanonicalProjectStateBridge>();
        while (Time.unscaledTime < deadline &&
               (HairProjectSaveData.PendingModifierRestore != null ||
                CanonicalProjectStateBridge.PendingCanonicalRestore != null ||
                (canonical != null && canonical.HasPendingRestore)))
        {
            yield return null;
        }

        yield return null;

        baseline = Capture();
        baselinePending = false;
    }

    bool Resolve()
    {
        if (viewer == null) viewer = FindFirstObjectByType<ModelViewer>();
        if (io == null) io = FindFirstObjectByType<RuntimeNavigationProjectIO>();
        return viewer != null && io != null;
    }

    GameObject CurrentModel()
    {
        System.Reflection.FieldInfo field = typeof(ModelViewer).GetField("loadedModel",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return field?.GetValue(viewer) as GameObject;
    }

    void Clear()
    {
        undoSteps.Clear();
        redoSteps.Clear();
        baseline = null;
        historyBytes = 0;
        armed = false;
        baselinePending = false;

        // A restore in flight belongs to the session being discarded. Its tail would resume
        // after the new content had loaded and select a group id from the old snapshot, on the
        // new project. The file dialog blocks the main thread, so this is not a narrow window:
        // a restore frozen mid-settle by LOAD PROJ resumes into a session it knows nothing about.
        StopAllCoroutines();
        restoring = false;
        Restoring = false;
    }

    // ------------------------------------------------------------------------------ hotkeys

    void HandleHotkeys()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null) return;
        if (!keyboard.ctrlKey.isPressed) return;

        // A group name being typed owns the keyboard. CTRL+Z inside a rename is the text
        // field's business, and GroupNameInlineEditAuthority already answers that question for
        // everything else that reads keys.
        if (GroupNameInlineEditAuthority.IsEnteringText) return;

        bool undoPressed = keyboard.zKey.wasPressedThisFrame && !keyboard.shiftKey.isPressed;
        bool redoPressed = keyboard.yKey.wasPressedThisFrame ||
                           (keyboard.zKey.wasPressedThisFrame && keyboard.shiftKey.isPressed);
        if (!undoPressed && !redoPressed) return;

        // The action being undone may not have been captured yet: the quiet timer is 300ms and
        // "no, undo that" is a faster reflex than that. Without this flush the step would be
        // skipped over entirely - CTRL+Z would jump two actions back and the one the user was
        // reacting to would be unreachable in either direction.
        if (armed)
        {
            armed = false;
            CommitIfChanged();
        }

        if (undoPressed) Undo();
        else Redo();
    }

    public void Undo()
    {
        if (undoSteps.Count == 0 || baseline == null)
        {
            StatusToast.Show("Nothing left to undo.");
            return;
        }

        Step target = undoSteps[undoSteps.Count - 1];
        HairProjectSaveData data = Decode(target);
        if (data == null) return;

        undoSteps.RemoveAt(undoSteps.Count - 1);
        historyBytes -= target.payload.Length;

        redoSteps.Add(baseline);
        historyBytes += baseline.payload.Length;

        Apply(target, data, "Undo");
    }

    public void Redo()
    {
        if (redoSteps.Count == 0 || baseline == null)
        {
            StatusToast.Show("Nothing left to redo.");
            return;
        }

        Step target = redoSteps[redoSteps.Count - 1];
        HairProjectSaveData data = Decode(target);
        if (data == null) return;

        redoSteps.RemoveAt(redoSteps.Count - 1);
        historyBytes -= target.payload.Length;

        undoSteps.Add(baseline);
        historyBytes += baseline.payload.Length;

        Apply(target, data, "Redo");
    }

    // Decoded before either stack is touched. Failing half way through leaves a step popped and
    // nothing restored, which loses history silently - the one outcome an undo system must not
    // have. A payload that cannot be read means the history is not trustworthy, so it goes.
    HairProjectSaveData Decode(Step step)
    {
        string json = null;
        HairProjectSaveData data = null;
        try
        {
            json = Decompress(step.payload);
            // Inside the same guard as the decompress. JsonUtility throws on malformed input,
            // and an exception escaping here would leave the history uncleared and unusable
            // while every later CTRL+Z threw again in the same place.
            if (!string.IsNullOrEmpty(json)) data = JsonUtility.FromJson<HairProjectSaveData>(json);
        }
        catch (System.Exception error)
        {
            Debug.LogWarning("HairBrush: an undo snapshot could not be read - " + error.Message);
            data = null;
        }

        if (data == null)
        {
            Clear();
            baseline = Capture();
            StatusToast.Show("Undo history was damaged and has been cleared.", true);
        }

        return data;
    }

    // -------------------------------------------------------------------------- capturing

    // Arming is driven by input rather than by watching the session for changes. An undo step
    // is meant to be one thing the user did, and the user is the only reliable witness to where
    // one action ends and the next begins; a state watcher has to guess, and guesses wrong on
    // exactly the cases that matter, splitting a brush stroke into hundreds of steps.
    //
    // Camera work is deliberately not armed. Orbiting and zooming change nothing that is saved,
    // and arming on them would spend a full capture proving that after every look around.
    void MaintainCapture()
    {
        Mouse mouse = Mouse.current;
        Keyboard keyboard = Keyboard.current;

        bool activity = false;
        if (mouse != null && mouse.leftButton.wasReleasedThisFrame) activity = true;
        if (!activity && keyboard != null && !keyboard.ctrlKey.isPressed) activity = AnyKeyWentDown(keyboard);

        if (activity)
        {
            armed = true;
            armedAt = Time.unscaledTime;
        }

        if (!armed) return;

        // Still mid-gesture. A brush stroke holds the button down across many frames and a
        // capture inside it would record the half-painted state as a step of its own.
        if (mouse != null && (mouse.leftButton.isPressed || mouse.rightButton.isPressed || mouse.middleButton.isPressed)) return;
        if (Time.unscaledTime - armedAt < QuietSeconds) return;

        armed = false;
        CommitIfChanged();
    }

    // Per key, not keyboard.anyKey. anyKey reads "some key is down", so its wasPressedThisFrame
    // is the edge from no keys to any key - and every gesture in this app that presses a key
    // while ALT, SHIFT, TAB or SPACE is already held would therefore never arm. Flipping a
    // group single-sided with 1 while orbiting on ALT is a saved change that would get no step
    // of its own and be folded silently into whatever the user did next.
    static bool AnyKeyWentDown(Keyboard keyboard)
    {
        foreach (KeyControl key in keyboard.allKeys)
        {
            if (key != null && key.wasPressedThisFrame) return true;
        }
        return false;
    }

    void CommitIfChanged()
    {
        Step current = Capture();
        if (current == null) return;

        if (baseline == null)
        {
            baseline = current;
            return;
        }

        // Nothing the save format can see actually moved. A click on empty space, a mode key,
        // a cancelled gesture: real input, no step.
        if (current.hash == baseline.hash) return;

        undoSteps.Add(baseline);
        historyBytes += baseline.payload.Length;
        baseline = current;

        // A new action invalidates the forward history, the same as every other editor.
        foreach (Step step in redoSteps) historyBytes -= step.payload.Length;
        redoSteps.Clear();

        Trim();
    }

    // historyBytes counts BOTH stacks, so trimming has to be able to reach both. Undoing a full
    // history moves every step into redo; a trim that only ever looked at undoSteps would then
    // find nothing to drop and hold the whole budget indefinitely. The forward history is the
    // first to go, because it is the half the user has already stepped away from.
    void Trim()
    {
        while (redoSteps.Count > MaxSteps ||
               (historyBytes > MaxHistoryBytes && redoSteps.Count > 0))
        {
            historyBytes -= redoSteps[0].payload.Length;
            redoSteps.RemoveAt(0);
        }

        while (undoSteps.Count > MaxSteps ||
               (historyBytes > MaxHistoryBytes && undoSteps.Count > 1))
        {
            historyBytes -= undoSteps[0].payload.Length;
            undoSteps.RemoveAt(0);
        }
    }

    Step Capture()
    {
        if (io == null) return null;

        HairProjectSaveData data = io.BuildSaveData();
        if (data == null) return null;

        // Not pretty-printed. The indentation is a third of the bytes and nobody reads a
        // snapshot.
        string json = JsonUtility.ToJson(data, false);
        if (string.IsNullOrEmpty(json)) return null;

        Step step = new Step();
        step.hash = Hash(json);
        step.materialHash = HashMaterials(data);
        step.payload = Compress(json);
        return step;
    }

    // Just the material block: the material list, the per-group assignments and the UV rects.
    // Compared between two steps to decide whether the material restore has anything to do.
    static ulong HashMaterials(HairProjectSaveData data)
    {
        ulong hash = 14695981039346656037UL;
        hash = Fold(hash, data.hairMaterials != null ? data.hairMaterials.Count : 0);

        if (data.hairMaterials != null)
        {
            foreach (HairMaterialSaveData material in data.hairMaterials)
            {
                if (material == null) continue;
                hash = Fold(hash, Hash(material.name ?? string.Empty));
                hash = Fold(hash, Hash(material.albedoPath ?? string.Empty));
                hash = Fold(hash, Hash(material.normalPath ?? string.Empty));
                hash = Fold(hash, Hash(material.opacityPath ?? string.Empty));
                // Smoothness and Metallic are sliders on the material panel and the only thing
                // that puts them back is the very restore this hash decides whether to skip.
                hash = Fold(hash, material.smooth.GetHashCode());
                hash = Fold(hash, material.metal.GetHashCode());
            }
        }

        if (data.groupMaterials != null)
        {
            foreach (GroupMaterialSaveData assignment in data.groupMaterials)
            {
                if (assignment == null) continue;
                hash = Fold(hash, (ulong)assignment.groupId);
                hash = Fold(hash, (ulong)assignment.materialIndex);
            }
        }

        hash = Fold(hash, data.uvRects != null ? data.uvRects.Count : 0);
        return hash;
    }

    static ulong Fold(ulong hash, ulong value)
    {
        hash ^= value;
        hash *= 1099511628211UL;
        return hash;
    }

    static ulong Fold(ulong hash, int value)
    {
        return Fold(hash, (ulong)(uint)value);
    }

    // ------------------------------------------------------------------------- restoring

    // The parse happens in Decode, before the stacks move. It is a parse rather than a direct
    // hand-off of the payload because FromJson is what arms every restore bridge in the project:
    // canonical card state, POST-local variance, POST curves, CLUMPER, GUIDE, materials, UV
    // rects. Those are the same bridges a project load leans on.
    void Apply(Step step, HairProjectSaveData data, string label)
    {
        // Materials are the one restore that is far too expensive to run per step: it re-reads
        // every texture off disk, rebuilds it, and abandons the one it replaced without
        // destroying it. Almost no step touches them, and a step that does not can simply
        // withdraw the request the parse just queued.
        if (baseline != null && step.materialHash == baseline.materialHash)
        {
            MaterialProjectPersistenceBridge.PendingRestore = null;
        }

        baseline = step;
        restoring = true;
        Restoring = true;
        armed = false;

        // Told before the work starts, not after. The settle can take several frames, and a
        // confirmation that arrives at the end reads as lag on an action the user has already
        // stopped waiting for.
        StatusToast.Show(label + " (" + undoSteps.Count + " back, " + redoSteps.Count + " forward)");
        StartCoroutine(ApplyRoutine(data, label));
    }

    IEnumerator ApplyRoutine(HairProjectSaveData data, string label)
    {
        int previousGroup = viewer != null ? viewer.currentGroupId : 0;

        // try/finally, because Unity abandons a coroutine on an unhandled exception without
        // unwinding it. Left set, `restoring` kills undo for the rest of the session and
        // `Restoring` is worse: NewGroupRootSelectionAuthority reads it, so every later
        // + GROUP would skip its fresh-root setup and come up in whatever modifier context
        // happened to be active. yield inside a try with only a finally is legal C#.
        try
        {

            foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
            {
                if (card != null) Destroy(card.gameObject);
            }

            // Destroy is deferred to the end of the frame, so for the rest of this one the outgoing
            // cards are still findable. Spawning the replacements now would leave twice the expected
            // count in the scene, and every restore bridge decides it is ready by counting cards.
            // They happen to look on a later frame today, purely because their execution orders are
            // all below this component's - which is not a thing to rely on. One frame costs nothing.
            yield return null;

            io.ApplyGlobalSliders(data);
            io.ApplyGroupRegistry(data);

            // The panels are deliberately NOT torn down and rebuilt the way a project load does it.
            // Nothing about the model or the canvas has changed, the group rows are rebuilt from the
            // registry by GroupRegistryFromCardsAuthority, and every modifier row is rebuilt from its
            // own manager - so a teardown would buy a full panel flash on every CTRL+Z and nothing
            // else. The one thing that does have to be re-established is the selected group, below.
            io.SpawnSavedCards(data);

            // Fresh cards come up with their renderers on. SOLO decides that, and it is the only
            // thing that does, so a respawn behind its back leaves hidden groups visible while
            // their SOLO buttons still read as lit and their evaluators still skip them as frozen.
            // NOT ResetSoloState, which is what a project load calls: SOLO is session state and an
            // undo is a step inside the session, not a new one.
            ForgetSoloForMissingGroups(data);
            GroupSoloVisibilityAuthority.ApplyVisibility();

            // Roots cached from the state being replaced. Forgotten BEFORE the modifier restore,
            // because restoring a group's variance immediately varies its cards around whatever base
            // is on hand, and that base is read from these.
            GroomRootStateAuthority rootState = FindFirstObjectByType<GroomRootStateAuthority>();
            if (rootState != null) rootState.ForgetStoredRoots();

            RestoreModifiers(data);

            // Wait the bridges out before letting anything be captured again, and before reading the
            // group's settings back onto the sliders. Same wait the project loader performs, and for
            // the same reason: in the meantime the cards hold intermediate values.
            float deadline = Time.unscaledTime + SettleTimeout;
            CanonicalProjectStateBridge canonical = FindFirstObjectByType<CanonicalProjectStateBridge>();
            while (Time.unscaledTime < deadline &&
                   (CanonicalProjectStateBridge.PendingCanonicalRestore != null ||
                    (canonical != null && canonical.HasPendingRestore)))
            {
                yield return null;
            }

            yield return null;

            SelectGroupAfterRestore(data, previousGroup);

            // Re-read rather than trusting the snapshot to have reproduced itself exactly. If the
            // restore lands even slightly off, adopting what is actually there stops the difference
            // being pushed as a phantom undo step the moment the user does something else.
            Step settled = Capture();
            if (settled != null) baseline = settled;
        }
        finally
        {
            restoring = false;
            Restoring = false;
        }
    }

    // Clear everything, then re-import what the step contains. Per-group importing alone is not
    // enough: ImportGroup only clears the group it is handed and ImportGroupSettings only
    // overwrites the channels listed in its payload, so a group the step removes keeps its
    // POSTs and its variance sitting in the managers. Group ids are handed straight back out,
    // so the next + GROUP would inherit a modifier stack nobody created.
    //
    // This is deliberately the same clear-then-restore ModifierPersistenceBridge performs when
    // it drains PendingModifierRestore, done here and now rather than waiting on its 0.2s poll -
    // and PendingModifierRestore is taken as it goes, because several other bridges hold off
    // until that field is null and there is no reason to make them wait for a second pass over
    // work already finished.
    void RestoreModifiers(HairProjectSaveData data)
    {
        ModifierPersistenceBridge modifiers = FindFirstObjectByType<ModifierPersistenceBridge>();
        GroomVarianceController variance = FindFirstObjectByType<GroomVarianceController>();
        PostAffectorManager posts = FindFirstObjectByType<PostAffectorManager>();
        if (modifiers == null || variance == null || posts == null) return;

        HairProjectSaveData.PendingModifierRestore = null;
        variance.ClearSavedSettings();
        posts.ClearAll();

        if (data.groups == null) return;
        foreach (GroupSaveData group in data.groups)
        {
            if (group != null) modifiers.RestoreGroup(group);
        }
    }

    // Soloing is session state and is deliberately not part of a snapshot, so stepping back over
    // the creation of a group can leave that group soloed and gone. IsGroupVisible then answers
    // false for everything that IS still there, and the SOLO button that would clear it went
    // with the row - an invisible groom and no control to bring it back.
    void ForgetSoloForMissingGroups(HairProjectSaveData data)
    {
        if (!GroupSoloVisibilityAuthority.AnySolo) return;

        HashSet<int> live = new HashSet<int>();
        if (data.groups != null)
        {
            foreach (GroupSaveData group in data.groups)
            {
                if (group != null) live.Add(group.groupId);
            }
        }

        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
        {
            if (card != null) live.Add(card.groupId);
        }

        foreach (int gid in GroupSoloVisibilityAuthority.SoloedGroups())
        {
            if (!live.Contains(gid)) GroupSoloVisibilityAuthority.Forget(gid);
        }
    }

    // The group that was selected before the step is kept if it still exists, so stepping back
    // and forward does not wander around the panel. SelectGroup rather than assigning the id,
    // because only SelectGroup resyncs the shape sliders to that group's own cards.
    void SelectGroupAfterRestore(HairProjectSaveData data, int preferredGroup)
    {
        if (viewer == null) return;

        // Forgotten a SECOND time, right before SelectGroup, exactly as the project loader does.
        // GroomRootStateAuthority runs at order -1100 and re-seeds a group's root from
        // viewer.current* on the very next frame after the first forget - and at that moment
        // current* holds the snapshot's single global slider block, not the group's own numbers.
        // SyncShapeSlidersToGroupRoot prefers a stored root, so without this the group is
        // recovered from that block instead of from its own cards: the hair comes back curly and
        // the curl sliders read zero, and the next card placed is dead straight.
        GroomRootStateAuthority rootState = FindFirstObjectByType<GroomRootStateAuthority>();
        if (rootState != null) rootState.ForgetStoredRoots();

        int target = preferredGroup;
        bool exists = false;
        if (data.groups != null)
        {
            foreach (GroupSaveData group in data.groups)
            {
                if (group != null && group.groupId == target) exists = true;
            }
            if (!exists && data.groups.Count > 0) target = data.groups[0].groupId;
        }

        System.Reflection.MethodInfo select = typeof(ModelViewer).GetMethod("SelectGroup",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (select != null)
        {
            select.Invoke(viewer, new object[] { target });
            CancelGroupFlash();
        }
        else
        {
            // Same fallback the project loader uses. Assigning the id alone skips the slider
            // resync, which is the whole reason SelectGroup is called rather than the field set.
            viewer.currentGroupId = target;
            viewer.SyncShapeSlidersToGroupRoot(target);
        }
    }

    // SelectGroup ends by starting the "which group is this?" flash, which hides every other
    // group for half a second. That is the right thing when a person clicks a group row and
    // exactly the wrong thing on every CTRL+Z - the whole point of not tearing the panels down
    // was that a step should not make the screen jump.
    void CancelGroupFlash()
    {
        System.Reflection.FieldInfo field = typeof(ModelViewer).GetField("flashGroupCoroutine",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (field == null) return;

        if (field.GetValue(viewer) is Coroutine running)
        {
            viewer.StopCoroutine(running);
            field.SetValue(viewer, null);
        }

        // The flash disables the other groups' renderers up front and only re-enables them when
        // it finishes. Stopped part way, it leaves them off, so visibility is handed back to the
        // one component that owns it.
        GroupSoloVisibilityAuthority.ApplyVisibility();
    }

    // -------------------------------------------------------------------------- plumbing

    static byte[] Compress(string json)
    {
        byte[] raw = Encoding.UTF8.GetBytes(json);
        using (MemoryStream output = new MemoryStream())
        {
            using (GZipStream gzip = new GZipStream(output, CompressionMode.Compress, true))
            {
                gzip.Write(raw, 0, raw.Length);
            }
            return output.ToArray();
        }
    }

    static string Decompress(byte[] payload)
    {
        if (payload == null || payload.Length == 0) return null;
        using (MemoryStream input = new MemoryStream(payload))
        using (GZipStream gzip = new GZipStream(input, CompressionMode.Decompress))
        using (MemoryStream output = new MemoryStream())
        {
            gzip.CopyTo(output);
            return Encoding.UTF8.GetString(output.ToArray());
        }
    }

    // FNV-1a over the JSON, so "did anything change" is one pass over a string already in hand
    // rather than a second compress-and-compare. Only ever tested for equality against another
    // snapshot of the same session, never stored or trusted across runs.
    static ulong Hash(string text)
    {
        ulong hash = 14695981039346656037UL;
        for (int i = 0; i < text.Length; i++)
        {
            hash ^= text[i];
            hash *= 1099511628211UL;
        }
        return hash;
    }
}
