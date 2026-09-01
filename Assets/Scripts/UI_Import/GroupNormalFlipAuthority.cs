using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Per-group N+ / N- toggle, sitting on each Hair Group row just left of SS/DS.
//
// Unlike the sidedness toggle next to it, this is NOT purely a rendering choice. N- flips the
// whole form:
//
//   1. The triangle winding reverses, which is what actually flips the surface normals.
//   2. The cross-section ridge inverts, turning the card's shallow convex arch into a concave
//      one - the A / V flip.
//
// The two are deliberately one control rather than two. Reversing the winding on its own
// leaves the geometry arching one way while the lighting says the other, which reads as a
// shading error rather than a choice; inverting the ridge on its own leaves a concave card lit
// as though it were still convex. Together they are a genuine mirror of the surface, which is
// the thing worth having a button for.
//
// Groups absent from the map render N+, which is the historical behaviour and what a project
// saved before this feature existed decodes to.
[DefaultExecutionOrder(6805)]
public class GroupNormalFlipAuthority : MonoBehaviour
{
    public const string ButtonName = "GroupNormalFlipButton";

    private const float ScanInterval = .10f;

    private static readonly Dictionary<int, bool> flippedByGroup = new Dictionary<int, bool>();

    private static HairProjectSaveData pendingRestore;
    private static int pendingRestoreFrames;

    private float nextScan;

    // Statics survive "Enter Play Mode -> Disable Domain Reload", so a flip left on when play
    // stopped would still be set when play started again, with no lit button to explain it.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        flippedByGroup.Clear();
        pendingRestore = null;
        pendingRestoreFrames = 0;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<GroupNormalFlipAuthority>() != null) return;
        GameObject go = new GameObject(nameof(GroupNormalFlipAuthority));
        DontDestroyOnLoad(go);
        go.AddComponent<GroupNormalFlipAuthority>();
    }

    // Read once per card per mesh rebuild, and folded into HairCard's mesh-input hash - which
    // is what makes a toggle take effect without anything here having to hunt down the cards.
    // Under DIAMOND there is nothing to flip. N- exists because an OPEN surface has a right
    // side and a wrong side and the tent gives you no way to know which one you are looking at;
    // a closed section has an outward normal on every face by construction, so the answer is
    // already correct and reversing the winding would only turn every card inside out. The
    // group's own setting is kept, not cleared - see IsFlippedStored.
    public static bool IsFlipped(int groupId)
    {
        if (HairCardSection.IsDiamond) return false;
        return IsFlippedStored(groupId);
    }

    // What the user chose, whether or not the current profile is honouring it. Saving and the
    // button label both read this; the mesh reads IsFlipped.
    public static bool IsFlippedStored(int groupId)
    {
        bool flipped;
        if (flippedByGroup.TryGetValue(groupId, out flipped)) return flipped;
        return false;
    }

    public static void SetFlipped(int groupId, bool flipped)
    {
        flippedByGroup[groupId] = flipped;
    }

    public static void Forget(int groupId)
    {
        flippedByGroup.Remove(groupId);
    }

    public static void ForgetAll()
    {
        flippedByGroup.Clear();
    }

    public static void Capture(HairProjectSaveData data)
    {
        if (data == null || data.groups == null) return;
        foreach (GroupSaveData group in data.groups)
        {
            if (group == null) continue;

            // Stored, not effective. Saving the effective answer while DIAMOND is on would
            // clear every group's N- and it would not come back with TENT.
            group.normalFlipped = IsFlippedStored(group.groupId);
        }
    }

    // Deferred for the same reason sidedness defers: the cards of a loaded project do not all
    // exist on the frame load returns.
    public static void QueueRestore(HairProjectSaveData data)
    {
        pendingRestore = data;
        pendingRestoreFrames = 0;
    }

    void Update()
    {
        TryRestorePending();

        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + ScanInterval;

        MaintainGroupButtons();
    }

    void TryRestorePending()
    {
        if (pendingRestore == null) return;
        if (++pendingRestoreFrames < 2) return;

        HairProjectSaveData data = pendingRestore;
        pendingRestore = null;

        flippedByGroup.Clear();
        if (data.groups != null)
        {
            foreach (GroupSaveData group in data.groups)
            {
                if (group == null) continue;
                flippedByGroup[group.groupId] = group.normalFlipped;
            }
        }
    }

    void MaintainGroupButtons()
    {
        // Shared, throttled - see GroupRowRegistry.
        IReadOnlyList<GroupRowRegistry.Row> registryRows = GroupRowRegistry.Rows;
        for (int r = 0; r < registryRows.Count; r++)
        {
            RectTransform row = registryRows[r].transform;
            int gid = registryRows[r].groupId;
            if (row == null) continue;

            // Anchor off SOLO exactly as the sidedness button does. GroupPanelPostHintStats
            // owns the final placement; this only has to land in the row.
            Transform solo = row.Find("SoloButton");
            if (solo == null) continue;

            Transform existing = row.Find(ButtonName);

            Button button;
            TextMeshProUGUI text;

            if (existing == null)
            {
                GameObject go = new GameObject(ButtonName, typeof(RectTransform), typeof(Image), typeof(Button));
                go.transform.SetParent(row, false);
                go.GetComponent<RectTransform>().sizeDelta = new Vector2(40f, 36f);
                button = go.GetComponent<Button>();
                int captured = gid;
                button.onClick.AddListener(() => Toggle(captured));

                GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
                textGO.transform.SetParent(go.transform, false);
                RectTransform tr = textGO.GetComponent<RectTransform>();
                tr.anchorMin = Vector2.zero;
                tr.anchorMax = Vector2.one;
                tr.offsetMin = Vector2.zero;
                tr.offsetMax = Vector2.zero;
                text = textGO.GetComponent<TextMeshProUGUI>();
                text.fontSize = 10f;
                text.fontStyle = FontStyles.Bold;
                text.alignment = TextAlignmentOptions.Center;
                text.color = Color.white;
                text.raycastTarget = false;

                // Keep the row reading Name | N+/N- | SS/DS | UV | SOLO.
                go.transform.SetSiblingIndex(Mathf.Clamp(solo.GetSiblingIndex(), 0, row.childCount - 1));
            }
            else
            {
                button = existing.GetComponent<Button>();
                text = existing.GetComponentInChildren<TextMeshProUGUI>(true);
            }

            // Effective, so under DIAMOND it reads N+ whatever the group holds, dimmed to say
            // the profile is answering rather than the button. Same treatment as SS/DS beside
            // it, and for the same reason.
            bool forced = HairCardSection.IsDiamond;
            bool flipped = IsFlipped(gid);

            if (text != null)
            {
                string label = "N+";
                if (flipped) label = "N-";
                if (text.text != label) text.text = label;

                Color textColour = Color.white;
                if (forced) textColour = new Color(1f, 1f, 1f, .35f);
                if (text.color != textColour) text.color = textColour;
            }

            if (button != null)
            {
                Image image = button.GetComponent<Image>();
                if (image != null)
                {
                    Color colour = new Color(.28f, .28f, .28f, 1f);
                    if (flipped) colour = new Color(.24f, .52f, .46f, 1f);
                    if (forced) colour = new Color(.20f, .20f, .20f, 1f);
                    if (image.color != colour) image.color = colour;
                }
            }
        }
    }

    void Toggle(int groupId)
    {
        if (HairCardSection.IsDiamond)
        {
            StatusToast.Show("DIAMOND cards need no normal flip - a closed section points outward on every face. Switch the CARD profile to TENT to use N+/N-.", true, 5f);
            return;
        }

        SetFlipped(groupId, !IsFlippedStored(groupId));

        // Nothing needs to touch the cards. The flag is part of HairCard's mesh-input hash, so
        // the next per-frame re-assertion sees a changed hash and rebuilds the group by itself.
        nextScan = 0f;
    }
}
