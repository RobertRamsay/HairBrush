using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

// Stateless group-level clumping. Each card is attracted to the centreline of its
// nearest card in the same group. This never writes HairCard authored/canonical state
// and never uses POST selection weights.
[DefaultExecutionOrder(5000)]
public class NearestCardClumpController : MonoBehaviour
{
    private readonly Dictionary<int, float> strengthByGroup = new();
    private readonly Dictionary<HairCard, HairCard> nearestByCard = new();

    private ModelViewer viewer;
    private FieldInfo hasSelectionField;
    private MethodInfo createSliderMethod;

    private GameObject sliderRow;
    private Slider slider;
    private int uiGroupId = int.MinValue;
    private float nextNearestRefresh;
    private int lastCardCount = -1;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<NearestCardClumpController>() != null) return;
        GameObject go = new GameObject("NearestCardClumpController");
        DontDestroyOnLoad(go);
        go.AddComponent<NearestCardClumpController>();
    }

    void Update()
    {
        Resolve();
        if (viewer == null) return;

        MaintainUI();

        HairCard[] cards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        if (cards.Length != lastCardCount || Time.unscaledTime >= nextNearestRefresh)
        {
            lastCardCount = cards.Length;
            nextNearestRefresh = Time.unscaledTime + 0.5f;
            RebuildNearestMap(cards);
        }
    }

    void LateUpdate()
    {
        HairCard[] cards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
        if (cards.Length == 0) return;

        bool anyClump = cards.Any(c => c != null && GetStrength(c.groupId) > 0.0001f);
        if (!anyClump) return;

        // First rebuild every participating card from the current evaluated groom values.
        // Then snapshot those clean meshes before deforming anything, so a card never
        // samples an already-clumped neighbour in the same frame.
        Dictionary<HairCard, Vector3[]> cleanVertices = new();
        foreach (HairCard card in cards)
        {
            if (card == null || GetStrength(card.groupId) <= 0.0001f) continue;
            card.GenerateMesh();
            MeshFilter mf = card.GetComponent<MeshFilter>();
            if (mf != null && mf.mesh != null)
                cleanVertices[card] = mf.mesh.vertices;
        }

        foreach (HairCard card in cards)
        {
            if (card == null) continue;
            float strength = GetStrength(card.groupId);
            if (strength <= 0.0001f) continue;
            if (!nearestByCard.TryGetValue(card, out HairCard target) || target == null) continue;
            if (!cleanVertices.TryGetValue(card, out Vector3[] sourceClean)) continue;
            if (!cleanVertices.TryGetValue(target, out Vector3[] targetClean)) continue;
            DeformTowardNearest(card, target, sourceClean, targetClean, strength);
        }
    }

    void Resolve()
    {
        if (viewer != null) return;
        viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer == null) return;

        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        hasSelectionField = typeof(ModelViewer).GetField("hasSelectionHotspot", flags);
        createSliderMethod = typeof(ModelViewer).GetMethod("CreateSliderUI", flags);
    }

    bool InLocalPostMode()
    {
        return hasSelectionField != null && hasSelectionField.GetValue(viewer) is bool selected && selected;
    }

    public float GetStrength(int groupId)
    {
        return strengthByGroup.TryGetValue(groupId, out float value) ? value : 0f;
    }

    public void SetStrength(int groupId, float value)
    {
        value = Mathf.Clamp01(value);
        float old = GetStrength(groupId);
        strengthByGroup[groupId] = value;

        // Returning to zero must immediately restore ordinary generated meshes.
        if (old > 0.0001f && value <= 0.0001f)
        {
            foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
                if (card != null && card.groupId == groupId)
                    card.GenerateMesh();
        }
    }

    void MaintainUI()
    {
        if (viewer.groomingSliderPanelGO == null || createSliderMethod == null || InLocalPostMode())
        {
            DestroyUI();
            return;
        }

        int gid = viewer.currentGroupId;
        if (sliderRow != null && uiGroupId != gid)
            DestroyUI();

        if (sliderRow == null)
        {
            uiGroupId = gid;
            float current = GetStrength(gid);
            UnityAction<float> changed = value => SetStrength(gid, value);
            object[] args = { viewer.groomingSliderPanelGO.transform, "Clump", 0f, 1f, current, changed, null, 44f, 16 };
            sliderRow = createSliderMethod.Invoke(viewer, args) as GameObject;
            slider = args[6] as Slider;
            PlaceSliderNearShapeControls();
        }

        float wanted = GetStrength(gid);
        if (slider != null && !Mathf.Approximately(slider.value, wanted))
            slider.SetValueWithoutNotify(wanted);
        UpdateLabel(wanted);
    }

    void PlaceSliderNearShapeControls()
    {
        if (sliderRow == null || viewer.groomingSliderPanelGO == null) return;
        Transform panel = viewer.groomingSliderPanelGO.transform;
        Transform anchor = panel.Find("Twist Angle_Row");
        if (anchor == null) anchor = panel.Find("Bend Angle_Row");
        if (anchor != null)
            sliderRow.transform.SetSiblingIndex(Mathf.Min(anchor.GetSiblingIndex() + 1, panel.childCount - 1));
    }

    void UpdateLabel(float value)
    {
        if (sliderRow == null) return;
        TextMeshProUGUI label = sliderRow.GetComponentInChildren<TextMeshProUGUI>(true);
        if (label != null) label.text = "Clump: " + value.ToString("F3");
    }

    void DestroyUI()
    {
        if (sliderRow != null) Destroy(sliderRow);
        sliderRow = null;
        slider = null;
        uiGroupId = int.MinValue;
    }

    void RebuildNearestMap(HairCard[] cards)
    {
        nearestByCard.Clear();
        foreach (IGrouping<int, HairCard> grouping in cards.Where(c => c != null).GroupBy(c => c.groupId))
        {
            HairCard[] group = grouping.ToArray();
            if (group.Length < 2) continue;

            for (int i = 0; i < group.Length; i++)
            {
                HairCard source = group[i];
                Vector3 sourceRoot = RootWorld(source);
                HairCard best = null;
                float bestD2 = float.PositiveInfinity;

                for (int j = 0; j < group.Length; j++)
                {
                    if (i == j) continue;
                    float d2 = (RootWorld(group[j]) - sourceRoot).sqrMagnitude;
                    if (d2 < bestD2)
                    {
                        bestD2 = d2;
                        best = group[j];
                    }
                }

                if (best != null) nearestByCard[source] = best;
            }
        }
    }

    static Vector3 RootWorld(HairCard card)
    {
        Vector3 root = card.GetSpawnHitPoint();
        return root == Vector3.zero ? card.transform.position : root;
    }

    static void DeformTowardNearest(HairCard source, HairCard target, Vector3[] sourceClean, Vector3[] targetClean, float strength)
    {
        MeshFilter sourceMF = source.GetComponent<MeshFilter>();
        if (sourceMF == null || sourceMF.mesh == null || sourceClean == null || targetClean == null) return;

        Mesh sourceMesh = sourceMF.mesh;
        Vector3[] vertices = (Vector3[])sourceClean.Clone();
        int rows = vertices.Length / 2;
        if (rows < 2) return;

        for (int row = 1; row < rows; row++)
        {
            float t = (float)row / (rows - 1);
            // Root remains planted; attraction ramps smoothly toward the tip.
            float lengthFalloff = t * t * (3f - 2f * t);
            float influence = Mathf.Clamp01(strength * lengthFalloff);
            if (influence <= 0f) continue;

            int leftIndex = row * 2;
            int rightIndex = leftIndex + 1;
            Vector3 left = vertices[leftIndex];
            Vector3 right = vertices[rightIndex];
            Vector3 ownCenter = (left + right) * 0.5f;
            Vector3 targetWorld = SampleCentreWorld(target, targetClean, t);
            Vector3 targetLocal = source.transform.InverseTransformPoint(targetWorld);
            Vector3 newCenter = Vector3.Lerp(ownCenter, targetLocal, influence);
            Vector3 halfSpan = (right - left) * 0.5f;

            vertices[leftIndex] = newCenter - halfSpan;
            vertices[rightIndex] = newCenter + halfSpan;
        }

        sourceMesh.vertices = vertices;
        sourceMesh.RecalculateNormals();
        sourceMesh.RecalculateBounds();
    }

    static Vector3 SampleCentreWorld(HairCard card, Vector3[] vertices, float t)
    {
        int rows = vertices.Length / 2;
        if (rows <= 0) return card.transform.position;
        if (rows == 1) return card.transform.TransformPoint((vertices[0] + vertices[1]) * 0.5f);

        float rowF = Mathf.Clamp01(t) * (rows - 1);
        int a = Mathf.Clamp(Mathf.FloorToInt(rowF), 0, rows - 1);
        int b = Mathf.Min(a + 1, rows - 1);
        float f = rowF - a;
        Vector3 ca = (vertices[a * 2] + vertices[a * 2 + 1]) * 0.5f;
        Vector3 cb = (vertices[b * 2] + vertices[b * 2 + 1]) * 0.5f;
        return card.transform.TransformPoint(Vector3.Lerp(ca, cb, f));
    }
}
