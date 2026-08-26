using System.Reflection;
using UnityEngine;

// "Is the Texture Editor open?", asked in one place so every caller gets the same answer.
//
// Every viewport preview in this project has to stand down while the texture workspace is up. That
// workspace parks the camera front-on against a texture preview plane and expects to have the view
// to itself, and it does not get it for free: the plane sits at z = 1.5 while the model and every
// ring and curve hanging off it sit at the origin, with the camera at z = -3 looking down +z. So
// the previews are all IN FRONT of the plane. Nothing is hidden by it, depth test or no depth test
// - a guide left drawing there lays its curve straight across the atlas.
//
// Seven previews needed this test. Three had written their own copy of it and four had none.
//
// ModelViewer.isTextureEditorMode is the answer, and it is private, so reflection is the way in -
// the same way half a dozen scripts already reached it. The obvious alternative, testing whether
// TextureEditorPanel is active, is strictly worse here: the panel is created lazily the first time
// the editor is opened, so a session that never opens it would have nothing to find and would go
// on searching the whole scene for it forever. It would also answer no later than the flag does
// and never sooner - SwitchEditorMode sets the flag and only then activates the panel, and it is
// the only thing in the project that activates it at all.
public static class TextureModeProbe
{
    private static ModelViewer viewer;
    private static FieldInfo flagField;

    // Statics do not clear themselves when Disable Domain Reload is on, and viewer holds a
    // reference into a scene that no longer exists.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        viewer = null;
        flagField = null;
    }

    public static bool Active
    {
        get
        {
            if (viewer == null)
            {
                viewer = Object.FindFirstObjectByType<ModelViewer>();
                flagField = null;
            }
            if (viewer == null) return false;

            if (flagField == null)
            {
                flagField = typeof(ModelViewer).GetField("isTextureEditorMode",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            }
            if (flagField == null) return false;

            // Read fresh every time rather than cached per frame. The flag flips inside a button
            // click, at execution order 0, and PlacementBrushModeAuthority asks this question at
            // order -5000 - before that click has happened. A frame-cached answer would hand that
            // authority's stale reading to everyone who asked later in the same frame.
            return flagField.GetValue(viewer) is bool active && active;
        }
    }
}
