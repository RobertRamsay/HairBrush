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
        if (viewer == null || controller == null || randomizeMethod == null || viewer.groomingSliderPanelGO == null) return;

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

        foreach (TextMeshProUGUI text in row.GetComponentsInChildren<TextMeshProUGUI>(true))
        {
            string value = text.text != null ? text.text.Trim() : string.Empty;
            if (value == "UV RECTS") SetLayout(text.gameObject, 76f, 30f);
            else if (value == "SEED") SetLayout(text.gameObject, 42f, 30f);
        }

        Transform range = row.Find("UVRectRangeSlider");
        if (range != null) SetLayout(range.gameObject, 190f, 30f);

        Transform seed = row.Find("SEEDInput");
        if (seed != null) SetLayout(seed.gameObject, 68f, 30f);

        Transform random = row.Find("GroupUVRandomSeedButton");
        if (random != null) SetLayout(random.gameObject, 46f, 30f);
    }

    static void SetLayout(GameObject go, float width, float height)
    {
        LayoutElement le = go.GetComponent<LayoutElement>();
        if (le == null) le = go.AddComponent<LayoutElement>();
        le.minWidth = width;
        le.preferredWidth = width;
        le.minHeight = height;
        le.preferredHeight = height;
    }

    static void StyleButton(Button button)
    {
        Image image = button.GetComponent<Image>();
        if (image == null) image = button.gameObject.AddComponent<Image>();
        image.raycastTarget = true;
        image.color = button.interactable ? new Color(.25f, .42f, .58f, 1f) : new Color(.16f, .20f, .24f, .65f);
        button.targetGraphic = image;

        TextMeshProUGUI label = button.GetComponentInChildren<TextMeshProUGUI>(true);
        if (label != null)
        {
            label.text = "R";
            label.fontSize = 14f;
            label.fontStyle = FontStyles.Bold;
            label.alignment = TextAlignmentOptions.Center;
            label.raycastTarget = false;
        }
    }
}
