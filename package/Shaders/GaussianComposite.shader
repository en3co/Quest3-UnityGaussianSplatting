// SPDX-License-Identifier: MIT
Shader "Hidden/Gaussian Splatting/Composite"
{
    SubShader
    {
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha

            Stencil
            {
                Ref 0
                Comp Equal
            }

CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute
#pragma require 2darray
#pragma use_dxc
#pragma shader_feature_local _ GAUSSIAN_SPLAT_ARRAY_RT

#include "UnityCG.cginc"

struct v2f
{
    float4 vertex : SV_POSITION;
};

v2f vert (uint vtxID : SV_VertexID)
{
    v2f o;
    float2 quadPos = float2(vtxID & 1, (vtxID >> 1) & 1) * 4.0 - 1.0;
    o.vertex = float4(quadPos, 1, 1);
    return o;
}

#if defined(GAUSSIAN_SPLAT_ARRAY_RT)
UNITY_DECLARE_TEX2DARRAY(_GaussianSplatRT);
#else
Texture2D _GaussianSplatRT;
#endif

int _CustomStereoEyeIndex;

half4 frag (v2f i) : SV_Target
{
    half4 col;

    #if defined(GAUSSIAN_SPLAT_ARRAY_RT)
        float2 uv = saturate((i.vertex.xy + 0.5) / _ScreenParams.xy);
        col = UNITY_SAMPLE_TEX2DARRAY(_GaussianSplatRT, float3(uv, _CustomStereoEyeIndex));
    #else
        col = _GaussianSplatRT.Load(int3(i.vertex.xy, 0));
    #endif

    if (col.a <= 1.0 / 255.0)
        discard;

    return half4(GammaToLinearSpace(col.rgb / col.a), col.a);
}
ENDCG
        }
    }
}
