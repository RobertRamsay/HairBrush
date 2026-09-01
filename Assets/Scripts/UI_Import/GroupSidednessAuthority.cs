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

    // Statics survive "Enter Play Mode -> Disable Domain Reload", which this project has on.
    // GroupNormalFlipAuthority next door has always reset; this one had not, so a play-mode
    // restart came back with the previous run's SS/DS map still in it while the flip map beside
    // it had been cleared - two controls on the same row disagreeing about whether the session
    // was new. Noticed while making the profile force both of them.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        singleSidedByGroup.Clear();
        pendingRestore = null;
        pendingRestoreFrames = 0;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<GroupSidednessAuthority>() != null) return;
        GameObject go = new GameObject(nameof(GroupSidednessAuthority));
        DontDestroyOnLoad(go);
        go.AddComponent<GroupSidednessAuthority>();
    }

    // What actually gets rendered. Under DIAMOND that is single sided for every group and
    // there is nothing to decide: a closed section has an outward normal on every face, so the
    // far pair is genuinely facing away and culling it is correct rather than a saving. Double
    // sided there would draw those faces a second time, from the inside, over the top of the
    // ones in front - the exact artefact the diamond exists to remove.
    public static bool IsSingleSided(int groupId)
    {
        if (HairCardSection.IsDiamond) return true;
        return IsSingleSidedStored(groupId);
    }

    // What the user chose, whether or not the current profile is honouring it.
    //
    // Kept separate from IsSingleSided so a groom switched to DIAMOND and saved does not come
    // back with every group's SS/DS choice overwritten by the profile's answer. Switch back to
    // TENT and the buttons say what they said before.
    public static bool IsSingleSidedStored(int groupId)
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

    // Called by HairCardSection when the profile changes. The scan below would get there within
    // a tenth of a second anyway; this makes the switch land on the same frame as the rebuild
    // instead of a beat after it, which on a large groom is a visible flash of the wrong cull.
    public static void ReapplyAll()
    {
        ApplyAll();
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

            // Stored, not effective - see IsSingleSidedStored. Saving the effective answer
            // while DIAMOND is on would write SS into every group and lose the choice for good.
            group.singleSided = IsSingleSidedStored(group.groupId);
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

        // ONLY WHEN THE ANSWER COULD HAVE CHANGED.
        //
        // This timer is insurance, not the mechanism: every real edit - SetSingleSided, Forget,
        // ReapplyAll, a project restore - calls ApplyAll directly. The scan existed to catch
        // cards that appeared without one of those, and it was paying for that by re-asserting
        // the cull state of every card in the groom ten times a second: forty thousand
        // GetComponent calls and forty thousand native sharedMaterial reads per scan, to write
        // back the value already there.
        //
        // SidednessState folds together every input the answer depends on, so the insurance
        // still holds - a card spawned into a single-sided group moves RegistryVersion and is
        // caught on the next scan - at the cost of one integer compare when nothing happened.
        int state = SidednessState();
        if (state == lastAppliedState) return;
        lastAppliedState = state;

        ApplyAll();
    }

    // See the guard in Update. -1 is a value SidednessState cannot return for a real scene, so
    // the first scan always applies.
    private int lastAppliedState = -1;

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
        IReadOnlyList<HairCard> cards = HairCard.All;
        for (int i = 0; i < cards.Count; i++)
        {
            HairCard card = cards[i];
            if (card == null) continue;
            card.SetDoubleSided(!IsSingleSided(card.groupId));
        }
    }

    // What ApplyAll's answer depends on. Every input is an int or a bool, so noticing that none
    // of them moved costs one comparison instead of a sweep of the whole groom.
    static int SidednessState()
    {
        int hash = 17;
        unchecked
        {
            hash = hash * 31 + HairCard.RegistryVersion;
            hash = hash * 31 + (HairCardSection.IsDiamond ? 1 : 0);
            hash = hash * 31 + singleSidedByGroup.Count;
            foreach (KeyValuePair<int, bool> entry in singleSidedByGroup)
            {
                // Summed rather than folded in sequence: Dictionary enumeration order is not
                // promised, and a hash that depended on it would report a change every time the
                // dictionary happened to rehash.
                hash += entry.Key * 2 + (entry.Value ? 1 : 0);
            }
        }
        return hash;
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

            // The label follows what is on screen, so under DIAMOND it reads SS whatever the
            // group's own setting says - a button reading DS beside a single-sided card would
            // be a lie. Dimmed rather than hidden, and left in the row: it comes back holding
            // the same value the moment the profile goes back to TENT, and a control that
            // vanishes and returns is harder to trust than one that visibly has no say.
            bool forced = HairCardSection.IsDiamond;
            bool single = IsSingleSided(gid);

            if (text != null)
            {
                string label = "DS";
                if (single) label = "SS";
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
                    if (single) colour = new Color(.62f, .38f, .18f, 1f);
                    if (forced) colour = new Color(.20f, .20f, .20f, 1f);
                    if (image.color != colour) image.color = colour;
                }
            }
        }
    }

    void Toggle(int groupId)
    {
        // Refused rather than swallowed. The click is a reasonable thing to try, and silence
        // would read as a broken button rather than as a control the profile has taken over.
        if (HairCardSection.IsDiamond)
        {
            StatusToast.Show("DIAMOND cards are single sided - every face already points outward. Switch the CARD profile to TENT to choose SS/DS per group.", true, 5f);
            return;
        }

        SetSingleSided(groupId, !IsSingleSidedStored(groupId));
        nextScan = 0f;
    }
}
