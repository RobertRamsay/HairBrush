using System.Reflection;
using System.Linq;
using UnityEngine;

// Keeps the Texture Generator's centre workspace authoritative.
// The legacy UV-rect workspace also knows about HairTexturePreviewPlane and can otherwise
// leave the source HairCard texture visible. In generator mode we deliberately display
// the runtime procedural atlas with a simple unlit preview material instead.
[DefaultExecutionOrder(9900)]
public class TextureGeneratorPreviewAuthority : MonoBehaviour
{
    private TextureEditorManager manager;
    private GameObject previewPlane;
    private Material previewMaterial;
    private FieldInfo generatedTextureField;
    private Texture lastTexture;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<TextureGeneratorPreviewAuthority>() != null) return;
        GameObject go = new GameObject("TextureGeneratorPreviewAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<TextureGeneratorPreviewAuthority>();
    }

    void LateUpdate()
    {
        Resolve();
        if (!GeneratorModeActive()) return;
        if (manager == null || previewPlane == null || generatedTextureField == null) return;

        Texture atlas = generatedTextureField.GetValue(manager) as Texture;
        if (atlas == null) return;

        EnsurePreviewMaterial();
        if (previewMaterial == null) return;

        if (atlas != lastTexture)
        {
            AssignTexture(previewMaterial, atlas);
            lastTexture = atlas;
        }

        MeshRenderer renderer = previewPlane.GetComponent<MeshRenderer>();
        if (renderer != null && renderer.sharedMaterial != previewMaterial)
            renderer.sharedMaterial = previewMaterial;

        // The UV rectangle workspace belongs to the old texture-authoring flow. Its cyan
        // overlays should not sit on top of the procedural atlas while generating clusters.
        GameObject uvVisuals = FindInactiveGameObject("TextureUVRectVisuals");
        if (uvVisuals != null && uvVisuals.activeSelf)
            uvVisuals.SetActive(false);

        GameObject uvSection = FindInactiveGameObject("UVWorkspaceSection");
        if (uvSection != null && uvSection.activeSelf)
            uvSection.SetActive(false);
    }

    void Resolve()
    {
        if (manager == null)
        {
            manager = FindFirstObjectByType<TextureEditorManager>();
            if (manager != null)
            {
                generatedTextureField = typeof(TextureEditorManager).GetField(
                    "generatedHairTexture",
                    BindingFlags.Instance | BindingFlags.NonPublic);
            }
        }

        if (previewPlane == null)
            previewPlane = FindInactiveGameObject("HairTexturePreviewPlane");
    }

    bool GeneratorModeActive()
    {
        GameObject left = FindInactiveGameObject("TextureClusterListPanel");
        GameObject right = FindInactiveGameObject("TextureGeneratorControlsPanel");
        return (left != null && left.activeInHierarchy) || (right != null && right.activeInHierarchy);
    }

    void EnsurePreviewMaterial()
    {
        if (previewMaterial != null) return;

        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Unlit/Texture");
        if (shader == null) shader = Shader.Find("Sprites/Default");
        if (shader == null) return;

        previewMaterial = new Material(shader);
        previewMaterial.name = "GeneratedHairAtlas_Preview_Runtime";
        AssignTexture(previewMaterial, lastTexture);
    }

    static void AssignTexture(Material material, Texture texture)
    {
        if (material == null || texture == null) return;

        if (material.HasProperty("_BaseMap"))
            material.SetTexture("_BaseMap", texture);
        if (material.HasProperty("_MainTex"))
            material.SetTexture("_MainTex", texture);
        if (material.HasProperty("_BaseColor"))
            material.SetColor("_BaseColor", Color.white);
        if (material.HasProperty("_Color"))
            material.SetColor("_Color", Color.white);
    }

    static GameObject FindInactiveGameObject(string objectName)
    {
        Transform found = FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .FirstOrDefault(t => t != null && t.name == objectName);
        return found != null ? found.gameObject : null;
    }

    void OnDestroy()
    {
        if (previewMaterial != null)
            Destroy(previewMaterial);
    }
}
