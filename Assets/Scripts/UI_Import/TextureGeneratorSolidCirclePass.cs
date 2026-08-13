using UnityEngine;
using UnityEngine.UI;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

[DefaultExecutionOrder(9800)]
public class TextureGeneratorSolidCirclePass : MonoBehaviour
{
    TextureEditorManager manager;
    FieldInfo clustersField, activeIndexField, textureField;
    GameObject boundPanel;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<TextureGeneratorSolidCirclePass>() != null) return;
        var go = new GameObject("TextureGeneratorSolidCirclePass");
        DontDestroyOnLoad(go);
        go.AddComponent<TextureGeneratorSolidCirclePass>();
    }

    void Update()
    {
        if (manager == null)
        {
            manager = FindFirstObjectByType<TextureEditorManager>();
            if (manager != null)
            {
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var t = typeof(TextureEditorManager);
                clustersField = t.GetField("clusters", flags);
                activeIndexField = t.GetField("activeClusterIndex", flags);
                textureField = t.GetField("generatedHairTexture", flags);
            }
        }
        if (manager == null) return;

        var panel = FindNamed("TextureGeneratorControlsPanel");
        if (panel == null || panel == boundPanel) return;
        boundPanel = panel;

        var buttonT = FindRecursive(panel.transform, "GENERATE / UPDATEButton");
        var button = buttonT != null ? buttonT.GetComponent<Button>() : null;
        if (button != null) button.onClick.AddListener(() => StartCoroutine(ApplyAfterGenerate()));
    }

    IEnumerator ApplyAfterGenerate()
    {
        yield return new WaitForEndOfFrame();

        var cluster = GetActiveCluster();
        var tex = textureField != null ? textureField.GetValue(manager) as Texture2D : null;
        if (cluster == null || tex == null || !cluster.generated) yield break;

        var src = tex.GetPixels32();
        var dst = (Color32[])src.Clone();
        var r = cluster.pixelRect;
        var black = new Color32(0,0,0,255);

        for (int y = r.yMin; y < r.yMax; y++)
            for (int x = r.xMin; x < r.xMax; x++)
                if (x >= 0 && x < tex.width && y >= 0 && y < tex.height)
                    dst[y * tex.width + x] = black;

        int radius = Mathf.Clamp(Mathf.RoundToInt(cluster.thicknessAmount), 1, 10);

        for (int y = Mathf.Max(0, r.yMin); y < Mathf.Min(tex.height, r.yMax); y++)
        {
            for (int x = Mathf.Max(0, r.xMin); x < Mathf.Min(tex.width, r.xMax); x++)
            {
                var p = src[y * tex.width + x];
                if (p.r < 24) continue;
                StampCircle(dst, tex.width, tex.height, x, y, radius, r);
            }
        }

        tex.SetPixels32(dst);
        tex.filterMode = FilterMode.Point;
        tex.Apply(true, false);
    }

    static void StampCircle(Color32[] pixels, int width, int height, int cx, int cy, int radius, RectInt clip)
    {
        int rr = radius * radius;
        var white = new Color32(255,255,255,255);
        int minY = Mathf.Max(clip.yMin, cy - radius, 0);
        int maxY = Mathf.Min(clip.yMax - 1, cy + radius, height - 1);
        int minX = Mathf.Max(clip.xMin, cx - radius, 0);
        int maxX = Mathf.Min(clip.xMax - 1, cx + radius, width - 1);

        for (int y = minY; y <= maxY; y++)
        {
            int dy = y - cy;
            for (int x = minX; x <= maxX; x++)
            {
                int dx = x - cx;
                if (dx * dx + dy * dy <= rr)
                    pixels[y * width + x] = white;
            }
        }
    }

    TextureEditorManager.HairTextureCluster GetActiveCluster()
    {
        if (clustersField == null || activeIndexField == null) return null;
        var list = clustersField.GetValue(manager) as List<TextureEditorManager.HairTextureCluster>;
        if (list == null) return null;
        int i = (int)activeIndexField.GetValue(manager);
        return i >= 0 && i < list.Count ? list[i] : null;
    }

    static Transform FindRecursive(Transform root, string name)
    {
        if (root == null) return null;
        if (root.name == name) return root;
        foreach (Transform child in root)
        {
            var found = FindRecursive(child, name);
            if (found != null) return found;
        }
        return null;
    }

    static GameObject FindNamed(string name)
    {
        foreach (Transform t in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (t != null && t.name == name) return t.gameObject;
        return null;
    }
}
