using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Right-click any known groom/modifier slider to restore its normal default value.
// Setting Slider.value deliberately invokes the slider's existing callback so the
// underlying authored/POST/variance state is updated through its normal path.
public class SliderRightClickReset : MonoBehaviour, IPointerClickHandler
{
    private Slider slider;
    private float resetValue;

    public void Configure(float value)
    {
        slider = GetComponent<Slider>();
        resetValue = value;
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        if (eventData.button != PointerEventData.InputButton.Right) return;

        // Under MAYA-NAV, ALT+RMB is the DOLLY. Reaching for it with the cursor a few pixels
        // inside the panel would reset this slider to its default on the way past - and
        // ModelViewer's per-button nav latch only stops the camera half of that collision, not
        // this half. Conditional on MAYA-NAV: with it off, ALT+right-clicking a slider to reset it
        // is something that has always worked and there is no reason to take it away.
        if (MayaNavigationAuthority.CameraGestureActive) return;
        if (slider == null) slider = GetComponent<Slider>();
        if (slider == null || !slider.interactable) return;

        float value = Mathf.Clamp(resetValue, slider.minValue, slider.maxValue);
        if (slider.wholeNumbers) value = Mathf.Round(value);
        slider.value = value;
        eventData.Use();
    }
}

[DefaultExecutionOrder(9100)]
public class SliderRightClickResetInstaller : MonoBehaviour
{
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<SliderRightClickResetInstaller>() != null) return;
        GameObject go = new GameObject("SliderRightClickResetInstaller");
        DontDestroyOnLoad(go);
        go.AddComponent<SliderRightClickResetInstaller>();
    }

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + .15f;

        foreach (Slider slider in FindObjectsByType<Slider>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (slider == null || slider.GetComponent<SliderRightClickReset>() != null) continue;

            // Known slider: reset to its authored default. Anything else: reset to the value it
            // is carrying the first time this installer sees it.
            //
            // The fallback is the point. Before it, a slider missing from the table below got no
            // right-click reset AT ALL - silently, with nothing to notice until someone tried it
            // - and every parameter added to the tool arrived that way. Now the guarantee is
            // "every slider resets", and the table only decides whether the rest point is the
            // real authored default or simply wherever the slider started.
            //
            // First sight is a good approximation because panels build their sliders at their
            // default value and this scan runs every 0.15s, so the capture normally happens
            // before anyone can touch it. Add a case below when you want it to be exact.
            float value;
            if (!TryGetResetValue(slider, out value)) value = slider.value;

            SliderRightClickReset reset = slider.gameObject.AddComponent<SliderRightClickReset>();
            reset.Configure(value);
        }
    }

    static bool TryGetResetValue(Slider slider, out float value)
    {
        value = 0f;
        string n = slider.gameObject.name;

        // Runtime variance rows all use this shared name.
        if (n == "VarianceSlider") { value = 0f; return true; }

        // Persistent POST row weight.
        if (n == "WeightSlider") { value = 1f; return true; }

        switch (n)
        {
            // The DEFAULT LENGTH, expressed as this slider's curve parameter. The assignment
            // below is a notifying `slider.value =`, so a raw 0.2 here would not be clamped or
            // rejected - it is a legal parameter - it would simply reset the hair to 0.008.
            case "Length_Slider": value = GroomLengthCurve.ToSlider(.2f); return true;
            case "Width_Slider": value = .01f; return true;
            case "Segments_Slider": value = 12f; return true;
            case "Bend Angle_Slider": value = 0f; return true;
            case "Twist Angle_Slider": value = 0f; return true;
            case "Embed Depth_Slider": value = .002f; return true;

            case "Offset X_Slider":
            case "Offset Y_Slider":
            case "Offset Z_Slider":
            case "Angle X_Slider":
            case "Angle Y_Slider":
            case "Angle Z_Slider":
                value = 0f; return true;

            case "U Scale_Slider":
            case "V Scale_Slider":
                value = 1f; return true;

            case "U Offset_Slider":
            case "V Offset_Slider":
                value = 0f; return true;

            case "Curl Frequency_Slider":
            case "Curl Diameter_Slider":
            case "Wave Amplitude_Slider":
            case "Wave Frequency_Slider":
                value = 0f; return true;

            // Not 0 - Wave Direction rests at up/down, matching HairCard.waveDirection's own
            // initialiser and the empty-group default in SyncShapeSlidersToGroupRoot. Resetting
            // it to 0 would quietly flip every wave in the group back to side-to-side.
            case "Wave Direction_Slider": value = 1f; return true;

            // Neutral, not 0 - resetting Arch to zero would flatten the group rather than
            // restore its default profile.
            case "Arch_Slider": value = .5f; return true;

            case "LightAngle_Slider": value = 0f; return true;
            case "MetallicSlider": value = 0f; return true;
            case "SmoothnessSlider": value = .56f; return true;

            case "Radius_Slider": value = .03f; return true;
            case "Falloff_Slider":
            case "Falloff Dist_Slider": value = .05f; return true;

            case "CLUMP Point_Slider": value = .9f; return true;
            case "CLUMP Amount_Slider": value = 0f; return true;

            // In POST editing the legacy Strength row is repurposed as WEIGHT.
            case "Strength_Slider": value = 1f; return true;
        }

        return false;
    }
}
