using System;
using System.Runtime.InteropServices;
using UnityEngine;

// Puts DEMO in the Windows title bar of a demo build, and does nothing at all in a PRO one.
//
// Unity has no runtime API for this. Application.productName is what the player uses to title
// its window and it is read-only once built, so the only way to change it is to ask Windows
// directly - which this project already does for file dialogs, so user32 is not a new dependency
// here. RuntimeFileDialog calls the same GetActiveWindow, though with a guarantee this does not
// have: it asks inside a click, when the app is by definition in the foreground, while this asks
// on a timer and can legitimately be handed nothing. See the two clocks below.
//
// Why bother, when the file itself is renamed _DEMO: the file name stops being visible the
// moment the thing is running. Somebody with both builds installed, or reporting a problem from
// a screenshot, has nothing else on screen to say which one they are looking at. The title bar
// is always there.
[DefaultExecutionOrder(9800)]
public class DemoWindowTitleAuthority : MonoBehaviour
{
    // Unity writes the window title itself during startup, and not on a frame this can predict -
    // it has been observed both before and after the first Update depending on display setup and
    // resolution changes. So the title is re-asserted for a while rather than set once and hoped
    // for, and then the component switches itself off: a permanent every-frame P/Invoke to hold a
    // string nothing else writes is a poll with no reason to exist once startup has settled.
    //
    // TWO clocks, and the difference matters. GetActiveWindow returns NULL whenever this thread
    // has no active window - which includes every frame the app is not in the foreground. Somebody
    // who launches the demo and alt-tabs away while Unity loads, then comes back, would have had
    // every attempt in a fixed ten-second window return NULL, and the title bar would say nothing
    // about DEMO for the rest of the session. So the short clock only starts counting once the
    // title has actually been written at least once; until then the long one applies.
    private const float ReassertAfterFirstWrite = 10f;
    private const float GiveUpAfterSeconds = 600f;
    private const float ReassertInterval = 1f;

    private float nextAssert;
    private float firstWriteAt = -1f;

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SetWindowTextW")]
    private static extern bool SetWindowText(IntPtr hwnd, string text);
#endif

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        // A PRO build spawns nothing: no object, no Update, no call into user32, and the title
        // bar is left exactly as Unity wrote it. The two DllImports below are still compiled into
        // a Windows PRO build - they are guarded by platform, not by edition - but nothing on any
        // path reaches them, and RuntimeFileDialog imports user32 regardless.
        if (!BuildEdition.IsDemo) return;
        if (FindFirstObjectByType<DemoWindowTitleAuthority>() != null) return;

        GameObject go = new GameObject("DemoWindowTitleAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<DemoWindowTitleAuthority>();
    }

    void Update()
    {
        bool written = firstWriteAt >= 0f;
        if (written && Time.unscaledTime > firstWriteAt + ReassertAfterFirstWrite)
        {
            enabled = false;
            return;
        }

        // Never wrote it, and long past the point where anything is still starting up. The window
        // handle is not coming, and something about this machine is not what this expected.
        if (!written && Time.unscaledTime > GiveUpAfterSeconds)
        {
            enabled = false;
            return;
        }

        if (Time.unscaledTime < nextAssert) return;
        nextAssert = Time.unscaledTime + ReassertInterval;

        // First write only. Re-stamping on every success would keep pushing the deadline out by
        // a second at a time and the component would never stand down.
        if (Apply() && firstWriteAt < 0f) firstWriteAt = Time.unscaledTime;
    }

    // True when the title was actually handed to Windows.
    bool Apply()
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        // A null handle is the normal answer while the window is still coming up, and on any
        // frame where this process is not the foreground one. Nothing to do about either except
        // try again on the next tick, which is what the re-assert window above is for.
        IntPtr window = GetActiveWindow();
        if (window == IntPtr.Zero) return false;

        // Built here rather than cached in a field: outside a Windows player the whole body of
        // this method is compiled out, and a field only ever read in here would be assigned and
        // never used, which is a warning in every editor build.
        string version = Application.version;
        if (string.IsNullOrWhiteSpace(version)) version = "0.0.0";

        return SetWindowText(window, Application.productName + " " + version + BuildEdition.EditionSuffix);
#else
        // Nothing to do off Windows or in the editor, and saying so as "never written" keeps the
        // caller on its long clock instead of switching this off ten seconds in. It stands down
        // either way; this just picks the honest reason.
        return false;
#endif
    }
}
