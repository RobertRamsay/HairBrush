using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.InputSystem;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class HairCard : MonoBehaviour
{
    [System.Serializable]
    public struct GroomState
    {
        public float length, width, bend, twist, depth;
        public int segments;
        public float x, y, z;
        public float uScale, vScale, uOffset, vOffset;
    }

    // A POST keeps its authored scalar delta, but its Bend/X/Y/Z contribution can have a
    // different root-to-tip profile from both the group root and the other POSTs. The manager's
    // scalar evaluator remains untouched; this transient list only carries profile provenance
    // into mesh generation so those additive deltas are shaped independently per row.
    public struct PostShapeProfileContribution
    {
        public int postId;
        public float bend, x, y, z;
    }

    // Native card cross-section: left edge / raised centre / right edge. The ridge height
    // follows card width so narrow and wide cards keep the same shallow convex profile.
    public const float CrossSectionRidgeRatio = 0.18f;
    public const int CrossSectionColumns = 3;

    [Header("Grooming Parameters")]
    public float width = 0.01f;
    public float length = 0.2f;
    [Range(1, 36)] public int segments = 12;

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

    private GroomState canonicalState;
    private bool hasCanonicalState;
    private readonly List<PostShapeProfileContribution> postShapeProfileContributions = new List<PostShapeProfileContribution>();

    private bool clumpActive;
    private Vector3 clumpSurfacePoint;
    private Vector3 clumpSurfaceNormal;
    private float clumpStrength;
    private AnimationCurve clumpCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    // The native CLUMPER stage writes a derived mesh after the normal card mesh has been built.
    // Keep a compact signature of that normal source mesh. If another authority asks us to
    // regenerate exactly the same source while a CLUMPER override is active, do not overwrite
    // the already-clumped mesh. A genuine source change clears the override automatically.
    private int generatedMeshSignature;
    private bool externalClumpOverrideActive;
    private int externalClumpSourceSignature;

    public float GetEmbedDepth() { return currentEmbedDepth; }
    public float GetOffsetX() { return storedOffsetX; }
    public float GetOffsetY() { return storedOffsetY; }
    public float GetOffsetZ() { return storedOffsetZ; }
    public Vector3 GetSpawnHitPoint() { return spawnHitPoint; }
    public Vector3 GetSurfaceNormal() { return surfaceNormal; }
    public float GetCrossSectionRidgeHeight() { return Mathf.Max(.0005f, width) * flattenFactor * CrossSectionRidgeRatio; }
    public int GetGeneratedMeshSignature() { return generatedMeshSignature; }

    public void MarkExternalClumpOverride()
    {
        externalClumpOverrideActive = true;
        externalClumpSourceSignature = generatedMeshSignature;
    }

    public void ClearExternalClumpOverride()
    {
        externalClumpOverrideActive = false;
    }

    public void ClearPostShapeProfileContributions()
    {
        postShapeProfileContributions.Clear();
    }

    public void AddPostShapeProfileContribution(int postId, float bend, float x, float y, float z)
    {
        if (Mathf.Abs(bend) + Mathf.Abs(x) + Mathf.Abs(y) + Mathf.Abs(z) <= .000001f) return;
        postShapeProfileContributions.Add(new PostShapeProfileContribution
        {
            postId = postId,
            bend = bend,
            x = x,
            y = y,
            z = z
        });
    }

    // Local per-row rotation which, after the GameObject's existing full X/Y/Z transform,
    // yields the requested root-to-tip angle profile. The scalar result is still the normal
    // canonical + POST evaluation. Each POST contribution then replaces only its share of the
    // group profile with that POST's own profile, preserving additive/spatial weighting exactly.
    public Quaternion GetLengthProfileRotation(float t)
    {
        t = Mathf.Clamp01(t);
        float bendMultiplier = PostShapeCurveBridge.EvaluateRoot(groupId, GroomShapeCurveChannel.Bend, t);
        float xMultiplier = PostShapeCurveBridge.EvaluateRoot(groupId, GroomShapeCurveChannel.X, t);
        float yMultiplier = PostShapeCurveBridge.EvaluateRoot(groupId, GroomShapeCurveChannel.Y, t);
        float zMultiplier = PostShapeCurveBridge.EvaluateRoot(groupId, GroomShapeCurveChannel.Z, t);

        float profiledBend = bendAngle * bendMultiplier;
        float profiledX = storedOffsetX * xMultiplier;
        float profiledY = storedOffsetY * yMultiplier;
        float profiledZ = storedOffsetZ * zMultiplier;

        foreach (PostShapeProfileContribution contribution in postShapeProfileContributions)
        {
            profiledBend += contribution.bend *
                (PostShapeCurveBridge.EvaluatePost(contribution.postId, GroomShapeCurveChannel.Bend, t) - bendMultiplier);
            profiledX += contribution.x *
                (PostShapeCurveBridge.EvaluatePost(contribution.postId, GroomShapeCurveChannel.X, t) - xMultiplier);
            profiledY += contribution.y *
                (PostShapeCurveBridge.EvaluatePost(contribution.postId, GroomShapeCurveChannel.Y, t) - yMultiplier);
            profiledZ += contribution.z *
                (PostShapeCurveBridge.EvaluatePost(contribution.postId, GroomShapeCurveChannel.Z, t) - zMultiplier);
        }

        Quaternion fullOffset = Quaternion.Euler(storedOffsetX, storedOffsetY, storedOffsetZ);
        Quaternion curvedOffset = Quaternion.Euler(profiledX, profiledY, profiledZ);
        Quaternion bendAndTwist = Quaternion.Euler(profiledBend, 0f, twistAngle * t);

        return Quaternion.Inverse(fullOffset) * curvedOffset * bendAndTwist;
    }

    public GroomState GetCanonicalState()
    {
        if (!hasCanonicalState)
        {
            canonicalState = ReadRenderedState();
            hasCanonicalState = true;
        }
        return canonicalState;
    }

    public void SetCanonicalState(GroomState state, bool applyToRendered = false)
    {
        canonicalState = SanitizeState(state);
        hasCanonicalState = true;
        if (applyToRendered) ApplyEvaluatedState(canonicalState);
    }

    public void ApplyEvaluatedState(GroomState state)
    {
        state = SanitizeState(state);
        length = state.length;
        width = state.width;
        segments = state.segments;
        bendAngle = state.bend;
        twistAngle = state.twist;
        storedOffsetX = state.x;
        storedOffsetY = state.y;
        storedOffsetZ = state.z;
        currentEmbedDepth = state.depth;
        uScale = state.uScale;
        vScale = state.vScale;
        uOffset = state.uOffset;
        vOffset = state.vOffset;
        if (surfaceNormal != Vector3.zero) UpdateTransformOrientation(currentEmbedDepth);
        GenerateMesh();
    }

    GroomState ReadRenderedState()
    {
        return new GroomState
        {
            length = length,
            width = width,
            segments = segments,
            bend = bendAngle,
            twist = twistAngle,
            depth = currentEmbedDepth,
            x = storedOffsetX,
            y = storedOffsetY,
            z = storedOffsetZ,
            uScale = uScale,
            vScale = vScale,
            uOffset = uOffset,
            vOffset = vOffset
        };
    }

    GroomState SanitizeState(GroomState state)
    {
        state.length = Mathf.Max(0.001f, state.length);
        state.width = Mathf.Max(0.0005f, state.width);
        state.segments = Mathf.Clamp(state.segments, 1, 36);
        state.depth = Mathf.Max(0f, state.depth);
        return state;
    }

    void CaptureCanonicalFromRendered()
    {
        canonicalState = SanitizeState(ReadRenderedState());
        hasCanonicalState = true;
    }

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
        CaptureCanonicalFromRendered();
    }

    public void UpdateDepth(float embedDepth)
    {
        currentEmbedDepth = embedDepth;
        UpdateTransformOrientation(currentEmbedDepth);
        CaptureCanonicalFromRendered();
    }

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
        uScale = newUScale;
        vScale = newVScale;
        uOffset = newUOffset;
        vOffset = newVOffset;
        if (surfaceNormal != Vector3.zero) UpdateTransformOrientation(currentEmbedDepth);
        CaptureCanonicalFromRendered();
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

    public void SetSelectionWeight(float weight) { selectionWeight = Mathf.Clamp01(weight); UpdateVisualHighlight(); }

    void Awake()
    {
        meshFilter = GetComponent<MeshFilter>();
        mesh = new Mesh { name = "ProceduralHairCard" };
        meshFilter.mesh = mesh;
        SetupMaterial();
        GenerateMesh();
        UpdateVisualHighlight();
        CaptureCanonicalFromRendered();
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

    public void SetSegments(int newSegments)
    {
        segments = Mathf.Clamp(newSegments, 1, 36);
        CaptureCanonicalFromRendered();
        GenerateMesh();
    }

    public void GenerateMesh()
    {
        if (mesh == null || segments < 1) return;

        const int columns = CrossSectionColumns;
        int numVertices = (segments + 1) * columns;
        baseVertices = new Vector3[numVertices];
        Vector2[] uvs = new Vector2[numVertices];
        int[] triangles = new int[segments * 12];
        float segmentHeight = length / segments;
        float halfWidth = width * 0.5f;
        float ridgeHeight = GetCrossSectionRidgeHeight();

        for (int i = 0; i <= segments; i++)
        {
            float z = i * segmentHeight;
            float t = (float)i / segments;
            float baseULeft = uScale < 0f ? 1f : 0f;
            float baseURight = uScale < 0f ? 0f : 1f;
            float finalULeft = baseULeft * Mathf.Abs(uScale) + uOffset;
            float finalURight = baseURight * Mathf.Abs(uScale) + uOffset;
            float finalUCenter = (finalULeft + finalURight) * .5f;

            float absVScale = Mathf.Abs(vScale);
            float baseV = (1f - t) * absVScale;
            if (vScale < 0f) baseV = absVScale - baseV;
            float finalV = baseV + vOffset;
            int index = i * columns;
            float currentWidth = halfWidth * flattenFactor;

            Vector3 left = new Vector3(-currentWidth, 0f, z);
            Vector3 center = new Vector3(0f, ridgeHeight, z);
            Vector3 right = new Vector3(currentWidth, 0f, z);

            if (clumpActive && t > 0f)
            {
                float influence = Mathf.Clamp01(clumpStrength * clumpCurve.Evaluate(t));
                Vector3 straightCenter = (left + right) * 0.5f;
                Vector3 worldAxisPoint = clumpSurfacePoint + clumpSurfaceNormal * (length * t);
                Vector3 targetCenter = transform.InverseTransformPoint(worldAxisPoint);
                Vector3 movedCenter = Vector3.Lerp(straightCenter, targetCenter, influence);
                Vector3 delta = movedCenter - straightCenter;
                left += delta;
                center += delta;
                right += delta;
            }

            Quaternion authoredRotation = GetLengthProfileRotation(t);
            left = authoredRotation * left;
            center = authoredRotation * center;
            right = authoredRotation * right;

            baseVertices[index] = left;
            baseVertices[index + 1] = center;
            baseVertices[index + 2] = right;
            uvs[index] = new Vector2(finalULeft, finalV);
            uvs[index + 1] = new Vector2(finalUCenter, finalV);
            uvs[index + 2] = new Vector2(finalURight, finalV);
        }

        int triIndex = 0;
        for (int i = 0; i < segments; i++)
        {
            int row = i * columns;
            int next = row + columns;

            // Left half of the convex strip.
            triangles[triIndex++] = row;
            triangles[triIndex++] = next;
            triangles[triIndex++] = row + 1;
            triangles[triIndex++] = row + 1;
            triangles[triIndex++] = next;
            triangles[triIndex++] = next + 1;

            // Right half.
            triangles[triIndex++] = row + 1;
            triangles[triIndex++] = next + 1;
            triangles[triIndex++] = row + 2;
            triangles[triIndex++] = row + 2;
            triangles[triIndex++] = next + 1;
            triangles[triIndex++] = next + 2;
        }

        int sourceSignature = ComputeGeneratedMeshSignature(baseVertices, uvs, segments);
        generatedMeshSignature = sourceSignature;

        // POST/other authorities can still call GenerateMesh every frame. If they produced the
        // exact same source that the CLUMPER stage already consumed, keep the derived mesh in
        // place. Any actual change to source vertices/UVs/topology automatically falls through.
        if (externalClumpOverrideActive && sourceSignature == externalClumpSourceSignature)
            return;

        externalClumpOverrideActive = false;
        mesh.Clear();
        mesh.vertices = baseVertices;
        mesh.uv = uvs;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
    }

    static int ComputeGeneratedMeshSignature(Vector3[] vertices, Vector2[] uvs, int segmentCount)
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + segmentCount;
            if (vertices != null)
            {
                hash = hash * 31 + vertices.Length;
                for (int i = 0; i < vertices.Length; i++)
                {
                    hash = hash * 31 + vertices[i].x.GetHashCode();
                    hash = hash * 31 + vertices[i].y.GetHashCode();
                    hash = hash * 31 + vertices[i].z.GetHashCode();
                }
            }
            if (uvs != null)
            {
                hash = hash * 31 + uvs.Length;
                for (int i = 0; i < uvs.Length; i++)
                {
                    hash = hash * 31 + uvs[i].x.GetHashCode();
                    hash = hash * 31 + uvs[i].y.GetHashCode();
                }
            }
            return hash;
        }
    }
}
