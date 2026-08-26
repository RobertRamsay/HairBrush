using TMPro;
using UnityEngine;
using UnityEngine.UI;

// The DEL button on a group row, and the two-step confirmation behind it.
//
// WHY THIS EXISTS AT ALL. Deleting a group was right-click on the group's name, and in a shipped
// build it did nothing whatsoever: ModelViewer.PromptDeleteGroup was a single EditorUtility
// .DisplayDialog wrapped in #if UNITY_EDITOR, so the whole gesture compiled out of the player and
// the right-click landed on an empty method. It worked in the editor, which is why it survived.
// The same trap took the shift-drag new-group prompt, which is still editor-only for the same
// reason and should be looked at next.
//
// So the confirmation had to become a runtime thing. This is the arm-then-confirm shape rather
// than a modal card:
//
//   * It needs no backdrop, and a backdrop is the part that goes wrong. DemoUpgradePrompt has
//     five separate places that have to check IsOpen because a raycast blocker does not stop code
//     that reads Mouse.current directly, and every one of them was found the hard way.
//   * The button IS the prompt, so the question is asked exactly where the answer is given -
//     no travelling to a dialog in the middle of the screen and back.
//   * It reads identically for the button and for the right-click, because both simply arm this
//     component and the second one confirms.
//
// The arm times out on its own. An armed delete that waited forever would be a loaded gun sitting
// on the row: come back to the panel five minutes later, click what you think is an ordinary
// button, and a group goes. Six seconds is long enough to read the armed word and act, short
// enough that it cannot survive an interruption.
//
// Undo still covers the delete either way - it is a saveable change like any other, and
// ModelViewer.DeleteGroupAndCardsConfirmed tells UndoHistoryAuthority about it explicitly because
// a right click arms nothing on its own - so this is a guard against the accident, not a
// substitute for CTRL+Z.
//
// The LAST group cannot be deleted at all. See ModelViewer.CanDeleteGroup for what deleting it
// would leave behind.
public class GroupDeleteButton : MonoBehaviour
{
    // How long an armed delete stays armed. See the class comment.
    private const float ArmedSeconds = 6f;

    private static readonly Color IdleColour = new Color(.42f, .24f, .24f, 1f);
    private static readonly Color ArmedColour = new Color(.86f, .22f, .18f, 1f);

    private ModelViewer viewer;
    private int groupId = -1;
    private Image image;
    private TextMeshProUGUI label;

    private bool armed;
    private float armedUntil;

    public int GroupId
    {
        get { return groupId; }
    }

    public void Bind(ModelViewer owner, int gid, Image background, TextMeshProUGUI text)
    {
        viewer = owner;
        groupId = gid;
        image = background;
        label = text;
        armed = false;
        armedUntil = 0f;
        Repaint();
    }

    // The click, and the right-click on the group's name, are the same gesture arriving by two
    // routes: the first one asks, the second one answers.
    public void Press()
    {
        if (armed)
        {
            armed = false;
            Repaint();

            if (viewer == null) return;

            // The confirmation toast is still on screen with its six seconds running, asking a
            // question that has just been answered. Replacing it is not decoration: StatusToast
            // shows one message at a time, so without this the row is gone and the panel is still
            // asking whether to delete it.
            string name = viewer.GroupDisplayName(groupId);
            bool allowed = viewer.CanDeleteGroup(groupId);

            viewer.DeleteGroupAndCardsConfirmed(groupId);

            // DeleteGroupAndCardsConfirmed puts up its own refusal when it will not delete, so
            // only the success case has anything left to say.
            if (allowed) StatusToast.Show(name + " deleted. CTRL+Z brings it back.", false, 3f);
            return;
        }

        Arm();
    }

    public void Arm()
    {
        // Refused BEFORE anything is armed or disarmed. Arming a delete that is going to be turned
        // down asks a question with only one answer, turns the button red for six seconds to do
        // it, and would disarm whatever the user had legitimately armed on another row.
        if (viewer != null && !viewer.CanDeleteGroup(groupId))
        {
            StatusToast.Show(ModelViewer.OnlyGroupRefusal, true);
            return;
        }

        // Only one row may be armed at a time. Two armed DELs sitting on adjacent rows is exactly
        // the picture in which somebody confirms the wrong one.
        DisarmAllOthers();

        armed = true;
        armedUntil = Time.unscaledTime + ArmedSeconds;
        Repaint();

        string name = "this group";
        if (viewer != null) name = viewer.GroupDisplayName(groupId);
        StatusToast.Show("Delete " + name + "? Click DEL again to confirm.", false, ArmedSeconds);
    }

    public void Disarm()
    {
        if (!armed) return;
        armed = false;
        Repaint();
    }

    private void DisarmAllOthers()
    {
        foreach (GroupDeleteButton other in FindObjectsByType<GroupDeleteButton>(FindObjectsSortMode.None))
        {
            if (other == null || other == this) continue;
            other.Disarm();
        }
    }

    private void Update()
    {
        if (!armed) return;
        if (Time.unscaledTime < armedUntil) return;

        armed = false;
        Repaint();
    }

    // The row can be destroyed out from under an armed delete - + GROUP, an undo, a project load
    // all rebuild the whole list. The button goes with it and the gesture fails safe, but the
    // six-second toast does not: it would sit there asking about a row that no longer exists, and
    // the next DEL press would re-arm rather than answer it. Say so instead.
    private void OnDestroy()
    {
        if (!armed) return;
        armed = false;

        // Only when THIS object is being destroyed, not when the whole scene is going away.
        // StatusToast.Ensure builds a Canvas and a label if its own singleton has already been
        // torn down, and spawning a GameObject hierarchy from OnDestroy during shutdown leaks it
        // and earns Unity's "Some objects were not cleaned up when closing the scene" warning -
        // to show a message nobody is there to read.
        if (!gameObject.scene.isLoaded) return;

        StatusToast.Show("Delete cancelled - the group list changed.", false, 2f);
    }

    private void Repaint()
    {
        if (label != null)
        {
            // "SURE?" rather than "CONFIRM", and nothing here touches fontSize.
            //
            // The label is built at 13 (see ModelViewer, where the button is made) and
            // PanelTypographyScale then pins it at 15 - it caches the first size it sees and
            // force-writes it every LateUpdate, so a size set from here would survive less than a
            // frame.
            //
            // Uppercase at 15pt bold runs about 10px a character in this font, so "CONFIRM" wants
            // roughly 72px against a 56px button and "SURE?" about 51. Six characters would be
            // borderline; seven are not. The button carries the short word and the toast carries
            // the whole sentence.
            string text = "DEL";
            if (armed) text = "SURE?";
            if (label.text != text) label.text = text;
        }

        if (image != null)
        {
            Color colour = IdleColour;
            if (armed) colour = ArmedColour;
            if (image.color != colour) image.color = colour;
        }
    }
}
