Shader "YC/Collection/Lit Floor"
{
 Properties {
 _Color ("哑光地板", Color) = (0.16,0.17,0.18,1)
 _Intensity ("地面受光强度", Range(0,2)) = 0.48
 _ShadowSoftness ("投影边缘柔化", Range(0,0.03)) = 0.008
 }
 SubShader {
 Tags { "Queue"="Geometry" "RenderType"="Opaque" }
 Cull Off ZWrite On
 Pass {
 Tags { "LightMode"="ForwardBase" }
 CGPROGRAM
 #pragma vertex vert
 #pragma fragment frag
 #include "UnityCG.cginc"
 float4 vert(float4 vertex:POSITION):SV_POSITION {return UnityObjectToClipPos(vertex);}
 float4 frag():SV_Target {return float4(0,0,0,1);}
 ENDCG
 }
 Pass {
 Tags { "LightMode"="ForwardAdd" }
 Blend One One ZWrite Off
 CGPROGRAM
 #pragma vertex vert
 #pragma fragment frag
 #pragma multi_compile_fwdadd_fullshadows
 #pragma target 3.0
 #include "UnityCG.cginc"
 #include "Lighting.cginc"
 #include "AutoLight.cginc"
 #include "CollectionSpotlight.cginc"
 float4 _Color;
 float _Intensity, _ShadowSoftness;
 struct v2f {float4 pos:SV_POSITION;float3 world:TEXCOORD0;float3 normal:TEXCOORD1;};
 v2f vert(appdata_base v) {v2f o;o.pos=UnityObjectToClipPos(v.vertex);o.world=mul(unity_ObjectToWorld,v.vertex).xyz;o.normal=UnityObjectToWorldNormal(v.normal);return o;}
 float4 frag(v2f i):SV_Target {
 // 只接收房间指定聚光灯，藏品自带点光不会改变房间地面。
 #if !defined(SPOT)
 return 0;
 #else
 if(distance(_WorldSpaceLightPos0.xyz,_SpotPositionRange.xyz)>0.001)return 0;
 float shadow=1;
 #if defined(SHADOWS_DEPTH)
 float4 coord=mul(unity_WorldToShadow[0],float4(i.world,1));
 shadow=0;float weightSum=0;
 [unroll] for(int y=-2;y<=2;y++) {
 [unroll] for(int x=-2;x<=2;x++) {
 float weight=(3-abs(x))*(3-abs(y));float4 tap=coord;
 tap.xy+=float2(x,y)*_ShadowSoftness*coord.w;
 shadow+=UnitySampleShadowmap(tap)*weight;weightSum+=weight;
 }}
 shadow/=weightSum;
 #endif
 float3 toLight=_SpotPositionRange.xyz-i.world;
 float falloff=1/(1+0.12*dot(toLight,toLight));
 float diffuse=saturate(dot(normalize(i.normal),normalize(toLight)));
 float energy=CollectionSpotMask(i.world)*falloff*diffuse*shadow;
 float3 reflectedColor=_Color.rgb*_LightColor0.rgb;
 // 背景采用独立稀有度色，仍使用同一盏中性灯的能量和真实投影。
 if(_UseRarityBackground>0.5)reflectedColor=dot(_Color.rgb,float3(0.2126,0.7152,0.0722))*_SpotColor.rgb*(_KeyLightFactor*4);
 return float4(reflectedColor*_Intensity*energy,0);
 #endif
 }
 ENDCG
 }
 UsePass "Legacy Shaders/VertexLit/SHADOWCASTER"
 }
 Fallback Off
}
