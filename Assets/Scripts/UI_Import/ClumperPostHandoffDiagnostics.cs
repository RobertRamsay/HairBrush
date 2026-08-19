using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

// TEMPORARY DIAGNOSTIC - delete once the clumper/POST handoff is confirmed fixed.
//
// Logs the six values that decide whether a POST slider drag can reach the hair cards.
// It only logs when one of them CHANGES, so the console stays readable during a session.
//
// What to look for after removing a clumper with [-] and then dragging a POST slider:
//
//   hotspot=True  activeId=-1   -> THE TRAP. Sliders are interactable (ModifierCoreLock
//                                  unlocks on hotspot) but POST has no active affector to
//                                  write a delta into. Sliders move, geometry frozen.
//   hotspot=True  activeId>=0
//                 curGroup != activeGroup -> MaintainActiveAuthoring bails every frame.
//   hotspot=True  activeId>=0  effect=0   -> POST is live but its spatial weight reaches
//                                            no cards (radius/falloff/centre wrong).
//   hotspot=False                          -> sliders should be greyed out; if they are
//                                            not, ModifierCoreLock is not seeing the POSTs.
[DefaultExecutionOrder(9900)]
public class ClumperPostHandoffDiagnostics : MonoBehaviour
{
    private ModelViewer viewer = null;
    private PostAffectorManager posts = null;
    private GroupClumperManager clumpers = null;

    private FieldInfo hasSelectionField = null;
    private FieldInfo selectionModeField = null;
    private FieldInfo activeIdField = null;
    private FieldInfo activeGroupField = null;
    private FieldInfo postGroupsField = null;

    private string lastLine = "";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<ClumperPostHandoffDiagnostics>() != null) return;
        GameObject go = new GameObject("ClumperPostHandoffDiagnostics");
        DontDestroyOnLoad(go);
        go.AddComponent<ClumperPostHandoffDiagnostics>();
    }

    void LateUpdate()
    {
        Resolve();
        if (viewer == null || posts == null) return;

        bool hotspot = ReadBool(hasSelectionField);
        bool selectionMode = ReadBool(selectionModeField);
        int activeId = ReadInt(activeIdField);
        int activeGroup = ReadInt(activeGroupField);
        int currentGroup = viewer.currentGroupId;

        int postCount = 0;
        var groups = postGroupsField.GetValue(posts) as Dictionary<int, List<PostAffectorManager.PostAffector>>;
        if (groups != null && groups.TryGetValue(currentGroup, out List<PostAffectorManager.PostAffector> list) && list != null)
        {
            postCount = list.Count;
        }

        int clumperCount = 0;
        int selectedClumper = -1;
        if (clumpers != null)
        {
            clumperCount = clumpers.GetGroupClumpers(currentGroup).Count;
            GroupClumperManager.GroupClumper selected = clumpers.GetSelectedClumper();
            if (selected != null) selectedClumper = selected.id;
        }

        float maxWeight = 0f;
        int cardsInGroup = 0;
        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
        {
            if (card == null || card.groupId != currentGroup) continue;
            cardsInGroup++;
            if (card.selectionWeight > maxWeight) maxWeight = card.selectionWeight;
        }

        string line =
            "HANDOFF hotspot=" + hotspot +
            " selMode=" + selectionMode +
            " activeId=" + activeId +
            " activeGroup=" + activeGroup +
            " curGroup=" + currentGroup +
            " posts=" + postCount +
            " clumpers=" + clumperCount +
            " selClumper=" + selectedClumper +
            " cards=" + cardsInGroup +
            " maxSelWeight=" + maxWeight.ToString("F3") +
            " activeClump=" + GroupClumperManager.HasActiveClumper(currentGroup);

        if (line == lastLine) return;
        lastLine = line;
        Debug.Log(line);
    }

    void Resolve()
    {
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;

        if (viewer == null)
        {
            viewer = FindFirstObjectByType<ModelViewer>();
            if (viewer != null)
            {
                hasSelectionField = typeof(ModelViewer).GetField("hasSelectionHotspot", flags);
                selectionModeField = typeof(ModelViewer).GetField("isSelectionMode", flags);
            }
        }

        if (posts == null)
        {
            posts = FindFirstObjectByType<PostAffectorManager>();
            if (posts != null)
            {
                activeIdField = typeof(PostAffectorManager).GetField("activeId", flags);
                activeGroupField = typeof(PostAffectorManager).GetField("activeGroup", flags);
                postGroupsField = typeof(PostAffectorManager).GetField("groups", flags);
            }
        }

        if (clumpers == null) clumpers = FindFirstObjectByType<GroupClumperManager>();
    }

    bool ReadBool(FieldInfo field)
    {
        if (field == null) return false;
        object value = field.GetValue(viewer);
        if (value is bool result) return result;
        return false;
    }

    int ReadInt(FieldInfo field)
    {
        if (field == null) return -999;
        object value = field.GetValue(posts);
        if (value is int result) return result;
        return -999;
    }
}
