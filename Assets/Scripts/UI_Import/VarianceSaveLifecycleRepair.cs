using UnityEngine;
using UnityEngine.UI;

// Keeps the runtime SAVE button bound to exactly one enhanced-save listener.
// Variance panel lifecycle is owned entirely by GroomVarianceController.
[DefaultExecutionOrder(2200)]
public class VarianceSaveLifecycleRepair : MonoBehaviour
{
    private RuntimeNavigationProjectIO projectIO;
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
        if (projectIO == null) projectIO = FindFirstObjectByType<RuntimeNavigationProjectIO>();
        NormalizeSaveButton();
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
