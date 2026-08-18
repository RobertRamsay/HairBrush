using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

[DefaultExecutionOrder(6750)]
public class GroupUVSeedButtonFix : MonoBehaviour
{
    private GroupPredeterminedUVController controller;
    private ModelViewer viewer;
    private MethodInfo getSettingsMethod;
    private MethodInfo clearAppliedMethod;
    private MethodInfo forceApplyMethod;
    private Button button;
    private TMP_InputField seedInput;
    private UVSeedPointerRelay relay;
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
        StyleAllRandomButtons();
    }

    void Resolve()
    {
        if (controller == null)
        {
            controller = FindFirstObjectByType<GroupPredeterminedUVController>();
            if (controller != null)
            {
                BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                Type type = typeof(GroupPredeterminedUVController);
                getSettingsMethod = type.GetMethod("GetSettings", flags);
                clearAppliedMethod = type.GetMethod("ClearAppliedForGroup", flags);
                forceApplyMethod = type.GetMethod("ForceApplyGroup", flags);
            }
        }
        if (viewer == null) viewer = FindFirstObjectByType<ModelViewer>();
    }

    void Bind()
    {
        if (viewer == null || viewer.groomingSliderPanelGO == null || controller == null || getSettingsMethod == null) return;
        Transform row = viewer.groomingSliderPanelGO.transform.Find("GroupUVPredetermined_Row");
        if (row == null)
        {
            button = null;
            seedInput = null;
            relay = null;
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

        if (button != null)
        {
            relay = button.GetComponent<UVSeedPointerRelay>();
            if (relay == null) relay = button.gameObject.AddComponent<UVSeedPointerRelay>();
            relay.onPress = Reshuffle;
            button.onClick.RemoveAllListeners();
        }

        Compact(row);
        StyleRandomButton(button);
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
            SetSeedDirect(parsed);
        }
        else if (!focused)
        {
            editText = seedInput.text;
        }

        wasFocused = focused;
    }

    void OnSeedChanged(string value)
    {
        if (seedInput == null || !seedInput.isFocused) return;
        editText = value;
        if (int.TryParse(value, out int parsed))
            SetSeedDirect(parsed);
    }

    void SetSeedDirect(int seed)
    {
        if (controller == null || viewer == null || getSettingsMethod == null) return;

        object settings = getSettingsMethod.Invoke(controller, new object[] { viewer.currentGroupId });
        if (settings == null) return;

        FieldInfo seedField = settings.GetType().GetField("seed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (seedField == null) return;

        seedField.SetValue(settings, seed);
        clearAppliedMethod?.Invoke(controller, new object[] { viewer.currentGroupId });
        forceApplyMethod?.Invoke(controller, new object[] { viewer.currentGroupId });
    }

    void Compact(Transform row)
    {
        // Fixed sizeDelta values are deliberately NOT set here any more.
        // GroupUVRangeSliderUIAuthority lays this row out with stretch anchors (Place), and
        // sizeDelta on a stretch-anchored rect is ADDITIVE to the anchor span - the old
        // 46x30 on the button is exactly what made it render as an oversized box hanging
        // out of the row. The proportional layout is the single owner of sizing now.
        Transform range = row.Find("UVRectRangeSlider");
        if (range != null)
        {
            LayoutElement le = range.GetComponent<LayoutElement>();
            if (le != null)
            {
                le.preferredWidth = 160f;
                le.minWidth = 130f;
            }
        }
    }

    void StyleAllRandomButtons()
    {
        foreach (Button candidate in FindObjectsByType<Button>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (!IsRandomButton(candidate)) continue;
            StyleRandomButton(candidate);
        }
    }

    static bool IsRandomButton(Button candidate)
    {
        if (candidate == null) return false;
        // Never touch the variance rows' self-styled reroll buttons.
        if (candidate.gameObject.name == "RANDOMIZEButton") return false;
        if (candidate.gameObject.name == "RButton" || candidate.gameObject.name == "GroupUVRandomSeedButton") return true;
        TextMeshProUGUI label = candidate.GetComponentInChildren<TextMeshProUGUI>(true);
        if (label == null) return false;
        string trimmed = label.text.Trim();
        return trimmed == "R" || trimmed == "RANDOMIZE";
    }

    static void StyleRandomButton(Button candidate)
    {
        if (candidate == null) return;

        // No sizeDelta / LayoutElement overrides here any more - the row's proportional layout
        // (GroupUVRangeSliderUIAuthority.Place) owns sizing, and sizeDelta on stretch anchors is
        // additive, which is what previously blew the button out past the row bounds.
        Image image = candidate.GetComponent<Image>();
        if (image == null) image = candidate.gameObject.AddComponent<Image>();
        image.raycastTarget = true;
        candidate.targetGraphic = image;

        // Same bright-teal sliced-sprite treatment as the variance RANDOMIZE button.
        if (UITheme.ButtonNormalSprite != null)
        {
            image.sprite = UITheme.ButtonNormalSprite;
            image.type = Image.Type.Sliced;
            image.color = new Color(.62f, 1f, .96f, 1f);
            candidate.transition = Selectable.Transition.SpriteSwap;
            SpriteState state = candidate.spriteState;
            state.highlightedSprite = UITheme.ButtonHoverSprite;
            state.pressedSprite = UITheme.ButtonClickSprite;
            candidate.spriteState = state;
        }
        else
        {
            candidate.transition = Selectable.Transition.ColorTint;
            ColorBlock colors = candidate.colors;
            colors.normalColor = new Color(.25f, .42f, .58f, 1f);
            colors.highlightedColor = new Color(.32f, .58f, .78f, 1f);
            colors.selectedColor = new Color(.30f, .52f, .70f, 1f);
            colors.pressedColor = new Color(.16f, .36f, .56f, 1f);
            colors.disabledColor = new Color(.16f, .20f, .24f, .65f);
            colors.colorMultiplier = 1f;
            colors.fadeDuration = .06f;
            candidate.colors = colors;
            image.color = colors.normalColor;
        }

        TextMeshProUGUI label = candidate.GetComponentInChildren<TextMeshProUGUI>(true);
        if (label != null)
        {
            label.text = "RANDOMIZE";
            label.fontStyle = FontStyles.Bold;
            label.fontSize = 11f;
            label.alignment = TextAlignmentOptions.Center;
            label.raycastTarget = false;
        }
    }

    void Reshuffle()
    {
        if (controller == null || viewer == null || getSettingsMethod == null) return;
        if (button != null && !button.interactable) return;

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
            SetSeedDirect(candidate);
            chosen = candidate;
            if (cards.Length == 0 || Changed(cards, before)) break;
        }

        if (chosen == oldSeed)
        {
            chosen = oldSeed == int.MaxValue ? 0 : oldSeed + 1;
            SetSeedDirect(chosen);
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
            if ((now - before[i]).sqrMagnitude > .0000000001f) return true;
        }
        return false;
    }
}

public class UVSeedPointerRelay : MonoBehaviour, IPointerDownHandler
{
    public Action onPress;

    public void OnPointerDown(PointerEventData eventData)
    {
        if (eventData.button != PointerEventData.InputButton.Left) return;
        onPress?.Invoke();
        eventData.Use();
    }
}
