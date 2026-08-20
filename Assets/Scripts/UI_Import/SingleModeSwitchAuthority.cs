using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Groom workspace header authority.
// Keeps the right-side destination controls together and uses the old left MENU slot
// for product/version branding instead of a navigation button.
[DefaultExecutionOrder(9400)]
public class SingleModeSwitchAuthority : MonoBehaviour
{
    private const float CompactButtonWidth = 136f;
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<SingleModeSwitchAuthority>() != null) return;
        GameObject go = new GameObject("SingleModeSwitchAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<SingleModeSwitchAuthority>();
    }

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + .15f;

        GameObject groomPanel = FindNamed("GroomingPanel");
        GameObject groupPanel = FindNamed("GroupManagerPanel");
        if (groomPanel == null || groupPanel == null || !groomPanel.activeInHierarchy) return;

        Transform row = groomPanel.transform.Find("PanelTabRow");
        if (row == null) return;

        Transform groomTab = row.Find("GroomTabButton");
        Transform textureTab = row.Find("TexTabButton");
        if (textureTab == null) return;

        // RuntimeNavigationProjectIO owns the MENU action. Move that exact button so its
        // existing ReturnToMenu listener comes with it rather than duplicating navigation logic.
        Transform menuButton = row.Find("WorkspaceMenuButton_Runtime");
        if (menuButton == null)
        {
            Transform leftMenu = groupPanel.transform.Find("MenuButton_Runtime");
            if (leftMenu != null && leftMenu.GetComponent<Button>() != null)
            {
                leftMenu.SetParent(row, false);
                leftMenu.name = "WorkspaceMenuButton_Runtime";
                menuButton = leftMenu;
            }
        }

        // Once the real MENU button has moved, leave a branding header with the original
        // runtime name. RuntimeNavigationProjectIO then sees the slot as occupied and does
        // not recreate another MENU button on the left every scan.
        if (menuButton != null)
            EnsureBrandHeader(groupPanel.transform);

        if (groomTab != null) groomTab.gameObject.SetActive(false);

        HorizontalLayoutGroup layout = row.GetComponent<HorizontalLayoutGroup>();
        if (layout != null)
        {
            layout.childControlWidth = false;
            layout.childForceExpandWidth = false;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.spacing = 8f;
        }

        StyleDestinationButton(textureTab, "TEXTURE MODE", new Color(.20f, .50f, .82f, 1f), CompactButtonWidth);

        if (menuButton != null)
        {
            menuButton.gameObject.SetActive(true);
            StyleDestinationButton(menuButton, "MENU", new Color(.24f, .30f, .38f, 1f), CompactButtonWidth);

            // MENU is immediately to the left of TEXTURE MODE.
            int textureIndex = textureTab.GetSiblingIndex();
            if (menuButton.GetSiblingIndex() != textureIndex - 1)
                menuButton.SetSiblingIndex(Mathf.Max(0, textureIndex));
        }
    }

    void EnsureBrandHeader(Transform groupPanel)
    {
        Transform existing = groupPanel.Find("MenuButton_Runtime");
        if (existing != null)
        {
            // A real button here means RuntimeNavigationProjectIO recreated it between scans;
            // leave it for the next pass to move rather than overlaying the branding.
            if (existing.GetComponent<Button>() != null) return;
            StyleBrandHeader(existing);
            existing.SetSiblingIndex(0);
            return;
        }

        GameObject header = new GameObject("MenuButton_Runtime", typeof(RectTransform), typeof(LayoutElement));
        header.transform.SetParent(groupPanel, false);
        RectTransform rect = header.GetComponent<RectTransform>();
        rect.sizeDelta = new Vector2(0f, 44f);
        LayoutElement le = header.GetComponent<LayoutElement>();
        le.minHeight = 44f;
        le.preferredHeight = 44f;
        le.flexibleHeight = 0f;

        // No version number here on purpose: StyleBrandHeader below rewrites this label
        // every time the header is restyled, so the two used to disagree ("ALPHA 1.0"
        // against "ALPHA") - and the build stamp is where the real version lives.
        AddBrandText(header.transform, "BrandTitle", "HAIRBRUSH - BETA", 15f, FontStyles.Bold,
            new Color(.96f, .96f, .96f, 1f), new Vector2(0f, .45f), new Vector2(1f, 1f));
        AddBrandText(header.transform, "BrandSubtitle", "by POLYTRICITY LTD 2026", 10f, FontStyles.Normal,
            new Color(.72f, .76f, .82f, 1f), new Vector2(0f, 0f), new Vector2(1f, .46f));

        header.transform.SetSiblingIndex(0);
    }

    static void AddBrandText(Transform parent, string name, string text, float size, FontStyles style,
        Color color, Vector2 anchorMin, Vector2 anchorMax)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        RectTransform rect = go.GetComponent<RectTransform>();
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        TextMeshProUGUI label = go.GetComponent<TextMeshProUGUI>();
        label.text = text;
        label.fontSize = size;
        label.fontStyle = style;
        label.alignment = TextAlignmentOptions.Center;
        label.color = color;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.overflowMode = TextOverflowModes.Overflow;
        label.raycastTarget = false;
    }

    static void StyleBrandHeader(Transform header)
    {
        TextMeshProUGUI title = header.Find("BrandTitle")?.GetComponent<TextMeshProUGUI>();
        if (title != null) title.text = "HAIRBRUSH - BETA";
        TextMeshProUGUI subtitle = header.Find("BrandSubtitle")?.GetComponent<TextMeshProUGUI>();
        if (subtitle != null) subtitle.text = "by POLYTRICITY LTD 2026";
    }

    static void StyleDestinationButton(Transform buttonTransform, string text, Color color, float width)
    {
        if (buttonTransform == null) return;

        LayoutElement le = buttonTransform.GetComponent<LayoutElement>();
        if (le == null) le = buttonTransform.gameObject.AddComponent<LayoutElement>();
        le.preferredWidth = width;
        le.minWidth = width;
        le.flexibleWidth = 0f;

        RectTransform rect = buttonTransform as RectTransform;
        if (rect != null) rect.sizeDelta = new Vector2(width, rect.sizeDelta.y);

        Image image = buttonTransform.GetComponent<Image>();
        if (image != null) image.color = color;

        Button button = buttonTransform.GetComponent<Button>();
        if (button != null) button.interactable = true;

        TextMeshProUGUI label = buttonTransform.GetComponentInChildren<TextMeshProUGUI>(true);
        if (label != null)
        {
            label.text = text;
            label.fontStyle = FontStyles.Bold;
            label.alignment = TextAlignmentOptions.Center;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.enableAutoSizing = true;
            label.fontSizeMin = 10f;
            label.fontSizeMax = 16f;
        }
    }

    static GameObject FindNamed(string name)
    {
        foreach (Transform t in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (t != null && t.name == name) return t.gameObject;
        return null;
    }
}