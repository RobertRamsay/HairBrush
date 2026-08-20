using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// Runtime (build-safe) inline rename for the left panel's Hair Group rows.
//
// The old rename gesture called EditorInputDialog, which only exists inside
// #if UNITY_EDITOR, so in a standalone build double-clicking a group name did
// nothing at all. This authority replaces that modal dialog with an in-place
// editor: the group button's own name line becomes a live text field with a
// blinking caret. Typing, BACKSPACE and CTRL + BACKSPACE (clear the whole
// name) all work, ENTER or a click elsewhere commits, ESC cancels, and an
// empty field falls back to the name the group already had.
//
// ModelViewer.groupNames stores only the friendly suffix ("Spike"); the panel
// renderer (GroupPanelPostHintStats) draws it as "G0_Spike", or "GROUP 0" when
// the group is unnamed. This editor keeps that contract by normalising whatever
// was typed back down to the suffix before storing it, so the numeric group id
// stays the real identity used by HairCards, POST, CLUMPER, UVs and saving.
[DefaultExecutionOrder(9500)]
public class GroupNameInlineEditAuthority : MonoBehaviour
{
    private const int NameCharacterLimit = 32;
    private const float CaretBlinkRate = 1.5f;

    // Windows-Explorer style: the existing name starts selected so typing
    // replaces it outright. Set this to false to place the caret at the end
    // of the existing name instead.
    private const bool SelectAllOnOpen = true;

    private static GroupNameInlineEditAuthority instance;

    private ModelViewer viewer;
    private FieldInfo groupNamesField;
    private MethodInfo refreshGroupListMethod;

    private int editingGroupId;
    private string originalStoredName;
    private TMP_InputField field;
    private TextMeshProUGUI hiddenNameText;
    private RectTransform hiddenNameRect;
    private bool teardownInProgress;

    // Other authorities can poll this to suppress single-key hotkeys while a
    // group name is being typed.
    public static bool IsEditing
    {
        get
        {
            if (instance == null) return false;
            if (instance.field == null) return false;
            return instance.editingGroupId >= 0;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        EnsureInstance();
    }

    static void EnsureInstance()
    {
        if (instance != null) return;

        GroupNameInlineEditAuthority existing = FindFirstObjectByType<GroupNameInlineEditAuthority>();
        if (existing != null)
        {
            instance = existing;
            return;
        }

        GameObject go = new GameObject("GroupNameInlineEditAuthority");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<GroupNameInlineEditAuthority>();
    }

    // Entry point used by ModelViewer's existing double-click gesture.
    public static void BeginEdit(int groupId)
    {
        EnsureInstance();
        if (instance == null) return;
        instance.OpenEditor(groupId);
    }

    public static void CommitActiveEdit()
    {
        if (instance == null) return;
        if (instance.field == null) return;
        instance.Commit();
    }

    void Awake()
    {
        instance = this;
        viewer = null;
        groupNamesField = null;
        refreshGroupListMethod = null;
        editingGroupId = -1;
        originalStoredName = string.Empty;
        field = null;
        hiddenNameText = null;
        hiddenNameRect = null;
        teardownInProgress = false;
    }

    void OnDestroy()
    {
        if (instance == this) instance = null;
    }

    void Update()
    {
        if (editingGroupId < 0) return;

        // A panel rebuild (add / delete / load / reset) destroys the row we are
        // sitting in. Drop the edit rather than writing a stale name back.
        if (field == null || hiddenNameText == null) ForgetEdit();
    }

    void LateUpdate()
    {
        if (editingGroupId < 0) return;
        if (field == null) return;

        // GroupPanelPostHintStats re-lays-out every row roughly ten times a
        // second, so keep the original label hidden and keep the editor pinned
        // over exactly the rect that label occupies.
        if (hiddenNameText != null) hiddenNameText.enabled = false;
        if (hiddenNameRect != null) CopyRect(hiddenNameRect, field.transform as RectTransform);

        HandleClearShortcut();
    }

    void OpenEditor(int groupId)
    {
        if (editingGroupId == groupId && field != null) return;
        if (field != null) Commit();

        GameObject row = GameObject.Find("GroupItem_" + groupId);
        if (row == null) return;

        Transform labelButton = row.transform.Find("LabelButton");
        if (labelButton == null) return;

        Transform nameLabel = labelButton.Find("Label");
        if (nameLabel == null) return;

        TextMeshProUGUI nameText = nameLabel.GetComponent<TextMeshProUGUI>();
        if (nameText == null) return;

        originalStoredName = string.Empty;
        Dictionary<int, string> names = GetGroupNames();
        if (names != null)
        {
            string stored;
            if (names.TryGetValue(groupId, out stored)) originalStoredName = stored;
        }

        editingGroupId = groupId;
        hiddenNameText = nameText;
        hiddenNameRect = nameText.rectTransform;
        hiddenNameText.enabled = false;

        BuildField(labelButton, nameText);
    }

    void BuildField(Transform labelButton, TextMeshProUGUI source)
    {
        GameObject fieldGO = new GameObject("GroupNameInlineEditor", typeof(RectTransform), typeof(Image), typeof(TMP_InputField));
        fieldGO.transform.SetParent(labelButton, false);
        CopyRect(source.rectTransform, fieldGO.GetComponent<RectTransform>());

        Image background = fieldGO.GetComponent<Image>();
        background.color = new Color(.10f, .10f, .10f, .92f);
        background.raycastTarget = true;

        GameObject viewportGO = new GameObject("Text Area", typeof(RectTransform), typeof(RectMask2D));
        viewportGO.transform.SetParent(fieldGO.transform, false);
        RectTransform viewport = viewportGO.GetComponent<RectTransform>();
        viewport.anchorMin = Vector2.zero;
        viewport.anchorMax = Vector2.one;
        viewport.pivot = new Vector2(.5f, .5f);
        viewport.offsetMin = new Vector2(4f, 1f);
        viewport.offsetMax = new Vector2(-4f, -1f);

        GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textGO.transform.SetParent(viewportGO.transform, false);
        TextMeshProUGUI text = textGO.GetComponent<TextMeshProUGUI>();
        RectTransform textRect = text.rectTransform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.pivot = new Vector2(.5f, .5f);
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;
        if (source.font != null) text.font = source.font;
        text.fontSize = 14f;
        text.fontStyle = FontStyles.Bold;
        text.color = Color.white;
        text.alignment = TextAlignmentOptions.MidlineLeft;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Overflow;
        text.richText = false;
        text.raycastTarget = false;

        field = fieldGO.GetComponent<TMP_InputField>();
        field.textViewport = viewport;
        field.textComponent = text;
        field.targetGraphic = background;
        field.transition = Selectable.Transition.None;
        field.lineType = TMP_InputField.LineType.SingleLine;
        field.contentType = TMP_InputField.ContentType.Standard;
        field.richText = false;
        field.characterLimit = NameCharacterLimit;
        field.caretWidth = 2;
        field.customCaretColor = true;
        field.caretColor = Color.white;
        field.caretBlinkRate = CaretBlinkRate;
        field.selectionColor = new Color(.25f, .65f, 1f, .45f);
        field.restoreOriginalTextOnEscape = true;
        field.onFocusSelectAll = SelectAllOnOpen;
        field.text = source.text;
        field.onEndEdit.AddListener(HandleEndEdit);

        if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(fieldGO);
        field.ActivateInputField();
        if (!SelectAllOnOpen) field.MoveTextEnd(false);
    }

    // CTRL + BACKSPACE wipes the whole name. This runs in LateUpdate so it lands
    // after TMP_InputField has already processed the frame's own key events.
    void HandleClearShortcut()
    {
        if (Keyboard.current == null) return;
        if (!Keyboard.current.backspaceKey.wasPressedThisFrame) return;
        if (!Keyboard.current.ctrlKey.isPressed) return;

        field.text = string.Empty;
        field.caretPosition = 0;
        field.selectionAnchorPosition = 0;
        field.selectionFocusPosition = 0;
        field.ForceLabelUpdate();
    }

    void HandleEndEdit(string value)
    {
        if (teardownInProgress) return;

        if (field != null && field.wasCanceled)
        {
            Cancel();
            return;
        }

        Commit();
    }

    void Commit()
    {
        if (teardownInProgress) return;
        teardownInProgress = true;

        string typed = string.Empty;
        if (field != null) typed = field.text;

        string cleaned = Sanitize(typed);
        Dictionary<int, string> names = GetGroupNames();
        if (names != null)
        {
            if (cleaned.Length == 0)
            {
                // Nothing entered - keep whatever the group was called before.
                names[editingGroupId] = originalStoredName;
            }
            else
            {
                names[editingGroupId] = NormalizeFriendlyName(editingGroupId, cleaned);
            }
        }

        Teardown();
        RequestRefresh();
    }

    void Cancel()
    {
        if (teardownInProgress) return;
        teardownInProgress = true;

        // Nothing was written during the edit, so cancelling just tears down.
        Teardown();
        RequestRefresh();
    }

    void ForgetEdit()
    {
        teardownInProgress = true;
        Teardown();
    }

    void Teardown()
    {
        if (field != null)
        {
            field.onEndEdit.RemoveListener(HandleEndEdit);
            GameObject go = field.gameObject;

            if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject == go)
                EventSystem.current.SetSelectedGameObject(null);

            field.DeactivateInputField();
            field = null;
            Destroy(go);
        }

        if (hiddenNameText != null) hiddenNameText.enabled = true;

        hiddenNameText = null;
        hiddenNameRect = null;
        editingGroupId = -1;
        originalStoredName = string.Empty;
        teardownInProgress = false;
    }

    static void CopyRect(RectTransform source, RectTransform target)
    {
        if (source == null || target == null) return;
        target.anchorMin = source.anchorMin;
        target.anchorMax = source.anchorMax;
        target.pivot = source.pivot;
        target.offsetMin = source.offsetMin;
        target.offsetMax = source.offsetMax;
    }

    // Group names end up inside a rich-text label, so drop anything that could
    // be read as a TMP tag and drop control characters the field may pass on.
    static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        StringBuilder builder = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '<' || c == '>') continue;
            if (char.IsControl(c)) continue;
            builder.Append(c);
        }

        return builder.ToString().Trim();
    }

    // Mirrors GroupPanelPostHintStats.NormalizeFriendlyName so the stored value
    // is always the bare suffix, never the rendered "G0_" form or a legacy
    // "Group 0 (Default)" placeholder.
    static string NormalizeFriendlyName(int groupId, string stored)
    {
        string value = string.Empty;
        if (stored != null) value = stored.Trim();
        if (value.Length == 0) return string.Empty;

        string legacy = "Group " + groupId;
        if (string.Equals(value, legacy, StringComparison.OrdinalIgnoreCase)) return string.Empty;
        if (string.Equals(value, legacy + " (Default)", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        if (string.Equals(value, "GROUP " + groupId, StringComparison.OrdinalIgnoreCase)) return string.Empty;
        if (string.Equals(value, "Default", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        if (string.Equals(value, "(Default)", StringComparison.OrdinalIgnoreCase)) return string.Empty;

        string renderedPrefix = "G" + groupId + "_";
        if (value.StartsWith(renderedPrefix, StringComparison.OrdinalIgnoreCase))
            value = value.Substring(renderedPrefix.Length).Trim();

        return value;
    }

    void ResolveViewer()
    {
        if (viewer != null && groupNamesField != null && refreshGroupListMethod != null) return;

        viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer == null) return;

        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        groupNamesField = typeof(ModelViewer).GetField("groupNames", flags);
        refreshGroupListMethod = typeof(ModelViewer).GetMethod("RefreshGroupListUI", flags, null, Type.EmptyTypes, null);
    }

    Dictionary<int, string> GetGroupNames()
    {
        ResolveViewer();
        if (viewer == null) return null;
        if (groupNamesField == null) return null;
        return groupNamesField.GetValue(viewer) as Dictionary<int, string>;
    }

    void RequestRefresh()
    {
        ResolveViewer();
        if (viewer == null) return;
        if (refreshGroupListMethod == null) return;
        refreshGroupListMethod.Invoke(viewer, null);
    }
}
