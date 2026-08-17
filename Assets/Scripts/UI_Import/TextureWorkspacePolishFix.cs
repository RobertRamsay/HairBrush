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
    private const float SliderLabelWidth = 74f;
    private const float SliderWidth = 126f;
    private const float SliderValueWidth = 34f;
    private const float SliderHeight = 17f;

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

            // Texture filenames stay on one line and auto-size just enough to show the complete
            // basename. The LOAD button keeps its fixed width, so the left panel can remain as
            // compact as the Groom Groups panel without ellipsis or two-line wrapping.
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
                    tmp.fontSizeMax = 10f;
                    tmp.fontSizeMin = 5f;
                    tmp.margin = Vector4.zero;
                }
            }

            Transform sliderTransform = row.Find("SmoothnessSlider");
            if (sliderTransform == null) sliderTransform = row.Find("MetallicSlider");
            if (sliderTransform == null) continue;

            HorizontalLayoutGroup rowLayout = row.GetComponent<HorizontalLayoutGroup>();
            if (rowLayout != null)
                rowLayout.childAlignment = TextAnchor.MiddleLeft;

            Transform label = row.Find("Label");
            LayoutElement labelLayout = label != null ? label.GetComponent<LayoutElement>() : null;
            if (labelLayout != null)
            {
                labelLayout.preferredWidth = SliderLabelWidth;
                labelLayout.minWidth = SliderLabelWidth;
            }

            LayoutElement sliderLayout = sliderTransform.GetComponent<LayoutElement>();
            if (sliderLayout != null)
            {
                sliderLayout.preferredWidth = SliderWidth;
                sliderLayout.minWidth = SliderWidth;
                // This was the missing piece: with childControlHeight enabled and no preferred
                // height, Unity collapsed the actual slider bar to zero while the value survived.
                sliderLayout.preferredHeight = SliderHeight;
                sliderLayout.minHeight = SliderHeight;
            }

            RectTransform sliderRect = sliderTransform.GetComponent<RectTransform>();
            if (sliderRect != null)
                sliderRect.sizeDelta = new Vector2(SliderWidth, SliderHeight);

            Transform value = row.Find("Value");
            LayoutElement valueLayout = value != null ? value.GetComponent<LayoutElement>() : null;
            if (valueLayout != null)
            {
                valueLayout.preferredWidth = SliderValueWidth;
                valueLayout.minWidth = SliderValueWidth;
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
