using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

[DefaultExecutionOrder(6750)]
public class GroupUVSeedButtonFix : MonoBehaviour
{
    private GroupPredeterminedUVController controller;
    private ModelViewer viewer;
    private MethodInfo setSeedMethod;
    private Button button;
    private TMP_InputField seedInput;
    private float nextScan;
    private bool wasFocused;
    private string editText = "0";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<GroupUVSeedButtonFix>() != null) return;
        GameObject go = new GameObject("GroupUVSeedButtonFix");
        DontDestroyOnLoad(go);
        go.AddComponent<GroupUVSeedButtonFix>();
    }

    void Update()
    {
        Resolve();
        Bind();
        MaintainEdit();

        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + 0.1f;
        OwnRandomButton();
    }

    void Resolve()
    {
        if (controller == null)
        {
            controller = FindFirstObjectByType<GroupPredeterminedUVController>();
            if (controller != null)
                setSeedMethod = typeof(GroupPredeterminedUVController).GetMethod("SetSeed", BindingFlags.Instance | BindingFlags.NonPublic);
        }
        if (viewer == null) viewer = FindFirstObjectByType<ModelViewer>();
    }

    void Bind()
    {
        if (viewer == null || viewer.groomingSliderPanelGO == null || controller == null || setSeedMethod == null) return;
        Transform row = viewer.groomingSliderPanelGO.transform.Find("GroupUVPredetermined_Row");
        if (row == null)
        {
            button = null;
            seedInput = null;
            wasFocused = false;
            return;
        }

        Button newButton = row.Find("GroupUVRandomSeedButton")?.GetComponent<Button>();
        TMP_InputField newInput = row.Find("SEEDInput")?.GetComponent<TMP_InputField>();
        if (newButton == button && newInput == seedInput) return;

        if (seedInput != null)
            seedInput.onValueChanged.RemoveListener(OnSeedChanged);

        button = newButton;
        seedInput = newInput;
        wasFocused = false;
        editText = seedInput != null ? seedInput.text : "0";

        if (seedInput != null)
            seedInput.onValueChanged.AddListener(OnSeedChanged);

        OwnRandomButton();
        Compact(row);
    }

    void OwnRandomButton()
    {
        if (button == null) return;
        button.onClick.RemoveAllListeners();
        button.onClick.AddListener(Reshuffle);
    }

    void MaintainEdit()
    {
        if (seedInput == null) return;

        bool focused = seedInput.isFocused;
        if (focused && !wasFocused)
            editText = seedInput.text;

        if (focused)
        {
            if (seedInput.text != editText)
                seedInput.SetTextWithoutNotify(editText);
        }
        else if (wasFocused && int.TryParse(editText, out int parsed))
        {
            SetSeed(parsed);
        }

        wasFocused = focused;
    }

    void OnSeedChanged(string value)
    {
        if (seedInput == null || !seedInput.isFocused) return;
        editText = value;
        if (int.TryParse(value, out int parsed))
            SetSeed(parsed);
    }

    void SetSeed(int seed)
    {
        if (controller == null || viewer == null || setSeedMethod == null) return;
        setSeedMethod.Invoke(controller, new object[] { viewer.currentGroupId, seed.ToString() });
    }

    void Compact(Transform row)
    {
        Transform range = row.Find("UVRectRangeSlider");
        if (range != null)
        {
            LayoutElement le = range.GetComponent<LayoutElement>();
            if (le != null)
            {
                le.preferredWidth = 170f;
                le.minWidth = 135f;
            }
            range.GetComponent<RectTransform>().sizeDelta = new Vector2(170f, 30f);
        }
        if (seedInput != null) seedInput.GetComponent<RectTransform>().sizeDelta = new Vector2(70f, 30f);
        if (button != null) button.GetComponent<RectTransform>().sizeDelta = new Vector2(34f, 30f);
    }

    void Reshuffle()
    {
        if (controller == null || viewer == null || setSeedMethod == null) return;

        int oldSeed = 0;
        if (seedInput != null) int.TryParse(seedInput.text, out oldSeed);

        HairCard[] cards = FindObjectsByType<HairCard>(FindObjectsSortMode.None)
            .Where(c => c != null && c.groupId == viewer.currentGroupId)
            .ToArray();
        List<Vector4> before = Capture(cards);

        int chosen = oldSeed;
        for (int i = 0; i < 64; i++)
        {
            int candidate = UnityEngine.Random.Range(0, 1000000);
            if (candidate == oldSeed) continue;
            SetSeed(candidate);
            chosen = candidate;
            if (cards.Length == 0 || Changed(cards, before)) break;
        }

        if (chosen == oldSeed)
        {
            chosen = oldSeed == int.MaxValue ? 0 : oldSeed + 1;
            SetSeed(chosen);
        }

        editText = chosen.ToString();
        if (seedInput != null) seedInput.SetTextWithoutNotify(editText);
    }

    static List<Vector4> Capture(HairCard[] cards)
    {
        List<Vector4> values = new List<Vector4>(cards.Length);
        foreach (HairCard card in cards)
        {
            HairCard.GroomState s = card.GetCanonicalState();
            values.Add(new Vector4(s.uScale, s.vScale, s.uOffset, s.vOffset));
        }
        return values;
    }

    static bool Changed(HairCard[] cards, List<Vector4> before)
    {
        for (int i = 0; i < cards.Length && i < before.Count; i++)
        {
            HairCard.GroomState s = cards[i].GetCanonicalState();
            Vector4 now = new Vector4(s.uScale, s.vScale, s.uOffset, s.vOffset);
            if ((now - before[i]).sqrMagnitude > 0.0000000001f) return true;
        }
        return false;
    }
}
