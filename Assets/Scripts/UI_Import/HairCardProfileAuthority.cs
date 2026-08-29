using TMPro;
using UnityEngine;
using UnityEngine.UI;

// The global CARD: TENT / DIAMOND toggle, on the left panel under GUIDES.
//
// Global rather than per group, and that was a decision rather than an omission. Per group
// looks tempting - it sits next to SS/DS and N+/N-, which are both per group - but it does not
// survive contact with the rest of the tool. The clumper and guide reconstructions rebuild
// whole groups at a time and share one evaluator; every one of them would have to carry a
// profile through it. And SS/DS is the reason the setting exists at all: a groom with both
// profiles in it has no consistent answer for whether double sided should be on, so the
// control that suppresses it cannot itself be per group.
//
// SAVED WITH THE PROJECT, not in the settings ini. MAYA-NAV and GUIDES ON TOP are about the
// person - how they like to look at things - so they follow the person from project to
// project. This is about the hair: a diamond card and a tent card are different geometry, and
// a groom has to come back the shape it was saved as. See HairCardSection.Capture.
//
// WHAT SWITCHING COSTS. Every card in the scene rebuilds, which is the same work a Segments
// change already does to a group and is fast enough to be instant on a normal groom. Nothing
// is lost either way: the mesh is a pure function of the card's parameters, so a groom taken
// to DIAMOND and back to TENT is the groom it started as, down to the vertex.
[DefaultExecutionOrder(6800)]
public class HairCardProfileAuthority : MonoBehaviour
{
    // GroupPanelPostHintStats orders the left panel and needs to know this button by name.
    // A panel child that is not in its list gets shoved around every scan.
    public const string ButtonName = "HairCardProfileButton";

    private const float ScanInterval = .25f;
    private const float ButtonHeight = 32f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Spawn()
    {
        if (FindFirstObjectByType<HairCardProfileAuthority>() != null) return;
        GameObject go = new GameObject(nameof(HairCardProfileAuthority));
        DontDestroyOnLoad(go);
        go.AddComponent<HairCardProfileAuthority>();
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
        // authority for this panel and puts this button under GUIDES every scan.
        Transform above = parent.Find(GuideOverlayAuthority.ButtonName);
        if (above == null) above = parent.Find(MayaNavigationAuthority.ButtonName);
        if (above != null) go.transform.SetSiblingIndex(Mathf.Clamp(above.GetSiblingIndex() + 1, 0, parent.childCount - 1));

        Repaint();
    }

    private void Repaint()
    {
        // Only write when the value actually changed - a TMP text assignment forces a mesh
        // rebuild of the label whether or not the string differs.
        if (label != null)
        {
            string text = "CARD: TENT";
            if (HairCardSection.IsDiamond) text = "CARD: DIAMOND";
            if (label.text != text) label.text = text;
        }

        if (image != null)
        {
            Color colour = new Color(.28f, .28f, .28f, 1f);
            if (HairCardSection.IsDiamond) colour = new Color(.32f, .34f, .62f, 1f);
            if (image.color != colour) image.color = colour;
        }
    }

    private void Toggle()
    {
        if (HairCardSection.IsDiamond)
        {
            HairCardSection.SetProfile(HairCardSection.Profile.Tent, true);
            StatusToast.Show("CARD: TENT - open cross-section. SS/DS and N+/N- are yours again per group.", false, 5f);
            nextScan = 0f;
            Repaint();
            return;
        }

        HairCardSection.SetProfile(HairCardSection.Profile.Diamond, true);
        StatusToast.Show("CARD: DIAMOND - closed cross-section, single sided, normals correct from any angle. SS/DS and N+/N- are held while this is on.", false, 6f);
        nextScan = 0f;
        Repaint();
    }
}
