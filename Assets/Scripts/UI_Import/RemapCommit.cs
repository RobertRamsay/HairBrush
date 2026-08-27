using System.IO;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

// CONFIRM: make the remapped groom, on the new head, the session.
//
// The obvious implementation is the wrong one. Swapping ModelViewer.loadedModel for the target
// head by hand would take about four lines and destroy the groom in the process: fifteen
// authorities poll that field for reference identity and treat a change as "new session, clear my
// state". SessionModifierFreshStartAuthority would delete the very clumpers the remap just moved;
// GroupPredeterminedUVLifecycle would clear the UV settings; PostLoadSelectionReset, the shape
// curve bridges and TextureUVRectWorkspace would all follow.
//
// The one route where every one of them behaves is a real project load, because that is the path
// their pending-restore guards were written for. So CONFIRM writes a project file describing the
// remapped groom against the NEW model, and then loads it. The swap is a side effect of the load
// rather than something this code has to arrange.
//
// It saves rather than saving-in-place on purpose - Bob's own framing: a new file, so the new
// model is the current one and the old is forgotten, with the original project still on disk
// untouched if the remap turns out to have been a mistake.
public static class RemapCommit
{
    public static bool Confirm(RemapSessionController session, out string failure)
    {
        failure = string.Empty;
        if (session == null || !session.SessionActive)
        {
            failure = "no REMAP session is running";
            return false;
        }
        if (!session.PreviewApplied)
        {
            failure = "nothing has been processed yet";
            return false;
        }

        string modelPath = session.TargetSourcePath;
        if (string.IsNullOrEmpty(modelPath))
        {
            failure = "the new head has no source path recorded";
            return false;
        }

        RuntimeNavigationProjectIO io = Object.FindFirstObjectByType<RuntimeNavigationProjectIO>();
        if (io == null)
        {
            failure = "the project save/load service is not available";
            return false;
        }

        string savePath = ChooseSavePath();
        // A cancelled save leaves the session exactly as it was, still previewing, still
        // revertable. Cancelling a file dialog should never be the thing that commits or discards
        // an edit.
        if (string.IsNullOrEmpty(savePath)) return false;

        HairProjectSaveData data = io.BuildSaveData();
        if (data == null)
        {
            failure = "the session could not be captured";
            return false;
        }

        // Built from the live scene, which is already remapped - so the cards, guides, clumpers
        // and POSTs in this payload are at their NEW positions. Only the model identity has to be
        // corrected, because BuildSaveData read it from the head still installed in the viewer.
        //
        // Every card also carries its frozen identity in this payload, so the variance, the
        // predetermined UV rectangles and the clump leaders all come back on load exactly as they
        // were authored on the original head. That is the whole point of the identity fields.
        data.modelPath = modelPath;
        data.importMetadata = session.TargetImportMetadata();

        try
        {
            File.WriteAllText(savePath, JsonUtility.ToJson(data, true));
        }
        catch (System.Exception exception)
        {
            Debug.LogException(exception);
            failure = "the project could not be written";
            return false;
        }

        // Order matters. CommitPreview first, so End stops treating the remap as something to undo;
        // End second, to put the layers, the culling masks, the camera rect and the input lock back
        // and to destroy the session's copy of the new head; the load last, which imports that head
        // again cleanly and rebuilds the whole session around it.
        //
        // And the load waits a frame. End's Destroy calls are deferred to the end of this one, so a
        // load running immediately would rebuild the session around objects that are already
        // condemned but still findable - which is exactly why the groom came up missing until the
        // project was reloaded by hand.
        session.CommitPreview();
        session.End(true);
        session.LoadProjectNextFrame(io, savePath);

        Debug.Log("HairBrush REMAP: confirmed onto " + Path.GetFileName(modelPath) + ", saved as " + savePath);
        return true;
    }

    static string ChooseSavePath()
    {
#if UNITY_EDITOR
        return EditorUtility.SaveFilePanel("Save Remapped Project", "", "HairProject_remapped", "json");
#else
        return RuntimeFileDialog.SaveFile("Save Remapped Project", "HairBrush Projects\0*.json\0All Files\0*.*\0\0", "HairProject_remapped", "json");
#endif
    }
}
