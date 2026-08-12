using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public class HairCardSaveData
{
    public float posX, posY, posZ;
    public float rotX, rotY, rotZ, rotW;
    public float length;
    public float width;
    public int segments;
    public float bendAngle;
    public float twistAngle;
    public float flattenFactor;
    public float embedDepth;
    public float offsetX, offsetY, offsetZ;
    public float uScale;
    public float vScale;
    public float uOffset;
    public float vOffset;
    public int groupId;
}

[Serializable]
public class VarianceChannelSaveData
{
    public string channel;
    public float amount;
    public int seed;
}

[Serializable]
public class ClumpPointSaveData
{
    public float posX, posY, posZ;
    public float normalX, normalY, normalZ;
    public float strength;
}

[Serializable]
public class ClumpLayerSaveData
{
    public bool enabled;
    public int pointCount = 20;
    public int generationSeed;
    public float globalStrength = 1f;
    public float brushRadius = 0.08f;
    public float brushStrength = 0.5f;
    public float brushFalloff = 0.5f;
    public float brushValue = 1f;
    public int debugMode;
    public float curveEarly = 0.08f;
    public float curveMid = 0.65f;
    public float curveTip = 1f;
    public List<ClumpPointSaveData> points = new List<ClumpPointSaveData>();
}

[Serializable]
public class GroupSaveData
{
    public int groupId;
    public string groupName;
    public float uScale;
    public float vScale;
    public float uOffset;
    public float vOffset;
    public List<VarianceChannelSaveData> variances = new List<VarianceChannelSaveData>();
    public ClumpLayerSaveData clump;
}

[Serializable]
public class HairProjectSaveData
{
    public string modelPath;
    public List<GroupSaveData> groups = new List<GroupSaveData>();
    public List<HairCardSaveData> hairCards = new List<HairCardSaveData>();
    
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
}