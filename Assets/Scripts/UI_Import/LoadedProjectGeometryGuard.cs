using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

// Project JSON stores the exact final per-card groom plus procedural modifier state.
// During load, variance UI can finish installing a frame later and re-apply itself.
// Capture the freshly reconstructed saved cards before modifier restoration settles,
// then restore those exact values once variance UI is installed. This is one-shot per load.
[DefaultExecutionOrder(-100)]
public class LoadedProjectGeometryGuard : MonoBehaviour
{
    private struct CardState
    {
        public HairCard card;
        public float length, width, bend, twist, embed, ox, oy, oz, uScale, vScale, uOffset, vOffset;
        public int segments;
    }

    private readonly List<CardState> captured = new();
    private HairProjectSaveData watchedProject;
    private bool waitingForSettle;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        GameObject go = new GameObject("LoadedProjectGeometryGuard");
        DontDestroyOnLoad(go);
        go.AddComponent<LoadedProjectGeometryGuard>();
    }

    void Update()
    {
        HairProjectSaveData pending = HairProjectSaveData.PendingModifierRestore;

        if (pending != null && pending != watchedProject)
        {
            int expected = pending.hairCards != null ? pending.hairCards.Count : 0;
            HairCard[] cards = FindObjectsByType<HairCard>(FindObjectsSortMode.None);
            if (cards.Length >= expected && expected > 0)
            {
                watchedProject = pending;
                Capture(cards);
                waitingForSettle = true;
            }
        }

        if (!waitingForSettle || HairProjectSaveData.PendingModifierRestore != null) return;

        GroomVarianceController variance = FindFirstObjectByType<GroomVarianceController>();
        if (variance == null) return;

        FieldInfo installed = typeof(GroomVarianceController).GetField("installed", BindingFlags.Instance | BindingFlags.NonPublic);
        if (installed != null && installed.GetValue(variance) is bool ready && !ready) return;

        Restore();
        waitingForSettle = false;
        captured.Clear();
    }

    void Capture(IEnumerable<HairCard> cards)
    {
        captured.Clear();
        foreach (HairCard card in cards.Where(c => c != null))
        {
            captured.Add(new CardState
            {
                card = card,
                length = card.length,
                width = card.width,
                segments = card.segments,
                bend = card.bendAngle,
                twist = card.twistAngle,
                embed = card.GetEmbedDepth(),
                ox = card.GetOffsetX(),
                oy = card.GetOffsetY(),
                oz = card.GetOffsetZ(),
                uScale = card.uScale,
                vScale = card.vScale,
                uOffset = card.uOffset,
                vOffset = card.vOffset
            });
        }
    }

    void Restore()
    {
        foreach (CardState s in captured)
        {
            if (s.card == null) continue;
            s.card.SetParameters(s.length, s.width, s.segments, s.bend, s.twist, s.ox, s.oy, s.oz, s.embed, 1f, s.uScale, s.vScale, s.uOffset, s.vOffset);
        }
    }
}
