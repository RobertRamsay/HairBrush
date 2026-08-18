using System.Collections.Generic;
using System.IO;
using UnityEngine;

// HairBrush owns the viewport appearance of imported heads. Imported OBJ material definitions
// are intentionally ignored: every imported renderer starts with one predictable neutral-grey
// material, and an optional user-selected albedo can then be applied to that material.
public static class ImportedHeadAppearance
{
    private static readonly Color DefaultGrey = new Color(0.55f, 0.55f, 0.55f, 1f);

    public static bool ApplyDefaultMaterial(GameObject root)
    {
        if (root == null) return false;

        Shader shader = FindViewportShader();
        if (shader == null)
        {
            Debug.LogWarning("Could not find a standard shader for the current render pipeline.");
            return false;
        }

        Material material = new Material(shader)
        {
            name = "HairBrush Imported Head"
        };

        SetBaseColor(material, DefaultGrey);
        SetBaseMap(material, null);
        if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0f);
        if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.3f);

        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        foreach (Renderer renderer in renderers)
        {
            int slotCount = Mathf.Max(1, renderer.sharedMaterials != null ? renderer.sharedMaterials.Length : 0);
            Material[] slots = new Material[slotCount];
            for (int i = 0; i < slots.Length; i++) slots[i] = material;
            renderer.sharedMaterials = slots;
        }

        return renderers.Length > 0;
    }

    public static bool HasUsableUV0(GameObject root)
    {
        if (root == null) return false;

        ImportedOBJMetadata metadata = root.GetComponent<ImportedOBJMetadata>();
        if (metadata != null) return metadata.hasUV0;

        MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(true);
        foreach (MeshFilter filter in filters)
        {
            Mesh mesh = filter.sharedMesh;
            if (mesh != null && mesh.vertexCount > 0 && mesh.uv != null && mesh.uv.Length == mesh.vertexCount)
                return true;
        }

        SkinnedMeshRenderer[] skinned = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        foreach (SkinnedMeshRenderer renderer in skinned)
        {
            Mesh mesh = renderer.sharedMesh;
            if (mesh != null && mesh.vertexCount > 0 && mesh.uv != null && mesh.uv.Length == mesh.vertexCount)
                return true;
        }

        return false;
    }

    public static bool TryApplyAlbedo(GameObject root, string texturePath)
    {
        if (root == null || string.IsNullOrWhiteSpace(texturePath)) return false;

        if (!HasUsableUV0(root))
        {
            StatusToast.Show("This mesh has no UV coordinates, so the albedo cannot be displayed. Keeping the grey head material.", true);
            return false;
        }

        if (!File.Exists(texturePath))
        {
            StatusToast.Show("Albedo texture file could not be found. Keeping the grey head material.", true);
            return false;
        }

        Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        texture.name = Path.GetFileNameWithoutExtension(texturePath);
        texture.wrapMode = TextureWrapMode.Repeat;

        try
        {
            byte[] bytes = File.ReadAllBytes(texturePath);
            if (!ImageConversion.LoadImage(texture, bytes, false))
            {
                Object.Destroy(texture);
                StatusToast.Show("HairBrush could not read that albedo image. Keeping the grey head material.", true);
                return false;
            }
        }
        catch (System.Exception ex)
        {
            Object.Destroy(texture);
            Debug.LogException(ex);
            StatusToast.Show("HairBrush could not read that albedo image. Keeping the grey head material.", true);
            return false;
        }

        HashSet<Material> materials = new HashSet<Material>();
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        foreach (Renderer renderer in renderers)
        {
            Material[] slots = renderer.sharedMaterials;
            if (slots == null) continue;
            foreach (Material material in slots)
                if (material != null) materials.Add(material);
        }

        if (materials.Count == 0)
        {
            Object.Destroy(texture);
            StatusToast.Show("No head material was available for the albedo. Keeping the grey head material.", true);
            return false;
        }

        foreach (Material material in materials)
        {
            SetBaseMap(material, texture);
            SetBaseColor(material, Color.white);
        }

        StatusToast.Show("Albedo applied: " + Path.GetFileName(texturePath));
        return true;
    }

    private static Shader FindViewportShader()
    {
        if (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null)
        {
            Shader urp = Shader.Find("Universal Render Pipeline/Lit");
            if (urp != null) return urp;

            Shader hdrp = Shader.Find("HDRP/Lit");
            if (hdrp != null) return hdrp;
        }

        return Shader.Find("Standard");
    }

    private static void SetBaseColor(Material material, Color color)
    {
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        else if (material.HasProperty("_Color")) material.SetColor("_Color", color);
    }

    private static void SetBaseMap(Material material, Texture texture)
    {
        if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
        else if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", texture);
    }
}
