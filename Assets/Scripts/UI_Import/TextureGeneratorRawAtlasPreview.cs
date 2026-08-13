using UnityEngine;
using System.Reflection;

// The texture-generator workspace must show the literal atlas pixels, not the hair-card shader.
// The hair shader can turn a wider white stamp into a lighting/alpha change, making thickness
// look like a dark gradient. This preview authority uses an unlit texture material instead.
[DefaultExecutionOrder(9600)]
public class TextureGeneratorRawAtlasPreview : MonoBehaviour
{
    TextureEditorManager manager;
    FieldInfo textureField;
    Material previewMaterial;
    Texture2D lastTexture;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<TextureGeneratorRawAtlasPreview>() != null) return;
        GameObject go = new GameObject("TextureGeneratorRawAtlasPreview");
        DontDestroyOnLoad(go);
        go.AddComponent<TextureGeneratorRawAtlasPreview>();
    }

    void Update()
    {
        if (manager == null)
        {
            manager = FindFirstObjectByType<TextureEditorManager>();
            if (manager != null)
                textureField = typeof(TextureEditorManager).GetField(
                    "generatedHairTexture",
                    BindingFlags.Instance | BindingFlags.NonPublic);
        }
        if (manager == null || textureField == null) return;

        GameObject panel = FindNamed("TextureGeneratorControlsPanel");
        if (panel == null || !panel.activeInHierarchy) return;

        Texture2D atlas = textureField.GetValue(manager) as Texture2D;
        if (atlas == null) return;

        // Show exact texels while developing the generator. No bilinear blur/mip shading.
        atlas.filterMode = FilterMode.Point;

        GameObject preview = FindNamed("HairTexturePreviewPlane");
        if (preview == null) return;
        MeshRenderer renderer = preview.GetComponent<MeshRenderer>();
        if (renderer == null) return;

        if (previewMaterial == null)
        {
            Shader shader = Shader.Find("Unlit/Texture");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader == null) return;

            previewMaterial = new Material(shader);
            previewMaterial.name = "GeneratedAtlas_RawPreview_Runtime";
            previewMaterial.color = Color.white;
        }

        if (atlas != lastTexture)
        {
            lastTexture = atlas;
            previewMaterial.mainTexture = atlas;
        }

        if (renderer.sharedMaterial != previewMaterial)
            renderer.sharedMaterial = previewMaterial;
    }

    void OnDestroy()
    {
        if (previewMaterial != null) Destroy(previewMaterial);
    }

    static GameObject FindNamed(string objectName)
    {
        foreach (Transform t in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (t != null && t.name == objectName) return t.gameObject;
        return null;
    }
}
