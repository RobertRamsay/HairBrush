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

    void Repair(Transform panel, string legacyName, string newName, string label)
    {
        Transform row = panel.Find(newName) ?? panel.Find(legacyName);
        if (row == null) return;
        row.name = newName;
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
