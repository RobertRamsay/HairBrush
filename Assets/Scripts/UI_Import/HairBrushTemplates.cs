using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// Predefined starts: hairstyle, colour and card-style templates.
//
// WHAT A TEMPLATE IS
// A small JSON file holding a list of steps in exactly the shape hb_batch takes - the same
// {tool, args} an MCP agent sends:
//
//   {
//     "name": "Dark Brown",
//     "kind": "colour",                       colour | hairstyle | style
//     "order": 20,                            optional, sorts the list
//     "description": "Deep brown, slightly glossy.",
//     "swatch": [0.17, 0.11, 0.07],           optional, the colour the UI shows for it
//     "steps": [
//       { "tool": "set_hair_material", "args": { "tint": [0.26, 0.19, 0.13], "smoothness": 0.52 } }
//     ]
//   }
//
// Steps are the MCP commands, so they place hair by az/el around the head rather than at world
// coordinates, and one template fits any head. The whole template is applied as one hb_batch,
// so it is ONE undo step.
//
// WHERE THEY LIVE
//   StreamingAssets/HairBrushTemplates/   the ones that ship with HairBrush
//   persistentDataPath/Templates/         the user's own; same layout, any subfolders
//
// SAFETY
// A template is a file someone can download and drop in, so it may only use the commands that
// edit the groom. Anything that reads or writes files, loads, moves the camera, undoes, or
// nests a batch is refused before a single step runs.
public static class HairBrushTemplates
{
    public const string KindColour = "colour";
    public const string KindHairstyle = "hairstyle";
    public const string KindStyle = "style";

    public class Template
    {
        public string name;
        public string kind;
        public string description;
        public int order;
        public Color? swatch;
        public string path;
        public List<object> steps;
    }

    static readonly HashSet<string> AllowedTools = new HashSet<string>
    {
        "create_group", "select_group", "rename_group", "delete_group", "set_group_flags",
        "place_cards", "fill_region", "erase_cards",
        "set_group_params", "set_variance", "set_shape_curve",
        "add_guide", "edit_guide", "remove_guide",
        "add_clumper", "edit_clumper", "remove_clumper",
        "add_post", "edit_post", "remove_post",
        "set_hair_material", "set_hair_textures", "set_group_uv", "set_uv_rects",
        "set_symmetry", "set_card_style",
    };

    public static string BuiltInFolder => Path.Combine(Application.streamingAssetsPath, "HairBrushTemplates");
    public static string UserFolder => Path.Combine(Application.persistentDataPath, "Templates");

    public static bool IsApplying => runner != null && runner.busy;

    // ------------------------------------------------------------------ listing

    // Every template of this kind (or of every kind, for null), built-in first, sorted by order
    // then name. Read from disk on each call - they are a few small files, and it means a template
    // dropped into the folder shows up without a restart. A file that will not parse is skipped
    // with a warning, never fatal to the list.
    public static List<Template> List(string kind = null)
    {
        List<Template> found = new List<Template>();
        Collect(BuiltInFolder, kind, found);
        Collect(UserFolder, kind, found);
        found.Sort((a, b) => a.order != b.order ? a.order.CompareTo(b.order) : string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
        return found;
    }

    static void Collect(string folder, string kind, List<Template> into)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;

        string[] files;
        try { files = Directory.GetFiles(folder, "*.json", SearchOption.AllDirectories); }
        catch (Exception error) { Debug.LogWarning("HairBrush templates: could not read " + folder + " - " + error.Message); return; }

        foreach (string file in files)
        {
            Template template = TryRead(file, out string problem);
            if (template == null)
            {
                Debug.LogWarning("HairBrush templates: skipped " + file + " - " + problem);
                continue;
            }
            if (kind != null && !string.Equals(template.kind, kind, StringComparison.OrdinalIgnoreCase)) continue;
            into.Add(template);
        }
    }

    public static Template TryRead(string file, out string problem)
    {
        problem = null;
        try
        {
            if (!(HairBrushMcpJson.Parse(File.ReadAllText(file)) is Dictionary<string, object> root))
            {
                problem = "not a JSON object.";
                return null;
            }

            Template t = new Template
            {
                path = file,
                name = Str(root, "name", Path.GetFileNameWithoutExtension(file)),
                kind = Str(root, "kind", KindHairstyle).ToLowerInvariant(),
                description = Str(root, "description", ""),
                order = root.TryGetValue("order", out object o) && o is double d ? (int)d : 100,
                steps = root.TryGetValue("steps", out object s) ? s as List<object> : null,
            };

            if (t.steps == null || t.steps.Count == 0) { problem = "it has no steps."; return null; }
            if (t.steps.Count > 200) { problem = "more than 200 steps (the batch limit)."; return null; }

            if (root.TryGetValue("swatch", out object sw) && sw is List<object> c && c.Count == 3 && c[0] is double r && c[1] is double g && c[2] is double b)
                t.swatch = new Color((float)r, (float)g, (float)b, 1f);

            for (int i = 0; i < t.steps.Count; i++)
            {
                if (!(t.steps[i] is Dictionary<string, object> step)) { problem = "step " + i + " is not {tool, args}."; return null; }
                string tool = Str(step, "tool", "");
                if (tool.StartsWith("hb_")) tool = tool.Substring(3);
                if (!AllowedTools.Contains(tool)) { problem = "step " + i + " uses '" + tool + "', which a template is not allowed to run."; return null; }
            }

            return t;
        }
        catch (Exception error)
        {
            problem = error.Message;
            return null;
        }
    }

    // For a colour template made only of set_hair_material steps: the tint and smoothness it
    // sets (the last step wins). Lets the material panel apply a colour preset to the material
    // being edited - set_hair_material itself always targets the global hair material, which is
    // not necessarily the one on screen. False for a template that does anything else.
    public static bool TryGetMaterialSettings(Template template, out Color tint, out float? smoothness)
    {
        tint = Color.white;
        smoothness = null;
        bool hasTint = false;
        if (template?.steps == null) return false;

        foreach (object s in template.steps)
        {
            if (!(s is Dictionary<string, object> step)) return false;
            string tool = Str(step, "tool", "");
            if (tool.StartsWith("hb_")) tool = tool.Substring(3);
            if (tool != "set_hair_material") return false;
            if (!(step.TryGetValue("args", out object a) && a is Dictionary<string, object> args)) continue;

            if (args.TryGetValue("tint", out object t) && t is List<object> c && c.Count == 3 && c[0] is double r && c[1] is double g && c[2] is double b)
            {
                tint = new Color(Mathf.Clamp01((float)r), Mathf.Clamp01((float)g), Mathf.Clamp01((float)b), 1f);
                hasTint = true;
            }
            if (args.TryGetValue("smoothness", out object sm) && sm is double smooth) smoothness = Mathf.Clamp01((float)smooth);
        }
        return hasTint;
    }

    static string Str(Dictionary<string, object> d, string key, string fallback)
    {
        return d.TryGetValue(key, out object v) && v is string s && !string.IsNullOrEmpty(s) ? s : fallback;
    }

    // ------------------------------------------------------------------ applying

    static Runner runner;

    // Runs the template as one hb_batch. onDone(true) once every step has succeeded; onDone(false)
    // if one failed (the steps before it stay applied, as one undo step) or it could not start.
    // The user is told either way.
    public static void Apply(Template template, Action<bool> onDone = null)
    {
        if (template == null) { onDone?.Invoke(false); return; }

        if (runner == null)
        {
            GameObject go = new GameObject("HairBrushTemplateRunner");
            UnityEngine.Object.DontDestroyOnLoad(go);
            runner = go.AddComponent<Runner>();
        }

        if (runner.busy)
        {
            StatusToast.Show("A template is already being applied.", true);
            onDone?.Invoke(false);
            return;
        }

        runner.busy = true;
        runner.StartCoroutine(runner.Run(template, onDone));
    }

    class Runner : MonoBehaviour
    {
        public bool busy;

        public IEnumerator Run(Template template, Action<bool> onDone)
        {
            string error = null;
            Dictionary<string, object> result = null;

            // The command layer the MCP server uses. It has no lifecycle of its own, so when
            // MCP is off (a normal player build) a copy on this object serves just as well.
            HairBrushMcpCommands commands = FindFirstObjectByType<HairBrushMcpCommands>();
            if (commands == null) commands = gameObject.AddComponent<HairBrushMcpCommands>();

            Dictionary<string, object> args = new Dictionary<string, object>
            {
                { "steps", template.steps },
                { "stop_on_error", true },
            };

            // Stepped by hand, as HairBrushMcpServer.Execute does, so an exception inside a
            // command is reported instead of silently killing the coroutine.
            bool finished = false;
            Stack<IEnumerator> stack = new Stack<IEnumerator>();
            try { stack.Push(commands.Run("batch", args, r => { result = r as Dictionary<string, object>; finished = true; }, e => { error = e; finished = true; })); }
            catch (Exception ex) { error = Describe(ex); }

            while (!finished && error == null && stack.Count > 0)
            {
                IEnumerator top = stack.Peek();
                bool more;
                object yielded = null;
                try
                {
                    more = top.MoveNext();
                    if (more) yielded = top.Current;
                }
                catch (Exception ex) { error = Describe(ex); break; }
                if (!more) { stack.Pop(); continue; }
                if (yielded is IEnumerator nested) { stack.Push(nested); continue; }
                yield return yielded;
            }

            if (error == null) error = FirstStepError(result);

            // The MCP server reads this after ITS OWN commands to hold its queue past the undo
            // window. A template is not one of its commands; left set, it would delay the next.
            HairBrushMcpCommands.EditPending = false;
            busy = false;

            if (error != null)
            {
                Debug.LogWarning("HairBrush template '" + template.name + "' failed: " + error);
                StatusToast.Show(template.name + " could not be applied: " + error + " (CTRL+Z undoes any part that was.)", true, 8f);
                onDone?.Invoke(false);
            }
            else
            {
                StatusToast.Show(template.name + " applied. CTRL+Z to undo.", false, 4f);
                onDone?.Invoke(true);
            }
        }

        static string FirstStepError(Dictionary<string, object> result)
        {
            if (result == null) return "it finished without a result.";
            if (!(result.TryGetValue("results", out object r) && r is List<object> steps)) return null;
            foreach (object s in steps)
            {
                if (!(s is Dictionary<string, object> step)) continue;
                if (step.TryGetValue("ok", out object ok) && ok is bool b && !b)
                {
                    string which = step.TryGetValue("step", out object i) ? "step " + i : "a step";
                    return which + ": " + (step.TryGetValue("error", out object e) ? e : "failed");
                }
            }
            return null;
        }

        static string Describe(Exception ex)
        {
            if (ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null) ex = tie.InnerException;
            return ex.Message;
        }
    }
}
