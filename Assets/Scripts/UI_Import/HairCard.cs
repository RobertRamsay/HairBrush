using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.InputSystem;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class HairCard : MonoBehaviour
{
    [Header("Grooming Parameters")]
    public float width = 0.01f;
    public float length = 0.2f;
    [Range(4, 36)]
    public int segments = 12;

    [Header("Deformations")]
    public float bendAngle = 0f;
    public float twistAngle = 0f;
    public float flattenFactor = 1f;

    [Header("Grouping")]
    public int groupId = 0;

    private MeshFilter meshFilter;
    private Mesh mesh;
    private Vector3[] baseVertices;

    private Vector3 spawnHitPoint;
    private Vector3 surfaceNormal;
    private float currentEmbedDepth = 0.01f;
    private float storedOffsetX;
    private float storedOffsetY;
    private float storedOffsetZ;

    [Header("Selection State")]
    [Range(0f, 1f)]
    public float selectionWeight = 0f;

    private float baseLength;
    private float baseWidth;
    private int baseSegments;
    private float baseBend;
    private float baseTwist;
    private float baseEmbedDepth;
    private float baseOffsetX;
    private float baseOffsetY;
    private float baseOffsetZ;

    private Material cardMaterial;

    // Public Getters for Relative Mode Lookups
    public float GetEmbedDepth() { return currentEmbedDepth; }
    public float GetOffsetX() { return storedOffsetX; }
    public float GetOffsetY() { return storedOffsetY; }
    public float GetOffsetZ() { return storedOffsetZ; }

    public void SetPlacementData(Vector3 hitPoint, Vector3 normal, float embedDepth, float offsetX, float offsetY, float offsetZ, int assignedGroupId)
    {
        spawnHitPoint = hitPoint;
        surfaceNormal = normal;
        currentEmbedDepth = embedDepth;
        storedOffsetX = offsetX;
        storedOffsetY = offsetY;
        storedOffsetZ = offsetZ;
        groupId = assignedGroupId;

        UpdateTransformOrientation(currentEmbedDepth);
    }

    public void UpdateDepth(float embedDepth)
    {
        currentEmbedDepth = embedDepth;
        UpdateTransformOrientation(currentEmbedDepth);
    }

    private void UpdateTransformOrientation(float embedDepth)
    {
        transform.position = spawnHitPoint - (surfaceNormal * embedDepth);

        Quaternion baseRotation = Quaternion.LookRotation(surfaceNormal);
        Quaternion offsetRotation = Quaternion.Euler(storedOffsetX, storedOffsetY, storedOffsetZ);
        transform.rotation = baseRotation * offsetRotation;
    }

    public void UpdateVisualHighlight()
    {
        if (cardMaterial != null)
        {
            Color baseColor = Color.yellow;
            Color highlightColor = Color.white;
            Color finalColor = Color.Lerp(baseColor, highlightColor, selectionWeight);

            if (cardMaterial.HasProperty("_BaseColor"))
                cardMaterial.SetColor("_BaseColor", finalColor);
            if (cardMaterial.HasProperty("_Color"))
                cardMaterial.SetColor("_Color", finalColor);
        }
    }

    public void SetParameters(float newLength, float newWidth, int newSegments, float newBend, float newTwist, float offsetX, float offsetY, float offsetZ, float newEmbedDepth, float strengthMultiplier = 1f)
    {
        if (selectionWeight > 0f)
        {
            float effectiveWeight = Mathf.Clamp01(selectionWeight * strengthMultiplier);
            length = Mathf.Max(0.001f, Mathf.Lerp(baseLength, newLength, effectiveWeight));
            width = Mathf.Lerp(baseWidth, newWidth, effectiveWeight);
            segments = Mathf.RoundToInt(Mathf.Lerp(baseSegments, newSegments, effectiveWeight));
            bendAngle = Mathf.Lerp(baseBend, newBend, effectiveWeight);
            twistAngle = Mathf.Lerp(baseTwist, newTwist, effectiveWeight);
            storedOffsetX = Mathf.Lerp(baseOffsetX, offsetX, effectiveWeight);
            storedOffsetY = Mathf.Lerp(baseOffsetY, offsetY, effectiveWeight);
            storedOffsetZ = Mathf.Lerp(baseOffsetZ, offsetZ, effectiveWeight);
            currentEmbedDepth = Mathf.Lerp(baseEmbedDepth, newEmbedDepth, effectiveWeight);
        }
        else
        {
            length = Mathf.Max(0.001f, newLength);
            width = newWidth;
            segments = newSegments;
            bendAngle = newBend;
            twistAngle = newTwist;
            storedOffsetX = offsetX;
            storedOffsetY = offsetY;
            storedOffsetZ = offsetZ;
            currentEmbedDepth = newEmbedDepth;
        }

        if (surfaceNormal != Vector3.zero)
        {
            UpdateTransformOrientation(currentEmbedDepth);
        }

        GenerateMesh();
    }

    public void CaptureBaseState(float activeLength, float activeWidth, int activeSegments, float activeBend, float activeTwist, float activeDepth, float ox, float oy, float oz)
    {
        baseLength = activeLength;
        baseWidth = activeWidth;
        baseSegments = activeSegments;
        baseBend = activeBend;
        baseTwist = activeTwist;
        baseEmbedDepth = activeDepth;
        baseOffsetX = ox;
        baseOffsetY = oy;
        baseOffsetZ = oz;
    }

    public void SetSelectionWeight(float weight)
    {
        selectionWeight = Mathf.Clamp01(weight);
        UpdateVisualHighlight();
    }

    void Awake()
    {
        meshFilter = GetComponent<MeshFilter>();
        mesh = new Mesh();
        mesh.name = "ProceduralHairCard";
        meshFilter.mesh = mesh;

        SetupMaterial();
        GenerateMesh();
        UpdateVisualHighlight();
    }

    void OnValidate()
    {
        if (mesh != null)
        {
            GenerateMesh();
        }
    }

    public void ApplyDeformations()
    {
        GenerateMesh();
    }

    void Update()
    {
        if (Keyboard.current == null) return;

        if (Keyboard.current.digit1Key.wasPressedThisFrame || Keyboard.current.numpad1Key.wasPressedThisFrame)
        {
            SetDoubleSided(false);
        }
        if (Keyboard.current.digit2Key.wasPressedThisFrame || Keyboard.current.numpad2Key.wasPressedThisFrame)
        {
            SetDoubleSided(true);
        }
    }

    void SetupMaterial()
    {
        MeshRenderer mr = GetComponent<MeshRenderer>();
        if (mr != null)
        {
            if (mr.sharedMaterial != null)
            {
                cardMaterial = new Material(mr.sharedMaterial);
            }
            else
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null)
                {
                    shader = Shader.Find("Standard");
                }
                cardMaterial = new Material(shader);
            }

            cardMaterial.name = "HairCardInstance_" + GetInstanceID();

            if (cardMaterial.HasProperty("_BaseColor"))
                cardMaterial.SetColor("_BaseColor", Color.yellow);
            if (cardMaterial.HasProperty("_Color"))
                cardMaterial.SetColor("_Color", Color.yellow);

            if (cardMaterial.HasProperty("_Cull"))
            {
                cardMaterial.SetFloat("_Cull", 0.0f);
            }

            cardMaterial.EnableKeyword("_DOUBLESIDED_ON");
            mr.material = cardMaterial;
        }
    }

    public void SetDoubleSided(bool enabled)
    {
        MeshRenderer mr = GetComponent<MeshRenderer>();
        if (mr != null && cardMaterial != null)
        {
            float cullValue = enabled ? 0.0f : 2.0f;

            if (cardMaterial.HasProperty("_Cull"))
            {
                cardMaterial.SetFloat("_Cull", cullValue);
            }
        }
    }

    public void SetSegments(int newSegments)
    {
        segments = Mathf.Clamp(newSegments, 4, 36);
        GenerateMesh();
    }

    public void GenerateMesh()
    {
        int numVertices = (segments + 1) * 2;
        baseVertices = new Vector3[numVertices];
        Vector2[] uvs = new Vector2[numVertices];
        int[] triangles = new int[segments * 6];

        float segmentHeight = length / segments;
        float halfWidth = width * 0.5f;

        for (int i = 0; i <= segments; i++)
        {
            float z = i * segmentHeight;
            float v = (float)i / segments;

            int index = i * 2;

            float currentWidth = halfWidth * flattenFactor;
            Quaternion rotationOffset = Quaternion.Euler(bendAngle * (v * v), 0, twistAngle * v);

            baseVertices[index] = rotationOffset * new Vector3(-currentWidth, 0, z);
            uvs[index] = new Vector2(0, v);

            baseVertices[index + 1] = rotationOffset * new Vector3(currentWidth, 0, z);
            uvs[index + 1] = new Vector2(1, v);
        }

        int triIndex = 0;
        for (int i = 0; i < segments; i++)
        {
            int rootIndex = i * 2;

            triangles[triIndex++] = rootIndex;
            triangles[triIndex++] = rootIndex + 2;
            triangles[triIndex++] = rootIndex + 1;

            triangles[triIndex++] = rootIndex + 1;
            triangles[triIndex++] = rootIndex + 2;
            triangles[triIndex++] = rootIndex + 3;
        }

        mesh.Clear();
        mesh.vertices = baseVertices;
        mesh.uv = uvs;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
    }
}