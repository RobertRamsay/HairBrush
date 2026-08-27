using System.Collections.Generic;
using UnityEngine;

// Moving a whole groom from one place to another.
//
// Every placement in HairBrush is a world-space point plus a world-space normal, and nothing is
// parented to the model - see HairCard.spawnHitPoint / GuideCurve.contact / GroupClumper.center /
// PostAffector.center. So "rescale this project" and "put this groom on a different head" are the
// same operation with a different mapping: walk every anchor, move it, and scale everything that
// is measured in world distance so the modifiers keep the reach they were authored with.
//
// The mapping is the extension point. UniformScaleAnchorMapping below is the whole of the import
// rescale; a landmark warp for REMAP is another subclass and needs no changes here.
//
// This file only ever transforms a HairProjectSaveData. That is deliberate: a payload is plain
// data with no evaluation order, no cached meshes and no authorities polling it, so a migration
// applied here lands before a single card exists. A scene-side applier is a separate job with a
// separate set of hazards and does not belong in the same pass.
public abstract class GroomAnchorMapping
{
    public abstract Vector3 MapPoint(Vector3 worldPoint);

    public abstract Vector3 MapNormal(Vector3 worldPoint, Vector3 worldNormal);

    // How much a world distance at this point is stretched by the mapping. Radii, falloffs,
    // lengths and depths are all raw world distances compared against Vector3.Distance, so a
    // mapping that changes scale without this would quietly change every modifier's reach.
    public abstract float LocalScale(Vector3 worldPoint);
}

// The import-rescale mapping: one uniform factor about the origin.
//
// About the ORIGIN, not about the model's centre, because that is where the model actually sits.
// CustomOBJImporter recentres the mesh on its own bounds and ModelViewer.LoadModel forces the
// root to Vector3.zero, so a card's world anchor is already relative to the model origin and a
// plain multiply is the correct rescale.
public class UniformScaleAnchorMapping : GroomAnchorMapping
{
    private float factor = 1f;

    public UniformScaleAnchorMapping(float scaleFactor)
    {
        factor = scaleFactor;
    }

    public override Vector3 MapPoint(Vector3 worldPoint)
    {
        return worldPoint * factor;
    }

    public override Vector3 MapNormal(Vector3 worldPoint, Vector3 worldNormal)
    {
        // A uniform scale does not rotate anything, so the normal is carried unchanged. It is
        // still normalised on the way out: the saved value may have drifted, and every consumer
        // treats it as a unit vector.
        if (worldNormal.sqrMagnitude < .000001f)
        {
            return Vector3.up;
        }
        return worldNormal.normalized;
    }

    public override float LocalScale(Vector3 worldPoint)
    {
        return factor;
    }
}

// What a transform pass was asked to do, and what it touched. Returned so a caller can log it
// or put it in front of the user rather than guessing.
public class GroomAnchorTransformReport
{
    public int cards;
    public int guides;
    public int guideNodes;
    public int clumpers;
    public int postAffectors;
    public bool identityFrozen;
    public bool dimensionsScaled;

    public override string ToString()
    {
        return "cards " + cards + ", guides " + guides + " (" + guideNodes + " nodes), clumpers "
            + clumpers + ", posts " + postAffectors + ", identity frozen " + identityFrozen
            + ", dimensions scaled " + dimensionsScaled;
    }
}

public static class GroomAnchorTransform
{
    // Apply a mapping to every anchor in a payload.
    //
    // freezeIdentity is the one that matters and it is on by default at every call site here.
    // Four separate hash sites key a card's randomisation to its spawn point - variance
    // (GroomVarianceController.SignedRandom), POST-local variance
    // (PostVarianceAffectorBridge.SignedRandom), the POST coverage threshold and both
    // predetermined-UV card hashes - and two of those mix the surface NORMAL in as well. Every
    // one of them rounds to a ten-thousandth, so moving a root by a tenth of a millimetre
    // re-rolls that card's variance and its predetermined rectangle. Stamping the pre-transform
    // point and normal into the identity fields is what stops a rescale or a remap scrambling a
    // groom's randomisation, and it is why HairCardSaveData carries them at all.
    //
    // scaleDimensions covers the values that are lengths rather than positions: card length and
    // width, embed depth, curl diameter, wave amplitude, and every radius and falloff. Correct
    // for a rescale, where the whole point is that nothing changes visually. A landmark warp will
    // want radii scaled and card dimensions left alone, which is why they are one flag each.
    public static GroomAnchorTransformReport ApplyToSaveData(
        HairProjectSaveData data,
        GroomAnchorMapping mapping,
        bool freezeIdentity,
        bool scaleDimensions)
    {
        GroomAnchorTransformReport report = new GroomAnchorTransformReport();
        report.identityFrozen = freezeIdentity;
        report.dimensionsScaled = scaleDimensions;

        if (data == null || mapping == null)
        {
            return report;
        }

        TransformCards(data, mapping, freezeIdentity, scaleDimensions, report);
        TransformGlobalSliders(data, mapping, scaleDimensions);
        TransformGroups(data, mapping, scaleDimensions, report);
        return report;
    }

    static void TransformCards(
        HairProjectSaveData data,
        GroomAnchorMapping mapping,
        bool freezeIdentity,
        bool scaleDimensions,
        GroomAnchorTransformReport report)
    {
        if (data.hairCards == null)
        {
            return;
        }

        foreach (HairCardSaveData card in data.hairCards)
        {
            if (card == null)
            {
                continue;
            }

            Vector3 hit = new Vector3(card.hitX, card.hitY, card.hitZ);
            Vector3 normal = new Vector3(card.normalX, card.normalY, card.normalZ);

            // Stamped BEFORE the anchor moves, and only when the card is not already carrying an
            // identity from an earlier pass. A groom that has been rescaled and then remapped
            // must still hash to the values it was first authored at, or the second pass
            // re-rolls everything the first pass protected.
            if (freezeIdentity && !card.hasIdentity)
            {
                card.hasIdentity = true;
                card.identityX = hit.x;
                card.identityY = hit.y;
                card.identityZ = hit.z;
                card.identityNX = normal.x;
                card.identityNY = normal.y;
                card.identityNZ = normal.z;
            }

            float scale = mapping.LocalScale(hit);
            Vector3 movedHit = mapping.MapPoint(hit);
            Vector3 movedNormal = mapping.MapNormal(hit, normal);

            card.hitX = movedHit.x;
            card.hitY = movedHit.y;
            card.hitZ = movedHit.z;
            card.normalX = movedNormal.x;
            card.normalY = movedNormal.y;
            card.normalZ = movedNormal.z;

            // posX/Y/Z and rotX/Y/Z/W are derived - SpawnSavedCards rebuilds both from the hit,
            // the normal and the embed depth through HairCard.UpdateTransformOrientation, and
            // never reads them. Moved anyway so a payload inspected on disk is not
            // self-contradictory, and so anything that starts trusting them later is not wrong.
            Vector3 position = new Vector3(card.posX, card.posY, card.posZ);
            Vector3 movedPosition = mapping.MapPoint(position);
            card.posX = movedPosition.x;
            card.posY = movedPosition.y;
            card.posZ = movedPosition.z;

            if (scaleDimensions)
            {
                card.length = card.length * scale;
                card.width = card.width * scale;
                card.embedDepth = card.embedDepth * scale;
                // Curl DIAMETER is a length-scale magnitude and curl FREQUENCY is a turn count -
                // see GroomVarianceController.FormatVariance, which formats the first as a plain
                // magnitude and neither as an angle. Same split for wave amplitude against wave
                // frequency and direction. Only the lengths move.
                card.curlDiameter = card.curlDiameter * scale;
                card.waveAmplitude = card.waveAmplitude * scale;

                // Accumulated, not assigned: a groom that has been rescaled twice has had both
                // factors applied to its lengths, and ClumperDeterministicLeaderAuthority divides
                // by the product to recover the values its key was first built from.
                card.identityScale = card.identityScale * scale;
            }

            // offsetX/Y/Z are Euler angles, not translations (HairCard.MirroredEuler consumes
            // them as one), and bendAngle/twistAngle/arch/flattenFactor and the UV block are all
            // dimensionless. None of them scale.

            report.cards++;
        }
    }

    static void TransformGlobalSliders(HairProjectSaveData data, GroomAnchorMapping mapping, bool scaleDimensions)
    {
        if (!scaleDimensions)
        {
            return;
        }

        // The slider block is what the next placed card inherits, so it has to move with
        // everything else or the first card placed after a migration comes out at the old scale.
        float scale = mapping.LocalScale(Vector3.zero);
        data.sliderLength = data.sliderLength * scale;
        data.sliderWidth = data.sliderWidth * scale;
        data.sliderEmbedDepth = data.sliderEmbedDepth * scale;
        data.sliderCurlDiameter = data.sliderCurlDiameter * scale;
        data.sliderWaveAmplitude = data.sliderWaveAmplitude * scale;
    }

    static void TransformGroups(
        HairProjectSaveData data,
        GroomAnchorMapping mapping,
        bool scaleDimensions,
        GroomAnchorTransformReport report)
    {
        if (data.groups == null)
        {
            return;
        }

        foreach (GroupSaveData group in data.groups)
        {
            if (group == null)
            {
                continue;
            }

            TransformClumpers(group, mapping, scaleDimensions, report);
            TransformGuides(group, mapping, scaleDimensions, report);
            TransformPostAffectors(group, mapping, scaleDimensions, report);
        }
    }

    static void TransformClumpers(
        GroupSaveData group,
        GroomAnchorMapping mapping,
        bool scaleDimensions,
        GroomAnchorTransformReport report)
    {
        List<GroupClumperSaveData> clumpers = new List<GroupClumperSaveData>();
        if (group.clumpers != null)
        {
            clumpers.AddRange(group.clumpers);
        }
        // The single legacy clumper is populated with the first entry when saving and is what an
        // older build reads back. Left behind, a migrated project would open correctly here and
        // wrongly there.
        if (group.clumper != null)
        {
            clumpers.Add(group.clumper);
        }

        foreach (GroupClumperSaveData clumper in clumpers)
        {
            if (clumper == null)
            {
                continue;
            }

            Vector3 center = new Vector3(clumper.centerX, clumper.centerY, clumper.centerZ);
            Vector3 normal = new Vector3(clumper.normalX, clumper.normalY, clumper.normalZ);
            float scale = mapping.LocalScale(center);
            Vector3 movedCenter = mapping.MapPoint(center);
            Vector3 movedNormal = mapping.MapNormal(center, normal);

            clumper.centerX = movedCenter.x;
            clumper.centerY = movedCenter.y;
            clumper.centerZ = movedCenter.z;
            clumper.normalX = movedNormal.x;
            clumper.normalY = movedNormal.y;
            clumper.normalZ = movedNormal.z;

            if (scaleDimensions)
            {
                clumper.radius = clumper.radius * scale;
                clumper.falloff = clumper.falloff * scale;
            }

            report.clumpers++;
        }
    }

    static void TransformGuides(
        GroupSaveData group,
        GroomAnchorMapping mapping,
        bool scaleDimensions,
        GroomAnchorTransformReport report)
    {
        if (group.guides == null)
        {
            return;
        }

        foreach (GuideCurveSaveData guide in group.guides)
        {
            if (guide == null)
            {
                continue;
            }

            Vector3 contact = new Vector3(guide.contactX, guide.contactY, guide.contactZ);
            Vector3 normal = new Vector3(guide.normalX, guide.normalY, guide.normalZ);
            float scale = mapping.LocalScale(contact);
            Vector3 movedContact = mapping.MapPoint(contact);
            Vector3 movedNormal = mapping.MapNormal(contact, normal);

            guide.contactX = movedContact.x;
            guide.contactY = movedContact.y;
            guide.contactZ = movedContact.z;
            guide.normalX = movedNormal.x;
            guide.normalY = movedNormal.y;
            guide.normalZ = movedNormal.z;

            // frameX/Y/Z/W is NOT touched. The contact frame is carried verbatim on purpose -
            // rebuilding or reorienting it rolls the saved shape about its own axis, which is the
            // whole reason GuideCurveSaveData stores a quaternion instead of deriving one. A
            // uniform scale does not rotate it; a mapping that DOES rotate the normal will need
            // the frame transported with GuideCurveManager.TransportFrame, which is a scene-side
            // operation and does not belong here.

            // Nodes are offsets in that frame, so they are lengths, not positions: scaled, never
            // mapped. Running them through MapPoint would treat a local offset as a world point.
            if (scaleDimensions)
            {
                if (guide.nodes != null)
                {
                    foreach (GuideNodeSaveData node in guide.nodes)
                    {
                        if (node == null)
                        {
                            continue;
                        }
                        node.x = node.x * scale;
                        node.y = node.y * scale;
                        node.z = node.z * scale;
                        report.guideNodes++;
                    }
                }

                // The legacy mid/end pair mirrors the first and last nodes and is what rebuilds a
                // guide read by an older build, or by this one from a file with no nodes list.
                guide.midX = guide.midX * scale;
                guide.midY = guide.midY * scale;
                guide.midZ = guide.midZ * scale;
                guide.endX = guide.endX * scale;
                guide.endY = guide.endY * scale;
                guide.endZ = guide.endZ * scale;

                guide.radius = guide.radius * scale;
                guide.falloff = guide.falloff * scale;
            }

            report.guides++;
        }
    }

    static void TransformPostAffectors(
        GroupSaveData group,
        GroomAnchorMapping mapping,
        bool scaleDimensions,
        GroomAnchorTransformReport report)
    {
        if (group.postAffectors == null)
        {
            return;
        }

        foreach (PostAffectorSaveData post in group.postAffectors)
        {
            if (post == null)
            {
                continue;
            }

            Vector3 center = new Vector3(post.centerX, post.centerY, post.centerZ);
            Vector3 normal = new Vector3(post.normalX, post.normalY, post.normalZ);
            float scale = mapping.LocalScale(center);
            Vector3 movedCenter = mapping.MapPoint(center);
            Vector3 movedNormal = mapping.MapNormal(center, normal);

            post.centerX = movedCenter.x;
            post.centerY = movedCenter.y;
            post.centerZ = movedCenter.z;
            post.normalX = movedNormal.x;
            post.normalY = movedNormal.y;
            post.normalZ = movedNormal.z;

            if (scaleDimensions)
            {
                post.radius = post.radius * scale;
                post.falloff = post.falloff * scale;
                ScaleControl(post.baseline, scale);
                ScaleControl(post.delta, scale);
            }

            report.postAffectors++;
        }
    }

    // A POST baseline is an absolute control state and a POST delta is a difference between two
    // of them. Both are in the same units, so both scale by the same factor - and x/y/z are the
    // Euler angle triple here exactly as they are on a card, so they stay put.
    static void ScaleControl(PostAffectorControlSaveData control, float scale)
    {
        if (control == null)
        {
            return;
        }
        control.length = control.length * scale;
        control.width = control.width * scale;
        control.depth = control.depth * scale;
        control.curlDiameter = control.curlDiameter * scale;
        control.waveAmplitude = control.waveAmplitude * scale;
    }
}
