// SPDX-License-Identifier: MIT
Shader "Gaussian Splatting/Render Splats"
{
    Properties
    {
        [HideInInspector] _ZTest ("ZTest", Float) = 4
        [HideInInspector] _FragmentAlphaCutoff ("Fragment Alpha Cutoff", Float) = 0.0039215686
        [HideInInspector] _MinSplatAxisPx ("Min Splat Axis (Pixels)", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }

        Pass
        {
            ZWrite Off
            ZTest [_ZTest]
            Blend OneMinusDstAlpha One
            Cull Off

CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute
#pragma use_dxc

#include "GaussianSplatting.hlsl"

StructuredBuffer<uint> _OrderBuffer;
StructuredBuffer<SplatViewData> _SplatViewData;
ByteAddressBuffer _SplatSelectedBits;

uint _SplatBitsValid;
uint _EyeIndex;
uint _IsStereo;
half _FragmentAlphaCutoff;
float _MinSplatAxisPx;

struct v2f
{
    half4 col : COLOR0;
    float2 pos : TEXCOORD0;
    float4 vertex : SV_POSITION;
};

v2f vert (uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
    v2f o = (v2f)0;
    instID = _OrderBuffer[instID];

    uint viewIndex = _IsStereo != 0 ? instID * 2 + _EyeIndex : instID;
    SplatViewData view = _SplatViewData[viewIndex];
    float4 centerClipPos = view.pos;
    bool behindCam = centerClipPos.w <= 0;

    if (behindCam)
    {
        o.vertex = asfloat(0x7fc00000);
    }
    else
    {
        float minAxisSq = _MinSplatAxisPx * _MinSplatAxisPx;
        if (minAxisSq > 0)
        {
            float axisSq = max(dot(view.axis1, view.axis1), dot(view.axis2, view.axis2));
            if (axisSq < minAxisSq)
            {
                o.vertex = asfloat(0x7fc00000);
                FlipProjectionIfBackbuffer(o.vertex);
                return o;
            }
        }

        o.col.r = f16tof32(view.color.x >> 16);
        o.col.g = f16tof32(view.color.x);
        o.col.b = f16tof32(view.color.y >> 16);
        o.col.a = f16tof32(view.color.y);

        float2 quadPos = float2(vtxID & 1, (vtxID >> 1) & 1) * 2.0 - 1.0;
        quadPos *= 2;
        o.pos = quadPos;

        float2 deltaScreenPos = (quadPos.x * view.axis1 + quadPos.y * view.axis2) * 2 / _ScreenParams.xy;
        o.vertex = centerClipPos;
        o.vertex.xy += deltaScreenPos * centerClipPos.w;

        if (_SplatBitsValid)
        {
            uint wordIdx = instID / 32;
            uint bitIdx = instID & 31;
            uint selVal = _SplatSelectedBits.Load(wordIdx * 4);
            if (selVal & (1 << bitIdx))
                o.col.a = -1;
        }
    }

    FlipProjectionIfBackbuffer(o.vertex);
    return o;
}

half4 frag (v2f i) : SV_Target
{
    float power = -dot(i.pos, i.pos);
    half alpha = exp(power);

    if (i.col.a >= 0)
    {
        alpha = saturate(alpha * i.col.a);
    }
    else
    {
        half3 selectedColor = half3(1,0,1);
        if (alpha > 7.0/255.0)
        {
            if (alpha < 10.0/255.0)
            {
                alpha = 1;
                i.col.rgb = selectedColor;
            }
            alpha = saturate(alpha + 0.3);
        }
        i.col.rgb = lerp(i.col.rgb, selectedColor, 0.5);
    }

    if (alpha < _FragmentAlphaCutoff)
        discard;

    return half4(i.col.rgb * alpha, alpha);
}
ENDCG
        }
    }
}
