Shader "YC/Originite Glass"
{
    Properties
    {
        [HDR] _Tint ("Glass Tint", Color) = (0.12, 0.025, 0.004, 1)
        [HDR] _EdgeColor ("Edge Color", Color) = (1.2, 0.18, 0.015, 1)
        _Opacity ("Opacity", Range(0, 1)) = 0.42
        _Refraction ("Refraction", Range(0, 0.08)) = 0.025
        _FresnelPower ("Fresnel Power", Range(0.5, 8)) = 3
        _EdgeStrength ("Edge Strength", Range(0, 4)) = 1.2
        _InteriorBrightness ("Interior Brightness", Range(0, 3)) = 1.05
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent+50"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }

        GrabPass
        {
            "_OriginiteGrabTexture"
        }

        Pass
        {
            Cull Back
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float4 grabPosition : TEXCOORD0;
                float3 worldNormal : TEXCOORD1;
                float3 worldViewDirection : TEXCOORD2;
            };

            sampler2D _OriginiteGrabTexture;
            half4 _Tint;
            half4 _EdgeColor;
            half _Opacity;
            half _Refraction;
            half _FresnelPower;
            half _EdgeStrength;
            half _InteriorBrightness;

            v2f vert(appdata input)
            {
                v2f output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.grabPosition = ComputeGrabScreenPos(output.vertex);
                output.worldNormal = UnityObjectToWorldNormal(input.normal);
                output.worldViewDirection = WorldSpaceViewDir(input.vertex);
                return output;
            }

            fixed4 frag(v2f input) : SV_Target
            {
                half3 normal = normalize(input.worldNormal);
                half3 viewDirection = normalize(input.worldViewDirection);
                half fresnel = pow(1.0h - saturate(dot(normal, viewDirection)), _FresnelPower);

                float4 refractedPosition = input.grabPosition;
                refractedPosition.xy += normal.xy * _Refraction * refractedPosition.w;
                half3 background = tex2Dproj(
                    _OriginiteGrabTexture,
                    UNITY_PROJ_COORD(refractedPosition)).rgb;

                half3 absorption = lerp(half3(1.0h, 1.0h, 1.0h), _Tint.rgb, 0.34h);
                half3 transmitted = background * absorption * _InteriorBrightness;
                half3 surface = _Tint.rgb * (0.12h + fresnel * 0.32h);
                half3 edge = _EdgeColor.rgb * fresnel * _EdgeStrength;
                half alpha = saturate(_Opacity + fresnel * 0.28h);
                return fixed4(transmitted + surface + edge, alpha);
            }
            ENDCG
        }
    }
}
