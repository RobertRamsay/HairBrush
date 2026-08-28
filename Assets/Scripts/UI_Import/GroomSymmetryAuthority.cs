using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// SYMMETRY: paint on one side, get the mirrored card on the other.
//
// Three responsibilities, deliberately kept in one place:
//
//   1. The on/off state, which is session-only. Like SOLO, symmetry is a way of working
//      rather than a property of the groom, so it is never written to a project file. What
//      IS written is each card's own `mirrored` flag - a mirrored card stays a mirror across
//      save and load whether or not the toggle happens to be on when you reopen the project.
//
//   2. The symmetry plane, resolved from the loaded model and validated against its actual
//      vertices, so a model that is NOT symmetric says so instead of quietly producing
//      rubbish.
//
//   3. The toggle button in the left panel, under the instructions.
//
// The mirror itself is deliberately NOT here - HairCard.mirrored owns the geometry side. See
// the comment on that field for why the mirror is a property of the card rather than of its
// stored numbers.
[DefaultExecutionOrder(8950)]
public class GroomSymmetryAuthority : MonoBehaviour
{
    // GroupPanelPostHintStats orders the left panel and needs to know this button by name.
    public const string ButtonName = "SymmetryToggleButton";

    private const float ScanInterval = .25f;
    private const float ButtonHeight = 32f;

    // How many mesh vertices to sample when validating the plane. The test is O(samples x
    // vertices) in the worst case, but it runs ONCE per model load, not per frame.
    private const int ValidationSamples = 220;

    // A sampled vertex counts as matched if its mirror lands within this fraction of the
    // model's own width. Generous enough to tolerate an asymmetric ear or a stray triangle,
    // tight enough that a genuinely one-sided model fails.
    private const float MatchToleranceFraction = .02f;

    // Below this score the plane is reported as unreliable. Real heads score well above it;
    // a lopsided prop scores far below.
    private const float ReliableScore = .85f;

    // Cards whose mirror would land closer to the plane than this - again as a fraction of
    // model width - are not mirrored at all. Without it, painting down the parting line
    // stacks two near-identical cards on the same spot, which reads as "symmetry doubled my
    // density" and is impossible to clean up afterwards.
    private const float MidlineSkipFraction = .012f;

    // ---- state ------------------------------------------------------------------------
    // Initialised here so nothing downstream has to test for existence.
    // True only while the user has the toggle on. Named symmetryOn rather than the obvious
    // `enabled` because this is a MonoBehaviour: a static field called `enabled` would hide
    // Behaviour.enabled, and every plain `enabled = false` in this file would then be
    // ambiguous to read and one refactor away from switching the component off instead.
    private static bool symmetryOn;
    private static bool planeResolved;
    private static float planeConfidence;
    private static float modelWidth = 1f;

    // Statics survive "Enter Play Mode -> Disable Domain Reload", so a symmetry left switched
    // on when play stopped would still be on - and silently doubling every stroke - when play
    // starts again.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        symmetryOn = false;
        planeResolved = false;
        planeConfidence = 0f;
        modelWidth = 1f;

        // Cleared for the same reason as the flags above: with domain reload disabled the
        // static survives play stop still pointing at last session's destroyed authority.
        instance = null;
    }

    public static bool Enabled
    {
        get { return symmetryOn; }
    }

    public static float PlaneConfidence
    {
        get { return planeConfidence; }
    }

    public static bool PlaneIsReliable
    {
        get { return planeResolved && planeConfidence >= ReliableScore; }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Spawn()
    {
        if (FindFirstObjectByType<GroomSymmetryAuthority>() != null) return;
        GameObject go = new GameObject(nameof(GroomSymmetryAuthority));
        DontDestroyOnLoad(go);
        go.AddComponent<GroomSymmetryAuthority>();
    }

    // ---- the mirror ---------------------------------------------------------------------
    //
    // Everything a caller needs: hand it a surface hit, get back the mirrored hit. Returns
    // false when symmetry is off, when there is no model to mirror about, or when the point
    // is close enough to the midline that mirroring it would just stack a duplicate.
    //
    // The mirror is taken in the MODEL's local space, not world space. That is what makes it
    // survive the model being moved or re-rotated: the OBJ importer recentres every mesh on
    // its own bounding box, so model-local X = 0 is the midline by construction.
    public static bool TryMirror(Vector3 worldPoint, Vector3 worldNormal, out Vector3 mirroredPoint, out Vector3 mirroredNormal)
    {
        return TryMirror(worldPoint, worldNormal, true, out mirroredPoint, out mirroredNormal);
    }

    // snapToSurface should only be true when worldNormal is a REAL surface normal. The snap
    // raycast is aimed along it, so feeding it a placeholder direction would send the ray off
    // at an angle and move the mirrored point somewhere the reflection never asked for.
    public static bool TryMirror(Vector3 worldPoint, Vector3 worldNormal, bool snapToSurface, out Vector3 mirroredPoint, out Vector3 mirroredNormal)
    {
        mirroredPoint = worldPoint;
        mirroredNormal = worldNormal;

        if (!symmetryOn) return false;

        Transform model = ResolveModelTransform();
        if (model == null) return false;

        Vector3 localPoint = model.InverseTransformPoint(worldPoint);
        if (Mathf.Abs(localPoint.x) < modelWidth * MidlineSkipFraction) return false;

        Vector3 localNormal = model.InverseTransformDirection(worldNormal);

        localPoint.x = -localPoint.x;
        localNormal.x = -localNormal.x;

        mirroredPoint = model.TransformPoint(localPoint);
        mirroredNormal = model.TransformDirection(localNormal).normalized;

        if (!snapToSurface) return true;

        // Snap the mirrored point back onto the real surface. On a perfectly symmetric mesh
        // this changes nothing; on a slightly asymmetric one it stops the mirrored card
        // floating above or sinking into the skin. The ray starts outside and aims inward, so
        // it hits the front face rather than escaping through the back.
        Ray ray = new Ray(mirroredPoint + mirroredNormal * (modelWidth * .25f), -mirroredNormal);
        RaycastHit hit;
        if (Physics.Raycast(ray, out hit, modelWidth * .5f))
        {
            // Only accept the snap if it landed near where the reflection said it should. A
            // far-away hit means the ray found some unrelated piece of geometry, and the pure
            // reflection is the better answer.
            if (Vector3.Distance(hit.point, mirroredPoint) < modelWidth * .05f)
            {
                mirroredPoint = hit.point;
                mirroredNormal = hit.normal;
            }
        }

        return true;
    }

    // For callers that have a point but no surface normal - the eraser, whose brush centre is
    // a position in space rather than a placement. Pure reflection, no surface snap: there is
    // no normal to aim a snap ray along, and an eraser only needs to be in roughly the right
    // place because the brush radius does the rest.
    public static bool TryMirrorPoint(Vector3 worldPoint, out Vector3 mirroredPoint)
    {
        Vector3 ignoredNormal;
        return TryMirror(worldPoint, Vector3.up, false, out mirroredPoint, out ignoredNormal);
    }

    // ---- plane resolution and validation --------------------------------------------------
    private static Transform ResolveModelTransform()
    {
        ModelViewer viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer == null) return null;

        FieldInfo field = typeof(ModelViewer).GetField("loadedModel", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null) return null;

        GameObject model = field.GetValue(viewer) as GameObject;
        if (model == null) return null;
        return model.transform;
    }

    // Score how well the mesh actually mirrors about local X = 0.
    //
    // Sample vertices spread across the whole mesh, reflect each one, and ask whether any
    // vertex sits near where the reflection landed. The fraction that find a partner is the
    // score. A head scores near 1; a model built entirely on one side of the origin scores
    // near 0, and the toast says so rather than letting the user paint into thin air.
    private static void ResolvePlane(Transform model)
    {
        planeResolved = false;
        planeConfidence = 0f;
        modelWidth = 1f;

        if (model == null) return;

        MeshFilter[] filters = model.GetComponentsInChildren<MeshFilter>();
        Mesh mesh = null;
        for (int i = 0; i < filters.Length; i++)
        {
            if (filters[i] == null) continue;
            if (filters[i].sharedMesh == null) continue;
            mesh = filters[i].sharedMesh;
            break;
        }
        if (mesh == null) return;

        Vector3[] vertices = mesh.vertices;
        if (vertices == null || vertices.Length < 8) return;

        modelWidth = Mathf.Max(.0001f, mesh.bounds.size.x);
        float tolerance = modelWidth * MatchToleranceFraction;
        float toleranceSqr = tolerance * tolerance;

        int step = Mathf.Max(1, vertices.Length / ValidationSamples);
        int tested = 0;
        int matched = 0;

        for (int i = 0; i < vertices.Length; i += step)
        {
            Vector3 source = vertices[i];

            // Vertices sitting on the plane are their own mirror and would pass trivially,
            // which flatters the score on a model that is only symmetric near the middle.
            if (Mathf.Abs(source.x) < tolerance) continue;

            Vector3 target = new Vector3(-source.x, source.y, source.z);
            tested++;

            for (int j = 0; j < vertices.Length; j += step)
            {
                if ((vertices[j] - target).sqrMagnitude <= toleranceSqr)
                {
                    matched++;
                    break;
                }
            }
        }

        if (tested == 0) return;

        planeResolved = true;
        planeConfidence = (float)matched / tested;
    }

    // ---- UI --------------------------------------------------------------------------
    //
    // The live authority, so the X shortcut does not scan the scene on every press. Initialised
    // to null here and cleared in OnDestroy rather than trusted to stay valid.
    private static GroomSymmetryAuthority instance = null;

    private void Awake()
    {
        instance = this;
    }

    private void OnDestroy()
    {
        if (instance == this) instance = null;
    }

    private GameObject boundPanel;
    private Button button;
    private TextMeshProUGUI label;
    private Image image;
    private float nextScan;
    private Transform lastModel;

    private void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + ScanInterval;

        Transform model = ResolveModelTransform();
        if (model != lastModel)
        {
            lastModel = model;
            ResolvePlane(model);

            // A new model is a new groom. Leaving symmetry on across a model swap would apply
            // the old session's intent to a body it was never chosen for.
            symmetryOn = false;
        }

        // The left panel is destroyed and rebuilt on every model and project load, so the
        // binding has to be re-checked rather than established once.
        GameObject panel = GameObject.Find("GroupManagerPanel");
        if (panel == null)
        {
            boundPanel = null;
            button = null;
            label = null;
            image = null;
            return;
        }

        if (boundPanel != panel || button == null) Bind(panel);
        Repaint();
    }

    private void Bind(GameObject panel)
    {
        boundPanel = panel;

        Transform existing = panel.transform.Find(ButtonName);
        if (existing != null)
        {
            button = existing.GetComponent<Button>();
            label = existing.GetComponentInChildren<TextMeshProUGUI>(true);
            image = existing.GetComponent<Image>();
            if (button != null) return;

            // A half-built husk from an interrupted rebuild - start again rather than adopt it.
            Destroy(existing.gameObject);
        }

        BuildButton(panel.transform);
    }

    private void BuildButton(Transform parent)
    {
        GameObject go = new GameObject(ButtonName, typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, ButtonHeight);
        go.GetComponent<LayoutElement>().preferredHeight = ButtonHeight;

        image = go.GetComponent<Image>();
        button = go.GetComponent<Button>();
        button.onClick.AddListener(Toggle);

        GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textGO.transform.SetParent(go.transform, false);
        RectTransform textRect = textGO.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;

        label = textGO.GetComponent<TextMeshProUGUI>();
        label.fontSize = 13f;
        label.fontStyle = FontStyles.Bold;
        label.alignment = TextAlignmentOptions.Center;
        label.color = Color.white;
        label.raycastTarget = false;

        // First guess only. GroupPanelPostHintStats.MaintainPanelOrder is the running order
        // authority for this panel and puts the button under INPUT KEYS every scan.
        Transform above = parent.Find(InputKeysDialog.ButtonName);
        if (above != null) go.transform.SetSiblingIndex(Mathf.Clamp(above.GetSiblingIndex() + 1, 0, parent.childCount - 1));

        Repaint();
    }

    private void Repaint()
    {
        // Only write when the value actually changed - a TMP text assignment forces a mesh
        // rebuild of the label whether or not the string differs.
        if (label != null)
        {
            string text = "SYMMETRY: OFF";
            if (symmetryOn) text = "SYMMETRY: ON";
            if (label.text != text) label.text = text;
        }

        if (image != null)
        {
            Color colour = new Color(.28f, .28f, .28f, 1f);
            if (symmetryOn)
            {
                colour = new Color(.20f, .58f, .45f, 1f);
                // Amber rather than green when the plane could not be trusted, so an
                // unreliable mirror is visible at a glance and not just in a toast that has
                // already faded.
                if (!PlaneIsReliable) colour = new Color(.72f, .48f, .12f, 1f);
            }
            if (image.color != colour) image.color = colour;
        }
    }

    // The X shortcut's way in - deliberately the same call the button makes, rather than a
    // second path that sets symmetryOn directly. Setting the flag from outside would skip the
    // plane resolve, the toast and the repaint, and symmetry would come on with the button
    // still painted off.
    public static void RequestToggle()
    {
        GroomSymmetryAuthority live = instance;
        if (live == null) live = FindFirstObjectByType<GroomSymmetryAuthority>();
        if (live == null) return;
        live.Toggle();
    }

    private void Toggle()
    {
        symmetryOn = !symmetryOn;

        if (symmetryOn)
        {
            if (lastModel == null) ResolvePlane(ResolveModelTransform());

            if (!planeResolved)
            {
                StatusToast.Show("SYMMETRY ON - no model loaded yet, nothing to mirror about.", true);
            }
            else if (PlaneIsReliable)
            {
                StatusToast.Show("SYMMETRY ON - mirroring about the model midline (" + Mathf.RoundToInt(planeConfidence * 100f) + "% match).");
            }
            else
            {
                StatusToast.Show("SYMMETRY ON - but this model is only " + Mathf.RoundToInt(planeConfidence * 100f) + "% symmetric, so mirrored cards may not sit on the surface.", true);
            }
        }
        else
        {
            StatusToast.Show("SYMMETRY OFF");
        }

        // Repaint on the very next frame rather than waiting out the scan interval.
        nextScan = 0f;
        Repaint();
    }
}
