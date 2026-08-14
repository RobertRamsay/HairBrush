Shader "Hidden/Hash31Node"
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

			float Hash31( float3 p3 )
			{
				p3 = frac( p3 * 0.1031 );
				p3 += dot( p3, p3.zyx + 33.33 );
				return frac( ( p3.x + p3.y ) * p3.z );
			}

			float4 frag( v2f_img i ) : SV_Target
			{
				float3 p = tex2D( _A, i.uv ).rgb;
				return float4( Hash31( p ), 0, 0, 0 );
			}
			ENDCG
		}
	}
}
