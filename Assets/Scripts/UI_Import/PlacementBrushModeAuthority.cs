using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// Owns card placement input so SHIFT can be a pure mode-cycle key instead of a held paint
// modifier. Existing ModelViewer camera controls remain untouched; only its legacy placement
// branch is suppressed for the frame while grooming itself stays enabled.
[DefaultExecutionOrder(-5000)]
public class PlacementBrushModeAuthority : MonoBehaviour
{
    // EVEN sits between SPRAY and ERASE because that is where it belongs to a hand: the three
    // placing modes in increasing coverage, then the one that takes cards away.
    public enum PlacementMode { Place = 0, Paint = 1, Spray = 2, Even = 3, Erase = 4 }

    // The active mode, for anything that needs to display it (see
    // PlacementModeBannerAuthority). Cycling stays owned by this component.
    public PlacementMode CurrentMode { get { return mode; } }

    // One line saying what a click actually does in each mode.
    public static string DescribeMode(PlacementMode value)
    {
        switch (value)
        {
            case PlacementMode.Paint: return "continuous placing while held";
            case PlacementMode.Spray: return "scatters cards in the brush radius";
            case PlacementMode.Even: return "fills to an even spacing, never closer";
            case PlacementMode.Erase: return "removes cards in the brush radius";
            default: return "one card per click";
        }
    }

    private ModelViewer viewer;
    private FieldInfo groomingModeField;
    private FieldInfo selectionModeField;
    private FieldInfo selectionHotspotField;
    private FieldInfo loadedModelField;
    private MethodInfo pinHairCardMethod;
    private MethodInfo enterSelectionModeMethod;
    private MethodInfo clearSelectionHotspotMethod;
    private MethodInfo selectGroupMethod;
    private MethodInfo refreshGroupListMethod;

    private PlacementMode mode = PlacementMode.Place;
    private float brushRadius = .035f;
    private float sprayFalloff = .55f;

    // EVEN's exclusion distance, in world UNITS, measured root to root.
    //
    // An imported head wider than 2 units is rescaled to about a third of a unit across, which
    // puts .008 at roughly three and a half millimetres of scalp at real head size - dense enough
    // to read as hair, open enough that a first pass over an area is not instantly saturated. A
    // model that arrives already smaller than 2 units is NOT rescaled, and on one of those this
    // number, like the brush radius beside it, means whatever that model's units mean.
    private float cardSpacing = .008f;
    private float nextActionTime;
    private float nextUIScan;
    private bool restorePending;
    private bool restoreSelectionState;

    private GameObject modeRow;
    private GameObject radiusRow;
    private GameObject falloffRow;
    private GameObject spacingRow;
    private Button modeButton;
    private TextMeshProUGUI modeText;
    private Slider radiusSlider;
    private Slider falloffSlider;
    private Slider spacingSlider;
    private Slider boundSegmentsSlider;

    // Every card root on the model, cached for the length of one stroke.
    //
    // EVEN has to answer "is anything already within the spacing" before every card it places,
    // and the project's only way to ask that is FindObjectsByType<HairCard>() - a managed
    // allocation of an array of thousands. At twenty ticks a second with up to sixteen candidate
    // attempts each, asking it per attempt would be several hundred full scene scans a second.
    //
    // Safe for the length of a stroke because it is dropped the moment the button is up, and a
    // stroke is the only window in which nothing else adds or removes a card: EVEN is the active
    // mode so ERASE is not, and every card this places is appended by hand. The one thing that
    // could still slip through is a CTRL+Z pressed with the button held, which is exotic enough
    // to cost a slightly-too-sparse stroke and nothing worse.
    private readonly List<Vector3> spacingRoots = new List<Vector3>();
    private bool spacingRootsValid;

    // The roots close enough to this tick's brush to be worth measuring against, narrowed once
    // per tick from the list above.
    //
    // Without it the expensive case is the SPARSE one, which is the case EVEN exists for: when
    // nothing is in range every crowding test walks the whole array to the end, sixteen attempts
    // times two points times twenty ticks a second. On a twenty thousand card groom that is
    // millions of distance tests a second to answer "no" every time. One pass to narrow, and the
    // inner loop then walks the handful that could possibly matter.
    private readonly List<Vector3> spacingNearby = new List<Vector3>();

    // The same, for where the MIRROR of this tick's brush lands.
    //
    // A separate list because the two are nowhere near each other. Narrowing around the brush and
    // then testing the mirrored candidate against that list is worse than not testing it at all:
    // the mirror is a whole head away, so the shortlist holds nothing near it, the test always
    // answers "clear", and the spacing silently stops being enforced on the mirrored side over
    // every part of the scalp except a thin band along the parting.
    private readonly List<Vector3> spacingNearbyMirror = new List<Vector3>();
    private bool mirrorZoneValid;

    // When EVEN last got a card down. Drives the brush ring's colour, which is the only way to
    // tell a saturated area from a broken brush - every candidate silently failing looks exactly
    // like the mode doing nothing at all.
    //
    // A TIME, not a per-tick flag. Ticks run at twenty a second and the ring is redrawn every
    // frame, so over half-filled scalp the place/no-place answer alternates and a flag would
    // strobe the ring twenty times a second - unpleasant, and squarely in the band that bothers
    // photosensitive people. Dimming only after a third of a second with nothing placed reports
    // the same thing and holds still.
    private float lastEvenPlaceTime;

    private LineRenderer brushPreview;
    private LineRenderer spacingPreview;
    private Material brushMaterial;
    private bool brushMaterialAttempted;

    private const float ActionInterval = .05f;
    private const int CircleSegments = 64;
    private const int EvenAttempts = 16;
    private const float EvenBlockedDelay = .35f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<PlacementBrushModeAuthority>() != null) return;
        GameObject go = new GameObject("PlacementBrushModeAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<PlacementBrushModeAuthority>();
    }

    void Update()
    {
        Resolve();
        if (viewer == null) return;

        // Keeps the slider correct if brushRadius was changed from outside this script - the
        // [ ] hotkey - rather than through the slider itself.
        //
        // Through .value, not SetValueWithoutNotify. Silencing the change also silences the only
        // thing that writes the row's LABEL, so the handle slid while the text went on reading
        // the old number. The listener it fires writes brushRadius back with the value it was
        // just given, so there is nothing to feed back on, and the Approximately guard means it
        // fires only on the frame the two actually differ.
        if (radiusSlider != null && !Mathf.Approximately(radiusSlider.value, brushRadius))
            radiusSlider.value = brushRadius;

        if (Time.unscaledTime >= nextUIScan)
        {
            nextUIScan = Time.unscaledTime + .15f;
            EnsureUI();
            EnsureSegmentMinimum();
        }

        // EVEN's cached card roots live for exactly as long as the button is held, and the test
        // is "is it up", not "was it just pressed".
        //
        // The edge looks equivalent and is not: it sits below the grooming and texture-mode
        // returns above, and grooming is switched off for the whole of an armed +POST placement
        // and for the single frame of any TAB or SPACE click. A press swallowed on one of those
        // frames would leave the previous stroke's cache in place for the stroke that followed.
        // Clearing whenever the button is up also picks up anything that changed the card set
        // between strokes without a mouse at all - an undo, a RESET, a project load.
        if (Mouse.current != null && !Mouse.current.leftButton.isPressed)
        {
            spacingRootsValid = false;

            // Every stroke starts with a clean third of a second before the ring is allowed to
            // report itself blocked, so beginning a stroke over full scalp dims once rather than
            // starting dim from a previous stroke's verdict.
            lastEvenPlaceTime = Time.unscaledTime;
        }

        bool groomingEnabled = GetBool(groomingModeField);
        bool selectionWasActive = GetBool(selectionModeField);
        restoreSelectionState = selectionWasActive;
        restorePending = false;

        if (!groomingEnabled || GetLoadedModel() == null || TextureModeProbe.Active)
        {
            HideBrushPreview();
            return;
        }

        if (Keyboard.current == null || Mouse.current == null) return;

        // Keystrokes belong to the text box while the user is entering text -
        // renaming a group must not cycle the placement mode on every SHIFT.
        if (GroupNameInlineEditAuthority.IsEnteringText)
        {
            HideBrushPreview();
            return;
        }

        bool shiftPressed = Keyboard.current.leftShiftKey.wasPressedThisFrame || Keyboard.current.rightShiftKey.wasPressedThisFrame;
        if (shiftPressed)
        {
            CycleMode();
            HideBrushPreview();
            SuppressLegacyPlacement(selectionWasActive);
            return;
        }

        bool alt = Keyboard.current.leftAltKey.isPressed || Keyboard.current.rightAltKey.isPressed;
        bool ctrl = Keyboard.current.ctrlKey.isPressed;
        bool tab = Keyboard.current.tabKey.isPressed;
        bool space = Keyboard.current.spaceKey.isPressed;

        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
        {
            HideBrushPreview();
            return;
        }

        if (alt && Mouse.current.leftButton.wasPressedThisFrame)
        {
            if (RaycastCursor(out RaycastHit hit)) SelectNearestGroup(hit.point);
            HideBrushPreview();
            SuppressLegacyPlacement(selectionWasActive);
            return;
        }

        if (ctrl && Mouse.current.leftButton.wasPressedThisFrame)
        {
            bool selected = false;
            if (RaycastCursor(out RaycastHit hit))
            {
                enterSelectionModeMethod?.Invoke(viewer, new object[] { hit.point, hit.normal });
                selected = true;
            }
            else
            {
                clearSelectionHotspotMethod?.Invoke(viewer, null);
            }
            SuppressLegacyPlacement(selected);
            HideBrushPreview();
            return;
        }

        if (tab || space || selectionWasActive)
        {
            HideBrushPreview();
            SuppressLegacyPlacement(selectionWasActive);
            return;
        }

        RaycastHit centerHit;
        bool hasSurface = RaycastCursor(out centerHit);
        if ((mode == PlacementMode.Spray || mode == PlacementMode.Even || mode == PlacementMode.Erase) && hasSurface)
            ShowBrushPreview(centerHit.point, centerHit.normal);
        else
            HideBrushPreview();

        bool act = false;
        switch (mode)
        {
            case PlacementMode.Place:
                act = Mouse.current.leftButton.wasPressedThisFrame;
                break;
            case PlacementMode.Paint:
            case PlacementMode.Spray:
            case PlacementMode.Even:
            case PlacementMode.Erase:
                act = Mouse.current.leftButton.isPressed && Time.unscaledTime >= nextActionTime;
                break;
        }

        if (act && hasSurface)
        {
            nextActionTime = Time.unscaledTime + ActionInterval;
            switch (mode)
            {
                case PlacementMode.Place:
                case PlacementMode.Paint:
                    PlaceCard(centerHit.point, centerHit.normal);
                    break;
                case PlacementMode.Spray:
                    SprayOne(centerHit);
                    break;
                case PlacementMode.Even:
                    PlaceEvenly(centerHit);
                    break;
                case PlacementMode.Erase:
                    EraseAt(centerHit.point);
                    break;
            }
        }

        // Block only ModelViewer's old placement branch for this frame. Grooming remains
        // enabled, so normal card creation/state and modifier systems stay live.
        SuppressLegacyPlacement(selectionWasActive);
    }

    void SuppressLegacyPlacement(bool stateToRestore)
    {
        if (viewer == null || selectionModeField == null) return;
        restoreSelectionState = stateToRestore;
        restorePending = true;
        selectionModeField.SetValue(viewer, true);
    }

    void LateUpdate()
    {
        if (!restorePending || viewer == null || selectionModeField == null) return;
        restorePending = false;

        // Only if the flag is still the true THIS authority wrote a moment ago. Anything that
        // deliberately cleared it in between has to win: ModifierContextExit repairs a stranded
        // isSelectionMode when a group is clicked, and on the one frame a SHIFT press coincides
        // with that click, the branch above has already armed a restore. Handing back the stale
        // true there would undo the repair and leave card placement off with nothing on screen
        // to explain it.
        if (!GetBool(selectionModeField)) return;

        selectionModeField.SetValue(viewer, restoreSelectionState);
    }

    void Resolve()
    {
        if (viewer != null) return;
        viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer == null) return;

        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        System.Type t = typeof(ModelViewer);
        groomingModeField = t.GetField("isGroomingMode", flags);
        selectionModeField = t.GetField("isSelectionMode", flags);
        selectionHotspotField = t.GetField("hasSelectionHotspot", flags);
        loadedModelField = t.GetField("loadedModel", flags);
        pinHairCardMethod = t.GetMethod("PinHairCard", flags);
        enterSelectionModeMethod = t.GetMethod("EnterSelectionMode", flags);
        clearSelectionHotspotMethod = t.GetMethod("ClearSelectionHotspot", flags);
        selectGroupMethod = t.GetMethod("SelectGroup", flags);
        refreshGroupListMethod = t.GetMethod("RefreshGroupListUI", flags);
    }

    bool GetBool(FieldInfo field)
    {
        return viewer != null && field != null && field.GetValue(viewer) is bool b && b;
    }

    GameObject GetLoadedModel()
    {
        return viewer != null && loadedModelField != null ? loadedModelField.GetValue(viewer) as GameObject : null;
    }

    bool RaycastCursor(out RaycastHit hit)
    {
        hit = default;
        if (viewer == null || viewer.mainCamera == null || Mouse.current == null) return false;
        Ray ray = viewer.mainCamera.ScreenPointToRay(Mouse.current.position.ReadValue());
        return Physics.Raycast(ray, out hit);
    }

    void PlaceCard(Vector3 point, Vector3 normal)
    {
        pinHairCardMethod?.Invoke(viewer, new object[] { point, normal });
    }

    void SprayOne(RaycastHit centerHit)
    {
        Vector3 n = centerHit.normal.sqrMagnitude > .000001f ? centerHit.normal.normalized : Vector3.up;
        Vector3 tangent = Vector3.Cross(n, Vector3.up);
        if (tangent.sqrMagnitude < .000001f) tangent = Vector3.Cross(n, Vector3.right);
        tangent.Normalize();
        Vector3 bitangent = Vector3.Cross(n, tangent).normalized;

        float radius = Mathf.Max(.001f, brushRadius);
        for (int attempt = 0; attempt < 6; attempt++)
        {
            float radial01 = Mathf.Sqrt(Random.value);
            float angle = Random.value * Mathf.PI * 2f;
            float acceptance = Mathf.Lerp(1f, 1f - radial01, Mathf.Clamp01(sprayFalloff));
            if (Random.value > acceptance) continue;

            Vector3 offset = (tangent * Mathf.Cos(angle) + bitangent * Mathf.Sin(angle)) * (radial01 * radius);
            Vector3 approximate = centerHit.point + offset;
            float lift = radius + .05f;
            Ray surfaceRay = new Ray(approximate + n * lift, -n);
            if (!Physics.Raycast(surfaceRay, out RaycastHit hit, lift * 2f)) continue;

            PlaceCard(hit.point, hit.normal);
            return;
        }
    }

    // EVEN. Scatters candidates like SPRAY, then refuses any that would land closer than the
    // spacing to a card that already exists.
    //
    // Uniform over the disc, not over the radius - the sqrt on radial01 is what stops a scatter
    // piling up in the middle - and deliberately WITHOUT spray's falloff weighting. Spray thins
    // toward the edge of its brush on purpose; an even fill has no centre, and it is the spacing
    // test, not a probability, that decides where a card is allowed.
    //
    // More attempts than spray gets. Once an area is filling up most of them fail, and the
    // difference between six and sixteen is the difference between a brush that stops working
    // before the patch is full and one that fills it and then quietly stops accepting. They are
    // cheap: a scatter, a short ray, and a squared-distance walk of an array already in hand.
    void PlaceEvenly(RaycastHit centerHit)
    {
        Vector3 n = centerHit.normal.sqrMagnitude > .000001f ? centerHit.normal.normalized : Vector3.up;
        Vector3 tangent = Vector3.Cross(n, Vector3.up);
        if (tangent.sqrMagnitude < .000001f) tangent = Vector3.Cross(n, Vector3.right);
        tangent.Normalize();
        Vector3 bitangent = Vector3.Cross(n, tangent).normalized;

        float radius = Mathf.Max(.001f, brushRadius);
        float spacing = Mathf.Max(.0005f, cardSpacing);

        EnsureSpacingRoots();

        // Reach is TWICE the brush radius, not once.
        //
        // The scatter offset is measured on the tangent plane and the candidate is then dropped
        // onto the surface, so its distance from the brush centre is the offset plus however far
        // the surface has fallen away underneath it. On a convex surface the far-side filter
        // below caps that at about 1.24 times the offset; doubling covers it with room to spare
        // and still narrows the list to a fraction of a groom.
        float reach = 2f * radius + spacing;
        NarrowSpacingRoots(spacingNearby, centerHit.point, reach);

        // Only when symmetry is actually on, and only once per tick rather than once per
        // candidate - TryMirror resolves the model by a scene-wide type search and snaps with a
        // raycast, which is not something to run sixteen times for an answer that cannot change
        // within a tick.
        mirrorZoneValid = false;
        if (GroomSymmetryAuthority.Enabled)
        {
            Vector3 mirroredCentre;
            if (GroomSymmetryAuthority.TryMirrorPoint(centerHit.point, out mirroredCentre))
            {
                mirrorZoneValid = true;
                NarrowSpacingRoots(spacingNearbyMirror, mirroredCentre, reach);
            }
        }

        for (int attempt = 0; attempt < EvenAttempts; attempt++)
        {
            float radial01 = Mathf.Sqrt(Random.value);
            float angle = Random.value * Mathf.PI * 2f;

            Vector3 offset = (tangent * Mathf.Cos(angle) + bitangent * Mathf.Sin(angle)) * (radial01 * radius);
            Vector3 approximate = centerHit.point + offset;
            float lift = radius + .05f;
            Ray surfaceRay = new Ray(approximate + n * lift, -n);
            if (!Physics.Raycast(surfaceRay, out RaycastHit hit, lift * 2f)) continue;

            // Off the far side of the head. A candidate is scattered on the tangent plane and
            // then dropped onto whatever the ray finds, and with a wide brush on a model about a
            // third of a unit across that ray is long enough to reach the neck, an ear's far
            // face, or the back of the skull. A stray card there is worse in EVEN than in SPRAY,
            // because it also joins spacingRoots and vetoes correct placements for the rest of
            // the stroke.
            if (Vector3.Dot(hit.normal.normalized, n) < .3f) continue;

            if (IsCrowded(spacingNearby, hit.point, spacing)) continue;

            // The mirror has to clear the spacing as well. PinHairCard places it unconditionally,
            // so without this a groom painted asymmetrically and then mirrored drops cards on top
            // of far-side neighbours while the slider still claims a spacing is being kept.
            Vector3 mirroredPoint = Vector3.zero;
            Vector3 mirroredNormal;
            bool mirrored = mirrorZoneValid &&
                GroomSymmetryAuthority.TryMirror(hit.point, hit.normal, out mirroredPoint, out mirroredNormal);

            // Against the MIRROR's shortlist, not the brush's.
            if (mirrored && IsCrowded(spacingNearbyMirror, mirroredPoint, spacing)) continue;

            // And against EACH OTHER, which the two tests above cannot cover because neither
            // point is in the list yet. It matters on the parting: TryMirror only declines within
            // about 1.2 percent of the model's width of the midline, so a symmetric pair can be
            // as little as eight thousandths of a unit apart - which is the DEFAULT spacing, and
            // a sixth of what the slider can ask for.
            if (mirrored && (mirroredPoint - hit.point).sqrMagnitude < spacing * spacing) continue;

            PlaceCard(hit.point, hit.normal);

            // Only into spacingRoots. This tick is over the moment it places - the return below -
            // and both shortlists are rebuilt from spacingRoots at the top of the next one, so an
            // entry added to either of them here could never be read.
            spacingRoots.Add(hit.point);
            if (mirrored) spacingRoots.Add(mirroredPoint);

            lastEvenPlaceTime = Time.unscaledTime;
            return;
        }
    }

    // Every card root on the model, once per stroke. See the spacingRoots field for why it is
    // cached rather than asked for per candidate.
    void EnsureSpacingRoots()
    {
        if (spacingRootsValid) return;
        spacingRootsValid = true;
        spacingRoots.Clear();

        // EVERY group, which is where this parts company with ERASE just below. Density is
        // density: a card another group put here is still occupying that piece of scalp, and
        // measuring against the current group alone would let a second pass over the same patch
        // silently double its density while the slider still claimed a spacing was being kept.
        HairCard[] cards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        for (int i = 0; i < cards.Length; i++)
        {
            HairCard card = cards[i];
            if (card == null) continue;

            // A group SOLO is hiding does not get a vote. Its cards still exist and still occupy
            // scalp, but you cannot see them and ERASE cannot reach them either - it is
            // current-group only - so leaving them in would give a dead brush over an area that
            // looks empty, with nothing on screen to explain it. SOLO means "this group as if it
            // were alone", and that has to include what counts as crowded.
            // Never the group being painted into, even when SOLO is hiding it. SOLO is a
            // per-group toggle and the current group is not necessarily one of the soloed ones,
            // so this is reachable: solo A, click B, paint. Skipping B's own cards would have
            // EVEN stacking a fresh set on the same spots every stroke, forever.
            if (card.groupId != viewer.currentGroupId &&
                !GroupSoloVisibilityAuthority.IsGroupVisible(card.groupId)) continue;

            // The surface root, not transform.position - the two differ by the Embed Depth, and
            // reading the buried one would make the spacing answer change when a slider that has
            // nothing to do with density is moved.
            Vector3 root = card.GetSpawnHitPoint();
            if (root == Vector3.zero) root = card.transform.position;
            spacingRoots.Add(root);
        }
    }

    // Everything within reach of this tick's brush. A candidate can land anywhere inside the
    // brush radius and excludes anything inside the spacing of it, so nothing further than the
    // two added together can affect the answer.
    void NarrowSpacingRoots(List<Vector3> into, Vector3 center, float reach)
    {
        into.Clear();
        float limit = reach * reach;
        for (int i = 0; i < spacingRoots.Count; i++)
        {
            if ((spacingRoots[i] - center).sqrMagnitude <= limit) into.Add(spacingRoots[i]);
        }
    }

    // Takes the list to walk rather than reading a field, because there are two of them and
    // walking the wrong one is a test that always passes.
    static bool IsCrowded(List<Vector3> roots, Vector3 point, float spacing)
    {
        float limit = spacing * spacing;
        for (int i = 0; i < roots.Count; i++)
        {
            if ((roots[i] - point).sqrMagnitude < limit) return true;
        }
        return false;
    }

    void EraseAt(Vector3 center)
    {
        bool removed = EraseAtPoint(center);

        // SYMMETRY. Erasing has to mirror too, or the two sides drift apart the moment you
        // tidy anything up - you would be able to paint symmetrically but never correct
        // symmetrically, which is worse than having no symmetry at all.
        //
        // Mirroring the BRUSH CENTRE rather than hunting for each card's partner is what makes
        // this robust: it does not care whether the cards on the far side were placed by
        // symmetry, painted by hand, or moved since, and it needs no pairing bookkeeping to
        // go stale.
        Vector3 mirroredCentre;
        if (GroomSymmetryAuthority.TryMirrorPoint(center, out mirroredCentre))
        {
            if (EraseAtPoint(mirroredCentre)) removed = true;
        }

        if (removed) refreshGroupListMethod?.Invoke(viewer, null);
    }

    bool EraseAtPoint(Vector3 center)
    {
        HairCard[] cards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        bool removed = false;
        for (int i = 0; i < cards.Length; i++)
        {
            HairCard card = cards[i];
            if (card == null || card.groupId != viewer.currentGroupId) continue;
            Vector3 root = card.GetSpawnHitPoint();
            if (root == Vector3.zero) root = card.transform.position;
            if (Vector3.Distance(root, center) > brushRadius) continue;

            if (viewer.lastPlacedCard == card) viewer.lastPlacedCard = null;
            Destroy(card.gameObject);
            removed = true;
        }
        return removed;
    }

    void SelectNearestGroup(Vector3 point)
    {
        HairCard[] cards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        HairCard nearest = null;
        float best = float.PositiveInfinity;
        foreach (HairCard card in cards)
        {
            if (card == null) continue;
            float d2 = (card.transform.position - point).sqrMagnitude;
            if (d2 < best) { best = d2; nearest = card; }
        }
        if (nearest != null) selectGroupMethod?.Invoke(viewer, new object[] { nearest.groupId });
    }

    void CycleMode()
    {
        // Counted from the enum rather than written as a literal. It was a literal 4, and adding
        // a fifth mode to the enum would have left SHIFT cycling past four of them forever with
        // nothing to say why the new one was unreachable.
        int count = System.Enum.GetValues(typeof(PlacementMode)).Length;
        SetMode((PlacementMode)(((int)mode + 1) % count));
    }

    void SetMode(PlacementMode next)
    {
        mode = next;
        nextActionTime = 0f;

        // The stroke that built it is over by definition.
        spacingRootsValid = false;

        UpdateModeUI();
    }

    void EnsureSegmentMinimum()
    {
        if (viewer == null || viewer.groomingSliderPanelGO == null) return;
        Slider[] sliders = viewer.groomingSliderPanelGO.GetComponentsInChildren<Slider>(true);
        foreach (Slider slider in sliders)
        {
            if (slider == null || slider.name != "Segments_Slider") continue;
            slider.minValue = 1f;
            slider.maxValue = 60f;
            slider.wholeNumbers = true;
            if (boundSegmentsSlider != slider)
            {
                boundSegmentsSlider = slider;
                slider.onValueChanged.AddListener(ApplySegmentOverride);
            }
            break;
        }
    }

    void ApplySegmentOverride(float value)
    {
        if (viewer == null || GetBool(selectionHotspotField)) return;
        int target = Mathf.Clamp(Mathf.RoundToInt(value), 1, 60);
        viewer.currentSegments = target;
        HairCard[] cards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        foreach (HairCard card in cards)
            if (card != null && card.groupId == viewer.currentGroupId)
                card.SetSegments(target);
    }

    void EnsureUI()
    {
        if (viewer == null || viewer.groomingSliderPanelGO == null) return;
        Transform panel = viewer.groomingSliderPanelGO.transform;

        Transform existing = panel.Find("PlacementModeRow");
        if (existing != null)
        {
            modeRow = existing.gameObject;
            if (modeButton == null) modeButton = existing.GetComponentInChildren<Button>(true);
            if (modeText == null && modeButton != null) modeText = modeButton.GetComponentInChildren<TextMeshProUGUI>(true);
            if (radiusRow == null)
            {
                Transform t = panel.Find("PlacementRadius_Row");
                if (t != null) radiusRow = t.gameObject;
            }
            if (falloffRow == null)
            {
                Transform t = panel.Find("SprayFalloff_Row");
                if (t != null) falloffRow = t.gameObject;
            }
            if (spacingRow == null)
            {
                Transform t = panel.Find("PlacementSpacing_Row");
                if (t != null) spacingRow = t.gameObject;
            }

            // The SLIDERS, not just the rows. This branch only re-grabbed the row objects, so
            // after a panel rebuild radiusSlider stayed null and the [ ] hotkey's handle resync
            // at the top of Update quietly stopped working - the value changed and the handle did
            // not follow it.
            if (radiusSlider == null && radiusRow != null) radiusSlider = radiusRow.GetComponentInChildren<Slider>(true);
            if (falloffSlider == null && falloffRow != null) falloffSlider = falloffRow.GetComponentInChildren<Slider>(true);
            if (spacingSlider == null && spacingRow != null) spacingSlider = spacingRow.GetComponentInChildren<Slider>(true);

            UpdateModeUI();
            return;
        }

        modeRow = new GameObject("PlacementModeRow", typeof(RectTransform), typeof(Image), typeof(Button));
        modeRow.transform.SetParent(panel, false);
        modeRow.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 38f);
        modeRow.GetComponent<Image>().color = ModeColor(mode);
        modeButton = modeRow.GetComponent<Button>();
        modeButton.onClick.AddListener(CycleMode);

        GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textGO.transform.SetParent(modeRow.transform, false);
        RectTransform tr = textGO.GetComponent<RectTransform>();
        tr.anchorMin = Vector2.zero;
        tr.anchorMax = Vector2.one;
        tr.offsetMin = Vector2.zero;
        tr.offsetMax = Vector2.zero;
        modeText = textGO.GetComponent<TextMeshProUGUI>();
        modeText.fontSize = 15f;
        modeText.fontStyle = FontStyles.Bold;
        modeText.alignment = TextAlignmentOptions.Center;
        modeText.color = Color.white;
        modeText.textWrappingMode = TextWrappingModes.NoWrap;
        modeText.raycastTarget = false;

        Transform top = panel.Find("TopControlsRow");
        if (top != null) modeRow.transform.SetSiblingIndex(Mathf.Min(top.GetSiblingIndex() + 1, panel.childCount - 1));

        radiusRow = CreateBrushSlider(panel, "PlacementRadius_Row", "Brush Radius", .002f, .20f, brushRadius, v => brushRadius = v, out radiusSlider);
        falloffRow = CreateBrushSlider(panel, "SprayFalloff_Row", "Spray Falloff", 0f, 1f, sprayFalloff, v => sprayFalloff = v, out falloffSlider);
        spacingRow = CreateBrushSlider(panel, "PlacementSpacing_Row", "Card Spacing", .001f, .05f, cardSpacing, v => cardSpacing = v, out spacingSlider);

        int afterMode = modeRow.transform.GetSiblingIndex() + 1;
        radiusRow.transform.SetSiblingIndex(Mathf.Min(afterMode, panel.childCount - 1));
        falloffRow.transform.SetSiblingIndex(Mathf.Min(afterMode + 1, panel.childCount - 1));
        spacingRow.transform.SetSiblingIndex(Mathf.Min(afterMode + 2, panel.childCount - 1));
        UpdateModeUI();
    }

    GameObject CreateBrushSlider(Transform parent, string name, string label, float min, float max, float value, UnityEngine.Events.UnityAction<float> changed, out Slider slider)
    {
        GameObject row = new GameObject(name, typeof(RectTransform), typeof(VerticalLayoutGroup));
        row.transform.SetParent(parent, false);
        row.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 38f);
        VerticalLayoutGroup layout = row.GetComponent<VerticalLayoutGroup>();
        layout.spacing = 1f;
        layout.childControlWidth = true;
        layout.childControlHeight = false;

        GameObject labelGO = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        labelGO.transform.SetParent(row.transform, false);
        labelGO.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 17f);
        TextMeshProUGUI tmp = labelGO.GetComponent<TextMeshProUGUI>();
        tmp.fontSize = 14f;
        tmp.color = Color.white;
        tmp.text = label + ": " + value.ToString("F3");

        GameObject sliderGO = new GameObject(label.Replace(" ", "") + "_Slider", typeof(RectTransform), typeof(Slider));
        sliderGO.transform.SetParent(row.transform, false);
        sliderGO.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 17f);
        slider = sliderGO.GetComponent<Slider>();
        slider.minValue = min;
        slider.maxValue = max;
        slider.value = value;

        GameObject background = new GameObject("Background", typeof(RectTransform), typeof(Image));
        background.transform.SetParent(sliderGO.transform, false);
        background.GetComponent<Image>().color = new Color(.28f, .28f, .28f);
        RectTransform bg = background.GetComponent<RectTransform>();
        bg.anchorMin = new Vector2(0f, .3f);
        bg.anchorMax = new Vector2(1f, .7f);
        bg.offsetMin = Vector2.zero;
        bg.offsetMax = Vector2.zero;

        GameObject fillArea = new GameObject("Fill Area", typeof(RectTransform));
        fillArea.transform.SetParent(sliderGO.transform, false);
        RectTransform fa = fillArea.GetComponent<RectTransform>();
        fa.anchorMin = new Vector2(0f, .3f);
        fa.anchorMax = new Vector2(1f, .7f);
        fa.offsetMin = Vector2.zero;
        fa.offsetMax = Vector2.zero;

        GameObject fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
        fill.transform.SetParent(fillArea.transform, false);
        fill.GetComponent<Image>().color = new Color(.2f, .6f, 1f);
        slider.fillRect = fill.GetComponent<RectTransform>();
        slider.fillRect.anchorMin = Vector2.zero;
        slider.fillRect.anchorMax = Vector2.zero;
        slider.fillRect.sizeDelta = Vector2.zero;

        GameObject handleArea = new GameObject("Handle Slide Area", typeof(RectTransform));
        handleArea.transform.SetParent(sliderGO.transform, false);
        RectTransform ha = handleArea.GetComponent<RectTransform>();
        ha.anchorMin = Vector2.zero;
        ha.anchorMax = Vector2.one;
        ha.offsetMin = Vector2.zero;
        ha.offsetMax = Vector2.zero;

        GameObject handle = new GameObject("Handle", typeof(RectTransform), typeof(Image));
        handle.transform.SetParent(handleArea.transform, false);
        handle.GetComponent<Image>().color = Color.white;
        slider.handleRect = handle.GetComponent<RectTransform>();
        slider.handleRect.sizeDelta = new Vector2(18f, 0f);

        slider.onValueChanged.AddListener(v =>
        {
            tmp.text = label + ": " + v.ToString("F3");
            changed?.Invoke(v);
        });
        return row;
    }

    void UpdateModeUI()
    {
        if (modeText != null) modeText.text = "PLACEMENT: " + mode.ToString().ToUpperInvariant() + "   [SHIFT]";
        if (modeRow != null)
        {
            Image image = modeRow.GetComponent<Image>();
            if (image != null) image.color = ModeColor(mode);
        }
        if (radiusRow != null)
            radiusRow.SetActive(mode == PlacementMode.Spray || mode == PlacementMode.Even || mode == PlacementMode.Erase);
        if (falloffRow != null) falloffRow.SetActive(mode == PlacementMode.Spray);
        if (spacingRow != null) spacingRow.SetActive(mode == PlacementMode.Even);
    }

    static Color ModeColor(PlacementMode value)
    {
        switch (value)
        {
            case PlacementMode.Paint: return new Color(.18f, .48f, .30f);
            case PlacementMode.Spray: return new Color(.58f, .40f, .15f);
            case PlacementMode.Even: return new Color(.24f, .46f, .46f);
            case PlacementMode.Erase: return new Color(.62f, .20f, .20f);
            default: return new Color(.20f, .42f, .68f);
        }
    }

    void EnsureBrushPreview()
    {
        // Attempted once, not once per frame. The old guard was "the ring does not exist yet",
        // which could only run once ever; this one is "there is no material", which on a build
        // that stripped all three shaders would be true forever - and EnsureBrushPreview is
        // called every frame the cursor is over the model.
        if (brushMaterial == null && !brushMaterialAttempted)
        {
            brushMaterialAttempted = true;
            Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            if (shader != null) brushMaterial = new Material(shader) { name = "HairBrushPlacementPreview" };
        }

        if (brushPreview == null) brushPreview = CreatePreviewRing("PlacementBrushPreview");

        // The second ring is EVEN's and only EVEN's. Spacing is not a number you can tune by
        // reading it - what matters is how big the exclusion disc is against the brush you are
        // filling with - so the mode that owns the number draws it.
        if (spacingPreview == null) spacingPreview = CreatePreviewRing("PlacementSpacingPreview");
    }

    LineRenderer CreatePreviewRing(string name)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(transform, false);
        LineRenderer line = go.AddComponent<LineRenderer>();
        line.loop = true;
        line.useWorldSpace = true;
        line.positionCount = CircleSegments;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;

        // sharedMaterial, not material. Assigning through .material makes Unity instantiate a
        // copy and hand the renderer that, leaving the field pointing at a source nothing draws -
        // so OnDestroy frees the one object that was never used and leaks the one that was.
        if (brushMaterial != null) line.sharedMaterial = brushMaterial;
        line.enabled = false;
        return line;
    }

    void ShowBrushPreview(Vector3 point, Vector3 normal)
    {
        EnsureBrushPreview();
        if (brushPreview == null) return;

        Color brush = new Color(.25f, .85f, 1f, .95f);
        if (mode == PlacementMode.Erase) brush = new Color(1f, .25f, .18f, .95f);

        // Dimmed while EVEN is painting and getting nowhere, which means the area under the brush
        // is full at this spacing. Only while the button is down - a ring that dimmed on hover
        // would just be reporting that nothing is being placed because nothing was asked for.
        if (mode == PlacementMode.Even && Mouse.current != null && Mouse.current.leftButton.isPressed &&
            Time.unscaledTime - lastEvenPlaceTime > EvenBlockedDelay)
            brush = new Color(.25f, .85f, 1f, .35f);
        DrawPreviewRing(brushPreview, point, normal, Mathf.Max(.001f, brushRadius), brush);

        if (spacingPreview == null) return;
        if (mode != PlacementMode.Even)
        {
            spacingPreview.enabled = false;
            return;
        }

        // Lifted a shade further off the surface than the brush ring, so the two do not
        // z-fight each other where a large spacing meets a small brush.
        DrawPreviewRing(spacingPreview, point + normal.normalized * .0004f, normal,
            Mathf.Max(.0005f, cardSpacing), new Color(.45f, 1f, .78f, .95f));
    }

    void DrawPreviewRing(LineRenderer line, Vector3 point, Vector3 normal, float radius, Color color)
    {
        if (line == null) return;

        Vector3 n = normal.sqrMagnitude > .000001f ? normal.normalized : Vector3.up;
        Vector3 tangent = Vector3.Cross(n, Vector3.up);
        if (tangent.sqrMagnitude < .000001f) tangent = Vector3.Cross(n, Vector3.right);
        tangent.Normalize();
        Vector3 bitangent = Vector3.Cross(n, tangent).normalized;
        Vector3 center = point + n * .001f;

        for (int i = 0; i < CircleSegments; i++)
        {
            float angle = (i / (float)CircleSegments) * Mathf.PI * 2f;
            line.SetPosition(i, center + (tangent * Mathf.Cos(angle) + bitangent * Mathf.Sin(angle)) * radius);
        }

        line.startColor = color;
        line.endColor = color;
        line.widthMultiplier = Mathf.Clamp(radius * .025f, .00035f, .0025f);
        line.enabled = true;
    }

    void HideBrushPreview()
    {
        if (brushPreview != null) brushPreview.enabled = false;
        if (spacingPreview != null) spacingPreview.enabled = false;
    }

    void OnDestroy()
    {
        if (brushMaterial != null) Destroy(brushMaterial);
    }
}
