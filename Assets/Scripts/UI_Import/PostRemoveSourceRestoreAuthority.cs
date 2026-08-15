using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

// POST removal should reveal the upstream authored/source card state. Hook the real remove
// buttons after PostAffectorManager's own listener, then restore SOURCE only when that group
// has no POST affectors left. If another POST remains, PostAffectorManager continues to own
// the evaluated result normally.
[DefaultExecutionOrder(5280)]
public class PostRemoveSourceRestoreAuthority : MonoBehaviour
{
    private readonly HashSet<Button> hooked = new HashSet<Button>();
    private PostAffectorManager manager;
    private FieldInfo groupsField;
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<PostRemoveSourceRestoreAuthority>() != null) return;
        GameObject go = new GameObject("PostRemoveSourceRestoreAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<PostRemoveSourceRestoreAuthority>();
    }

    void Update()
    {
        Resolve();
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + .08f;
        HookButtons();
    }

    void Resolve()
    {
        if (manager != null) return;
        manager = FindFirstObjectByType<PostAffectorManager>();
        if (manager == null) return;
        groupsField = typeof(PostAffectorManager).GetField("groups", BindingFlags.Instance | BindingFlags.NonPublic);
    }

    void HookButtons()
    {
        RectTransform[] rows = FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (RectTransform row in rows)
        {
            if (row == null || !row.name.StartsWith("PostAffector_")) continue;
            string[] parts = row.name.Split('_');
            if (parts.Length < 3 || !int.TryParse(parts[1], out int gid)) continue;

            Button[] buttons = row.GetComponentsInChildren<Button>(true);
            foreach (Button button in buttons)
            {
                if (button == null || button.gameObject.name != "[-]" || hooked.Contains(button)) continue;
                hooked.Add(button);
                int capturedGroup = gid;
                button.onClick.AddListener(() => RestoreIfFinalPostWasRemoved(capturedGroup));
            }
        }
        hooked.RemoveWhere(b => b == null);
    }

    void RestoreIfFinalPostWasRemoved(int gid)
    {
        if (manager == null || groupsField == null) return;
        object raw = groupsField.GetValue(manager);
        if (raw is Dictionary<int, List<PostAffectorManager.PostAffector>> groups && groups.ContainsKey(gid))
            return;

        ModifierEvaluationSnapshots.RestoreSourceGroup(gid);
    }
}
