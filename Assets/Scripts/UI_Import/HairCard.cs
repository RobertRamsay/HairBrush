using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.InputSystem;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class HairCard : MonoBehaviour
{
    [Header("Grooming Parameters")]
    public float width = 0.01f;
    public float length = 0.2f;
    [Range(4, 36)] public int segments = 12;

    [Header("Deformations")]
    public float bendAngle = 0f;
    public float twistAngle = 0f;
    public float flattenFactor = 1f;

    [Header("UV Settings")]
    public float uScale = 1.0f;
    public float vScale = 1.0f;
    public float uOffset = 0.0f;
    public float vOffset = 0.0f;

    [Header("Grouping")]
    public int groupId = 0;

    [Header("Selection State")]
    [Range(0f, 1f)] public float selectionWeight = 0f;

    private MeshFilter meshFilter;
    private Mesh mesh;
    private Vector3[] baseVertices;
    private Vector3 spawnHitPoint;
    private Vector3 surfaceNormal;
    private float currentEmbedDepth = 0.01f;
    private float storedOffsetX, storedOffsetY, storedOffsetZ;
    private float baseLength, baseWidth, baseBend, baseTwist, baseEmbedDepth;
    private int baseSegments;
    private float baseOffsetX, baseOffsetY, baseOffsetZ;
    private Material cardMaterial;

    // Clump is an upstream groom deformation: straight/length shape -> clump -> bend/twist -> card angle transform.
    // It never overwrites the authored groom parameters above.
    private bool clumpActive;
    private Vector3 clumpSurfacePoint;
    private Vector3 clumpSurfaceNormal;
    private float clumpStrength;
    private AnimationCurve clumpCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    public float GetEmbedDepth() { return currentEmbedDepth; }
    public float GetOffsetX() { return storedOffsetX; }
    public float GetOffsetY() { return storedOffsetY; }
    public float GetOffsetZ() { return storedOffsetZ; }
    public Vector3 GetSpawnHitPoint() { return spawnHitPoint; }
    public Vector3 GetSurfaceNormal() { return surfaceNormal; }

    public void SetClumpModifier(Vector3 surfacePoint, Vector3 normal, float strength, AnimationCurve curve)
    {
        clumpSurfacePoint = surfacePoint;
        clumpSurfaceNormal = normal.sqrMagnitude > 0f ? normal.normalized : surfaceNormal.normalized;
        clumpStrength = Mathf.Clamp01(strength);
        clumpCurve = curve ?? AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
        clumpActive = clumpStrength > 0.0001f;
        GenerateMesh();
    }

    public void ClearClumpModifier()
    {
        clumpActive = false;
        clumpStrength = 0f;
        GenerateMesh();
    }

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

    public void UpdateDepth(float embedDepth) { currentEmbedDepth = embedDepth; UpdateTransformOrientation(currentEmbedDepth); }

    private void UpdateTransformOrientation(float embedDepth)
    {
        transform.position = spawnHitPoint - (surfaceNormal * embedDepth);
        transform.rotation = Quaternion.LookRotation(surfaceNormal) * Quaternion.Euler(storedOffsetX, storedOffsetY, storedOffsetZ);
    }

    public void UpdateVisualHighlight()
    {
        if (cardMaterial == null) return;
        Color finalColor = Color.Lerp(Color.yellow, Color.white, selectionWeight);
        if (cardMaterial.HasProperty("_BaseColor")) cardMaterial.SetColor("_BaseColor", finalColor);
        if (cardMaterial.HasProperty("_Color")) cardMaterial.SetColor("_Color", finalColor);
    }

    public void SetParameters(float newLength, float newWidth, int newSegments, float newBend, float newTwist, float offsetX, float offsetY, float offsetZ, float newEmbedDepth, float strengthMultiplier = 1f, float newUScale = 1f, float newVScale = 1f, float newUOffset = 0f, float newVOffset = 0f)
    {
        if (selectionWeight > 0f)
        {
            float w = Mathf.Clamp01(selectionWeight * strengthMultiplier);
            length = Mathf.Max(0.001f, Mathf.Lerp(baseLength, newLength, w));
            width = Mathf.Lerp(baseWidth, newWidth, w);
            segments = Mathf.RoundToInt(Mathf.Lerp(baseSegments, newSegments, w));
            bendAngle = Mathf.Lerp(baseBend, newBend, w);
            twistAngle = Mathf.Lerp(baseTwist, newTwist, w);
            storedOffsetX = Mathf.Lerp(baseOffsetX, offsetX, w);
            storedOffsetY = Mathf.Lerp(baseOffsetY, offsetY, w);
            storedOffsetZ = Mathf.Lerp(baseOffsetZ, offsetZ, w);
            currentEmbedDepth = Mathf.Lerp(baseEmbedDepth, newEmbedDepth, w);
        }
        else
        {
            length = Mathf.Max(0.001f, newLength); width = newWidth; segments = newSegments;
            bendAngle = newBend; twistAngle = newTwist;
            storedOffsetX = offsetX; storedOffsetY = offsetY; storedOffsetZ = offsetZ; currentEmbedDepth = newEmbedDepth;
        }
        uScale = newUScale; vScale = newVScale; uOffset = newUOffset; vOffset = newVOffset;
        if (surfaceNormal != Vector3.zero) UpdateTransformOrientation(currentEmbedDepth);
        GenerateMesh();
    }

    public void CaptureBaseState(float activeLength, float activeWidth, int activeSegments, float activeBend, float activeTwist, float activeDepth, float ox, float oy, float oz)
    {
        baseLength = activeLength; baseWidth = activeWidth; baseSegments = activeSegments; baseBend = activeBend; baseTwist = activeTwist;
        baseEmbedDepth = activeDepth; baseOffsetX = ox; baseOffsetY = oy; baseOffsetZ = oz;
    }

    public void SetSelectionWeight(float weight) { selectionWeight = Mathf.Clamp01(weight); UpdateVisualHighlight(); }

    void Awake()
    {
        meshFilter = GetComponent<MeshFilter>();
        mesh = new Mesh { name = "ProceduralHairCard" };
        meshFilter.mesh = mesh;
        SetupMaterial(); GenerateMesh(); UpdateVisualHighlight();
    }

    void OnValidate() { if (mesh != null) GenerateMesh(); }
    public void ApplyDeformations() { GenerateMesh(); }

    void Update()
    {
        if (Keyboard.current == null) return;
        if (Keyboard.current.digit1Key.wasPressedThisFrame || Keyboard.current.numpad1Key.wasPressedThisFrame) SetDoubleSided(false);
        if (Keyboard.current.digit2Key.wasPressedThisFrame || Keyboard.current.numpad2Key.wasPressedThisFrame) SetDoubleSided(true);
    }

    void SetupMaterial()
    {
        MeshRenderer mr = GetComponent<MeshRenderer>();
        if (mr == null) return;
        if (mr.sharedMaterial != null) cardMaterial = new Material(mr.sharedMaterial);
        else
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (shader != null) cardMaterial = new Material(shader);
        }
        if (cardMaterial == null) return;
        cardMaterial.name = "HairCardInstance_" + GetInstanceID();
        if (cardMaterial.HasProperty("_BaseColor")) cardMaterial.SetColor("_BaseColor", Color.yellow);
        if (cardMaterial.HasProperty("_Color")) cardMaterial.SetColor("_Color", Color.yellow);
        if (cardMaterial.HasProperty("_Cull")) cardMaterial.SetFloat("_Cull", 0f);
        cardMaterial.EnableKeyword("_DOUBLESIDED_ON");
        mr.material = cardMaterial;
    }

    public void SetDoubleSided(bool enabled)
    {
        if (cardMaterial != null && cardMaterial.HasProperty("_Cull")) cardMaterial.SetFloat("_Cull", enabled ? 0f : 2f);
    }

    public void SetSegments(int newSegments) { segments = Mathf.Clamp(newSegments, 4, 36); GenerateMesh(); }

    public void GenerateMesh()
    {
        if (mesh == null || segments < 1) return;
        int numVertices = (segments + 1) * 2;
        baseVertices = new Vector3[numVertices];
        Vector2[] uvs = new Vector2[numVertices];
        int[] triangles = new int[segments * 6];
        float segmentHeight = length / segments;
        float halfWidth = width * 0.5f;

        for (int i = 0; i <= segments; i++)
        {
            float z = i * segmentHeight;
            float t = (float)i / segments;
            float baseULeft = uScale < 0f ? 1f : 0f;
            float baseURight = uScale < 0f ? 0f : 1f;
            float finalULeft = baseULeft * Mathf.Abs(uScale) + uOffset;
            float finalURight = baseURight * Mathf.Abs(uScale) + uOffset;
            float baseV = t * Mathf.Abs(vScale);
            if (vScale < 0f) baseV = Mathf.Abs(vScale) - baseV;
            float finalV = baseV + vOffset;
            int index = i * 2;
            float currentWidth = halfWidth * flattenFactor;

            // 1) Build the straight length/width shape.
            Vector3 left = new Vector3(-currentWidth, 0f, z);
            Vector3 right = new Vector3(currentWidth, 0f, z);

            // 2) Converge that straight shape toward the generated clump attractor.
            if (clumpActive && t > 0f)
            {
                float influence = Mathf.Clamp01(clumpStrength * clumpCurve.Evaluate(t));
                Vector3 straightCenter = (left + right) * 0.5f;
                Vector3 worldAxisPoint = clumpSurfacePoint + clumpSurfaceNormal * (length * t);
                Vector3 targetCenter = transform.InverseTransformPoint(worldAxisPoint);
                Vector3 center = Vector3.Lerp(straightCenter, targetCenter, influence);
                Vector3 halfSpan = (right - left) * 0.5f;
                left = center - halfSpan;
                right = center + halfSpan;
            }

            // 3) Bend/twist the already-clumped shape. Angle X/Y/Z is the card transform,
            // so it naturally remains downstream of all local mesh deformation.
            Quaternion authoredRotation = Quaternion.Euler(bendAngle * (t * t), 0f, twistAngle * t);
            left = authoredRotation * left;
            right = authoredRotation * right;

            baseVertices[index] = left; baseVertices[index + 1] = right;
            uvs[index] = new Vector2(finalULeft, finalV); uvs[index + 1] = new Vector2(finalURight, finalV);
        }

        int triIndex = 0;
        for (int i = 0; i < segments; i++)
        {
            int r = i * 2;
            triangles[triIndex++] = r; triangles[triIndex++] = r + 2; triangles[triIndex++] = r + 1;
            triangles[triIndex++] = r + 1; triangles[triIndex++] = r + 2; triangles[triIndex++] = r + 3;
        }
        mesh.Clear(); mesh.vertices = baseVertices; mesh.uv = uvs; mesh.triangles = triangles;
        mesh.RecalculateNormals(); mesh.RecalculateBounds();
    }
}
