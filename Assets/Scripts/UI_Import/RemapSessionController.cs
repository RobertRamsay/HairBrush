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

    private ModelViewer viewer;
    private GameObject targetModel;
    private Transform targetRoot;

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

    public bool SessionActive { get { return sessionActive; } }
    public GameObject TargetModel { get { return targetModel; } }

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
        targetRoot = rootObject.transform;
        targetRoot.position = Vector3.zero;
        targetModel.transform.SetParent(targetRoot, true);
        targetModel.transform.localPosition = Vector3.zero;
        targetModel.transform.localEulerAngles = new Vector3(0f, 180f, 0f);
        targetRoot.position = new Vector3(SourceHeadWidth() * SeparationInHeadWidths, 0f, 0f);

        AssignLayers();
        BuildRightView();

        // Both, deliberately. The mode flag is what makes every ring, handle and brush preview
        // stand down through GroomViewportSuppressed; the input lock is what stops the placement
        // authorities that hold their own gestures. ModelViewer.HandleGrooming checks the flag
        // directly as well, because a cursor over the right-hand view builds a ray that lands
        // somewhere arbitrary on the left-hand model.
        RemapModeProbe.SetActive(true);
        GroomingInputLock.Hold("REMAP", viewer);

        sessionActive = true;
        StatusToast.Show("REMAP: your groom is on the left, the new head on the right. Markers are not built yet - press ESC to cancel.");
        return true;
    }

    // Put everything back. Called by the user cancelling, and by anything that needs the session
    // gone in a hurry; safe to call when no session is up.
    public void End(bool destroyTargetModel)
    {
        if (!sessionActive) return;

        RestoreLayers();
        TearDownRightView();

        RemapModeProbe.SetActive(false);
        GroomingInputLock.Release("REMAP");
        GroomingInputLock.TryRestore(viewer);

        if (destroyTargetModel && targetRoot != null) Destroy(targetRoot.gameObject);
        targetRoot = null;
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
        if (targetRoot != null) SetLayerRecursive(targetRoot, targetLayer);
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
        if (targetRoot != null) offset = targetRoot.position;
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

        viewer.mainCamera.rect = new Rect(x0, 0f, Mathf.Max(.01f, mid - x0), 1f);
        rightCamera.rect = new Rect(mid, 0f, Mathf.Max(.01f, x1 - mid), 1f);
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
