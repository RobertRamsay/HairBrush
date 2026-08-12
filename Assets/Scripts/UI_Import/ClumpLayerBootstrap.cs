using UnityEngine;

// Runtime bootstrap only: the ClumpLayerManager now injects its modifier controls
// directly beneath each runtime group entry, so no floating window/button is needed.
public class ClumpLayerBootstrap : MonoBehaviour
{
    private bool installed;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        GameObject go = new GameObject("ClumpLayerBootstrap");
        DontDestroyOnLoad(go);
        go.AddComponent<ClumpLayerBootstrap>();
    }

    void Update()
    {
        if (!installed)
        {
            ModelViewer viewer = FindFirstObjectByType<ModelViewer>();
            if (viewer == null || viewer.groomingSliderPanelGO == null) return;

            ClumpLayerManager manager = viewer.GetComponent<ClumpLayerManager>();
            if (manager == null) manager = viewer.gameObject.AddComponent<ClumpLayerManager>();
            manager.Init(viewer);
            installed = true;
        }

        MaintainModifierLayout();
    }

    void MaintainModifierLayout()
    {
        RectTransform[] rects = FindObjectsByType<RectTransform>(FindObjectsSortMode.None);
        foreach (RectTransform rect in rects)
        {
            if (!rect.name.StartsWith("ClumpModifier_")) continue;
            float targetHeight = rect.childCount > 1 ? 620f : 34f;
            if (!Mathf.Approximately(rect.sizeDelta.y, targetHeight))
                rect.sizeDelta = new Vector2(rect.sizeDelta.x, targetHeight);
        }
    }
}
