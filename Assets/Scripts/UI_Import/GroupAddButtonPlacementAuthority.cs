using System;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// V017 - "+POST / +CLUMPER / +GUIDE" add buttons beneath every Hair Group row.
//
// The keyboard gestures are untouched and still work exactly as before:
//     CTRL + click  = place a POST
//     TAB   + click = place a CLUMPER
//     SPACE + click = reposition
//
// This adds a discoverable second route. Click an add button under a group and the tool
// enters a one-shot placement mode: the group becomes current, a line across the bottom
// of the viewport says what to do, and the next left-click on the model places the thing.
// Right-click or ESC cancels.
//
// While the mode is armed, grooming input is switched off the same way
// ModifierGestureReservation switches it off for TAB/SPACE. That is what stops the
// placement click from ALSO spawning a hair card, and it stops the brush preview ring
// from following the cursor during a modal placement. The previous grooming state is
// captured on arm and restored on disarm, so an armed-then-cancelled placement leaves
// the tool exactly where it started.
//
// Execution order -6000 puts this component's LateUpdate ahead of GroupAddRowUIAuthority
// (10500), so the button lights up in the same frame it is armed. The suppression itself
// does not depend on the order, because it is held for the whole armed period rather than
// being applied per click.
//
// All three buttons place a real modifier. GUIDE was a placeholder in the first cut of this
// file; GuideCurveManager now owns guide curves properly, so +GUIDE creates one.
[DefaultExecutionOrder(-6000)]
public class GroupAddButtonPlacementAuthority : MonoBehaviour
{
    public enum AddKind
    {
        None = 0,
        Post = 1,
        Clumper = 2,
        Guide = 3
    }

    private const string BannerName = "GroupAddPlacementPrompt";
    private const float BottomInset = 42f;
    private const float BannerHeight = 26f;
    private const float BannerFontSize = 16f;
    private const float ShadowOffset = 1.5f;

    // Read by GroupAddRowUIAuthority so the armed button can light up.
    private static AddKind armedKind;
    private static int armedGroupId;

    public static AddKind ArmedKind
    {
        get { return armedKind; }
    }

    public static int ArmedGroupId
    {
        get { return armedGroupId; }
    }

    private ModelViewer viewer;
    private PostAffectorManager posts;
    private GroupClumperManager clumpers;
    private GuideCurveManager guides;

    private FieldInfo postLastCreatedFrameField;
    private MethodInfo enterSelectionModeMethod;
    private MethodInfo selectGroupMethod;
    private MethodInfo createAffectorMethod;

    private const string LockOwner = "AddButtonPlacement";
    private bool restorePending;
    private int restoreRequestedFrame;
    private int armedFrame;

    private GameObject bannerObject;
    private TextMeshProUGUI bannerLabel;
    private TextMeshProUGUI bannerShadow;
    private Canvas boundCanvas;
    private string lastBannerText;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<GroupAddButtonPlacementAuthority>() != null) return;
        GameObject go = new GameObject("GroupAddButtonPlacementAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<GroupAddButtonPlacementAuthority>();
    }

    void Awake()
    {
        armedKind = AddKind.None;
        armedGroupId = -1;

        viewer = null;
        posts = null;
        clumpers = null;

        postLastCreatedFrameField = null;
        enterSelectionModeMethod = null;
        selectGroupMethod = null;
        createAffectorMethod = null;

        restorePending = false;
        restoreRequestedFrame = -1;
        armedFrame = -1;

        bannerObject = null;
        bannerLabel = null;
        bannerShadow = null;
        boundCanvas = null;
        lastBannerText = string.Empty;
    }

    // Called by the row buttons.
    public static void Arm(AddKind kind, int groupId)
    {
        GroupAddButtonPlacementAuthority instance = FindFirstObjectByType<GroupAddButtonPlacementAuthority>();
        if (instance == null) return;
        instance.BeginPlacement(kind, groupId);
    }

    public static void CancelArmed()
    {
        GroupAddButtonPlacementAuthority instance = FindFirstObjectByType<GroupAddButtonPlacementAuthority>();
        if (instance == null) return;
        instance.Disarm();
    }

    void BeginPlacement(AddKind kind, int groupId)
    {
        Resolve();
        if (viewer == null) return;
        if (kind == AddKind.None) return;

        // Re-arming before the deferred restore has run: the captured state from the first
        // arm is still the true one, so take the suppression back over rather than
        // re-reading a value that is currently switched off.
        // GroomingInputLock owns the captured state now, and only the FIRST holder captures it -
        // so re-arming, or arming while a GUIDE is being shaped, cannot record the already
        // suppressed value and hand card placement back as "off" later.
        restorePending = false;
        GroomingInputLock.Hold(LockOwner, viewer);

        // The add button belongs to a specific group, so that group becomes the working
        // group before anything is placed into it.
        if (viewer.currentGroupId != groupId && selectGroupMethod != null)
        {
            selectGroupMethod.Invoke(viewer, new object[] { groupId });
        }
        viewer.currentGroupId = groupId;

        armedKind = kind;
        armedGroupId = groupId;
        armedFrame = Time.frameCount;
    }

    void Disarm()
    {
        armedKind = AddKind.None;
        armedGroupId = -1;
        armedFrame = -1;

        // Grooming is NOT switched back on here. Paint mode places a card for every frame
        // the left button is held, so restoring the instant the placement click is consumed
        // would paint a trail of cards out of the tail of that same click. The restore
        // waits for the button to come back up, and for at least one frame to pass.
        if (GroomingInputLock.Holds(LockOwner))
        {
            GroomingInputLock.Release(LockOwner);
            restorePending = true;
            restoreRequestedFrame = Time.frameCount;
        }

        SetBannerVisible(false);
    }

    void ServiceDeferredRestore()
    {
        if (!restorePending) return;
        if (Time.frameCount <= restoreRequestedFrame) return;

        bool stillHeld = Mouse.current != null && Mouse.current.leftButton.isPressed;
        if (stillHeld) return;

        // Only actually restores once every holder has let go; if a guide is still being shaped
        // it keeps the lock and this simply stops asking.
        if (GroomingInputLock.TryRestore(viewer)) restorePending = false;
    }

    // Everything happens in LateUpdate, not Update.
    //
    // The obvious place for this is Update at -6000, ahead of the placement authorities.
    // It is the wrong place: EventSystem does its pointer raycast inside its own Update,
    // so EventSystem.IsPointerOverGameObject() read at -6000 answers for LAST frame. Click
    // a panel button on the same frame the cursor arrives over it and a -6000 reader is
    // told the pointer was over the 3D view, which would fire a placement into the model
    // behind the panel. Every LateUpdate runs after EventSystem, so the answer is current.
    //
    // Running late costs nothing here, because the reservation is not per-click: grooming
    // is held off for the whole armed period, from the button press to the placement.
    void LateUpdate()
    {
        Resolve();
        ServiceDeferredRestore();

        if (armedKind == AddKind.None)
        {
            SetBannerVisible(false);
            return;
        }

        if (viewer == null)
        {
            Disarm();
            return;
        }

        // The group the placement belongs to was deleted while the mode was armed.
        if (GameObject.Find("GroupItem_" + armedGroupId) == null)
        {
            Disarm();
            return;
        }

        // Re-asserted every frame rather than once on arm: a project load re-enables
        // grooming from outside this component, and an armed placement must not quietly
        // turn back into a card-painting click halfway through.
        GroomingInputLock.Hold(LockOwner, viewer);

        UpdateBanner();

        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            Disarm();
            return;
        }

        if (Mouse.current == null) return;

        // Right click cancels an armed placement - unless it is the MAYA-NAV DOLLY. Zooming in for
        // a closer look at where the POST should go is not a request to cancel placing it.
        //
        // CameraGestureActive, NOT AltReserved. With MAYA-NAV off, ALT+RMB is the ordinary classic
        // ORBIT and has cancelled an armed placement since the button existed; exempting it there
        // would leave the placement armed after a gesture the user reads as a cancel, grooming
        // locked off behind it, and the next left click planting the thing they thought they had
        // thrown away.
        //
        // The other two camera gestures need no test here: ALT+MMB fails this line's own
        // right-button test, and ALT+LMB reaches IsShortcutModifierHeld further down and is
        // refused there. Of the three camera gestures only the right button can reach a Disarm,
        // so only it needs saying here.
        if (Mouse.current.rightButton.wasPressedThisFrame && !MayaNavigationAuthority.CameraGestureActive)
        {
            Disarm();
            return;
        }

        if (!Mouse.current.leftButton.wasPressedThisFrame) return;

        // The click that pressed the add button itself must not also be read as the
        // placement click.
        if (armedFrame == Time.frameCount) return;

        // A click on the panel is a panel click, not a placement. The mode stays armed so
        // an accidental click on the UI does not silently drop out of placement.
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;

        // The keyboard gestures do not go through this component and do not care that a
        // button placement is in progress: PostAffectorManager.DetectCtrlClick fires on
        // CTRL+click on its own, and the two clumper interaction scripts fire on TAB+click
        // on their own. A modifier resting under a hand during a button placement would
        // therefore create a second object on top of the one being placed. Wait for the
        // modifier to come up rather than placing a duplicate; the mode stays armed.
        if (IsShortcutModifierHeld())
        {
            // A MAYA-NAV camera gesture is not a mistake and gets no toast. ALT is how the user
            // moves the camera there, so telling them to release it while they line up the very
            // shot they want to place into is worse than saying nothing - the placement stays
            // armed either way, and the toast would fire on every tumble.
            //
            // CameraGestureActive, NOT AltReserved. Written against AltReserved this return fires
            // whenever ALT is down in EITHER mode, which makes the toast below unreachable while
            // ALT is held - so it could only ever name a key the user is guaranteed not to be
            // holding, and the MAYA-NAV-off user with an armed placement and ALT down out of old
            // habit would click, get nothing, and be told nothing.
            if (MayaNavigationAuthority.CameraGestureActive) return;

            // Names ALT as well, for the case where it IS a mistake: MAYA-NAV off, where ALT is
            // reserved and means nothing. Without it the toast sends the user hunting for three
            // keys none of which are down.
            StatusToast.Show("Release ALT / CTRL / TAB / SPACE, then click the effect point.", false, 2f);
            return;
        }

        RaycastHit hit;
        if (!TryRaycastModel(out hit))
        {
            StatusToast.Show("Nothing under the cursor - click on the model.", false, 2f);
            return;
        }

        AddKind kind = armedKind;
        int groupId = armedGroupId;
        Disarm();
        Place(kind, groupId, hit.point, hit.normal);
    }

    void Place(AddKind kind, int groupId, Vector3 point, Vector3 normal)
    {
        if (kind == AddKind.Post)
        {
            PlacePost(groupId, point, normal);
            return;
        }

        if (kind == AddKind.Clumper)
        {
            PlaceClumper(groupId, point, normal);
            return;
        }

        if (kind == AddKind.Guide)
        {
            PlaceGuide(groupId, point, normal);
            return;
        }
    }

    // Reproduces the CTRL+click path exactly, in the same order it happens there:
    // ModelViewer.EnterSelectionMode sets the selection hotspot (execution order 0 on a
    // real CTRL+click), then PostAffectorManager.CreateAffector turns that hotspot into
    // an affector. Creating the affector without the hotspot is the stranded-pair state
    // documented in PostAffectorManager - handles that drag but geometry that never moves.
    void PlacePost(int groupId, Vector3 point, Vector3 normal)
    {
        if (viewer == null || posts == null)
        {
            StatusToast.Show("POST could not be placed - affector manager not found.", true);
            return;
        }

        if (enterSelectionModeMethod == null || createAffectorMethod == null)
        {
            StatusToast.Show("POST could not be placed - placement entry points not found.", true);
            return;
        }

        enterSelectionModeMethod.Invoke(viewer, new object[] { point, normal });
        createAffectorMethod.Invoke(posts, new object[] { groupId, point, normal });

        // PostAffectorManager guards itself against creating two affectors in one frame.
        // Stamping the same guard here means a CTRL that happened to be held on the
        // placement click cannot produce a second POST on top of this one.
        if (postLastCreatedFrameField != null)
        {
            postLastCreatedFrameField.SetValue(posts, Time.frameCount);
        }

        StatusToast.Show("POST placed on group " + groupId + ".", false, 2f);
    }

    void PlaceClumper(int groupId, Vector3 point, Vector3 normal)
    {
        if (clumpers == null)
        {
            StatusToast.Show("CLUMPER could not be placed - clumper manager not found.", true);
            return;
        }

        // Same public entry point TAB+click uses. It selects the new clumper, which is
        // what opens its controls in the right-hand panel.
        clumpers.CreateClumper(groupId, point, normal);
        StatusToast.Show("CLUMPER placed on group " + groupId + ".", false, 2f);
    }

    void PlaceGuide(int groupId, Vector3 point, Vector3 normal)
    {
        if (guides == null) guides = FindFirstObjectByType<GuideCurveManager>();
        if (guides == null)
        {
            StatusToast.Show("GUIDE could not be placed - guide manager not found.", true);
            return;
        }

        guides.CreateGuide(groupId, point, normal);
        StatusToast.Show("GUIDE placed on group " + groupId + ". Raise Guide Amount to comb.", false, 3f);
    }

    // "Some other gesture owns this click, so an armed placement stands down."
    //
    // Two aim rings have to stay in step with this, and NOT in the same way:
    //
    //   SelectionBrushVisualizer.blockedModifier mirrors this set exactly.
    //   InfluenceRingPreviewAuthority.blockedModifierHeld mirrors it MINUS TAB, deliberately -
    //   TAB+click is picked up by GroupClumperInteractionAuthority and does create a clumper, so
    //   the ring is telling the truth there.
    //
    // Anything added here has to be considered for both, but only copied verbatim into the first.
    // Get that wrong in either direction and a ring starts promising a placement the click will
    // not make, or hiding one it will.
    static bool IsShortcutModifierHeld()
    {
        if (Keyboard.current == null) return false;
        if (Keyboard.current.ctrlKey.isPressed) return true;
        if (Keyboard.current.tabKey.isPressed) return true;
        if (Keyboard.current.spaceKey.isPressed) return true;

        // ALT. Under MAYA-NAV this is a camera gesture and an armed placement must not fire
        // underneath a tumble. With MAYA-NAV off ALT is reserved rather than meaningless -
        // ModelViewer.HandleGrooming and PlacementBrushModeAuthority both refuse an ALT click
        // outright, so that a user reaching for the old ALT+click group pick gets nothing instead
        // of a card planted on the model - and this is the same reservation.
        //
        // It was never in this list, which was a latent bug for as long as ALT+click WAS the group
        // pick: picking a group with a +POST armed both selected the group AND placed the POST.
        if (MayaNavigationAuthority.AltReserved) return true;
        return false;
    }

    bool TryRaycastModel(out RaycastHit hit)
    {
        hit = default;
        if (viewer == null) return false;
        if (viewer.mainCamera == null) return false;
        if (Mouse.current == null) return false;
        Ray ray = viewer.mainCamera.ScreenPointToRay(Mouse.current.position.ReadValue());
        return Physics.Raycast(ray, out hit);
    }

    void Resolve()
    {
        if (viewer == null)
        {
            viewer = FindFirstObjectByType<ModelViewer>();
            enterSelectionModeMethod = null;
            selectGroupMethod = null;
        }

        if (viewer != null && enterSelectionModeMethod == null)
        {
            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            Type t = typeof(ModelViewer);
            enterSelectionModeMethod = t.GetMethod("EnterSelectionMode", flags);
            selectGroupMethod = t.GetMethod("SelectGroup", flags);
        }

        if (posts == null)
        {
            posts = FindFirstObjectByType<PostAffectorManager>();
            createAffectorMethod = null;
            postLastCreatedFrameField = null;
        }

        if (posts != null && createAffectorMethod == null)
        {
            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            Type t = typeof(PostAffectorManager);
            createAffectorMethod = t.GetMethod("CreateAffector", flags);
            postLastCreatedFrameField = t.GetField("lastCreatedFrame", flags);
        }

        if (clumpers == null)
        {
            clumpers = FindFirstObjectByType<GroupClumperManager>();
        }

        if (guides == null)
        {
            guides = FindFirstObjectByType<GuideCurveManager>();
        }
    }
    // ---------------------------------------------------------------------------------
    // Bottom-of-viewport prompt.
    //
    // PlacementModeBannerAuthority hides itself while grooming is off, and grooming is
    // always off while a placement is armed, so this can occupy the same line without the
    // two ever drawing over each other.
    // ---------------------------------------------------------------------------------

    void UpdateBanner()
    {
        Canvas canvas = ResolveCanvas();
        if (canvas == null)
        {
            SetBannerVisible(false);
            return;
        }

        if (boundCanvas != canvas || bannerLabel == null)
        {
            BuildBanner(canvas);
        }
        if (bannerLabel == null) return;

        SetBannerVisible(true);

        string what = "POST";
        if (armedKind == AddKind.Clumper) what = "CLUMPER";
        if (armedKind == AddKind.Guide) what = "GUIDE";

        string text = "PLACE " + what + " ON GROUP " + armedGroupId +
                      "   -   click the effect point on the model    (right-click or ESC to cancel)";

        if (text == lastBannerText) return;
        lastBannerText = text;
        bannerLabel.text = text;
        if (bannerShadow != null) bannerShadow.text = text;
    }

    Canvas ResolveCanvas()
    {
        if (viewer == null) return null;
        if (viewer.groomingSliderPanelGO == null) return null;
        Canvas canvas = viewer.groomingSliderPanelGO.GetComponentInParent<Canvas>();
        if (canvas == null) return null;
        return canvas.rootCanvas;
    }

    void BuildBanner(Canvas canvas)
    {
        boundCanvas = canvas;
        lastBannerText = string.Empty;

        Transform existing = canvas.transform.Find(BannerName);
        if (existing != null)
        {
            bannerObject = existing.gameObject;
            bannerShadow = FindChildText(existing, "Shadow");
            bannerLabel = FindChildText(existing, "Label");
            if (bannerLabel != null) return;
            Destroy(bannerObject);
            bannerObject = null;
        }

        bannerObject = new GameObject(BannerName, typeof(RectTransform));
        bannerObject.transform.SetParent(canvas.transform, false);

        RectTransform rect = bannerObject.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 0f);
        rect.anchorMax = new Vector2(1f, 0f);
        rect.pivot = new Vector2(.5f, 0f);
        rect.anchoredPosition = new Vector2(0f, BottomInset);
        rect.sizeDelta = new Vector2(-24f, BannerHeight);

        bannerShadow = BuildBannerText(bannerObject.transform, "Shadow", new Color(0f, 0f, 0f, .75f),
            new Vector2(ShadowOffset, -ShadowOffset));
        bannerLabel = BuildBannerText(bannerObject.transform, "Label", new Color(1f, .82f, .28f, 1f),
            Vector2.zero);

        // Sits over the viewport, so it must never eat the placement click.
        bannerObject.transform.SetAsLastSibling();
    }

    static TextMeshProUGUI FindChildText(Transform parent, string childName)
    {
        Transform child = parent.Find(childName);
        if (child == null) return null;
        return child.GetComponent<TextMeshProUGUI>();
    }

    static TextMeshProUGUI BuildBannerText(Transform parent, string childName, Color color, Vector2 offset)
    {
        GameObject go = new GameObject(childName, typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);

        RectTransform rect = go.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.pivot = new Vector2(.5f, .5f);
        rect.offsetMin = new Vector2(offset.x, offset.y);
        rect.offsetMax = new Vector2(offset.x, offset.y);

        TextMeshProUGUI text = go.GetComponent<TextMeshProUGUI>();
        text.fontSize = BannerFontSize;
        text.fontStyle = FontStyles.Bold;
        text.color = color;
        text.alignment = TextAlignmentOptions.Center;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Overflow;
        text.raycastTarget = false;
        return text;
    }

    void SetBannerVisible(bool visible)
    {
        if (bannerObject == null) return;
        if (bannerObject.activeSelf == visible) return;
        bannerObject.SetActive(visible);
    }

    void OnDisable()
    {
        // Never leave grooming switched off because this component went away mid-placement.
        restorePending = false;
        GroomingInputLock.Release(LockOwner);
        GroomingInputLock.TryRestore(viewer);
    }
}

// ------------------------------------------------------------------------------------
// The row of add buttons itself.
//
// Kept as a separate component because it has to run LAST (after
// GroupPanelRowOrderAuthority at 10000 and after GroupClumperManager's row scan), while
// the input reservation above has to run FIRST. One component cannot be both.
//
// The row is placed at the bottom of its group's block: below the group header, below
// that group's POST rows and below its CLUMPER rows. Recomputing the index rather than
// pinning it to header+1 is what keeps it out of a sibling-index fight with the two
// authorities that own those rows.
// ------------------------------------------------------------------------------------
[DefaultExecutionOrder(10500)]
public class GroupAddRowUIAuthority : MonoBehaviour
{
    private const string RowPrefix = "GroupAddRow_";

    public const string CopyButtonName = "GroupCopyParamsButton";
    public const string PasteButtonName = "GroupPasteParamsButton";
    private const float ScanInterval = .15f;
    private const float RowHeight = 26f;
    private const float ButtonHeight = 20f;

    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<GroupAddRowUIAuthority>() != null) return;
        GameObject go = new GameObject("GroupAddRowUIAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<GroupAddRowUIAuthority>();
    }

    void Awake()
    {
        nextScan = 0f;
    }

    void LateUpdate()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + ScanInterval;

        HashSet<int> liveGroups = new HashSet<int>();
        List<Transform> addRows = new List<Transform>();

        RectTransform[] all = FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        foreach (RectTransform rect in all)
        {
            if (rect == null) continue;
            if (rect.name.StartsWith(RowPrefix, StringComparison.Ordinal))
            {
                addRows.Add(rect);
            }
        }

        foreach (RectTransform groupItem in all)
        {
            if (groupItem == null) continue;
            if (!groupItem.name.StartsWith("GroupItem_", StringComparison.Ordinal)) continue;

            int gid;
            if (!int.TryParse(groupItem.name.Substring("GroupItem_".Length), out gid)) continue;

            Transform parent = groupItem.parent;
            if (parent == null) continue;

            liveGroups.Add(gid);

            string rowName = RowPrefix + gid;
            Transform row = parent.Find(rowName);
            if (row == null)
            {
                row = BuildRow(parent, gid).transform;
            }

            row.SetSiblingIndex(ResolveInsertIndex(parent, groupItem, gid));
            PaintRow(row, gid);
        }

        // A group can be deleted between rebuilds; its buttons must not outlive it.
        foreach (Transform row in addRows)
        {
            if (row == null) continue;
            int gid;
            if (!int.TryParse(row.name.Substring(RowPrefix.Length), out gid)) continue;
            if (liveGroups.Contains(gid)) continue;
            Destroy(row.gameObject);
        }

    }

    // Header, then this group's POST rows, then its CLUMPER rows, then this row.
    static int ResolveInsertIndex(Transform parent, RectTransform groupItem, int gid)
    {
        int index = groupItem.GetSiblingIndex() + 1;
        string postPrefix = "PostAffector_" + gid + "_";
        string clumperPrefix = "GroupClumper_" + gid + "_";
        string guidePrefix = "GuideCurve_" + gid + "_";

        while (index < parent.childCount)
        {
            Transform child = parent.GetChild(index);
            if (child == null) break;
            bool ownedByThisGroup = child.name.StartsWith(postPrefix, StringComparison.Ordinal) ||
                                    child.name.StartsWith(clumperPrefix, StringComparison.Ordinal) ||
                                    child.name.StartsWith(guidePrefix, StringComparison.Ordinal);
            if (!ownedByThisGroup) break;
            index++;
        }

        int maxIndex = parent.childCount - 1;
        if (index > maxIndex) index = maxIndex;
        if (index < 0) index = 0;
        return index;
    }

    GameObject BuildRow(Transform parent, int gid)
    {
        GameObject row = new GameObject(RowPrefix + gid, typeof(RectTransform), typeof(Image), typeof(HorizontalLayoutGroup));
        row.transform.SetParent(parent, false);
        row.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, RowHeight);
        row.GetComponent<Image>().color = new Color(.10f, .10f, .12f, .95f);

        HorizontalLayoutGroup layout = row.GetComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(8, 8, 3, 3);
        layout.spacing = 6f;
        layout.childControlWidth = false;
        layout.childControlHeight = true;
        layout.childAlignment = TextAnchor.MiddleLeft;

        int captured = gid;

        GameObject postButton = BuildButton(row.transform, "AddPostButton", "+POST", 52f);
        postButton.GetComponent<Button>().onClick.AddListener(delegate
        {
            GroupAddButtonPlacementAuthority.Arm(GroupAddButtonPlacementAuthority.AddKind.Post, captured);
        });

        GameObject clumperButton = BuildButton(row.transform, "AddClumperButton", "+CLUMPER", 70f);
        clumperButton.GetComponent<Button>().onClick.AddListener(delegate
        {
            GroupAddButtonPlacementAuthority.Arm(GroupAddButtonPlacementAuthority.AddKind.Clumper, captured);
        });

        GameObject guideButton = BuildButton(row.transform, "AddGuideButton", "+GUIDE", 58f);
        guideButton.GetComponent<Button>().onClick.AddListener(delegate
        {
            GroupAddButtonPlacementAuthority.Arm(GroupAddButtonPlacementAuthority.AddKind.Guide, captured);
        });

        // COPY and PASTE move a group's whole parameter block to another group. They sit on this
        // row rather than in the right panel because that makes the group they act on the row's
        // own, rather than whichever one happens to be selected - the one thing that has to be
        // unambiguous when the whole point is moving settings between two of them.
        //
        // The three add buttons lost width to make room. There is about 314 units of usable strip
        // once the panel's 10, the content layout's 5 and this row's 8 are paid for on each side;
        // five buttons at 52 + 70 + 58 + 44 + 50 and four 6-unit gaps come to 298.
        GameObject copyButton = BuildButton(row.transform, CopyButtonName, "COPY", 44f);
        copyButton.GetComponent<Button>().onClick.AddListener(delegate
        {
            GroupParameterClipboardAuthority.Copy(captured);
        });

        GameObject pasteButton = BuildButton(row.transform, PasteButtonName, "PASTE", 50f);
        pasteButton.GetComponent<Button>().onClick.AddListener(delegate
        {
            GroupParameterClipboardAuthority.Paste(captured);
        });

        return row;
    }

    static GameObject BuildButton(Transform parent, string objectName, string label, float width)
    {
        GameObject go = new GameObject(objectName, typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        go.transform.SetParent(parent, false);

        LayoutElement element = go.GetComponent<LayoutElement>();
        element.preferredWidth = width;
        element.minWidth = width;
        element.preferredHeight = ButtonHeight;

        go.GetComponent<RectTransform>().sizeDelta = new Vector2(width, ButtonHeight);

        GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textGO.transform.SetParent(go.transform, false);

        RectTransform textRect = textGO.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;

        TextMeshProUGUI text = textGO.GetComponent<TextMeshProUGUI>();
        text.text = label;
        text.fontSize = 10f;
        text.fontStyle = FontStyles.Bold;
        text.alignment = TextAlignmentOptions.Center;
        text.color = Color.white;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.raycastTarget = false;

        return go;
    }

    // Idle colours match the row colours already used elsewhere in the panel: POST rows are
    // blue, CLUMPER rows are green. The armed button goes amber, matching the prompt text.
    static void PaintRow(Transform row, int gid)
    {
        bool armedHere = GroupAddButtonPlacementAuthority.ArmedGroupId == gid;

        PaintButton(row, "AddPostButton",
            armedHere && GroupAddButtonPlacementAuthority.ArmedKind == GroupAddButtonPlacementAuthority.AddKind.Post,
            new Color(.18f, .26f, .38f, 1f));

        PaintButton(row, "AddClumperButton",
            armedHere && GroupAddButtonPlacementAuthority.ArmedKind == GroupAddButtonPlacementAuthority.AddKind.Clumper,
            new Color(.15f, .30f, .21f, 1f));

        PaintButton(row, "AddGuideButton",
            armedHere && GroupAddButtonPlacementAuthority.ArmedKind == GroupAddButtonPlacementAuthority.AddKind.Guide,
            new Color(.26f, .20f, .34f, 1f));

        // COPY is always available. PASTE lights only once something has been copied, and stays
        // lit for the rest of the session or until COPY is pressed again, so the button itself
        // answers "do I have anything to paste" without the user having to remember.
        PaintButton(row, CopyButtonName, false, new Color(.30f, .28f, .20f, 1f));
        PaintPasteButton(row);
    }

    static void PaintPasteButton(Transform row)
    {
        Transform child = row.Find(PasteButtonName);
        if (child == null) return;

        bool ready = GroupParameterClipboardAuthority.HasCopy;

        Image image = child.GetComponent<Image>();
        if (image != null)
            image.color = ready ? new Color(.44f, .38f, .16f, 1f) : new Color(.17f, .17f, .18f, 1f);

        // The label greys with it. The button is deliberately left clickable so that pressing it
        // with an empty clipboard says why, rather than doing nothing at all.
        TextMeshProUGUI label = child.GetComponentInChildren<TextMeshProUGUI>(true);
        if (label != null)
            label.color = ready ? Color.white : new Color(.55f, .55f, .58f, 1f);
    }

    static void PaintButton(Transform row, string objectName, bool armed, Color idle)
    {
        Transform child = row.Find(objectName);
        if (child == null) return;
        Image image = child.GetComponent<Image>();
        if (image == null) return;

        if (armed)
        {
            image.color = new Color(.85f, .60f, .12f, 1f);
            return;
        }
        image.color = idle;
    }
}
