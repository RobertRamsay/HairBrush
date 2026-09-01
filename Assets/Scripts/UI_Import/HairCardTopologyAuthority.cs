using TMPro;
using UnityEngine;
using UnityEngine.UI;

// The global TOPOLOGY: SYMMETRIC / DYNAMIC toggle, on the left panel directly under CARD.
//
// A quad has two ways to become two triangles, and this is how that choice gets made. It sits
// next to CARD because the two are the same kind of decision - what shape the mesh is, globally,
// saved with the groom - and because a diamond is where the difference is easiest to see.
//
// WHAT IT IS NOT is a fix for the bug that produced it. Both settings fix that. Every quad used
// to take the SAME diagonal, which made the mesh asymmetric even when the card was not: measured
// against the true surface on a straight symmetric card, the ridge vertex's normal came out 13.9
// degrees off under TENT and 84.5 under DIAMOND, the same way on every card in the groom. Under
// either setting here it measures 0.00. The toggle is about what to do with the freedom that is
// left over.
//
//   SYMMETRIC  The diagonal alternates by edge index and reads nothing else. A card's triangle
//              list is a function of its segment count alone, so it cannot re-cut underneath a
//              slider, and two exports of the same groom always have the same faces.
//
//   DYNAMIC    Each quad takes its shorter diagonal, with the alternating rule breaking exact
//              ties - which is what keeps a straight card symmetric, since its two diagonals are
//              exactly equal and a bare comparison would pick the same side every time. Folds
//              less on a twisted or waved card: 12.9 degrees against 13.9 on a hard case.
//
// SYMMETRIC IS THE DEFAULT, and deliberately not the better-measuring one. DYNAMIC makes the
// triangle list a property of the card's CURRENT SHAPE rather than of the card, so dragging a
// slider can re-cut the topology under the hair and a guide moved between two exports changes
// their faces as well as their vertices. That is a fair trade for somebody who wants it and a
// strange thing to hand somebody who did not ask for it.
[DefaultExecutionOrder(6801)]
public class HairCardTopologyAuthority : MonoBehaviour
{
    // GroupPanelPostHintStats orders the left panel and needs to know this button by name.
    // A panel child that is not in its list gets shoved around every scan.
    public const string ButtonName = "HairCardTopologyButton";

    private const float ScanInterval = .25f;
    private const float ButtonHeight = 32f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Spawn()
    {
        if (FindFirstObjectByType<HairCardTopologyAuthority>() != null) return;
        GameObject go = new GameObject(nameof(HairCardTopologyAuthority));
        DontDestroyOnLoad(go);
        go.AddComponent<HairCardTopologyAuthority>();
    }

    private GameObject boundPanel;
    private Button button;
    private TextMeshProUGUI label;
    private Image image;
    private float nextScan;

    private void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + ScanInterval;

        // The left panel is destroyed and rebuilt on every model and project load, so the
        // binding is re-checked rather than established once.
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
            // Deactivated and renamed with a PREFIX before the Destroy, because Destroy is
            // deferred to the end of the frame and a scan later in this same frame would
            // otherwise find it by name and adopt the thing being thrown away.
            existing.gameObject.SetActive(false);
            existing.gameObject.name = "Discarded_" + ButtonName;
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
        // authority for this panel and puts this button under CARD every scan.
        Transform above = parent.Find(HairCardProfileAuthority.ButtonName);
        if (above == null) above = parent.Find(GuideOverlayAuthority.ButtonName);
        if (above != null) go.transform.SetSiblingIndex(Mathf.Clamp(above.GetSiblingIndex() + 1, 0, parent.childCount - 1));

        Repaint();
    }

    private void Repaint()
    {
        // Only write when the value actually changed - a TMP text assignment forces a mesh
        // rebuild of the label whether or not the string differs.
        if (label != null)
        {
            string text = "TOPOLOGY: SYMMETRIC";
            if (HairCardSection.IsDynamicTopology) text = "TOPOLOGY: DYNAMIC";
            if (label.text != text) label.text = text;
        }

        if (image != null)
        {
            Color colour = new Color(.28f, .28f, .28f, 1f);
            if (HairCardSection.IsDynamicTopology) colour = new Color(.24f, .48f, .40f, 1f);
            if (image.color != colour) image.color = colour;
        }
    }

    private void Toggle()
    {
        if (HairCardSection.IsDynamicTopology)
        {
            HairCardSection.SetTopology(HairCardSection.Topology.Symmetric, true);
            StatusToast.Show("TOPOLOGY: SYMMETRIC - every card splits the same way every time, whatever it is bent into.", false, 5f);
            nextScan = 0f;
            Repaint();
            return;
        }

        HairCardSection.SetTopology(HairCardSection.Topology.Dynamic, true);
        StatusToast.Show("TOPOLOGY: DYNAMIC - each quad takes its shorter diagonal. Less fold on twisted hair; the triangles re-cut as the shape changes.", false, 6f);
        nextScan = 0f;
        Repaint();
    }
}
