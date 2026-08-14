using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

// Once a group has zero POST affectors, no POST-local cache should be allowed to keep
// re-applying stale evaluated values. At the end of the frame, POST-free groups render
// directly from each HairCard's own canonical authored state.
[DefaultExecutionOrder(5000)]
public class PostFreeCanonicalAuthority : MonoBehaviour
{
    private PostAffectorManager manager;
    private FieldInfo groupsField;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<PostFreeCanonicalAuthority>() != null) return;
        GameObject go = new GameObject("PostFreeCanonicalAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<PostFreeCanonicalAuthority>();
    }

    void LateUpdate()
    {
        Resolve();
        if (manager == null || groupsField == null) return;

        IDictionary groups = groupsField.GetValue(manager) as IDictionary;
        HashSet<int> groupsWithPosts = new HashSet<int>();
        if (groups != null)
        {
            foreach (DictionaryEntry entry in groups)
            {
                if (!(entry.Key is int gid)) continue;
                int count = entry.Value is ICollection collection ? collection.Count : 0;
                if (count > 0) groupsWithPosts.Add(gid);
            }
        }

        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
        {
            if (card == null || groupsWithPosts.Contains(card.groupId)) continue;
            card.SetSelectionWeight(0f);
            card.ApplyEvaluatedState(card.GetCanonicalState());
        }
    }

    void Resolve()
    {
        if (manager != null) return;
        manager = FindFirstObjectByType<PostAffectorManager>();
        if (manager != null)
            groupsField = typeof(PostAffectorManager).GetField("groups", BindingFlags.Instance | BindingFlags.NonPublic);
    }
}
