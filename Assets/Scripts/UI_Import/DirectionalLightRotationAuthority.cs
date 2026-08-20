using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

// LEFT / RIGHT arrow keys swing the scene's directional light around the world up axis,
// so a groom can be checked against light coming from any side without leaving the tool.
//
// Tap for a single step, hold to sweep - the same feel as the [ ] brush-radius hotkeys,
// and the same rule about typing: while a text box has focus the arrows belong to the caret.
[DefaultExecutionOrder(-1000)]
public class DirectionalLightRotationAuthority : MonoBehaviour
{
    // Degrees per tap.
    private const float StepDegrees = 5f;

    // Held longer than this and it sweeps continuously at SweepDegreesPerSecond.
    private const float HoldDelay = .35f;
    private const float SweepDegreesPerSecond = 90f;

    private Light target;
    private float nextResolve;
    private float leftHeldSince;
    private float rightHeldSince;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<DirectionalLightRotationAuthority>() != null) return;
        GameObject go = new GameObject(nameof(DirectionalLightRotationAuthority));
        DontDestroyOnLoad(go);
        go.AddComponent<DirectionalLightRotationAuthority>();
    }

    void Awake()
    {
        target = null;
        nextResolve = 0f;
        leftHeldSince = -1f;
        rightHeldSince = -1f;
    }

    void Update()
    {
        Resolve();

        if (target == null || Keyboard.current == null)
        {
            leftHeldSince = -1f;
            rightHeldSince = -1f;
            return;
        }

        // Arrow keys move the caret while a group name or a seed is being typed.
        if (GroupNameInlineEditAuthority.IsEnteringText)
        {
            leftHeldSince = -1f;
            rightHeldSince = -1f;
            return;
        }

        HandleKey(Keyboard.current.leftArrowKey, -1f, ref leftHeldSince);
        HandleKey(Keyboard.current.rightArrowKey, 1f, ref rightHeldSince);
    }

    void HandleKey(KeyControl key, float direction, ref float heldSince)
    {
        if (key == null) return;

        if (key.wasPressedThisFrame)
        {
            heldSince = Time.unscaledTime;
            Rotate(direction * StepDegrees);
            return;
        }

        if (!key.isPressed)
        {
            heldSince = -1f;
            return;
        }

        if (heldSince < 0f) return;
        if (Time.unscaledTime < heldSince + HoldDelay) return;

        Rotate(direction * SweepDegreesPerSecond * Time.unscaledDeltaTime);
    }

    void Rotate(float degrees)
    {
        if (target == null) return;

        // World up, not the light's own up: the light keeps its authored tilt and simply
        // orbits, which is what reads as moving the sun around the head. Rotating about its
        // local up would tip the elevation as it went.
        target.transform.Rotate(Vector3.up, degrees, Space.World);
    }

    void Resolve()
    {
        if (target != null && target.isActiveAndEnabled) return;
        if (Time.unscaledTime < nextResolve) return;
        nextResolve = Time.unscaledTime + .5f;

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

        target = best;
    }
}
