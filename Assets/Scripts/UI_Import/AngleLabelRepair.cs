using TMPro;
using UnityEngine;
using UnityEngine.UI;

[DefaultExecutionOrder(1850)]
public class AngleLabelRepair : MonoBehaviour
{
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        GameObject go = new GameObject("AngleLabelRepair");
        DontDestroyOnLoad(go);
        go.AddComponent<AngleLabelRepair>();
    }

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + 0.25f;
        ModelViewer viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer == null || viewer.groomingSliderPanelGO == null) return;
        Repair(viewer.groomingSliderPanelGO.transform, "Offset X_Row", "Angle X_Row", "Angle X");
        Repair(viewer.groomingSliderPanelGO.transform, "Offset Y_Row", "Angle Y_Row", "Angle Y");
        Repair(viewer.groomingSliderPanelGO.transform, "Offset Z_Row", "Angle Z_Row", "Angle Z");
    }

    void Repair(Transform panel, string stableRowName, string renamedRowName, string label)
    {
        // Keep the visible control labelled as Angle X/Y/Z, but preserve the original
        // stable row object name. Grooming extensions (including the per-axis curve rows)
        // key off these row identifiers, so renaming the GameObject made those controls
        // impossible to attach even though the slider itself continued to work.
        Transform row = panel.Find(stableRowName) ?? panel.Find(renamedRowName);
        if (row == null) return;
        row.name = stableRowName;

        Slider slider = row.GetComponentInChildren<Slider>(true);
        TextMeshProUGUI text = row.GetComponentInChildren<TextMeshProUGUI>(true);
        if (slider != null) slider.gameObject.name = label + "_Slider";
        if (text != null && slider != null)
        {
            text.gameObject.name = label + "_Text";
            text.text = label + ": " + slider.value.ToString("F3");
        }
    }
}
