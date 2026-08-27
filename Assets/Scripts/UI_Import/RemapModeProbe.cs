using UnityEngine;

// Is a REMAP session up?
//
// Mirrors TextureModeProbe, and for the same reason: a groom-space overlay drawn during a mode
// that owns the viewport is drawn in the wrong place, over the wrong model, or on top of a
// second camera's half of the screen. The two are asked together often enough that
// GroomViewportSuppressed exists to ask both at once.
//
// A plain static rather than a reflected field: REMAP is new code, so there is no private state
// to reach into and no reason to invent any.
public static class RemapModeProbe
{
    private static bool active;

    public static bool Active { get { return active; } }

    public static void SetActive(bool value)
    {
        active = value;
    }

    // Domain reload can leave a static true from a previous play session, which would come up in
    // a mode with no session behind it and suppress the whole groom viewport. Same guard
    // TextureModeProbe carries.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        active = false;
    }
}

// "Should groom-space overlays stand down right now."
//
// Every ring, handle, banner and brush preview in the project used to ask TextureModeProbe.Active
// directly. There are two viewport-owning modes now, and there will be more; asking one question
// means a third mode is one line here rather than another sweep through nine files.
public static class GroomViewportSuppressed
{
    public static bool Active
    {
        get
        {
            if (TextureModeProbe.Active) return true;
            if (RemapModeProbe.Active) return true;
            return false;
        }
    }
}
