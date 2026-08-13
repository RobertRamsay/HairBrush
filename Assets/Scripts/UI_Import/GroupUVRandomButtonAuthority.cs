using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

[DefaultExecutionOrder(14000)]
public class GroupUVRandomButtonAuthority : MonoBehaviour
{
    private ModelViewer viewer;
    private GroupPredeterminedUVController controller;
    private MethodInfo randomizeMethod;
    private Button boundButton;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<GroupUVRandomButtonAuthority>() != null) return;
        GameObject go = new GameObject("GroupUVRandomButtonAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<GroupUVRandomButtonAuthority>();
    }

    void LateUpdate()
    {
        if (viewer == null) viewer = FindFirstObjectByType<ModelViewer>();
        if (controller == null)
        {
            controller = FindFirstObjectByType<GroupPredeterminedUVController>();
            if (controller != null)
                randomizeMethod = typeof(GroupPredeterminedUVController).GetMethod("RandomizeSeed", BindingFlags.Instance | BindingFlags.NonPublic);
        }
        if (viewer == null || viewer.groomingSliderPanelGO == null) return;

        // Make every random-seed control use the same obvious blue button treatment.
        StyleAllRandomButtons(viewer.groomingSliderPanelGO.transform);

        if (controller == null || randomizeMethod == null) return;

        Transform row = viewer.groomingSliderPanelGO.transform.Find("GroupUVPredetermined_Row");
        if (row == null) return;

        TidyRow(row);

        Button button = row.Find("GroupUVRandomSeedButton")?.GetComponent<Button>();
        if (button == null) return;
        StyleButton(button);

        if (boundButton == button) return;
        boundButton = button;
        boundButton.onClick.RemoveAllListeners();
        boundButton.onClick.AddListener(() => randomizeMethod.Invoke(controller, new object[] { viewer.currentGroupId }));
    }

    static void TidyRow(Transform row)
    {
        HorizontalLayoutGroup layout = row.GetComponent<HorizontalLayoutGroup>();
        if (layout != null)
        {
            layout.spacing = 6f;
            layout.padding = new RectOffset(0, 0, 3, 3);
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
        }

        TextMeshProUGUI seedLabel = null;
        foreach (TextMeshProUGUI text in row.GetComponentsInChildren<TextMeshProUGUI>(true))
        {
            string value = text.text != null ? text.text.Trim() : string.Empty;
            if (value == "UV RECTS") SetLayout(text.gameObject, 76f, 30f);
            else if (value == "SEED")
            {
                seedLabel = text;
                SetLayout(text.gameObject, 42f, 30f);
            }
        }

        Transform range = row.Find("UVRectRangeSlider");
        if (range != null) SetLayout(range.gameObject, 190f, 30f);

        Transform seed = row.Find("SEEDInput");
        if (seed != null) SetLayout(seed.gameObject, 68f, 30f);

        Transform random = row.Find("GroupUVRandomSeedButton");
        if (random != null) SetLayout(random.gameObject, 46f, 30f);

        // Shift only the SEED [value] R cluster to the right by about 20 px.
        // A dedicated layout spacer keeps the range slider itself in place.
        if (seedLabel != null && seedLabel.transform.parent == row)
        {
            Transform spacer = row.Find("UVSeedSpacer");
            if (spacer == null)
            {
                GameObject spacerGO = new GameObject("UVSeedSpacer", typeof(RectTransform), typeof(LayoutElement));
                spacerGO.transform.SetParent(row, false);
                spacer = spacerGO.transform;
            }
            SetLayout(spacer.gameObject, 20f, 30f);

            int spacerIndex = spacer.GetSiblingIndex();
            int labelIndex = seedLabel.transform.GetSiblingIndex();
            if (spacerIndex + 1 != labelIndex)
            {
                int target = spacerIndex < labelIndex ? labelIndex - 1 : labelIndex;
                spacer.SetSiblingIndex(Mathf.Max(0, target));
            }
        }
    }

    static void StyleAllRandomButtons(Transform root)
    {
        if (root == null) return;
        foreach (Button button in root.GetComponentsInChildren<Button>(true))
        {
            if (!IsRandomButton(button)) continue;
            SetLayout(button.gameObject, 46f, 30f);
            StyleButton(button);
        }
    }

    static bool IsRandomButton(Button button)
    {
        if (button == null) return false;
        if (button.gameObject.name == "RButton" || button.gameObject.name == "GroupUVRandomSeedButton") return true;
        TextMeshProUGUI label = button.GetComponentInChildren<TextMeshProUGUI>(true);
        return label != null && label.text != null && label.text.Trim() == "R";
    }

    static void SetLayout(GameObject go, float width, float height)
    {
        LayoutElement le = go.GetComponent<LayoutElement>();
        if (le == null) le = go.AddComponent<LayoutElement>();
        le.minWidth = width;
        le.preferredWidth = width;
        le.minHeight = height;
        le.preferredHeight = height;

        RectTransform rect = go.transform as RectTransform;
        if (rect != null) rect.sizeDelta = new Vector2(width, height);
    }

    static void StyleButton(Button button)
    {
        Image image = button.GetComponent<Image>();
        if (image == null) image = button.gameObject.AddComponent<Image>();
        image.raycastTarget = true;
        button.targetGraphic = image;
        button.transition = Selectable.Transition.ColorTint;

        ColorBlock colors = button.colors;
        colors.normalColor = new Color(.25f, .42f, .58f, 1f);
        colors.highlightedColor = new Color(.32f, .58f, .78f, 1f);
        colors.selectedColor = new Color(.30f, .52f, .70f, 1f);
        colors.pressedColor = new Color(.16f, .36f, .56f, 1f);
        colors.disabledColor = new Color(.16f, .20f, .24f, .65f);
        colors.colorMultiplier = 1f;
        colors.fadeDuration = .06f;
        button.colors = colors;
        image.color = button.interactable ? colors.normalColor : colors.disabledColor;

        TextMeshProUGUI label = button.GetComponentInChildren<TextMeshProUGUI>(true);
        if (label != null)
        {
            label.text = "R";
            label.fontSize = Mathf.Max(label.fontSize, 14f);
            label.fontStyle = FontStyles.Bold;
            label.alignment = TextAlignmentOptions.Center;
            label.raycastTarget = false;
        }
    }
}
