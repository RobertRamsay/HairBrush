using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Adds AUTO to the Texture UV Rect workspace. AUTO inspects the currently previewed
// base-colour/albedo texture, finds occupied hair-card bands separated by background space,
// and replaces the authored UV rectangles with padded deterministic boxes.
//
// Hair textures are usually made of many disconnected/anti-aliased strands, so this does
// not use raw connected-components (which would identify individual hairs). Instead it
// projects foreground occupancy into texture columns, joins only tiny <=4px gaps, then takes
// the true vertical foreground bounds of each resulting atlas band. That matches the vertical
// card-atlas convention while remaining tolerant of sparse wispy strands.
[DefaultExecutionOrder(9250)]
public class TextureUVRectAutoAuthority : MonoBehaviour
{
    private const int PaddingPixels = 4;
    private const int MaxJoinedGapPixels = 4;

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

        Transform existing = buttons.Find("AUTO");
        if (existing != null)
        {
            autoButton = existing.gameObject;
            return;
        }

        autoButton = new GameObject("AUTO", typeof(RectTransform), typeof(Image), typeof(Button));
        autoButton.transform.SetParent(buttons, false);
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
        text.fontSize = 10.5f;
        text.fontStyle = FontStyles.Bold;
        text.alignment = TextAlignmentOptions.Center;
        text.color = Color.white;
        text.raycastTarget = false;

        // Familiar order: DRAWING | UNDO LAST | AUTO | CLEAR.
        Transform clear = buttons.Find("CLEAR");
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
            detected = DetectBands(readable);
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
        Debug.Log("UV AUTO: created " + detected.Count + " rectangle(s) with " + PaddingPixels + "px padding.");
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

        // HairBrush's current hair material calls this slot _Albedo. Keep common URP/main
        // aliases as fallbacks so AUTO also survives future shader/template changes.
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

    static List<UVRectSaveData> DetectBands(Texture2D texture)
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

        int[] hits = new int[width];
        int[] columnMinY = new int[width];
        int[] columnMaxY = new int[width];
        for (int x = 0; x < width; x++)
        {
            columnMinY[x] = height;
            columnMaxY[x] = -1;
        }

        // One pass gathers both horizontal occupancy and each column's true vertical extent.
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                if (!IsForeground(pixels[row + x], background, useAlpha)) continue;
                hits[x]++;
                if (y < columnMinY[x]) columnMinY[x] = y;
                if (y > columnMaxY[x]) columnMaxY[x] = y;
            }
        }

        // Requiring a handful of foreground pixels prevents isolated compression/noise specks
        // in an otherwise empty column from joining two real cards together.
        int minimumColumnHits = Mathf.Max(2, height / 1024);
        bool[] occupied = new bool[width];
        for (int x = 0; x < width; x++) occupied[x] = hits[x] >= minimumColumnHits;

        JoinSmallHorizontalGaps(occupied, MaxJoinedGapPixels);

        int minimumBandWidth = Mathf.Max(3, width / 2048);
        List<UVRectSaveData> result = new List<UVRectSaveData>();
        int id = 1;
        int cursor = 0;
        while (cursor < width)
        {
            while (cursor < width && !occupied[cursor]) cursor++;
            if (cursor >= width) break;

            int start = cursor;
            while (cursor + 1 < width && occupied[cursor + 1]) cursor++;
            int end = cursor;
            cursor++;

            if (end - start + 1 < minimumBandWidth) continue;

            int minY = height;
            int maxY = -1;
            for (int x = start; x <= end; x++)
            {
                if (columnMaxY[x] < 0) continue;
                if (columnMinY[x] < minY) minY = columnMinY[x];
                if (columnMaxY[x] > maxY) maxY = columnMaxY[x];
            }
            if (maxY < minY) continue;

            int xMin = Mathf.Max(0, start - PaddingPixels);
            int xMaxExclusive = Mathf.Min(width, end + 1 + PaddingPixels);
            int yMin = Mathf.Max(0, minY - PaddingPixels);
            int yMaxExclusive = Mathf.Min(height, maxY + 1 + PaddingPixels);

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
            int threshold = Mathf.Clamp(background.a + 16, 16, 224);
            return pixel.a > threshold;
        }

        int dr = pixel.r - background.r;
        int dg = pixel.g - background.g;
        int db = pixel.b - background.b;
        int distanceSq = dr * dr + dg * dg + db * db;
        int maxDelta = Mathf.Max(Mathf.Abs(dr), Mathf.Max(Mathf.Abs(dg), Mathf.Abs(db)));

        // 14/255 max-channel difference plus a modest RGB distance rejects flat-space JPEG
        // noise while still retaining dark purple/black-ish anti-aliased hair against a dark
        // atlas background.
        return maxDelta >= 14 && distanceSq >= 300;
    }

    static void JoinSmallHorizontalGaps(bool[] occupied, int maxGap)
    {
        if (occupied == null || occupied.Length == 0 || maxGap <= 0) return;

        int x = 0;
        while (x < occupied.Length)
        {
            while (x < occupied.Length && occupied[x]) x++;
            int gapStart = x;
            while (x < occupied.Length && !occupied[x]) x++;
            int gapEnd = x - 1;
            int length = gapEnd - gapStart + 1;

            bool boundedLeft = gapStart > 0 && occupied[gapStart - 1];
            bool boundedRight = x < occupied.Length && occupied[x];
            if (boundedLeft && boundedRight && length > 0 && length <= maxGap)
                for (int fill = gapStart; fill <= gapEnd; fill++) occupied[fill] = true;
        }
    }
}
