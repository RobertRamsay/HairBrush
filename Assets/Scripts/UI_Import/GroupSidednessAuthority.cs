using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Per-group SS / DS toggle, sitting on each Hair Group row just left of UV: ADJ/PRE.
//
// This is purely a rendering choice - it changes the hair shader's cull mode for that
// group's cards and nothing else. No geometry, no card state, no export. Double sided (DS)
// is the default and what every existing project gets.
//
// Cards do not each get their own material for this. HairCard keeps ONE shared single-sided
// clone per source material, so a whole group flipped to SS still batches with every other
// single-sided card instead of turning into hundreds of unique materials.
[DefaultExecutionOrder(6810)]
public class GroupSidednessAuthority : MonoBehaviour
{
    public const string ButtonName = "GroupSidednessButton";

    private const float ScanInterval = .10f;

    // Groups absent from this map render double sided, which is both the historical
    // behaviour and what a project saved before this feature existed decodes to.
    private static readonly Dictionary<int, bool> singleSidedByGroup = new Dictionary<int, bool>();

    private static HairProjectSaveData pendingRestore;
    private static int pendingRestoreFrames;

    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<GroupSidednessAuthority>() != null) return;
        GameObject go = new GameObject(nameof(GroupSidednessAuthority));
        DontDestroyOnLoad(go);
        go.AddComponent<GroupSidednessAuthority>();
    }

    public static bool IsSingleSided(int groupId)
    {
        bool single;
        if (singleSidedByGroup.TryGetValue(groupId, out single)) return single;
        return false;
    }

    public static void SetSingleSided(int groupId, bool single)
    {
        singleSidedByGroup[groupId] = single;
        ApplyGroup(groupId);
    }

    public static void Forget(int groupId)
    {
        singleSidedByGroup.Remove(groupId);
        ApplyGroup(groupId);
    }

    public static void ForgetAll()
    {
        singleSidedByGroup.Clear();
    }

    public static void Capture(HairProjectSaveData data)
    {
        if (data == null || data.groups == null) return;
        foreach (GroupSaveData group in data.groups)
        {
            if (group == null) continue;
            group.singleSided = IsSingleSided(group.groupId);
        }
    }

    // Restoring is deferred: the cards for a loaded project do not all exist on the frame
    // load returns, and applying to a half-built group would leave the rest double sided.
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

        // Keeps the shared single-sided clone in step with whatever the base hair
        // material currently is - textures included - after a Texture Editor change.
        HairCard.RefreshSingleSidedVariants();
        ApplyAll();
    }

    void TryRestorePending()
    {
        if (pendingRestore == null) return;

        // Two frames, the same settle the other restore bridges use.
        if (++pendingRestoreFrames < 2) return;

        HairProjectSaveData data = pendingRestore;
        pendingRestore = null;

        singleSidedByGroup.Clear();
        if (data.groups != null)
        {
            foreach (GroupSaveData group in data.groups)
            {
                if (group == null) continue;
                singleSidedByGroup[group.groupId] = group.singleSided;
            }
        }

        ApplyAll();
    }

    static void ApplyAll()
    {
        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
        {
            if (card == null) continue;
            card.SetDoubleSided(!IsSingleSided(card.groupId));
        }
    }

    static void ApplyGroup(int groupId)
    {
        bool doubleSided = !IsSingleSided(groupId);
        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
        {
            if (card == null || card.groupId != groupId) continue;
            card.SetDoubleSided(doubleSided);
        }
    }

    void MaintainGroupButtons()
    {
        foreach (RectTransform row in FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (row == null || !row.name.StartsWith("GroupItem_")) continue;
            if (!int.TryParse(row.name.Substring("GroupItem_".Length), out int gid)) continue;

            Transform solo = row.Find("SoloButton");
            if (solo == null) continue;

            Transform existing = row.Find(ButtonName);
            Button button;
            TextMeshProUGUI text;

            if (existing == null)
            {
                GameObject go = new GameObject(ButtonName, typeof(RectTransform), typeof(Image), typeof(Button));
                go.transform.SetParent(row, false);
                go.GetComponent<RectTransform>().sizeDelta = new Vector2(44f, 36f);
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

                // Keep the row reading Name | SS/DS | UV | SOLO.
                go.transform.SetSiblingIndex(Mathf.Clamp(solo.GetSiblingIndex(), 0, row.childCount - 1));
            }
            else
            {
                button = existing.GetComponent<Button>();
                text = existing.GetComponentInChildren<TextMeshProUGUI>(true);
            }

            bool single = IsSingleSided(gid);
            if (text != null)
            {
                string label = "DS";
                if (single) label = "SS";
                if (text.text != label) text.text = label;
            }

            if (button != null)
            {
                Image image = button.GetComponent<Image>();
                if (image != null)
                {
                    Color colour = new Color(.28f, .28f, .28f, 1f);
                    if (single) colour = new Color(.62f, .38f, .18f, 1f);
                    if (image.color != colour) image.color = colour;
                }
            }
        }
    }

    void Toggle(int groupId)
    {
        SetSingleSided(groupId, !IsSingleSided(groupId));
        nextScan = 0f;
    }
}
