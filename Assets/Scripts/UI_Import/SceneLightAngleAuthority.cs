using TMPro;
using UnityEngine;
using UnityEngine.UI;

// LIGHT ANGLE slider in the Hair Groups panel: swings the scene's directional light around
// the world up axis so a groom can be checked against light from any side without leaving
// the tool.
//
// The light keeps its authored pitch and roll and simply orbits - rotating about its own up
// would tip the elevation as it went. The slider is absolute (0-360 from wherever the light
// was authored), which is why it replaced the earlier arrow-key nudges: with a slider you
// can see where the light is, not just change it.
[DefaultExecutionOrder(8900)]
public class SceneLightAngleAuthority : MonoBehaviour
{
    // GroupPanelPostHintStats orders the left panel and needs to know this row by name.
    public const string RowName = "LightAngleRow";

    private const float RowHeight = 40f;
    private const float ScanInterval = .25f;

    private Light target;
    private Quaternion baseRotation;
    private float baseYaw;
    private bool hasBase;

    private GameObject boundPanel;
    private Slider slider;
    private TextMeshProUGUI label;
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<SceneLightAngleAuthority>() != null) return;
        GameObject go = new GameObject(nameof(SceneLightAngleAuthority));
        DontDestroyOnLoad(go);
        go.AddComponent<SceneLightAngleAuthority>();
    }

    void Awake()
    {
        target = null;
        baseRotation = Quaternion.identity;
        baseYaw = 0f;
        hasBase = false;
        boundPanel = null;
        slider = null;
        label = null;
        nextScan = 0f;
    }

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + ScanInterval;

        ResolveLight();

        GameObject panel = GameObject.Find("GroupManagerPanel");
        if (panel == null)
        {
            boundPanel = null;
            slider = null;
            label = null;
            return;
        }

        if (boundPanel != panel || slider == null) Bind(panel);
    }

    void ResolveLight()
    {
        if (target != null && target.isActiveAndEnabled) return;

        // The brightest active directional light is the scene's key light.
        Light best = null;
        float bestIntensity = float.MinValue;
        foreach (Light light in FindObjectsByType<Light>(FindObjectsSortMode.None))
        {
            if (light == null) continue;
            if (light.type != LightType.Directional) continue;
            if (!light.isActiveAndEnabled) continue;
            if (light.intensity <= bestIntensity) continue;

            bestIntensity = light.intensity;
            best = light;
        }

        if (best == null)
        {
            target = null;
            hasBase = false;
            return;
        }

        if (target == best && hasBase) return;

        target = best;
        baseRotation = best.transform.rotation;
        baseYaw = Mathf.Repeat(best.transform.eulerAngles.y, 360f);
        hasBase = true;

        if (slider != null) slider.SetValueWithoutNotify(baseYaw);
        UpdateLabel(baseYaw);
    }

    void Bind(GameObject panel)
    {
        boundPanel = panel;

        Transform existing = panel.transform.Find(RowName);
        if (existing != null)
        {
            slider = existing.GetComponentInChildren<Slider>(true);
            label = existing.GetComponentInChildren<TextMeshProUGUI>(true);
            if (slider != null) return;
            Destroy(existing.gameObject);
        }

        float startAngle = 0f;
        if (hasBase) startAngle = baseYaw;

        BuildRow(panel.transform, startAngle);
    }

    void BuildRow(Transform parent, float startAngle)
    {
        GameObject row = new GameObject(RowName, typeof(RectTransform), typeof(VerticalLayoutGroup));
        row.transform.SetParent(parent, false);
        row.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, RowHeight);
        VerticalLayoutGroup layout = row.GetComponent<VerticalLayoutGroup>();
        layout.spacing = 2f;
        layout.childControlWidth = true;
        layout.childControlHeight = false;

        GameObject labelGO = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        labelGO.transform.SetParent(row.transform, false);
        labelGO.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 18f);
        label = labelGO.GetComponent<TextMeshProUGUI>();
        label.fontSize = 13f;
        label.fontStyle = FontStyles.Bold;
        label.alignment = TextAlignmentOptions.MidlineLeft;
        label.color = new Color(.78f, .82f, .86f, 1f);
        label.raycastTarget = false;

        GameObject sliderGO = new GameObject("LightAngle_Slider", typeof(RectTransform), typeof(Slider));
        sliderGO.transform.SetParent(row.transform, false);
        sliderGO.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 18f);
        slider = sliderGO.GetComponent<Slider>();
        slider.minValue = 0f;
        slider.maxValue = 360f;
        slider.wholeNumbers = false;

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
        fill.GetComponent<Image>().color = new Color(.85f, .72f, .32f);
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
        slider.handleRect.sizeDelta = new Vector2(14f, 0f);

        slider.SetValueWithoutNotify(startAngle);
        slider.onValueChanged.AddListener(ApplyAngle);
        UpdateLabel(startAngle);
    }

    void ApplyAngle(float angle)
    {
        UpdateLabel(angle);
        if (target == null || !hasBase) return;

        // Orbit about world up from the light's authored orientation, so its pitch and roll
        // survive untouched however far round the slider goes.
        target.transform.rotation = Quaternion.AngleAxis(angle - baseYaw, Vector3.up) * baseRotation;
    }

    void UpdateLabel(float angle)
    {
        if (label == null) return;
        label.text = "LIGHT ANGLE: " + Mathf.RoundToInt(Mathf.Repeat(angle, 360f)) + "°";
    }
}
