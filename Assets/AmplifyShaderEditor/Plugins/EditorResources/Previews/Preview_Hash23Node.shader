Shader "Hidden/Hash23Node"
{
	Properties
	{
		_A ("_A", 2D) = "black" {}
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

			sampler2D _A;

			float3 Hash23( float2 p )
			{
				float3 p3 = frac( p.xyx * float3( 0.1031, 0.1030, 0.0973 ) );
				p3 += dot( p3, p3.yxz + 33.33 );
				return frac( ( p3.xxy + p3.yzz ) * p3.zyx );
			}

			float4 frag( v2f_img i ) : SV_Target
			{
				float2 p = tex2D( _A, i.uv ).rg;
				return float4( Hash23( p ), 0 );
			}
			ENDCG
		}
	}
}
