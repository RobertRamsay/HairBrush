using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

// Lightweight startup update check. The installed version comes from Unity Player Settings
// (Application.version). The latest public version is a single text file on GitHub.
public sealed class HairBrushUpdateChecker : MonoBehaviour
{
    private const string LatestVersionUrl =
        "https://raw.githubusercontent.com/RobertRamsay/HairBrush/main/hairbrush_version.txt";

    private const float StartupDelaySeconds = 2f;
    private const float NotificationSeconds = 10f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Create()
    {
        if (FindFirstObjectByType<HairBrushUpdateChecker>() != null) return;

        GameObject go = new GameObject("HairBrushUpdateChecker");
        DontDestroyOnLoad(go);
        go.AddComponent<HairBrushUpdateChecker>();
    }

    private IEnumerator Start()
    {
        // Let the first scene/UI settle before doing the network request or showing anything.
        yield return new WaitForSecondsRealtime(StartupDelaySeconds);

        using UnityWebRequest request = UnityWebRequest.Get(LatestVersionUrl);
        request.timeout = 5;
        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            // Update checks should never interrupt startup just because the user is offline.
            Debug.Log("[HairBrush] Update check skipped: " + request.error);
            yield break;
        }

        string currentVersion = Application.version.Trim();
        string availableVersion = request.downloadHandler.text.Trim();

        if (string.IsNullOrWhiteSpace(availableVersion))
            yield break;

        if (!IsNewerVersion(availableVersion, currentVersion))
            yield break;

        StatusToast.Show(
            "Current version: " + currentVersion + "    New version available: " + availableVersion,
            false,
            NotificationSeconds);
    }

    private static bool IsNewerVersion(string candidate, string current)
    {
        if (!TryParseVersion(candidate, out Version candidateVersion) ||
            !TryParseVersion(current, out Version currentVersion))
        {
            Debug.LogWarning(
                "[HairBrush] Could not compare update versions. Current='" + current +
                "', Available='" + candidate + "'.");
            return false;
        }

        return candidateVersion > currentVersion;
    }

    private static bool TryParseVersion(string value, out Version version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value)) return false;

        string cleaned = value.Trim();
        if (cleaned.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned.Substring(1);

        int suffixIndex = cleaned.IndexOfAny(new[] { '-', '+' });
        if (suffixIndex >= 0)
            cleaned = cleaned.Substring(0, suffixIndex);

        // System.Version expects at least major.minor.
        if (cleaned.IndexOf('.') < 0)
            cleaned += ".0";

        return Version.TryParse(cleaned, out version);
    }
}
