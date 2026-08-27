using TMPro;
using UnityEngine;
using UnityEngine.UI;

// The strip across the top of a REMAP session: where you are, what is still missing, and the
// buttons that move you on.
//
// Built and destroyed with the session rather than hidden, so nothing of it survives a cancel to
// be found by UIThemeAuthority's every-button sweep on the next groom.
//
// The status line is the useful half. The gate on PROCESS is coverage, not a count - the
// automatic set matched plus a pinned pair behind each ear - and a user who is one marker short
// needs to be told WHICH, not just that the button is dead.
[DefaultExecutionOrder(9740)]
public class RemapPhaseBar : MonoBehaviour
{
    private RemapSessionController session;
    private GameObject root;
    private TextMeshProUGUI title;
    private TextMeshProUGUI status;
    private TextMeshProUGUI progress;
    private GameObject nextButton;
    private GameObject backButton;
    private GameObject mirrorButton;
    private GameObject processButton;
    private GameObject revertButton;
    private TextMeshProUGUI toneLabel;
    private Image toneFill;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<RemapPhaseBar>() != null) return;
        GameObject go = new GameObject("RemapPhaseBar");
        DontDestroyOnLoad(go);
        go.AddComponent<RemapPhaseBar>();
    }

    void Update()
    {
        if (session == null) session = RemapSessionController.Instance;
        if (session == null) return;

        if (!session.SessionActive)
        {
            if (root != null)
            {
                Destroy(root);
                root = null;
            }
            return;
        }

        if (root == null) Build();
        Refresh();
    }

    // Everything the user needs to keep going lives here rather than in a toast.
    //
    // A StatusToast is the wrong carrier for an instruction that stays true for the next twenty
    // clicks - it is gone before it has been read, and re-reading it is impossible. So the bar
    // carries the running count and the name of the marker being placed right now, refreshed
    // every frame; toasts are kept for things that actually are events, like a mirror landing or
    // a side mismatch being refused.
    void Refresh()
    {
        string reason;
        bool covered = RemapMarkerSet.CoverageSatisfied(session.Markers, out reason);

        int done;
        int total;
        RemapMarkerSet.PhaseProgress(session.Markers, session.Phase, out done, out total);
        progress.text = done + "/" + total;

        Color progressColour = new Color(.98f, .84f, .36f);
        if (done >= total && total > 0) progressColour = new Color(.42f, 1f, .55f);
        progress.color = progressColour;

        bool ready = session.Phase == RemapPhase.Ready;
        bool auto = session.Phase == RemapPhase.AutoMarkers;

        if (ready)
        {
            title.text = "REMAP  -  PREVIEW:  YOUR GROOM IS ON THE NEW HEAD";
            progress.text = "";
            RemapProjectionReport report = session.PreviewReport;
            string detail = "";
            if (report != null) detail = report.ToString();
            status.text = "Nothing has been saved. REVERT puts it back on the original head; CANCEL leaves the whole session with your groom untouched.   " + detail;
        }
        if (!ready)
        {
            if (auto) title.text = "REMAP  -  STEP 1 OF 2:  MATCH THE NUMBERED MARKERS";
            if (!auto) title.text = "REMAP  -  STEP 2 OF 2:  PIN BOTH EARS";
            status.text = NextInstruction(auto, covered, reason);
        }

        nextButton.SetActive(auto);
        backButton.SetActive(!auto && !ready);
        mirrorButton.SetActive(!auto && !ready);
        processButton.SetActive(!auto && !ready);
        revertButton.SetActive(ready);

        // Dead rather than hidden. A button that vanishes reads as a bug; one that is visibly
        // disabled next to a status line naming what is missing reads as an instruction.
        Button process = processButton.GetComponent<Button>();
        process.interactable = covered;
    }

    // Names the marker being placed and which head it goes on. In the ear phase that name is the
    // whole point: "place 13" is useless, "13: L LOBE - below the lobe attachment, on the head" is
    // the instruction, and it has to be readable for as long as it takes to find the spot.
    string NextInstruction(bool auto, bool covered, string reason)
    {
        int index = RemapMarkerSet.NextUnplaced(session.Markers, session.Phase, false);
        bool onTarget = false;
        if (index < 0)
        {
            index = RemapMarkerSet.NextUnplaced(session.Markers, session.Phase, true);
            onTarget = true;
        }

        if (index < 0)
        {
            if (auto) return "All matched. Drag any marker on either head to adjust, then go on to the ears.";
            if (covered) return "Both ears pinned. Ready to process.";
            return "Still needed: " + reason + ".";
        }

        RemapMarker marker = session.Markers[index];
        string head = "the ORIGINAL head, on the left";
        if (onTarget) head = "the NEW head, on the right";

        string detail = marker.description;
        if (auto) detail = "the same spot you see marker " + marker.label + " on";

        return "Place " + marker.label + " on " + head + "  -  " + detail + ".   Drag any placed marker to adjust it.";
    }

    void OnNext()
    {
        session.GoToPhase(RemapPhase.EarMarkers);
        StatusToast.Show("Now pin the ear markers - three per side, on the head behind the ear.");
    }

    void OnBack()
    {
        session.GoToPhase(RemapPhase.AutoMarkers);
    }

    void OnMirror()
    {
        int moved = session.MirrorEarMarkers();
        if (moved == 0)
        {
            StatusToast.Show("Place the LEFT ear markers first, then mirror them across.", true);
            return;
        }
        StatusToast.Show("Mirrored " + moved + " placement(s) to the right side. Nudge any that do not sit right - a scanned head is never quite symmetric.");
    }

    void OnProcess()
    {
        int mismatched;
        if (RemapMarkerSet.TryFindSideMismatch(session.Markers, session.SourceRoot, session.TargetRoot, out mismatched))
        {
            StatusToast.Show("Marker " + mismatched + " is on the left of one head and the right of the other. Fix it before processing, or the groom will fold inside out.", true);
            return;
        }
        string failure;
        if (!session.RunPreview(out failure))
        {
            StatusToast.Show("Could not solve the warp: " + failure + ".", true);
            return;
        }

        RemapProjectionReport report = session.PreviewReport;
        if (report != null && report.failed > 0)
        {
            StatusToast.Show(report.failed + " anchor(s) could not be placed on the new head and were left where the warp put them. " + report, true);
            return;
        }
        StatusToast.Show("Preview applied. " + report);
    }

    void OnRevert()
    {
        session.RevertPreview();
        StatusToast.Show("Put back on the original head. Adjust the markers and process again.");
    }

    void OnCancel()
    {
        session.End(true);
        StatusToast.Show("REMAP cancelled. Your groom is untouched.");
    }

    void Build()
    {
        root = new GameObject("RemapPhaseBarCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        root.transform.SetParent(transform, false);
        Canvas canvas = root.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        // Over the marker numbers at 4000, under the import prompt at 5000.
        canvas.sortingOrder = 4500;
        CanvasScaler scaler = root.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);

        GameObject bar = new GameObject("Bar", typeof(RectTransform), typeof(Image), typeof(HorizontalLayoutGroup));
        bar.transform.SetParent(root.transform, false);
        RectTransform barRect = bar.GetComponent<RectTransform>();
        barRect.anchorMin = new Vector2(0f, 1f);
        barRect.anchorMax = new Vector2(1f, 1f);
        barRect.pivot = new Vector2(.5f, 1f);
        barRect.offsetMin = new Vector2(0f, -88f);
        barRect.offsetMax = new Vector2(0f, 0f);
        bar.GetComponent<Image>().color = new Color(.11f, .13f, .16f, .94f);
        HorizontalLayoutGroup layout = bar.GetComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(18, 18, 8, 8);
        layout.spacing = 10f;
        layout.childControlWidth = false;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = true;
        layout.childAlignment = TextAnchor.MiddleLeft;

        // The running count, big and on its own. It sits outside the text column precisely so it
        // can never be the thing that gets ellipsised away when the instruction runs long.
        progress = AddText(bar.transform, "PhaseProgress", 30, FontStyles.Bold, new Color(.98f, .84f, .36f), 44f);
        progress.alignment = TextAlignmentOptions.Center;
        LayoutElement progressLayout = progress.gameObject.GetComponent<LayoutElement>();
        progressLayout.preferredWidth = 116f;

        GameObject textColumn = new GameObject("TextColumn", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(LayoutElement));
        textColumn.transform.SetParent(bar.transform, false);
        textColumn.GetComponent<LayoutElement>().preferredWidth = 620f;
        VerticalLayoutGroup column = textColumn.GetComponent<VerticalLayoutGroup>();
        column.childControlHeight = false;
        column.childForceExpandHeight = false;
        column.spacing = 2f;

        title = AddText(textColumn.transform, "PhaseTitle", 18, FontStyles.Bold, new Color(.94f, .96f, .98f), 24f);
        status = AddText(textColumn.transform, "PhaseStatus", 15, FontStyles.Normal, new Color(.78f, .84f, .90f), 34f);
        // The instruction is the one line allowed to wrap. Left on NoWrap it was silently
        // ellipsised at the column edge, which is how a status line ends up hiding the very thing
        // it was added to say.
        status.textWrappingMode = TextWrappingModes.Normal;

        BuildToneControl(bar.transform);

        backButton = AddButton(bar.transform, "RemapPhaseBack", "BACK", new Color(.24f, .30f, .38f), OnBack);
        mirrorButton = AddButton(bar.transform, "RemapPhaseMirror", "MIRROR L TO R", new Color(.28f, .36f, .46f), OnMirror);
        nextButton = AddButton(bar.transform, "RemapPhaseNext", "NEXT: EAR MARKERS", new Color(.20f, .44f, .34f), OnNext);
        processButton = AddButton(bar.transform, "RemapPhaseProcess", "PROCESS", new Color(.20f, .44f, .34f), OnProcess);
        revertButton = AddButton(bar.transform, "RemapPhaseRevert", "REVERT", new Color(.40f, .34f, .22f), OnRevert);
        AddButton(bar.transform, "RemapPhaseCancel", "CANCEL", new Color(.44f, .26f, .22f), OnCancel);
    }

    // MARKER TONE: pure greyscale, white at one end and black at the other.
    //
    // Built the way every other runtime slider in the project is (see
    // PlacementBrushModeAuthority's row builder) - background, fill area, handle - rather than
    // from a prefab, because there is no prefab. The fill is drawn in the tone itself, so the
    // control previews what it is about to do to the markers.
    //
    // Present in every phase. Whichever head is loaded decides what reads, and the user finds that
    // out while placing, not before starting.
    void BuildToneControl(Transform parent)
    {
        GameObject column = new GameObject("ToneColumn", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(LayoutElement));
        column.transform.SetParent(parent, false);
        column.GetComponent<LayoutElement>().preferredWidth = 186f;
        VerticalLayoutGroup layout = column.GetComponent<VerticalLayoutGroup>();
        layout.childControlHeight = false;
        layout.childForceExpandHeight = false;
        layout.spacing = 3f;
        layout.padding = new RectOffset(0, 8, 6, 0);

        toneLabel = AddText(column.transform, "ToneLabel", 13, FontStyles.Bold, new Color(.72f, .78f, .84f), 18f);

        GameObject sliderObject = new GameObject("RemapMarkerToneSlider", typeof(RectTransform), typeof(Slider), typeof(LayoutElement));
        sliderObject.transform.SetParent(column.transform, false);
        sliderObject.GetComponent<LayoutElement>().preferredHeight = 18f;
        Slider slider = sliderObject.GetComponent<Slider>();
        slider.minValue = 0f;
        slider.maxValue = 1f;

        GameObject background = new GameObject("Background", typeof(RectTransform), typeof(Image));
        background.transform.SetParent(sliderObject.transform, false);
        background.GetComponent<Image>().color = new Color(.28f, .28f, .28f);
        RectTransform backgroundRect = background.GetComponent<RectTransform>();
        backgroundRect.anchorMin = new Vector2(0f, .3f);
        backgroundRect.anchorMax = new Vector2(1f, .7f);
        backgroundRect.offsetMin = Vector2.zero;
        backgroundRect.offsetMax = Vector2.zero;

        GameObject fillArea = new GameObject("Fill Area", typeof(RectTransform));
        fillArea.transform.SetParent(sliderObject.transform, false);
        RectTransform fillAreaRect = fillArea.GetComponent<RectTransform>();
        fillAreaRect.anchorMin = new Vector2(0f, .3f);
        fillAreaRect.anchorMax = new Vector2(1f, .7f);
        fillAreaRect.offsetMin = Vector2.zero;
        fillAreaRect.offsetMax = Vector2.zero;

        GameObject fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
        fill.transform.SetParent(fillArea.transform, false);
        toneFill = fill.GetComponent<Image>();
        slider.fillRect = fill.GetComponent<RectTransform>();
        slider.fillRect.anchorMin = Vector2.zero;
        slider.fillRect.anchorMax = Vector2.zero;
        slider.fillRect.sizeDelta = Vector2.zero;

        GameObject handleArea = new GameObject("Handle Slide Area", typeof(RectTransform));
        handleArea.transform.SetParent(sliderObject.transform, false);
        RectTransform handleAreaRect = handleArea.GetComponent<RectTransform>();
        handleAreaRect.anchorMin = Vector2.zero;
        handleAreaRect.anchorMax = Vector2.one;
        handleAreaRect.offsetMin = Vector2.zero;
        handleAreaRect.offsetMax = Vector2.zero;

        GameObject handle = new GameObject("Handle", typeof(RectTransform), typeof(Image));
        handle.transform.SetParent(handleArea.transform, false);
        handle.GetComponent<Image>().color = new Color(.86f, .88f, .92f);
        slider.handleRect = handle.GetComponent<RectTransform>();
        slider.handleRect.sizeDelta = new Vector2(16f, 0f);

        // Set AFTER the parts are wired, so the first assignment lays the fill out properly, and
        // the listener is attached afterwards so restoring the saved value does not write it
        // straight back to disk.
        slider.value = RemapMarkerAuthority.MarkerTone;
        ApplyTone(slider.value);
        slider.onValueChanged.AddListener(ApplyTone);
    }

    void ApplyTone(float value)
    {
        RemapMarkerAuthority.MarkerTone = value;
        if (toneFill != null) toneFill.color = new Color(value, value, value, 1f);
        if (toneLabel != null) toneLabel.text = "MARKER TONE  " + Mathf.RoundToInt(value * 100f) + "%";
    }

    static TextMeshProUGUI AddText(Transform parent, string name, int size, FontStyles style, Color colour, float height)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        TextMeshProUGUI text = go.GetComponent<TextMeshProUGUI>();
        text.fontSize = size;
        text.fontStyle = style;
        text.color = colour;
        text.alignment = TextAlignmentOptions.Left;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.raycastTarget = false;
        go.GetComponent<LayoutElement>().preferredHeight = height;
        return text;
    }

    static GameObject AddButton(Transform parent, string name, string label, Color colour, UnityEngine.Events.UnityAction onClick)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        go.GetComponent<Image>().color = colour;
        LayoutElement element = go.GetComponent<LayoutElement>();
        element.preferredWidth = 168f;
        element.preferredHeight = 40f;

        GameObject textObject = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textObject.transform.SetParent(go.transform, false);
        RectTransform textRect = textObject.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;
        TextMeshProUGUI text = textObject.GetComponent<TextMeshProUGUI>();
        text.text = label;
        text.fontSize = 15;
        text.fontStyle = FontStyles.Bold;
        text.color = Color.white;
        text.alignment = TextAlignmentOptions.Center;
        text.raycastTarget = false;

        go.GetComponent<Button>().onClick.AddListener(onClick);
        return go;
    }
}
