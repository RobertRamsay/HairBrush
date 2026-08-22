using System.Collections;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Loading content must never inherit an editor gesture/mode from the previous session.
// New OBJ: clear modifier definitions as well as transient selection.
// Project: keep restored POST/CLUMPER definitions, but leave them inactive and land on Group 0.
[DefaultExecutionOrder(4925)]
public class SessionModifierFreshStartAuthority : MonoBehaviour
{
    private ModelViewer viewer;
    private Button boundProjectButton;
    private GameObject lastModel;
    private bool projectLoadRequested;
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<SessionModifierFreshStartAuthority>() != null) return;
        GameObject go = new GameObject("SessionModifierFreshStartAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<SessionModifierFreshStartAuthority>();
    }

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + .05f;

        if (viewer == null)
        {
            viewer = FindFirstObjectByType<ModelViewer>();
            lastModel = GetLoadedModel();
        }
        if (viewer == null) return;

        BindProjectButton();

        GameObject current = GetLoadedModel();
        if (current != null && current != lastModel)
        {
            lastModel = current;
            ClearGuidesForNewSession();
            if (projectLoadRequested)
                ResetTransientEditorState();
            else
            {
                ClearClumpersForNewModel();
                ResetTransientEditorState();
            }
            projectLoadRequested = false;
        }
        else if (current == null)
        {
            lastModel = null;
        }
    }

    void BindProjectButton()
    {
        if (viewer.loadProjectButton == null || boundProjectButton == viewer.loadProjectButton) return;
        boundProjectButton = viewer.loadProjectButton;
        boundProjectButton.onClick.AddListener(() => projectLoadRequested = true);
    }

    GameObject GetLoadedModel()
    {
        FieldInfo f = typeof(ModelViewer).GetField("loadedModel", BindingFlags.Instance | BindingFlags.NonPublic);
        return f?.GetValue(viewer) as GameObject;
    }

    void ResetTransientEditorState()
    {
        // ModelViewer local/POST hotspot state and all old per-card brush weights.
        Invoke(viewer, "ClearSelectionHotspot");
        SetField(viewer, "hasSelectionHotspot", false);
        SetField(viewer, "isSelectionMode", false);
        viewer.lastPlacedCard = null;
        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
            if (card != null) card.SetSelectionWeight(0f);

        // POST definitions remain on project load; only edit ownership is released.
        PostAffectorManager post = FindFirstObjectByType<PostAffectorManager>();
        if (post != null)
        {
            SetField(post, "activeId", -1);
            SetField(post, "activeGroup", -1);
            SetField(post, "nextUIScan", 0f);
        }

        // Same rule for CLUMPER: definition remains, editor selection/panel does not.
        GroupClumperManager clumper = FindFirstObjectByType<GroupClumperManager>();
        if (clumper != null)
        {
            SetField(clumper, "selectedGroup", -1);
            Invoke(clumper, "DestroyControls");
            Invoke(clumper, "RebuildRowsSoon");
        }

        GameObject scroll = GameObject.Find("ClumperScrollHost");
        if (scroll != null) Destroy(scroll);

        // Always enter loaded content at the group root. Selecting a POST/CLUMPER later
        // switches to its owning group through the normal modifier selection authority.
        EnsureGroupZeroExists();
        MethodInfo select = typeof(ModelViewer).GetMethod("SelectGroup", BindingFlags.Instance | BindingFlags.NonPublic);
        if (select != null) select.Invoke(viewer, new object[] { 0 });
        else viewer.currentGroupId = 0;

        if (EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(null);
    }

    // Guides are session-only - nothing writes them to a project file yet - so ANY load has to
    // drop them, and this is the only method that runs on both branches.
    // GroomSessionResetCoordinator's ClearAll is deliberately skipped on the project path
    // (projectLoadJustCompleted), and ClearClumpersForNewModel is the new-OBJ branch only, so
    // putting it in either of those leaves the project path uncovered: the previous model's
    // guides survive, adopt the loaded project's reused group ids, and comb its hair from a
    // contact point in geometry that no longer exists.
    void ClearGuidesForNewSession()
    {
        GuideCurveManager guides = FindFirstObjectByType<GuideCurveManager>();
        if (guides != null) guides.ClearAll();
    }

    void ClearClumpersForNewModel()
    {
        GroupClumperManager clumper = FindFirstObjectByType<GroupClumperManager>();
        if (clumper == null) return;

        FieldInfo groupsField = typeof(GroupClumperManager).GetField("byGroup", BindingFlags.Instance | BindingFlags.NonPublic);
        if (groupsField?.GetValue(clumper) is IDictionary dict) dict.Clear();
        SetField(clumper, "selectedGroup", -1);
        Invoke(clumper, "DestroyControls");
        Invoke(clumper, "RebuildRowsSoon");

        foreach (RectTransform row in FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (row != null && row.name.StartsWith("GroupClumper_")) Destroy(row.gameObject);
    }

    void EnsureGroupZeroExists()
    {
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        FieldInfo idsField = typeof(ModelViewer).GetField("allGroupIds", flags);
        object ids = idsField?.GetValue(viewer);
        MethodInfo add = ids?.GetType().GetMethod("Add");
        add?.Invoke(ids, new object[] { 0 });

        FieldInfo namesField = typeof(ModelViewer).GetField("groupNames", flags);
        if (namesField?.GetValue(viewer) is IDictionary names && !names.Contains(0))
            names[0] = "Group 0 (Default)";
    }

    static void SetField(object owner, string name, object value)
    {
        if (owner == null) return;
        FieldInfo f = owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        f?.SetValue(owner, value);
    }

    static void Invoke(object owner, string name)
    {
        if (owner == null) return;
        MethodInfo m = owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        m?.Invoke(owner, null);
    }
}
