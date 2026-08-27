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
    private GameObject nextButton;
    private GameObject backButton;
    private GameObject mirrorButton;
    private GameObject processButton;

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

    void Refresh()
    {
        int paired = RemapMarkerSet.CountPaired(session.Markers);
        string reason;
        bool covered = RemapMarkerSet.CoverageSatisfied(session.Markers, out reason);

        bool auto = session.Phase == RemapPhase.AutoMarkers;
        if (auto) title.text = "REMAP  -  STEP 1 OF 2:  MATCH THE NUMBERED MARKERS";
        if (!auto) title.text = "REMAP  -  STEP 2 OF 2:  PIN BOTH EARS";

        if (auto)
        {
            status.text = "Click the same spot on the new head for each number. " + paired + " of " + session.Markers.Count + " pairs matched. Drag any marker on either head to adjust it.";
        }
        if (!auto)
        {
            string detail = "Ready to process.";
            if (!covered) detail = "Still needed: " + reason + ".";
            status.text = "Place each ear slot on the HEAD behind the ear, not on the ear itself. " + detail;
        }

        nextButton.SetActive(auto);
        backButton.SetActive(!auto);
        mirrorButton.SetActive(!auto);
        processButton.SetActive(!auto);

        // Dead rather than hidden. A button that vanishes reads as a bug; one that is visibly
        // disabled next to a status line naming what is missing reads as an instruction.
        Button process = processButton.GetComponent<Button>();
        process.interactable = covered;
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
        session.GoToPhase(RemapPhase.Ready);
        StatusToast.Show("Markers accepted. The solve and the projection are the next thing to build - nothing has moved yet.");
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
        barRect.offsetMin = new Vector2(0f, -74f);
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

        GameObject textColumn = new GameObject("TextColumn", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(LayoutElement));
        textColumn.transform.SetParent(bar.transform, false);
        textColumn.GetComponent<LayoutElement>().preferredWidth = 900f;
        VerticalLayoutGroup column = textColumn.GetComponent<VerticalLayoutGroup>();
        column.childControlHeight = false;
        column.childForceExpandHeight = false;
        column.spacing = 2f;

        title = AddText(textColumn.transform, "PhaseTitle", 19, FontStyles.Bold, new Color(.94f, .96f, .98f), 24f);
        status = AddText(textColumn.transform, "PhaseStatus", 15, FontStyles.Normal, new Color(.72f, .78f, .84f), 22f);

        backButton = AddButton(bar.transform, "RemapPhaseBack", "BACK", new Color(.24f, .30f, .38f), OnBack);
        mirrorButton = AddButton(bar.transform, "RemapPhaseMirror", "MIRROR L TO R", new Color(.28f, .36f, .46f), OnMirror);
        nextButton = AddButton(bar.transform, "RemapPhaseNext", "NEXT: EAR MARKERS", new Color(.20f, .44f, .34f), OnNext);
        processButton = AddButton(bar.transform, "RemapPhaseProcess", "PROCESS", new Color(.20f, .44f, .34f), OnProcess);
        AddButton(bar.transform, "RemapPhaseCancel", "CANCEL", new Color(.44f, .26f, .22f), OnCancel);
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
