using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

// Loaded HairCards already carry their groupId. If project metadata/UI missed those groups,
// reconstruct ModelViewer's group registry from the cards so the group panel and future saves
// cannot lose ownership information.
[DefaultExecutionOrder(1950)]
public class GroupRegistryFromCardsAuthority : MonoBehaviour
{
    private ModelViewer viewer;
    private FieldInfo idsField;
    private FieldInfo namesField;
    private FieldInfo soloField;
    private FieldInfo uScaleField;
    private FieldInfo vScaleField;
    private FieldInfo uOffsetField;
    private FieldInfo vOffsetField;
    private MethodInfo buildGroupsMethod;
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<GroupRegistryFromCardsAuthority>() != null) return;
        GameObject go = new GameObject("GroupRegistryFromCardsAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<GroupRegistryFromCardsAuthority>();
    }

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + .25f;

        Resolve();
        if (viewer == null || idsField == null) return;

        HairCard[] cards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        if (cards.Length == 0) return;

        HashSet<int> cardIds = new HashSet<int>(cards.Where(c => c != null).Select(c => c.groupId));
        if (cardIds.Count == 0) return;

        HashSet<int> ids = idsField.GetValue(viewer) as HashSet<int>;
        Dictionary<int, string> names = namesField?.GetValue(viewer) as Dictionary<int, string>;
        Dictionary<int, bool> solo = soloField?.GetValue(viewer) as Dictionary<int, bool>;
        Dictionary<int, float> uScales = uScaleField?.GetValue(viewer) as Dictionary<int, float>;
        Dictionary<int, float> vScales = vScaleField?.GetValue(viewer) as Dictionary<int, float>;
        Dictionary<int, float> uOffsets = uOffsetField?.GetValue(viewer) as Dictionary<int, float>;
        Dictionary<int, float> vOffsets = vOffsetField?.GetValue(viewer) as Dictionary<int, float>;
        if (ids == null) return;

        bool changed = false;
        foreach (int id in cardIds)
        {
            if (ids.Add(id)) changed = true;

            HairCard representative = cards.FirstOrDefault(c => c != null && c.groupId == id);
            // groupNames stores only the optional friendly suffix. The left-panel renderer owns
            // the numeric identity (GROUP n / Gn_Name), so recovery must not recreate legacy
            // strings such as "Group 0 (Default)" inside the authored name field.
            if (names != null && !names.ContainsKey(id)) names[id] = string.Empty;
            if (solo != null && !solo.ContainsKey(id)) solo[id] = false;
            if (uScales != null && !uScales.ContainsKey(id)) uScales[id] = representative != null ? representative.uScale : 1f;
            if (vScales != null && !vScales.ContainsKey(id)) vScales[id] = representative != null ? representative.vScale : 1f;
            if (uOffsets != null && !uOffsets.ContainsKey(id)) uOffsets[id] = representative != null ? representative.uOffset : 0f;
            if (vOffsets != null && !vOffsets.ContainsKey(id)) vOffsets[id] = representative != null ? representative.vOffset : 0f;
        }

        // A group is valid because it exists in the registry, not because it currently owns cards.
        // Newly-created empty groups must remain selectable. Only recover the selection when the
        // selected ID genuinely no longer exists (for example after deleting a group).
        if (!ids.Contains(viewer.currentGroupId))
        {
            viewer.currentGroupId = ids.Count > 0 ? ids.OrderBy(id => id).First() : cardIds.OrderBy(id => id).First();
            changed = true;
        }

        GameObject panel = GameObject.Find("GroupManagerPanel");
        bool hasAnyGroupRow = FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Any(r => r != null && r.name.StartsWith("GroupItem_"));

        // Project load may have built an empty panel before the card-derived registry was ready.
        // Rebuild only when registry data changed or the loaded-card session has no group rows at all.
        if (changed || (panel != null && !hasAnyGroupRow))
            buildGroupsMethod?.Invoke(viewer, null);
    }

    void Resolve()
    {
        if (viewer != null) return;
        viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer == null) return;

        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        System.Type type = typeof(ModelViewer);
        idsField = type.GetField("allGroupIds", flags);
        namesField = type.GetField("groupNames", flags);
        soloField = type.GetField("groupSoloState", flags);
        uScaleField = type.GetField("groupUScales", flags);
        vScaleField = type.GetField("groupVScales", flags);
        uOffsetField = type.GetField("groupUOffsets", flags);
        vOffsetField = type.GetField("groupVOffsets", flags);
        buildGroupsMethod = type.GetMethod("BuildGroupManagementUI", flags);
    }
}
