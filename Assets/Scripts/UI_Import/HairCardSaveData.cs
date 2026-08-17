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
}

[Serializable]
public class HairMaterialSaveData
{
    public string name;
    public string albedoPath;
    public string normalPath;
    public string opacityPath;
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
    public float radius=.05f;
    public float falloff=.05f;
}

[Serializable] public class PostAffectorControlSaveData
{
    public float length,width,segments,bend,twist,depth;
    public float x,y,z;
    public float uScale,vScale,uOffset,vOffset;
}

[Serializable] public class PostAffectorSaveData
{
    public int id;
    public float centerX,centerY,centerZ;
    public float normalX,normalY,normalZ;
    public float radius=.03f;
    public float falloff=.05f;
    public float weight=1f;

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

    // Group UV source. Adjustable keeps the legacy group U/V controls. Predetermined
    // chooses one authored Texture Editor rectangle per card using the inclusive ID range
    // and a deterministic seed.
    public bool usePredeterminedUVs;
    public int uvRectMinId=1;
    public int uvRectMaxId=1;
    public int uvRectSeed;

    public List<VarianceChannelSaveData> variances=new();
    public List<PostAffectorSaveData> postAffectors=new();
    public List<PostPredeterminedUVSaveData> postPredeterminedUVs=new();

    // Current multi-CLUMPER payload. The single clumper field is retained as a legacy
    // fallback and is populated with the first point when saving for graceful compatibility.
    public List<GroupClumperSaveData> clumpers=new();
    public GroupClumperSaveData clumper;

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
    public string modelPath;
    public List<GroupSaveData> groups=new();
    public List<HairCardSaveData> hairCards=new();
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
        PostShapeCurveBridge.BeginProjectCapture(this);
        GroomShapeCurveAuthority.Capture(this);
        PostShapeCurveBridge.EndProjectCapture();
        MaterialProjectPersistenceBridge.Capture(this);
        CanonicalProjectStateBridge.CanonicalizeForSave(this);
    }

    public void OnAfterDeserialize()
    {
        int sourceVersion = formatVersion;

        // v3 changes the native procedural card UV convention from root V=0 / tip V=1
        // to root V=1 / tip V=0. Negating every saved absolute V scale (and POST V delta)
        // preserves the exact visual orientation of older projects under the corrected mesh.
        if(sourceVersion < CanonicalProjectStateBridge.CurrentFormatVersion)
            MigrateLegacyVConvention();

        // v2 already has the canonical POST save contract. Promote it after the UV-only
        // migration so the normal canonical restore path still runs unchanged.
        if(sourceVersion >= 2 && sourceVersion < CanonicalProjectStateBridge.CurrentFormatVersion)
            formatVersion = CanonicalProjectStateBridge.CurrentFormatVersion;

        PendingModifierRestore=this;
        PendingUVRectRestore=this;
        PendingGroupUVRestore=this;
        PostPredeterminedUVAuthority.QueueRestore(this);
        GroomShapeCurveAuthority.QueueRestore(this);
        PostShapeCurveBridge.QueueRestore(this);
        MaterialProjectPersistenceBridge.PendingRestore=this;
        GroupClumperPersistenceBridge.QueueRestore(this);
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
