// Draws on top of everything, whatever the depth buffer says.
//
// This exists for the GUIDE handle points. They are what you reach for while shaping a guide,
// and the guide runs through the hair it is guiding - so once a group has any density in it the
// points spend most of their time buried behind cards. Hair renders as opaque alpha-cutout at
// queue 2450 and WRITES depth, so every preview in this project, all of which use Sprites/Default
// at queue 3000 with the default LEqual depth test, is hidden by it. Being later in the queue is
// not enough on its own; the depth test is what has to go.
//
// Sprites/Default cannot be told to skip it - it has no _ZTest property and its pass hardcodes
// the default - which is why this is a shader rather than three lines of C#.
//
// Kept to exactly what its clients need, and no more. Those are the LineRenderers below, plus -
// only while GUIDES ON TOP is on - the guide curve's TUBE, which is a MeshRenderer. The tube's
// mesh carries no colour stream at all, so its COLOR attribute defaults to white and its colour
// arrives through _Color instead; both paths work because the fragment multiplies the two.
//
//   Cull Off       - the rings are flat circles seen from either side.
//   ZWrite Off     - nothing behind an overlay should be occluded BY it.
//   ZTest Always   - the point of the file.
//   Queue Overlay  - after the transparents, so a point sits on top of the guide curve as well
//                    as on top of the hair.
//   vertex colour  - LineRenderer carries startColor/endColor as vertex colour, and the handle
//                    colours (tip, middle, inner, hot) are all set that way.
//   _Color         - a plain tint on top, left white by default, so a caller that carries its
//                    colour on the material instead of per vertex still works.
//
// The guide curve tube and the influence rings use this too, but ONLY while GUIDES ON TOP is
// switched on - see GuideOverlayAuthority. Off, which is the default, they keep their depth test,
// because reading where a curve passes behind the head is worth having and the influence rings
// sit ON the surface, where drawing through the skull looks like a bug rather than a feature. On
// a full head the curve is buried in the very hair it steers, which is the case that toggle is
// for.
//
// When they do come in here they are pinned one queue BELOW this one, so the handle points still
// sit on top of the curve as the Queue note above promises. Same depth, same queue and no sort
// between them is not a tie this file can win.
Shader "HairBrush/Overlay"
{
    Properties
    {
        _Color ("Tint", Color) = (1,1,1,1)
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Overlay"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            // No LightMode tag, so URP renders this in its SRPDefaultUnlit pass - the same way
            // it already handles the Sprites/Default material every other preview here uses.
            Cull Off
            ZWrite Off
            ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // Named UnityPerMaterial so the SRP batcher can take this material.
            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 color : COLOR;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert (Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.color = input.color;
                return output;
            }

            half4 frag (Varyings input) : SV_Target
            {
                return half4(input.color * _Color);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
