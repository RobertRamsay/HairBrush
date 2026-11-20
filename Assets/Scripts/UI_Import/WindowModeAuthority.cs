using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// Window behaviour for the desktop player.
//
// 1. Every launch starts borderless full screen at the display's native resolution. Unity
//    remembers the last window mode in the registry, so without this a single windowed run
//    (or a -screen-fullscreen 0 launch) would make every later launch windowed too. An explicit
//    -screen-fullscreen / -screen-width / -screen-height on the command line still wins.
// 2. F11 toggles between full screen and a normal resizable window. The window comes back at
//    the size it last had, or 75% of the display the first time.
// 3. The start-menu canvas in the scene uses a constant pixel scale tuned for a 2160-line
//    display. It is rescaled with the window height so the menu fits a small window and looks
//    the same as before at 4K. Canvases built in code already use ScaleWithScreenSize.
//
// The maximise button on the title bar is Windows' own: it needs "Resizable Window" on in the
// player settings, which this project now has.
[DefaultExecutionOrder(-9000)]
public class WindowModeAuthority : MonoBehaviour
{
    private const float WindowedFraction = 0.75f;
    private const float MenuCanvasDesignHeight = 2160f;
    private const string MenuCanvasName = "Canvas";

    private int windowedWidth;
    private int windowedHeight;
    private CanvasScaler menuScaler;
    private float menuBaseScale;
    private int lastScaledHeight = -1;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (Application.isEditor) return;
        if (FindFirstObjectByType<WindowModeAuthority>() != null) return;

        GameObject go = new GameObject("WindowModeAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<WindowModeAuthority>();
    }

    void Start()
    {
        if (!CommandLineSetsWindow())
            EnterFullscreen();
        else if (Screen.fullScreenMode == FullScreenMode.Windowed)
            RememberWindowSize();

        FindMenuScaler();
    }

    void Update()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard != null && keyboard.f11Key.wasPressedThisFrame)
        {
            if (Screen.fullScreenMode == FullScreenMode.Windowed) EnterFullscreen();
            else EnterWindowed();
        }

        if (Screen.fullScreenMode == FullScreenMode.Windowed) RememberWindowSize();

        RescaleMenuCanvas();
    }

    private void EnterFullscreen()
    {
        if (Screen.fullScreenMode == FullScreenMode.Windowed) RememberWindowSize();
        GetNativeSize(out int w, out int h);
        Screen.SetResolution(w, h, FullScreenMode.FullScreenWindow);
    }

    private void EnterWindowed()
    {
        GetNativeSize(out int nativeW, out int nativeH);
        int w = windowedWidth, h = windowedHeight;
        // A remembered size that fills the display is not a window anyone wants back.
        if (w <= 0 || h <= 0 || w >= nativeW || h >= nativeH)
        {
            w = Mathf.RoundToInt(nativeW * WindowedFraction);
            h = Mathf.RoundToInt(nativeH * WindowedFraction);
        }
        Screen.SetResolution(w, h, FullScreenMode.Windowed);
    }

    private void RememberWindowSize()
    {
        if (Screen.width > 0 && Screen.height > 0)
        {
            windowedWidth = Screen.width;
            windowedHeight = Screen.height;
        }
    }

    private static void GetNativeSize(out int w, out int h)
    {
        DisplayInfo info = Screen.mainWindowDisplayInfo;
        w = info.width;
        h = info.height;
        if (w <= 0 || h <= 0)
        {
            w = Display.main.systemWidth;
            h = Display.main.systemHeight;
        }
    }

    private static bool CommandLineSetsWindow()
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i].ToLowerInvariant();
            if (a == "-screen-fullscreen" || a == "-screen-width" || a == "-screen-height" ||
                a == "-window-mode" || a == "-popupwindow")
                return true;
        }
        return false;
    }

    private void FindMenuScaler()
    {
        Canvas[] canvases = FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < canvases.Length; i++)
        {
            Canvas c = canvases[i];
            if (c == null || !c.isRootCanvas || c.gameObject.name != MenuCanvasName) continue;
            CanvasScaler s = c.GetComponent<CanvasScaler>();
            if (s == null || s.uiScaleMode != CanvasScaler.ScaleMode.ConstantPixelSize) continue;
            menuScaler = s;
            menuBaseScale = s.scaleFactor;
            return;
        }
    }

    private void RescaleMenuCanvas()
    {
        if (menuScaler == null || Screen.height == lastScaledHeight) return;
        lastScaledHeight = Screen.height;
        menuScaler.scaleFactor = Mathf.Max(0.5f, menuBaseScale * Screen.height / MenuCanvasDesignHeight);
    }
}
