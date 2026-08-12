using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

// Keeps runtime-generated variance UI tied to the current grooming panel instance
// and normalizes the save button to a single enhanced-save listener.
[DefaultExecutionOrder(2200)]
public class VarianceSaveLifecycleRepair : MonoBehaviour
{
    private ModelViewer viewer;
    private GroomVarianceController variance;
    private RuntimeNavigationProjectIO projectIO;
    private GameObject lastPanel;
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        GameObject go = new GameObject("VarianceSaveLifecycleRepair");
        DontDestroyOnLoad(go);
        go.AddComponent<VarianceSaveLifecycleRepair>();
    }

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + 0.1f;

        if (viewer == null) viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer == null) return;
        if (variance == null) variance = viewer.GetComponent<GroomVarianceController>();
        if (projectIO == null) projectIO = FindFirstObjectByType<RuntimeNavigationProjectIO>();

        RepairVariancePanelLifecycle();
        NormalizeSaveButton();
    }

    void RepairVariancePanelLifecycle()
    {
        GameObject currentPanel = viewer.groomingSliderPanelGO;
        if (currentPanel == lastPanel) return;

        lastPanel = currentPanel;
        if (variance == null) variance = viewer.GetComponent<GroomVarianceController>();
        if (variance == null || currentPanel == null) return;

        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        System.Type type = typeof(GroomVarianceController);

        // The controller survived the old runtime panel, so its UI references point
        // at destroyed objects. Clear only UI/install state; keep groupSettings intact.
        type.GetField("installed", flags)?.SetValue(variance, false);
        type.GetField("lastGroupId", flags)?.SetValue(variance, int.MinValue);
        type.GetField("lastCardCount", flags)?.SetValue(variance, -1);
        type.GetField("nextInstallAttempt", flags)?.SetValue(variance, 0f);

        ClearDictionary(type.GetField("rows", flags)?.GetValue(variance));
        ClearDictionary(type.GetField("mainSliders", flags)?.GetValue(variance));
        ClearDictionary(type.GetField("mainLabels", flags)?.GetValue(variance));
    }

    void ClearDictionary(object obj)
    {
        if (obj is IDictionary dictionary) dictionary.Clear();
    }

    void NormalizeSaveButton()
    {
        if (projectIO == null) return;
        GameObject saveGO = GameObject.Find("SaveProjectButton");
        if (saveGO == null) return;
        Button button = saveGO.GetComponent<Button>();
        if (button == null) return;

        SaveSingleBindingMarker marker = saveGO.GetComponent<SaveSingleBindingMarker>();
        if (marker != null && marker.boundProjectIO == projectIO) return;

        button.onClick.RemoveAllListeners();
        button.onClick.AddListener(projectIO.SaveProjectEnhanced);

        if (marker == null) marker = saveGO.AddComponent<SaveSingleBindingMarker>();
        marker.boundProjectIO = projectIO;
    }
}

public class SaveSingleBindingMarker : MonoBehaviour
{
    public RuntimeNavigationProjectIO boundProjectIO;
}
