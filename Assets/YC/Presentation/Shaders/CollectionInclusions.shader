Shader "YC/Collection/Crystal Inclusions"
{
 Properties {
 _Color ("晶簇颜色", Color) = (0.32,0.055,0.006,1)
 _Emission ("琥珀亮点", Range(0,3)) = 0.9
 _Seed ("纹理差异", Float) = 0
 _FloorBounce ("受光地板反照", Range(0,2)) = 0.65
 }
 SubShader {
 Tags { "RenderType"="Opaque" }
 UsePass "Standard/SHADOWCASTER"
 CGINCLUDE
 #include "UnityCG.cginc"
 #include "Lighting.cginc"
 #include "AutoLight.cginc"
 struct v2f {float4 pos:SV_POSITION;float3 world:TEXCOORD0;float3 local:TEXCOORD1;float3 normal:TEXCOORD4;LIGHTING_COORDS(2,3)};
 float4 _Color;float _Emission,_Seed,_FloorBounce;
 v2f vert(appdata_base v) {
 v2f o;UNITY_INITIALIZE_OUTPUT(v2f,o);
 o.pos=UnityObjectToClipPos(v.vertex);o.world=mul(unity_ObjectToWorld,v.vertex).xyz;o.local=v.vertex.xyz;o.normal=UnityObjectToWorldNormal(v.normal);
 TRANSFER_VERTEX_TO_FRAGMENT(o);return o;
 }
 float4 fragBody(v2f i):SV_Target {
 // 不再生成空间正弦条纹，碎晶明暗由原模型法线和实际受光决定。
 return float4(_Color.rgb*(0.025+0.05*_Emission),1);
 }
 float4 fragLight(v2f i):SV_Target {
 // 保留原网格的切面与倒角法线，不把每个三角形强制当作独立平面。
 float3 n=normalize(i.normal);
 float3 v=normalize(lerp(_WorldSpaceCameraPos-i.world,UNITY_MATRIX_V[2].xyz,unity_OrthoParams.w));
 n=dot(n,v)<0?-n:n;
 float3 l=normalize(UnityWorldSpaceLightDir(i.world));
 UNITY_LIGHT_ATTENUATION(attenuation,i,i.world);
 float diffuse=saturate(dot(n,l));
 float glint=pow(saturate(dot(reflect(-v,n),l)),96);
 float3 color=(_Color.rgb*diffuse*0.7+float3(1,0.48,0.12)*glint*1.3)*_LightColor0.rgb*attenuation;
 // 地板亮区的低阶反照近似；由当前灯的能量驱动，不添加侧灯或额外自发光。
 float3 floorCenter=float3(0,-0.67,0);
 float3 toFloor=floorCenter-i.world;
 float floorDistanceSq=max(dot(toFloor,toFloor),0.01);
 float3 floorDirection=toFloor*rsqrt(floorDistanceSq);
 float3 floorNormal=normalize(float3(0,1,-0.420));
 float formFactor=0.56/(0.56+floorDistanceSq)*saturate(dot(floorNormal,-floorDirection));
 float bounceDiffuse=saturate(dot(n,floorDirection));
 float3 reflected=reflect(-v,n);
 float denominator=reflected.y-0.420*reflected.z;
 float floorT=(-0.67-i.world.y+0.420*i.world.z)/min(denominator,-0.0001);
 float2 floorHit=(i.world+reflected*floorT).xz;
 float bounceGlint=exp(-dot(floorHit,floorHit)*6)*step(denominator,-0.001);
 color+=(_Color.rgb*bounceDiffuse*0.5+float3(1,0.30,0.035)*bounceGlint*0.3)
        *float3(0.65,0.60,0.46)*formFactor*_FloorBounce*_LightColor0.rgb;
 return float4(color,0);
 }
 ENDCG
 Pass {
 Tags {"LightMode"="ForwardBase"}
 Cull Off
 CGPROGRAM
 #pragma vertex vert
 #pragma fragment fragBody
 #pragma target 3.0
 ENDCG
 }
 Pass {
 Tags {"LightMode"="ForwardAdd"}
 Cull Off ZWrite Off Blend One One
 CGPROGRAM
 #pragma vertex vert
 #pragma fragment fragLight
 #pragma target 3.0
 #pragma multi_compile_fwdadd_fullshadows
 ENDCG
 }
 }
}