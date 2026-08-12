using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

// Keeps the Ctrl+Click localized selection brush at half the original scale.
// Original system used 0.25 initial falloff with a 0..1 slider. This clamps
// the active system to 0.125 initial falloff and a 0..0.5 slider range.
[DefaultExecutionOrder(2100)]
public class SelectionBrushScaleTuning : MonoBehaviour
{
    private ModelViewer viewer;
    private FieldInfo hasSelectionField;
    private FieldInfo falloffField;
    private FieldInfo falloffRowField;
    private bool wasSelected;
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
            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            hasSelectionField = typeof(ModelViewer).GetField("hasSelectionHotspot", flags);
            falloffField = typeof(ModelViewer).GetField("brushFalloffDistance", flags);
            falloffRowField = typeof(ModelViewer).GetField("falloffRowGO", flags);
        }

        bool selected = hasSelectionField != null && hasSelectionField.GetValue(viewer) is bool b && b;

        // EnterSelectionMode currently seeds the old 0.25 value. On a new selection,
        // translate that old default to the new half-scale default of 0.125.
        if (selected && !wasSelected && falloffField != null)
        {
            float current = (float)falloffField.GetValue(viewer);
            if (Mathf.Approximately(current, .25f) || current > .5f)
                falloffField.SetValue(viewer, .125f);
        }
        wasSelected = selected;

        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + .05f;

        // The row is runtime generated, so keep its slider range canonical after each
        // selection rebuild. Changing its value still invokes ModelViewer's callback,
        // which recomputes weights; SelectionBrushVisualizer reads the same field live.
        GameObject row = falloffRowField?.GetValue(viewer) as GameObject;
        if (row == null) return;
        Slider slider = row.GetComponentInChildren<Slider>(true);
        if (slider == null) return;

        slider.minValue = .001f;
        slider.maxValue = .5f;

        float fieldValue = falloffField != null ? (float)falloffField.GetValue(viewer) : slider.value;
        float clamped = Mathf.Clamp(fieldValue, slider.minValue, slider.maxValue);
        if (!Mathf.Approximately(fieldValue, clamped) && falloffField != null)
            falloffField.SetValue(viewer, clamped);
        if (!Mathf.Approximately(slider.value, clamped))
            slider.SetValueWithoutNotify(clamped);
    }
}
