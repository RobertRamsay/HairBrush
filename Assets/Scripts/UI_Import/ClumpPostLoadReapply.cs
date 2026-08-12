using System.Collections;
using System.Reflection;
using UnityEngine;

// Project restoration spans several runtime systems. Reapply loaded clump layers
// after those systems have settled so an enabled saved layer is visually active
// immediately, without requiring a slider tweak or OFF/ON toggle.
[DefaultExecutionOrder(-9000)]
public class ClumpPostLoadReapply : MonoBehaviour
{
    private bool restoreSeen;
    private bool reapplyScheduled;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<ClumpPostLoadReapply>() != null) return;
        GameObject go = new GameObject("ClumpPostLoadReapply");
        DontDestroyOnLoad(go);
        go.AddComponent<ClumpPostLoadReapply>();
    }

    void Update()
    {
        if (HairProjectSaveData.PendingModifierRestore != null)
        {
            restoreSeen = true;
            reapplyScheduled = false;
            return;
        }

        if (restoreSeen && !reapplyScheduled)
        {
            restoreSeen = false;
            reapplyScheduled = true;
            StartCoroutine(ReapplyAfterSettled());
        }
    }

    IEnumerator ReapplyAfterSettled()
    {
        // Allow ModelViewer reconstruction, variance restore and POST base recovery
        // to finish before clump becomes the final deformation stage again.
        yield return null;
        yield return null;
        yield return new WaitForEndOfFrame();

        ClumpLayerManager manager = FindFirstObjectByType<ClumpLayerManager>();
        if (manager == null) { reapplyScheduled = false; yield break; }

        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        FieldInfo layersField = typeof(ClumpLayerManager).GetField("layers", flags);
        MethodInfo applyMethod = typeof(ClumpLayerManager).GetMethod("ApplyLayer", flags);
        IDictionary layers = layersField?.GetValue(manager) as IDictionary;

        if (layers != null && applyMethod != null)
        {
            foreach (DictionaryEntry entry in layers)
            {
                ClumpLayerManager.ClumpLayer layer = entry.Value as ClumpLayerManager.ClumpLayer;
                if (layer != null) applyMethod.Invoke(manager, new object[] { layer });
            }
        }

        reapplyScheduled = false;
    }
}
