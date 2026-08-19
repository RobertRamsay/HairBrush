using System;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

// Gives Ctrl+Click localized editing three independent controls:
// Radius = full influence zone, Falloff = fade distance beyond Radius,
// Strength = final edit multiplier. Also keeps the generated UI in sync.
[DefaultExecutionOrder(2100)]
public class SelectionBrushScaleTuning : MonoBehaviour
{
    private const float DefaultRadius = .03f;
    private const float DefaultFalloff = .05f;
    private const float MaxRadius = .25f;
    private const float MaxFalloff = .25f;

    private ModelViewer viewer;
    private FieldInfo hasSelectionField;
    private FieldInfo hitPointField;
    private FieldInfo falloffRowField;
    private FieldInfo activeSliderPanelField;
    private MethodInfo createSliderMethod;

    private GameObject radiusRow;
    private Slider radiusSlider;
    private Slider falloffSlider;
    private bool wasSelected;
    private bool initializedDefaults;
    private float lastRadius = -1f;
    private float lastFalloff = -1f;
    private int lastGroup = int.MinValue;
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        GameObject go = new GameObject("SelectionBrushScaleTuning");
        DontDestroyOnLoad(go);
        go.AddComponent<SelectionBrushScaleTuning>();
    }

    void Update()
    {
        if (viewer == null)
        {
            viewer = FindFirstObjectByType<ModelViewer>();
            if (viewer == null) return;
            CacheMembers();
        }

        if (!initializedDefaults)
        {
            // First-use values only. From then on, Radius/Falloff persist between hotspots.
            viewer.brushRadius = DefaultRadius;
            viewer.brushFalloffDistance = DefaultFalloff;
            lastRadius = viewer.brushRadius;
            lastFalloff = viewer.brushFalloffDistance;
            initializedDefaults = true;
        }

        bool selected = IsSelected();
        if (selected && !wasSelected)
        {
            // EnterSelectionMode still writes its legacy .25 falloff. Replace only that
            // legacy reset with the last user value (or .05 on first use). Selecting an
            // existing POST already loaded its own stored radius/falloff before we get here.
            if (Mathf.Approximately(viewer.brushFalloffDistance, .25f))
                viewer.brushFalloffDistance = lastFalloff > 0f ? lastFalloff : DefaultFalloff;
            if (viewer.brushRadius <= 0f || viewer.brushRadius > MaxRadius)
                viewer.brushRadius = lastRadius > 0f ? lastRadius : DefaultRadius;

            lastGroup = int.MinValue;
            nextScan = 0f;
        }
        wasSelected = selected;

        if (Time.unscaledTime >= nextScan)
        {
            nextScan = Time.unscaledTime + .05f;
            MaintainGeneratedUI(selected);
        }

        if (!selected) return;

        float radius = Mathf.Clamp(viewer.brushRadius, .001f, MaxRadius);
        float falloff = Mathf.Clamp(viewer.brushFalloffDistance, 0f, MaxFalloff);
        if (!Mathf.Approximately(radius, viewer.brushRadius)) viewer.brushRadius = radius;
        if (!Mathf.Approximately(falloff, viewer.brushFalloffDistance)) viewer.brushFalloffDistance = falloff;

        if (!Mathf.Approximately(radius, lastRadius) ||
            !Mathf.Approximately(falloff, lastFalloff) ||
            lastGroup != viewer.currentGroupId)
        {
            RecomputeWeights(GetHitPoint(), radius, falloff);
            lastRadius = radius;
            lastFalloff = falloff;
            lastGroup = viewer.currentGroupId;
        }
    }

    void CacheMembers()
    {
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Type type = typeof(ModelViewer);
        hasSelectionField = type.GetField("hasSelectionHotspot", flags);
        hitPointField = type.GetField("selectionHitPoint", flags);
        falloffRowField = type.GetField("falloffRowGO", flags);
        activeSliderPanelField = type.GetField("activeSliderPanel", flags);
        createSliderMethod = type.GetMethod("CreateSliderUI", flags);
    }

    bool IsSelected() => hasSelectionField != null && hasSelectionField.GetValue(viewer) is bool b && b;
    Vector3 GetHitPoint() => hitPointField != null && hitPointField.GetValue(viewer) is Vector3 p ? p : Vector3.zero;

    void MaintainGeneratedUI(bool selected)
    {
        if (!selected)
        {
            if (radiusRow != null) Destroy(radiusRow);
            radiusRow = null;
            radiusSlider = null;
            falloffSlider = null;

            // Group-header selection can end POST mode without going through
            // ModelViewer.ClearSelectionHotspot(), so clean up the legacy falloff row here too.
            GameObject staleFalloff = falloffRowField?.GetValue(viewer) as GameObject;
            if (staleFalloff != null) Destroy(staleFalloff);
            falloffRowField?.SetValue(viewer, null);
            return;
        }

        GameObject falloffRow = falloffRowField?.GetValue(viewer) as GameObject;

        // Ctrl+Click creation builds this row in ModelViewer, but selecting an existing POST
        // from the left panel does not. Recreate it here so Radius/Falloff are always available
        // whenever a POST/local edit is active.
        if (falloffRow == null && createSliderMethod != null)
        {
            GameObject panel = activeSliderPanelField?.GetValue(viewer) as GameObject;
            if (panel == null) panel = viewer.groomingSliderPanelGO;

            if (panel != null)
            {
                UnityAction<float> onFalloff = value =>
                {
                    viewer.brushFalloffDistance = Mathf.Clamp(value, 0f, MaxFalloff);
                    RecomputeWeights(GetHitPoint(), viewer.brushRadius, viewer.brushFalloffDistance);
                    lastRadius = viewer.brushRadius;
                    lastFalloff = viewer.brushFalloffDistance;
                };

                object[] args = { panel.transform, "Falloff", 0f, MaxFalloff, viewer.brushFalloffDistance, onFalloff, null, 38f, 16 };
                falloffRow = createSliderMethod.Invoke(viewer, args) as GameObject;
                falloffSlider = args[6] as Slider;
                if (falloffRow != null)
                    falloffRowField?.SetValue(viewer, falloffRow);
            }
        }

        if (falloffRow == null) return;

        falloffSlider = falloffRow.GetComponentInChildren<Slider>(true);
        if (falloffSlider != null)
        {
            falloffSlider.minValue = 0f;
            falloffSlider.maxValue = MaxFalloff;
            if (!Mathf.Approximately(falloffSlider.value, viewer.brushFalloffDistance))
                falloffSlider.SetValueWithoutNotify(viewer.brushFalloffDistance);
        }

        TextMeshProUGUI falloffLabel = falloffRow.GetComponentInChildren<TextMeshProUGUI>(true);
        if (falloffLabel != null)
            falloffLabel.text = "Falloff: " + viewer.brushFalloffDistance.ToString("F3");

        if (radiusRow == null && createSliderMethod != null)
        {
            Transform parent = falloffRow.transform.parent;
            UnityAction<float> onRadius = value =>
            {
                viewer.brushRadius = Mathf.Clamp(value, .001f, MaxRadius);
                RecomputeWeights(GetHitPoint(), viewer.brushRadius, viewer.brushFalloffDistance);
                lastRadius = viewer.brushRadius;
                lastFalloff = viewer.brushFalloffDistance;
            };

            object[] args = { parent, "Radius", .001f, MaxRadius, viewer.brushRadius, onRadius, null, 38f, 16 };
            radiusRow = createSliderMethod.Invoke(viewer, args) as GameObject;
            radiusSlider = args[6] as Slider;
        }

        PlaceBrushControlsAtTop(falloffRow);

        if (radiusSlider != null)
        {
            radiusSlider.minValue = .001f;
            radiusSlider.maxValue = MaxRadius;
            if (!Mathf.Approximately(radiusSlider.value, viewer.brushRadius))
                radiusSlider.SetValueWithoutNotify(viewer.brushRadius);
        }

        if (falloffSlider != null && falloffSlider.GetComponent<SelectionFalloffBindingMarker>() == null)
        {
            falloffSlider.gameObject.AddComponent<SelectionFalloffBindingMarker>();
            falloffSlider.onValueChanged.AddListener(value =>
            {
                viewer.brushFalloffDistance = Mathf.Clamp(value, 0f, MaxFalloff);
                RecomputeWeights(GetHitPoint(), viewer.brushRadius, viewer.brushFalloffDistance);
                lastRadius = viewer.brushRadius;
                lastFalloff = viewer.brushFalloffDistance;
                TextMeshProUGUI label = falloffRow != null ? falloffRow.GetComponentInChildren<TextMeshProUGUI>(true) : null;
                if (label != null) label.text = "Falloff: " + viewer.brushFalloffDistance.ToString("F3");
            });
        }
    }

    void PlaceBrushControlsAtTop(GameObject falloffRow)
    {
        if (radiusRow == null || falloffRow == null) return;

        Transform parent = falloffRow.transform.parent;
        if (parent == null || radiusRow.transform.parent != parent) return;

        // Keep the panel-wide tab/save/mode rows first, then make POST-local spatial
        // controls the first modifier controls: Radius -> Falloff -> Length/etc.
        int insertIndex = 0;
        Transform tabRow = parent.Find("PanelTabRow");
        if (tabRow != null)
            insertIndex = Mathf.Max(insertIndex, tabRow.GetSiblingIndex() + 1);
        Transform topControlsRow = parent.Find("TopControlsRow");
        if (topControlsRow != null)
            insertIndex = Mathf.Max(insertIndex, topControlsRow.GetSiblingIndex() + 1);

        radiusRow.transform.SetSiblingIndex(Mathf.Min(insertIndex, parent.childCount - 1));
        falloffRow.transform.SetSiblingIndex(Mathf.Min(insertIndex + 1, parent.childCount - 1));
    }

    public void RecomputeWeights(Vector3 center, float radius, float falloff)
    {
        float outerRadius = radius + falloff;
        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
        {
            if (card.groupId != viewer.currentGroupId)
            {
                card.SetSelectionWeight(0f);
                continue;
            }

            float previousWeight = card.selectionWeight;
            float distance = Vector3.Distance(center, card.transform.position);
            float weight;

            if (distance <= radius) weight = 1f;
            else if (falloff > .000001f && distance <= outerRadius) weight = 1f - ((distance - radius) / falloff);
            else weight = 0f;

            weight = Mathf.Clamp01(weight);
            if (previousWeight <= 0f && weight > 0f)
            {
                card.CaptureBaseState(card.length, card.width, card.segments, card.bendAngle, card.twistAngle,
                    card.GetEmbedDepth(), card.GetOffsetX(), card.GetOffsetY(), card.GetOffsetZ(), card.curlFrequency, card.curlDiameter);
            }
            card.SetSelectionWeight(weight);
        }
    }
}

public class SelectionFalloffBindingMarker : MonoBehaviour { }