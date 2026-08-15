using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

// Groom editing is permanently ABS. Removes the now-redundant REL/ABS button from the
// runtime TopControlsRow and keeps the underlying legacy flag false for all sessions.
[DefaultExecutionOrder(5300)]
public class AbsoluteGroomModeAuthority : MonoBehaviour
{
    private ModelViewer viewer;
    private FieldInfo relativeField;
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<AbsoluteGroomModeAuthority>() != null) return;
        GameObject go = new GameObject("AbsoluteGroomModeAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<AbsoluteGroomModeAuthority>();
    }

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + .1f;

        if (viewer == null)
        {
            viewer = FindFirstObjectByType<ModelViewer>();
            if (viewer != null)
                relativeField = typeof(ModelViewer).GetField("isRelativeMode", BindingFlags.Instance | BindingFlags.NonPublic);
        }
        if (viewer == null) return;

        // Even older projects/sessions can never re-enter relative mode.
        relativeField?.SetValue(viewer, false);

        if (viewer.groomingSliderPanelGO == null) return;
        Transform top = viewer.groomingSliderPanelGO.transform.Find("TopControlsRow");
        if (top == null) return;

        Transform mode = top.Find("ModeToggleButton");
        if (mode != null) Destroy(mode.gameObject);

        // With the mode button gone, the existing HorizontalLayoutGroup naturally gives
        // SAVE PROJ and RESET the row between them. Make sure both remain active.
        Transform save = top.Find("SaveProjectButton");
        if (save != null && !save.gameObject.activeSelf) save.gameObject.SetActive(true);
        Transform reset = top.Find("ResetButton");
        if (reset != null && !reset.gameObject.activeSelf) reset.gameObject.SetActive(true);

        HorizontalLayoutGroup layout = top.GetComponent<HorizontalLayoutGroup>();
        if (layout != null)
        {
            layout.childControlWidth = true;
            layout.childForceExpandWidth = true;
        }
    }
}
