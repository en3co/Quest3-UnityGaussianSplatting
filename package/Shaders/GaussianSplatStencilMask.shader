// Claude:
Shader "Hidden/Gaussian Splatting/StencilMask"
{
    Properties
    {
        [HideInInspector] _ZTest ("ZTest", Float) = 4
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            ZWrite Off
            ZTest [_ZTest]
            ColorMask 0
            Cull Off

            Stencil
            {
                Ref 1
                Comp Always
                Pass Replace
            }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma use_dxc
            #include "UnityCG.cginc"

            float4 vert(float4 v : POSITION) : SV_POSITION
            {
                return UnityObjectToClipPos(v);
            }

            fixed4 frag() : SV_Target { return 0; }
            ENDCG
        }
    }
}