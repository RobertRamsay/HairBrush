using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// MCP FLOW - the higher-level grooming helpers built on top of the one-to-one commands.
//
//   - FLOW GUIDES: a guide described as "leave the scalp here, travel back this far, lift this
//     high" is traced over the actual skull, so the curve follows the head instead of cutting
//     through it or floating off it. Hand-placed straight nodes cannot do that on a curved head.
//   - MIRRORING for guides, POSTs and clumpers. Symmetry only ever mirrored card placement; the
//     modifiers had to be placed twice by hand, and the second copy was never quite the mirror.
//   - BATCH runs a list of commands as one call and one undo step.
//   - SAMPLE CARDS reports what individual cards actually are, for checking a group's spread.
//   - CARD STYLE sets the global card cross-section and topology.
public partial class HairBrushMcpCommands
{
    // ---------------------------------------------------------------------------------
    // Mirroring
    // ---------------------------------------------------------------------------------

    // Reflection across the head's own midline, in the model's local space - the same plane
    // GroomSymmetryAuthority mirrors card placement across, but available whether or not the
    // symmetry toggle is on.
    Vector3 MirrorPoint(Vector3 world)
    {
        Transform m = RequireModel().transform;
        Vector3 local = m.InverseTransformPoint(world);
        local.x = -local.x;
        return m.TransformPoint(local);
    }

    Vector3 MirrorDirection(Vector3 world)
    {
        Transform m = RequireModel().transform;
        Vector3 local = m.InverseTransformDirection(world);
        local.x = -local.x;
        return m.TransformDirection(local);
    }

    // True when the point sits on the midline, where a mirror would only stack a duplicate.
    bool OnMidline(Vector3 world, HeadFrame f)
    {
        return Mathf.Abs(Vector3.Dot(world - f.center, f.right)) < f.bounds.extents.x * .03f;
    }

    // Snap a mirrored root back onto the surface, along the line from the head centre.
    bool SnapToSurface(HeadFrame f, Vector3 near, out Vector3 point, out Vector3 normal)
    {
        Dictionary<string, object> p = new Dictionary<string, object> { { "position", new List<object> { (double)near.x, (double)near.y, (double)near.z } } };
        return ResolvePoint(f, p, out point, out normal);
    }

    // ---------------------------------------------------------------------------------
    // Flow guides
    // ---------------------------------------------------------------------------------

    // Traces a guide over the scalp. The root direction from the head centre is swung, a fraction
    // of a degree at a time, about the axis that carries it toward the flow direction - a great
    // circle on the head - and each step is cast back onto the surface. The curve is then laid
    // out by arc length and lifted off the surface by a height profile:
    //   rises to `lift` at `peak_at` (0-1 along the guide), then eases to lift*`tail` at the tip.
    // Lift is along the head's radial direction swung toward world up by `up_bias`, because at
    // the front hairline the radial direction faces forward and pure-radial lift pushes hair out
    // over the brow instead of up.
    List<Vector3> BuildFlowNodes(HeadFrame f, Vector3 contact, Args flow)
    {
        Vector3 dir = ReadDirection(f, flow, "direction", -f.front);
        float length = Mathf.Max(.01f, flow.Float("length", .08f));
        float lift = Mathf.Max(0f, flow.Float("lift", .01f));
        float peakAt = Mathf.Clamp(flow.Float("peak_at", .3f), .01f, .99f);
        float tail = Mathf.Clamp01(flow.Float("tail", .4f));
        float upBias = Mathf.Clamp01(flow.Float("up_bias", 0f));
        int count = Mathf.Clamp(flow.Int("node_count", 6), 2, 20);

        Vector3 rootDir = (contact - f.center).normalized;
        Vector3 travel = Vector3.ProjectOnPlane(dir, rootDir);
        if (travel.sqrMagnitude < 1e-6f) throw new CommandException("The flow direction points straight into or out of the head here; pick another direction.");
        Vector3 axis = Vector3.Cross(rootDir, travel.normalized).normalized;

        List<Vector3> surface = new List<Vector3> { contact };
        List<float> cumulative = new List<float> { 0f };
        for (float deg = .5f; deg <= 170f && cumulative[cumulative.Count - 1] < length * 1.05f; deg += .5f)
        {
            Vector3 d = Quaternion.AngleAxis(deg, axis) * rootDir;
            if (!CastToSurface(f, d, out RaycastHit hit)) continue;
            cumulative.Add(cumulative[cumulative.Count - 1] + Vector3.Distance(surface[surface.Count - 1], hit.point));
            surface.Add(hit.point);
        }
        if (surface.Count < 3) throw new CommandException("Could not trace the scalp from that root in that direction.");

        List<Vector3> nodes = new List<Vector3>();
        for (int j = 1; j <= count; j++)
        {
            float t = j / (float)count;
            float s = Mathf.Min(length * t, cumulative[cumulative.Count - 1]);
            int i = 1;
            while (i < cumulative.Count - 1 && cumulative[i] < s) i++;
            float u = Mathf.InverseLerp(cumulative[i - 1], cumulative[i], s);
            Vector3 p = Vector3.Lerp(surface[i - 1], surface[i], u);

            float h = t < peakAt
                ? lift * Mathf.Sin(t / peakAt * Mathf.PI * .5f)
                : lift * (tail + (1f - tail) * Mathf.Cos((t - peakAt) / (1f - peakAt) * Mathf.PI * .5f));
            Vector3 radial = (p - f.center).normalized;
            Vector3 offset = Vector3.Lerp(radial, f.up, upBias).normalized;
            nodes.Add(p + offset * h);
        }
        return nodes;
    }

    // ---------------------------------------------------------------------------------
    // Mirrored modifiers
    // ---------------------------------------------------------------------------------

    object MirrorGuide(HeadFrame f, GuideCurveManager.GuideCurve g)
    {
        if (OnMidline(g.contact, f)) return null;
        if (!SnapToSurface(f, MirrorPoint(g.contact), out Vector3 root, out Vector3 normal)) return null;
        GuideCurveManager gm = Guides();
        GuideCurveManager.GuideCurve m = gm.CreateGuide(g.groupId, root, normal);
        m.nodesLocal = GuideCurveManager.WorldPoints(g).Skip(1).Select(w => GuideCurveManager.ToLocal(m, MirrorPoint(w))).ToList();
        GuideCurveManager.NormaliseRoll(m);
        m.amount = g.amount;
        m.radius = g.radius;
        m.falloff = g.falloff;
        if (Mathf.Abs(g.spin) > 1e-4f) GuideCurveManager.SetSpin(m, -g.spin);
        gm.ClearSelection();
        return DescribeGuide(m, f);
    }

    object MirrorClumper(HeadFrame f, GroupClumperManager.GroupClumper c)
    {
        if (OnMidline(c.center, f)) return null;
        if (!SnapToSurface(f, MirrorPoint(c.center), out Vector3 point, out Vector3 normal)) return null;
        GroupClumperManager cm = Clumpers();
        GroupClumperManager.GroupClumper m = cm.CreateClumper(c.groupId, point, normal);
        m.mode = c.mode; m.amount = c.amount; m.count = c.count; m.seed = c.seed + 1;
        m.radius = c.radius; m.falloff = c.falloff;
        cm.Invalidate(m);
        cm.ClearSelection();
        return DescribeClumper(m, f);
    }

    // A mirrored POST mirrors its sideways channels too: an angle about Y or Z, or a twist, that
    // leans hair toward the face on one side must lean it toward the face on the other.
    object MirrorPost(HeadFrame f, int gid, PostAffectorSaveData p)
    {
        Vector3 c = new Vector3(p.centerX, p.centerY, p.centerZ);
        if (OnMidline(c, f)) return null;
        if (!SnapToSurface(f, MirrorPoint(c), out Vector3 point, out Vector3 normal)) return null;
        PostAffectorManager pm = Posts();
        List<PostAffectorSaveData> list = pm.ExportGroup(gid);
        PostAffectorSaveData m = JsonUtility.FromJson<PostAffectorSaveData>(JsonUtility.ToJson(p));
        m.id = NextPostId();
        m.centerX = point.x; m.centerY = point.y; m.centerZ = point.z;
        m.normalX = normal.x; m.normalY = normal.y; m.normalZ = normal.z;
        if (m.delta != null) { m.delta.y = -m.delta.y; m.delta.z = -m.delta.z; m.delta.twist = -m.delta.twist; }
        list.Add(m);
        pm.ImportGroup(gid, list);
        return DescribePost(m, f);
    }

    // ---------------------------------------------------------------------------------
    // Batch
    // ---------------------------------------------------------------------------------

    // Steps run in order inside this one command, so nothing else can interleave, and the undo
    // history sees a single quiet window at the end - the whole batch is one Ctrl+Z.
    IEnumerator Batch(Args a, Action<object> ok)
    {
        List<object> steps = a.List("steps");
        if (steps == null || steps.Count == 0) throw new CommandException("steps is required.");
        if (steps.Count > 200) throw new CommandException("At most 200 steps per batch.");
        bool stopOnError = a.Bool("stop_on_error", true);
        List<object> results = new List<object>();

        for (int i = 0; i < steps.Count; i++)
        {
            if (!(steps[i] is Dictionary<string, object> step)) throw new CommandException("Each step must be {tool, args}.");
            Args s = new Args(step);
            string tool = s.Str("tool", "");
            string method = tool.StartsWith("hb_") ? tool.Substring(3) : tool;
            if (method == "batch") throw new CommandException("Batches cannot nest.");
            Dictionary<string, object> args = s.Obj("args")?.Dict ?? new Dictionary<string, object>();

            object result = null; string error = null; bool done = false;
            IEnumerator inner = null;
            try { inner = Run(method, args, r => { result = r; done = true; }, e => { error = e; done = true; }); }
            catch (Exception ex) { error = ex.Message; }

            // Stepped here, not yielded wholesale, so a failing step is reported against its
            // index rather than ending the batch with a bare exception.
            Stack<IEnumerator> stack = new Stack<IEnumerator>();
            if (inner != null) stack.Push(inner);
            while (!done && error == null && stack.Count > 0)
            {
                IEnumerator top = stack.Peek();
                bool more; object y = null;
                try { more = top.MoveNext(); if (more) y = top.Current; }
                catch (Exception ex)
                {
                    if (ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null) ex = tie.InnerException;
                    error = ex.Message; break;
                }
                if (!more) { stack.Pop(); continue; }
                if (y is IEnumerator nested) { stack.Push(nested); continue; }
                yield return y;
            }

            // Destroy() only takes effect at the end of the frame. Without this, a save or count
            // straight after a delete/erase in the same batch still saw the destroyed cards.
            yield return null;

            Dictionary<string, object> entry = new Dictionary<string, object> { { "step", i }, { "tool", "hb_" + method } };
            if (error != null) { entry["ok"] = false; entry["error"] = error; }
            else { entry["ok"] = true; entry["result"] = result; }
            results.Add(entry);
            if (error != null && stopOnError) break;
        }
        ok(new Dictionary<string, object> { { "results", results } });
    }

    // ---------------------------------------------------------------------------------
    // Inspection
    // ---------------------------------------------------------------------------------

    object SampleCards(Args a)
    {
        HeadFrame f = Frame();
        int gid = ResolveGroup(a);
        int count = Mathf.Clamp(a.Int("count", 12), 1, 200);
        Region region = a.Has("az_min") || a.Has("el_min") || a.Has("az_max") || a.Has("el_max") ? Region.From(a) : null;
        System.Random rng = new System.Random(a.Int("seed", 1));

        List<HairCard> pool = new List<HairCard>();
        foreach (HairCard c in AllCards())
        {
            if (c == null || c.groupId != gid) continue;
            if (region != null)
            {
                AzEl(f, c.GetSpawnHitPoint() - f.center, out float az, out float el);
                if (!region.Contains(az, el)) continue;
            }
            pool.Add(c);
        }
        List<object> cards = new List<object>();
        foreach (HairCard c in pool.OrderBy(_ => rng.Next()).Take(count))
        {
            AzEl(f, c.GetSpawnHitPoint() - f.center, out float az, out float el);
            HairCard.GroomState s = c.GetCanonicalState();
            cards.Add(new Dictionary<string, object>
            {
                { "az", Math.Round(az, 1) }, { "el", Math.Round(el, 1) }, { "mirrored", c.mirrored },
                // Rendered = what is drawn now (after variance, POSTs, guides); canonical = the base.
                { "rendered", new Dictionary<string, object>
                    {
                        { "length", c.length }, { "width", c.width }, { "bend", c.bendAngle }, { "twist", c.twistAngle },
                        { "angle_x", c.GetOffsetX() }, { "angle_y", c.GetOffsetY() }, { "angle_z", c.GetOffsetZ() },
                        { "u_scale", c.uScale }, { "v_scale", c.vScale }, { "u_offset", c.uOffset }, { "v_offset", c.vOffset }
                    } },
                { "canonical", new Dictionary<string, object> { { "length", s.length }, { "width", s.width }, { "bend", s.bend }, { "angle_x", s.x } } }
            });
        }
        return new Dictionary<string, object> { { "group_id", gid }, { "matching_cards", pool.Count }, { "cards", cards } };
    }

    // ---------------------------------------------------------------------------------
    // Card style
    // ---------------------------------------------------------------------------------

    object SetCardStyle(Args a)
    {
        if (a.Has("profile"))
        {
            if (!Enum.TryParse(a.Str("profile", ""), true, out HairCardSection.Profile p)) throw new CommandException("profile must be Tent or Diamond.");
            HairCardSection.SetProfile(p, true);
        }
        if (a.Has("topology"))
        {
            if (!Enum.TryParse(a.Str("topology", ""), true, out HairCardSection.Topology t)) throw new CommandException("topology must be Symmetric or Dynamic.");
            HairCardSection.SetTopology(t, true);
        }
        return new Dictionary<string, object> { { "profile", HairCardSection.Current.ToString() }, { "topology", HairCardSection.CurrentTopology.ToString() } };
    }

    // ---------------------------------------------------------------------------------
    // Autosave
    // ---------------------------------------------------------------------------------

    public static string AutosavePath => System.IO.Path.Combine(Application.persistentDataPath, "mcp_autosave.json");

    // Called by the server on quit (which includes leaving Play mode in the editor) and
    // periodically after edits. A groom is never lost to a recompile again.
    public static void WriteAutosave(string reason)
    {
        try
        {
            int cards = FindObjectsByType<HairCard>(FindObjectsSortMode.None).Length;
            if (cards == 0) { Debug.Log("HairBrush MCP: autosave skipped (" + reason + ") - no cards."); return; }
            RuntimeNavigationProjectIO io = FindFirstObjectByType<RuntimeNavigationProjectIO>();
            if (io == null) { Debug.Log("HairBrush MCP: autosave skipped (" + reason + ") - project IO not running."); return; }
            System.IO.File.WriteAllText(AutosavePath, JsonUtility.ToJson(io.BuildSaveData(), true));
            Debug.Log("HairBrush MCP: autosaved " + cards + " cards (" + reason + ") to " + AutosavePath);
        }
        catch (Exception ex) { Debug.LogWarning("HairBrush MCP: autosave failed (" + reason + ") - " + ex.Message); }
    }

    object AutosaveInfo()
    {
        bool exists = System.IO.File.Exists(AutosavePath);
        return new Dictionary<string, object>
        {
            { "path", AutosavePath }, { "exists", exists },
            { "written", exists ? System.IO.File.GetLastWriteTime(AutosavePath).ToString("yyyy-MM-dd HH:mm:ss") : null }
        };
    }

    IEnumerator LoadAutosave(Args a, Action<object> ok)
    {
        if (!System.IO.File.Exists(AutosavePath)) throw new CommandException("There is no autosave.");
        Dictionary<string, object> args = new Dictionary<string, object>(a.Dict) { ["path"] = AutosavePath };
        yield return LoadProject(new Args(args), ok);
    }
}
