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
[Serializable] public class ClumpPointSaveData { public float posX,posY,posZ; public float normalX,normalY,normalZ; public float strength; }
[Serializable] public class ClumpLayerSaveData { public bool enabled; public int pointCount=20; public int generationSeed; public float globalStrength=1f; public float brushRadius=.08f; public float brushStrength=.5f; public float brushFalloff=.5f; public float brushValue=1f; public int debugMode; public float curveEarly=.08f; public float curveMid=.65f; public float curveTip=1f; public List<ClumpPointSaveData> points=new(); }

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
    public float radius=.02f;
    public float falloff=.03f;
    public float weight=1f;
    public float clumpBaseline;
    public float clumpDelta;
    public List<VarianceChannelSaveData> localVariances=new();
    public PostAffectorControlSaveData baseline=new();
    public PostAffectorControlSaveData delta=new();
}

[Serializable] public class GroupSaveData
{
    public int groupId;
    public string groupName;
    public float uScale;
    public float vScale;
    public float uOffset;
    public float vOffset;
    public List<VarianceChannelSaveData> variances=new();
    public List<PostAffectorSaveData> postAffectors=new();
    public ClumpLayerSaveData clump;
}

[Serializable]
public class HairProjectSaveData : ISerializationCallbackReceiver
{
    public static HairProjectSaveData PendingModifierRestore;
    public string modelPath;
    public List<GroupSaveData> groups=new();
    public List<HairCardSaveData> hairCards=new();
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
        if(bridge==null||groups==null)return;
        foreach(GroupSaveData group in groups) bridge.PopulateGroupSave(group);

        PostClumpAffectorBridge postClump=UnityEngine.Object.FindFirstObjectByType<PostClumpAffectorBridge>();
        if(postClump!=null)
            foreach(GroupSaveData group in groups)
                postClump.PopulateSave(group.postAffectors);

        PostVarianceAffectorBridge postVariance=UnityEngine.Object.FindFirstObjectByType<PostVarianceAffectorBridge>();
        if(postVariance!=null)
            foreach(GroupSaveData group in groups)
                postVariance.PopulateSave(group.postAffectors);
    }

    public void OnAfterDeserialize()
    {
        PendingModifierRestore=this;
    }
}
