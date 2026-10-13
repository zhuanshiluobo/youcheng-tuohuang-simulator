Shader "YC/Collection/ExhibitPBR"
{
    Properties
    {
        _Color ("基色", Color) = (1,1,1,1)
        _Metallic ("金属度", Range(0,1)) = 0
        _Glossiness ("光滑度", Range(0,1)) = 0.5
        [HDR] _EmissionColor ("自发光颜色", Color) = (0,0,0,1)
        _LightResponse ("直接光响应", Range(0,1)) = 0.35
        _EnvironmentResponse ("环境反射响应", Range(0,1)) = 0.7
        _HighlightCompression ("高光压缩", Range(0,1)) = 1
        [ToggleOff] _SpecularHighlights ("镜面高光", Float) = 1
        [ToggleOff] _GlossyReflections ("光泽反射", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 300
        CGPROGRAM
        #pragma target 3.0
        #pragma surface surf ExhibitPBR fullforwardshadows addshadow finalcolor:FinishExhibit exclude_path:deferred exclude_path:prepass
        #pragma shader_feature_local _EMISSION
        #pragma shader_feature_local _SPECULARHIGHLIGHTS_OFF
        #pragma shader_feature_local _GLOSSYREFLECTIONS_OFF
        #include "UnityPBSLighting.cginc"

        half4 _Color;
        half4 _EmissionColor;
        half _Metallic, _Glossiness;
        half _LightResponse, _EnvironmentResponse, _HighlightCompression;

        struct Input { float3 worldPos; };

        inline half4 LightingExhibitPBR(SurfaceOutputStandard s, half3 viewDir, UnityGI gi)
        {
            // 保留 Standard 的 BRDF、几何法线和阴影衰减，只调整展品入射能量。
            gi.light.color *= _LightResponse;
            return LightingStandard(s, viewDir, gi);
        }

        inline void LightingExhibitPBR_GI(SurfaceOutputStandard s, UnityGIInput data, inout UnityGI gi)
        {
            LightingStandard_GI(s, data, gi);
            gi.indirect.diffuse *= _EnvironmentResponse;
            gi.indirect.specular *= _EnvironmentResponse;
        }

        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            o.Albedo = _Color.rgb;
            o.Metallic = _Metallic;
            o.Smoothness = _Glossiness;
            o.Occlusion = 1;
            o.Alpha = 1;
            #if defined(_EMISSION)
                o.Emission = _EmissionColor.rgb;
            #endif
        }

        void FinishExhibit(Input IN, SurfaceOutputStandard o, inout half4 color)
        {
            #if !defined(UNITY_PASS_FORWARDADD)
                // 基础 pass 的亮度肩部保留色相；低于 0.6 的亮度不改动。
                float3 rgb = max((float3)color.rgb, 0);
                float peak = max(rgb.r, max(rgb.g, rgb.b));
                float excess = max(peak - 0.6, 0);
                float compressed = 0.6 + excess / (1 + excess / 0.4);
                float boundedPeak = peak <= 0.6 ? peak : compressed;
                color.rgb = rgb * lerp(1, boundedPeak / max(peak, 0.00001), _HighlightCompression);
            #endif
            // ForwardAdd 保持线性相加，不对各附加灯重复 tone map。
        }
        ENDCG
    }
    FallBack "Standard"
}
