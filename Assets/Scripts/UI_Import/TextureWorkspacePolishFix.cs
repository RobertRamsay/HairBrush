using System.Collections;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Final layout/alignment pass for Texture mode. TextureUVRectWorkspace deliberately owns the
// orthographic camera and preview scale, so this runs just after it and centres the completed
// atlas in the actually usable screen area between the two side panels. It also keeps the UV
// rectangle world visuals locked to that final plane transform.
[DefaultExecutionOrder(9300)]
public class TextureWorkspacePolishFix : MonoBehaviour
{
    private const float MaterialPanelWidth = 300f;
    private const float TextureInfoWidth = 220f;
    private const float SliderWidth = 216f;
    private const float SliderValueWidth = 44f;
    private const float SliderHeight = 18f;
    private const float SliderRowHeight = 54f;

    private GameObject texturePanel;
    private GameObject materialPanel;
    private GameObject previewPlane;
    private TextureUVRectWorkspace uvWorkspace;

    private FieldInfo rectanglesField;
    private FieldInfo draftLineField;
    private MethodInfo updateRectangleVisualMethod;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<TextureWorkspacePolishFix>() != null) return;
        GameObject go = new GameObject("TextureWorkspacePolishFix");
        DontDestroyOnLoad(go);
        go.AddComponent<TextureWorkspacePolishFix>();
    }

    void LateUpdate()
    {
        Resolve();
        if (texturePanel == null || !texturePanel.activeInHierarchy) return;

        PolishMaterialPanel();

        if (previewPlane == null || Camera.main == null) return;
        Vector3 moveDelta = CentrePreviewBetweenPanels();
        RefreshUVVisuals(moveDelta);
    }

    void Resolve()
    {
        if (texturePanel == null)
            texturePanel = FindInactive("TextureEditorPanel");
        if (materialPanel == null)
            materialPanel = FindInactive("TextureMaterialPanel");
        if (previewPlane == null)
            previewPlane = FindInactive("HairTexturePreviewPlane");

        if (uvWorkspace == null)
        {
            uvWorkspace = FindFirstObjectByType<TextureUVRectWorkspace>();
            if (uvWorkspace != null)
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                System.Type type = typeof(TextureUVRectWorkspace);
                rectanglesField = type.GetField("rectangles", flags);
                draftLineField = type.GetField("draftLine", flags);
                updateRectangleVisualMethod = type.GetMethod("CreateOrUpdateRectangleVisual", flags);
            }
        }
    }

    void PolishMaterialPanel()
    {
        if (materialPanel == null || !materialPanel.activeInHierarchy) return;

        RectTransform panelRect = materialPanel.GetComponent<RectTransform>();
        if (panelRect != null)
            panelRect.sizeDelta = new Vector2(MaterialPanelWidth, panelRect.sizeDelta.y);

        Transform properties = materialPanel.transform.Find("Properties");
        if (properties == null) return;

        foreach (Transform row in properties)
        {
            if (row == null) continue;

            // Keep the complete texture filename on one line, but make it substantially more
            // readable than the previous tiny fit. Auto-sizing is only allowed to reduce from
            // the larger target when a particularly long basename genuinely needs it.
            Transform info = row.Find("Info");
            if (info != null)
            {
                LayoutElement infoLayout = info.GetComponent<LayoutElement>();
                if (infoLayout != null)
                {
                    infoLayout.preferredWidth = TextureInfoWidth;
                    infoLayout.minWidth = TextureInfoWidth;
                }

                foreach (TextMeshProUGUI tmp in info.GetComponentsInChildren<TextMeshProUGUI>(true))
                {
                    if (tmp == null || !tmp.text.StartsWith("Current:")) continue;
                    tmp.enableWordWrapping = false;
                    tmp.overflowMode = TextOverflowModes.Overflow;
                    tmp.enableAutoSizing = true;
                    tmp.fontSize = 18f;
                    tmp.fontSizeMax = 18f;
                    tmp.fontSizeMin = 10f;
                    tmp.margin = Vector4.zero;
                }
            }

            Transform sliderTransform = row.Find("SmoothnessSlider");
            if (sliderTransform == null) sliderTransform = row.Find("MetallicSlider");
            if (sliderTransform == null) continue;

            // These material properties read much better as a compact two-line block:
            // property name on the first line, then the actual control directly underneath.
            // Disable the original one-line HorizontalLayoutGroup and position its existing
            // children explicitly inside the 280 px content width of the 300 px side panel.
            HorizontalLayoutGroup rowLayout = row.GetComponent<HorizontalLayoutGroup>();
            if (rowLayout != null) rowLayout.enabled = false;

            LayoutElement rowElement = row.GetComponent<LayoutElement>();
            if (rowElement != null)
            {
                rowElement.preferredHeight = SliderRowHeight;
                rowElement.minHeight = SliderRowHeight;
            }

            RectTransform labelRect = row.Find("Label") as RectTransform;
            if (labelRect != null)
            {
                labelRect.anchorMin = new Vector2(0f, 1f);
                labelRect.anchorMax = new Vector2(1f, 1f);
                labelRect.pivot = new Vector2(0f, 1f);
                labelRect.anchoredPosition = Vector2.zero;
                labelRect.sizeDelta = new Vector2(0f, 22f);

                LayoutElement labelLayout = labelRect.GetComponent<LayoutElement>();
                if (labelLayout != null)
                {
                    labelLayout.preferredWidth = -1f;
                    labelLayout.minWidth = -1f;
                    labelLayout.preferredHeight = 22f;
                }
            }

            LayoutElement sliderLayout = sliderTransform.GetComponent<LayoutElement>();
            if (sliderLayout != null)
            {
                sliderLayout.preferredWidth = SliderWidth;
                sliderLayout.minWidth = SliderWidth;
                sliderLayout.preferredHeight = SliderHeight;
                sliderLayout.minHeight = SliderHeight;
            }

            RectTransform sliderRect = sliderTransform.GetComponent<RectTransform>();
            if (sliderRect != null)
            {
                sliderRect.anchorMin = new Vector2(0f, 1f);
                sliderRect.anchorMax = new Vector2(0f, 1f);
                sliderRect.pivot = new Vector2(0f, .5f);
                sliderRect.anchoredPosition = new Vector2(0f, -36f);
                sliderRect.sizeDelta = new Vector2(SliderWidth, SliderHeight);
            }

            RectTransform valueRect = row.Find("Value") as RectTransform;
            if (valueRect != null)
            {
                valueRect.anchorMin = new Vector2(0f, 1f);
                valueRect.anchorMax = new Vector2(0f, 1f);
                valueRect.pivot = new Vector2(0f, .5f);
                valueRect.anchoredPosition = new Vector2(SliderWidth + 8f, -36f);
                valueRect.sizeDelta = new Vector2(SliderValueWidth, 22f);

                LayoutElement valueLayout = valueRect.GetComponent<LayoutElement>();
                if (valueLayout != null)
                {
                    valueLayout.preferredWidth = SliderValueWidth;
                    valueLayout.minWidth = SliderValueWidth;
                    valueLayout.preferredHeight = 22f;
                }
            }
        }
    }

    Vector3 CentrePreviewBetweenPanels()
    {
        Camera camera = Camera.main;
        if (camera == null || previewPlane == null) return Vector3.zero;

        RectTransform leftRect = materialPanel != null && materialPanel.activeInHierarchy
            ? materialPanel.GetComponent<RectTransform>() : null;
        RectTransform rightRect = texturePanel.GetComponent<RectTransform>();
        if (leftRect == null || rightRect == null) return Vector3.zero;

        if (!TryGetScreenBounds(leftRect, out float leftMin, out float leftMax) ||
            !TryGetScreenBounds(rightRect, out float rightMin, out float rightMax))
            return Vector3.zero;

        float workspaceLeft = leftMax;
        float workspaceRight = rightMin;
        if (workspaceRight <= workspaceLeft + 20f) return Vector3.zero;

        float centreX = (workspaceLeft + workspaceRight) * .5f;
        float depth = Vector3.Dot(previewPlane.transform.position - camera.transform.position, camera.transform.forward);
        if (depth <= camera.nearClipPlane)
            depth = Mathf.Max(1f, camera.nearClipPlane + .1f);

        Vector3 oldPosition = previewPlane.transform.position;
        Vector3 target = camera.ScreenToWorldPoint(new Vector3(centreX, Screen.height * .5f, depth));
        previewPlane.transform.position = target;
        return target - oldPosition;
    }

    static bool TryGetScreenBounds(RectTransform rect, out float minX, out float maxX)
    {
        minX = float.MaxValue;
        maxX = float.MinValue;
        if (rect == null) return false;

        Vector3[] corners = new Vector3[4];
        rect.GetWorldCorners(corners);
        Camera canvasCamera = null;
        Canvas canvas = rect.GetComponentInParent<Canvas>();
        if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
            canvasCamera = canvas.worldCamera;

        for (int i = 0; i < corners.Length; i++)
        {
            Vector2 screen = RectTransformUtility.WorldToScreenPoint(canvasCamera, corners[i]);
            minX = Mathf.Min(minX, screen.x);
            maxX = Mathf.Max(maxX, screen.x);
        }
        return maxX >= minX;
    }

    void RefreshUVVisuals(Vector3 moveDelta)
    {
        if (uvWorkspace == null) return;

        // Existing saved rectangles are re-evaluated from their UV coordinates against the
        // preview plane's final transform. That keeps both cyan outlines and number labels glued
        // to the texture instead of retaining world positions from before the workspace shift.
        if (rectanglesField != null && updateRectangleVisualMethod != null)
        {
            IEnumerable definitions = rectanglesField.GetValue(uvWorkspace) as IEnumerable;
            if (definitions != null)
            {
                foreach (object definition in definitions)
                    if (definition != null)
                        updateRectangleVisualMethod.Invoke(uvWorkspace, new[] { definition });
            }
        }

        // The in-progress draft is recalculated by TextureUVRectWorkspace during Update, before
        // this final plane centring pass, so carry it by the same one-frame delta while drawing.
        if (moveDelta.sqrMagnitude > .0000001f && draftLineField != null)
        {
            LineRenderer draft = draftLineField.GetValue(uvWorkspace) as LineRenderer;
            if (draft != null && draft.gameObject.activeInHierarchy)
            {
                for (int i = 0; i < draft.positionCount; i++)
                    draft.SetPosition(i, draft.GetPosition(i) + moveDelta);
            }
        }
    }

    static GameObject FindInactive(string objectName)
    {
        foreach (Transform transform in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (transform != null && transform.name == objectName)
                return transform.gameObject;
        return null;
    }
}
