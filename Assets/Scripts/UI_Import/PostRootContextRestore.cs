using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

// Treat leaving POST authoring as an explicit return to the group-root context.
// PostAffectorManager clears its active selection at execution order 3300; this runs just
// after it and restores both ModelViewer.current* and the visible sliders before another
// hair card can be placed from stale POST-local control values.
[DefaultExecutionOrder(3400)]
public class PostRootContextRestore : MonoBehaviour
{
    private ModelViewer viewer;
    private PostAffectorManager posts;
    private GroomRootStateAuthority roots;
    private FieldInfo activeIdField;
    private int previousActiveId = -1;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<PostRootContextRestore>() != null) return;
        GameObject go = new GameObject("PostRootContextRestore");
        DontDestroyOnLoad(go);
        go.AddComponent<PostRootContextRestore>();
    }

    void Update()
    {
        Resolve();
        if (viewer == null || posts == null || roots == null || activeIdField == null) return;

        int activeId = activeIdField.GetValue(posts) is int id ? id : -1;
        if (previousActiveId >= 0 && activeId < 0)
            RestoreCurrentGroupRoot();

        previousActiveId = activeId;
    }

    void Resolve()
    {
        if (viewer == null) viewer = FindFirstObjectByType<ModelViewer>();
        if (roots == null) roots = FindFirstObjectByType<GroomRootStateAuthority>();
        if (posts != null) return;

        posts = FindFirstObjectByType<PostAffectorManager>();
        if (posts != null)
            activeIdField = typeof(PostAffectorManager).GetField("activeId", BindingFlags.Instance | BindingFlags.NonPublic);
    }

    void RestoreCurrentGroupRoot()
    {
        roots.RestoreRootToViewer(viewer.currentGroupId);
        SyncVisibleSliders();
    }

    void SyncVisibleSliders()
    {
        GameObject panel = viewer.groomingSliderPanelGO;
        if (panel == null) return;

        foreach (Slider slider in panel.GetComponentsInChildren<Slider>(true))
        {
            if (slider == null) continue;
            switch (slider.gameObject.name)
            {
                case "Length_Slider":
                    slider.SetValueWithoutNotify(viewer.currentLength);
                    break;
                case "Width_Slider":
                    slider.SetValueWithoutNotify(viewer.currentWidth);
                    break;
                case "Segments_Slider":
                    slider.SetValueWithoutNotify(viewer.currentSegments);
                    break;
                case "Bend Angle_Slider":
                    slider.SetValueWithoutNotify(viewer.currentBend);
                    break;
                case "Twist Angle_Slider":
                    slider.SetValueWithoutNotify(viewer.currentTwist);
                    break;
                case "Embed Depth_Slider":
                    slider.SetValueWithoutNotify(viewer.currentEmbedDepth);
                    break;
                case "Offset X_Slider":
                case "Angle X_Slider":
                    slider.SetValueWithoutNotify(viewer.currentOffsetX);
                    break;
                case "Offset Y_Slider":
                case "Angle Y_Slider":
                    slider.SetValueWithoutNotify(viewer.currentOffsetY);
                    break;
                case "Offset Z_Slider":
                case "Angle Z_Slider":
                    slider.SetValueWithoutNotify(viewer.currentOffsetZ);
                    break;
                case "U Scale_Slider":
                    slider.SetValueWithoutNotify(viewer.currentUScale);
                    break;
                case "V Scale_Slider":
                    slider.SetValueWithoutNotify(viewer.currentVScale);
                    break;
                case "U Offset_Slider":
                    slider.SetValueWithoutNotify(viewer.currentUOffset);
                    break;
                case "V Offset_Slider":
                    slider.SetValueWithoutNotify(viewer.currentVOffset);
                    break;
                case "Curl Frequency_Slider":
                    slider.SetValueWithoutNotify(viewer.currentCurlFrequency);
                    break;
                case "Curl Diameter_Slider":
                    slider.SetValueWithoutNotify(viewer.currentCurlDiameter);
                    break;
            }
        }
    }
}
