using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

// The one reader and the one writer of hairbrush.ini.
//
// This used to live as private helpers inside WelcomeWhatsNewAuthority, which was fine while
// the welcome panel was the only thing with anything to remember. It is not any more - the
// MAYA-NAV preference is written from two places (the left-panel button and the welcome card's
// checkbox) and read before either of them exists. Two copies of a read-modify-write against
// the same file is how a setting gets silently dropped: whichever copy wrote last would have
// rebuilt the file from ITS OWN snapshot of the contents, discarding anything the other copy
// had added in between. Hence one class, and everything goes through it.
//
// The file lives in Application.persistentDataPath rather than beside the executable, because
// an installed build usually sits somewhere the user cannot write to. On Windows that is
// %USERPROFILE%\AppData\LocalLow\<company>\<product>.
//
// Note what that path is keyed on: company and PRODUCT, not version. The ini therefore
// survives a version bump, which is the whole point for a preference like MAYA-NAV - making
// somebody re-pick their navigation scheme after every update would be a bug. It also survives
// buying, because the DEMO and PRO editions share a product name and so share the file.
//
// The ONE key that is deliberately version-scoped is the welcome panel's
// suppressWelcomeForVersion, and it achieves that by storing the version as its VALUE rather
// than by the file being version-scoped. Nothing else has to work that way, and nothing else
// should.
public static class HairBrushSettings
{
    public const string SettingsFileName = "hairbrush.ini";

    public static string SettingsPath()
    {
        return Path.Combine(Application.persistentDataPath, SettingsFileName);
    }

    // Always returns a dictionary, never null, so callers can index or TryGetValue without a
    // guard. A missing or unreadable file reads as "no settings", which is the correct answer
    // on a first launch and the only safe answer on a corrupt one.
    public static Dictionary<string, string> ReadSettings()
    {
        Dictionary<string, string> values = new Dictionary<string, string>();

        try
        {
            string path = SettingsPath();
            if (!File.Exists(path)) return values;

            foreach (string line in File.ReadAllLines(path))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                if (trimmed.StartsWith(";") || trimmed.StartsWith("#") || trimmed.StartsWith("[")) continue;

                int split = trimmed.IndexOf('=');
                if (split <= 0) continue;

                values[trimmed.Substring(0, split).Trim()] = trimmed.Substring(split + 1).Trim();
            }
        }
        catch (Exception error)
        {
            // A settings file that cannot be read is not worth failing a launch over.
            Debug.LogWarning("HairBrush: could not read " + SettingsFileName + " - " + error.Message);
        }

        return values;
    }

    // Read-modify-write of the whole file. Every other key is preserved because ReadSettings
    // runs first and its result is what gets written back out.
    public static void WriteSetting(string key, string value)
    {
        Dictionary<string, string> values = ReadSettings();
        values[key] = value;

        StringBuilder builder = new StringBuilder();
        builder.AppendLine("; HairBrush settings. Safe to delete - it will be rebuilt.");
        builder.AppendLine("[HairBrush]");
        foreach (KeyValuePair<string, string> pair in values)
            builder.AppendLine(pair.Key + "=" + pair.Value);

        try
        {
            File.WriteAllText(SettingsPath(), builder.ToString());
        }
        catch (Exception error)
        {
            Debug.LogWarning("HairBrush: could not write " + SettingsFileName + " - " + error.Message);
        }
    }

    // Written as "1"/"0", but "true"/"yes"/"on" are accepted on the way back in - the file is
    // plain text sitting in a folder the user can open, and somebody WILL hand-edit it. A value
    // that is present but unrecognised falls back rather than reading as false, so a typo does
    // not silently switch a preference off.
    public static bool GetBool(string key, bool fallback)
    {
        string stored;
        if (!ReadSettings().TryGetValue(key, out stored)) return fallback;

        string normalised = stored.Trim().ToLower(CultureInfo.InvariantCulture);
        if (normalised == "1" || normalised == "true" || normalised == "yes" || normalised == "on") return true;
        if (normalised == "0" || normalised == "false" || normalised == "no" || normalised == "off") return false;
        return fallback;
    }

    public static void SetBool(string key, bool value)
    {
        string written = "0";
        if (value) written = "1";
        WriteSetting(key, written);
    }
}
