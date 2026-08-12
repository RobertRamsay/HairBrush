using UnityEngine;

// Older project files only stored the final card transform plus Angle X/Y/Z.
// Reconstruct the hidden scalp hit point and surface normal so deterministic
// variance, selection and clump regeneration continue to work after loading.
[DefaultExecutionOrder(1400)]
public class LoadedCardPlacementRepair : MonoBehaviour
{
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        GameObject go = new GameObject("LoadedCardPlacementRepair");
        DontDestroyOnLoad(go);
        go.AddComponent<LoadedCardPlacementRepair>();
    }

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + 0.2f;

        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
        {
            if (card == null || card.GetSurfaceNormal().sqrMagnitude > 0.000001f) continue;

            float ox = card.GetOffsetX();
            float oy = card.GetOffsetY();
            float oz = card.GetOffsetZ();
            Quaternion authoredOffset = Quaternion.Euler(ox, oy, oz);
            Quaternion surfaceRotation = card.transform.rotation * Quaternion.Inverse(authoredOffset);
            Vector3 normal = (surfaceRotation * Vector3.forward).normalized;
            if (normal.sqrMagnitude < 0.000001f) normal = card.transform.forward.normalized;

            float embed = card.GetEmbedDepth();
            Vector3 hitPoint = card.transform.position + normal * embed;

            float length = card.length;
            float width = card.width;
            int segments = card.segments;
            float bend = card.bendAngle;
            float twist = card.twistAngle;
            float uScale = card.uScale;
            float vScale = card.vScale;
            float uOffset = card.uOffset;
            float vOffset = card.vOffset;
            int groupId = card.groupId;

            card.SetPlacementData(hitPoint, normal, embed, ox, oy, oz, groupId);
            card.SetParameters(length, width, segments, bend, twist, ox, oy, oz, embed, 1f, uScale, vScale, uOffset, vOffset);
        }
    }
}
