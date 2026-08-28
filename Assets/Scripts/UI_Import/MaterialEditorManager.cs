using UnityEngine;
using UnityEngine.UI;
using System;
using System.IO;
using System.Collections.Generic;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class MaterialEditorManager : MonoBehaviour
{
    private const string AlbedoProperty = "_Albedo";
    private const string NormalProperty = "_Normal";
    private const string OpacityProperty = "_OpacityMask";
    private const string SmoothProperty = "_Smooth";
    private const string MetalProperty = "_Metal";

    // The master colour. The hair shader multiplies it straight into the albedo sample
    // (HairShader_DithCut: _HairTint * tex2D(_Albedo, uv).rgb), so white leaves the texture
    // exactly as authored and anything else tints it. With the albedo CLEARED it is the hair
    // colour outright, because an unassigned texture samples as white.
    public const string TintProperty = "_HairTint";

    // What the shader itself defaults _HairTint to - a faint warm grey, not white. Captured
    // from the template material at Init rather than written as a literal, and used for exactly
    // one thing: a project saved before this control existed had no tint of its own, so it is
    // restored to this and looks precisely as it did. New materials start white, as asked.
    private Color shaderDefaultTint = Color.white;
    private bool hasShaderDefaultTint = false;

    public Color ShaderDefaultTint
    {
        get { return shaderDefaultTint; }
    }

    // HairBrush intentionally has one active hair material for the whole session. Multiple
    // material entries are authoring presets/stages, never per-group assignments. Keeping the
    // old dictionary with one reserved key lets existing project persistence migrate cleanly.
    private const int GlobalMaterialKey = int.MinValue;

    // Label line, button line, filename line. TextureWorkspacePolishFix places the children
    // against these same numbers.
    public const float TextureRowHeight = 74f;

    [Serializable]
    private class HairMaterialEntry
    {
        public string name;
        public Material material;
        public string albedoPath = "";
        public string normalPath = "";
        public string opacityPath = "";

        // CLEARED is not the same state as "no path". The material is cloned from the template,
        // which SHIPS with all three maps, so an empty path has always meant "never loaded, keep
        // what the template gave you". Now that a slot can be deliberately emptied, that needs
        // saying out loud - otherwise a clear cannot survive a save, an undo or a redo, because
        // the restore has no way to tell it apart from a project nobody ever loaded a map into.
        public bool albedoCleared = false;
        public bool normalCleared = false;
        public bool opacityCleared = false;
    }

    private ModelViewer viewer;
    private Material sourceMaterial;
    private readonly List<HairMaterialEntry> materials = new List<HairMaterialEntry>();
    private readonly Dictionary<int, int> groupMaterial = new Dictionary<int, int>();
    private int selectedMaterialIndex;
    private int lastGroupId = int.MinValue;
    private float nextApplyScan;

    private GameObject panelGO;
    private Transform materialListRoot;
    private Transform propertiesRoot;
    private TMPro.TextMeshProUGUI assignmentLabel;

    public void Init(ModelViewer modelViewer, Material materialTemplate)
    {
        viewer = modelViewer;
        sourceMaterial = materialTemplate;
        foreach (HairMaterialEntry e in materials) if (e.material != null) Destroy(e.material);
        materials.Clear();
        groupMaterial.Clear();
        if (sourceMaterial != null)
        {
            materials.Add(CreateEntry("Mat 1", sourceMaterial));
            selectedMaterialIndex = 0;
            groupMaterial[GlobalMaterialKey] = 0;
            ApplyAssignments();
        }
    }

    private void Update()
    {
        if (viewer == null || materials.Count == 0) return;
        if (Time.unscaledTime < nextApplyScan) return;
        nextApplyScan = Time.unscaledTime + .2f;

        // Group selection no longer changes material state. Refresh only so any group-related
        // workspace around this panel can change without accidentally changing the hair material.
        if (lastGroupId != viewer.currentGroupId)
        {
            lastGroupId = viewer.currentGroupId;
            SyncViewerMaterialToCurrentGroup();
            RefreshPanel();
        }
        ApplyAssignments();
    }

    public void SetWorkspaceVisible(bool visible, Transform parentCanvas)
    {
        if (visible)
        {
            if (panelGO == null) BuildUI(parentCanvas);
            else if (panelGO.transform.parent != parentCanvas) panelGO.transform.SetParent(parentCanvas, false);
            panelGO.SetActive(true);
            UpdatePreviewForSelectedMaterial();
            RefreshPanel();
        }
        else if (panelGO != null) panelGO.SetActive(false);
    }

    public void TogglePanel(Transform parentCanvas) => SetWorkspaceVisible(panelGO == null || !panelGO.activeSelf, parentCanvas);
    public void ShowPanel(Transform parentCanvas) => SetWorkspaceVisible(true, parentCanvas);
    public void HidePanel() { if (panelGO != null) panelGO.SetActive(false); }

    private HairMaterialEntry CreateEntry(string name, Material template)
    {
        Material mat = new Material(template); mat.name = name + "_Runtime";

        if (!hasShaderDefaultTint && mat.HasProperty(TintProperty))
        {
            shaderDefaultTint = mat.GetColor(TintProperty);
            hasShaderDefaultTint = true;
        }

        // White by default, per the request. It is not what the shader ships with, so it is set
        // here rather than assumed - and a project saved before this existed puts the shader's
        // own value back on load, so nothing already authored changes appearance.
        if (mat.HasProperty(TintProperty)) mat.SetColor(TintProperty, Color.white);

        return new HairMaterialEntry { name = name, material = mat };
    }

    private void AddNewMaterial()
    {
        if (sourceMaterial == null) return;
        materials.Add(CreateEntry("Mat " + (materials.Count + 1), sourceMaterial));
        selectedMaterialIndex = materials.Count - 1;
        RefreshPanel();
        UpdatePreviewForSelectedMaterial();
    }

    private void AssignSelectedToAllGroups()
    {
        if (viewer == null || selectedMaterialIndex < 0 || selectedMaterialIndex >= materials.Count) return;

        // A material choice is session-global by design. Clear any legacy per-group mappings so
        // there is only one source of truth from this point onward.
        groupMaterial.Clear();
        groupMaterial[GlobalMaterialKey] = selectedMaterialIndex;
        SyncViewerMaterialToCurrentGroup();
        ApplyAssignments();
        UpdatePreviewForSelectedMaterial();
        RefreshPanel();
    }

    private void SelectMaterial(int index)
    {
        if (index < 0 || index >= materials.Count) return;
        selectedMaterialIndex = index;
        UpdatePreviewForSelectedMaterial();
        RefreshPanel();
    }

    private int GetGlobalMaterialIndex()
    {
        if (materials.Count == 0) return -1;
        int index = groupMaterial.TryGetValue(GlobalMaterialKey, out int assigned) ? assigned : 0;
        return Mathf.Clamp(index, 0, materials.Count - 1);
    }

    private void BuildUI(Transform parentCanvas)
    {
        // Root Canvas already owns raycasting; do not add another GraphicRaycaster here.
        panelGO = new GameObject("TextureMaterialPanel", typeof(RectTransform), typeof(Image));
        panelGO.transform.SetParent(parentCanvas, false);
        RectTransform rect = panelGO.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 0f); rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, .5f); rect.sizeDelta = new Vector2(440f, 0f); rect.anchoredPosition = new Vector2(10f, 0f);
        panelGO.GetComponent<Image>().color = new Color(.12f, .12f, .12f, .96f);

        VerticalLayoutGroup layout = panelGO.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(10, 10, 10, 10);
        layout.spacing = 2f;
        layout.childControlWidth = true;
        layout.childControlHeight = false;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        layout.childAlignment = TextAnchor.UpperLeft;

        CreateHeader(panelGO.transform, "MATERIALS", 20f);
        GameObject listRow = new GameObject("MaterialButtons", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        listRow.transform.SetParent(panelGO.transform, false);
        listRow.GetComponent<LayoutElement>().preferredHeight = 26f;
        HorizontalLayoutGroup rowLayout = listRow.GetComponent<HorizontalLayoutGroup>();
        rowLayout.spacing = 4f;
        rowLayout.childControlWidth = false;
        rowLayout.childControlHeight = true;
        rowLayout.childForceExpandWidth = false;
        rowLayout.childForceExpandHeight = false;
        materialListRoot = listRow.transform;

        assignmentLabel = CreateSubLabel(panelGO.transform, "", 16f);
        CreateActionButton(panelGO.transform, "APPLY ALL", AssignSelectedToAllGroups, 24f);
        CreateHeader(panelGO.transform, "PROPERTIES", 20f);
        propertiesRoot = CreateContainer(panelGO.transform, "Properties", 300f).transform;
    }

    private void RefreshPanel()
    {
        if (panelGO == null || materials.Count == 0) return;
        selectedMaterialIndex = Mathf.Clamp(selectedMaterialIndex, 0, materials.Count - 1);

        if (materialListRoot != null)
        {
            ClearChildren(materialListRoot);
            for (int i = 0; i < materials.Count; i++)
            {
                int capture = i;
                string label = i == selectedMaterialIndex ? "[" + materials[i].name + "]" : materials[i].name;
                CreateSmallButton(materialListRoot, label, () => SelectMaterial(capture), 48f, 24f);
            }
            CreateSmallButton(materialListRoot, "+", AddNewMaterial, 26f, 24f);
        }

        if (assignmentLabel != null)
        {
            int assignedIndex = GetGlobalMaterialIndex();
            string assigned = assignedIndex >= 0 ? materials[assignedIndex].name : "Mat 1";
            assignmentLabel.text = "ALL GROUPS  •  " + assigned;
        }

        if (propertiesRoot != null)
        {
            ClearChildren(propertiesRoot);
            HairMaterialEntry entry = materials[selectedMaterialIndex];
            CreateSubLabel(propertiesRoot, entry.name, 15f);
            // Texture2D's final constructor argument is "linear": colour/albedo must be false
            // (sRGB), while normals and opacity masks are data textures and stay linear.
            CreateTextureRow(propertiesRoot, "Albedo", AlbedoProperty, false, entry.albedoPath);
            CreateTextureRow(propertiesRoot, "Normal", NormalProperty, true, entry.normalPath);
            CreateTextureRow(propertiesRoot, "Opacity Mask", OpacityProperty, true, entry.opacityPath);
            CreateTintRow(propertiesRoot, entry);
            CreateFloatSliderRow(propertiesRoot, "Smoothness", SmoothProperty, entry);
            CreateFloatSliderRow(propertiesRoot, "Metallic", MetalProperty, entry);
        }
    }

    // One texture slot, in the shape asked for:
    //
    //     ALBEDO:
    //     [LOAD] [FIND] [CLEAR]
    //     FILE: HSD_NiceHairsExport_Color
    //
    // A flat row with the three children stacked by TextureWorkspacePolishFix, which positions
    // everything in this panel by hand. The previous shape - a two-line text block beside a
    // stacked button column - put the buttons and the filename on the same line as each other
    // and left neither enough room.
    private void CreateTextureRow(Transform parent, string label, string propertyName, bool linear, string currentPath)
    {
        GameObject row = new GameObject(label + "Row", typeof(RectTransform), typeof(LayoutElement));
        row.transform.SetParent(parent, false);

        LayoutElement rowElement = row.GetComponent<LayoutElement>();
        rowElement.preferredHeight = TextureRowHeight;
        rowElement.minHeight = TextureRowHeight;

        // No layout group on the row at all. Every child below is anchored explicitly by the
        // polish pass, and a layout group would only fight it - which is what it did.
        CreateSubLabel(row.transform, label, 18f);

        CreateSmallButton(row.transform, "LOAD", () => LoadTextureIntoSlot(propertyName, linear), 86f, 24f);

        // FIND rather than LOCATE: three buttons across 280px want short words, and this one
        // opens the folder the file came from.
        CreateSmallButton(row.transform, "FIND", () => LocateTextureFile(currentPath), 86f, 24f);
        CreateSmallButton(row.transform, "CLEAR", () => ClearTextureSlot(propertyName), 86f, 24f);

        string currentName = string.IsNullOrEmpty(currentPath) ? GetCurrentTextureName(propertyName) : Path.GetFileName(currentPath);

        GameObject fileGO = new GameObject("File", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI), typeof(LayoutElement));
        fileGO.transform.SetParent(row.transform, false);
        TMPro.TextMeshProUGUI file = fileGO.GetComponent<TMPro.TextMeshProUGUI>();
        file.text = "FILE: " + currentName;
        file.fontSize = 12f;
        file.color = new Color(.72f, .72f, .72f);
        file.alignment = TMPro.TextAlignmentOptions.MidlineLeft;

        // On its own line with the full panel width to itself, so a long basename shrinks a
        // little rather than wrapping into the row below - which is what it was doing.
        file.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
        file.overflowMode = TMPro.TextOverflowModes.Ellipsis;
    }

    // One 0-1 slider, built the way this panel builds them. Extracted so the master-colour
    // channels are the same widget as Smoothness and Metallic rather than a second, subtly
    // different copy of sixty lines - the fill-rect convention below in particular is not
    // something to re-derive twice.
    //
    // sliderName matters: TextureWorkspacePolishFix reformats any row containing a child called
    // "SmoothnessSlider" or "MetallicSlider", so what a caller names this decides whether its
    // row keeps its own layout.
    private static Slider BuildSliderWidget(Transform parent, string sliderName, float width)
    {
        GameObject sliderGO = new GameObject(sliderName, typeof(RectTransform), typeof(Slider), typeof(LayoutElement));
        sliderGO.transform.SetParent(parent, false);
        RectTransform sliderRect = sliderGO.GetComponent<RectTransform>();
        sliderRect.sizeDelta = new Vector2(width, 17f);
        sliderGO.GetComponent<LayoutElement>().preferredWidth = width;
        Slider slider = sliderGO.GetComponent<Slider>();
        slider.minValue = 0f;
        slider.maxValue = 1f;

        GameObject bgGO = new GameObject("Background", typeof(RectTransform), typeof(Image));
        bgGO.transform.SetParent(sliderGO.transform, false);
        RectTransform bgRect = bgGO.GetComponent<RectTransform>();
        bgRect.anchorMin = new Vector2(0f, .3f); bgRect.anchorMax = new Vector2(1f, .7f);
        bgRect.offsetMin = Vector2.zero; bgRect.offsetMax = Vector2.zero;
        bgGO.GetComponent<Image>().color = new Color(.18f, .18f, .20f);

        GameObject fillAreaGO = new GameObject("Fill Area", typeof(RectTransform));
        fillAreaGO.transform.SetParent(sliderGO.transform, false);
        RectTransform fillAreaRect = fillAreaGO.GetComponent<RectTransform>();
        fillAreaRect.anchorMin = new Vector2(0f, .3f); fillAreaRect.anchorMax = new Vector2(1f, .7f);
        fillAreaRect.offsetMin = Vector2.zero; fillAreaRect.offsetMax = Vector2.zero;

        GameObject fillGO = new GameObject("Fill", typeof(RectTransform), typeof(Image));
        fillGO.transform.SetParent(fillAreaGO.transform, false);
        fillGO.GetComponent<Image>().color = new Color(.30f, .65f, .70f);
        // Slider drives progress by resizing this rect's anchors itself each frame - it must
        // start as a zero-size point anchor at the origin, not a full stretch anchor. Using a
        // stretch anchor here (the original mistake) is what silently broke drag interaction:
        // the Slider's internal anchor math assumes this exact convention.
        slider.fillRect = fillGO.GetComponent<RectTransform>();
        slider.fillRect.anchorMin = Vector2.zero;
        slider.fillRect.anchorMax = Vector2.zero;
        slider.fillRect.sizeDelta = Vector2.zero;

        GameObject handleAreaGO = new GameObject("Handle Slide Area", typeof(RectTransform));
        handleAreaGO.transform.SetParent(sliderGO.transform, false);
        RectTransform handleAreaRect = handleAreaGO.GetComponent<RectTransform>();
        handleAreaRect.anchorMin = Vector2.zero; handleAreaRect.anchorMax = Vector2.one;
        handleAreaRect.offsetMin = Vector2.zero; handleAreaRect.offsetMax = Vector2.zero;

        GameObject handleGO = new GameObject("Handle", typeof(RectTransform), typeof(Image));
        handleGO.transform.SetParent(handleAreaGO.transform, false);
        handleGO.GetComponent<Image>().color = Color.white;
        slider.handleRect = handleGO.GetComponent<RectTransform>();
        slider.handleRect.sizeDelta = new Vector2(18f, 0f);

        return slider;
    }

    // Simple 0-1 float slider bound directly to a shader property on this entry's material.
    // The material's own current value is the single source of truth - no separate field is
    // kept on HairMaterialEntry, so there's nothing that can drift out of sync with it. Saving
    // reads straight from the material via MaterialProjectPersistenceBridge.Capture.
    private void CreateFloatSliderRow(Transform parent, string label, string propertyName, HairMaterialEntry entry)
    {
        GameObject row = new GameObject(label + "Row", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        row.transform.SetParent(parent, false);
        row.GetComponent<LayoutElement>().preferredHeight = 32f;
        HorizontalLayoutGroup layout = row.GetComponent<HorizontalLayoutGroup>();
        layout.spacing = 6f;
        layout.childControlWidth = false;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;

        GameObject labelGO = new GameObject("Label", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI), typeof(LayoutElement));
        labelGO.transform.SetParent(row.transform, false);
        labelGO.GetComponent<LayoutElement>().preferredWidth = 90f;
        TMPro.TextMeshProUGUI labelTmp = labelGO.GetComponent<TMPro.TextMeshProUGUI>();
        labelTmp.text = label;
        labelTmp.fontSize = 12f;
        labelTmp.color = Color.white;
        labelTmp.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
        labelTmp.textWrappingMode = TMPro.TextWrappingModes.NoWrap;

        Slider slider = BuildSliderWidget(row.transform, label + "Slider", 180f);

        GameObject valueGO = new GameObject("Value", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI), typeof(LayoutElement));
        valueGO.transform.SetParent(row.transform, false);
        valueGO.GetComponent<LayoutElement>().preferredWidth = 40f;
        TMPro.TextMeshProUGUI valueTmp = valueGO.GetComponent<TMPro.TextMeshProUGUI>();
        valueTmp.fontSize = 11f;
        valueTmp.color = new Color(.75f, .75f, .75f);
        valueTmp.alignment = TMPro.TextAlignmentOptions.MidlineLeft;

        float startValue = entry.material != null && entry.material.HasProperty(propertyName)
            ? entry.material.GetFloat(propertyName) : .5f;
        slider.SetValueWithoutNotify(startValue);
        valueTmp.text = startValue.ToString("F2");

        slider.onValueChanged.AddListener(v =>
        {
            valueTmp.text = v.ToString("F2");
            if (entry.material != null && entry.material.HasProperty(propertyName))
                entry.material.SetFloat(propertyName, v);

            UndoHistoryAuthority.NotifyEdit();

            // Same rule texture loading uses: only push to rendered hair cards if this entry
            // is the currently active global material.
            if (GetGlobalMaterialIndex() == selectedMaterialIndex)
            {
                viewer.hairCardMaterial = entry.material;
                ApplyAssignments();
            }
        });
    }

    // MASTER COLOUR. A swatch, three channel sliders and a WHITE reset, bound straight to the
    // material's _HairTint - the material is the single source of truth, exactly as the Smooth
    // and Metal rows work, so there is no second copy of the value to drift out of step.
    //
    // Three sliders rather than a colour picker: the panel is 300px wide and a picker wants a
    // wheel, a value ramp and a hex field. R/G/B fits, reads at a glance next to the swatch, and
    // is built out of the same slider widget the rest of this panel already uses.
    private void CreateTintRow(Transform parent, HairMaterialEntry entry)
    {
        if (entry == null || entry.material == null || !entry.material.HasProperty(TintProperty)) return;

        GameObject header = new GameObject("MasterColourRow", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        header.transform.SetParent(parent, false);
        // TextureWorkspacePolishFix positions this row's children by hand within the 280px
        // content width and sets the final heights. These are the starting values, and they set
        // BOTH min and preferred - a row that offers only a preferred height can be given none.
        LayoutElement headerElement = header.GetComponent<LayoutElement>();
        headerElement.preferredHeight = 26f;
        headerElement.minHeight = 26f;

        HorizontalLayoutGroup headerLayout = header.GetComponent<HorizontalLayoutGroup>();
        headerLayout.spacing = 6f;
        headerLayout.childControlWidth = false;
        headerLayout.childControlHeight = true;
        headerLayout.childForceExpandWidth = false;
        headerLayout.childForceExpandHeight = false;

        GameObject labelGO = new GameObject("Label", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI), typeof(LayoutElement));
        labelGO.transform.SetParent(header.transform, false);
        labelGO.GetComponent<LayoutElement>().preferredWidth = 132f;
        TMPro.TextMeshProUGUI labelTmp = labelGO.GetComponent<TMPro.TextMeshProUGUI>();
        labelTmp.text = "MASTER COLOUR";
        labelTmp.fontSize = 12f;
        labelTmp.fontStyle = TMPro.FontStyles.Bold;
        labelTmp.color = Color.white;
        labelTmp.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
        labelTmp.textWrappingMode = TMPro.TextWrappingModes.NoWrap;

        GameObject swatchGO = new GameObject("Swatch", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
        swatchGO.transform.SetParent(header.transform, false);
        LayoutElement swatchLayout = swatchGO.GetComponent<LayoutElement>();
        swatchLayout.preferredWidth = 44f;
        swatchLayout.minWidth = 44f;
        Image swatch = swatchGO.GetComponent<Image>();

        Color current = entry.material.GetColor(TintProperty);

        // Opaque in the swatch whatever the stored alpha is. The shader multiplies only the RGB
        // (see _HairTint * tex2D(...).rgb), so alpha means nothing here - and the material ships
        // with alpha 0, which would draw an invisible swatch that looks like a bug.
        swatch.color = new Color(current.r, current.g, current.b, 1f);

        // Rebuilds the panel rather than only setting the colour: the three sliders below hold
        // their own handles, and a WHITE that moved the material but left them where they were
        // would leave the row reading a value it no longer has.
        CreateSmallButton(header.transform, "WHITE", () => { SetTint(entry, Color.white, swatch); RefreshPanel(); }, 62f, 20f);

        // R/G/B rather than Red/Green/Blue: under a heading that already says MASTER COLOUR the
        // words earn nothing, and the letter leaves the slider the width it needs.
        CreateTintChannelRow(parent, entry, swatch, 0, "R");
        CreateTintChannelRow(parent, entry, swatch, 1, "G");
        CreateTintChannelRow(parent, entry, swatch, 2, "B");
    }

    private void CreateTintChannelRow(Transform parent, HairMaterialEntry entry, Image swatch, int channel, string label)
    {
        GameObject row = new GameObject(label + "TintRow", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        row.transform.SetParent(parent, false);
        LayoutElement rowLayout = row.GetComponent<LayoutElement>();
        rowLayout.preferredHeight = 24f;
        rowLayout.minHeight = 24f;
        HorizontalLayoutGroup layout = row.GetComponent<HorizontalLayoutGroup>();
        layout.spacing = 4f;
        layout.childControlWidth = false;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;

        GameObject labelGO = new GameObject("Label", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI), typeof(LayoutElement));
        labelGO.transform.SetParent(row.transform, false);
        labelGO.GetComponent<LayoutElement>().preferredWidth = 16f;
        TMPro.TextMeshProUGUI labelTmp = labelGO.GetComponent<TMPro.TextMeshProUGUI>();
        labelTmp.text = label;
        labelTmp.fontSize = 11f;
        labelTmp.color = new Color(.82f, .82f, .82f);
        labelTmp.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
        labelTmp.textWrappingMode = TMPro.TextWrappingModes.NoWrap;

        // NOT named "<label>Slider". TextureWorkspacePolishFix reformats any row it finds a
        // "SmoothnessSlider" or "MetallicSlider" in, tearing the horizontal layout off and
        // repositioning two known children by hand. These rows want to keep their own layout.
        Slider slider = BuildSliderWidget(row.transform, label + "TintChannel", 196f);

        GameObject valueGO = new GameObject("Value", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI), typeof(LayoutElement));
        valueGO.transform.SetParent(row.transform, false);
        valueGO.GetComponent<LayoutElement>().preferredWidth = 40f;
        TMPro.TextMeshProUGUI valueTmp = valueGO.GetComponent<TMPro.TextMeshProUGUI>();
        valueTmp.fontSize = 11f;
        valueTmp.color = new Color(.75f, .75f, .75f);
        valueTmp.alignment = TMPro.TextAlignmentOptions.MidlineLeft;

        Color start = entry.material.GetColor(TintProperty);
        float startValue = ChannelOf(start, channel);
        slider.SetValueWithoutNotify(startValue);
        valueTmp.text = startValue.ToString("F2");

        slider.onValueChanged.AddListener(v =>
        {
            valueTmp.text = v.ToString("F2");
            if (entry.material == null || !entry.material.HasProperty(TintProperty)) return;

            // Read-modify-write off the MATERIAL each time rather than off a captured Color, so
            // the three sliders and the WHITE button cannot fight over a stale copy.
            Color live = entry.material.GetColor(TintProperty);
            if (channel == 0) live.r = v;
            else if (channel == 1) live.g = v;
            else live.b = v;

            SetTint(entry, live, swatch);
        });
    }

    private static float ChannelOf(Color c, int channel)
    {
        if (channel == 0) return c.r;
        if (channel == 1) return c.g;
        return c.b;
    }

    private void SetTint(HairMaterialEntry entry, Color colour, Image swatch)
    {
        if (entry == null || entry.material == null || !entry.material.HasProperty(TintProperty)) return;

        entry.material.SetColor(TintProperty, colour);
        if (swatch != null) swatch.color = new Color(colour.r, colour.g, colour.b, 1f);

        // Armed on every write, not only on the one that ends the drag. The authority coalesces
        // - it captures a settle after activity stops and drops a step whose hash matches the
        // last - so a drag becomes one step, not one per frame.
        UndoHistoryAuthority.NotifyEdit();

        if (GetGlobalMaterialIndex() == selectedMaterialIndex)
        {
            viewer.hairCardMaterial = entry.material;
            ApplyAssignments();
        }

        UpdatePreviewForSelectedMaterial();
    }

    private void LoadTextureIntoSlot(string propertyName, bool linear)
    {
        if (selectedMaterialIndex < 0 || selectedMaterialIndex >= materials.Count) return;
        HairMaterialEntry entry = materials[selectedMaterialIndex];

        string path;
#if UNITY_EDITOR
        path = EditorUtility.OpenFilePanel("Load texture", "", "png,jpg,jpeg,tga");
#else
        path = RuntimeFileDialog.OpenFile("Load texture", "Images\0*.png;*.jpg;*.jpeg;*.tga\0All Files\0*.*\0\0", "png");
#endif
        if (string.IsNullOrEmpty(path)) return;

        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception ex)
        {
            Debug.LogError("Could not read texture file: " + ex.Message);
            StatusToast.Show("Couldn't read that file: " + ex.Message, true);
            return;
        }

        Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear);
        texture.name = Path.GetFileNameWithoutExtension(path);
        if (!texture.LoadImage(bytes, false))
        {
            Destroy(texture);
            Debug.LogError("Could not decode texture: " + path);
            StatusToast.Show("Couldn't decode that image (unsupported format or corrupt file): " + Path.GetFileName(path), true);
            return;
        }

        texture.wrapMode = TextureWrapMode.Clamp;
        texture.filterMode = FilterMode.Bilinear;

        if (entry.material == null || !entry.material.HasProperty(propertyName))
        {
            Destroy(texture);
            Debug.LogError("Selected hair shader has no texture property " + propertyName);
            StatusToast.Show("Current shader has no " + propertyName + " texture slot.", true);
            return;
        }

        entry.material.SetTexture(propertyName, texture);
        SetSlotPath(entry, propertyName, path);
        SetSlotCleared(entry, propertyName, false);

        StatusToast.Show("Loaded " + Path.GetFileName(path));

        // The panel's buttons are ordinary left clicks, which the undo authority already arms
        // on - but it arms on the RELEASE, and a file dialog swallows that release on some
        // platforms. Saying so explicitly costs nothing and does not depend on the gesture.
        UndoHistoryAuthority.NotifyEdit();

        // Edits to the active global material must update every existing card immediately.
        if (GetGlobalMaterialIndex() == selectedMaterialIndex)
        {
            viewer.hairCardMaterial = entry.material;
            ApplyAssignments();
        }

        // Always preview the material currently being edited, even before applying it globally.
        UpdatePreviewForSelectedMaterial();
        RefreshPanel();
    }

    // Empties one texture slot: the shader falls back to its declared default, which for all
    // three of these is white or flat - so clearing the albedo leaves plain hair the master
    // colour can then tint outright, rather than leaving the old texture stuck on the material
    // with no way off it except loading another one.
    //
    // The stored PATH goes with it. Without that the slot would come back on the next project
    // load, which is the version of this that would look like a bug.
    private void ClearTextureSlot(string propertyName)
    {
        if (selectedMaterialIndex < 0 || selectedMaterialIndex >= materials.Count) return;
        HairMaterialEntry entry = materials[selectedMaterialIndex];

        if (entry.material == null || !entry.material.HasProperty(propertyName))
        {
            StatusToast.Show("Current shader has no " + propertyName + " texture slot.", true);
            return;
        }

        // Nothing to say and nothing to do - but say so, because a button that answers a click
        // with silence reads as broken.
        if (entry.material.GetTexture(propertyName) == null && string.IsNullOrEmpty(PathForSlot(entry, propertyName)))
        {
            StatusToast.Show("That slot is already empty.");
            return;
        }

        // The Texture2D itself is deliberately NOT destroyed. It may be the same object another
        // material entry is still pointing at - LoadTextureIntoSlot makes one per load, but a
        // project restore hands the same instance to more than one slot - and destroying a
        // texture out from under a material that is still using it is a black hair card with no
        // error to explain it. Unity collects it when the last reference goes.
        entry.material.SetTexture(propertyName, null);
        SetSlotPath(entry, propertyName, "");
        SetSlotCleared(entry, propertyName, true);

        StatusToast.Show("Cleared the " + LabelForSlot(propertyName) + " texture.");
        UndoHistoryAuthority.NotifyEdit();

        if (GetGlobalMaterialIndex() == selectedMaterialIndex)
        {
            viewer.hairCardMaterial = entry.material;
            ApplyAssignments();
        }

        UpdatePreviewForSelectedMaterial();
        RefreshPanel();
    }

    private static void SetSlotPath(HairMaterialEntry entry, string propertyName, string path)
    {
        if (propertyName == AlbedoProperty) entry.albedoPath = path;
        else if (propertyName == NormalProperty) entry.normalPath = path;
        else if (propertyName == OpacityProperty) entry.opacityPath = path;
    }

    private static void SetSlotCleared(HairMaterialEntry entry, string propertyName, bool cleared)
    {
        if (propertyName == AlbedoProperty) entry.albedoCleared = cleared;
        else if (propertyName == NormalProperty) entry.normalCleared = cleared;
        else if (propertyName == OpacityProperty) entry.opacityCleared = cleared;
    }

    private static string PathForSlot(HairMaterialEntry entry, string propertyName)
    {
        if (propertyName == AlbedoProperty) return entry.albedoPath;
        if (propertyName == NormalProperty) return entry.normalPath;
        if (propertyName == OpacityProperty) return entry.opacityPath;
        return "";
    }

    private static string LabelForSlot(string propertyName)
    {
        if (propertyName == AlbedoProperty) return "Albedo";
        if (propertyName == NormalProperty) return "Normal";
        if (propertyName == OpacityProperty) return "Opacity Mask";
        return propertyName;
    }

    // Opens the containing folder for a loaded texture's source file, with the file itself
    // pre-selected, so the person can find it again without hunting through their filesystem.
    private void LocateTextureFile(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            StatusToast.Show("Nothing loaded into this slot yet.", true);
            return;
        }
        if (!File.Exists(path))
        {
            StatusToast.Show("That file can't be found anymore: " + Path.GetFileName(path), true);
            return;
        }

#if UNITY_EDITOR
        EditorUtility.RevealInFinder(path);
#elif UNITY_STANDALONE_WIN
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
        }
        catch (Exception ex)
        {
            Debug.LogError("Could not open containing folder: " + ex.Message);
            StatusToast.Show("Couldn't open that folder: " + ex.Message, true);
        }
#else
        Debug.LogWarning("Locate containing folder is currently supported in the Editor and standalone Windows builds only.");
        StatusToast.Show("Locate isn't supported on this platform yet.", true);
#endif
    }

    private void UpdatePreviewForSelectedMaterial()
    {
        if (selectedMaterialIndex < 0 || selectedMaterialIndex >= materials.Count) return;
        TextureEditorManager textureEditor = FindFirstObjectByType<TextureEditorManager>();
        textureEditor?.SetPreviewMaterial(materials[selectedMaterialIndex].material);
    }

    // Kept under its historical method name because project restore invokes ApplyAssignments via
    // reflection. Viewer material is now global; currentGroupId is deliberately irrelevant.
    private void SyncViewerMaterialToCurrentGroup()
    {
        if (viewer == null || materials.Count == 0) return;
        int index = GetGlobalMaterialIndex();
        if (index < 0) return;
        viewer.hairCardMaterial = materials[index].material;
    }

    private void ApplyAssignments()
    {
        if (viewer == null || materials.Count == 0) return;
        int index = GetGlobalMaterialIndex();
        if (index < 0) return;
        Material active = materials[index].material;
        if (active == null) return;

        // Hair cards are thin planes that need to render from both sides, and there can be
        // thousands of them - GPU instancing is what actually lets the SRP batcher merge their
        // draw calls instead of issuing one per card. Both are safe to force directly onto this
        // object: it's not a UI preview swatch, it's literally what every card renders with.
        if (active.HasProperty("_Cull")) active.SetFloat("_Cull", 0f);
        active.EnableKeyword("_DOUBLESIDED_ON");
        active.enableInstancing = true;

        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
        {
            // Skip cards that currently own a per-instance material for a genuine reason
            // (an active selection highlight, or an explicit single-sided override) - this
            // runs on a recurring timer, so without this check it would silently erase both
            // every ~0.2s, fighting HairCard's own material bookkeeping every time it ran.
            if (card.HasDivergedMaterial()) continue;
            MeshRenderer renderer = card.GetComponent<MeshRenderer>();
            if (renderer != null && renderer.sharedMaterial != active)
                renderer.sharedMaterial = active;
        }
        viewer.hairCardMaterial = active;
    }

    private string GetCurrentTextureName(string propertyName)
    {
        HairMaterialEntry entry = materials[Mathf.Clamp(selectedMaterialIndex, 0, materials.Count - 1)];
        if (entry.material == null || !entry.material.HasProperty(propertyName)) return "Not available";
        Texture texture = entry.material.GetTexture(propertyName);
        return texture != null ? texture.name : "None";
    }

    private static GameObject CreateContainer(Transform parent, string name, float height)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        go.GetComponent<LayoutElement>().preferredHeight = height;
        VerticalLayoutGroup layout = go.GetComponent<VerticalLayoutGroup>();
        layout.spacing = 2f;
        layout.childControlWidth = true;
        layout.childControlHeight = false;
        layout.childForceExpandHeight = false;
        return go;
    }

    private static void ClearChildren(Transform root)
    {
        for (int i = root.childCount - 1; i >= 0; i--) Destroy(root.GetChild(i).gameObject);
    }

    private static void CreateHeader(Transform parent, string text, float height)
    {
        GameObject go = new GameObject(text, typeof(RectTransform), typeof(TMPro.TextMeshProUGUI), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        go.GetComponent<LayoutElement>().preferredHeight = height;
        TMPro.TextMeshProUGUI tmp = go.GetComponent<TMPro.TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = 15f;
        tmp.fontStyle = TMPro.FontStyles.Bold;
        tmp.color = new Color(.35f, .75f, 1f);
        tmp.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
    }

    private static TMPro.TextMeshProUGUI CreateSubLabel(Transform parent, string text, float height)
    {
        GameObject go = new GameObject("Label", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        go.GetComponent<LayoutElement>().preferredHeight = height;
        TMPro.TextMeshProUGUI tmp = go.GetComponent<TMPro.TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = 11f;
        tmp.color = Color.white;
        tmp.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
        tmp.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
        return tmp;
    }

    private static void CreateSmallButton(Transform parent, string label, UnityEngine.Events.UnityAction action, float width, float height)
    {
        GameObject go = new GameObject(label + "Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        LayoutElement le = go.GetComponent<LayoutElement>();
        le.preferredWidth = width; le.minWidth = width; le.preferredHeight = height; le.minHeight = height;
        go.GetComponent<Image>().color = new Color(.20f, .50f, .82f);
        go.GetComponent<Button>().onClick.AddListener(action);
        AddButtonText(go.transform, label, 10f);
    }

    private static void CreateActionButton(Transform parent, string label, UnityEngine.Events.UnityAction action, float height)
    {
        GameObject go = new GameObject(label + "Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        go.GetComponent<Image>().color = new Color(.20f, .50f, .82f);
        LayoutElement le = go.GetComponent<LayoutElement>();
        le.minHeight = height; le.preferredHeight = height;
        go.GetComponent<Button>().onClick.AddListener(action);
        AddButtonText(go.transform, label, 10f);
    }

    private static void AddButtonText(Transform parent, string label, float fontSize)
    {
        GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI));
        textGO.transform.SetParent(parent, false);
        RectTransform rect = textGO.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = Vector2.zero; rect.offsetMax = Vector2.zero;
        TMPro.TextMeshProUGUI tmp = textGO.GetComponent<TMPro.TextMeshProUGUI>();
        tmp.text = label; tmp.fontSize = fontSize; tmp.fontStyle = TMPro.FontStyles.Bold;
        tmp.alignment = TMPro.TextAlignmentOptions.Center; tmp.color = Color.white; tmp.raycastTarget = false;
    }

    private void OnDestroy()
    {
        foreach (HairMaterialEntry e in materials) if (e.material != null) Destroy(e.material);
    }
}