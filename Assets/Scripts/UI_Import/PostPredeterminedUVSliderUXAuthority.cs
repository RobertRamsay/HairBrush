using System;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

// Repairs the ownership handoff between POST editing and the group-root UV router.
// Clicking any control inside a GroupItem exits POST editing, so GroupPredeterminedUVController
// can immediately own ADJ/PRE switching again instead of seeing a stale POST selection flag.
[DefaultExecutionOrder(6095)]
public class GroupUVRootPostExitAuthority : MonoBehaviour
{
    private ModelViewer viewer;
    private PostAffectorManager posts;
    private FieldInfo activeIdField;
    private FieldInfo activeGroupField;
    private FieldInfo hasSelectionField;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<GroupUVRootPostExitAuthority>() != null) return;
        GameObject go = new GameObject("GroupUVRootPostExitAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<GroupUVRootPostExitAuthority>();
    }

    void Update()
    {
        Resolve();
        if (viewer == null || posts == null || EventSystem.current == null) return;

        int activeId = activeIdField != null && activeIdField.GetValue(posts) is int id ? id : -1;
        if (activeId < 0) return;

        GameObject selected = EventSystem.current.currentSelectedGameObject;
        if (selected == null || !IsInsideGroupRoot(selected.transform)) return;

        if (activeIdField != null) activeIdField.SetValue(posts, -1);
        if (activeGroupField != null) activeGroupField.SetValue(posts, -1);
        if (hasSelectionField != null) hasSelectionField.SetValue(viewer, false);
    }

    void Resolve()
    {
        if (viewer == null)
        {
            viewer = FindFirstObjectByType<ModelViewer>();
            if (viewer != null)
                hasSelectionField = typeof(ModelViewer).GetField("hasSelectionHotspot", BindingFlags.Instance | BindingFlags.NonPublic);
        }

        if (posts == null)
        {
            posts = FindFirstObjectByType<PostAffectorManager>();
            if (posts != null)
            {
                BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                activeIdField = typeof(PostAffectorManager).GetField("activeId", flags);
                activeGroupField = typeof(PostAffectorManager).GetField("activeGroup", flags);
            }
        }
    }

    static bool IsInsideGroupRoot(Transform transform)
    {
        for (Transform current = transform; current != null; current = current.parent)
        {
            if (current.name.StartsWith("PostAffector_", StringComparison.Ordinal) ||
                current.name.StartsWith("GroupClumper_", StringComparison.Ordinal))
                return false;
            if (current.name.StartsWith("GroupItem_", StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}

// Compatibility authority for the short-lived slider version of POST PRE UV controls.
// POST now deliberately reuses the same compact MIN -> MAX / SEED / R row as the group root.
// Keeping this class name also makes hot-reload safe if the previous runtime component exists.
[DefaultExecutionOrder(6120)]
public class PostPredeterminedUVSliderUXAuthority : MonoBehaviour
{
    private PostPredeterminedUVUIAuthority rootStyleUI;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void SpawnCompatibility()
    {
        if (FindFirstObjectByType<PostPredeterminedUVSliderUXAuthority>() != null) return;
        GameObject go = new GameObject("PostPredeterminedUVSliderUXAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<PostPredeterminedUVSliderUXAuthority>();
    }

    void Update()
    {
        if (rootStyleUI == null) rootStyleUI = FindFirstObjectByType<PostPredeterminedUVUIAuthority>();
        if (rootStyleUI != null && !rootStyleUI.enabled) rootStyleUI.enabled = true;

        RemoveOldSliderRows();
        MirrorRootLabel();
    }

    static void RemoveOldSliderRows()
    {
        foreach (RectTransform rect in FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (rect == null) continue;
            if (rect.name == "POST_PRE_MIN_Row" ||
                rect.name == "POST_PRE_MAX_Row" ||
                rect.name == "POST_PRE_SEED_Row")
                Destroy(rect.gameObject);
        }
    }

    static void MirrorRootLabel()
    {
        foreach (RectTransform rect in FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (rect == null || rect.name != "PostPredeterminedUV_Row") continue;
            TextMeshProUGUI[] labels = rect.GetComponentsInChildren<TextMeshProUGUI>(true);
            foreach (TextMeshProUGUI label in labels)
            {
                if (label != null && label.text == "POST UV")
                {
                    label.text = "UV RECTS";
                    return;
                }
            }
        }
    }
}
