using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

// Shared visual language for radius/falloff tools:
// strong inner ring = full-effect radius, faint outer ring = zero-effect falloff edge.
[DefaultExecutionOrder(9600)]
public class InfluenceRingPreviewAuthority : MonoBehaviour
{
    private const int CircleSegments = 64;

    private ModelViewer viewer;
    private PlacementBrushModeAuthority placement;
    private GroupClumperManager clumperManager;

    private FieldInfo placementModeField;
    private FieldInfo placementRadiusField;
    private FieldInfo placementFalloffField;
    private FieldInfo groomingModeField;

    // Last frame on which a placement gesture was genuinely live, and this frame's cached
    // answer to "are we on the groom screen with grooming on". See TrackPlacementLive.
    private int lastPlacementLiveFrame = -100;
    private bool groomScreenLiveThisFrame;
    private FieldInfo clumperByGroupField;
    private FieldInfo clumperSelectedGroupField;

    private LineRenderer sprayOuter;
    private LineRenderer clumpInner;
    private LineRenderer clumpOuter;
    private LineRenderer clumpAimInner;
    private LineRenderer clumpAimOuter;
    private Material lineMaterial;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<InfluenceRingPreviewAuthority>() != null) return;
        GameObject go = new GameObject("InfluenceRingPreviewAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<InfluenceRingPreviewAuthority>();
    }

    void Update()
    {
        Resolve();
        TrackPlacementLive();
        UpdateSprayRings();
        UpdateClumperRings();
        UpdateClumperAimRings();
    }

    void Resolve()
    {
        if (viewer == null)
        {
            viewer = FindFirstObjectByType<ModelViewer>();
            if (viewer != null)
            {
                BindingFlags viewerFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                groomingModeField = typeof(ModelViewer).GetField("isGroomingMode", viewerFlags);
            }
        }

        if (placement == null)
        {
            placement = FindFirstObjectByType<PlacementBrushModeAuthority>();
            if (placement != null)
            {
                BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                System.Type t = typeof(PlacementBrushModeAuthority);
                placementModeField = t.GetField("mode", flags);
                placementRadiusField = t.GetField("brushRadius", flags);
                placementFalloffField = t.GetField("sprayFalloff", flags);
            }
        }

        if (clumperManager == null)
        {
            clumperManager = FindFirstObjectByType<GroupClumperManager>();
            if (clumperManager != null)
            {
                BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                System.Type t = typeof(GroupClumperManager);
                clumperByGroupField = t.GetField("byGroup", flags);
                clumperSelectedGroupField = t.GetField("selectedGroup", flags);
            }
        }
    }

    void UpdateSprayRings()
    {
        if (viewer == null || viewer.mainCamera == null || placement == null || Mouse.current == null ||
            placementModeField == null || placementRadiusField == null || placementFalloffField == null || IsTextureMode())
        {
            SetEnabled(sprayOuter, false);
            return;
        }

        // TAB and SPACE are modifier-placement gestures, not spray. PlacementBrushModeAuthority
        // already hides its own brush preview for them; this ring never learned to, so holding
        // TAB in Spray mode used to leave a stray cyan ring on the cursor. Harmless when it was
        // the only thing on screen - actively misleading now that the clumper aim ring draws
        // there too, since the two describe different sizes and only one is what the click makes.
        // ALT belongs in this list as much as TAB and SPACE do. PlacementBrushModeAuthority hides
        // its own brush ring and places nothing while ALT is held, so without it the cyan falloff
        // ring went on drawing on the cursor for the whole of a tumble - the same "ring you aim
        // with over a click that does nothing" this file already guards against for the clumper.
        bool modifierGesture = Keyboard.current != null &&
                               (Keyboard.current.tabKey.isPressed ||
                                Keyboard.current.spaceKey.isPressed ||
                                MayaNavigationAuthority.AltReserved);

        // The !groomingEnabled half of PlacementBrushModeAuthority's own hide condition, which
        // this ring never mirrored. It matters now: a +CLUMPER placement holds grooming off for
        // its whole duration, and without this the cyan spray ring drew on the cursor at
        // brushRadius * 1.55 right on top of the green 0.04 aim ring - two rings, two sizes,
        // one of them describing something the click will not do. Reuses the frame's cached
        // answer, which also carries the menu test.
        bool sprayEnabled = groomScreenLiveThisFrame;

        object modeObj = placementModeField.GetValue(placement);
        if (modeObj == null || modeObj.ToString() != "Spray" || modifierGesture || !sprayEnabled ||
            (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()))
        {
            SetEnabled(sprayOuter, false);
            return;
        }

        Ray ray = viewer.mainCamera.ScreenPointToRay(Mouse.current.position.ReadValue());
        if (!Physics.Raycast(ray, out RaycastHit hit))
        {
            SetEnabled(sprayOuter, false);
            return;
        }

        float radius = Mathf.Max(.001f, (float)placementRadiusField.GetValue(placement));
        float falloff01 = Mathf.Clamp01((float)placementFalloffField.GetValue(placement));

        // Spray's existing Falloff slider is normalized. Treat it as an additional shell
        // up to one radius wide, so the current inner radius remains the full-density zone.
        float outerRadius = radius * (1f + falloff01);
        if (falloff01 <= .0001f)
        {
            SetEnabled(sprayOuter, false);
            return;
        }

        EnsureRenderers();
        DrawRing(sprayOuter, hit.point, hit.normal, outerRadius,
            new Color(.25f, .85f, 1f, .42f), false);
    }

    void UpdateClumperRings()
    {
        if (clumperManager == null || clumperByGroupField == null || clumperSelectedGroupField == null || IsTextureMode())
        {
            HideClumper();
            return;
        }

        int selected = (int)clumperSelectedGroupField.GetValue(clumperManager);
        if (selected < 0)
        {
            HideClumper();
            return;
        }

        // DEAD PATH, left as-is on purpose. GroupClumperManager.byGroup is
        // Dictionary<int, List<GroupClumper>>, so this cast yields null on every frame and the
        // method has always fallen straight through to HideClumper().
        //
        // Do not "fix" the cast without deleting the draw below: the selected clumper's rings
        // are already drawn by SelectedClumperRadialPreviewAuthority (5270), so a corrected
        // cast here would put a second, identical pair of rings on top of them.
        var byGroup = clumperByGroupField.GetValue(clumperManager) as Dictionary<int, GroupClumperManager.GroupClumper>;
        if (byGroup == null || !byGroup.TryGetValue(selected, out GroupClumperManager.GroupClumper clumper) || clumper == null ||
            clumper.mode == GroupClumperManager.ClumpMode.DispersedEvenly)
        {
            HideClumper();
            return;
        }

        EnsureRenderers();
        float radius = Mathf.Max(.001f, clumper.radius);
        float falloff = Mathf.Max(0f, clumper.falloff);
        Vector3 normal = clumper.normal.sqrMagnitude > .000001f ? clumper.normal.normalized : Vector3.up;

        DrawRing(clumpInner, clumper.center, normal, radius,
            new Color(.35f, 1f, .50f, .92f), true);

        if (falloff > .0001f)
            DrawRing(clumpOuter, clumper.center, normal, radius + falloff,
                new Color(.35f, 1f, .50f, .38f), false);
        else
            SetEnabled(clumpOuter, false);
    }

    // Pre-click aim ring for CLUMPER placement.
    //
    // POST has had one of these for a long time - SelectionBrushVisualizer draws a ring at the
    // cursor while CTRL is held, so you can see where the affector will land and how wide it
    // will be before committing. CLUMPER had nothing: the first sight of its radius was after
    // the click, which makes placing one guesswork.
    //
    // Drawn at the CREATION size, not at any existing clumper's size. CreateClumper never sets
    // radius or falloff, so GroupClumperManager's defaults are exactly what the click produces.
    void UpdateClumperAimRings()
    {
        if (viewer == null || viewer.mainCamera == null || Mouse.current == null || IsTextureMode())
        {
            HideClumperAim();
            return;
        }

        if (!IsPlacementLive())
        {
            HideClumperAim();
            return;
        }

        bool armedForClumper = GroupAddButtonPlacementAuthority.ArmedKind ==
                               GroupAddButtonPlacementAuthority.AddKind.Clumper;
        bool tabHeld = Keyboard.current != null && Keyboard.current.tabKey.isPressed;
        bool blockedModifierHeld = MayaNavigationAuthority.AltReserved ||
                               (Keyboard.current != null &&
                                (Keyboard.current.ctrlKey.isPressed ||
                                 Keyboard.current.spaceKey.isPressed));

        // TAB always aims: GroupClumperInteractionAuthority creates a clumper on TAB+click at
        // exactly these defaults, and it does so even while a +POST or +GUIDE placement is
        // armed, so the ring is telling the truth in all of those combinations.
        //
        // The armed +CLUMPER button only aims while no OTHER modifier is down, because
        // GroupAddButtonPlacementAuthority.IsShortcutModifierHeld refuses its own placement
        // whenever CTRL, ALT, TAB or SPACE is held. With SPACE resting under a hand the click
        // does not create anything - it repositions the SELECTED clumper, at that clumper's own
        // radius - so a ring promising a new 0.04 clump would be a straight lie. ALT is in that
        // set because under MAYA-NAV it is a camera gesture. TAB is excluded from this test
        // because the TAB path picks the click up and does create one.
        //
        // This list has to keep matching IsShortcutModifierHeld. It is not shared with it because
        // TAB is treated differently on the two sides.
        // tabHeld is qualified by ALT rather than standing alone. TAB "always aims" because
        // TAB+click always creates a clumper - except that it no longer does while ALT is held,
        // since both TAB+click handlers now stand down for the camera. Left as a bare disjunct it
        // short-circuited the ALT that was just added to blockedModifierHeld, and drew the green
        // 0.04 ring under an ALT+TAB the click would ignore.
        bool aiming = (tabHeld && !MayaNavigationAuthority.AltReserved) ||
                      (armedForClumper && !blockedModifierHeld);
        if (!aiming)
        {
            HideClumperAim();
            return;
        }

        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
        {
            HideClumperAim();
            return;
        }

        Ray ray = viewer.mainCamera.ScreenPointToRay(Mouse.current.position.ReadValue());
        if (!Physics.Raycast(ray, out RaycastHit hit))
        {
            HideClumperAim();
            return;
        }

        EnsureRenderers();

        float radius = Mathf.Max(.001f, GroupClumperManager.DefaultClumperRadius);
        float falloff = Mathf.Max(0f, GroupClumperManager.DefaultClumperFalloff);

        // Same green as the selected-clumper rings on purpose: it is the same tool. The two are
        // told apart by position - the aim ring tracks the cursor, the selected one sits on its
        // stored centre - and seeing both at once is what makes placing a second clumper
        // relative to the first possible.
        DrawRing(clumpAimInner, hit.point, hit.normal, radius,
            new Color(.35f, 1f, .50f, .92f), true);

        if (falloff > .0001f)
        {
            DrawRing(clumpAimOuter, hit.point, hit.normal, radius + falloff,
                new Color(.35f, 1f, .50f, .38f), false);
        }
        else
        {
            SetEnabled(clumpAimOuter, false);
        }
    }

    // isGroomingMode is the "grooming input is live" flag, but three separate paths switch it
    // off transiently while placement gestures are still very much active:
    // GroupAddButtonPlacementAuthority holds it off for a whole armed placement and for the
    // deferred restore after one, and ModifierGestureReservation drops it for the single frame
    // of any TAB or SPACE click. Reading it raw would blink the aim ring out at the exact
    // moment of commit and hide it entirely during a button placement. So: latch the last frame
    // on which grooming or an armed placement was true, and allow a couple of frames of grace.
    //
    // The menu is what this must NOT cover. ReturnToMenu leaves the model rendered with its
    // collider live and puts no full-screen backdrop over it, so a ring drawn there tracks the
    // cursor across the menu screen. isGroomingMode ALONE cannot gate that, because two paths
    // switch it back on with the menu still displayed and never switch it off again:
    // GroupAddButtonPlacementAuthority's deferred restore (arm a placement, then click MENU),
    // and ModifierGestureReservation, whose restore is an unconditional ToggleGroomingMode(true)
    // that any stray TAB or SPACE click can trigger.
    //
    // uiContainer is the menu canvas itself and cannot be forged: ModelViewer deactivates it on
    // entering the groom screen, ReturnToMenu reactivates it. Testing it directly is the only
    // gate here that the flag-flipping above cannot walk straight through.
    void TrackPlacementLive()
    {
        groomScreenLiveThisFrame = false;
        if (OnMenuScreen()) return;

        groomScreenLiveThisFrame = IsGroomingMode();
        bool armed = GroupAddButtonPlacementAuthority.ArmedKind !=
                     GroupAddButtonPlacementAuthority.AddKind.None;
        if (!groomScreenLiveThisFrame && !armed) return;
        lastPlacementLiveFrame = Time.frameCount;
    }

    bool OnMenuScreen()
    {
        return viewer != null && viewer.uiContainer != null && viewer.uiContainer.activeInHierarchy;
    }

    bool IsPlacementLive()
    {
        return Time.frameCount - lastPlacementLiveFrame <= 2;
    }

    bool IsGroomingMode()
    {
        return viewer != null && groomingModeField != null && groomingModeField.GetValue(viewer) is bool b && b;
    }

    // Delegated so every preview in the project answers this the same way. The body was the same
    // reflection read the probe does; three files had written it out and four had not, and three
    // of those four were drawing over the texture editor. (The fourth, ClumperRuntimeMarker, has
    // never drawn anything at all - see the note in it.)
    bool IsTextureMode()
    {
        return GroomViewportSuppressed.Active;
    }

    void EnsureRenderers()
    {
        if (lineMaterial == null)
        {
            Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            if (shader != null) lineMaterial = new Material(shader) { name = "HairBrushInfluenceRingPreview" };
        }

        if (sprayOuter == null) sprayOuter = CreateRing("SprayFalloffRing");
        if (clumpInner == null) clumpInner = CreateRing("ClumperRadiusRing");
        if (clumpOuter == null) clumpOuter = CreateRing("ClumperFalloffRing");
        if (clumpAimInner == null) clumpAimInner = CreateRing("ClumperAimRadiusRing");
        if (clumpAimOuter == null) clumpAimOuter = CreateRing("ClumperAimFalloffRing");
    }

    LineRenderer CreateRing(string name)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(transform, false);
        LineRenderer lr = go.AddComponent<LineRenderer>();
        lr.loop = true;
        lr.useWorldSpace = true;
        lr.positionCount = CircleSegments;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        if (lineMaterial != null) lr.material = lineMaterial;
        lr.enabled = false;
        return lr;
    }

    void DrawRing(LineRenderer line, Vector3 point, Vector3 normal, float radius, Color color, bool strong)
    {
        if (line == null) return;
        Vector3 n = normal.sqrMagnitude > .000001f ? normal.normalized : Vector3.up;
        Vector3 tangent = Vector3.Cross(n, Vector3.up);
        if (tangent.sqrMagnitude < .000001f) tangent = Vector3.Cross(n, Vector3.right);
        tangent.Normalize();
        Vector3 bitangent = Vector3.Cross(n, tangent).normalized;
        Vector3 center = point + n * .0015f;

        for (int i = 0; i < CircleSegments; i++)
        {
            float a = (i / (float)CircleSegments) * Mathf.PI * 2f;
            line.SetPosition(i, center + (tangent * Mathf.Cos(a) + bitangent * Mathf.Sin(a)) * radius);
        }

        line.startColor = color;
        line.endColor = color;
        float width = strong ? radius * .027f : radius * .016f;
        line.widthMultiplier = Mathf.Clamp(width, .00028f, .0024f);
        line.enabled = true;
    }

    void HideClumper()
    {
        SetEnabled(clumpInner, false);
        SetEnabled(clumpOuter, false);
    }

    void HideClumperAim()
    {
        SetEnabled(clumpAimInner, false);
        SetEnabled(clumpAimOuter, false);
    }

    static void SetEnabled(LineRenderer line, bool enabled)
    {
        if (line != null) line.enabled = enabled;
    }

    void OnDestroy()
    {
        if (lineMaterial != null) Destroy(lineMaterial);
    }
}
