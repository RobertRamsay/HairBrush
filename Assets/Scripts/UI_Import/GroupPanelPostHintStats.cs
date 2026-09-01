using System;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Left group-panel presentation authority. Keeps POST/CLUMPER row structure untouched while
// giving each Hair Group a compact, readable identity block:
//
//   GROUP 0              (unnamed)
//   6 cards
//   1,944 polys
//
// or, after the existing double-click rename gesture:
//
//   G0_Spike
//   6 cards
//   1,944 polys
//
// ModelViewer's private groupNames dictionary stores only the friendly suffix ("Spike"). The
// numeric group id remains the real identity used by HairCards, POST, CLUMPER, UVs and saving.
[DefaultExecutionOrder(9000)]
public class GroupPanelPostHintStats : MonoBehaviour
{
    private const float HeaderHeight = 66f;
    private const float HeaderControlHeight = 54f;
    private const float UtilityButtonHeight = 24f;
    private const float UtilityButtonGap = 5f;
    private const float UtilityRightInset = 8f;
    private const float UtilityBottomInset = 6f;
    private const float UVButtonWidth = 74f;
    private const float SoloButtonWidth = 52f;
    private const float SidednessButtonWidth = 40f;
    private const float NormalFlipButtonWidth = 36f;

    // DEL, in the row's TOP-right corner rather than in the bottom-right utility strip with
    // SOLO/UV/SS/N.
    //
    // Not for want of room - those four take 225px of a 330px row, so a fifth at 56 plus a gap
    // would fit with space to spare. It is that a delete sitting in the middle of a run of
    // harmless toggles is the one you hit by accident: SOLO, UV, SS and N± are all one click to
    // set and one click to put back, and this one is not. The top lane is otherwise just the group
    // name, which is left aligned, so there is a corner going spare that nothing reversible wants.
    private const float DeleteButtonWidth = 56f;
    private const float DeleteButtonHeight = 20f;
    private const float DeleteTopInset = 3f;

    // What the name has to give up so the two never overlap: the button, its right inset, and a
    // gap. The name ellipsises rather than wrapping, so this shortens a long name instead of
    // pushing it under the button.
    //
    // The inline RENAME field shortens with it, and that is wanted rather than incidental:
    // GroupNameInlineEditAuthority builds its field by copying this label's rect, so without the
    // reserve the caret and the text being typed would run under a live delete button.
    private const float NameRightReserve = UtilityRightInset + DeleteButtonWidth + 6f;
    // The names the old in-panel instructions block used. Kept only so Bind can sweep the two
    // objects away when a panel is rebuilt from an older layout - nothing builds them now.
    private const string InstructionsHeaderName = "InstructionsHeader";

    private int handledRebuildFrame = -1;
    private GameObject boundPanel;
    private readonly List<Transform> ordered = new List<Transform>();
    private ModelViewer viewer;
    private FieldInfo groupNamesField;
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<GroupPanelPostHintStats>() != null) return;
        GameObject go = new GameObject("GroupPanelPostHintStats");
        DontDestroyOnLoad(go);
        go.AddComponent<GroupPanelPostHintStats>();
    }

    void LateUpdate()
    {
        bool rebuilt = RuntimeUIRebuildSignal.TryConsume(ref handledRebuildFrame);
        if (!rebuilt && Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + .10f;

        ResolveViewer();

        GameObject panel = GameObject.Find("GroupManagerPanel");
        if (panel == null)
        {
            boundPanel = null;
            return;
        }

        if (boundPanel != panel)
            Bind(panel);

        MaintainPanelOrder(panel.transform);
        UpdateGroupHeaders();
    }

    void ResolveViewer()
    {
        if (viewer != null && groupNamesField != null) return;
        viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer != null)
            groupNamesField = typeof(ModelViewer).GetField("groupNames", BindingFlags.Instance | BindingFlags.NonPublic);
    }

    // The instructions block used to be BUILT here. It now only gets swept away: the control
    // reference lives behind the INPUT KEYS button (InputKeysDialog), which has room to be
    // complete and correct, and this panel gets its 152px back.
    //
    // The sweep is not only for tidiness. A project saved by an older build, and any panel
    // rebuilt from one, can still carry both objects; left in place they would sit in the
    // running order below with nothing maintaining them, showing a list that is now two lines
    // wrong and missing a dozen others.
    void Bind(GameObject panel)
    {
        boundPanel = panel;
        RemoveLegacyChild(panel.transform, "PostCreateHint");
        RemoveLegacyChild(panel.transform, InstructionsHeaderName);
        MaintainPanelOrder(panel.transform);
    }

    void RemoveLegacyChild(Transform panel, string childName)
    {
        Transform existing = panel.Find(childName);
        if (existing == null) return;

        // Deactivated and renamed before the Destroy, not merely destroyed. Destroy is deferred
        // to the end of the frame, so a panel scan later in this same frame would otherwise
        // still find it by name and put it back in the running order.
        existing.gameObject.SetActive(false);
        existing.gameObject.name = childName + "_Discarded";
        Destroy(existing.gameObject);
    }

    // Owns the whole left-panel running order, because the pieces are built by five different
    // scripts at five different moments and each one only ever knew where to put itself
    // relative to whatever already existed.
    //
    //   MENU  ->  POLYGONS  ->  LIGHT ANGLE  ->  INPUT KEYS  ->  SYMMETRY  ->  MAYA-NAV
    //         ->  GUIDES  ->  CARD  ->  TOPOLOGY  ->  Hair Groups  ->  + GROUP  ->  the group list
    void MaintainPanelOrder(Transform panel)
    {
        if (panel == null) return;

        ordered.Clear();
        AddIfPresent(panel, "HairPolygonCounterText");
        AddIfPresent(panel, SceneLightAngleAuthority.RowName);
        AddIfPresent(panel, InputKeysDialog.ButtonName);
        // The SYMMETRY toggle sits directly under INPUT KEYS. Anything NOT listed here
        // gets shoved around every scan as the listed children are reindexed past it, so a new
        // panel child has to be named here or its position will not hold.
        //
        // The exception is a child that is not in the flow at all. ManualLinkFooterAuthority's
        // strip is anchored to the panel's bottom edge with ignoreLayout, and wants to be the
        // LAST sibling so it draws over the group list. Listing it here would pull it into this
        // front block and put it underneath. It is deliberately absent.
        AddIfPresent(panel, GroomSymmetryAuthority.ButtonName);
        AddIfPresent(panel, MayaNavigationAuthority.ButtonName);
        AddIfPresent(panel, GuideOverlayAuthority.ButtonName);
        AddIfPresent(panel, HairCardProfileAuthority.ButtonName);
        AddIfPresent(panel, HairCardTopologyAuthority.ButtonName);
        AddIfPresent(panel, "TitleText");
        AddIfPresent(panel, "NewGroupButton");
        AddIfPresent(panel, "GroupScrollView");

        // The MENU button pins itself to index 0, so start immediately after it.
        int index = 0;
        Transform menu = panel.Find("MenuButton_Runtime");
        if (menu == null) menu = panel.Find("WorkspaceMenuButton_Runtime");
        if (menu != null) index = menu.GetSiblingIndex() + 1;

        foreach (Transform child in ordered)
        {
            if (child.GetSiblingIndex() != index) child.SetSiblingIndex(index);
            index++;
        }
    }

    void AddIfPresent(Transform panel, string childName)
    {
        Transform child = panel.Find(childName);
        if (child == null) return;
        ordered.Add(child);
    }

    void UpdateGroupHeaders()
    {
        Dictionary<int, int> cardsByGroup = new();
        Dictionary<int, long> polysByGroup = new();

        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
        {
            if (card == null) continue;
            int gid = card.groupId;
            cardsByGroup[gid] = cardsByGroup.TryGetValue(gid, out int count) ? count + 1 : 1;

            long polys = 0;
            MeshFilter filter = card.GetComponent<MeshFilter>();
            Mesh mesh = filter != null ? filter.sharedMesh : null;
            if (mesh != null)
                for (int sub = 0; sub < mesh.subMeshCount; sub++)
                    polys += (long)mesh.GetIndexCount(sub) / 3L;

            polysByGroup[gid] = (polysByGroup.TryGetValue(gid, out long existing) ? existing : 0L) + polys;
        }

        Dictionary<int, string> groupNames = GetGroupNames();

        foreach (RectTransform item in FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (item == null || !item.name.StartsWith("GroupItem_", StringComparison.Ordinal)) continue;
            if (!int.TryParse(item.name.Substring("GroupItem_".Length), out int gid)) continue;

            Transform labelButton = item.Find("LabelButton");
            if (labelButton == null) continue;

            Transform nameLabel = labelButton.Find("Label");
            Transform countLabel = labelButton.Find("CardCountLabel");
            TextMeshProUGUI nameText = nameLabel != null ? nameLabel.GetComponent<TextMeshProUGUI>() : null;
            TextMeshProUGUI statsText = countLabel != null ? countLabel.GetComponent<TextMeshProUGUI>() : null;
            if (nameText == null || statsText == null) continue;

            string friendly = string.Empty;
            if (groupNames != null && groupNames.TryGetValue(gid, out string stored))
            {
                friendly = NormalizeFriendlyName(gid, stored);
                if (!string.Equals(stored ?? string.Empty, friendly, StringComparison.Ordinal))
                    groupNames[gid] = friendly;
            }

            int cardCount = cardsByGroup.TryGetValue(gid, out int c) ? c : 0;
            long polyCount = polysByGroup.TryGetValue(gid, out long p) ? p : 0L;
            string cardWord = cardCount == 1 ? "card" : "cards";
            string polyWord = polyCount == 1 ? "poly" : "polys";

            // The row shows the name on its own - the numeric id is still the real
            // identity behind the scenes, it just does not need to be spelled out
            // on a group the user has explicitly named.
            if (string.IsNullOrWhiteSpace(friendly))
            {
                nameText.text = "GROUP " + gid;
            }
            else
            {
                nameText.text = friendly;
            }
            statsText.text = cardCount.ToString("N0") + " " + cardWord + "\n" +
                             polyCount.ToString("N0") + " " + polyWord;

            ApplyHeaderLayout(item, labelButton, nameText, statsText);
        }
    }

    Dictionary<int, string> GetGroupNames()
    {
        ResolveViewer();
        return viewer != null && groupNamesField != null
            ? groupNamesField.GetValue(viewer) as Dictionary<int, string>
            : null;
    }

    // The name the row actually shows, from whatever is in the store. Public so nothing else has
    // to guess at it: ModelViewer.GroupDisplayName asks this rather than reading groupNames, which
    // is NOT the displayed name and is routinely the empty string.
    public static string DisplayName(int gid, string stored)
    {
        string friendly = NormalizeFriendlyName(gid, stored);
        if (string.IsNullOrWhiteSpace(friendly)) return "GROUP " + gid;
        return friendly;
    }

    static string NormalizeFriendlyName(int gid, string stored)
    {
        string value = (stored ?? string.Empty).Trim();
        if (value.Length == 0) return string.Empty;

        string legacy = "Group " + gid;
        if (string.Equals(value, legacy, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, legacy + " (Default)", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "Default", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "(Default)", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        string renderedPrefix = "G" + gid + "_";
        if (value.StartsWith(renderedPrefix, StringComparison.OrdinalIgnoreCase))
            value = value.Substring(renderedPrefix.Length).Trim();

        return value;
    }

    static void ApplyHeaderLayout(
        RectTransform item,
        Transform labelButton,
        TextMeshProUGUI nameText,
        TextMeshProUGUI statsText)
    {
        item.sizeDelta = new Vector2(item.sizeDelta.x, HeaderHeight);

        // This header needs two vertical lanes: the name gets the full top width while
        // UV/SOLO occupy only the lower-right half. The original HorizontalLayoutGroup
        // permanently reserved their width beside the name, so disable it and position the
        // three header controls explicitly inside the existing GroupItem rect.
        HorizontalLayoutGroup row = item.GetComponent<HorizontalLayoutGroup>();
        if (row != null) row.enabled = false;

        RectTransform labelRect = labelButton as RectTransform;
        if (labelRect != null)
        {
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.pivot = new Vector2(.5f, .5f);
            labelRect.offsetMin = new Vector2(8f, 6f);
            labelRect.offsetMax = new Vector2(-8f, -6f);
        }

        // LabelButton historically relied on its child TMP labels to provide the raycast
        // surface. The tidy header deliberately makes those labels non-raycastable, so give
        // the button its own invisible hit graphic. This restores both normal selection and
        // ModelViewer's existing double-click-to-rename gesture without putting the text back
        // in the event path.
        Image labelHit = labelButton.GetComponent<Image>();
        if (labelHit == null) labelHit = labelButton.gameObject.AddComponent<Image>();
        labelHit.color = new Color(0f, 0f, 0f, 0f);
        labelHit.raycastTarget = true;

        Button labelControl = labelButton.GetComponent<Button>();
        if (labelControl != null) labelControl.targetGraphic = labelHit;

        // The coloured group strip itself should select too. Child controls (UV/SOLO) still
        // receive their own pointer clicks first, while otherwise-empty header space forwards
        // to the same LabelButton callback used by clicking the name.
        Image itemHit = item.GetComponent<Image>();
        if (itemHit != null) itemHit.raycastTarget = true;
        GroupHeaderBackgroundClickProxy proxy = item.GetComponent<GroupHeaderBackgroundClickProxy>();
        if (proxy == null) proxy = item.gameObject.AddComponent<GroupHeaderBackgroundClickProxy>();
        proxy.labelButton = labelControl;

        RectTransform titleRect = nameText.rectTransform;
        titleRect.anchorMin = new Vector2(0f, 1f);
        titleRect.anchorMax = new Vector2(1f, 1f);
        titleRect.pivot = new Vector2(.5f, 1f);
        titleRect.offsetMin = new Vector2(2f, -23f);
        titleRect.offsetMax = new Vector2(-NameRightReserve, -2f);
        nameText.fontSize = 14f;
        nameText.fontStyle = FontStyles.Bold;
        nameText.alignment = TextAlignmentOptions.TopLeft;
        nameText.color = Color.white;
        nameText.textWrappingMode = TextWrappingModes.NoWrap;
        nameText.overflowMode = TextOverflowModes.Ellipsis;
        nameText.raycastTarget = false;

        RectTransform statsRect = statsText.rectTransform;
        statsRect.anchorMin = new Vector2(0f, 0f);
        statsRect.anchorMax = new Vector2(1f, 0f);
        statsRect.pivot = new Vector2(.5f, 0f);
        statsRect.offsetMin = new Vector2(2f, 2f);
        statsRect.offsetMax = new Vector2(-2f, 33f);
        statsText.fontSize = 10.5f;
        statsText.fontStyle = FontStyles.Normal;
        statsText.alignment = TextAlignmentOptions.BottomLeft;
        statsText.color = new Color(.82f, .82f, .82f, .95f);
        statsText.textWrappingMode = TextWrappingModes.NoWrap;
        statsText.overflowMode = TextOverflowModes.Ellipsis;
        statsText.lineSpacing = -8f;
        statsText.raycastTarget = false;

        Transform solo = item.Find("SoloButton");
        if (solo is RectTransform soloRect)
        {
            soloRect.anchorMin = new Vector2(1f, 0f);
            soloRect.anchorMax = new Vector2(1f, 0f);
            soloRect.pivot = new Vector2(1f, 0f);
            soloRect.anchoredPosition = new Vector2(-UtilityRightInset, UtilityBottomInset);
            soloRect.sizeDelta = new Vector2(SoloButtonWidth, UtilityButtonHeight);
        }

        Transform uv = item.Find("GroupUVModeButton");
        if (uv is RectTransform uvRect)
        {
            uvRect.anchorMin = new Vector2(1f, 0f);
            uvRect.anchorMax = new Vector2(1f, 0f);
            uvRect.pivot = new Vector2(1f, 0f);
            uvRect.anchoredPosition = new Vector2(-(UtilityRightInset + SoloButtonWidth + UtilityButtonGap), UtilityBottomInset);
            uvRect.sizeDelta = new Vector2(UVButtonWidth, UtilityButtonHeight);
        }

        // Rendering sidedness, immediately left of UV. Row reads Name | SS/DS | UV | SOLO.
        Transform sidedness = item.Find(GroupSidednessAuthority.ButtonName);
        if (sidedness is RectTransform sidednessRect)
        {
            sidednessRect.anchorMin = new Vector2(1f, 0f);
            sidednessRect.anchorMax = new Vector2(1f, 0f);
            sidednessRect.pivot = new Vector2(1f, 0f);
            sidednessRect.anchoredPosition = new Vector2(
                -(UtilityRightInset + SoloButtonWidth + UtilityButtonGap + UVButtonWidth + UtilityButtonGap),
                UtilityBottomInset);
            sidednessRect.sizeDelta = new Vector2(SidednessButtonWidth, UtilityButtonHeight);
        }

        // Normal / form flip, immediately left of SS/DS.
        // Row reads Name | N+/N- | SS/DS | UV | SOLO.
        Transform normalFlip = item.Find(GroupNormalFlipAuthority.ButtonName);
        if (normalFlip is RectTransform normalFlipRect)
        {
            normalFlipRect.anchorMin = new Vector2(1f, 0f);
            normalFlipRect.anchorMax = new Vector2(1f, 0f);
            normalFlipRect.pivot = new Vector2(1f, 0f);
            normalFlipRect.anchoredPosition = new Vector2(
                -(UtilityRightInset + SoloButtonWidth + UtilityButtonGap + UVButtonWidth + UtilityButtonGap
                  + SidednessButtonWidth + UtilityButtonGap),
                UtilityBottomInset);
            normalFlipRect.sizeDelta = new Vector2(NormalFlipButtonWidth, UtilityButtonHeight);
        }

        // DEL, top right. This method owns the geometry of every child of a group row - the
        // HorizontalLayoutGroup is switched off at the top of it - so a child WITHOUT a case here
        // is not merely mispositioned, it keeps whatever RectTransform it was constructed with and
        // lands wherever that happens to put it. ModelViewer builds this button; this is what
        // places it.
        Transform del = item.Find("DeleteButton");
        if (del is RectTransform delRect)
        {
            delRect.anchorMin = new Vector2(1f, 1f);
            delRect.anchorMax = new Vector2(1f, 1f);
            delRect.pivot = new Vector2(1f, 1f);
            delRect.anchoredPosition = new Vector2(-UtilityRightInset, -DeleteTopInset);
            delRect.sizeDelta = new Vector2(DeleteButtonWidth, DeleteButtonHeight);
        }
    }
}

// The group row already has the green/grey Image that visually reads as the tab, but the
// original interaction only lived on the narrower LabelButton. Forward clicks on otherwise
// empty row space into that existing callback. UV and SOLO remain independent child Buttons.
public class GroupHeaderBackgroundClickProxy : MonoBehaviour, IPointerClickHandler
{
    [NonSerialized] public Button labelButton;

    public void OnPointerClick(PointerEventData eventData)
    {
        if (eventData == null || eventData.button != PointerEventData.InputButton.Left) return;

        // Consistent with CustomClickDetector on the same row: an ALT+LMB tumble begun over empty
        // header space should not switch the selected group and close the modifier panel the user
        // was working in. No data loss here, but the same gesture should not do two different
        // things depending on which pixel of a row it started on.
        if (MayaNavigationAuthority.CameraGestureActive) return;

        if (labelButton == null) return;
        labelButton.onClick.Invoke();
        eventData.Use();
    }
}
