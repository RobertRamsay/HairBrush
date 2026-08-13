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
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + 0.1f;
        Resolve();
        Bind();
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
        if (row == null) return;

        Button found = row.Find("GroupUVRandomSeedButton")?.GetComponent<Button>();
        if (found == null || found == button) return;

        button = found;
        seedInput = row.Find("SEEDInput")?.GetComponent<TMP_InputField>();
        button.onClick.RemoveAllListeners();
        button.onClick.AddListener(Reshuffle);

        CompactRow(row);
    }

    void CompactRow(Transform row)
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

        if (seedInput != null)
            seedInput.GetComponent<RectTransform>().sizeDelta = new Vector2(70f, 30f);
        if (button != null)
            button.GetComponent<RectTransform>().sizeDelta = new Vector2(34f, 30f);
    }

    void Reshuffle()
    {
        if (controller == null || viewer == null || setSeedMethod == null) return;

        int oldSeed = 0;
        if (seedInput != null) int.TryParse(seedInput.text, out oldSeed);

        HairCard[] cards = FindObjectsByType<HairCard>(FindObjectsSortMode.None)
            .Where(c => c != null && c.groupId == viewer.currentGroupId)
            .ToArray();
        List<Vector4> before = CaptureUVs(cards);

        int chosen = oldSeed;
        for (int attempt = 0; attempt < 32; attempt++)
        {
            chosen = UnityEngine.Random.Range(0, 1000000);
            if (chosen == oldSeed) continue;
            setSeedMethod.Invoke(controller, new object[] { viewer.currentGroupId, chosen.ToString() });
            if (cards.Length == 0 || UVsChanged(cards, before)) break;
        }

        if (chosen == oldSeed)
        {
            chosen = oldSeed == int.MaxValue ? 0 : oldSeed + 1;
            setSeedMethod.Invoke(controller, new object[] { viewer.currentGroupId, chosen.ToString() });
        }

        if (seedInput != null) seedInput.SetTextWithoutNotify(chosen.ToString());
    }

    static List<Vector4> CaptureUVs(HairCard[] cards)
    {
        List<Vector4> values = new List<Vector4>(cards.Length);
        foreach (HairCard card in cards)
        {
            HairCard.GroomState s = card.GetCanonicalState();
            values.Add(new Vector4(s.uScale, s.vScale, s.uOffset, s.vOffset));
        }
        return values;
    }

    static bool UVsChanged(HairCard[] cards, List<Vector4> before)
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
