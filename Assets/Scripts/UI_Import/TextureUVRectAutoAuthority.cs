using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Adds AUTO to the Texture UV Rect workspace. AUTO inspects the currently previewed
// base-colour/albedo texture and recursively splits occupied atlas regions across meaningful
// empty gutters on BOTH axes. This handles cards arranged side-by-side, stacked vertically,
// and mixed layouts (for example one wide card above two narrower cards).
[DefaultExecutionOrder(9250)]
public class TextureUVRectAutoAuthority : MonoBehaviour
{
    private const int PaddingPixels = 6;
    private const int MaxJoinedGapPixels = 4;
    private const int MaxSplitDepth = 16;

    private struct PixelBounds
    {
        public int xMin, xMax, yMin, yMax;
        public int Width => xMax - xMin + 1;
        public int Height => yMax - yMin + 1;
        public bool Valid => xMax >= xMin && yMax >= yMin;
    }

    private struct GapCandidate
    {
        public bool valid;
        public bool verticalSplit;
        public int start;
        public int end;
        public float score;
    }

    private TextureUVRectWorkspace workspace;
    private GameObject autoButton;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<TextureUVRectAutoAuthority>() != null) return;
        GameObject go = new GameObject("TextureUVRectAutoAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<TextureUVRectAutoAuthority>();
    }

    void Update()
    {
        if (workspace == null) workspace = FindFirstObjectByType<TextureUVRectWorkspace>();
        if (workspace == null) return;
        EnsureButton();
    }

    void EnsureButton()
    {
        Transform buttons = FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .FirstOrDefault(t => t != null && t.name == "Buttons" && t.parent != null && t.parent.name == "UVWorkspaceSection");
        if (buttons == null) return;

        Transform existing = buttons.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t != null && t.name == "AUTO");
        if (existing != null)
        {
            autoButton = existing.gameObject;
            return;
        }

        // CLEAR now lives inside its own row (buttons layout is 2-per-row), so AUTO joins
        // that same row rather than parenting directly under the top-level Buttons container.
        Transform clear = buttons.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t != null && t.name == "CLEAR");
        Transform targetRow = clear != null ? clear.parent : buttons;

        autoButton = new GameObject("AUTO", typeof(RectTransform), typeof(Image), typeof(Button));
        autoButton.transform.SetParent(targetRow, false);
        autoButton.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 28f);
        autoButton.GetComponent<Image>().color = new Color(.20f, .25f, .32f, 1f);
        autoButton.GetComponent<Button>().onClick.AddListener(AutoDetectRectangles);

        GameObject textGO = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textGO.transform.SetParent(autoButton.transform, false);
        RectTransform textRect = textGO.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;
        TextMeshProUGUI text = textGO.GetComponent<TextMeshProUGUI>();
        text.text = "AUTO";
        text.fontSize = 12.5f;
        text.fontStyle = FontStyles.Bold;
        text.alignment = TextAlignmentOptions.Center;
        text.color = Color.white;
        text.raycastTarget = false;

        // Familiar order: DRAWING | UNDO LAST / AUTO | CLEAR.
        if (clear != null)
            autoButton.transform.SetSiblingIndex(clear.GetSiblingIndex());
    }

    void AutoDetectRectangles()
    {
        if (workspace == null) workspace = FindFirstObjectByType<TextureUVRectWorkspace>();
        Texture source = GetPreviewBaseColourTexture();
        if (workspace == null || source == null)
        {
            Debug.LogWarning("UV AUTO: no base-colour texture is available in the Texture workspace.");
            return;
        }

        Texture2D readable = GetReadableTexture(source, out bool ownsReadable);
        if (readable == null)
        {
            Debug.LogWarning("UV AUTO: could not read the current base-colour texture.");
            return;
        }

        List<UVRectSaveData> detected;
        try
        {
            detected = DetectRegions(readable);
        }
        finally
        {
            if (ownsReadable && readable != null) Destroy(readable);
        }

        // Failed detection should never destroy hand-authored work.
        if (detected == null || detected.Count == 0)
        {
            Debug.LogWarning("UV AUTO: no separated hair-card regions were detected; existing UV rectangles were kept.");
            return;
        }

        workspace.ImportDefinitions(detected);
        MaterialUVRectAuthority.StoreSelectedWorkspaceNow();
        Debug.Log("UV AUTO: created " + detected.Count + " rectangle(s) from horizontal/vertical gutters with " + PaddingPixels + "px padding.");
    }

    Texture GetPreviewBaseColourTexture()
    {
        GameObject preview = FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Where(t => t != null && t.name == "HairTexturePreviewPlane")
            .Select(t => t.gameObject)
            .FirstOrDefault();
        if (preview == null) return null;

        MeshRenderer renderer = preview.GetComponent<MeshRenderer>();
        Material material = renderer != null ? renderer.sharedMaterial : null;
        if (material == null) return null;

        Texture texture = null;
        if (material.HasProperty("_Albedo")) texture = material.GetTexture("_Albedo");
        if (texture == null && material.HasProperty("_BaseMap")) texture = material.GetTexture("_BaseMap");
        if (texture == null) texture = material.mainTexture;
        return texture;
    }

    static Texture2D GetReadableTexture(Texture source, out bool ownsTexture)
    {
        ownsTexture = false;
        if (source == null || source.width <= 0 || source.height <= 0) return null;

        if (source is Texture2D texture2D && texture2D.isReadable)
            return texture2D;

        RenderTexture previous = RenderTexture.active;
        RenderTexture temporary = null;
        try
        {
            temporary = RenderTexture.GetTemporary(
                source.width,
                source.height,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Default);
            Graphics.Blit(source, temporary);
            RenderTexture.active = temporary;

            Texture2D copy = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false, false);
            copy.name = source.name + "_UVAutoReadback";
            copy.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
            copy.Apply(false, false);
            ownsTexture = true;
            return copy;
        }
        catch (System.Exception exception)
        {
            Debug.LogWarning("UV AUTO texture readback failed: " + exception.Message);
            return null;
        }
        finally
        {
            RenderTexture.active = previous;
            if (temporary != null) RenderTexture.ReleaseTemporary(temporary);
        }
    }

    static List<UVRectSaveData> DetectRegions(Texture2D texture)
    {
        int width = texture.width;
        int height = texture.height;
        if (width < 2 || height < 2) return new List<UVRectSaveData>();

        Color32[] pixels;
        try { pixels = texture.GetPixels32(); }
        catch { return new List<UVRectSaveData>(); }
        if (pixels == null || pixels.Length != width * height) return new List<UVRectSaveData>();

        Color32 background = EstimateBackground(pixels, width, height);

        byte minAlpha = 255;
        byte maxAlpha = 0;
        for (int i = 0; i < pixels.Length; i++)
        {
            byte a = pixels[i].a;
            if (a < minAlpha) minAlpha = a;
            if (a > maxAlpha) maxAlpha = a;
        }
        bool useAlpha = background.a < 96 && maxAlpha - minAlpha >= 40;

        bool[] foreground = new bool[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
            foreground[i] = IsForeground(pixels[i], background, useAlpha);

        if (!TryFindOccupiedBounds(foreground, width, height, out PixelBounds root))
            return new List<UVRectSaveData>();

        List<PixelBounds> leaves = new List<PixelBounds>();
        SplitRegion(foreground, width, height, root, leaves, 0);

        // Texture UV origin is bottom-left, but IDs are easier to reason about in atlas reading
        // order: top-to-bottom, then left-to-right within a row.
        leaves = leaves
            .Where(bounds => bounds.Valid)
            .OrderByDescending(bounds => bounds.yMax)
            .ThenBy(bounds => bounds.xMin)
            .ToList();

        List<UVRectSaveData> result = new List<UVRectSaveData>();
        int id = 1;
        foreach (PixelBounds leaf in leaves)
        {
            int xMin = Mathf.Max(0, leaf.xMin - PaddingPixels);
            int xMaxExclusive = Mathf.Min(width, leaf.xMax + 1 + PaddingPixels);
            int yMin = Mathf.Max(0, leaf.yMin - PaddingPixels);
            int yMaxExclusive = Mathf.Min(height, leaf.yMax + 1 + PaddingPixels);
            if (xMaxExclusive <= xMin || yMaxExclusive <= yMin) continue;

            result.Add(new UVRectSaveData
            {
                id = id++,
                uMin = (float)xMin / width,
                vMin = (float)yMin / height,
                uMax = (float)xMaxExclusive / width,
                vMax = (float)yMaxExclusive / height
            });
        }
        return result;
    }

    static void SplitRegion(bool[] foreground, int width, int height, PixelBounds region, List<PixelBounds> leaves, int depth)
    {
        if (!TrimToForeground(foreground, width, height, region, out PixelBounds trimmed)) return;
        if (depth >= MaxSplitDepth)
        {
            leaves.Add(trimmed);
            return;
        }

        GapCandidate vertical = FindGap(foreground, width, height, trimmed, true);
        GapCandidate horizontal = FindGap(foreground, width, height, trimmed, false);
        GapCandidate best = !vertical.valid ? horizontal : !horizontal.valid ? vertical :
            (vertical.score >= horizontal.score ? vertical : horizontal);

        if (!best.valid)
        {
            leaves.Add(trimmed);
            return;
        }

        if (best.verticalSplit)
        {
            PixelBounds left = new PixelBounds { xMin = trimmed.xMin, xMax = best.start - 1, yMin = trimmed.yMin, yMax = trimmed.yMax };
            PixelBounds right = new PixelBounds { xMin = best.end + 1, xMax = trimmed.xMax, yMin = trimmed.yMin, yMax = trimmed.yMax };
            SplitRegion(foreground, width, height, left, leaves, depth + 1);
            SplitRegion(foreground, width, height, right, leaves, depth + 1);
        }
        else
        {
            PixelBounds bottom = new PixelBounds { xMin = trimmed.xMin, xMax = trimmed.xMax, yMin = trimmed.yMin, yMax = best.start - 1 };
            PixelBounds top = new PixelBounds { xMin = trimmed.xMin, xMax = trimmed.xMax, yMin = best.end + 1, yMax = trimmed.yMax };
            SplitRegion(foreground, width, height, bottom, leaves, depth + 1);
            SplitRegion(foreground, width, height, top, leaves, depth + 1);
        }
    }

    static GapCandidate FindGap(bool[] foreground, int width, int height, PixelBounds region, bool verticalSplit)
    {
        int axisLength = verticalSplit ? region.Width : region.Height;
        int perpendicular = verticalSplit ? region.Height : region.Width;
        if (axisLength < 7 || perpendicular < 1) return default;

        int[] hits = new int[axisLength];
        int totalForeground = 0;
        if (verticalSplit)
        {
            for (int x = region.xMin; x <= region.xMax; x++)
            {
                int local = x - region.xMin;
                for (int y = region.yMin; y <= region.yMax; y++)
                {
                    if (!foreground[y * width + x]) continue;
                    hits[local]++;
                    totalForeground++;
                }
            }
        }
        else
        {
            for (int y = region.yMin; y <= region.yMax; y++)
            {
                int local = y - region.yMin;
                int row = y * width;
                for (int x = region.xMin; x <= region.xMax; x++)
                {
                    if (!foreground[row + x]) continue;
                    hits[local]++;
                    totalForeground++;
                }
            }
        }
        if (totalForeground <= 0) return default;

        int minimumAxisHits = Mathf.Max(1, perpendicular / 1024);
        bool[] occupied = new bool[axisLength];
        for (int i = 0; i < axisLength; i++) occupied[i] = hits[i] >= minimumAxisHits;
        JoinSmallGaps(occupied, MaxJoinedGapPixels);

        int[] prefix = new int[axisLength + 1];
        for (int i = 0; i < axisLength; i++) prefix[i + 1] = prefix[i] + hits[i];

        int minimumGap = Mathf.Max(MaxJoinedGapPixels + 1, axisLength / 512);
        int minimumChildSpan = 3;
        int minimumChildForeground = Mathf.Max(8, totalForeground / 40); // each side must own >=2.5% of this region

        GapCandidate best = default;
        int cursor = 0;
        while (cursor < axisLength)
        {
            while (cursor < axisLength && occupied[cursor]) cursor++;
            int start = cursor;
            while (cursor < axisLength && !occupied[cursor]) cursor++;
            int end = cursor - 1;
            int gapLength = end - start + 1;
            if (gapLength < minimumGap) continue;

            int leftSpan = start;
            int rightSpan = axisLength - (end + 1);
            if (leftSpan < minimumChildSpan || rightSpan < minimumChildSpan) continue;

            int leftForeground = prefix[start];
            int rightForeground = prefix[axisLength] - prefix[end + 1];
            if (leftForeground < minimumChildForeground || rightForeground < minimumChildForeground) continue;

            float balance = Mathf.Min(leftForeground, rightForeground) / (float)Mathf.Max(1, Mathf.Max(leftForeground, rightForeground));
            float score = gapLength / (float)axisLength + balance * .08f;
            if (!best.valid || score > best.score)
            {
                best.valid = true;
                best.verticalSplit = verticalSplit;
                best.start = (verticalSplit ? region.xMin : region.yMin) + start;
                best.end = (verticalSplit ? region.xMin : region.yMin) + end;
                best.score = score;
            }
        }
        return best;
    }

    static bool TryFindOccupiedBounds(bool[] foreground, int width, int height, out PixelBounds bounds)
    {
        bounds = new PixelBounds { xMin = width, xMax = -1, yMin = height, yMax = -1 };
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                if (!foreground[row + x]) continue;
                if (x < bounds.xMin) bounds.xMin = x;
                if (x > bounds.xMax) bounds.xMax = x;
                if (y < bounds.yMin) bounds.yMin = y;
                if (y > bounds.yMax) bounds.yMax = y;
            }
        }
        return bounds.Valid;
    }

    static bool TrimToForeground(bool[] foreground, int width, int height, PixelBounds input, out PixelBounds trimmed)
    {
        trimmed = new PixelBounds
        {
            xMin = Mathf.Clamp(input.xMin, 0, width - 1),
            xMax = Mathf.Clamp(input.xMax, 0, width - 1),
            yMin = Mathf.Clamp(input.yMin, 0, height - 1),
            yMax = Mathf.Clamp(input.yMax, 0, height - 1)
        };
        if (!trimmed.Valid) return false;

        int minX = width, maxX = -1, minY = height, maxY = -1;
        for (int y = trimmed.yMin; y <= trimmed.yMax; y++)
        {
            int row = y * width;
            for (int x = trimmed.xMin; x <= trimmed.xMax; x++)
            {
                if (!foreground[row + x]) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        if (maxX < minX || maxY < minY) return false;
        trimmed = new PixelBounds { xMin = minX, xMax = maxX, yMin = minY, yMax = maxY };
        return true;
    }

    static Color32 EstimateBackground(Color32[] pixels, int width, int height)
    {
        int patch = Mathf.Clamp(Mathf.Min(width, height) / 64, 4, 32);
        long r = 0, g = 0, b = 0, a = 0, count = 0;

        AccumulateCorner(pixels, width, height, 0, 0, patch, ref r, ref g, ref b, ref a, ref count);
        AccumulateCorner(pixels, width, height, width - patch, 0, patch, ref r, ref g, ref b, ref a, ref count);
        AccumulateCorner(pixels, width, height, 0, height - patch, patch, ref r, ref g, ref b, ref a, ref count);
        AccumulateCorner(pixels, width, height, width - patch, height - patch, patch, ref r, ref g, ref b, ref a, ref count);

        if (count <= 0) return new Color32(0, 0, 0, 255);
        return new Color32((byte)(r / count), (byte)(g / count), (byte)(b / count), (byte)(a / count));
    }

    static void AccumulateCorner(
        Color32[] pixels,
        int width,
        int height,
        int startX,
        int startY,
        int size,
        ref long r,
        ref long g,
        ref long b,
        ref long a,
        ref long count)
    {
        int x0 = Mathf.Clamp(startX, 0, width - 1);
        int y0 = Mathf.Clamp(startY, 0, height - 1);
        int x1 = Mathf.Min(width, x0 + size);
        int y1 = Mathf.Min(height, y0 + size);
        for (int y = y0; y < y1; y++)
        {
            int row = y * width;
            for (int x = x0; x < x1; x++)
            {
                Color32 c = pixels[row + x];
                r += c.r;
                g += c.g;
                b += c.b;
                a += c.a;
                count++;
            }
        }
    }

    static bool IsForeground(Color32 pixel, Color32 background, bool useAlpha)
    {
        if (useAlpha)
        {
            int threshold = Mathf.Clamp(background.a + 8, 8, 224);
            return pixel.a > threshold;
        }

        int dr = pixel.r - background.r;
        int dg = pixel.g - background.g;
        int db = pixel.b - background.b;
        int distanceSq = dr * dr + dg * dg + db * db;
        int maxDelta = Mathf.Max(Mathf.Abs(dr), Mathf.Max(Mathf.Abs(dg), Mathf.Abs(db)));
        return maxDelta >= 7 && distanceSq >= 90;
    }

    static void JoinSmallGaps(bool[] occupied, int maxGap)
    {
        if (occupied == null || occupied.Length == 0 || maxGap <= 0) return;

        int cursor = 0;
        while (cursor < occupied.Length)
        {
            while (cursor < occupied.Length && occupied[cursor]) cursor++;
            int gapStart = cursor;
            while (cursor < occupied.Length && !occupied[cursor]) cursor++;
            int gapEnd = cursor - 1;
            int length = gapEnd - gapStart + 1;

            bool boundedBefore = gapStart > 0 && occupied[gapStart - 1];
            bool boundedAfter = cursor < occupied.Length && occupied[cursor];
            if (boundedBefore && boundedAfter && length > 0 && length <= maxGap)
                for (int i = gapStart; i <= gapEnd; i++) occupied[i] = true;
        }
    }
}
