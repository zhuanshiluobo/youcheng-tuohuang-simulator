Shader "YC/Collection/OrirockMatte"
{
    Properties
    {
        _Color ("材质颜色", Color) = (1,1,1,1)
        _MainTex ("岩质基础色", 2D) = "white" {}
        [Normal] _BumpMap ("断面法线", 2D) = "bump" {}
        _BumpScale ("细节强度", Range(0,1)) = 0.6
        _OcclusionMap ("缝隙遮蔽", 2D) = "white" {}
        _OcclusionStrength ("遮蔽强度", Range(0,1)) = 0.6
        _MetallicGlossMap ("金属度与光滑度", 2D) = "black" {}
        _DirectScale ("主光响应", Range(0,1)) = 0.18
        _FillStrength ("中性漫反射补偿", Range(0,1)) = 0.44
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 200
        CGPROGRAM
        #pragma surface surf RockMatte fullforwardshadows addshadow
        #pragma target 3.0
        #include "UnityCG.cginc"
        sampler2D _MainTex, _BumpMap, _OcclusionMap, _MetallicGlossMap;
        fixed4 _Color;
        half _BumpScale, _OcclusionStrength, _DirectScale, _FillStrength;
        struct Input { float2 uv_MainTex; };

        // 仅此岩石材质限制高强度直射能量，避免灰蓝与黄褐断面被照成纯白。
        // 中性漫反射是插画风格的材质响应，不取稀有度颜色，也不使用自发光。
        half4 LightingRockMatte(SurfaceOutput s, half3 lightDir, half3 viewDir, half atten)
        {
            half diffuse = saturate(dot(s.Normal, lightDir));
            half3 direct = min(_LightColor0.rgb * atten * _DirectScale, half3(0.75,0.75,0.75));
            half3 illumination = direct * diffuse;
            #ifndef UNITY_PASS_FORWARDADD
                illumination += _FillStrength;
            #endif
            half3 halfDir = normalize(lightDir + viewDir);
            half spec = pow(saturate(dot(s.Normal, halfDir)), 12) * s.Specular * 0.018;
            half3 result = s.Albedo * min(illumination, half3(1.08,1.08,1.08));
            result += direct * spec;
            return half4(result, s.Alpha);
        }
        void surf(Input IN, inout SurfaceOutput o)
        {
            fixed4 baseColor = tex2D(_MainTex, IN.uv_MainTex) * _Color;
            half ao = lerp(1, tex2D(_OcclusionMap, IN.uv_MainTex).g, _OcclusionStrength);
            o.Albedo = baseColor.rgb * ao;
            o.Normal = UnpackScaleNormal(tex2D(_BumpMap, IN.uv_MainTex), _BumpScale);
            o.Specular = tex2D(_MetallicGlossMap, IN.uv_MainTex).a;
            o.Gloss = 1;
            o.Alpha = 1;
        }
        ENDCG
    }
    Fallback "Diffuse"
}
