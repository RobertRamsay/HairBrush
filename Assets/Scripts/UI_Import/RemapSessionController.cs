using UnityEngine.UI;
using UnityEngine;

// The REMAP session: two heads on screen at once, the groom still on the old one.
//
// This is the shell only. It brings up the side-by-side view, owns the mode flag every groom
// overlay stands down on, and tears all of it back down again. Markers, the landmark solve and
// the projection are not here yet - see claude/hair-remap-design.md.
//
// Three things about the codebase shape everything below.
//
// ONE CAMERA, and its local position (0.05, 0.03, -0.56) is load-bearing for the dolly maths -
// see the comment in ModelViewer.HandleCameraControls. So the existing camera becomes the LEFT
// view and a duplicate is slaved to it, rather than building two new ones and reimplementing
// ~200 lines of navigation. Camera state lives entirely on the transforms: cameraPivot carries
// orbit and pan, the camera's own transform carries dolly and wheel. Copying both every
// LateUpdate is the whole of the sync, and it means both views orbit together, which is what you
// want when comparing the same landmark on two heads.
//
// NO LAYERS AT ALL. Nothing in the project set a layer or a culling mask before this; every
// object was on Default. RemapSource / RemapTarget / RemapHair are new, declared in
// TagManager.asset, and every object this session moves is put back on its original layer on the
// way out.
//
// THE TARGET HEAD IS NEVER ModelViewer.loadedModel. Fifteen authorities poll that field for
// reference identity and treat a change as "new session, clear my state" - GroomRootStateAuthority,
// SessionModifierFreshStartAuthority, GroupPredeterminedUVLifecycle and friends. A second head
// assigned there would wipe the groom this session exists to preserve.
[DefaultExecutionOrder(9700)]
public class RemapSessionController : MonoBehaviour
{
    public static RemapSessionController Instance { get; private set; }

    // Where the target head sits relative to the source, as a fraction of the source head's
    // width. Far enough apart that the two never overlap at any orbit angle.
    private const float SeparationInHeadWidths = 1.6f;

    // Kept in step with the bar RemapPhaseBar builds. Its canvas scales with screen size against a
    // 1920x1080 reference, so this is the right figure at that height and close enough elsewhere;
    // the cost of being a few pixels out is a few pixels of gap, not a broken viewport.
    private const float PhaseBarHeightPixels = 88f;

    private ModelViewer viewer;
    private GameObject targetModel;
    private Transform targetOffset;

    private Camera rightCamera;
    private Transform rightPivot;

    private Rect savedMainRect = new Rect(0f, 0f, 1f, 1f);
    private int savedMainCullingMask;
    private bool sessionActive;

    // The start screen is where viewer.loadButton lives, so a REMAP always begins with the menu
    // on top of the viewport. Every other import path hides it as a side effect of
    // LoadModelAtPath; this one deliberately does not go through that, so it has to hide it
    // itself - and put it back on cancel, because the menu is where the user came from.
    private bool menuWasActive;

    private int sourceLayer = -1;
    private int targetLayer = -1;
    private int hairLayer = -1;

    private RemapPhase phase = RemapPhase.AutoMarkers;
    private readonly System.Collections.Generic.List<RemapMarker> markers = new System.Collections.Generic.List<RemapMarker>();

    public bool SessionActive { get { return sessionActive; } }
    public GameObject TargetModel { get { return targetModel; } }
    public RemapPhase Phase { get { return phase; } }
    public System.Collections.Generic.List<RemapMarker> Markers { get { return markers; } }

    public Camera LeftCamera { get { if (viewer == null) return null; return viewer.mainCamera; } }
    public Camera RightCamera { get { return rightCamera; } }
    public int SourceLayer { get { return sourceLayer; } }
    public int TargetLayer { get { return targetLayer; } }
    public int HairLayer { get { return hairLayer; } }

    // Each model's OWN transform, not the offset root the target hangs from. Mirroring and the
    // left/right agreement check both work in a head's local space, and the two heads deliberately
    // sit at different world X.
    public Transform SourceRoot
    {
        get
        {
            GameObject source = SourceModel();
            if (source == null) return null;
            return source.transform;
        }
    }

    public Transform TargetRoot
    {
        get
        {
            if (targetModel == null) return null;
            return targetModel.transform;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<RemapSessionController>() != null) return;
        GameObject go = new GameObject("RemapSessionController");
        DontDestroyOnLoad(go);
        go.AddComponent<RemapSessionController>();
    }

    void Awake()
    {
        Instance = this;
        sourceLayer = LayerMask.NameToLayer("RemapSource");
        targetLayer = LayerMask.NameToLayer("RemapTarget");
        hairLayer = LayerMask.NameToLayer("RemapHair");
    }

    public bool LayersAvailable()
    {
        if (sourceLayer < 0) return false;
        if (targetLayer < 0) return false;
        if (hairLayer < 0) return false;
        return true;
    }

    // Bring up a session against an already-imported target head.
    //
    // The caller owns the import, because the caller is the one that just asked the user whether
    // they wanted this at all - see ModelImportRouter.
    public bool Begin(ModelViewer owner, GameObject importedTarget)
    {
        if (sessionActive) return false;
        if (owner == null || importedTarget == null) return false;
        if (owner.mainCamera == null || owner.cameraPivot == null) return false;
        if (!LayersAvailable())
        {
            Debug.LogError("HairBrush: REMAP needs the RemapSource/RemapTarget/RemapHair layers, which are not present in this project's TagManager.");
            return false;
        }

        viewer = owner;
        targetModel = importedTarget;

        menuWasActive = false;
        if (viewer.uiContainer != null)
        {
            menuWasActive = viewer.uiContainer.activeSelf;
            viewer.uiContainer.SetActive(false);
        }

        // A root of our own so the offset can be undone in one step, and so the head can be
        // handed to a solver later in a known space.
        GameObject rootObject = new GameObject("RemapTargetRoot");
        targetOffset = rootObject.transform;
        targetOffset.position = Vector3.zero;
        targetModel.transform.SetParent(targetOffset, true);
        targetModel.transform.localPosition = Vector3.zero;
        targetModel.transform.localEulerAngles = new Vector3(0f, 180f, 0f);
        targetOffset.position = new Vector3(SourceHeadWidth() * SeparationInHeadWidths, 0f, 0f);

        AssignLayers();
        BuildRightView();
        BuildMarkers();
        phase = RemapPhase.AutoMarkers;
        ApplyHairVisibility();

        // Both, deliberately. The mode flag is what makes every ring, handle and brush preview
        // stand down through GroomViewportSuppressed; the input lock is what stops the placement
        // authorities that hold their own gestures. ModelViewer.HandleGrooming checks the flag
        // directly as well, because a cursor over the right-hand view builds a ray that lands
        // somewhere arbitrary on the left-hand model.
        RemapModeProbe.SetActive(true);
        GroomingInputLock.Hold("REMAP", viewer);

        sessionActive = true;
        StatusToast.Show("REMAP: click the new head to match markers 1-" + RemapMarkerSet.AutoMarkerCount + ". ESC cancels.");
        return true;
    }

    // The automatic set is sampled from the groom's own anchors so the marker hull provably
    // contains every point the warp will move - see RemapMarkerSet.FarthestPointSample.
    //
    // Card spawn points only, for now. Guide contacts and clumper/POST centres are anchors too and
    // belong in this cloud, but they live behind reflected private dictionaries and cards dominate
    // the hull in any real groom; a modifier placed outside the card hull is the case this misses.
    void BuildMarkers()
    {
        markers.Clear();

        System.Collections.Generic.List<Vector3> anchors = new System.Collections.Generic.List<Vector3>();
        System.Collections.Generic.List<Vector3> normals = new System.Collections.Generic.List<Vector3>();
        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
        {
            if (card == null) continue;
            anchors.Add(card.GetSpawnHitPoint());
            normals.Add(card.GetSurfaceNormal());
        }

        System.Collections.Generic.List<int> picked = RemapMarkerSet.FarthestPointSample(anchors, RemapMarkerSet.AutoMarkerCount);
        for (int i = 0; i < picked.Count; i++)
        {
            RemapMarker marker = new RemapMarker();
            marker.id = i + 1;
            marker.label = marker.id.ToString();
            marker.kind = RemapMarkerKind.Auto;
            marker.description = "auto marker " + marker.id;
            marker.sourcePlaced = true;
            marker.sourcePoint = anchors[picked[i]];
            marker.sourceNormal = normals[picked[i]];
            markers.Add(marker);
        }

        markers.AddRange(RemapMarkerSet.BuildEarMarkers(markers.Count + 1));
    }

    public void GoToPhase(RemapPhase next)
    {
        if (!sessionActive) return;
        phase = next;
        ApplyHairVisibility();
    }

    private RemapPreviewSnapshot previewSnapshot;
    private RemapProjectionReport previewReport;

    public bool PreviewApplied { get { return previewSnapshot != null; } }
    public RemapProjectionReport PreviewReport { get { return previewReport; } }

    // Solve and move the groom onto the new head, keeping everything needed to put it back.
    public bool RunPreview(out string failure)
    {
        failure = string.Empty;
        if (!sessionActive) return false;
        if (previewSnapshot != null) return true;

        RemapPreviewSnapshot snapshot;
        RemapProjectionReport report;
        if (!RemapPreview.Run(markers, targetLayer, TargetHeadSize(), out snapshot, out report, out failure)) return false;

        previewSnapshot = snapshot;
        previewReport = report;
        phase = RemapPhase.Ready;
        ApplyHairVisibility();
        return true;
    }

    public void RevertPreview()
    {
        if (previewSnapshot == null) return;
        RemapPreview.Revert(previewSnapshot);
        previewSnapshot = null;
        previewReport = null;
        phase = RemapPhase.EarMarkers;
        ApplyHairVisibility();
    }

    // The hair is on its own layer precisely so it can be shown or denied per phase, and which
    // phase it is changes whether it is context or an obstruction.
    //
    // Hidden outright while the EAR markers are being placed. Every ear landmark - the helix root,
    // the crease, the lobe attachment - is underneath the groom on the original head, so leaving
    // the hair up asks the user to click a feature they cannot see. That is what sent six markers
    // onto the bald head and none onto the groomed one: the instruction was pointing at geometry
    // the view was covering.
    void ApplyHairVisibility()
    {
        if (viewer == null || viewer.mainCamera == null || rightCamera == null) return;

        int sourceMask = 1 << sourceLayer;
        int targetMask = 1 << targetLayer;

        if (phase == RemapPhase.EarMarkers)
        {
            viewer.mainCamera.cullingMask = sourceMask;
            rightCamera.cullingMask = targetMask;
            return;
        }
        if (phase == RemapPhase.Ready)
        {
            viewer.mainCamera.cullingMask = sourceMask;
            rightCamera.cullingMask = targetMask | (1 << hairLayer);
            return;
        }
        viewer.mainCamera.cullingMask = sourceMask | (1 << hairLayer);
        rightCamera.cullingMask = targetMask;
    }

    float TargetHeadSize()
    {
        if (targetModel == null) return 1f;
        MeshRenderer[] renderers = targetModel.GetComponentsInChildren<MeshRenderer>();
        if (renderers.Length == 0) return 1f;
        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
        float size = bounds.size.magnitude;
        if (size < .000001f) return 1f;
        return size;
    }

    // Mirror the placed left-ear markers onto the right, on BOTH heads.
    //
    // A one-shot action rather than a live constraint: a live link immediately raises "which side
    // is master" and fights the user the moment they nudge for real asymmetry. This places the
    // twin and leaves both independent.
    public int MirrorEarMarkers()
    {
        if (!sessionActive) return 0;

        int moved = 0;
        foreach (RemapMarker marker in markers)
        {
            if (marker == null || marker.kind != RemapMarkerKind.Ear || !marker.isRightSide) continue;

            RemapMarker twin = FindEarTwin(marker);
            if (twin == null) continue;

            if (twin.sourcePlaced && MirrorOnto(SourceRoot, 1 << sourceLayer, twin.sourcePoint, twin.sourceNormal, out Vector3 sp, out Vector3 sn))
            {
                marker.sourcePoint = sp;
                marker.sourceNormal = sn;
                marker.sourcePlaced = true;
                moved++;
            }
            if (twin.targetPlaced && MirrorOnto(TargetRoot, 1 << targetLayer, twin.targetPoint, twin.targetNormal, out Vector3 tp, out Vector3 tn))
            {
                marker.targetPoint = tp;
                marker.targetNormal = tn;
                marker.targetPlaced = true;
                moved++;
            }
        }
        return moved;
    }

    // Slots pair up by their position within a side: the Nth left slot mirrors to the Nth right.
    RemapMarker FindEarTwin(RemapMarker rightSide)
    {
        int indexWithinSide = 0;
        foreach (RemapMarker marker in markers)
        {
            if (marker == null || marker.kind != RemapMarkerKind.Ear || !marker.isRightSide) continue;
            if (marker == rightSide) break;
            indexWithinSide++;
        }

        int seen = 0;
        foreach (RemapMarker marker in markers)
        {
            if (marker == null || marker.kind != RemapMarkerKind.Ear || !marker.isLeftSide) continue;
            if (seen == indexWithinSide) return marker;
            seen++;
        }
        return null;
    }

    // Reflect through the model's own local X, then put the result back ON the surface with an
    // inward ray, the same shape GroomSymmetryAuthority.TryMirror uses. A scanned head is never
    // exactly symmetric, so the reflection alone would leave the marker floating just off the
    // mesh; when the ray misses entirely the pure reflection is kept and the user nudges it.
    static bool MirrorOnto(Transform root, int layerMask, Vector3 point, Vector3 normal, out Vector3 mirroredPoint, out Vector3 mirroredNormal)
    {
        mirroredPoint = point;
        mirroredNormal = normal;
        if (root == null) return false;

        Vector3 local = root.InverseTransformPoint(point);
        local.x = -local.x;
        mirroredPoint = root.TransformPoint(local);

        Vector3 localNormal = root.InverseTransformDirection(normal);
        localNormal.x = -localNormal.x;
        mirroredNormal = root.TransformDirection(localNormal).normalized;

        Ray ray = new Ray(mirroredPoint + mirroredNormal * .25f, -mirroredNormal);
        RaycastHit hit;
        if (Physics.Raycast(ray, out hit, .5f, layerMask))
        {
            mirroredPoint = hit.point;
            mirroredNormal = hit.normal;
        }
        return true;
    }

    // Put everything back. Called by the user cancelling, and by anything that needs the session
    // gone in a hurry; safe to call when no session is up.
    public void End(bool destroyTargetModel)
    {
        if (!sessionActive) return;

        // Cancelling has to mean the groom is untouched, and by this point a preview may already
        // have moved every anchor in it. Reverted before anything else, while the cards and
        // modifiers this snapshot points at are all still alive.
        if (previewSnapshot != null)
        {
            RemapPreview.Revert(previewSnapshot);
            previewSnapshot = null;
            previewReport = null;
        }

        RestoreLayers();
        TearDownRightView();

        RemapModeProbe.SetActive(false);
        GroomingInputLock.Release("REMAP");
        GroomingInputLock.TryRestore(viewer);

        if (destroyTargetModel && targetOffset != null) Destroy(targetOffset.gameObject);
        targetOffset = null;
        targetModel = null;
        sessionActive = false;

        if (viewer != null && viewer.uiContainer != null) viewer.uiContainer.SetActive(menuWasActive);
    }

    void Update()
    {
        if (!sessionActive) return;
        if (UnityEngine.InputSystem.Keyboard.current == null) return;
        if (UnityEngine.InputSystem.Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            End(true);
            StatusToast.Show("REMAP cancelled. Your groom is untouched.");
        }
    }

    void LateUpdate()
    {
        if (!sessionActive) return;
        SyncRightView();
        // Reasserted every frame rather than set once. ModelViewer.HandleCameraControls runs at
        // default order and writes the pivot and the camera transform directly; the same reason
        // TextureUVRectWorkspace reasserts its own camera in both Update and LateUpdate.
        ApplyViewportRects();
    }

    float SourceHeadWidth()
    {
        GameObject source = SourceModel();
        if (source == null) return 1f;
        MeshRenderer[] renderers = source.GetComponentsInChildren<MeshRenderer>();
        if (renderers.Length == 0) return 1f;
        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
        if (bounds.size.x < .000001f) return 1f;
        return bounds.size.x;
    }

    GameObject SourceModel()
    {
        if (viewer == null) return null;
        System.Reflection.FieldInfo field = typeof(ModelViewer).GetField("loadedModel", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (field == null) return null;
        return field.GetValue(viewer) as GameObject;
    }

    // Hair goes on its own layer rather than travelling with the source head, because it has to
    // be shown and hidden per phase independently of either model: hidden on the right while
    // markers are being placed, shown there once there is a projection to look at.
    void AssignLayers()
    {
        GameObject source = SourceModel();
        if (source != null) SetLayerRecursive(source.transform, sourceLayer);
        if (targetOffset != null) SetLayerRecursive(targetOffset, targetLayer);
        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
        {
            if (card == null) continue;
            SetLayerRecursive(card.transform, hairLayer);
        }
    }

    // Everything in this project was on Default before REMAP existed and goes back there. Stored
    // per-object restoration would be more careful and would also be a lie: there is nothing else
    // to restore to.
    void RestoreLayers()
    {
        GameObject source = SourceModel();
        if (source != null) SetLayerRecursive(source.transform, 0);
        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
        {
            if (card == null) continue;
            SetLayerRecursive(card.transform, 0);
        }
        if (viewer != null && viewer.mainCamera != null)
        {
            viewer.mainCamera.rect = savedMainRect;
            viewer.mainCamera.cullingMask = savedMainCullingMask;
        }
    }

    static void SetLayerRecursive(Transform root, int layer)
    {
        if (root == null) return;
        root.gameObject.layer = layer;
        for (int i = 0; i < root.childCount; i++) SetLayerRecursive(root.GetChild(i), layer);
    }

    void BuildRightView()
    {
        savedMainRect = viewer.mainCamera.rect;
        savedMainCullingMask = viewer.mainCamera.cullingMask;

        GameObject pivotObject = new GameObject("RemapRightPivot");
        rightPivot = pivotObject.transform;

        GameObject cameraObject = new GameObject("RemapRightCamera", typeof(Camera));
        rightCamera = cameraObject.GetComponent<Camera>();
        rightCamera.transform.SetParent(rightPivot, false);
        rightCamera.clearFlags = viewer.mainCamera.clearFlags;
        rightCamera.backgroundColor = viewer.mainCamera.backgroundColor;
        rightCamera.fieldOfView = viewer.mainCamera.fieldOfView;
        rightCamera.nearClipPlane = viewer.mainCamera.nearClipPlane;
        rightCamera.farClipPlane = viewer.mainCamera.farClipPlane;
        rightCamera.orthographic = viewer.mainCamera.orthographic;
        rightCamera.orthographicSize = viewer.mainCamera.orthographicSize;
        // Below the main camera so the left view is what any code asking for Camera.main still
        // finds. Nothing in the project sets a depth, so -1 is what it is competing with.
        rightCamera.depth = viewer.mainCamera.depth - 1f;

        // UI is a screen-space overlay canvas and is not drawn by either of these; the masks only
        // decide which head, and whether the hair, each half shows.
        viewer.mainCamera.cullingMask = (1 << sourceLayer) | (1 << hairLayer);
        rightCamera.cullingMask = 1 << targetLayer;

        SyncRightView();
        ApplyViewportRects();
    }

    void TearDownRightView()
    {
        if (rightPivot != null) Destroy(rightPivot.gameObject);
        rightPivot = null;
        rightCamera = null;
    }

    // The right view is the left one, offset by the gap between the two heads. Orbit and pan live
    // on the pivot, dolly and wheel on the camera's own local position, so both have to be
    // copied - and the target root's offset is added to the pivot POSITION only, so the two
    // cameras look at their own head from the same angle and the same distance.
    void SyncRightView()
    {
        if (rightPivot == null || viewer == null || viewer.cameraPivot == null) return;
        Vector3 offset = Vector3.zero;
        if (targetOffset != null) offset = targetOffset.position;
        rightPivot.position = viewer.cameraPivot.position + offset;
        rightPivot.rotation = viewer.cameraPivot.rotation;
        rightPivot.localScale = viewer.cameraPivot.localScale;
        if (rightCamera == null || viewer.mainCamera == null) return;
        rightCamera.transform.localPosition = viewer.mainCamera.transform.localPosition;
        rightCamera.transform.localRotation = viewer.mainCamera.transform.localRotation;
    }

    // The split goes BETWEEN the panels, not across the whole screen. Halving the full width puts
    // the seam under whichever panel happens to be wider and gives the two views different
    // amounts of usable space. Precedent for asking the panels where they are:
    // TextureWorkspacePolishFix.CentrePreviewBetweenPanels.
    void ApplyViewportRects()
    {
        if (viewer == null || viewer.mainCamera == null || rightCamera == null) return;

        float left = PanelEdge("GroupManagerPanel", true, 0f);
        float right = PanelEdge("GroomingPanel", false, Screen.width);
        if (right - left < 64f)
        {
            left = 0f;
            right = Screen.width;
        }

        float x0 = Mathf.Clamp01(left / Mathf.Max(1f, Screen.width));
        float x1 = Mathf.Clamp01(right / Mathf.Max(1f, Screen.width));
        float mid = (x0 + x1) * .5f;

        // Both views stop below the phase bar. Drawn under it instead, the top of each head sits
        // behind the buttons - and a marker placed up there could not be clicked, because every
        // gesture over UI is refused by EventSystem.IsPointerOverGameObject.
        float barFraction = PhaseBarHeightPixels / Mathf.Max(1f, Screen.height);
        float height = Mathf.Clamp01(1f - barFraction);

        viewer.mainCamera.rect = new Rect(x0, 0f, Mathf.Max(.01f, mid - x0), height);
        rightCamera.rect = new Rect(mid, 0f, Mathf.Max(.01f, x1 - mid), height);
    }

    // Screen-space edge of a named panel, or the fallback when it is absent or hidden.
    static float PanelEdge(string panelName, bool wantRightEdge, float fallback)
    {
        GameObject panel = GameObject.Find(panelName);
        if (panel == null || !panel.activeInHierarchy) return fallback;
        RectTransform rect = panel.GetComponent<RectTransform>();
        if (rect == null) return fallback;

        Vector3[] corners = new Vector3[4];
        rect.GetWorldCorners(corners);
        float min = float.MaxValue;
        float max = float.MinValue;
        for (int i = 0; i < 4; i++)
        {
            Vector2 screen = RectTransformUtility.WorldToScreenPoint(null, corners[i]);
            if (screen.x < min) min = screen.x;
            if (screen.x > max) max = screen.x;
        }
        if (wantRightEdge) return max;
        return min;
    }
}
