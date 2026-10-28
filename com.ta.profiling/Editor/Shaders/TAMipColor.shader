Shader "Hidden/TA/MipColor"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Cull Off
        ZWrite On

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 worldPos : TEXCOORD1;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }

            // mip0 红 → mip1 橙 → mip2 黄 → mip3 绿 → mip4+ 蓝
            fixed4 MipHeat(float mip)
            {
                mip = clamp(mip, 0, 5);
                if (mip < 1) return lerp(fixed4(1,0.1,0.1,1), fixed4(1,0.55,0.1,1), mip);
                if (mip < 2) return lerp(fixed4(1,0.55,0.1,1), fixed4(1,0.9,0.15,1), mip-1);
                if (mip < 3) return lerp(fixed4(1,0.9,0.15,1), fixed4(0.2,0.85,0.3,1), mip-2);
                if (mip < 4) return lerp(fixed4(0.2,0.85,0.3,1), fixed4(0.2,0.55,1,1), mip-3);
                return lerp(fixed4(0.2,0.55,1,1), fixed4(0.35,0.2,0.85,1), saturate(mip-4));
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 dx = ddx(i.uv * _MainTex_TexelSize.zw);
                float2 dy = ddy(i.uv * _MainTex_TexelSize.zw);
                float mip = 0.5 * log2(max(dot(dx,dx), dot(dy,dy)));
                return MipHeat(mip);
            }
            ENDCG
        }
    }
    FallBack Off
}
