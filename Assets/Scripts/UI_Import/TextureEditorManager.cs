using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;
using System.Linq;

public class TextureEditorManager : MonoBehaviour
{
    private GameObject textureSliderPanelGO;
    private GameObject texturePreviewPlane;
    private Material hairCardMaterial;

    // Strand generation parameters for up to 100 strands per group
    public int currentTextureGroupId = 0;
    public float strandCount = 50f;
    public float waveAmount = 0.1f;
    public float clumpStrength = 0.2f;
    public float taperAmount = 0.5f;
    public float noiseScale = 0.1f;
    public float strandLength = 1.0f;

    public void Init(Material mat)
    {
        hairCardMaterial = mat;
    }

    public void SetPanelActive(bool active, Transform parentCanvas, System.Action onSwitchToGroom)
    {
        if (textureSliderPanelGO == null && active)
        {
            BuildTextureEditorUI(parentCanvas, onSwitchToGroom);
        }
        else if (textureSliderPanelGO != null)
        {
            textureSliderPanelGO.SetActive(active);
        }

        if (active)
        {
            if (texturePreviewPlane == null)
            {
                texturePreviewPlane = GameObject.CreatePrimitive(PrimitiveType.Quad);
                texturePreviewPlane.name = "HairTexturePreviewPlane";
                texturePreviewPlane.transform.position = new Vector3(0f, 0f, 1.5f);
                texturePreviewPlane.transform.localScale = new Vector3(0.6f, 1.2f, 1.0f);

                // Enforce exact 0-1 UV mapping on the preview plane mesh
                MeshFilter meshFilter = texturePreviewPlane.GetComponent<MeshFilter>();
                if (meshFilter != null && meshFilter.sharedMesh != null)
                {
                    Mesh mesh = meshFilter.sharedMesh;
                    Vector2[] uvs = new Vector2[]
                    {
                        new Vector2(0, 0),
                        new Vector2(1, 0),
                        new Vector2(0, 1),
                        new Vector2(1, 1)
                    };
                    mesh.uv = uvs;
                }

                MeshRenderer mr = texturePreviewPlane.GetComponent<MeshRenderer>();
                if (hairCardMaterial != null)
                {
                    mr.sharedMaterial = hairCardMaterial;
                }
            }
            else
            {
                texturePreviewPlane.SetActive(true);
            }
        }
        else
        {
            if (texturePreviewPlane != null)
                texturePreviewPlane.SetActive(false);
        }
    }

    void BuildTextureEditorUI(Transform parentCanvas, System.Action onSwitchToGroom)
    {
        GameObject panelGO = new GameObject("TextureEditorPanel", typeof(RectTransform), typeof(Image), typeof(GraphicRaycaster));
        panelGO.transform.SetParent(parentCanvas, false);

        RectTransform panelRect = panelGO.GetComponent<RectTransform>();
        panelRect.anchorMin = new Vector2(1, 0);
        panelRect.anchorMax = new Vector2(1, 1);
        panelRect.pivot = new Vector2(1, 0.5f);
        panelRect.sizeDelta = new Vector2(560, 0);
        panelRect.anchoredPosition = new Vector2(-10, 0);

        Image bgImage = panelGO.GetComponent<Image>();
        bgImage.color = new Color(0.15f, 0.15f, 0.15f, 0.85f);

        VerticalLayoutGroup layout = panelGO.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(15, 15, 12, 12);
        layout.spacing = 6;
        layout.childControlWidth = true;
        layout.childControlHeight = false;

        textureSliderPanelGO = panelGO;

        // Tab switcher row
        GameObject tabRowGO = new GameObject("PanelTabRow", typeof(RectTransform));
        tabRowGO.transform.SetParent(panelGO.transform, false);
        RectTransform tabRect = tabRowGO.GetComponent<RectTransform>();
        tabRect.sizeDelta = new Vector2(0, 45);

        HorizontalLayoutGroup hLayout = tabRowGO.AddComponent<HorizontalLayoutGroup>();
        hLayout.spacing = 8;
        hLayout.childControlWidth = true;
        hLayout.childControlHeight = true;

        // Groom Tab Button
        GameObject groomTabGO = new GameObject("GroomTabButton", typeof(RectTransform), typeof(Image), typeof(Button));
        groomTabGO.transform.SetParent(tabRowGO.transform, false);
        groomTabGO.GetComponent<Image>().color = new Color(0.25f, 0.25f, 0.25f);
        Button groomBtn = groomTabGO.GetComponent<Button>();

        GameObject groomTxtGO = new GameObject("Text", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI));
        groomTxtGO.transform.SetParent(groomTabGO.transform, false);
        TMPro.TextMeshProUGUI groomTmp = groomTxtGO.GetComponent<TMPro.TextMeshProUGUI>();
        groomTmp.text = "Groom Mode";
        groomTmp.fontSize = 16;
        groomTmp.fontStyle = TMPro.FontStyles.Bold;
        groomTmp.alignment = TMPro.TextAlignmentOptions.Center;
        groomTmp.color = Color.white;
        groomTxtGO.GetComponent<RectTransform>().anchorMin = Vector2.zero;
        groomTxtGO.GetComponent<RectTransform>().anchorMax = Vector2.one;
        groomTxtGO.GetComponent<RectTransform>().sizeDelta = Vector2.zero;

        // Texture Tab Button (Active)
        GameObject texTabGO = new GameObject("TexTabButton", typeof(RectTransform), typeof(Image), typeof(Button));
        texTabGO.transform.SetParent(tabRowGO.transform, false);
        texTabGO.GetComponent<Image>().color = new Color(0.2f, 0.5f, 0.8f);

        GameObject texTxtGO = new GameObject("Text", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI));
        texTxtGO.transform.SetParent(texTabGO.transform, false);
        TMPro.TextMeshProUGUI texTmp = texTxtGO.GetComponent<TMPro.TextMeshProUGUI>();
        texTmp.text = "Texture Editor";
        texTmp.fontSize = 16;
        texTmp.fontStyle = TMPro.FontStyles.Bold;
        texTmp.alignment = TMPro.TextAlignmentOptions.Center;
        texTmp.color = Color.white;
        texTxtGO.GetComponent<RectTransform>().anchorMin = Vector2.zero;
        texTxtGO.GetComponent<RectTransform>().anchorMax = Vector2.one;
        texTxtGO.GetComponent<RectTransform>().sizeDelta = Vector2.zero;

        groomBtn.onClick.AddListener(() => {
            onSwitchToGroom?.Invoke();
        });

        // Add procedural texture sliders
        CreateSliderUI(panelGO.transform, "Strand Count", 1f, 100f, strandCount, (val) => { strandCount = val; }, out _, 38, 16);
        CreateSliderUI(panelGO.transform, "Wave Amount", 0.0f, 1.0f, waveAmount, (val) => { waveAmount = val; }, out _, 38, 16);
        CreateSliderUI(panelGO.transform, "Clump Strength", 0.0f, 1.0f, clumpStrength, (val) => { clumpStrength = val; }, out _, 38, 16);
        CreateSliderUI(panelGO.transform, "Taper Amount", 0.0f, 1.0f, taperAmount, (val) => { taperAmount = val; }, out _, 38, 16);
        CreateSliderUI(panelGO.transform, "Noise Scale", 0.0f, 1.0f, noiseScale, (val) => { noiseScale = val; }, out _, 38, 16);
        CreateSliderUI(panelGO.transform, "Strand Length", 0.1f, 2.0f, strandLength, (val) => { strandLength = val; }, out _, 38, 16);
    }

    GameObject CreateSliderUI(Transform parent, string labelText, float min, float max, float defaultValue, UnityEngine.Events.UnityAction<float> onValueChanged, out Slider createdSlider, float rowHeight = 44f, int fontSize = 16)
    {
        GameObject rowGO = new GameObject(labelText + "_Row", typeof(RectTransform));
        rowGO.transform.SetParent(parent, false);
        RectTransform rowRect = rowGO.GetComponent<RectTransform>();
        rowRect.sizeDelta = new Vector2(0, rowHeight);

        VerticalLayoutGroup rowLayout = rowGO.AddComponent<VerticalLayoutGroup>();
        rowLayout.spacing = 2;
        rowLayout.padding = new RectOffset(0, 0, 2, 2);
        rowLayout.childControlWidth = true;
        rowLayout.childControlHeight = false;

        GameObject textGO = new GameObject(labelText + "_Text", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI));
        textGO.transform.SetParent(rowGO.transform, false);
        textGO.GetComponent<RectTransform>().sizeDelta = new Vector2(0, 18);

        TMPro.TextMeshProUGUI tmp = textGO.GetComponent<TMPro.TextMeshProUGUI>();
        tmp.text = labelText + ": " + defaultValue.ToString("F3");
        tmp.fontSize = fontSize;
        tmp.color = Color.white;

        GameObject sliderGO = new GameObject(labelText + "_Slider", typeof(RectTransform), typeof(Slider));
        sliderGO.transform.SetParent(rowGO.transform, false);
        sliderGO.GetComponent<RectTransform>().sizeDelta = new Vector2(0, 18);

        Slider slider = sliderGO.GetComponent<Slider>();
        slider.minValue = min;
        slider.maxValue = max;
        slider.value = defaultValue;

        GameObject backgroundGO = new GameObject("Background", typeof(RectTransform), typeof(Image));
        backgroundGO.transform.SetParent(sliderGO.transform, false);
        backgroundGO.GetComponent<Image>().color = new Color(0.3f, 0.3f, 0.3f);
        RectTransform bgRect = backgroundGO.GetComponent<RectTransform>();
        bgRect.anchorMin = new Vector2(0, 0.3f);
        bgRect.anchorMax = new Vector2(1, 0.7f);
        bgRect.sizeDelta = Vector2.zero;

        GameObject fillAreaGO = new GameObject("Fill Area", typeof(RectTransform));
        fillAreaGO.transform.SetParent(sliderGO.transform, false);
        RectTransform fillAreaRect = fillAreaGO.GetComponent<RectTransform>();
        fillAreaRect.anchorMin = new Vector2(0, 0.3f);
        fillAreaRect.anchorMax = new Vector2(1, 0.7f);
        fillAreaRect.sizeDelta = Vector2.zero;

        GameObject fillGO = new GameObject("Fill", typeof(RectTransform), typeof(Image));
        fillGO.transform.SetParent(fillAreaGO.transform, false);
        fillGO.GetComponent<Image>().color = new Color(0.2f, 0.6f, 1.0f);
        slider.fillRect = fillGO.GetComponent<RectTransform>();
        slider.fillRect.anchorMin = Vector2.zero;
        slider.fillRect.anchorMax = Vector2.zero;
        slider.fillRect.sizeDelta = Vector2.zero;

        GameObject handleAreaGO = new GameObject("Handle Slide Area", typeof(RectTransform));
        handleAreaGO.transform.SetParent(sliderGO.transform, false);
        RectTransform handleAreaRect = handleAreaGO.GetComponent<RectTransform>();
        handleAreaRect.anchorMin = Vector2.zero;
        handleAreaRect.anchorMax = Vector2.one;
        handleAreaRect.sizeDelta = Vector2.zero;

        GameObject handleGO = new GameObject("Handle", typeof(RectTransform), typeof(Image));
        handleGO.transform.SetParent(handleAreaGO.transform, false);
        handleGO.GetComponent<Image>().color = Color.white;
        slider.handleRect = handleGO.GetComponent<RectTransform>();
        slider.handleRect.sizeDelta = new Vector2(20, 0);

        slider.onValueChanged.AddListener((val) => {
            tmp.text = labelText + ": " + val.ToString("F3");
            onValueChanged.Invoke(val);
        });

        createdSlider = slider;
        return rowGO;
    }
}