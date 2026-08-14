Shader "Hidden/MaterialQualityNode"
{
	Properties
	{
		_A ("_A", 2D) = "white" {}
		_B ("_B", 2D) = "white" {}
		_C ("_C", 2D) = "white" {}
	}
	SubShader
	{
		Pass
		{
			CGPROGRAM
			#include "UnityCG.cginc"
			#include "Preview.cginc"
			#pragma vertex vert_img
			#pragma fragment frag
			#pragma multi_compile MATERIAL_QUALITY_HIGH MATERIAL_QUALITY_MEDIUM MATERIAL_QUALITY_LOW

			sampler2D _A;
			sampler2D _B;
			sampler2D _C;

			float4 frag( v2f_img i ) : SV_Target
			{
				#if defined( MATERIAL_QUALITY_HIGH )
					return tex2D( _A, i.uv );
				#elif defined( MATERIAL_QUALITY_MEDIUM )
					return tex2D( _B, i.uv );
				#else
					return tex2D( _C, i.uv );
				#endif
			}
			ENDCG
		}
	}
}
