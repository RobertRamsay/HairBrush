using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Core editing policy:
// - A group with live modifiers starts locked.
// - FORCE UNLOCK freezes modifier evaluation, restores the group's canonical groom and unlocks core controls.
// - UNFREEZE / REAPPLY turns the modifier systems back on and evaluates their stored settings against the new core.
[DefaultExecutionOrder(5000)]
public class ModifierCoreLock : MonoBehaviour
{
    private static readonly string[] CoreRowNames =
    {
        "Length_Row", "Width_Row", "Segments_Row", "Bend Angle_Row", "Twist Angle_Row",
        "Embed Depth_Row", "Offset X_Row", "Offset Y_Row", "Offset Z_Row",
        "Angle X_Row", "Angle Y_Row", "Angle Z_Row", "U Scale_Row", "V Scale_Row",
        "U Offset_Row", "V Offset_Row"
    };

    private ModelViewer viewer;
    private PostAffectorManager postManager;
    private GroomVarianceController varianceManager;
    private ClumpLayerManager clumpManager;
    private FieldInfo hasSelectionField;
    private FieldInfo clumpLayersField;
    private GameObject boundPanel;
    private GameObject lockNotice;
    private Button actionButton;
    private TextMeshProUGUI noticeText;
    private TextMeshProUGUI actionText;
    private bool? lastLocked;
    private int lastGroup = int.MinValue;
    private float nextScan;

    private bool frozen;
    private int frozenGroup = -1;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<ModifierCoreLock>() != null) return;
        GameObject go = new GameObject("ModifierCoreLock");
        DontDestroyOnLoad(go);
        go.AddComponent<ModifierCoreLock>();
    }

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + 0.08f;

        ResolveReferences();
        if (viewer == null || viewer.groomingSliderPanelGO == null) return;

        if (boundPanel != viewer.groomingSliderPanelGO)
        {
            boundPanel = viewer.groomingSliderPanelGO;
            lastLocked = null;
            EnsureNotice();
        }

        int groupId = viewer.currentGroupId;

        // Freeze belongs to one group's core-edit session. If the user moves away, safely
        // re-enable the stack so other groups are never left with globally paused managers.
        if (frozen && groupId != frozenGroup)
            UnfreezeAndReapply();

        bool editingPost = IsLocalizedPostEditing();
        bool hasModifiers = GroupHasPost(groupId) || GroupHasVariance(groupId) || GroupHasEnabledClump(groupId);
        bool locked = hasModifiers && !editingPost && !frozen;

        if (lastLocked != locked || lastGroup != groupId)
        {
            ApplyLock(locked);
            lastLocked = locked;
            lastGroup = groupId;
        }

        UpdateNotice(hasModifiers, locked);
    }

    void ResolveReferences()
    {
        if (viewer == null)
        {
            viewer = FindFirstObjectByType<ModelViewer>();
            if (viewer != null)
                hasSelectionField = typeof(ModelViewer).GetField("hasSelectionHotspot", BindingFlags.Instance | BindingFlags.NonPublic);
        }
        if (postManager == null) postManager = FindFirstObjectByType<PostAffectorManager>();
        if (varianceManager == null) varianceManager = FindFirstObjectByType<GroomVarianceController>();
        if (clumpManager == null)
        {
            clumpManager = FindFirstObjectByType<ClumpLayerManager>();
            if (clumpManager != null)
                clumpLayersField = typeof(ClumpLayerManager).GetField("layers", BindingFlags.Instance | BindingFlags.NonPublic);
        }
    }

    bool IsLocalizedPostEditing()
    {
        return viewer != null && hasSelectionField != null && hasSelectionField.GetValue(viewer) is bool active && active;
    }

    bool GroupHasPost(int groupId)
    {
        if (postManager == null) return false;
        try
        {
            List<PostAffectorSaveData> items = postManager.ExportGroup(groupId);
            return items != null && items.Count > 0;
        }
        catch { return false; }
    }

    bool GroupHasVariance(int groupId)
    {
        if (varianceManager == null) return false;
        try
        {
            List<VarianceChannelSaveData> settings = varianceManager.ExportGroupSettings(groupId);
            return settings != null && settings.Any(s => s != null && Mathf.Abs(s.amount) > 0.000001f);
        }
        catch { return false; }
    }

    bool GroupHasEnabledClump(int groupId)
    {
        if (clumpManager == null || clumpLayersField == null) return false;
        try
        {
            object raw = clumpLayersField.GetValue(clumpManager);
            if (raw is IDictionary dictionary && dictionary.Contains(groupId))
            {
                object layer = dictionary[groupId];
                if (layer == null) return false;
                FieldInfo enabledField = layer.GetType().GetField("enabled", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return enabledField != null && enabledField.GetValue(layer) is bool enabled && enabled;
            }
        }
        catch { }
        return false;
    }

    void ForceUnlockAndFreeze()
    {
        if (viewer == null || frozen) return;

        frozen = true;
        frozenGroup = viewer.currentGroupId;

        // Stop all three modifier evaluators while the core is being authored. They retain
        // their own settings/state; disabling them only pauses evaluation.
        if (postManager != null) postManager.enabled = false;
        if (varianceManager != null) varianceManager.enabled = false;
        if (clumpManager != null) clumpManager.enabled = false;

        if (hasSelectionField != null) hasSelectionField.SetValue(viewer, false);

        // Show the untouched canonical groom while modifiers are frozen.
        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None).Where(c => c.groupId == frozenGroup))
        {
            card.ClearClumpModifier();
            card.ApplyEvaluatedState(card.GetCanonicalState());
        }

        ApplyLock(false);
        lastLocked = false;
        UpdateNotice(true, false);
    }

    void UnfreezeAndReapply()
    {
        if (!frozen) return;
        int groupId = frozenGroup;

        frozen = false;
        frozenGroup = -1;

        if (postManager != null) postManager.enabled = true;
        if (varianceManager != null) varianceManager.enabled = true;
        if (clumpManager != null) clumpManager.enabled = true;

        // Variance normally reapplies on UI/card lifecycle changes; force one fresh evaluation now.
        if (varianceManager != null)
        {
            MethodInfo applyVariance = typeof(GroomVarianceController).GetMethod("ApplyAllVarianceForGroup", BindingFlags.Instance | BindingFlags.NonPublic);
            try { applyVariance?.Invoke(varianceManager, new object[] { groupId }); } catch { }
        }

        // Clump also retains its generated points/settings while frozen. Invoke its private
        // ApplyLayer once so the stored layer immediately comes back against the new core.
        if (clumpManager != null && clumpLayersField != null)
        {
            try
            {
                object raw = clumpLayersField.GetValue(clumpManager);
                if (raw is IDictionary dictionary && dictionary.Contains(groupId))
                {
                    object layer = dictionary[groupId];
                    MethodInfo applyLayer = typeof(ClumpLayerManager).GetMethod("ApplyLayer", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (layer != null) applyLayer?.Invoke(clumpManager, new object[] { layer });
                }
            }
            catch { }
        }

        // POST evaluates in LateUpdate once re-enabled.
        lastLocked = null;
        nextScan = 0f;
    }

    void ApplyLock(bool locked)
    {
        if (boundPanel == null) return;

        foreach (string rowName in CoreRowNames)
        {
            Transform row = boundPanel.transform.Find(rowName);
            if (row == null) continue;
            foreach (Slider slider in row.GetComponentsInChildren<Slider>(true))
                slider.interactable = !locked;

            CanvasGroup cg = row.GetComponent<CanvasGroup>();
            if (cg == null) cg = row.gameObject.AddComponent<CanvasGroup>();
            cg.alpha = locked ? 0.48f : 1f;
        }
    }

    void UpdateNotice(bool hasModifiers, bool locked)
    {
        EnsureNotice();
        if (lockNotice == null) return;

        if (frozen)
        {
            lockNotice.SetActive(true);
            if (noticeText != null) noticeText.text = "MODIFIERS FROZEN — editing core groom";
            if (actionText != null) actionText.text = "UNFREEZE / REAPPLY";
            if (actionButton != null)
            {
                actionButton.onClick.RemoveAllListeners();
                actionButton.onClick.AddListener(UnfreezeAndReapply);
            }
            return;
        }

        lockNotice.SetActive(locked);
        if (!locked) return;

        if (noticeText != null) noticeText.text = "CORE LOCKED — modifiers depend on this base groom";
        if (actionText != null) actionText.text = "FORCE UNLOCK (FREEZE MODIFIERS)";
        if (actionButton != null)
        {
            actionButton.onClick.RemoveAllListeners();
            actionButton.onClick.AddListener(ForceUnlockAndFreeze);
        }
    }

    void EnsureNotice()
    {
        if (boundPanel == null) return;
        if (lockNotice != null && lockNotice.transform.parent == boundPanel.transform) return;

        Transform existing = boundPanel.transform.Find("CoreLockedNotice");
        if (existing != null)
        {
            lockNotice = existing.gameObject;
            noticeText = lockNotice.GetComponentInChildren<TextMeshProUGUI>(true);
            actionButton = lockNotice.GetComponentInChildren<Button>(true);
            if (actionButton != null) actionText = actionButton.GetComponentInChildren<TextMeshProUGUI>(true);
            return;
        }

        lockNotice = new GameObject("CoreLockedNotice", typeof(RectTransform), typeof(LayoutElement), typeof(VerticalLayoutGroup));
        lockNotice.transform.SetParent(boundPanel.transform, false);
        RectTransform rt = lockNotice.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(0f, 62f);
        LayoutElement le = lockNotice.GetComponent<LayoutElement>();
        le.preferredHeight = 62f;
        le.minHeight = 62f;
        VerticalLayoutGroup layout = lockNotice.GetComponent<VerticalLayoutGroup>();
        layout.spacing = 3f;
        layout.childControlHeight = false;
        layout.childControlWidth = true;
        layout.childForceExpandHeight = false;

        GameObject textGO = new GameObject("Message", typeof(RectTransform), typeof(LayoutElement), typeof(TextMeshProUGUI));
        textGO.transform.SetParent(lockNotice.transform, false);
        textGO.GetComponent<LayoutElement>().preferredHeight = 25f;
        noticeText = textGO.GetComponent<TextMeshProUGUI>();
        noticeText.fontSize = 12f;
        noticeText.fontStyle = FontStyles.Bold;
        noticeText.alignment = TextAlignmentOptions.Center;
        noticeText.color = new Color(1f, 0.72f, 0.28f, 1f);
        noticeText.raycastTarget = false;

        GameObject buttonGO = new GameObject("CoreLockAction", typeof(RectTransform), typeof(LayoutElement), typeof(Image), typeof(Button));
        buttonGO.transform.SetParent(lockNotice.transform, false);
        buttonGO.GetComponent<LayoutElement>().preferredHeight = 30f;
        buttonGO.GetComponent<Image>().color = new Color(0.48f, 0.28f, 0.10f, 1f);
        actionButton = buttonGO.GetComponent<Button>();

        GameObject buttonTextGO = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        buttonTextGO.transform.SetParent(buttonGO.transform, false);
        RectTransform btr = buttonTextGO.GetComponent<RectTransform>();
        btr.anchorMin = Vector2.zero; btr.anchorMax = Vector2.one; btr.offsetMin = Vector2.zero; btr.offsetMax = Vector2.zero;
        actionText = buttonTextGO.GetComponent<TextMeshProUGUI>();
        actionText.fontSize = 11f;
        actionText.fontStyle = FontStyles.Bold;
        actionText.alignment = TextAlignmentOptions.Center;
        actionText.color = Color.white;
        actionText.raycastTarget = false;

        int targetIndex = Mathf.Min(2, boundPanel.transform.childCount - 1);
        lockNotice.transform.SetSiblingIndex(targetIndex);
        lockNotice.SetActive(false);
    }

    void OnDestroy()
    {
        // Never leave evaluator components disabled if this helper is destroyed/reloaded.
        if (postManager != null) postManager.enabled = true;
        if (varianceManager != null) varianceManager.enabled = true;
        if (clumpManager != null) clumpManager.enabled = true;
    }
}
