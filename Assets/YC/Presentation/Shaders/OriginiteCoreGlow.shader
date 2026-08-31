Shader "YC/Originite Core Glow"
{
    Properties
    {
        [HDR] _Color ("Glow Color", Color) = (1, 0.12, 0.01, 1)
        _Intensity ("Intensity", Range(0, 4)) = 1.2
        _FresnelPower ("Fresnel Power", Range(0.5, 8)) = 2
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent+25"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Cull Off
            ZWrite Off
            Blend One One

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float3 worldNormal : TEXCOORD0;
                float3 worldViewDirection : TEXCOORD1;
            };

            half4 _Color;
            half _Intensity;
            half _FresnelPower;

            v2f vert(appdata input)
            {
                v2f output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.worldNormal = UnityObjectToWorldNormal(input.normal);
                output.worldViewDirection = WorldSpaceViewDir(input.vertex);
                return output;
            }

            fixed4 frag(v2f input) : SV_Target
            {
                half3 normal = normalize(input.worldNormal);
                half3 viewDirection = normalize(input.worldViewDirection);
                half fresnel = pow(1.0h - saturate(dot(normal, viewDirection)), _FresnelPower);
                half glow = (0.12h + fresnel * 0.88h) * _Intensity;
                return fixed4(_Color.rgb * glow, 1.0h);
            }
            ENDCG
        }
    }
}
