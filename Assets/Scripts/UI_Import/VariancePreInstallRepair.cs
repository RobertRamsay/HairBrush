using System.Reflection;
using UnityEngine;

// Runs before GroomVarianceController.Update. If a rebuilt panel has already had
// its legacy Offset rows visually renamed to Angle rows, temporarily restores the
// row object names the installer expects. GroomVarianceController immediately
// renames them back to Angle while installing its variance rows.
[DefaultExecutionOrder(-2000)]
public class VariancePreInstallRepair : MonoBehaviour
{
    private ModelViewer viewer;
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        GameObject go = new GameObject("VariancePreInstallRepair");
        DontDestroyOnLoad(go);
        go.AddComponent<VariancePreInstallRepair>();
    }

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + 0.05f;
        if (viewer == null) viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer == null || viewer.groomingSliderPanelGO == null) return;

        GroomVarianceController variance = viewer.GetComponent<GroomVarianceController>();
        if (variance == null) return;

        FieldInfo installedField = typeof(GroomVarianceController).GetField("installed", BindingFlags.Instance | BindingFlags.NonPublic);
        if (installedField != null && (bool)installedField.GetValue(variance)) return;

        Transform panel = viewer.groomingSliderPanelGO.transform;
        RestoreInstallerName(panel, "Angle X_Row", "Offset X_Row");
        RestoreInstallerName(panel, "Angle Y_Row", "Offset Y_Row");
        RestoreInstallerName(panel, "Angle Z_Row", "Offset Z_Row");
    }

    void RestoreInstallerName(Transform panel, string angleName, string legacyName)
    {
        Transform row = panel.Find(angleName);
        if (row != null && panel.Find(legacyName) == null) row.name = legacyName;
    }
}
