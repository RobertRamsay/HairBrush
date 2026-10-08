using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// MCP STUDIO - everything the agent uses to LOOK at the groom rather than change it.
//
// The plain screenshot only ever showed the hair. Judging a groom needs more than that:
//   - OVERLAY draws the modifiers the hair is responding to - guide curves, POST rings, clumper
//     rings - with their ids written beside them, so "what is pulling that patch" is answered by
//     looking rather than by working backwards from the result.
//   - STUDIO lighting replaces the scene's single grazing light with a key/fill/rim set for the
//     one render, so form, volume and strand texture actually read.
//   - SCALP CHECK paints the head flat magenta for the render, so every gap in coverage shows.
//   - TURNAROUND renders several views into one contact sheet, so a change is judged from all
//     sides in one look instead of four.
//   - UV ATLAS PREVIEW shows the hair texture itself with every UV rectangle outlined and
//     numbered, plus per-strip coverage numbers, so strips are chosen on purpose.
// None of these move the user's camera or leave anything behind in the scene.
public partial class HairBrushMcpCommands
{
    // ---------------------------------------------------------------------------------
    // Render options
    // ---------------------------------------------------------------------------------

    class RenderOptions
    {
        public bool overlay;
        public bool overlayCards;
        public int overlayGroup = -1;     // -1 = every group
        public string lighting = "scene"; // scene | studio
        public Color? headColour;          // scalp check
    }

    RenderOptions ReadRenderOptions(Args a)
    {
        RenderOptions o = new RenderOptions
        {
            overlay = a.Bool("overlay", false),
            overlayCards = a.Bool("overlay_cards", false),
            overlayGroup = a.Int("overlay_group", -1),
            lighting = a.Str("lighting", "scene")
        };
        if (o.lighting != "scene" && o.lighting != "studio") throw new CommandException("lighting must be scene or studio.");
        if (a.Bool("scalp_check", false)) o.headColour = new Color(1f, 0f, 1f, 1f);
        return o;
    }

    // One render of the main camera from `pose` into a fresh texture, with every temporary
    // change (pose, aspect, target, lights, head material) undone before returning.
    Texture2D RenderView(Camera cam, Vector3? position, Quaternion? rotation, int width, int height, RenderOptions o, List<object> legend)
    {
        Transform t = cam.transform;
        Vector3 savedPos = t.position;
        Quaternion savedRot = t.rotation;
        RenderTexture savedTarget = cam.targetTexture;
        RenderTexture rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
        rt.antiAliasing = 4;

        List<Light> disabledLights = new List<Light>();
        List<GameObject> tempLights = new List<GameObject>();
        Color savedAmbient = RenderSettings.ambientLight;
        UnityEngine.Rendering.AmbientMode savedAmbientMode = RenderSettings.ambientMode;
        Dictionary<Renderer, Material[]> savedHead = new Dictionary<Renderer, Material[]>();
        Material flat = null;

        try
        {
            if (position.HasValue) t.SetPositionAndRotation(position.Value, rotation.Value);

            if (o.lighting == "studio" && LoadedModel != null)
                SetUpStudioLights(Frame(), t, disabledLights, tempLights);

            if (o.headColour.HasValue && LoadedModel != null)
            {
                Shader unlit = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
                flat = new Material(unlit);
                if (flat.HasProperty("_BaseColor")) flat.SetColor("_BaseColor", o.headColour.Value);
                if (flat.HasProperty("_Color")) flat.SetColor("_Color", o.headColour.Value);
                foreach (Renderer r in LoadedModel.GetComponentsInChildren<Renderer>())
                {
                    savedHead[r] = r.sharedMaterials;
                    r.sharedMaterials = Enumerable.Repeat(flat, r.sharedMaterials.Length).ToArray();
                }
            }

            cam.targetTexture = rt;
            cam.aspect = width / (float)height;
            cam.Render();

            if (o.overlay || o.overlayCards) DrawOverlay(cam, rt, o, legend);

            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            Texture2D shot = new Texture2D(width, height, TextureFormat.RGB24, false);
            shot.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            shot.Apply();
            RenderTexture.active = prev;
            return shot;
        }
        finally
        {
            cam.targetTexture = savedTarget;
            cam.ResetAspect();
            t.SetPositionAndRotation(savedPos, savedRot);
            RenderTexture.ReleaseTemporary(rt);
            foreach (Light l in disabledLights) if (l != null) l.enabled = true;
            foreach (GameObject g in tempLights) if (g != null) DestroyImmediate(g);
            RenderSettings.ambientLight = savedAmbient;
            RenderSettings.ambientMode = savedAmbientMode;
            foreach (KeyValuePair<Renderer, Material[]> kv in savedHead) if (kv.Key != null) kv.Key.sharedMaterials = kv.Value;
            if (flat != null) DestroyImmediate(flat);
        }
    }

    // Classic three-point set, placed relative to the camera so it reads the same from every
    // angle: a warm key high and to one side, a cool soft fill opposite, a strong rim behind.
    static void SetUpStudioLights(HeadFrame f, Transform cam, List<Light> disabled, List<GameObject> temps)
    {
        foreach (Light l in FindObjectsByType<Light>(FindObjectsSortMode.None))
            if (l.enabled) { l.enabled = false; disabled.Add(l); }

        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(.16f, .16f, .18f);

        Vector3 toCam = (cam.position - f.center).normalized;
        Vector3 side = Vector3.Cross(Vector3.up, toCam).normalized;
        void Add(string name, Vector3 from, Color colour, float intensity, bool shadows)
        {
            GameObject go = new GameObject("McpStudio_" + name);
            go.hideFlags = HideFlags.HideAndDontSave;
            Light l = go.AddComponent<Light>();
            l.type = LightType.Directional;
            l.color = colour;
            l.intensity = intensity;
            l.shadows = shadows ? LightShadows.Soft : LightShadows.None;
            go.transform.rotation = Quaternion.LookRotation(-from.normalized);
            temps.Add(go);
        }
        Add("Key", toCam + side * .9f + Vector3.up * .9f, new Color(1f, .95f, .88f), 1.6f, true);
        Add("Fill", toCam - side * 1.1f + Vector3.up * .2f, new Color(.75f, .82f, 1f), .45f, false);
        Add("Rim", -toCam + Vector3.up * .8f, new Color(1f, .97f, .92f), 1.3f, false);
    }

    // ---------------------------------------------------------------------------------
    // Overlay
    // ---------------------------------------------------------------------------------

    static Material overlayMaterial;

    static Material OverlayMaterial()
    {
        if (overlayMaterial != null) return overlayMaterial;
        overlayMaterial = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
        overlayMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        overlayMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        overlayMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
        overlayMaterial.SetInt("_ZWrite", 0);
        overlayMaterial.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
        return overlayMaterial;
    }

    static readonly Color GuideColour = new Color(1f, .3f, 1f, 1f);
    static readonly Color PostColour = new Color(.2f, 1f, 1f, 1f);
    static readonly Color ClumperColour = new Color(1f, .9f, .2f, 1f);
    static readonly Color CardColour = new Color(.4f, 1f, .4f, .9f);

    // Drawn straight into the render target after the camera, always on top, in screen-space
    // pixels for the labels. The legend lists every item with its pixel position (origin top
    // left) so ids can be matched to what is in the picture.
    void DrawOverlay(Camera cam, RenderTexture rt, RenderOptions o, List<object> legend)
    {
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rt;
        GL.PushMatrix();
        OverlayMaterial().SetPass(0);
        // GL already handles the render-texture flip for the active target; asking for it again
        // here drew the 3D overlay upside down while the pixel-space labels were correct.
        GL.LoadProjectionMatrix(GL.GetGPUProjectionMatrix(cam.projectionMatrix, false));
        GL.modelview = cam.worldToCameraMatrix;
        GL.Begin(GL.LINES);

        List<(Vector3 world, string text, Color colour)> labels = new List<(Vector3, string, Color)>();
        bool wanted(int gid) => o.overlayGroup < 0 || o.overlayGroup == gid;

        if (o.overlay)
        {
            GuideCurveManager gm = FindFirstObjectByType<GuideCurveManager>();
            if (gm != null)
                foreach (GuideCurveManager.GuideCurve g in gm.GetAllGuides())
                {
                    if (!wanted(g.groupId)) continue;
                    Vector3[] pts = GuideCurveManager.WorldPoints(g);
                    GL.Color(GuideColour);
                    for (int i = 1; i < pts.Length; i++) { GL.Vertex(pts[i - 1]); GL.Vertex(pts[i]); }
                    for (int i = 0; i < pts.Length; i++) Cross(pts[i], .002f);
                    GL.Color(new Color(GuideColour.r, GuideColour.g, GuideColour.b, .45f));
                    Ring(g.contact, g.normal, g.radius);
                    labels.Add((g.contact, "G" + g.id, GuideColour));
                }

            PostAffectorManager pm = PostAffectorManager.Instance;
            if (pm != null)
                foreach (int gid in GroupIds)
                {
                    if (!wanted(gid)) continue;
                    foreach (PostAffectorSaveData p in pm.ExportGroup(gid))
                    {
                        Vector3 c = new Vector3(p.centerX, p.centerY, p.centerZ);
                        Vector3 n = new Vector3(p.normalX, p.normalY, p.normalZ);
                        GL.Color(PostColour);
                        Ring(c, n, p.radius);
                        GL.Color(new Color(PostColour.r, PostColour.g, PostColour.b, .35f));
                        Ring(c, n, p.radius + p.falloff);
                        labels.Add((c, "P" + p.id, PostColour));
                    }
                }

            GroupClumperManager cm = FindFirstObjectByType<GroupClumperManager>();
            if (cm != null)
                foreach (GroupClumperManager.GroupClumper c in cm.GetAllClumpers())
                {
                    if (!wanted(c.groupId)) continue;
                    GL.Color(ClumperColour);
                    Ring(c.center, c.normal, c.radius);
                    GL.Color(new Color(ClumperColour.r, ClumperColour.g, ClumperColour.b, .35f));
                    Ring(c.center, c.normal, c.radius + c.falloff);
                    labels.Add((c.center, "C" + c.id, ClumperColour));
                }
        }

        if (o.overlayCards)
        {
            GL.Color(CardColour);
            foreach (HairCard card in AllCards())
                if (card != null && wanted(card.groupId)) Cross(card.GetSpawnHitPoint(), .0012f);
        }
        GL.End();

        // Labels: projected once, then drawn as seven-segment digits in pixel space.
        GL.LoadPixelMatrix(0, rt.width, 0, rt.height);
        GL.Begin(GL.LINES);
        foreach (var (world, text, colour) in labels)
        {
            Vector3 s = cam.WorldToScreenPoint(world);
            if (s.z <= 0f) continue;
            // WorldToScreenPoint uses the camera's current pixel rect, which is the target here.
            float x = s.x * rt.width / cam.pixelWidth + 6f, y = s.y * rt.height / cam.pixelHeight + 4f;
            GL.Color(new Color(0f, 0f, 0f, .85f));
            DrawText(text, x + 1f, y - 1f, 9f);
            GL.Color(colour);
            DrawText(text, x, y, 9f);
            legend?.Add(new Dictionary<string, object>
            {
                { "label", text }, { "x", Mathf.RoundToInt(x) }, { "y", Mathf.RoundToInt(rt.height - y) }
            });
        }
        GL.End();
        GL.PopMatrix();
        RenderTexture.active = prev;
    }

    static void Cross(Vector3 p, float s)
    {
        GL.Vertex(p - Vector3.right * s); GL.Vertex(p + Vector3.right * s);
        GL.Vertex(p - Vector3.up * s); GL.Vertex(p + Vector3.up * s);
        GL.Vertex(p - Vector3.forward * s); GL.Vertex(p + Vector3.forward * s);
    }

    static void Ring(Vector3 centre, Vector3 normal, float radius)
    {
        if (normal.sqrMagnitude < 1e-6f) normal = Vector3.up;
        normal.Normalize();
        Vector3 a = Vector3.Cross(normal, Mathf.Abs(normal.y) < .9f ? Vector3.up : Vector3.right).normalized;
        Vector3 b = Vector3.Cross(normal, a);
        const int steps = 40;
        Vector3 last = centre + a * radius;
        for (int i = 1; i <= steps; i++)
        {
            float th = i * Mathf.PI * 2f / steps;
            Vector3 p = centre + (a * Mathf.Cos(th) + b * Mathf.Sin(th)) * radius;
            GL.Vertex(last); GL.Vertex(p);
            last = p;
        }
    }

    // Seven-segment glyphs: digits plus the three prefix letters the overlay uses. Each glyph is a
    // list of segments a..g; coordinates are in a 0..1 x 0..2 cell.
    static readonly Dictionary<char, string> Glyphs = new Dictionary<char, string>
    {
        { '0', "abcdef" }, { '1', "bc" }, { '2', "abged" }, { '3', "abgcd" }, { '4', "fgbc" },
        { '5', "afgcd" }, { '6', "afgedc" }, { '7', "abc" }, { '8', "abcdefg" }, { '9', "abcdfg" },
        { 'G', "afedc" }, { 'P', "abgfe" }, { 'C', "afed" }, { '-', "g" }
    };

    static readonly Dictionary<char, (float x0, float y0, float x1, float y1)> Segments = new Dictionary<char, (float, float, float, float)>
    {
        { 'a', (0, 2, 1, 2) }, { 'b', (1, 2, 1, 1) }, { 'c', (1, 1, 1, 0) }, { 'd', (0, 0, 1, 0) },
        { 'e', (0, 0, 0, 1) }, { 'f', (0, 1, 0, 2) }, { 'g', (0, 1, 1, 1) }
    };

    // GL immediate-mode text: one line per segment, in whatever matrix is loaded.
    static void DrawText(string text, float x, float y, float size)
    {
        foreach ((float x0, float y0, float x1, float y1) in TextSegments(text, x, y, size))
        {
            GL.Vertex3(x0, y0, 0f); GL.Vertex3(x1, y1, 0f);
        }
    }

    static IEnumerable<(float, float, float, float)> TextSegments(string text, float x, float y, float size)
    {
        float w = size * .5f, h = size * .5f;
        foreach (char ch in text.ToUpperInvariant())
        {
            if (Glyphs.TryGetValue(ch, out string segs))
                foreach (char s in segs)
                {
                    var (x0, y0, x1, y1) = Segments[s];
                    yield return (x + x0 * w, y + y0 * h, x + x1 * w, y + y1 * h);
                }
            x += w + size * .35f;
        }
    }

    // ---------------------------------------------------------------------------------
    // Screenshot and turnaround
    // ---------------------------------------------------------------------------------

    IEnumerator Screenshot(Args a, Action<object> ok)
    {
        int width = Mathf.Clamp(a.Int("width", 768), 64, 2048);
        int height = Mathf.Clamp(a.Int("height", 768), 64, 2048);
        Camera cam = viewer.mainCamera;
        if (cam == null) throw new CommandException("No main camera.");
        RenderOptions o = ReadRenderOptions(a);

        // Mesh deformation (guides, clumpers, POSTs) is applied in LateUpdate, so render after it.
        yield return new WaitForEndOfFrame();

        Texture2D shot;
        List<object> legend = new List<object>();
        if (a.Bool("include_ui", false))
        {
            // The user's actual window, panels and all - for "look at this slider" conversations.
            shot = ScreenCapture.CaptureScreenshotAsTexture();
        }
        else
        {
            Vector3? pos = null; Quaternion? rot = null;
            bool currentView = a.Str("view", null) == "current" && !a.Has("az") && !a.Has("el");
            if (!currentView && LoadedModel != null)
            {
                ViewPose(a, Frame(), width / (float)height, cam, out Vector3 p, out Quaternion r, out _, out _);
                pos = p; rot = r;
            }
            shot = RenderView(cam, pos, rot, width, height, o, legend);
        }

        Dictionary<string, object> result = EncodeImage(shot);
        if (legend.Count > 0) result["overlay_legend"] = legend;
        if (o.headColour.HasValue) result["note"] = "Scalp check: the head is flat magenta, so every magenta patch inside the hairline is a coverage gap.";
        ok(result);
    }

    static Dictionary<string, object> EncodeImage(Texture2D tex)
    {
        byte[] png = tex.EncodeToPNG();
        Dictionary<string, object> d = new Dictionary<string, object>
        {
            { "png_base64", Convert.ToBase64String(png) }, { "width", tex.width }, { "height", tex.height }
        };
        Destroy(tex);
        return d;
    }

    static readonly string[] DefaultTurnaround = { "front", "three_quarter_right", "right", "back_three_quarter_right", "back", "top" };

    IEnumerator Turnaround(Args a, Action<object> ok)
    {
        Camera cam = viewer.mainCamera;
        if (cam == null) throw new CommandException("No main camera.");
        HeadFrame f = Frame();
        int tile = Mathf.Clamp(a.Int("tile", 384), 128, 768);
        RenderOptions o = ReadRenderOptions(a);

        List<Args> views = new List<Args>();
        if (a.Has("views"))
        {
            foreach (object v in a.List("views"))
            {
                if (v is string s) views.Add(new Args(new Dictionary<string, object> { { "view", s } }));
                else if (v is Dictionary<string, object> d) views.Add(new Args(d));
                else throw new CommandException("views entries must be preset names or {az, el} objects.");
            }
        }
        else views.AddRange(DefaultTurnaround.Select(s => new Args(new Dictionary<string, object> { { "view", s } })));
        if (views.Count == 0 || views.Count > 12) throw new CommandException("Give 1 to 12 views.");

        // Shared framing for every tile, so the views compare like for like.
        foreach (Args v in views)
        {
            foreach (string k in new[] { "zoom", "fit", "target" })
                if (a.Has(k) && !v.Has(k)) v.Dict[k] = a.Raw(k);
        }

        yield return new WaitForEndOfFrame();

        int cols = Mathf.Min(views.Count, a.Int("columns", 3));
        int rows = Mathf.CeilToInt(views.Count / (float)cols);
        Texture2D sheet = new Texture2D(cols * tile, rows * tile, TextureFormat.RGB24, false);
        Color32[] fill = Enumerable.Repeat(new Color32(12, 12, 16, 255), sheet.width * sheet.height).ToArray();
        sheet.SetPixels32(fill);

        List<object> tiles = new List<object>();
        for (int i = 0; i < views.Count; i++)
        {
            ViewPose(views[i], f, 1f, cam, out Vector3 pos, out Quaternion rot, out _, out _);
            Texture2D t = RenderView(cam, pos, rot, tile, tile, o, null);
            int cx = (i % cols) * tile, cy = (rows - 1 - i / cols) * tile;
            sheet.SetPixels(cx, cy, tile, tile, t.GetPixels());
            Destroy(t);
            tiles.Add(new Dictionary<string, object> { { "index", i }, { "view", views[i].Str("view", null) ?? ("az " + views[i].Float("az", 0f) + " el " + views[i].Float("el", 0f)) }, { "column", i % cols }, { "row", i / cols } });
        }
        sheet.Apply();
        Dictionary<string, object> result = EncodeImage(sheet);
        result["tiles"] = tiles;
        ok(result);
    }

    // ---------------------------------------------------------------------------------
    // UV atlas preview
    // ---------------------------------------------------------------------------------

    Material GlobalHairMaterial()
    {
        MaterialEditorManager mem = FindFirstObjectByType<MaterialEditorManager>();
        if (mem == null) return viewer.hairCardMaterial;
        Type t = typeof(MaterialEditorManager);
        System.Collections.IList entries = t.GetField("materials", Private)?.GetValue(mem) as System.Collections.IList;
        System.Reflection.MethodInfo globalIndex = t.GetMethod("GetGlobalMaterialIndex", Private);
        if (entries == null || globalIndex == null) return viewer.hairCardMaterial;
        int index = (int)globalIndex.Invoke(mem, null);
        if (index < 0 || index >= entries.Count) return viewer.hairCardMaterial;
        object entry = entries[index];
        return entry.GetType().GetField("material")?.GetValue(entry) as Material ?? viewer.hairCardMaterial;
    }

    // GPU copy, so it works whatever the texture's import settings (read/write off, compressed).
    static Color[] ReadTexture(Texture tex, int w, int h, bool linear)
    {
        RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
        Graphics.Blit(tex, rt);
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rt;
        Texture2D t = new Texture2D(w, h, TextureFormat.RGBA32, false, linear);
        t.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        t.Apply();
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);
        Color[] px = t.GetPixels();
        Destroy(t);
        return px;
    }

    object PreviewUVRects(Args a)
    {
        Material mat = GlobalHairMaterial();
        if (mat == null) throw new CommandException("There is no hair material.");
        Texture albedo = mat.HasProperty("_Albedo") ? mat.GetTexture("_Albedo") : null;
        if (albedo == null && mat.HasProperty("_BaseMap")) albedo = mat.GetTexture("_BaseMap");
        Texture mask = mat.HasProperty("_OpacityMask") ? mat.GetTexture("_OpacityMask") : null;
        if (albedo == null && mask == null) throw new CommandException("The hair material has no albedo or opacity texture loaded.");

        int size = Mathf.Clamp(a.Int("size", 1024), 256, 2048);
        Color[] col = albedo != null ? ReadTexture(albedo, size, size, false) : null;
        Color[] alpha = mask != null ? ReadTexture(mask, size, size, true) : null;
        float Alpha(int i) => alpha != null ? alpha[i].r : (col != null ? col[i].a : 1f);
        Color32 grey = new Color32(70, 70, 76, 255), dark = new Color32(52, 52, 58, 255);

        // Composite over a checker so the strip silhouettes are obvious.
        Color32[] img = new Color32[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int i = y * size + x;
                Color32 bg = ((x / 32 + y / 32) & 1) == 0 ? grey : dark;
                Color c = col != null ? col[i] : Color.white;
                float al = Alpha(i);
                img[i] = Color32.Lerp(bg, (Color32)new Color(c.r, c.g, c.b, 1f), al);
            }

        List<UVRectSaveData> rects = Workspace().ExportDefinitions().OrderBy(r => r.id).ToList();
        // Rects cut from the same strip share a corner; nudge later labels down so ids never merge.
        List<Vector2> usedLabels = new List<Vector2>();
        List<object> stats = new List<object>();
        foreach (UVRectSaveData r in rects)
        {
            int x0 = Mathf.Clamp(Mathf.RoundToInt(r.uMin * size), 0, size - 1), x1 = Mathf.Clamp(Mathf.RoundToInt(r.uMax * size), 0, size - 1);
            int y0 = Mathf.Clamp(Mathf.RoundToInt(r.vMin * size), 0, size - 1), y1 = Mathf.Clamp(Mathf.RoundToInt(r.vMax * size), 0, size - 1);

            // Coverage profile root -> tip in 10 bins. The root sits at v_max unless flipped.
            float[] bins = new float[10];
            int[] counts = new int[10];
            double lum = 0, lumW = 0;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    int i = y * size + x;
                    float tRoot = (y1 - y) / (float)Mathf.Max(1, y1 - y0);
                    if (r.flipV) tRoot = 1f - tRoot;
                    int b = Mathf.Clamp((int)(tRoot * 10f), 0, 9);
                    float al = Alpha(i);
                    bins[b] += al; counts[b]++;
                    if (col != null) { lum += al * (col[i].r * .3f + col[i].g * .59f + col[i].b * .11f); lumW += al; }
                }
            List<object> profile = new List<object>();
            float total = 0f; int totalN = 0;
            for (int b = 0; b < 10; b++) { profile.Add(Math.Round(counts[b] > 0 ? bins[b] / counts[b] : 0f, 2)); total += bins[b]; totalN += counts[b]; }

            // Coverage bands across the width (strand count proxy): runs of covered columns at mid-height.
            int midY = (y0 + y1) / 2, runs = 0; bool inRun = false;
            for (int x = x0; x <= x1; x++)
            {
                bool covered = Alpha(midY * size + x) > .35f;
                if (covered && !inRun) runs++;
                inRun = covered;
            }

            stats.Add(new Dictionary<string, object>
            {
                { "id", r.id }, { "u_min", r.uMin }, { "v_min", r.vMin }, { "u_max", r.uMax }, { "v_max", r.vMax }, { "flip_v", r.flipV },
                { "aspect_h_over_w", Math.Round((r.vMax - r.vMin) / Mathf.Max(1e-4f, r.uMax - r.uMin), 2) },
                { "coverage", Math.Round(totalN > 0 ? total / totalN : 0f, 3) },
                { "coverage_root_to_tip", profile },
                { "separate_strands_at_mid", runs },
                { "mean_luminance", lumW > 0 ? Math.Round(lum / lumW, 3) : 0.0 }
            });

            Color32 outline = new Color32(255, 220, 40, 255);
            DrawRectOutline(img, size, x0, y0, x1, y1, outline);
            Color32 rootMark = new Color32(60, 255, 120, 255);
            int ry = r.flipV ? y0 : y1;
            for (int x = x0; x <= x1; x++) { SetPx(img, size, x, ry, rootMark); SetPx(img, size, x, ry + (r.flipV ? 1 : -1), rootMark); }
            Vector2 at = new Vector2(x0 + 4, y1 - 22);
            while (usedLabels.Any(u => Mathf.Abs(u.x - at.x) < 40 && Mathf.Abs(u.y - at.y) < 24)) at.y -= 26;
            usedLabels.Add(at);
            DrawLabel(img, size, r.id.ToString(), at.x, at.y, 18, new Color32(255, 220, 40, 255));
        }

        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.SetPixels32(img);
        tex.Apply();
        Dictionary<string, object> result = EncodeImage(tex);
        result["rects"] = stats;
        result["note"] = "Yellow boxes are the UV rects with their ids; the green edge marks the ROOT end. coverage_root_to_tip is mean alpha in 10 bins from root to tip: a fall-off toward the tip means a tapered, wispy strip; flat means a blunt strip. separate_strands_at_mid counts distinct covered runs across the middle (higher = more separated strands).";
        return result;
    }

    static void SetPx(Color32[] img, int size, int x, int y, Color32 c)
    {
        if (x < 0 || y < 0 || x >= size || y >= size) return;
        img[y * size + x] = c;
    }

    static void DrawRectOutline(Color32[] img, int size, int x0, int y0, int x1, int y1, Color32 c)
    {
        for (int k = 0; k < 2; k++)
        {
            for (int x = x0; x <= x1; x++) { SetPx(img, size, x, y0 + k, c); SetPx(img, size, x, y1 - k, c); }
            for (int y = y0; y <= y1; y++) { SetPx(img, size, x0 + k, y, c); SetPx(img, size, x1 - k, y, c); }
        }
    }

    static void DrawLine(Color32[] img, int size, float x0, float y0, float x1, float y1, Color32 c)
    {
        int steps = Mathf.CeilToInt(Mathf.Max(Mathf.Abs(x1 - x0), Mathf.Abs(y1 - y0))) + 1;
        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps;
            int x = Mathf.RoundToInt(Mathf.Lerp(x0, x1, t)), y = Mathf.RoundToInt(Mathf.Lerp(y0, y1, t));
            SetPx(img, size, x, y, c); SetPx(img, size, x + 1, y, c); SetPx(img, size, x, y + 1, c);
        }
    }

    static void DrawLabel(Color32[] img, int size, string text, float x, float y, float h, Color32 c)
    {
        Color32 shadow = new Color32(0, 0, 0, 255);
        foreach (var (x0, y0, x1, y1) in TextSegments(text, x + 1, y - 1, h)) DrawLine(img, size, x0, y0, x1, y1, shadow);
        foreach (var (x0, y0, x1, y1) in TextSegments(text, x, y, h)) DrawLine(img, size, x0, y0, x1, y1, c);
    }
}
