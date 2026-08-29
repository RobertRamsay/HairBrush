using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public class HairCardSaveData
{
    public float posX,posY,posZ;
    public float rotX,rotY,rotZ,rotW;
    public float hitX,hitY,hitZ;
    public float normalX,normalY,normalZ;
    public float length;
    public float width;
    public int segments;
    public float bendAngle;
    public float twistAngle;
    public float flattenFactor;
    public float embedDepth;
    public float offsetX,offsetY,offsetZ;
    public float uScale;
    public float vScale;
    public float uOffset;
    public float vOffset;
    public int groupId;
    // Curl (spiral/coil) modifier - applied after width, before bend, in the shape pipeline.
    public float curlFrequency;
    public float curlDiameter;
    // Zero for legacy projects, which is exactly the correct no-wave default: EvaluateWave
    // early-outs on amplitude <= 0, so an older file renders bit-identically. JsonUtility runs
    // field initialisers first and only overwrites keys present in the JSON, so a missing
    // waveAmplitude/waveFrequency lands on 0f with no migration and no version bump.
    public float waveAmplitude;
    public float waveFrequency;
    // Initialised to 1, unlike the two above. JsonUtility runs field initialisers first and
    // only overwrites keys present in the JSON, so a project saved before Wave Direction
    // existed deserializes to 1 (up/down) rather than 0 (side to side). That is the deliberate
    // choice: the side-to-side original was the thing being replaced. Anyone who wants the old
    // look sets the slider to 0.
    public float waveDirection = 1f;
    // Initialised to the neutral 0.5, NOT 0. JsonUtility runs field initialisers first and
    // only overwrites keys present in the JSON, so a project saved before Arch existed lands
    // here and renders exactly as it always did. A default of 0 would flatten every card in
    // every legacy project to a plain ribbon.
    public float arch = 0.5f;

    // SYMMETRY. A mirrored card evaluates its geometry through a local-X mirror, so this has
    // to survive a round trip or every mirrored card in a reloaded project would come back
    // shaped like its partner instead of like its reflection.
    //
    // Backward compatible: JsonUtility leaves a missing bool as false, so projects saved
    // before symmetry existed load as all-unmirrored, which is exactly what they were.
    public bool mirrored;

    // The point and normal this card's RANDOMISATION is keyed to, which is not necessarily where
    // the card sits.
    //
    // Four hash sites derive per-card randomness from the spawn point, and two of them mix the
    // surface normal in as well: GroomVarianceController.SignedRandom (group variance),
    // PostVarianceAffectorBridge.SignedRandom (POST-local variance),
    // PostPredeterminedUVAuthority.StablePostThreshold (which cards a POST covers) and the
    // StableCardHash in both PostPredeterminedUVAuthority and GroupPredeterminedUVController
    // (which atlas rectangle a card draws). All of them round to a ten-thousandth, so moving a
    // root by a tenth of a millimetre re-rolls that card's variance AND its predetermined
    // rectangle. Any operation that moves a whole groom - the import rescale below, a REMAP onto
    // a different head - would otherwise scramble the randomisation of every card in the project
    // as its first visible act.
    //
    // So identity is separated from placement. It is stamped from the spawn point at creation and
    // then held still while the anchor moves. Absent in a project written before this existed,
    // which JsonUtility leaves at false: the loader then stamps identity from hit/normal, which
    // is bit-for-bit the historical mapping. No formatVersion bump is needed for that - and see
    // the note on VConventionFormatVersion for why a bump would be the wrong tool anyway.
    public bool hasIdentity;
    public float identityX,identityY,identityZ;
    public float identityNX,identityNY,identityNZ;

    // How much this card's LENGTHS have been scaled since identity was frozen. One deterministic
    // site - ClumperDeterministicLeaderAuthority.CardStableKey, which picks clump leaders -
    // quantises the authored length, width and depth alongside the anchor, so a rescale would
    // re-pick every leader even with the anchor held still. Initialised to 1, not 0: a file
    // without the key is a card that has never been rescaled, and CardStableKey divides by this.
    public float identityScale=1f;
}

// What the model this project was authored against was imported AS.
//
// ImportedOBJMetadata on the model root describes the import that just happened, under today's
// rule. This describes the one the project was written under. The two are compared on load and
// any difference in working scale is reconciled before a card is spawned - see
// RuntimeNavigationProjectIO.MigrateImportScale.
//
// A project written before this existed has no key here at all, so appliedScale stays 0, which
// is the sentinel for "unknown, assume it matches" rather than a scale of zero. Every other
// field is inert without it.
[Serializable]
public class ImportMetadataSaveData
{
    public float appliedScale;
    public string normalisationMode;
    public float normalisationTarget;
    public float measuredExtent;

    // Identity of the source geometry, from CustomOBJImporter. modelPath is a bare absolute path
    // and nothing verifies the file behind it is the one the groom was authored on; a mismatch
    // here is the difference between "your model moved" and "this is a different head".
    public int meshHash;
}

[Serializable] public class VarianceChannelSaveData { public string channel; public float amount; public int seed; }

// Runtime AnimationCurve key payload used by both group-root and POST-local Bend/X/Y/Z
// length profiles. Ownership is determined by the containing GroupSaveData/PostAffectorSaveData.
[Serializable]
public class GroomCurveKeySaveData
{
    public float time;
    public float value;
    public float inTangent;
    public float outTangent;
}

[Serializable]
public class UVRectSaveData
{
    public int id;
    public float uMin;
    public float vMin;
    public float uMax;
    public float vMax;

    // Which end of this strip the ROOT of a card lands on.
    //
    // A card always runs t=0 at the root to t=1 at the tip, and the UV ramp in
    // HairCard.GenerateMesh puts the root at the TOP of the rectangle (V = vMax) unless vScale
    // is negative. That is a coin flip against any given hair texture: a sheet drawn with its
    // strands hanging down and one drawn with them growing up are both perfectly ordinary, and
    // one sheet can carry some of each.
    //
    // So this is a property of the STRIP, not of the card and not of the group - which is why it
    // lives here rather than as a card field. Set it once in the texture editor and every card
    // that ever draws this rectangle comes out the right way up, in every group and every
    // project that uses the material.
    //
    // Absent in a project written before this existed, which JsonUtility leaves at false: the
    // historical root-at-top mapping. No formatVersion bump is needed for that.
    public bool flipV;
}

[Serializable]
public class HairMaterialSaveData
{
    public string name;
    public string albedoPath;
    public string normalPath;
    public string opacityPath;
    public float smooth = 0.56f;
    public float metal = 0.33f;

    // MASTER COLOUR (_HairTint), which the shader multiplies into the albedo. hasTint is what
    // separates "white" from "this project predates the control": absent in an older file it
    // deserializes to false, and the restore then puts the shader's OWN default back rather
    // than white, so an existing groom looks exactly as it did. New materials save true.
    // A slot the user emptied on purpose. Distinct from an empty PATH, which has always meant
    // "never loaded anything here, keep whatever the template material came with" - and still
    // does for every project written before CLEAR existed, all of which read false here.
    public bool albedoCleared = false;
    public bool normalCleared = false;
    public bool opacityCleared = false;

    public bool hasTint = false;
    public float tintR = 1f;
    public float tintG = 1f;
    public float tintB = 1f;

    // Predetermined card atlas cuts belong to the material/texture set, not the project
    // workspace globally. Kept here so switching materials swaps both texture and cuts.
    public List<UVRectSaveData> uvRects=new();
}

[Serializable]
public class GroupMaterialSaveData
{
    public int groupId;
    public int materialIndex;
}

// Legacy clump payloads remain in the schema so older JSON project files still deserialize cleanly.
// No runtime group-clump system reads or writes these fields anymore.
[Serializable] public class ClumpPointSaveData { public float posX,posY,posZ; public float normalX,normalY,normalZ; public float strength; }
[Serializable] public class ClumpLayerSaveData { public bool enabled; public int pointCount=20; public int generationSeed; public float globalStrength=1f; public float brushRadius=.08f; public float brushStrength=.5f; public float brushFalloff=.5f; public float brushValue=1f; public int debugMode; public float curveEarly=.08f; public float curveMid=.65f; public float curveTip=1f; public List<ClumpPointSaveData> points=new(); }

// Current CLUMPER is a downstream relationship modifier, not baked HairCard state.
[Serializable]
public class GroupClumperSaveData
{
    // Runtime identity is persisted so row ordering remains stable after reload. Older
    // single-clumper project files deserialize this as 0 and receive a fresh unique ID.
    public int id;
    public bool enabled;
    public int mode;
    public float centerX,centerY,centerZ;
    public float normalX,normalY,normalZ;
    public float amount;
    public int count=6;
    public int seed=1;
    // Legacy files that predate these two keys deserialize to the initialiser, so it has to
    // stay in step with GroupClumperManager's creation default - they agreed before, and an
    // old project loading at a different clumper size than a new one is exactly the drift this
    // pairing exists to avoid. Referenced now rather than copied, so it cannot drift again.
    public float radius=GroupClumperManager.DefaultClumperRadius;
    public float falloff=GroupClumperManager.DefaultClumperFalloff;
}

// GUIDE curves. Like CLUMPER this is a recipe, not baked HairCard state: the cards are saved
// in their authored form and the guide is re-applied downstream on load.
[Serializable]
public class GuideCurveSaveData
{
    // Persisted so left-panel row ordering is the same after a reload.
    public int id;

    public float contactX,contactY,contactZ;
    public float normalX,normalY,normalZ;

    // The contact frame is CARRIED at runtime, never re-derived from the normal - see the
    // comment on GuideCurveManager.GuideCurve.frame. Rebuilding it from the normal on load
    // would roll the saved shape about its own axis, so the quaternion travels verbatim.
    // W defaults to 1 so a file missing these keys lands on identity rather than all-zero,
    // which is not a rotation at all.
    public float frameX,frameY,frameZ;
    public float frameW=1f;

    // Mid and end control points, in that frame.
    //
    // These are now a MIRROR of the first and last entries of nodes below, kept because a guide
    // used to be exactly three points and this is what older files carry and older builds read.
    // A file with no nodes list at all is rebuilt from these; a file with a single node borrows
    // the end from them to reach the two node floor; a complete list ignores them.
    public float midX,midY,midZ;
    public float endX,endY,endZ;

    // Every point the curve passes through, root to tip, the contact excluded. Two entries is
    // the original mid-and-end guide; more are the points added with CTRL+SHIFT+click.
    public List<GuideNodeSaveData> nodes=new();

    public float amount;
    public float radius=GuideCurveManager.DefaultGuideRadius;
    public float falloff=GuideCurveManager.DefaultGuideFalloff;

    // Colour, as a hue. Defaulted rather than left at zero, and that matters: JsonUtility leaves
    // a field at its initialiser when the key is absent, so every project saved before guides
    // could be recoloured loads at the original purple. Zero would have loaded them all as RED.
    public float hue=GuideCurveManager.DefaultGuideHue;
}

// One point on a guide, in the guide's own contact frame.
[Serializable]
public class GuideNodeSaveData
{
    public float x,y,z;
}

[Serializable] public class PostAffectorControlSaveData
{
    public float length,width,segments,bend,twist,depth;
    public float x,y,z;
    public float uScale,vScale,uOffset,vOffset;
    // Zero for legacy projects, which is exactly the correct no-curl-delta default.
    public float curlFrequency,curlDiameter;
    public float waveAmplitude,waveFrequency,waveDirection;
    // A POST DELTA, so 0 is correct here - it means 'this POST does not change the arch'.
    public float arch;
}

[Serializable] public class PostAffectorSaveData
{
    public int id;
    public float centerX,centerY,centerZ;
    public float normalX,normalY,normalZ;
    // Same reasoning as GroupClumperSaveData above: these are what a legacy project file
    // without the keys deserializes to, and they used to match the POST creation default
    // exactly. Referenced rather than copied so they cannot fall out of step again.
    public float radius=PostGroupLifetimeAuthority.DefaultPostRadius;
    public float falloff=PostGroupLifetimeAuthority.DefaultPostFalloff;
    public float weight=1f;

    // The user's name for this POST, up to 6 characters. Empty means unnamed, which is what
    // every project written before this field deserializes to, and the row then shows "POST n".
    public string label="";

    // false = RELATIVE, the only behaviour that existed before this field and what every older
    // project deserializes to. true = ABSOLUTE: the POST replaces the base inside its radius
    // rather than offsetting it. No format bump - an absent key reads as false, which is right.
    public bool absolute=false;

    // Each POST owns a private snapshot of the four shape profiles. Empty lists identify a
    // legacy project; PostShapeCurveBridge then copies the restored group curve once so the
    // old project keeps its exact pre-local-curve appearance before the POST diverges.
    public List<GroomCurveKeySaveData> bendCurve=new();
    public List<GroomCurveKeySaveData> xAngleCurve=new();
    public List<GroomCurveKeySaveData> yAngleCurve=new();
    public List<GroomCurveKeySaveData> zAngleCurve=new();

    // Legacy POST clump fields retained only so older project JSON still deserializes.
    public float clumpPoint=.9f;
    public float clumpAmount=0f;
    public float clumpBaseline;
    public float clumpDelta;
    public List<VarianceChannelSaveData> localVariances=new();
    public PostAffectorControlSaveData baseline=new();
    public PostAffectorControlSaveData delta=new();
}

// PRE mode remains group-global, but a POST may locally choose a different predetermined
// rectangle range/seed inside its influence. These records are keyed by the persisted POST id.
[Serializable]
public class PostPredeterminedUVSaveData
{
    public int postId;
    public int minId=1;
    public int maxId=1;
    public int seed;
}

[Serializable] public class GroupSaveData
{
    public int groupId;
    public string groupName;
    public float uScale;
    public float vScale;
    public float uOffset;
    public float vOffset;

    // Group-root 0..1 length profiles for the authored Bend/X/Y/Z angle values.
    // Empty lists mean legacy/default behaviour: Bend=t^2 and X/Y/Z=1 throughout.
    public List<GroomCurveKeySaveData> bendCurve=new();
    public List<GroomCurveKeySaveData> xAngleCurve=new();
    public List<GroomCurveKeySaveData> yAngleCurve=new();
    public List<GroomCurveKeySaveData> zAngleCurve=new();

    // Group-root 0..1 length profiles for the Curl modifier's frequency/diameter magnitudes.
    // Curl has no per-POST override (unlike Bend/X/Y/Z above) - empty lists mean a flat x1
    // multiplier throughout, same default convention as X/Y/Z.
    public List<GroomCurveKeySaveData> curlFrequencyCurve=new();
    public List<GroomCurveKeySaveData> curlDiameterCurve=new();
    // Segment density: where segments cluster along the length. Not a magnitude multiplier
    // like the curves above - a 0..1 -> 0..1 remap (see HairCard.GenerateMesh).
    public List<GroomCurveKeySaveData> segmentDensityCurve=new();
    // Group-root 0..1 width taper. Root-only, like Curl and Segment Density. An empty list -
    // which is what every project saved before this channel existed deserializes to - imports
    // as a flat x1 multiplier, so old files render bit-identically. No migration needed.
    public List<GroomCurveKeySaveData> widthCurve=new();
    // Root-only wave profiles. Empty list -> flat x1, the same convention as every curve above.
    public List<GroomCurveKeySaveData> waveAmplitudeCurve=new();
    public List<GroomCurveKeySaveData> waveFrequencyCurve=new();
    public List<GroomCurveKeySaveData> waveDirectionCurve=new();

    // Rendering only: true culls back faces for this group's cards. Stored as
    // "singleSided" rather than "doubleSided" so a project saved before this existed
    // decodes the missing field to false, which is the historical double-sided look.
    public bool singleSided;

    // Geometry AND shading: true reverses this group's triangle winding and inverts the
    // cross-section ridge. Named for the flipped state so a project saved before this existed
    // decodes the missing field to false, which is the original N+ form.
    public bool normalFlipped;

    // Group UV source. Adjustable keeps the legacy group U/V controls. Predetermined
    // chooses one authored Texture Editor rectangle per card using the inclusive ID range
    // and a deterministic seed.
    public bool usePredeterminedUVs;
    public int uvRectMinId=1;
    public int uvRectMaxId=1;
    public int uvRectSeed;

    // The group's own V flip, XORed on top of each rectangle's UVRectSaveData.flipV.
    //
    // Two levels rather than one because they answer different questions. The rectangle's flag
    // says which way round that STRIP is drawn, and is shared by everything that uses the
    // material. This one says "this group is coming out upside down", which is the answer when
    // the whole sheet is the other way up, or when a group is deliberately combed against the
    // grain - and it must not reach across into other groups sharing the same strips.
    //
    // PREDETERMINED mode only. In ADJUSTABLE the flip is the sign of the group's own V Scale,
    // which has always been there and needs no second store.
    public bool uvFlipV;

    public List<VarianceChannelSaveData> variances=new();
    public List<PostAffectorSaveData> postAffectors=new();
    public List<PostPredeterminedUVSaveData> postPredeterminedUVs=new();

    // Current multi-CLUMPER payload. The single clumper field is retained as a legacy
    // fallback and is populated with the first point when saving for graceful compatibility.
    public List<GroupClumperSaveData> clumpers=new();
    public GroupClumperSaveData clumper;

    // GUIDE curves. A project written before guides existed has no key here at all, so the
    // initialiser leaves an empty list, which reads correctly as "this group has no guides".
    // No formatVersion bump is needed for that.
    public List<GuideCurveSaveData> guides=new();

    // Legacy-only; retained for old JSON compatibility.
    public ClumpLayerSaveData clump;
}

[Serializable]
public class HairProjectSaveData : ISerializationCallbackReceiver
{
    public static HairProjectSaveData PendingModifierRestore;
    public static HairProjectSaveData PendingUVRectRestore;
    public static HairProjectSaveData PendingGroupUVRestore;
    public int formatVersion;

    // The cards each hairCards entry was built from, in the same order, set by whoever gathered
    // the payload. Never serialized: it exists so CanonicalProjectStateBridge.CanonicalizeForSave
    // can pair a saved card back to its source directly instead of hunting for the nearest spawn
    // point, which sorts every card in the group once per card - fine at one call per SAVE PROJ,
    // ruinous for UndoHistoryAuthority, which captures whenever the user pauses. Left null by a
    // payload read from a file, where the pairing genuinely has to be inferred.
    [NonSerialized] public List<HairCard> captureSourceCards;

    public string modelPath;

    // What modelPath was imported as when this project was written. See ImportMetadataSaveData.
    public ImportMetadataSaveData importMetadata=new();

    public List<GroupSaveData> groups=new();
    public List<HairCardSaveData> hairCards=new();
    // Legacy/global mirror kept for backwards compatibility with pre per-material projects.
    public List<UVRectSaveData> uvRects=new();
    public List<HairMaterialSaveData> hairMaterials=new();
    public List<GroupMaterialSaveData> groupMaterials=new();
    public float sliderLength;
    public float sliderWidth;
    public int sliderSegments;
    public float sliderBend;
    public float sliderTwist;
    public float sliderEmbedDepth;
    public float sliderOffsetX;
    public float sliderOffsetY;
    public float sliderOffsetZ;
    public float sliderUScale;
    public float sliderVScale;
    public float sliderUOffset;
    public float sliderVOffset;
    public float sliderCurlFrequency;
    public float sliderCurlDiameter;
    public float sliderWaveAmplitude;
    public float sliderWaveFrequency;
    public float sliderWaveDirection = 1f;
    public float sliderArch = 0.5f;

    // HairCardSection.Profile: 0 TENT, 1 DIAMOND. Absent from a project written before the
    // diamond existed, which decodes to 0 - so every old file opens as the shape it was made
    // with, which is the whole point of defaulting this way round.
    public int cardSectionProfile;

    public void OnBeforeSerialize()
    {
        ModifierPersistenceBridge bridge=UnityEngine.Object.FindFirstObjectByType<ModifierPersistenceBridge>();
        if(bridge!=null&&groups!=null)
            foreach(GroupSaveData group in groups) bridge.PopulateGroupSave(group);

        PostVarianceAffectorBridge postVariance=UnityEngine.Object.FindFirstObjectByType<PostVarianceAffectorBridge>();
        if(postVariance!=null&&groups!=null)
            foreach(GroupSaveData group in groups)
                postVariance.PopulateSave(group.postAffectors);

        PostPredeterminedUVAuthority.Capture(this);
        GroupClumperPersistenceBridge.Capture(this);
        GuideCurvePersistenceBridge.Capture(this);

        TextureUVRectWorkspace uvWorkspace=UnityEngine.Object.FindFirstObjectByType<TextureUVRectWorkspace>();
        if(uvWorkspace!=null)
            uvRects=uvWorkspace.ExportDefinitions();

        GroupPredeterminedUVController groupUV=UnityEngine.Object.FindFirstObjectByType<GroupPredeterminedUVController>();
        if(groupUV!=null&&groups!=null)
            foreach(GroupSaveData group in groups)
                groupUV.PopulateGroupSave(group);

        // The existing curve editor presents a POST's private curves through the group
        // registry while that POST is selected. Swap the actual group root back in only for
        // group serialization, then restore the selected POST immediately afterward.
        // try/finally because the capture swap now silences the curve registry's epoch for its
        // duration. A throw in any of the three Capture calls would otherwise leave it silenced
        // for the rest of the session, and a silenced epoch means curve edits stop reaching the
        // mesh - a far worse failure than the one save that went wrong.
        try
        {
            PostShapeCurveBridge.BeginProjectCapture(this);
            GroomShapeCurveAuthority.Capture(this);
            GroupSidednessAuthority.Capture(this);
            GroupNormalFlipAuthority.Capture(this);
            HairCardSection.Capture(this);
        }
        finally
        {
            PostShapeCurveBridge.EndProjectCapture();
        }
        MaterialProjectPersistenceBridge.Capture(this);
        MaterialUVRectAuthority.Capture(this);
        CanonicalProjectStateBridge.CanonicalizeForSave(this);
    }

    public void OnAfterDeserialize()
    {
        int sourceVersion = formatVersion;

        // v3 changes the native procedural card UV convention from root V=0 / tip V=1
        // to root V=1 / tip V=0. Negating every saved absolute V scale (and POST V delta)
        // preserves the exact visual orientation of older projects under the corrected mesh.
        //
        // Gated on the version that INTRODUCED the change, not on whatever is current. Those were
        // the same number while CurrentFormatVersion was 3, and the next bump would otherwise have
        // re-run this over every v3 file - negating every V scale a second time and turning every
        // card's texture upside down. See CanonicalProjectStateBridge.VConventionFormatVersion.
        if(sourceVersion < CanonicalProjectStateBridge.VConventionFormatVersion)
            MigrateLegacyVConvention();

        // v2 already has the canonical POST save contract. Promote it after the UV-only
        // migration so the normal canonical restore path still runs unchanged.
        if(sourceVersion >= 2 && sourceVersion < CanonicalProjectStateBridge.CurrentFormatVersion)
            formatVersion = CanonicalProjectStateBridge.CurrentFormatVersion;

        // Ahead of every QueueRestore below, and applied on the spot rather than queued: this
        // decides what shape the cards are BUILT as, so it has to land before they exist. It
        // sets a single field and touches nothing in the scene - see HairCardSection.Restore.
        HairCardSection.Restore(this);

        PendingModifierRestore=this;
        PendingUVRectRestore=this;
        PendingGroupUVRestore=this;
        PostPredeterminedUVAuthority.QueueRestore(this);
        GroomShapeCurveAuthority.QueueRestore(this);
        GroupSidednessAuthority.QueueRestore(this);
        GroupNormalFlipAuthority.QueueRestore(this);
        PostShapeCurveBridge.QueueRestore(this);
        MaterialProjectPersistenceBridge.PendingRestore=this;
        MaterialUVRectAuthority.QueueRestore(this);
        GroupClumperPersistenceBridge.QueueRestore(this);
        GuideCurvePersistenceBridge.QueueRestore(this);
        if(sourceVersion>=2)
            CanonicalProjectStateBridge.PendingCanonicalRestore=this;
    }

    void MigrateLegacyVConvention()
    {
        // The legacy loader interpreted a serialized 0 group/card/root V scale as +1.
        // Preserve that old meaning while switching the mesh's native orientation.
        sliderVScale = FlipLegacyAbsoluteV(sliderVScale);

        if(groups != null)
        {
            foreach(GroupSaveData group in groups)
            {
                if(group == null) continue;
                group.vScale = FlipLegacyAbsoluteV(group.vScale);

                if(group.postAffectors == null) continue;
                foreach(PostAffectorSaveData post in group.postAffectors)
                {
                    if(post == null) continue;
                    if(post.baseline != null) post.baseline.vScale = -post.baseline.vScale;
                    if(post.delta != null) post.delta.vScale = -post.delta.vScale;
                }
            }
        }

        if(hairCards != null)
            foreach(HairCardSaveData card in hairCards)
                if(card != null) card.vScale = FlipLegacyAbsoluteV(card.vScale);
    }

    static float FlipLegacyAbsoluteV(float value)
    {
        return Mathf.Approximately(value, 0f) ? -1f : -value;
    }
}
