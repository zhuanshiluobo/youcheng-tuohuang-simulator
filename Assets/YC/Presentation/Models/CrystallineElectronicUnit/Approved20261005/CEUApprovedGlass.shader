Shader "YC/Collection/CEUApprovedGlass"
{
    Properties
    {
        _Color ("基色与透明度", Color) = (1,1,1,0.35)
        _EdgeOpacity ("边缘不透明度", Range(0,1)) = 0.5
        _FresnelPower ("边缘反射过渡", Range(1,8)) = 4
        [NoScaleOffset] _SurfaceMap ("表面数据（R 金属 / G 遮蔽 / A 光滑）", 2D) = "white" {}
        _OcclusionStrength ("环境遮蔽强度", Range(0,1)) = 0.6
        [HDR] _EmissionColor ("自发光颜色", Color) = (0,0,0,1)
        _LightResponse ("直接光响应", Range(0,1)) = 0.35
        _EnvironmentResponse ("环境反射响应", Range(0,1)) = 0.7
        _HighlightCompression ("高光压缩", Range(0,1)) = 1
        [ToggleOff] _SpecularHighlights ("镜面高光", Float) = 1
        [ToggleOff] _GlossyReflections ("光泽反射", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "IgnoreProjector"="True" }
        ZWrite Off
        LOD 300
        CGPROGRAM
        #pragma target 3.0
        #pragma surface surf ExhibitPBR fullforwardshadows alpha:fade finalcolor:FinishExhibit exclude_path:deferred exclude_path:prepass
        #pragma shader_feature_local _EMISSION
        #pragma shader_feature_local _SPECULARHIGHLIGHTS_OFF
        #pragma shader_feature_local _GLOSSYREFLECTIONS_OFF
        #include "UnityPBSLighting.cginc"

        half4 _Color;
        half4 _EmissionColor;
        sampler2D _SurfaceMap;
        half _OcclusionStrength;
        half _EdgeOpacity, _FresnelPower;
        half _LightResponse, _EnvironmentResponse, _HighlightCompression;

        struct Input { float3 worldPos; float3 viewDir; float2 uv_SurfaceMap; };

        inline half4 LightingExhibitPBR(SurfaceOutputStandard s, half3 viewDir, UnityGI gi)
        {
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
            // 图集 Alpha 存光滑度；透明度单独来自颜色与 Fresnel 边缘参数。
            half4 surface = tex2D(_SurfaceMap, IN.uv_SurfaceMap);
            o.Albedo = _Color.rgb;
            o.Metallic = surface.r;
            o.Smoothness = surface.a;
            o.Occlusion = lerp(1, surface.g, _OcclusionStrength);
            half edge = pow(1 - saturate(dot(normalize(IN.viewDir), float3(0,0,1))), _FresnelPower);
            o.Alpha = lerp(_Color.a, _EdgeOpacity, edge);
            #if defined(_EMISSION)
                o.Emission = _EmissionColor.rgb;
            #endif
        }

        void FinishExhibit(Input IN, SurfaceOutputStandard o, inout half4 color)
        {
            #if !defined(UNITY_PASS_FORWARDADD)
                // 与不透明展品同一亮度肩部；保留色相和独立透明度。
                float3 rgb = max((float3)color.rgb, 0);
                float peak = max(rgb.r, max(rgb.g, rgb.b));
                float excess = max(peak - 0.6, 0);
                float compressed = 0.6 + excess / (1 + excess / 0.4);
                float boundedPeak = peak <= 0.6 ? peak : compressed;
                color.rgb = rgb * lerp(1, boundedPeak / max(peak, 0.00001), _HighlightCompression);
            #endif
            // 附加灯由生成的 alpha:fade pass 线性混合，不重复高光压缩。
        }
        ENDCG
    }
    FallBack Off
}
