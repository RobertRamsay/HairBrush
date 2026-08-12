using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

// Final authority for Ctrl+Click localized editing.
// The legacy slider callbacks (and variance callbacks) can still touch the whole group;
// this component restores the selection-entry state each LateUpdate and reapplies only
// the controls that have actually changed, weighted by brush selection * Strength.
[DefaultExecutionOrder(3200)]
public class SelectionLocalizedEditAuthority : MonoBehaviour
{
    private class Snapshot
    {
        public float length, width, bend, twist, depth;
        public int segments;
        public float x, y, z;
        public float uScale, vScale, uOffset, vOffset;
    }

    private struct Controls
    {
        public float length, width, bend, twist, depth;
        public int segments;
        public float x, y, z;
        public float uScale, vScale, uOffset, vOffset;
    }

    private readonly Dictionary<HairCard, Snapshot> snapshots = new();
    private ModelViewer viewer;
    private FieldInfo hasSelectionField;
    private FieldInfo hitPointField;
    private bool wasSelected;
    private Vector3 lastHitPoint;
    private int lastGroup = int.MinValue;
    private Controls baseline;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        GameObject go = new GameObject("SelectionLocalizedEditAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<SelectionLocalizedEditAuthority>();
    }

    void LateUpdate()
    {
        if (viewer == null)
        {
            viewer = FindFirstObjectByType<ModelViewer>();
            if (viewer == null) return;
            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            hasSelectionField = typeof(ModelViewer).GetField("hasSelectionHotspot", flags);
            hitPointField = typeof(ModelViewer).GetField("selectionHitPoint", flags);
        }

        bool selected = HasSelection();
        Vector3 hit = GetHitPoint();

        // A second Ctrl+Click while already selected moves the hotspot without toggling
        // hasSelectionHotspot, so hit-point/group changes also start a fresh edit basis.
        if (selected && (!wasSelected || viewer.currentGroupId != lastGroup || (hit - lastHitPoint).sqrMagnitude > 0.0000001f))
            CaptureSelectionBasis(hit);

        if (!selected)
        {
            snapshots.Clear();
            wasSelected = false;
            lastGroup = viewer.currentGroupId;
            lastHitPoint = hit;
            return;
        }

        Controls now = ReadControls();
        float strength = Mathf.Clamp01(viewer.selectionStrength);

        bool lengthChanged = !Mathf.Approximately(now.length, baseline.length);
        bool widthChanged = !Mathf.Approximately(now.width, baseline.width);
        bool segmentsChanged = now.segments != baseline.segments;
        bool bendChanged = !Mathf.Approximately(now.bend, baseline.bend);
        bool twistChanged = !Mathf.Approximately(now.twist, baseline.twist);
        bool depthChanged = !Mathf.Approximately(now.depth, baseline.depth);
        bool xChanged = !Mathf.Approximately(now.x, baseline.x);
        bool yChanged = !Mathf.Approximately(now.y, baseline.y);
        bool zChanged = !Mathf.Approximately(now.z, baseline.z);
        bool uScaleChanged = !Mathf.Approximately(now.uScale, baseline.uScale);
        bool vScaleChanged = !Mathf.Approximately(now.vScale, baseline.vScale);
        bool uOffsetChanged = !Mathf.Approximately(now.uOffset, baseline.uOffset);
        bool vOffsetChanged = !Mathf.Approximately(now.vOffset, baseline.vOffset);

        foreach (var pair in snapshots)
        {
            HairCard card = pair.Key;
            Snapshot s = pair.Value;
            if (card == null || card.groupId != viewer.currentGroupId) continue;

            float w = Mathf.Clamp01(card.selectionWeight * strength);

            float length = lengthChanged ? Mathf.Lerp(s.length, now.length, w) : s.length;
            float width = widthChanged ? Mathf.Lerp(s.width, now.width, w) : s.width;
            int segments = segmentsChanged ? Mathf.RoundToInt(Mathf.Lerp(s.segments, now.segments, w)) : s.segments;
            float bend = bendChanged ? Mathf.Lerp(s.bend, now.bend, w) : s.bend;
            float twist = twistChanged ? Mathf.Lerp(s.twist, now.twist, w) : s.twist;
            float depth = depthChanged ? Mathf.Lerp(s.depth, now.depth, w) : s.depth;
            float x = xChanged ? Mathf.Lerp(s.x, now.x, w) : s.x;
            float y = yChanged ? Mathf.Lerp(s.y, now.y, w) : s.y;
            float z = zChanged ? Mathf.Lerp(s.z, now.z, w) : s.z;
            float uScale = uScaleChanged ? Mathf.Lerp(s.uScale, now.uScale, w) : s.uScale;
            float vScale = vScaleChanged ? Mathf.Lerp(s.vScale, now.vScale, w) : s.vScale;
            float uOffset = uOffsetChanged ? Mathf.Lerp(s.uOffset, now.uOffset, w) : s.uOffset;
            float vOffset = vOffsetChanged ? Mathf.Lerp(s.vOffset, now.vOffset, w) : s.vOffset;

            // SetParameters normally applies selectionWeight internally for geometry but
            // not UVs. We already computed one consistent weighted result for everything,
            // so temporarily bypass that second interpolation.
            float selectionWeight = card.selectionWeight;
            card.SetSelectionWeight(0f);
            card.SetParameters(length, width, segments, bend, twist, x, y, z, depth, 1f,
                uScale, vScale, uOffset, vOffset);
            card.SetSelectionWeight(selectionWeight);
        }

        wasSelected = true;
        lastGroup = viewer.currentGroupId;
        lastHitPoint = hit;
    }

    void CaptureSelectionBasis(Vector3 hit)
    {
        snapshots.Clear();
        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
        {
            if (card.groupId != viewer.currentGroupId) continue;
            snapshots[card] = new Snapshot
            {
                length = card.length,
                width = card.width,
                segments = card.segments,
                bend = card.bendAngle,
                twist = card.twistAngle,
                depth = card.GetEmbedDepth(),
                x = card.GetOffsetX(), y = card.GetOffsetY(), z = card.GetOffsetZ(),
                uScale = card.uScale, vScale = card.vScale,
                uOffset = card.uOffset, vOffset = card.vOffset
            };
        }
        baseline = ReadControls();
        wasSelected = true;
        lastGroup = viewer.currentGroupId;
        lastHitPoint = hit;
    }

    Controls ReadControls() => new Controls
    {
        length = viewer.currentLength,
        width = viewer.currentWidth,
        segments = viewer.currentSegments,
        bend = viewer.currentBend,
        twist = viewer.currentTwist,
        depth = viewer.currentEmbedDepth,
        x = viewer.currentOffsetX, y = viewer.currentOffsetY, z = viewer.currentOffsetZ,
        uScale = viewer.currentUScale, vScale = viewer.currentVScale,
        uOffset = viewer.currentUOffset, vOffset = viewer.currentVOffset
    };

    bool HasSelection() => hasSelectionField != null && hasSelectionField.GetValue(viewer) is bool b && b;
    Vector3 GetHitPoint() => hitPointField != null && hitPointField.GetValue(viewer) is Vector3 v ? v : Vector3.zero;
}
