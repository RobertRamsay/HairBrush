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
    private const float DefaultRadius = .02f;
    private const float DefaultFalloff = .03f;
    private const float MaxRadius = .25f;
    private const float MaxFalloff = .25f;

    private ModelViewer viewer;
    private FieldInfo hasSelectionField;
    private FieldInfo hitPointField;
    private FieldInfo falloffRowField;
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
            // Small first-use brush; after this, user values persist between hotspots.
            if (viewer.brushRadius <= 0f || viewer.brushRadius > MaxRadius) viewer.brushRadius = DefaultRadius;
            if (viewer.brushFalloffDistance <= 0f || viewer.brushFalloffDistance > MaxFalloff) viewer.brushFalloffDistance = DefaultFalloff;
            initializedDefaults = true;
        }

        bool selected = IsSelected();
        if (selected && !wasSelected)
        {
            // IMPORTANT: do not reset Radius/Falloff on each Ctrl+Click.
            // ModelViewer still writes its legacy 0.25 falloff in EnterSelectionMode,
            // so only translate that exact legacy value back to the user's previous/default value.
            if (Mathf.Approximately(viewer.brushFalloffDistance, .25f))
                viewer.brushFalloffDistance = lastFalloff > 0f ? lastFalloff : DefaultFalloff;
            if (viewer.brushRadius <= 0f || viewer.brushRadius > MaxRadius)
                viewer.brushRadius = lastRadius > 0f ? lastRadius : DefaultRadius;

            lastRadius = -1f;
            lastFalloff = -1f;
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
            return;
        }

        GameObject falloffRow = falloffRowField?.GetValue(viewer) as GameObject;
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
            if (radiusRow != null)
                radiusRow.transform.SetSiblingIndex(falloffRow.transform.GetSiblingIndex());
        }

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
                    card.GetEmbedDepth(), card.GetOffsetX(), card.GetOffsetY(), card.GetOffsetZ());
            }
            card.SetSelectionWeight(weight);
        }
    }
}

public class SelectionFalloffBindingMarker : MonoBehaviour { }
