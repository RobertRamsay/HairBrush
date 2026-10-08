using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

// MCP COMMANDS - what each tool call actually does to the groom.
//
// Everything here goes through the same entry points the UI uses (PinHairCard, the slider
// handlers, CreateGuide, the variance and shape-curve registries, the project IO) rather than
// writing card fields directly. That is the whole point: symmetry, POST preparation, the root
// state, undo capture and the group panel all keep working because nothing here goes around them.
// Many of those entry points are private on ModelViewer, so they are reached by reflection in
// exactly the way PlacementBrushModeAuthority and RuntimeNavigationProjectIO already do.
//
// HEAD FRAME. Points on the scalp are addressed by azimuth/elevation from the centre of the head
// mesh's bounds, so a caller can say "the crown" or "behind the left ear" without knowing the
// model's coordinates:
//   front   = the loaded model's forward axis (the face; world -Z after the 180 degree import turn)
//   right   = the character's own right, Cross(up, front)
//   az      = degrees around the vertical, 0 = front, +90 = character's right, -90 = left, 180 = back
//   el      = degrees above the horizontal through the centre, +90 = straight up (crown)
// A ray is cast from well outside the head toward the centre along that direction, and the first
// surface it meets is the point, with the mesh normal there. Hair cards have no colliders, so the
// ray only ever finds the head.
//
// Every mutating command ends with UndoHistoryAuthority.NotifyEdit(), so each tool call becomes
// its own undo step instead of being folded into whatever the user does next.
public partial class HairBrushMcpCommands : MonoBehaviour
{
    public class CommandException : Exception
    {
        public CommandException(string message) : base(message) { }
    }

    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    ModelViewer viewer;

    // ---------------------------------------------------------------------------------
    // Dispatch
    // ---------------------------------------------------------------------------------

    public IEnumerator Run(string method, Dictionary<string, object> raw, Action<object> ok, Action<string> fail)
    {
        Args a = new Args(raw);
        viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer == null) throw new CommandException("HairBrush is not ready: no ModelViewer in the scene.");

        switch (method)
        {
            case "ping": ok(new Dictionary<string, object> { { "pong", true } }); yield break;
            case "status": ok(Status()); yield break;
            case "head_info": ok(HeadInfo()); yield break;
            case "probe_points": ok(ProbePoints(a)); yield break;
            case "list_groups": ok(ListGroups()); yield break;
            case "get_group": ok(GetGroup(a)); yield break;

            case "load_model": yield return LoadModel(a, ok); yield break;
            case "load_project": yield return LoadProject(a, ok); yield break;
            case "save_project": ok(SaveProject(a)); yield break;
            case "export_obj": ok(ExportObj(a)); yield break;

            case "create_group": ok(Edit(CreateGroup(a))); yield break;
            case "select_group": ok(SelectGroupCommand(a)); yield break;
            case "rename_group": ok(Edit(RenameGroup(a))); yield break;
            case "delete_group": ok(DeleteGroup(a)); yield break;
            case "set_group_flags": ok(Edit(SetGroupFlags(a))); yield break;

            case "place_cards": ok(Edit(PlaceCards(a))); yield break;
            case "fill_region": ok(Edit(FillRegion(a))); yield break;
            case "erase_cards": ok(Edit(EraseCards(a))); yield break;
            case "set_group_params": ok(Edit(SetGroupParams(a))); yield break;
            case "set_variance": ok(Edit(SetVariance(a))); yield break;
            case "set_shape_curve": ok(Edit(SetShapeCurve(a))); yield break;

            case "add_guide": ok(Edit(AddGuide(a))); yield break;
            case "edit_guide": ok(Edit(EditGuide(a))); yield break;
            case "remove_guide": yield return RemoveGuide(a, ok); yield break;
            case "add_clumper": ok(Edit(AddClumper(a))); yield break;
            case "edit_clumper": ok(Edit(EditClumper(a))); yield break;
            case "remove_clumper": yield return RemoveClumper(a, ok); yield break;

            case "set_hair_material": ok(Edit(SetHairMaterial(a))); yield break;
            case "get_uv_rects": ok(GetUVRects()); yield break;
            case "set_uv_rects": yield return SetUVRects(a, ok); yield break;
            case "set_group_uv": ok(Edit(SetGroupUV(a))); yield break;

            case "add_post": ok(Edit(AddPost(a))); yield break;
            case "edit_post": ok(Edit(EditPost(a))); yield break;
            case "remove_post": ok(Edit(RemovePost(a))); yield break;
            case "set_symmetry": ok(SetSymmetry(a)); yield break;
            case "set_view": ok(SetView(a)); yield break;
            case "screenshot": yield return Screenshot(a, ok); yield break;
            case "turnaround": yield return Turnaround(a, ok); yield break;
            case "preview_uv_rects": ok(PreviewUVRects(a)); yield break;
            case "sample_cards": ok(SampleCards(a)); yield break;
            case "set_card_style": ok(Edit(SetCardStyle(a))); yield break;
            case "batch": yield return Batch(a, ok); yield break;
            case "load_autosave": yield return LoadAutosave(a, ok); yield break;
            case "undo": yield return UndoRedo(true, ok); yield break;
            case "redo": yield return UndoRedo(false, ok); yield break;
        }
        fail("Unknown HairBrush command '" + method + "'.");
    }

    static object Edit(object result)
    {
        MarkEdited();
        return result;
    }

    // Set when the last command changed the groom. HairBrushMcpServer reads and clears it after
    // replying, and then holds the queue past UndoHistoryAuthority's quiet window so the step
    // commits before the next command lands - otherwise a quick run of tool calls would fold
    // into one undo step, exactly as a burst of clicks does.
    public static bool EditPending;

    static void MarkEdited()
    {
        UndoHistoryAuthority.NotifyEdit();
        EditPending = true;
    }

    // ---------------------------------------------------------------------------------
    // Reflection into ModelViewer
    // ---------------------------------------------------------------------------------

    T Field<T>(string name)
    {
        FieldInfo f = typeof(ModelViewer).GetField(name, Private);
        if (f == null) throw new CommandException("HairBrush internals changed: ModelViewer." + name + " is missing.");
        return (T)f.GetValue(viewer);
    }

    void SetField(string name, object value)
    {
        FieldInfo f = typeof(ModelViewer).GetField(name, Private);
        if (f == null) throw new CommandException("HairBrush internals changed: ModelViewer." + name + " is missing.");
        f.SetValue(viewer, value);
    }

    object Call(string name, params object[] args)
    {
        MethodInfo m = typeof(ModelViewer).GetMethod(name, Private, null, args.Select(x => x.GetType()).ToArray(), null);
        if (m == null) throw new CommandException("HairBrush internals changed: ModelViewer." + name + "() is missing.");
        return m.Invoke(viewer, args);
    }

    object CallNoArgs(string name)
    {
        MethodInfo m = typeof(ModelViewer).GetMethod(name, Private, null, Type.EmptyTypes, null);
        if (m == null) throw new CommandException("HairBrush internals changed: ModelViewer." + name + "() is missing.");
        return m.Invoke(viewer, null);
    }

    GameObject LoadedModel => Field<GameObject>("loadedModel");
    HashSet<int> GroupIds => Field<HashSet<int>>("allGroupIds");
    Dictionary<int, string> GroupNames => Field<Dictionary<int, string>>("groupNames");

    GameObject RequireModel()
    {
        GameObject model = LoadedModel;
        if (model == null) throw new CommandException("No head model is loaded. Use hb_load_model or hb_load_project first.");
        return model;
    }

    int ResolveGroup(Args a)
    {
        int gid = a.Int("group_id", viewer.currentGroupId);
        if (!GroupIds.Contains(gid)) throw new CommandException("Group " + gid + " does not exist. Use hb_list_groups.");
        return gid;
    }

    // Selects the group the way clicking its row does, so every current* field and slider comes
    // from that group's root. Skipped when it is already current - SelectGroup flashes the row,
    // and a burst of tool calls on one group should not strobe the panel.
    void EnsureGroupSelected(int gid)
    {
        if (viewer.currentGroupId == gid) return;
        ModifierContextExit.LeaveEverything(viewer);
        Call("SelectGroup", gid);
    }

    // A live Ctrl+click selection redirects every slider handler to the selected cards only, and
    // relative mode turns the values into deltas. A tool call always means "this group, these
    // absolute values", so both are cleared or suspended around the edit.
    void ClearCardSelection()
    {
        if (Field<bool>("hasSelectionHotspot")) CallNoArgs("ClearSelectionHotspot");
    }

    // ---------------------------------------------------------------------------------
    // Head frame and surface probing
    // ---------------------------------------------------------------------------------

    struct HeadFrame
    {
        public Vector3 center, front, right, up;
        public Bounds bounds;
        public float radius;
    }

    HeadFrame Frame()
    {
        GameObject model = RequireModel();
        Renderer[] renderers = model.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) throw new CommandException("The loaded model has no renderers.");
        Bounds b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);

        Vector3 up = Vector3.up;
        Vector3 front = Vector3.ProjectOnPlane(model.transform.forward, up).normalized;
        if (front.sqrMagnitude < .5f) front = Vector3.back;
        return new HeadFrame
        {
            center = b.center,
            up = up,
            front = front,
            right = Vector3.Cross(up, front).normalized,
            bounds = b,
            radius = b.extents.magnitude
        };
    }

    static Vector3 Direction(HeadFrame f, float azDeg, float elDeg)
    {
        float az = azDeg * Mathf.Deg2Rad, el = elDeg * Mathf.Deg2Rad;
        return (Mathf.Cos(el) * (Mathf.Cos(az) * f.front + Mathf.Sin(az) * f.right) + Mathf.Sin(el) * f.up).normalized;
    }

    static void AzEl(HeadFrame f, Vector3 dir, out float az, out float el)
    {
        dir.Normalize();
        el = Mathf.Asin(Mathf.Clamp(Vector3.Dot(dir, f.up), -1f, 1f)) * Mathf.Rad2Deg;
        az = Mathf.Atan2(Vector3.Dot(dir, f.right), Vector3.Dot(dir, f.front)) * Mathf.Rad2Deg;
    }

    // Named directions in the head frame, for guide flow and similar. "down" is world down,
    // "back" is away from the face, and so on - the words a hairdresser would use.
    static Vector3 NamedDirection(HeadFrame f, string name)
    {
        switch (name)
        {
            case "down": return -f.up;
            case "up": return f.up;
            case "front": case "forward": return f.front;
            case "back": case "backward": return -f.front;
            case "right": return f.right;
            case "left": return -f.right;
        }
        throw new CommandException("Unknown direction '" + name + "'. Use down/up/front/back/left/right or an [x,y,z] vector.");
    }

    Vector3 ReadDirection(HeadFrame f, Args a, string key, Vector3 fallback)
    {
        if (!a.Has(key)) return fallback;
        object v = a.Raw(key);
        if (v is string s) return NamedDirection(f, s);
        return a.Vec(key).normalized;
    }

    bool CastToSurface(HeadFrame f, Vector3 dir, out RaycastHit hit)
    {
        Vector3 origin = f.center + dir * (f.radius * 3f + .1f);
        return CastAlong(origin, -dir, f.radius * 6f + .2f, out hit);
    }

    // Only the head's colliders, so nothing else in the scene - UI, gizmos, a future collider on
    // something unrelated - can ever be mistaken for the scalp.
    bool CastAlong(Vector3 origin, Vector3 dir, float distance, out RaycastHit best)
    {
        best = default;
        bool found = false;
        Ray ray = new Ray(origin, dir);
        foreach (Collider c in RequireModel().GetComponentsInChildren<Collider>())
        {
            if (c.Raycast(ray, out RaycastHit h, distance) && (!found || h.distance < best.distance))
            {
                best = h;
                found = true;
            }
        }
        return found;
    }

    // A point is either {az, el} on the head or a world {position}. A world position is snapped
    // to the surface along the line from the head centre, so a slightly-off coordinate still
    // lands on the scalp with a real normal.
    bool ResolvePoint(HeadFrame f, Dictionary<string, object> p, out Vector3 point, out Vector3 normal)
    {
        Args pa = new Args(p);
        Vector3 dir;
        if (pa.Has("position")) dir = (pa.Vec("position") - f.center).normalized;
        else if (pa.Has("az") || pa.Has("el")) dir = Direction(f, pa.Float("az", 0f), pa.Float("el", 0f));
        else throw new CommandException("A point needs either az/el or position.");

        if (CastToSurface(f, dir, out RaycastHit hit))
        {
            point = hit.point;
            normal = hit.normal;
            return true;
        }
        point = normal = Vector3.zero;
        return false;
    }

    object HeadInfo()
    {
        HeadFrame f = Frame();
        Dictionary<string, object> landmarks = new Dictionary<string, object>();
        (string name, float az, float el)[] probes =
        {
            ("crown", 0f, 90f), ("front_hairline", 0f, 35f), ("forehead", 0f, 15f),
            ("right_temple", 70f, 20f), ("left_temple", -70f, 20f),
            ("right_side", 90f, 0f), ("left_side", -90f, 0f),
            ("back", 180f, 10f), ("nape", 180f, -25f)
        };
        foreach (var probe in probes)
        {
            if (CastToSurface(f, Direction(f, probe.az, probe.el), out RaycastHit hit))
                landmarks[probe.name] = new Dictionary<string, object> { { "az", probe.az }, { "el", probe.el }, { "position", hit.point }, { "normal", hit.normal } };
        }
        return new Dictionary<string, object>
        {
            { "center", f.center },
            { "size", f.bounds.size },
            { "front", f.front },
            { "right", f.right },
            { "up", f.up },
            { "note", "Head width is normalised to about 0.33 units on import, so a card length of 0.2 is roughly shoulder length on an average head. az 0 = face, +90 = character's right, 180 = back; el +90 = crown." },
            { "landmarks", landmarks }
        };
    }

    object ProbePoints(Args a)
    {
        HeadFrame f = Frame();
        List<object> results = new List<object>();
        foreach (Dictionary<string, object> p in a.Objects("points"))
        {
            if (ResolvePoint(f, p, out Vector3 point, out Vector3 normal))
            {
                AzEl(f, point - f.center, out float az, out float el);
                results.Add(new Dictionary<string, object> { { "hit", true }, { "position", point }, { "normal", normal }, { "az", az }, { "el", el } });
            }
            else results.Add(new Dictionary<string, object> { { "hit", false } });
        }
        return new Dictionary<string, object> { { "points", results } };
    }

    // ---------------------------------------------------------------------------------
    // Status and inspection
    // ---------------------------------------------------------------------------------

    static HairCard[] AllCards() => FindObjectsByType<HairCard>(FindObjectsSortMode.None);

    object Status()
    {
        GameObject model = LoadedModel;
        HairCard[] cards = AllCards();
        return new Dictionary<string, object>
        {
            { "app_version", Application.version },
            { "running_in_editor", Application.isEditor },
            { "model_loaded", model != null },
            { "model_path", Field<string>("currentModelPath") },
            { "grooming_mode", Field<bool>("isGroomingMode") },
            { "current_group_id", viewer.currentGroupId },
            { "symmetry", GroomSymmetryAuthority.Enabled },
            { "total_cards", cards.Length },
            { "groups", GroupSummaries(cards) },
            { "autosave", AutosaveInfo() }
        };
    }

    List<object> GroupSummaries(HairCard[] cards)
    {
        GuideCurveManager guides = FindFirstObjectByType<GuideCurveManager>();
        GroupClumperManager clumpers = FindFirstObjectByType<GroupClumperManager>();
        Dictionary<int, string> names = GroupNames;
        List<object> list = new List<object>();
        foreach (int id in GroupIds.OrderBy(x => x))
        {
            list.Add(new Dictionary<string, object>
            {
                { "id", id },
                { "name", names.TryGetValue(id, out string n) ? n : "Group " + id },
                { "cards", cards.Count(c => c != null && c.groupId == id) },
                { "guides", guides != null ? guides.GetGroupGuides(id).Count : 0 },
                { "clumpers", clumpers != null ? clumpers.GetGroupClumpers(id).Count : 0 },
                { "current", id == viewer.currentGroupId }
            });
        }
        return list;
    }

    object ListGroups() => new Dictionary<string, object> { { "groups", GroupSummaries(AllCards()) } };

    object GetGroup(Args a)
    {
        int gid = ResolveGroup(a);
        HairCard[] cards = AllCards().Where(c => c != null && c.groupId == gid).ToArray();

        // Read off the cards' canonical (pre-POST) state, median per field - the same recovery
        // SyncShapeSlidersToGroupRoot uses. With variance on, no single card is "the" value.
        Dictionary<string, object> parameters = new Dictionary<string, object>();
        if (cards.Length > 0)
        {
            List<HairCard.GroomState> states = cards.Select(c => c.GetCanonicalState()).ToList();
            parameters["length"] = Median(states, s => s.length);
            parameters["width"] = Median(states, s => s.width);
            parameters["segments"] = Mathf.RoundToInt(Median(states, s => s.segments));
            parameters["bend"] = Median(states, s => s.bend);
            parameters["twist"] = Median(states, s => s.twist);
            parameters["angle_x"] = Median(states, s => s.x);
            parameters["angle_y"] = Median(states, s => s.y);
            parameters["angle_z"] = Median(states, s => s.z);
            parameters["embed_depth"] = Median(states, s => s.depth);
            parameters["curl_frequency"] = Median(states, s => s.curlFrequency);
            parameters["curl_diameter"] = Median(states, s => s.curlDiameter);
            parameters["wave_amplitude"] = Median(states, s => s.waveAmplitude);
            parameters["wave_frequency"] = Median(states, s => s.waveFrequency);
            parameters["wave_direction"] = Median(states, s => s.waveDirection);
            parameters["arch"] = Median(states, s => s.arch);
        }
        else if (gid == viewer.currentGroupId) parameters = CurrentParams();

        Dictionary<string, object> curves = new Dictionary<string, object>();
        foreach (GroomShapeCurveChannel ch in Enum.GetValues(typeof(GroomShapeCurveChannel)))
            curves[ch.ToString()] = GroomShapeCurveRegistry.Export(gid, ch).Select(k => (object)new List<object> { k.time, k.value }).ToList();

        List<object> variance = new List<object>();
        GroomVarianceController vc = FindFirstObjectByType<GroomVarianceController>();
        if (vc != null)
            foreach (VarianceChannelSaveData v in vc.ExportGroupSettings(gid))
                if (v.amount != 0f) variance.Add(new Dictionary<string, object> { { "channel", v.channel }, { "amount", v.amount }, { "seed", v.seed } });

        HeadFrame? frame = LoadedModel != null ? Frame() : (HeadFrame?)null;
        List<object> guideList = new List<object>();
        GuideCurveManager gm = FindFirstObjectByType<GuideCurveManager>();
        if (gm != null) foreach (GuideCurveManager.GuideCurve g in gm.GetGroupGuides(gid)) guideList.Add(DescribeGuide(g, frame));
        List<object> clumperList = new List<object>();
        GroupClumperManager cm = FindFirstObjectByType<GroupClumperManager>();
        if (cm != null) foreach (GroupClumperManager.GroupClumper c in cm.GetGroupClumpers(gid)) clumperList.Add(DescribeClumper(c, frame));

        return new Dictionary<string, object>
        {
            { "id", gid },
            { "name", GroupNames.TryGetValue(gid, out string n) ? n : "Group " + gid },
            { "cards", cards.Length },
            { "params", parameters },
            { "variance", variance },
            { "shape_curves", curves },
            { "single_sided", GroupSidednessAuthority.IsSingleSided(gid) },
            { "normals_flipped", GroupNormalFlipAuthority.IsFlipped(gid) },
            { "guides", guideList },
            { "clumpers", clumperList },
            { "posts", Posts().ExportGroup(gid).Select(p => DescribePost(p, frame)).ToList() },
            { "uv", DescribeGroupUV(gid) }
        };
    }

    static float Median(List<HairCard.GroomState> states, Func<HairCard.GroomState, float> pick)
    {
        List<float> v = states.Select(pick).OrderBy(x => x).ToList();
        int m = v.Count / 2;
        return v.Count % 2 == 1 ? v[m] : (v[m - 1] + v[m]) * .5f;
    }

    // ---------------------------------------------------------------------------------
    // Card parameters
    // ---------------------------------------------------------------------------------

    // Tool-facing name -> the ModelViewer slider handler that applies it to a group. Order matters
    // only in that segments goes before the shape fields, matching the panel.
    static readonly (string key, string handler)[] ParamHandlers =
    {
        ("length", "OnActualSliderLengthChanged"),
        ("width", "OnSliderWidthChanged"),
        ("segments", "OnSliderSegmentsChanged"),
        ("embed_depth", "OnSliderEmbedDepthChanged"),
        ("bend", "OnSliderBendChanged"),
        ("twist", "OnSliderTwistChanged"),
        ("angle_x", "OnSliderOffsetXChanged"),
        ("angle_y", "OnSliderOffsetYChanged"),
        ("angle_z", "OnSliderOffsetZChanged"),
        ("curl_frequency", "OnSliderCurlFrequencyChanged"),
        ("curl_diameter", "OnSliderCurlDiameterChanged"),
        ("wave_amplitude", "OnSliderWaveAmplitudeChanged"),
        ("wave_frequency", "OnSliderWaveFrequencyChanged"),
        ("wave_direction", "OnSliderWaveDirectionChanged"),
        ("arch", "OnSliderArchChanged"),
        ("u_scale", "OnSliderUScaleChanged"),
        ("v_scale", "OnSliderVScaleChanged"),
        ("u_offset", "OnSliderUOffsetChanged"),
        ("v_offset", "OnSliderVOffsetChanged"),
    };

    Dictionary<string, object> CurrentParams()
    {
        return new Dictionary<string, object>
        {
            { "length", viewer.currentLength }, { "width", viewer.currentWidth }, { "segments", viewer.currentSegments },
            { "embed_depth", viewer.currentEmbedDepth }, { "bend", viewer.currentBend }, { "twist", viewer.currentTwist },
            { "angle_x", viewer.currentOffsetX }, { "angle_y", viewer.currentOffsetY }, { "angle_z", viewer.currentOffsetZ },
            { "curl_frequency", viewer.currentCurlFrequency }, { "curl_diameter", viewer.currentCurlDiameter },
            { "wave_amplitude", viewer.currentWaveAmplitude }, { "wave_frequency", viewer.currentWaveFrequency },
            { "wave_direction", viewer.currentWaveDirection }, { "arch", viewer.currentArch },
            { "u_scale", viewer.currentUScale }, { "v_scale", viewer.currentVScale },
            { "u_offset", viewer.currentUOffset }, { "v_offset", viewer.currentVOffset }
        };
    }

    // Placement defaults only - the values the NEXT card is born with. Existing cards are not
    // touched; that is what set_group_params is for.
    void ApplyPlacementDefaults(Args p)
    {
        if (p == null) return;
        foreach (string key in p.Keys)
            if (!ParamHandlers.Any(h => h.key == key)) throw new CommandException("Unknown card parameter '" + key + "'.");
        if (p.Has("length")) viewer.currentLength = Mathf.Max(.0001f, p.Float("length", 0f));
        if (p.Has("width")) viewer.currentWidth = Mathf.Max(.0005f, p.Float("width", 0f));
        if (p.Has("segments")) viewer.currentSegments = Mathf.Clamp(p.Int("segments", 12), 4, 60);
        if (p.Has("embed_depth")) viewer.currentEmbedDepth = Mathf.Max(0f, p.Float("embed_depth", 0f));
        if (p.Has("bend")) viewer.currentBend = p.Float("bend", 0f);
        if (p.Has("twist")) viewer.currentTwist = p.Float("twist", 0f);
        if (p.Has("angle_x")) viewer.currentOffsetX = p.Float("angle_x", 0f);
        if (p.Has("angle_y")) viewer.currentOffsetY = p.Float("angle_y", 0f);
        if (p.Has("angle_z")) viewer.currentOffsetZ = p.Float("angle_z", 0f);
        if (p.Has("curl_frequency")) viewer.currentCurlFrequency = p.Float("curl_frequency", 0f);
        if (p.Has("curl_diameter")) viewer.currentCurlDiameter = Mathf.Max(0f, p.Float("curl_diameter", 0f));
        if (p.Has("wave_amplitude")) viewer.currentWaveAmplitude = Mathf.Max(0f, p.Float("wave_amplitude", 0f));
        if (p.Has("wave_frequency")) viewer.currentWaveFrequency = p.Float("wave_frequency", 0f);
        if (p.Has("wave_direction")) viewer.currentWaveDirection = Mathf.Clamp01(p.Float("wave_direction", 1f));
        if (p.Has("arch")) viewer.currentArch = Mathf.Max(0f, p.Float("arch", HairCard.ArchNeutral));
        if (p.Has("u_scale")) viewer.currentUScale = p.Float("u_scale", 1f);
        if (p.Has("v_scale")) viewer.currentVScale = p.Float("v_scale", 1f);
        if (p.Has("u_offset")) viewer.currentUOffset = p.Float("u_offset", 0f);
        if (p.Has("v_offset")) viewer.currentVOffset = p.Float("v_offset", 0f);
        CallNoArgs("PushAllGroomSliders");
        SyncRootState(viewer.currentGroupId);
    }

    // GroomRootStateAuthority keeps each group's "root" slider values and, a frame later, either
    // captures the viewer's values or - once the group has a POST or clumper - pushes its stored
    // root BACK into the viewer. The variance controller also builds on that stored root. Values
    // written here land in the viewer immediately, so without this the authority could still be
    // holding a new group's 0.2 defaults: a batch that placed 0.085 cards and then set Length
    // variance or added a POST rebuilt them all around 0.2. Writing the root (and recording it as
    // last pushed, so it is not mistaken for a user slider move) keeps every system in step.
    void SyncRootState(int gid)
    {
        GroomRootStateAuthority ra = FindFirstObjectByType<GroomRootStateAuthority>();
        if (ra == null) return;
        Type t = typeof(GroomRootStateAuthority);
        MethodInfo read = t.GetMethod("ReadViewer", Private);
        if (read == null) throw new CommandException("HairBrush internals changed: GroomRootStateAuthority.ReadViewer is missing.");
        object state = read.Invoke(ra, null);
        foreach (string field in new[] { "roots", "lastPushed" })
            if (t.GetField(field, Private)?.GetValue(ra) is System.Collections.IDictionary d) d[gid] = state;
    }

    object SetGroupParams(Args a)
    {
        RequireModel();
        int gid = ResolveGroup(a);
        Args p = a.Obj("params");
        if (p == null || p.Keys.Count == 0) throw new CommandException("params is empty.");
        foreach (string key in p.Keys)
            if (!ParamHandlers.Any(h => h.key == key)) throw new CommandException("Unknown card parameter '" + key + "'.");

        ModifierContextExit.LeaveEverything(viewer);
        EnsureGroupSelected(gid);
        ClearCardSelection();

        bool relative = Field<bool>("isRelativeMode");
        SetField("isRelativeMode", false);
        try
        {
            foreach (var (key, handler) in ParamHandlers)
            {
                if (!p.Has(key)) continue;
                MethodInfo m = typeof(ModelViewer).GetMethod(handler, BindingFlags.Instance | BindingFlags.Public);
                if (m == null) throw new CommandException("HairBrush internals changed: ModelViewer." + handler + " is missing.");
                m.Invoke(viewer, new object[] { p.Float(key, 0f) });
            }
        }
        finally { SetField("isRelativeMode", relative); }

        CallNoArgs("PushAllGroomSliders");
        SyncRootState(viewer.currentGroupId);
        return new Dictionary<string, object> { { "group_id", gid }, { "params", CurrentParams() } };
    }

    // ---------------------------------------------------------------------------------
    // Placement
    // ---------------------------------------------------------------------------------

    HairCard Pin(Vector3 point, Vector3 normal, bool mirror)
    {
        if (mirror) return (HairCard)Call("PinHairCard", point, normal);
        HairCard card = (HairCard)Call("SpawnHairCard", point, normal, false);
        return card;
    }

    object PlaceCards(Args a)
    {
        HeadFrame f = Frame();
        int gid = ResolveGroup(a);
        EnsureGroupSelected(gid);
        ClearCardSelection();
        ApplyPlacementDefaults(a.Obj("params"));
        bool mirror = a.Bool("mirror", true);

        int before = AllCards().Length, missed = 0;
        foreach (Dictionary<string, object> p in a.Objects("points"))
        {
            if (ResolvePoint(f, p, out Vector3 point, out Vector3 normal)) Pin(point, normal, mirror);
            else missed++;
        }
        CallNoArgs("RefreshGroupListUI");
        int placed = AllCards().Length - before;
        StatusToast.Show("Claude placed " + placed + " cards in " + GroupLabel(gid));
        return new Dictionary<string, object> { { "group_id", gid }, { "placed", placed }, { "missed_surface", missed }, { "symmetry_mirrored", mirror && GroomSymmetryAuthority.Enabled } };
    }

    static float WrapAz(float az)
    {
        az = Mathf.Repeat(az + 180f, 360f) - 180f;
        return az;
    }

    // az_min > az_max means the range wraps through the back (e.g. 120 .. -120 is the back third).
    static bool InAz(float az, float min, float max)
    {
        az = WrapAz(az); min = WrapAz(min); max = WrapAz(max);
        if (Mathf.Approximately(min, max)) return true;
        return min <= max ? az >= min && az <= max : az >= min || az <= max;
    }

    class Region
    {
        public float azMin, azMax, elMin, elMax;
        public bool Contains(float az, float el) => el >= elMin && el <= elMax && InAz(az, azMin, azMax);
        public static Region From(Args r) => new Region
        {
            azMin = r.Float("az_min", -180f), azMax = r.Float("az_max", 180f),
            elMin = r.Float("el_min", -90f), elMax = r.Float("el_max", 90f)
        };
    }

    // Covers a patch of scalp evenly, like the EVEN brush but over a whole region at once.
    // Candidates come from a Fibonacci sphere (uniform, no grid seams), are shuffled by seed, cast
    // onto the head, and accepted greedily if nothing already accepted - existing cards of the
    // group included, and the symmetry mirror of every accepted point - lies within `spacing`.
    object FillRegion(Args a)
    {
        HeadFrame f = Frame();
        int gid = ResolveGroup(a);
        Region region = Region.From(a);
        // A hairline: the lower boundary as [az, el] points (interpolated by az, mirrored to the
        // other side automatically when only one side is given), roughened by edge_jitter
        // degrees of smooth noise, with cards thinning out over the last edge_falloff degrees.
        List<Vector2> lowerEdge = a.Has("lower_edge") ? a.List("lower_edge").Select(p => { Vector3 v = Args.ToVec2(p); return new Vector2(v.x, v.y); }).OrderBy(p => p.x).ToList() : null;
        float edgeJitter = Mathf.Max(0f, a.Float("edge_jitter", 0f));
        float edgeFalloff = Mathf.Max(0f, a.Float("edge_falloff", 0f));
        float edgeSpacingScale = Mathf.Max(1f, a.Float("edge_spacing_scale", 2.2f));
        int edgeSeed = a.Int("seed", 1);
        List<Region> excludes = a.Has("exclude") ? a.Objects("exclude").Select(e => Region.From(new Args(e))).ToList() : new List<Region>();
        float spacing = Mathf.Max(.002f, a.Float("spacing", .01f));
        int maxCards = Mathf.Clamp(a.Int("max_cards", 3000), 1, 30000);
        bool mirror = a.Bool("mirror", true);
        bool avoidExisting = a.Bool("avoid_existing", true);
        System.Random rng = new System.Random(a.Int("seed", 1));

        EnsureGroupSelected(gid);
        ClearCardSelection();
        ApplyPlacementDefaults(a.Obj("params"));

        float r = Mathf.Max(f.bounds.extents.x, f.bounds.extents.y, f.bounds.extents.z);
        int n = Mathf.Clamp(Mathf.CeilToInt(4f * Mathf.PI * r * r / (spacing * spacing) * 4f), 2000, 400000);
        List<Vector3> candidates = new List<Vector3>();
        float golden = Mathf.PI * (3f - Mathf.Sqrt(5f));
        for (int i = 0; i < n; i++)
        {
            float y = 1f - 2f * (i + .5f) / n;
            float rad = Mathf.Sqrt(1f - y * y);
            float th = golden * i;
            Vector3 local = new Vector3(Mathf.Cos(th) * rad, y, Mathf.Sin(th) * rad);
            Vector3 dir = local.x * f.right + local.y * f.up + local.z * f.front;
            AzEl(f, dir, out float az, out float el);
            if (!region.Contains(az, el)) continue;
            if (excludes.Any(x => x.Contains(az, el))) continue;
            if (EdgeDistance(az, el, lowerEdge, edgeJitter, edgeSeed) < 0f) continue;
            candidates.Add(dir);
        }
        for (int i = candidates.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
        }

        SpatialHash occupied = new SpatialHash(spacing);
        if (avoidExisting)
            foreach (HairCard c in AllCards())
                if (c != null && c.groupId == gid) occupied.Add(c.GetSpawnHitPoint());

        bool mirroring = mirror && GroomSymmetryAuthority.Enabled;
        int before = AllCards().Length, accepted = 0;
        foreach (Vector3 dir in candidates)
        {
            if (accepted >= maxCards) break;
            if (!CastToSurface(f, dir, out RaycastHit hit)) continue;
            float local = spacing;
            if (edgeFalloff > 0f)
            {
                AzEl(f, dir, out float caz, out float cel);
                float d = Mathf.Min(EdgeDistance(caz, cel, lowerEdge, edgeJitter, edgeSeed), cel - region.elMin);
                if (d < edgeFalloff) local = spacing * Mathf.Lerp(edgeSpacingScale, 1f, Mathf.SmoothStep(0f, 1f, d / edgeFalloff));
            }
            if (occupied.AnyWithin(hit.point, local)) continue;

            Vector3 mirrored = default;
            bool hasMirror = mirroring && GroomSymmetryAuthority.TryMirrorPoint(hit.point, out mirrored);
            if (hasMirror && occupied.AnyWithin(mirrored, local)) continue;

            Pin(hit.point, hit.normal, mirror);
            occupied.Add(hit.point);
            if (hasMirror) occupied.Add(mirrored);
            accepted++;
        }
        CallNoArgs("RefreshGroupListUI");
        int placed = AllCards().Length - before;
        StatusToast.Show("Claude filled a region of " + GroupLabel(gid) + " with " + placed + " cards");
        return new Dictionary<string, object>
        {
            { "group_id", gid }, { "placed", placed }, { "primary_points", accepted },
            { "hit_max_cards", accepted >= maxCards }, { "symmetry_mirrored", mirroring }
        };
    }

    // Signed distance in degrees of elevation above the (jittered) lower edge; positive = inside.
    // No edge given means everything is inside.
    static float EdgeDistance(float az, float el, List<Vector2> edge, float jitter, int seed)
    {
        if (edge == null || edge.Count == 0) return float.MaxValue;
        float a = Mathf.Abs(WrapAz(az));
        // One-sided edges (all az >= 0) are mirrored by using |az|; a full edge uses az as-is.
        bool oneSided = edge.All(p => p.x >= 0f);
        float x = oneSided ? a : WrapAz(az);
        float lower;
        if (x <= edge[0].x) lower = edge[0].y;
        else if (x >= edge[edge.Count - 1].x) lower = edge[edge.Count - 1].y;
        else
        {
            int i = 1;
            while (edge[i].x < x) i++;
            lower = Mathf.Lerp(edge[i - 1].y, edge[i].y, Mathf.InverseLerp(edge[i - 1].x, edge[i].x, x));
        }
        if (jitter > 0f)
        {
            // Sum of incommensurate sines: smooth, irregular, repeatable by seed, and the same on
            // both sides of a one-sided edge so symmetry keeps the hairline symmetric.
            float s = seed * 1.618f;
            float n = .5f * Mathf.Sin(x * .21f + s) + .3f * Mathf.Sin(x * .53f + s * 2.1f) + .2f * Mathf.Sin(x * 1.37f + s * 3.7f);
            lower += n * jitter;
        }
        return el - lower;
    }

    class SpatialHash
    {
        readonly float cell;
        readonly Dictionary<Vector3Int, List<Vector3>> cells = new Dictionary<Vector3Int, List<Vector3>>();
        public SpatialHash(float cell) { this.cell = cell; }
        Vector3Int Key(Vector3 p) => new Vector3Int(Mathf.FloorToInt(p.x / cell), Mathf.FloorToInt(p.y / cell), Mathf.FloorToInt(p.z / cell));
        public void Add(Vector3 p)
        {
            Vector3Int k = Key(p);
            if (!cells.TryGetValue(k, out List<Vector3> list)) cells[k] = list = new List<Vector3>();
            list.Add(p);
        }
        public bool AnyWithin(Vector3 p, float d)
        {
            Vector3Int k = Key(p);
            float d2 = d * d;
            int r = Mathf.Max(1, Mathf.CeilToInt(d / cell));
            for (int x = -r; x <= r; x++)
                for (int y = -r; y <= r; y++)
                    for (int z = -r; z <= r; z++)
                        if (cells.TryGetValue(new Vector3Int(k.x + x, k.y + y, k.z + z), out List<Vector3> list))
                            foreach (Vector3 q in list) if ((q - p).sqrMagnitude < d2) return true;
            return false;
        }
    }

    object EraseCards(Args a)
    {
        int gid = ResolveGroup(a);
        bool all = a.Bool("all", false);
        List<HairCard> victims = new List<HairCard>();
        HairCard[] cards = AllCards().Where(c => c != null && c.groupId == gid).ToArray();

        if (all) victims.AddRange(cards);
        else
        {
            HeadFrame f = Frame();
            Dictionary<string, object> at = a.Has("point") ? a.Obj("point").Dict : null;
            if (at == null) throw new CommandException("Give either all=true or a point {az, el} with a radius.");
            if (!ResolvePoint(f, at, out Vector3 point, out _)) throw new CommandException("That point does not land on the head.");
            float radius = Mathf.Max(.001f, a.Float("radius", .03f));
            List<Vector3> centres = new List<Vector3> { point };
            if (a.Bool("mirror", true) && GroomSymmetryAuthority.Enabled && GroomSymmetryAuthority.TryMirrorPoint(point, out Vector3 m)) centres.Add(m);
            foreach (HairCard c in cards)
                if (centres.Any(p => Vector3.Distance(c.GetSpawnHitPoint(), p) <= radius)) victims.Add(c);
        }

        foreach (HairCard c in victims) Destroy(c.gameObject);
        CallNoArgs("RefreshGroupListUI");
        if (victims.Count > 0) StatusToast.Show("Claude erased " + victims.Count + " cards from " + GroupLabel(gid));
        return new Dictionary<string, object> { { "group_id", gid }, { "erased", victims.Count } };
    }

    // ---------------------------------------------------------------------------------
    // Groups
    // ---------------------------------------------------------------------------------

    string GroupLabel(int gid) => GroupNames.TryGetValue(gid, out string n) ? n : "Group " + gid;

    // The + GROUP button's lambda, step for step (ModelViewer.BuildGroupManagementUI).
    object CreateGroup(Args a)
    {
        RequireModel();
        ModifierContextExit.LeaveEverything(viewer);
        int newId = (int)CallNoArgs("GetNextAvailableGroupId");
        GroupIds.Add(newId);
        GroupNames[newId] = a.Str("name", "Group " + newId);
        Field<Dictionary<int, float>>("groupUScales")[newId] = 1f;
        Field<Dictionary<int, float>>("groupVScales")[newId] = 1f;
        Field<Dictionary<int, float>>("groupUOffsets")[newId] = 0f;
        Field<Dictionary<int, float>>("groupVOffsets")[newId] = 0f;
        viewer.ResetSoloState();
        Call("SelectGroup", newId);
        return new Dictionary<string, object> { { "group_id", newId }, { "name", GroupNames[newId] } };
    }

    object SelectGroupCommand(Args a)
    {
        int gid = ResolveGroup(a);
        if (viewer.currentGroupId != gid)
        {
            ModifierContextExit.LeaveEverything(viewer);
            Call("SelectGroup", gid);
        }
        return new Dictionary<string, object> { { "group_id", gid }, { "params", CurrentParams() } };
    }

    object RenameGroup(Args a)
    {
        int gid = ResolveGroup(a);
        string name = a.Str("name", null);
        if (string.IsNullOrWhiteSpace(name)) throw new CommandException("name is required.");
        GroupNames[gid] = name.Trim();
        CallNoArgs("RefreshGroupListUI");
        return new Dictionary<string, object> { { "group_id", gid }, { "name", GroupNames[gid] } };
    }

    object DeleteGroup(Args a)
    {
        int gid = ResolveGroup(a);
        if (!viewer.CanDeleteGroup(gid)) throw new CommandException("HairBrush will not delete group " + gid + " (the last remaining group cannot be deleted).");
        ModifierContextExit.LeaveEverything(viewer);

        // Group ids are recycled (the next + GROUP takes the lowest free id), and the POST,
        // guide and clumper registries are keyed by id. Cleared here, explicitly, so a group
        // created straight afterwards can never inherit this one's modifiers.
        Posts().ImportGroup(gid, new List<PostAffectorSaveData>());
        GuideCurveManager gm = FindFirstObjectByType<GuideCurveManager>();
        if (gm != null) foreach (GuideCurveManager.GuideCurve g in gm.GetGroupGuides(gid).ToList()) gm.RemoveGuide(g);
        GroupClumperManager cm = FindFirstObjectByType<GroupClumperManager>();
        MethodInfo removeClumper = typeof(GroupClumperManager).GetMethod("RemoveClumper", Private);
        if (cm != null && removeClumper != null) foreach (GroupClumperManager.GroupClumper c in cm.GetGroupClumpers(gid).ToList()) removeClumper.Invoke(cm, new object[] { c });

        viewer.DeleteGroupAndCardsConfirmed(gid);
        return new Dictionary<string, object> { { "deleted", gid }, { "current_group_id", viewer.currentGroupId } };
    }

    object SetGroupFlags(Args a)
    {
        int gid = ResolveGroup(a);
        if (a.Has("single_sided")) GroupSidednessAuthority.SetSingleSided(gid, a.Bool("single_sided", false));
        if (a.Has("flip_normals")) GroupNormalFlipAuthority.SetFlipped(gid, a.Bool("flip_normals", false));
        return new Dictionary<string, object>
        {
            { "group_id", gid },
            { "single_sided", GroupSidednessAuthority.IsSingleSided(gid) },
            { "normals_flipped", GroupNormalFlipAuthority.IsFlipped(gid) }
        };
    }

    // ---------------------------------------------------------------------------------
    // Variance and shape curves
    // ---------------------------------------------------------------------------------

    static readonly string[] VarianceChannels = { "Length", "Width", "Bend", "Twist", "AngleX", "AngleY", "AngleZ", "CurlFrequency", "CurlDiameter", "WaveAmplitude", "WaveFrequency", "WaveDirection", "Arch" };

    object SetVariance(Args a)
    {
        int gid = ResolveGroup(a);
        GroomVarianceController vc = FindFirstObjectByType<GroomVarianceController>();
        if (vc == null) throw new CommandException("The variance controller is not running yet - load a model first.");

        List<VarianceChannelSaveData> settings = vc.ExportGroupSettings(gid) ?? new List<VarianceChannelSaveData>();
        foreach (Dictionary<string, object> entry in a.Objects("channels"))
        {
            Args e = new Args(entry);
            string channel = VarianceChannels.FirstOrDefault(c => string.Equals(c, e.Str("channel", ""), StringComparison.OrdinalIgnoreCase));
            if (channel == null) throw new CommandException("Unknown variance channel '" + e.Str("channel", "") + "'. Valid: " + string.Join(", ", VarianceChannels));
            VarianceChannelSaveData existing = settings.FirstOrDefault(s => s.channel == channel);
            if (existing == null) settings.Add(existing = new VarianceChannelSaveData { channel = channel, seed = 1 });
            existing.amount = Mathf.Max(0f, e.Float("amount", existing.amount));
            existing.seed = e.Int("seed", existing.seed);
        }
        vc.ImportGroupSettings(gid, settings);
        return new Dictionary<string, object>
        {
            { "group_id", gid },
            { "variance", vc.ExportGroupSettings(gid).Where(v => v.amount != 0f).Select(v => (object)new Dictionary<string, object> { { "channel", v.channel }, { "amount", v.amount }, { "seed", v.seed } }).ToList() }
        };
    }

    object SetShapeCurve(Args a)
    {
        int gid = ResolveGroup(a);
        string chName = a.Str("channel", "");
        if (!Enum.TryParse(chName, true, out GroomShapeCurveChannel channel))
            throw new CommandException("Unknown shape channel '" + chName + "'. Valid: " + string.Join(", ", Enum.GetNames(typeof(GroomShapeCurveChannel))));

        if (a.Bool("reset", false)) GroomShapeCurveRegistry.Reset(gid, channel);
        else
        {
            List<object> keys = a.List("keys");
            if (keys == null || keys.Count < 2) throw new CommandException("keys needs at least two [t, value] pairs, t from 0 (root) to 1 (tip).");
            AnimationCurve curve = new AnimationCurve();
            foreach (object k in keys)
            {
                if (!(k is List<object> pair) || pair.Count < 2) throw new CommandException("Each key must be [t, value].");
                curve.AddKey(new Keyframe(Mathf.Clamp01(Args.ToFloat(pair[0])), Args.ToFloat(pair[1])));
            }
            for (int i = 0; i < curve.length; i++) curve.SmoothTangents(i, 0f);
            GroomShapeCurveRegistry.SetCurve(gid, channel, curve);
        }
        GroomShapeCurveRegistry.RefreshGroup(gid);
        return new Dictionary<string, object>
        {
            { "group_id", gid }, { "channel", channel.ToString() },
            { "keys", GroomShapeCurveRegistry.Export(gid, channel).Select(k => (object)new List<object> { k.time, k.value }).ToList() }
        };
    }

    // ---------------------------------------------------------------------------------
    // Guides
    // ---------------------------------------------------------------------------------

    GuideCurveManager Guides()
    {
        GuideCurveManager gm = FindFirstObjectByType<GuideCurveManager>();
        if (gm == null) throw new CommandException("The guide manager is not running.");
        return gm;
    }

    object DescribeGuide(GuideCurveManager.GuideCurve g, HeadFrame? frame)
    {
        Dictionary<string, object> d = new Dictionary<string, object>
        {
            { "id", g.id }, { "group_id", g.groupId }, { "contact", g.contact }, { "normal", g.normal },
            { "nodes_world", GuideCurveManager.WorldPoints(g).Skip(1).Cast<object>().ToList() },
            { "amount", g.amount }, { "radius", g.radius }, { "falloff", g.falloff }, { "spin", g.spin }
        };
        if (frame.HasValue)
        {
            AzEl(frame.Value, g.contact - frame.Value.center, out float az, out float el);
            d["az"] = az; d["el"] = el;
        }
        return d;
    }

    // Shape a guide either from explicit world nodes, or from a flow direction: the strand lifts
    // off the scalp along the normal, then sweeps toward `direction` and ends `length` away.
    // That is the common case ("these fall down the back", "swept to the left") in one call.
    void ShapeGuide(HeadFrame f, GuideCurveManager.GuideCurve g, Args a)
    {
        List<Vector3> world = null;
        if (a.Has("nodes_world"))
        {
            world = a.List("nodes_world").Select(Args.ToVec).ToList();
        }
        else if (a.Has("flow"))
        {
            world = BuildFlowNodes(f, g.contact, a.Obj("flow"));
        }
        else if (a.Has("direction") || a.Has("length"))
        {
            Vector3 dir = ReadDirection(f, a, "direction", -f.up);
            float length = Mathf.Max(.01f, a.Float("length", viewer.currentLength));
            float lift = Mathf.Clamp(a.Float("lift", .15f), 0f, 1f);
            int count = Mathf.Clamp(a.Int("node_count", 4), 2, 20);
            world = new List<Vector3>();
            for (int i = 1; i <= count; i++)
            {
                float t = i / (float)count;
                // Lift rises fast and then holds, so the root stands off the scalp before the
                // strand turns - the way real hair leaves the head before it falls.
                float rise = lift * length * Mathf.Sin(Mathf.Min(1f, t * 2f) * Mathf.PI * .5f);
                world.Add(g.contact + g.normal * rise + dir * (length * t));
            }
        }
        if (world == null) return;
        if (world.Count < 2 || world.Count > 20) throw new CommandException("A guide needs 2 to 20 nodes (not counting the root).");
        g.nodesLocal = world.Select(w => GuideCurveManager.ToLocal(g, w)).ToList();
        GuideCurveManager.NormaliseRoll(g);
    }

    void ApplyGuideSettings(GuideCurveManager.GuideCurve g, Args a)
    {
        if (a.Has("amount")) g.amount = Mathf.Clamp01(a.Float("amount", 0f));
        if (a.Has("radius")) g.radius = Mathf.Clamp(a.Float("radius", g.radius), .001f, .25f);
        if (a.Has("falloff")) g.falloff = Mathf.Clamp(a.Float("falloff", g.falloff), 0f, .25f);
        if (a.Has("spin")) GuideCurveManager.SetSpin(g, a.Float("spin", 0f));
    }

    object AddGuide(Args a)
    {
        HeadFrame f = Frame();
        int gid = ResolveGroup(a);
        if (!ResolvePoint(f, a.Dict, out Vector3 point, out Vector3 normal)) throw new CommandException("The guide root does not land on the head.");
        EnsureGroupSelected(gid);
        GuideCurveManager gm = Guides();
        GuideCurveManager.GuideCurve g = gm.CreateGuide(gid, point, normal);
        ShapeGuide(f, g, a);
        if (!a.Has("amount")) g.amount = .8f;
        ApplyGuideSettings(g, a);
        // CreateGuide selects the new guide, and a selected guide holds the grooming input lock -
        // the user's next click on the head would be swallowed. Leave it unselected.
        gm.ClearSelection();
        object primary = DescribeGuide(g, f);
        if (!a.Bool("mirror", false)) return primary;
        return new Dictionary<string, object> { { "guide", primary }, { "mirror", MirrorGuide(f, g) } };
    }

    object EditGuide(Args a)
    {
        HeadFrame f = Frame();
        GuideCurveManager gm = Guides();
        GuideCurveManager.GuideCurve g = gm.FindGuidePublic(a.Int("guide_id", -1));
        if (g == null) throw new CommandException("No guide with that id. Use hb_get_group to list guides.");
        if (a.Has("az") || a.Has("el") || a.Has("position"))
        {
            if (!ResolvePoint(f, a.Dict, out Vector3 point, out Vector3 normal)) throw new CommandException("The new root does not land on the head.");
            Vector3[] keep = GuideCurveManager.WorldPoints(g);
            gm.MoveGuideRoot(g, point, normal, keep, 0);
        }
        ShapeGuide(f, g, a);
        ApplyGuideSettings(g, a);
        return DescribeGuide(g, f);
    }

    // Neutralise, give the mesh evaluator a frame to restore the cards, then remove - the same
    // two-phase pattern the UI's [-] button follows.
    IEnumerator RemoveGuide(Args a, Action<object> ok)
    {
        GuideCurveManager gm = Guides();
        GuideCurveManager.GuideCurve g = gm.FindGuidePublic(a.Int("guide_id", -1));
        if (g == null) throw new CommandException("No guide with that id.");
        g.amount = 0f;
        yield return null;
        yield return null;
        gm.RemoveGuide(g);
        MarkEdited();
        ok(new Dictionary<string, object> { { "removed", g.id } });
    }

    // ---------------------------------------------------------------------------------
    // Clumpers
    // ---------------------------------------------------------------------------------

    GroupClumperManager Clumpers()
    {
        GroupClumperManager cm = FindFirstObjectByType<GroupClumperManager>();
        if (cm == null) throw new CommandException("The clumper manager is not running.");
        return cm;
    }

    GroupClumperManager.GroupClumper FindClumper(int id) => Clumpers().GetAllClumpers().FirstOrDefault(c => c.id == id);

    object DescribeClumper(GroupClumperManager.GroupClumper c, HeadFrame? frame)
    {
        Dictionary<string, object> d = new Dictionary<string, object>
        {
            { "id", c.id }, { "group_id", c.groupId }, { "center", c.center }, { "mode", c.mode.ToString() },
            { "amount", c.amount }, { "count", c.count }, { "seed", c.seed }, { "radius", c.radius }, { "falloff", c.falloff }
        };
        if (frame.HasValue)
        {
            AzEl(frame.Value, c.center - frame.Value.center, out float az, out float el);
            d["az"] = az; d["el"] = el;
        }
        return d;
    }

    void ApplyClumperSettings(GroupClumperManager.GroupClumper c, Args a)
    {
        if (a.Has("mode"))
        {
            if (!Enum.TryParse(a.Str("mode", ""), true, out GroupClumperManager.ClumpMode mode))
                throw new CommandException("mode must be Singular, DispersedEvenly or FromPoint.");
            c.mode = mode;
        }
        if (a.Has("amount")) c.amount = Mathf.Clamp01(a.Float("amount", 0f));
        if (a.Has("count")) c.count = Mathf.Clamp(a.Int("count", 6), 1, 24);
        if (a.Has("seed")) c.seed = a.Int("seed", 1);
        if (a.Has("radius")) c.radius = Mathf.Max(.001f, a.Float("radius", c.radius));
        if (a.Has("falloff")) c.falloff = Mathf.Max(0f, a.Float("falloff", c.falloff));
        // SCOPE is per group in HairBrush: CONTIG keeps clumps on the surface island they sit on,
        // so scalp hair never clumps with ear or beard hair.
        if (a.Has("scope"))
        {
            string scope = a.Str("scope", "all").ToLowerInvariant();
            if (scope != "all" && scope != "contig") throw new CommandException("scope must be all or contig.");
            SurfaceIslandScope.SetClumperContiguous(c.groupId, scope == "contig");
        }
        Clumpers().Invalidate(c);
    }

    object AddClumper(Args a)
    {
        HeadFrame f = Frame();
        int gid = ResolveGroup(a);
        if (!ResolvePoint(f, a.Dict, out Vector3 point, out Vector3 normal)) throw new CommandException("The clumper centre does not land on the head.");
        EnsureGroupSelected(gid);
        GroupClumperManager cm = Clumpers();
        GroupClumperManager.GroupClumper c = cm.CreateClumper(gid, point, normal);
        if (!a.Has("amount")) c.amount = .6f;
        ApplyClumperSettings(c, a);
        cm.ClearSelection();
        object primary = DescribeClumper(c, f);
        if (!a.Bool("mirror", false)) return primary;
        return new Dictionary<string, object> { { "clumper", primary }, { "mirror", MirrorClumper(f, c) } };
    }

    object EditClumper(Args a)
    {
        HeadFrame f = Frame();
        GroupClumperManager.GroupClumper c = FindClumper(a.Int("clumper_id", -1));
        if (c == null) throw new CommandException("No clumper with that id. Use hb_get_group to list clumpers.");
        if (a.Has("az") || a.Has("el") || a.Has("position"))
        {
            if (!ResolvePoint(f, a.Dict, out Vector3 point, out Vector3 normal)) throw new CommandException("The new centre does not land on the head.");
            c.center = point;
            c.normal = normal;
        }
        ApplyClumperSettings(c, a);
        return DescribeClumper(c, f);
    }

    IEnumerator RemoveClumper(Args a, Action<object> ok)
    {
        GroupClumperManager cm = Clumpers();
        GroupClumperManager.GroupClumper c = FindClumper(a.Int("clumper_id", -1));
        if (c == null) throw new CommandException("No clumper with that id.");
        c.amount = 0f;
        cm.Invalidate(c);
        yield return null;
        yield return null;
        MethodInfo remove = typeof(GroupClumperManager).GetMethod("RemoveClumper", Private);
        if (remove == null) throw new CommandException("HairBrush internals changed: GroupClumperManager.RemoveClumper is missing.");
        remove.Invoke(cm, new object[] { c });
        MarkEdited();
        ok(new Dictionary<string, object> { { "removed", c.id } });
    }

    // ---------------------------------------------------------------------------------
    // Material
    // ---------------------------------------------------------------------------------

    // The global hair material - the one every group uses unless the texture workspace assigned
    // it another. Tint goes through MaterialEditorManager.SetTint, the swatch's own path, so the
    // viewer's material and every card are re-pointed at it. The float properties are written on
    // the entry's material directly, which is exactly where MaterialProjectPersistenceBridge reads
    // them back from on save.
    object SetHairMaterial(Args a)
    {
        MaterialEditorManager mem = FindFirstObjectByType<MaterialEditorManager>();
        if (mem == null) throw new CommandException("The material editor is not running - load a model first.");
        Type t = typeof(MaterialEditorManager);
        System.Collections.IList entries = t.GetField("materials", Private)?.GetValue(mem) as System.Collections.IList;
        MethodInfo globalIndex = t.GetMethod("GetGlobalMaterialIndex", Private);
        MethodInfo setTint = t.GetMethod("SetTint", Private);
        MethodInfo apply = t.GetMethod("ApplyAssignments", Private);
        if (entries == null || globalIndex == null || setTint == null || apply == null)
            throw new CommandException("HairBrush internals changed: MaterialEditorManager material API is missing.");
        int index = (int)globalIndex.Invoke(mem, null);
        if (index < 0 || index >= entries.Count) throw new CommandException("There is no hair material yet.");
        object entry = entries[index];
        Material mat = entry.GetType().GetField("material")?.GetValue(entry) as Material;
        if (mat == null) throw new CommandException("The hair material is missing.");

        void SetFloat(string key, string property)
        {
            if (!a.Has(key)) return;
            if (!mat.HasProperty(property)) throw new CommandException("This hair shader has no " + property + ".");
            mat.SetFloat(property, Mathf.Clamp01(a.Float(key, 0f)));
        }
        SetFloat("smoothness", "_Smooth");
        SetFloat("metallic", "_Metal");
        SetFloat("dither", "_DitheringAmt");

        if (a.Has("tint"))
        {
            List<object> c = a.List("tint");
            if (c == null || c.Count != 3) throw new CommandException("tint must be [r, g, b] with components 0-1.");
            Color colour = new Color(Mathf.Clamp01(Args.ToFloat(c[0])), Mathf.Clamp01(Args.ToFloat(c[1])), Mathf.Clamp01(Args.ToFloat(c[2])), 1f);
            setTint.Invoke(mem, new object[] { entry, colour, null });
        }
        apply.Invoke(mem, null);

        Color tint = mat.HasProperty(MaterialEditorManager.TintProperty) ? mat.GetColor(MaterialEditorManager.TintProperty) : Color.white;
        return new Dictionary<string, object>
        {
            { "tint", new List<object> { tint.r, tint.g, tint.b } },
            { "smoothness", mat.HasProperty("_Smooth") ? mat.GetFloat("_Smooth") : (object)null },
            { "metallic", mat.HasProperty("_Metal") ? mat.GetFloat("_Metal") : (object)null },
            { "dither", mat.HasProperty("_DitheringAmt") ? mat.GetFloat("_DitheringAmt") : (object)null }
        };
    }

    // ---------------------------------------------------------------------------------
    // UV rectangles and predetermined UVs
    // ---------------------------------------------------------------------------------

    // The rectangles are the strips cut out of the active hair material's atlas. They live in the
    // texture workspace; MaterialUVRectAuthority notices the change and files them under the
    // material, which is where the project save reads them from.
    TextureUVRectWorkspace Workspace()
    {
        TextureUVRectWorkspace ws = FindFirstObjectByType<TextureUVRectWorkspace>();
        if (ws == null) throw new CommandException("The UV rectangle workspace is not running.");
        return ws;
    }

    static object DescribeRect(UVRectSaveData r) => new Dictionary<string, object>
    {
        { "id", r.id }, { "u_min", r.uMin }, { "v_min", r.vMin }, { "u_max", r.uMax }, { "v_max", r.vMax }, { "flip_v", r.flipV }
    };

    object GetUVRects() => new Dictionary<string, object>
    {
        { "rects", Workspace().ExportDefinitions().OrderBy(r => r.id).Select(DescribeRect).ToList() },
        { "note", "Rect ids are what hb_set_group_uv's min_id/max_id select between. V runs bottom (0) to top (1); a card's ROOT lands at v_max unless flip_v." }
    };

    IEnumerator SetUVRects(Args a, Action<object> ok)
    {
        TextureUVRectWorkspace ws = Workspace();
        if (a.Bool("auto_detect", false))
        {
            TextureUVRectAutoAuthority auto = FindFirstObjectByType<TextureUVRectAutoAuthority>();
            MethodInfo detect = typeof(TextureUVRectAutoAuthority).GetMethod("AutoDetectRectangles", Private);
            if (auto == null || detect == null) throw new CommandException("UV auto-detect is not available.");
            detect.Invoke(auto, null);
        }
        else
        {
            List<UVRectSaveData> rects = new List<UVRectSaveData>();
            foreach (Dictionary<string, object> d in a.Objects("rects"))
            {
                Args r = new Args(d);
                rects.Add(new UVRectSaveData
                {
                    id = r.Int("id", 0),
                    uMin = r.Float("u_min", 0f), vMin = r.Float("v_min", 0f),
                    uMax = r.Float("u_max", 1f), vMax = r.Float("v_max", 1f),
                    flipV = r.Bool("flip_v", false)
                });
            }
            ws.ImportDefinitions(rects);
        }
        // Let MaterialUVRectAuthority pick the new set up before anything reads it back.
        yield return null;
        yield return null;
        MarkEdited();
        if (ws.ExportDefinitions().Count == 0) throw new CommandException("No UV rectangles were defined (auto-detect needs the material's base-colour texture).");
        ok(GetUVRects());
    }

    GroupPredeterminedUVController UVController()
    {
        GroupPredeterminedUVController c = FindFirstObjectByType<GroupPredeterminedUVController>();
        if (c == null) throw new CommandException("The predetermined-UV controller is not running.");
        return c;
    }

    // GroupUVSettings is a private nested class; its fields are reached by name.
    object GroupUVSettingsOf(int gid)
    {
        MethodInfo get = typeof(GroupPredeterminedUVController).GetMethod("GetSettings", Private);
        if (get == null) throw new CommandException("HairBrush internals changed: GroupPredeterminedUVController.GetSettings is missing.");
        return get.Invoke(UVController(), new object[] { gid });
    }

    Dictionary<string, object> DescribeGroupUV(int gid)
    {
        object s = GroupUVSettingsOf(gid);
        Type t = s.GetType();
        return new Dictionary<string, object>
        {
            { "predetermined", t.GetField("predetermined").GetValue(s) },
            { "min_id", t.GetField("minId").GetValue(s) },
            { "max_id", t.GetField("maxId").GetValue(s) },
            { "seed", t.GetField("seed").GetValue(s) },
            { "flip_v", t.GetField("flipV").GetValue(s) }
        };
    }

    object SetGroupUV(Args a)
    {
        int gid = ResolveGroup(a);
        object s = GroupUVSettingsOf(gid);
        Type t = s.GetType();
        if (a.Has("predetermined")) t.GetField("predetermined").SetValue(s, a.Bool("predetermined", true));
        if (a.Has("min_id")) t.GetField("minId").SetValue(s, Mathf.Max(1, a.Int("min_id", 1)));
        if (a.Has("max_id")) t.GetField("maxId").SetValue(s, Mathf.Max(1, a.Int("max_id", 1)));
        if (a.Has("seed")) t.GetField("seed").SetValue(s, a.Int("seed", 0));
        if (a.Has("flip_v")) t.GetField("flipV").SetValue(s, a.Bool("flip_v", false));

        // Same steps the panel's own inputs take: tidy the range, forget which cards were already
        // assigned so every card is re-rolled, apply now, and repaint the PRE row.
        Type ct = typeof(GroupPredeterminedUVController);
        GroupPredeterminedUVController c = UVController();
        ct.GetMethod("SanitizeRange", BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, new[] { s });
        (ct.GetField("appliedSignatureByCard", Private)?.GetValue(c) as System.Collections.IDictionary)?.Clear();
        ct.GetMethod("ForceApplyGroup", Private)?.Invoke(c, new object[] { gid });
        ct.GetMethod("MaintainRightPanelUI", Private)?.Invoke(c, null);
        return new Dictionary<string, object> { { "group_id", gid }, { "uv", DescribeGroupUV(gid) } };
    }

    // ---------------------------------------------------------------------------------
    // POSTs - localized manipulators
    // ---------------------------------------------------------------------------------

    // A POST is a soft sphere on the scalp that offsets (RELATIVE) or overrides (ABSOLUTE) the
    // card parameters of its group inside radius + falloff. The group's whole POST list is
    // exported, changed and imported back - the same round trip a project load makes - so the
    // evaluator, the rows and the save all see the POST exactly as if it had been loaded.
    PostAffectorManager Posts()
    {
        PostAffectorManager pm = PostAffectorManager.Instance != null ? PostAffectorManager.Instance : FindFirstObjectByType<PostAffectorManager>();
        if (pm == null) throw new CommandException("The POST manager is not running.");
        return pm;
    }

    // Tool-facing name -> PostAffectorControlSaveData field.
    static readonly (string key, string field)[] PostChannels =
    {
        ("length", "length"), ("width", "width"), ("segments", "segments"), ("bend", "bend"), ("twist", "twist"),
        ("embed_depth", "depth"), ("angle_x", "x"), ("angle_y", "y"), ("angle_z", "z"),
        ("u_scale", "uScale"), ("v_scale", "vScale"), ("u_offset", "uOffset"), ("v_offset", "vOffset"),
        ("curl_frequency", "curlFrequency"), ("curl_diameter", "curlDiameter"),
        ("wave_amplitude", "waveAmplitude"), ("wave_frequency", "waveFrequency"), ("wave_direction", "waveDirection"), ("arch", "arch"),
    };

    static Dictionary<string, object> ControlToDict(PostAffectorControlSaveData c)
    {
        Dictionary<string, object> d = new Dictionary<string, object>();
        if (c == null) return d;
        foreach (var (key, field) in PostChannels)
        {
            float v = (float)typeof(PostAffectorControlSaveData).GetField(field).GetValue(c);
            if (Mathf.Abs(v) > 1e-6f) d[key] = v;
        }
        return d;
    }

    static void WriteControl(PostAffectorControlSaveData c, Args values)
    {
        foreach (string key in values.Keys)
        {
            var match = PostChannels.FirstOrDefault(p => p.key == key);
            if (match.key == null) throw new CommandException("Unknown POST channel '" + key + "'.");
            typeof(PostAffectorControlSaveData).GetField(match.field).SetValue(c, values.Float(key, 0f));
        }
    }

    // The group's root controls, so an ABSOLUTE POST starts from what the group actually is.
    PostAffectorControlSaveData RootBaseline(int gid)
    {
        PostAffectorControlSaveData b = new PostAffectorControlSaveData();
        Dictionary<string, object> group = GetGroup(new Args(new Dictionary<string, object> { { "group_id", (double)gid } })) as Dictionary<string, object>;
        if (!(group?["params"] is Dictionary<string, object> p)) return b;
        foreach (var (key, field) in PostChannels)
            if (p.TryGetValue(key, out object v) && v != null)
                typeof(PostAffectorControlSaveData).GetField(field).SetValue(b, Convert.ToSingle(v));
        return b;
    }

    object DescribePost(PostAffectorSaveData p, HeadFrame? frame)
    {
        Vector3 c = new Vector3(p.centerX, p.centerY, p.centerZ);
        Dictionary<string, object> d = new Dictionary<string, object>
        {
            { "id", p.id }, { "label", p.label }, { "center", c }, { "radius", p.radius }, { "falloff", p.falloff },
            { "weight", p.weight }, { "absolute", p.absolute }, { "delta", ControlToDict(p.delta) }
        };
        if (frame.HasValue) { AzEl(frame.Value, c - frame.Value.center, out float az, out float el); d["az"] = az; d["el"] = el; }
        return d;
    }

    static void ApplyPostSettings(PostAffectorSaveData p, Args a)
    {
        if (a.Has("radius")) p.radius = Mathf.Max(.001f, a.Float("radius", p.radius));
        if (a.Has("falloff")) p.falloff = Mathf.Max(0f, a.Float("falloff", p.falloff));
        if (a.Has("weight")) p.weight = Mathf.Clamp01(a.Float("weight", 1f));
        if (a.Has("absolute")) p.absolute = a.Bool("absolute", false);
        if (a.Has("label")) { string l = a.Str("label", ""); p.label = l.Length > 6 ? l.Substring(0, 6) : l; }
        if (p.delta == null) p.delta = new PostAffectorControlSaveData();
        if (a.Has("delta")) WriteControl(p.delta, a.Obj("delta"));
    }

    // POST ids are unique across all groups, as CreateAffector issues them.
    int NextPostId()
    {
        // Also respect the manager's own counter: it has issued ids for POSTs this listing
        // cannot see, and a reused id made later edits act on the wrong POST.
        int id = typeof(PostAffectorManager).GetField("nextId", Private)?.GetValue(Posts()) is int n ? n : 1;
        foreach (int gid in GroupIds)
            foreach (PostAffectorSaveData p in Posts().ExportGroup(gid)) id = Mathf.Max(id, p.id + 1);
        return id;
    }

    object AddPost(Args a)
    {
        HeadFrame f = Frame();
        int gid = ResolveGroup(a);
        if (!ResolvePoint(f, a.Dict, out Vector3 point, out Vector3 normal)) throw new CommandException("The POST centre does not land on the head.");
        ModifierContextExit.LeaveEverything(viewer);
        PostAffectorManager pm = Posts();
        List<PostAffectorSaveData> list = pm.ExportGroup(gid);
        PostAffectorSaveData p = new PostAffectorSaveData
        {
            id = NextPostId(),
            centerX = point.x, centerY = point.y, centerZ = point.z,
            normalX = normal.x, normalY = normal.y, normalZ = normal.z,
            baseline = RootBaseline(gid), delta = new PostAffectorControlSaveData()
        };
        ApplyPostSettings(p, a);
        list.Add(p);
        pm.ImportGroup(gid, list);
        object primary = DescribePost(p, f);
        if (!a.Bool("mirror", false)) return primary;
        return new Dictionary<string, object> { { "post", primary }, { "mirror", MirrorPost(f, gid, p) } };
    }

    (int gid, List<PostAffectorSaveData> list, PostAffectorSaveData post) FindPost(int id)
    {
        foreach (int gid in GroupIds)
        {
            List<PostAffectorSaveData> list = Posts().ExportGroup(gid);
            PostAffectorSaveData p = list.FirstOrDefault(x => x.id == id);
            if (p != null) return (gid, list, p);
        }
        throw new CommandException("No POST with id " + id + ". Use hb_get_group to list them.");
    }

    object EditPost(Args a)
    {
        HeadFrame f = Frame();
        var (gid, list, p) = FindPost(a.Int("post_id", -1));
        if (a.Has("az") || a.Has("el") || a.Has("position"))
        {
            if (!ResolvePoint(f, a.Dict, out Vector3 point, out Vector3 normal)) throw new CommandException("The new centre does not land on the head.");
            p.centerX = point.x; p.centerY = point.y; p.centerZ = point.z;
            p.normalX = normal.x; p.normalY = normal.y; p.normalZ = normal.z;
        }
        if (a.Bool("reset_delta", false)) p.delta = new PostAffectorControlSaveData();
        ApplyPostSettings(p, a);
        ModifierContextExit.LeaveEverything(viewer);
        Posts().ImportGroup(gid, list);
        return DescribePost(p, f);
    }

    object RemovePost(Args a)
    {
        var (gid, list, p) = FindPost(a.Int("post_id", -1));
        list.Remove(p);
        ModifierContextExit.LeaveEverything(viewer);
        Posts().ImportGroup(gid, list);
        return new Dictionary<string, object> { { "removed", p.id }, { "group_id", gid } };
    }

    // ---------------------------------------------------------------------------------
    // Symmetry, files, undo
    // ---------------------------------------------------------------------------------

    object SetSymmetry(Args a)
    {
        bool want = a.Bool("enabled", true);
        if (GroomSymmetryAuthority.Enabled != want) GroomSymmetryAuthority.RequestToggle();
        return new Dictionary<string, object>
        {
            { "symmetry", GroomSymmetryAuthority.Enabled },
            { "plane_reliable", GroomSymmetryAuthority.PlaneIsReliable }
        };
    }

    static string CheckPath(string path, string ext, bool mustExist)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new CommandException("path is required.");
        if (!Path.IsPathRooted(path)) throw new CommandException("Use an absolute path.");
        path = Path.GetFullPath(path);
        if (ext != null && !string.Equals(Path.GetExtension(path), ext, StringComparison.OrdinalIgnoreCase))
            throw new CommandException("The path must end in " + ext + ".");
        if (mustExist && !File.Exists(path)) throw new CommandException("File not found: " + path);
        return path;
    }

    IEnumerator LoadModel(Args a, Action<object> ok)
    {
        string path = CheckPath(a.Str("path", null), ".obj", true);
        RuntimeBuildLoadAuthority build = FindFirstObjectByType<RuntimeBuildLoadAuthority>();
        RuntimeNavigationProjectIO io = FindFirstObjectByType<RuntimeNavigationProjectIO>();
        if (build == null) throw new CommandException("RuntimeBuildLoadAuthority is not running.");

        // The same teardown the "start afresh" choice of ModelImportRouter performs: loading a
        // new head under an existing groom would leave the old cards floating in the air.
        if (AllCards().Length > 0 && !a.Bool("discard_groom", false))
            throw new CommandException("There is a groom in the scene. Save it first, or pass discard_groom=true to start afresh.");
        UndoHistoryAuthority.NotifySessionReplaced();
        if (io != null) io.CleanupEditorUIAndCards();

        WelcomeWhatsNewAuthority.DismissIfOpen();
        build.LoadModelAtPath(path, false);
        yield return null;
        yield return null;
        GameObject model = LoadedModel;
        if (model == null) throw new CommandException("HairBrush could not import that OBJ.");

        string albedo = a.Str("albedo_path", null);
        bool albedoApplied = false;
        if (!string.IsNullOrEmpty(albedo))
            albedoApplied = ImportedHeadAppearance.TryApplyAlbedo(model, CheckPath(albedo, null, true));

        ok(new Dictionary<string, object> { { "loaded", path }, { "albedo_applied", albedoApplied }, { "head", HeadInfo() } });
    }

    IEnumerator LoadProject(Args a, Action<object> ok)
    {
        string path = CheckPath(a.Str("path", null), ".json", true);
        RuntimeNavigationProjectIO io = FindFirstObjectByType<RuntimeNavigationProjectIO>();
        if (io == null) throw new CommandException("RuntimeNavigationProjectIO is not running.");
        if (AllCards().Length > 0 && !a.Bool("discard_groom", false))
            throw new CommandException("There is a groom in the scene. Save it first, or pass discard_groom=true to replace it.");

        WelcomeWhatsNewAuthority.DismissIfOpen();
        io.LoadProjectFromPath(path);

        // A load settles over several frames: each persistence bridge takes its copy and the
        // canonical restore re-asserts the saved values. Same wait SelectLoadedGroupWhenSettled uses.
        CanonicalProjectStateBridge bridge = FindFirstObjectByType<CanonicalProjectStateBridge>();
        int frames = 0;
        do { yield return null; frames++; }
        while (frames < 600 && (CanonicalProjectStateBridge.ProjectRestorePending() || (bridge != null && bridge.HasPendingRestore)));
        // The restore is done, but card meshes are regenerated over the next few LateUpdates;
        // a screenshot taken straight after showed a bald head. Let them land first.
        for (int i = 0; i < 6; i++) yield return null;

        if (LoadedModel == null)
            throw new CommandException("The project's head model could not be found, and HairBrush is showing its missing-model prompt. Ask the user to resolve it in the app.");
        ok(new Dictionary<string, object> { { "loaded", path }, { "status", Status() } });
    }

    object SaveProject(Args a)
    {
        string path = CheckPath(a.Str("path", null), ".json", false);
        if (File.Exists(path) && !a.Bool("overwrite", false)) throw new CommandException("File exists. Pass overwrite=true to replace it.");
        RuntimeNavigationProjectIO io = FindFirstObjectByType<RuntimeNavigationProjectIO>();
        if (io == null) throw new CommandException("RuntimeNavigationProjectIO is not running.");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, JsonUtility.ToJson(io.BuildSaveData(), true));
        StatusToast.Show("Claude saved the project to " + Path.GetFileName(path));
        return new Dictionary<string, object> { { "saved", path }, { "cards", AllCards().Length } };
    }

    object ExportObj(Args a)
    {
        string path = CheckPath(a.Str("path", null), ".obj", false);
        if (File.Exists(path) && !a.Bool("overwrite", false)) throw new CommandException("File exists. Pass overwrite=true to replace it.");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string error = HairObjExporter.ExportToPath(path, out int cards);
        if (error != null) throw new CommandException(error);
        return new Dictionary<string, object> { { "exported", path }, { "cards", cards } };
    }

    IEnumerator UndoRedo(bool undo, Action<object> ok)
    {
        UndoHistoryAuthority history = FindFirstObjectByType<UndoHistoryAuthority>();
        if (history == null) throw new CommandException("Undo history is not running.");
        if (undo) history.Undo(); else history.Redo();
        int frames = 0;
        do { yield return null; frames++; } while (UndoHistoryAuthority.Restoring && frames < 600);
        yield return null;
        ok(new Dictionary<string, object> { { undo ? "undone" : "redone", true }, { "total_cards", AllCards().Length } });
    }

    // ---------------------------------------------------------------------------------
    // Camera and screenshots
    // ---------------------------------------------------------------------------------

    static readonly Dictionary<string, (float az, float el)> ViewPresets = new Dictionary<string, (float, float)>
    {
        { "front", (0f, 5f) }, { "back", (180f, 5f) }, { "right", (90f, 5f) }, { "left", (-90f, 5f) },
        { "top", (0f, 85f) }, { "three_quarter_right", (40f, 15f) }, { "three_quarter_left", (-40f, 15f) },
        { "back_three_quarter_right", (140f, 15f) }, { "back_three_quarter_left", (-140f, 15f) }
    };

    // Head plus every visible card, so a long groom is not cropped at the chin.
    Bounds GroomBounds(HeadFrame f)
    {
        Bounds b = f.bounds;
        foreach (HairCard c in AllCards())
        {
            if (c == null) continue;
            Renderer r = c.GetComponent<Renderer>();
            if (r != null && r.enabled) b.Encapsulate(r.bounds);
        }
        return b;
    }

    void ViewPose(Args a, HeadFrame f, float aspect, Camera cam, out Vector3 position, out Quaternion rotation, out Vector3 target, out float distance)
    {
        float az, el;
        string view = a.Str("view", "three_quarter_right");
        if (a.Has("az") || a.Has("el")) { az = a.Float("az", 0f); el = a.Float("el", 0f); }
        else if (!ViewPresets.TryGetValue(view, out var preset)) throw new CommandException("Unknown view '" + view + "'. Valid: " + string.Join(", ", ViewPresets.Keys) + ", or give az/el.");
        else { az = preset.az; el = preset.el; }

        Bounds frameBounds = a.Str("fit", "groom") == "head" ? f.bounds : GroomBounds(f);
        target = a.Has("target") ? a.Vec("target") : frameBounds.center;
        float vfov = cam.fieldOfView * Mathf.Deg2Rad;
        float hfov = 2f * Mathf.Atan(Mathf.Tan(vfov * .5f) * aspect);
        float fitRadius = frameBounds.extents.magnitude;
        distance = fitRadius / Mathf.Sin(Mathf.Min(vfov, hfov) * .5f) / Mathf.Max(.1f, a.Float("zoom", 1f));
        Vector3 dir = Direction(f, az, el);
        position = target + dir * distance;
        rotation = Quaternion.LookRotation(-dir, Mathf.Abs(el) > 80f ? f.front * -Mathf.Sign(el) : f.up);
    }

    // Moves the user's own camera. The orbit code rebuilds the pivot's rotation from its private
    // pitch field on the next drag, so that has to be written too or the view snaps back.
    object SetView(Args a)
    {
        HeadFrame f = Frame();
        Camera cam = viewer.mainCamera;
        if (cam == null || viewer.cameraPivot == null) throw new CommandException("No camera rig.");
        ViewPose(a, f, cam.aspect, cam, out _, out Quaternion rot, out Vector3 target, out float distance);
        Vector3 e = rot.eulerAngles;
        float pitch = Mathf.Clamp(Mathf.DeltaAngle(0f, e.x), -89f, 89f);
        viewer.cameraPivot.position = target;
        viewer.cameraPivot.rotation = Quaternion.Euler(pitch, e.y, 0f);
        cam.transform.localPosition = new Vector3(0f, 0f, -distance);
        cam.transform.localRotation = Quaternion.identity;
        SetField("pitch", pitch);
        return new Dictionary<string, object> { { "pivot", target }, { "distance", distance } };
    }

    // ---------------------------------------------------------------------------------
    // Argument access
    // ---------------------------------------------------------------------------------

    public class Args
    {
        public readonly Dictionary<string, object> Dict;
        public Args(Dictionary<string, object> d) { Dict = d ?? new Dictionary<string, object>(); }
        public ICollection<string> Keys => Dict.Keys;
        public bool Has(string k) => Dict.TryGetValue(k, out object v) && v != null;
        public object Raw(string k) => Dict.TryGetValue(k, out object v) ? v : null;

        public float Float(string k, float d) => Has(k) ? ToFloat(Dict[k], k) : d;
        public int Int(string k, int d) => Has(k) ? Mathf.RoundToInt(ToFloat(Dict[k], k)) : d;
        public string Str(string k, string d) => Has(k) ? Convert.ToString(Dict[k]) : d;
        public bool Bool(string k, bool d)
        {
            if (!Has(k)) return d;
            if (Dict[k] is bool b) return b;
            throw new CommandException(k + " must be true or false.");
        }
        public Vector3 Vec(string k) => ToVec(Dict[k]);
        public List<object> List(string k) => Has(k) ? Dict[k] as List<object> ?? throw new CommandException(k + " must be an array.") : null;
        public Args Obj(string k)
        {
            if (!Has(k)) return null;
            if (Dict[k] is Dictionary<string, object> d) return new Args(d);
            throw new CommandException(k + " must be an object.");
        }
        public IEnumerable<Dictionary<string, object>> Objects(string k)
        {
            List<object> list = List(k);
            if (list == null) throw new CommandException(k + " is required.");
            foreach (object o in list)
            {
                if (o is Dictionary<string, object> d) yield return d;
                else throw new CommandException("Every entry of " + k + " must be an object.");
            }
        }

        public static float ToFloat(object v) => ToFloat(v, "value");
        static float ToFloat(object v, string k)
        {
            if (v is double d && !double.IsNaN(d) && !double.IsInfinity(d)) return (float)d;
            throw new CommandException(k + " must be a number.");
        }
        // [a, b] pairs, returned in x/y.
        public static Vector3 ToVec2(object v)
        {
            if (v is List<object> l && l.Count == 2) return new Vector3(ToFloat(l[0]), ToFloat(l[1]), 0f);
            throw new CommandException("Expected an [az, el] pair.");
        }
        public static Vector3 ToVec(object v)
        {
            if (v is List<object> l && l.Count == 3) return new Vector3(ToFloat(l[0]), ToFloat(l[1]), ToFloat(l[2]));
            throw new CommandException("Expected an [x, y, z] vector.");
        }
    }
}
