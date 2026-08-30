// WHICH BUILD THIS IS. One line changes between a PRO build and a DEMO build.
//
//     DEMO = 0   ship as PRO
//     DEMO = 1   ship as DEMO
//
// Set it, build, rename the output to _DEMO. Nothing else in the project needs touching.
//
// COMPILED IN, on purpose. hairbrush_version.txt sits beside the executable because every
// running copy has to be able to read it. This is the opposite case: a plain text file next to
// the exe saying DEMO=1 is an invitation to change it to 0, and a PlayerPrefs key is worse
// again. A const in a script becomes a literal in the assembly and there is nothing on disk to
// edit.
//
// Nothing here is secret in the cryptographic sense - anyone determined enough can patch a
// binary - but the bar is now "open a hex editor", not "open Notepad", which is the whole of
// what a demo lock is for.
public static class BuildEdition
{
    // 0 = PRO, 1 = DEMO.
    public const int DEMO = 1;

    // readonly rather than const, and this is deliberate. A const bool makes every
    // `if (BuildEdition.IsDemo)` branch statically known, so the compiler flags the other side
    // as unreachable and Unity fills the console with CS0162 on every build. A static readonly
    // is resolved at type initialisation instead: same single literal to edit above, same
    // compiled-in value, no warnings, and both sides of every branch still compile - which also
    // means a PRO build cannot silently rot the demo path.
    public static readonly bool IsDemo = DEMO == 1;

    // ------------------------------------------------------------------------ where to buy
    public const string ArtStationUrl =
        "https://polytricity.artstation.com/store/3LAg5/hairbrush-3d-card-placement-grooming-windows-pc-tool-v020-beta";
    public const string ItchUrl = "https://polytricity.itch.io/hairbrush";

    // ------------------------------------------------------------------------ what it says
    //
    // The EXPORT OBJ button keeps its name and its place in the row; only the label changes, so
    // a demo user can see the feature exists and what they would be buying. A button that is
    // simply missing teaches them nothing, and one that is greyed out reads as broken.
    //
    // Both spellings live here because WorkspaceExportUtilityAuthority has to be able to FIND an
    // export button it may have created under either of them - see the note at its lookup.
    public const string ExportProLabel = "EXPORT OBJ";
    public const string ExportDemoLabel = "EXPORT OBJ (PRO)";

    public static string ExportLabel
    {
        get
        {
            if (IsDemo) return ExportDemoLabel;
            return ExportProLabel;
        }
    }

    // Appended to the Welcome panel heading and the window title. Empty in a PRO build, so both
    // of those read exactly as they do today.
    public static string EditionSuffix
    {
        get
        {
            if (IsDemo) return " DEMO";
            return "";
        }
    }
}
