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
// Kept to exactly what the LineRenderers it serves need, and no more:
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
// Deliberately NOT used for the guide curve tube or the influence rings. The curve keeps its
// depth test so you can still read where it passes behind the head, and the influence rings sit
// ON the surface, where drawing through the skull would look like a bug rather than a feature.
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
