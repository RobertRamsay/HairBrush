using UnityEngine;
using UnityEngine.UI;

// THE LENGTH SLIDER'S RESPONSE CURVE.
//
// Hair lengths are not spread evenly over the slider's range and never were. A groom sits around
// 0.2, and the fine work happens between 0.01 and 0.001 - a tenth of a percent of a slider whose
// range runs to 1.0. On a 250px control that decade was about two pixels wide, so getting from
// 0.010 to 0.003 meant nudging a single pixel and hoping.
//
// So the slider no longer carries the length. It carries a 0-1 PARAMETER, and the length is
//
//     length = MaxLength * t^Gamma
//
// which spends the travel where the work is. At Gamma 3 the 0.001-0.010 decade gets 11.5% of the
// slider - about thirty pixels instead of two - while 0.2 still lands at t = 0.58 and the top of
// the range is still 1.0. Nothing about the achievable lengths changed; only where they sit.
//
// ----------------------------------------------------------------------------------------
// Why the slider's own domain had to change, rather than something cleverer
// ----------------------------------------------------------------------------------------
// A Unity Slider draws its handle at (value - min) / (max - min) and sets its value from the
// pointer by the same formula, and both live in private methods. There is no hook that makes the
// handle sit anywhere other than where the value says. So a non-linear response means the value
// IS the position, and the length is derived - the alternative is a handle that does not follow
// the cursor, which is worse than the problem being solved.
//
// ----------------------------------------------------------------------------------------
// The rule for anything that touches Length_Slider
// ----------------------------------------------------------------------------------------
// slider.value is NOT a length. Never write a length into it and never read one out of it.
// Everything goes through the two functions here, and the three Slider-aware helpers below exist
// so that the GENERIC sites - the ones that walk every slider in the panel by name - need one
// guarded line rather than a special case each:
//
//   ToSliderFor / ReadAsLength   a value going into / out of any slider, converted only if it is
//                                this one
//   Displayed                    what the row's label should say
//
// The dangerous sites are the ones that compile perfectly either way. A world length written raw
// into this slider is a legal parameter - 0.2 is a valid t - so it does not throw, it just makes
// the hair the wrong length. Both notifying writers (ModelViewer.ResetAllSliders and
// SliderRightClickReset's ALT+right-click default) are exactly that shape.
public static class GroomLengthCurve
{
    public const string SliderName = "Length_Slider";

    // The shortest hair the slider can ask for. Unchanged from the linear slider's own minimum.
    public const float MinLength = 0.0001f;

    // The longest. Unchanged, and still reached at t = 1.
    public const float MaxLength = 1f;

    // How hard the travel is biased towards short hair. 1 is the old linear slider; higher gives
    // the small end more room and takes it from the top. 3 is the setting that makes 0.001-0.010
    // comfortable without making 0.2-1.0 feel cramped - raise it if the fine end still wants more.
    public const float Gamma = 3f;

    // Parameter -> length. Clamped at MinLength rather than allowed to reach zero: a zero-length
    // card is degenerate geometry, and the apply path guards against it anyway.
    public static float ToLength(float sliderValue)
    {
        float t = Mathf.Clamp01(sliderValue);
        return Mathf.Max(MinLength, MaxLength * Mathf.Pow(t, Gamma));
    }

    // Length -> parameter. The exact inverse of ToLength above MinLength, so a push to the slider
    // followed by the callback it raises returns the same length it started with. That round trip
    // happens on every reset and every group change, so an approximate inverse would let the
    // length creep a little further wrong every time.
    public static float ToSlider(float length)
    {
        float normalised = Mathf.Clamp01(length / MaxLength);
        return Mathf.Pow(normalised, 1f / Gamma);
    }

    public static bool IsLengthSlider(Slider slider)
    {
        if (slider == null) return false;
        return slider.gameObject.name == SliderName;
    }

    // For a generic writer holding a world value and a slider it did not build.
    public static float ToSliderFor(Slider slider, float value)
    {
        if (!IsLengthSlider(slider)) return value;
        return ToSlider(value);
    }

    // For a generic reader that wants the value in the units the rest of the tool speaks.
    public static float ReadAsLength(Slider slider)
    {
        if (slider == null) return 0f;
        if (!IsLengthSlider(slider)) return slider.value;
        return ToLength(slider.value);
    }

    // What the row's label should show. Every label in this panel is "Name: value" formatted from
    // slider.value, so without this the Length row would read out its curve parameter.
    public static float Displayed(Slider slider)
    {
        return ReadAsLength(slider);
    }
}
