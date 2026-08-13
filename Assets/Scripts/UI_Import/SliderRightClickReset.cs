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
            if (!TryGetResetValue(slider, out float value)) continue;

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
            case "Length_Slider": value = .2f; return true;
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
