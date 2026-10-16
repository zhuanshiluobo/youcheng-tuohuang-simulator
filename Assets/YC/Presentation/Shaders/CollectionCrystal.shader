Shader "YC/Collection/Crystal"
{
 Properties {
 _KeyLightFactor ("顶灯能量联动", Float) = 1
 _InteriorCube ("原晶簇内部反射", Cube) = "black" {}
 _InteriorOcclusion ("原晶簇光线遮挡", 3D) = "black" {}
 _InteriorAbsorption ("晶簇遮挡强度", Range(0,30)) = 12
 _InteriorCeilingStrength ("内部顶光反射", Range(0,4)) = 2.2
 _InteriorFloorStrength ("内部地板反射", Range(0,6)) = 0.32
 _CoreReflectionPosition ("核心反射局部中心", Vector) = (0.0931,-0.2866,0.0575,0)
 _CoreReflectionRadius ("核心反射柔化半径", Float) = 0.24
 _CoreReflectionStrength ("核心反射亮度", Range(0,20)) = 12
 _CaptureRotation ("反射采样朝向", Vector) = (0,0,0,1)
 _Tint ("吸收颜色", Color) = (0.12,0.028,0.006,1)
 _Amber ("内部琥珀反射", Color) = (1,0.23,0.022,1)
 _Refraction ("折射强度", Range(0,0.06)) = 0.018
 _Reflection ("切面反射", Range(0,2)) = 0.7
 _Transmission ("内部透光", Range(0,2)) = 1.1
 _SurfaceLightStrength ("实际灯光表面受光", Range(0,2)) = 1
 }
 SubShader {
 Tags { "Queue"="Transparent+50" "RenderType"="Transparent" }
 Pass {
 Name "SHADOWCASTER"
 Tags { "LightMode"="ShadowCaster" }
 ZWrite On ZTest LEqual Cull Back
 CGPROGRAM
 #pragma vertex shadowVert
 #pragma fragment shadowFrag
 #pragma multi_compile_shadowcaster
 #include "UnityCG.cginc"
 struct shadowData { V2F_SHADOW_CASTER; };
 shadowData shadowVert(appdata_base v) {shadowData o;TRANSFER_SHADOW_CASTER_NORMALOFFSET(o);return o;}
 float4 shadowFrag(shadowData i):SV_Target {SHADOW_CASTER_FRAGMENT(i)}
 ENDCG
 }
 GrabPass { "_CollectionCrystalInterior" }
 // 显示原网格背面的晶体切面，不增加或改变几何结构。
 Pass {
 Cull Front ZWrite Off ZTest LEqual
 CGPROGRAM
 #pragma vertex vertBack
 #pragma fragment fragBack
 #include "UnityCG.cginc"
 struct backData {float4 pos:SV_POSITION;float3 normal:TEXCOORD0;float3 world:TEXCOORD1;float4 screen:TEXCOORD2;};
 sampler2D _CollectionCrystalInterior;
 samplerCUBE _InteriorCube;float4 _CaptureRotation;
 // 小范围方向采样表达外壳微观粗糙度，切面边界仍由原始网格决定。
 float3 sampleCrystalReflection(float3 direction) {
 float3 r=normalize(direction);
 float3 tangent=normalize(cross(r,abs(r.y)<0.95?float3(0,1,0):float3(1,0,0)))*0.035;
 float3 bitangent=cross(r,tangent);
 return texCUBE(_InteriorCube,r).rgb*0.4+
 (texCUBE(_InteriorCube,r+tangent).rgb+texCUBE(_InteriorCube,r-tangent).rgb+
 texCUBE(_InteriorCube,r+bitangent).rgb+texCUBE(_InteriorCube,r-bitangent).rgb)*0.15;
 }
 backData vertBack(appdata_base v){backData o;o.pos=UnityObjectToClipPos(v.vertex);o.normal=UnityObjectToWorldNormal(v.normal);o.world=mul(unity_ObjectToWorld,v.vertex).xyz;o.screen=ComputeGrabScreenPos(o.pos);return o;}
 float4 fragBack(backData i):SV_Target{
 float3 n=normalize(i.normal),v=normalize(lerp(_WorldSpaceCameraPos-i.world,UNITY_MATRIX_V[2].xyz,unity_OrthoParams.w));
 float3 r=reflect(-v,n);
 float3 localR=normalize(mul((float3x3)unity_WorldToObject,r));
 localR=normalize(mul(unity_WorldToObject,float4(i.world,1)).xyz+localR*0.85);
 float3 cubeR=localR+2*cross(_CaptureRotation.xyz,cross(_CaptureRotation.xyz,localR)+_CaptureRotation.w*localR);
 float3 backSample=max(sampleCrystalReflection(cubeR),0);
 float backPeak=max(backSample.r,max(backSample.g,backSample.b));
 float3 crystalReflection=pow(backSample/(1+backPeak),1.8)*float3(1,0.65,0.25);
 return float4(float3(0.0005,0.0001,0.00002)+crystalReflection*0.07,1);
 }
 ENDCG
 }
 GrabPass { "_CollectionCrystalBackground" }
 Pass {
 Cull Back ZWrite Off Blend One Zero
 CGPROGRAM
 #pragma vertex vert
 #pragma fragment frag
 #pragma target 3.0
 #include "UnityCG.cginc"
 struct v2f { float4 pos:SV_POSITION; float4 grab:TEXCOORD0; float3 world:TEXCOORD1; float3 normal:TEXCOORD2; float3 local:TEXCOORD3; };
 sampler2D _CollectionCrystalBackground;
 samplerCUBE _InteriorCube;float4 _CaptureRotation;
 // 小范围方向采样表达外壳微观粗糙度，切面边界仍由原始网格决定。
 float3 sampleCrystalReflection(float3 direction) {
 float3 r=normalize(direction);
 float3 tangent=normalize(cross(r,abs(r.y)<0.95?float3(0,1,0):float3(1,0,0)))*0.035;
 float3 bitangent=cross(r,tangent);
 return texCUBE(_InteriorCube,r).rgb*0.4+
 (texCUBE(_InteriorCube,r+tangent).rgb+texCUBE(_InteriorCube,r-tangent).rgb+
 texCUBE(_InteriorCube,r+bitangent).rgb+texCUBE(_InteriorCube,r-bitangent).rgb)*0.15;
 }
 // 来自原始源石网格的20个主切面，仅用于光线路径，不改变几何轮廓。
 static const float4 crystalPlanes[20] = {
 float4(-0.9818763,0.0001295,-0.1895228,0.7949943),
 float4(0.9824494,0.0016345,0.1865224,0.7949945),
 float4(-0.3032975,0.9338594,-0.1895186,0.7949895),
 float4(0.3039386,-0.9341694,0.1869459,0.7947381),
 float4(0.3036792,0.9343235,0.1865976,0.7948433),
 float4(-0.3035444,-0.9337797,-0.1895158,0.7949887),
 float4(-0.7948537,-0.5773990,0.1865959,0.7948452),
 float4(0.7938526,-0.5788012,-0.1865131,0.7949907),
 float4(0.7940437,0.5780552,-0.1880075,0.7948455),
 float4(-0.7938506,0.5788054,0.1865087,0.7949924),
 float4(-0.4906724,-0.3574404,0.7946552,0.7947466),
 float4(0.4919364,-0.3562853,-0.7943925,0.7948518),
 float4(0.4911642,0.3561580,-0.7949272,0.7947180),
 float4(-0.4923054,0.3578487,0.7934605,0.7949969),
 float4(0.1885278,-0.5775363,0.7942978,0.7948504),
 float4(-0.1868279,0.5779603,-0.7943912,0.7948506),
 float4(-0.1883159,-0.5771150,-0.7946543,0.7947450),
 float4(0.1868280,0.5779606,0.7943909,0.7948507),
 float4(-0.6075267,0.0008299,-0.7942988,0.7948521),
 float4(0.6067089,-0.0005569,0.7949238,0.7947154)
 };
 // 用原壳主切面计算地板到顶灯之间的遮挡；短穿透路径柔化投影边缘。
 float4 _SpotPositionRange, _SpotDirectionCos, _SpotColor;
 float _SpotInnerCos;
 float spotCoverage(float3 worldPoint) {
 float3 delta=worldPoint-_SpotPositionRange.xyz;
 float distance=length(delta);
 float cosine=dot(delta/max(distance,0.0001),_SpotDirectionCos.xyz);
 return smoothstep(_SpotDirectionCos.w,max(_SpotDirectionCos.w+0.0001,_SpotInnerCos),cosine)*
 (1-smoothstep(_SpotPositionRange.w*0.9,_SpotPositionRange.w,distance));
 }
 float reflectedFloorVisibility(float3 worldFloor) {
 float3 origin=mul(unity_WorldToObject,float4(worldFloor,1)).xyz;
 float3 light=mul(unity_WorldToObject,float4(_SpotPositionRange.xyz,1)).xyz;
 float limit=length(light-origin);
 float3 direction=(light-origin)/max(limit,0.0001);
 float entry=0,exit=limit;
 [unroll] for(int face=0;face<20;face++) {
 float4 plane=crystalPlanes[face];
 float signedDistance=plane.w-dot(origin,plane.xyz);
 float denominator=dot(direction,plane.xyz);
 if(abs(denominator)<0.0001) {if(signedDistance<0)return 1;}
 else {
 float crossing=signedDistance/denominator;
 if(denominator<0)entry=max(entry,crossing);else exit=min(exit,crossing);
 }
 }
 return 1-0.65*smoothstep(0,0.18,exit-entry);
 }
 float _KeyLightFactor;
 float _InteriorCeilingStrength, _InteriorFloorStrength;
 float3 internalEnvironment(float3 localPoint,float3 localDirection) {
 float3 p=mul(unity_ObjectToWorld,float4(localPoint,1)).xyz;
 float3 d=normalize(mul((float3x3)unity_ObjectToWorld,localDirection));
 float3 toLight=normalize(_SpotPositionRange.xyz-p);
 float source=pow(saturate(dot(d,toLight)),96)*spotCoverage(p);
 // 下方只反射已有受光地板，避免额外添加侧灯。
 float floorDenom=d.y-0.420*d.z;
 float floorT=(-0.67-p.y+0.420*p.z)/min(floorDenom,-0.0001);
 float2 floorHit=(p+d*floorT).xz;
 float floorSource=spotCoverage(p+d*floorT)*(1-smoothstep(1.02,1.16,length(floorHit)))*step(floorDenom,-0.001);
 if(floorSource>0.0001)floorSource*=reflectedFloorVisibility(p+d*floorT);
 return _SpotColor.rgb*(float3(1,0.58,0.23)*source*_InteriorCeilingStrength+float3(1,0.53,0.14)*floorSource*_InteriorFloorStrength)*_KeyLightFactor;
 }
  sampler3D _InteriorOcclusion;
 float _InteriorAbsorption;
 // 遮挡体素由原有晶簇烘焙，使用壳局部坐标，不随相机移动或改变网格。
 float interiorVisibility(float3 origin,float3 direction,float distance) {
 float density=0;
 [loop] for(int sampleIndex=0;sampleIndex<24;sampleIndex++) {
 float3 samplePosition=origin+direction*(distance*(sampleIndex+0.5)/24);
 density+=tex3Dlod(_InteriorOcclusion,float4((samplePosition+1.1)/2.2,0)).r;
 }
 return exp(-density*distance*(_InteriorAbsorption/24));
 }
 float4 _CoreReflectionPosition;
 float _CoreReflectionRadius, _CoreReflectionStrength;
 float3 traceInternalFacets(float3 start,float3 direction) {
 float3 p=start*0.985;
 float3 d=normalize(direction);
 float3 energy=float3(0.75,0.75,0.5);
 float3 radiance=0;
 [loop] for(int bounce=0;bounce<6;bounce++) {
 float distance=10;float3 faceNormal=0;
 [unroll] for(int face=0;face<20;face++) {
 float4 plane=crystalPlanes[face];
 float denominator=dot(d,plane.xyz);
 float candidate=(plane.w-dot(p,plane.xyz))/max(denominator,0.00001);
 if(denominator>0.001 && candidate>0.001 && candidate<distance) {distance=candidate;faceNormal=plane.xyz;}
 }
 if(distance>9)break;
 // 用现有核心的有限发光面积近似多次反射，不复制标志轮廓，也不添加场景光源。
 if(bounce>0) {
 float coreTravel=dot(_CoreReflectionPosition.xyz-p,d);
 float3 coreOffset=p+d*coreTravel-_CoreReflectionPosition.xyz;
 float coreRadius=max(_CoreReflectionRadius,0.001);
 float coreGlow=exp(-dot(coreOffset,coreOffset)/(coreRadius*coreRadius)*2);
 if(coreTravel>0 && coreTravel<distance && coreGlow>0.001)
 radiance+=energy*float3(1,0.40,0.04)*coreGlow*_CoreReflectionStrength*interiorVisibility(p,d,coreTravel);
 }
 // 每段反射路径采样原晶簇，让内部切面保留实际碎晶细节。
 float3 contentsDirection=normalize(p+d*distance*0.5);
 float3 capturedDirection=contentsDirection+2*cross(_CaptureRotation.xyz,cross(_CaptureRotation.xyz,contentsDirection)+_CaptureRotation.w*contentsDirection);
 float3 bounceSample=max(texCUBElod(_InteriorCube,float4(capturedDirection,0)).rgb,0);
 float bouncePeak=max(bounceSample.r,max(bounceSample.g,bounceSample.b));
 float3 bounceContents=pow(bounceSample/(1+bouncePeak),1.6)*float3(1,0.65,0.25)*2;
 radiance+=energy*bounceContents*0.08*step(0.5,(float)bounce);
 energy*=interiorVisibility(p,d,distance);
 p+=d*distance;
 energy*=exp(-distance*float3(0.28,0.65,1.4));
 float cosine=saturate(dot(d,faceNormal));
 float3 outgoing=refract(d,-faceNormal,1.72);
 float totalReflection=step(dot(outgoing,outgoing),0.0001);
 float fresnel=lerp(0.07+0.93*pow(1-cosine,5),1,totalReflection);
 if(totalReflection<0.5)radiance+=energy*(1-fresnel)*internalEnvironment(p,outgoing);
 energy*=fresnel;
 d=reflect(d,faceNormal);
 p+=d*0.003;
 }
 return radiance;
 }
 float4 _Tint, _Amber;
 float _Refraction, _Reflection, _Transmission;

 v2f vert(appdata_base v) { v2f o; o.pos=UnityObjectToClipPos(v.vertex);o.grab=ComputeGrabScreenPos(o.pos);o.world=mul(unity_ObjectToWorld,v.vertex).xyz;o.normal=UnityObjectToWorldNormal(v.normal);o.local=v.vertex.xyz;return o; }
 float4 frag(v2f i):SV_Target {
 float3 n=normalize(i.normal), v=normalize(lerp(_WorldSpaceCameraPos-i.world,UNITY_MATRIX_V[2].xyz,unity_OrthoParams.w));
 float fresnel=pow(1-saturate(dot(n,v)),3);
 float2 uv=i.grab.xy/i.grab.w;
 float2 offset=mul((float3x3)UNITY_MATRIX_V,n).xy*_Refraction;
 float3 inner=tex2D(_CollectionCrystalBackground,uv+offset).rgb;
 
 // Secondary internal reflections sample the actual crystal contents.
 // 压低透过外壳的无色地板/雾，仅保留琥珀内含物的饱和光泽。
 float3 contents=inner;
 float saturation=saturate((max(contents.r,max(contents.g,contents.b))-min(contents.r,min(contents.g,contents.b)))/max(max(contents.r,max(contents.g,contents.b)),0.001));
 float3 transmitted=contents*lerp(0.004,0.92,smoothstep(0.5,0.85,saturation))*float3(1,0.9,0.6)*_Transmission;
 float3 reflected=reflect(-v,n);
 
 
 float rim=pow(1-saturate(dot(n,v)),5);
 
 
 
 float floorDenominator=reflected.y-0.420*reflected.z;
 float3 floorRay=reflected*((-0.67-i.world.y+0.420*i.world.z)/min(floorDenominator,-0.0001));
 float2 floorHit=(i.world+floorRay).xz;
 float floorReflection=spotCoverage(i.world+floorRay)*(1-smoothstep(1.02,1.16,length(floorHit)))*step(floorDenominator,-0.001);
 if(floorReflection>0.0001)floorReflection*=reflectedFloorVisibility(i.world+floorRay);
 float3 reflection=0;
 // 地板亮区的镜像独立保留，不用边缘遮罩压掉正对视线的底部切面。

 float3 localR=normalize(mul((float3x3)unity_WorldToObject,reflected));
 localR=normalize(mul(unity_WorldToObject,float4(i.world,1)).xyz+localR*0.85);
 float3 cubeR=localR+2*cross(_CaptureRotation.xyz,cross(_CaptureRotation.xyz,localR)+_CaptureRotation.w*localR);
 float3 facetContents=max(sampleCrystalReflection(cubeR),0);
 // 用原晶簇反射分布打散亮区，避免整块底面变成均匀橙色。
 float facetPeak=max(facetContents.r,max(facetContents.g,facetContents.b));
 float floorFacetDetail=pow(facetPeak/(1+facetPeak),4);
 reflection+=_SpotColor.rgb*float3(1,0.39,0.09)*pow(saturate(floorReflection),2.5)*(0.008+floorFacetDetail*2.0)*_KeyLightFactor;
 // 只放大原晶簇反射中的亮部，暗面不增加均匀底色。
 float facetHighlight=smoothstep(0.08,0.32,max(facetContents.r,max(facetContents.g,facetContents.b)));
 // 压缩HDR采样后再提取亮部，避免高能量晶簇被幂函数放大成整面反光。
 float3 boundedFacet=facetContents/(1+facetPeak);
 reflection+=pow(boundedFacet,2.5)*float3(1,0.85,0.55)*(0.03+fresnel*0.55)*(1+facetHighlight*1.5);
 // 折射方向采样原晶簇，与表面反射分离，补足斜切面下的内部层次。
 float3 internalRay=refract(-v,n,1.0/1.72);
 float3 localInternal=normalize(mul((float3x3)unity_WorldToObject,internalRay));
 float3 internalDirection=normalize(i.local+localInternal*0.7);
 float3 internalCubeDirection=internalDirection+2*cross(_CaptureRotation.xyz,cross(_CaptureRotation.xyz,internalDirection)+_CaptureRotation.w*internalDirection);
 float3 internalReflection=sampleCrystalReflection(internalCubeDirection);
 internalReflection=max(internalReflection,0);
 float internalPeak=max(internalReflection.r,max(internalReflection.g,internalReflection.b));
 internalReflection=pow(internalReflection/(1+internalPeak),1.65)*float3(1,0.65,0.25)*2;
 reflection+=internalReflection*(0.01+fresnel*0.15);
 reflection+=traceInternalFacets(i.local,localInternal);
 // 内含物反射来自立方贴图，避免屏幕采样复制核心标志。
 return float4(transmitted*(1-fresnel*0.72)+reflection*_Reflection+_Tint.rgb*0.035,1);
 }
 ENDCG
 }

 // 外壳直接响应场景聚光灯，光源坐标与阴影来自 Unity，而非模型局部坐标。
 Pass {
 Name "DIRECT_SURFACE_LIGHT"
 Tags { "LightMode"="ForwardAdd" }
 Cull Back ZWrite Off Blend One One
 CGPROGRAM
 #pragma vertex vertSurface
 #pragma fragment fragSurface
 #pragma target 3.0
 #pragma multi_compile_fwdadd_fullshadows
 #include "UnityCG.cginc"
 #include "Lighting.cginc"
 #include "AutoLight.cginc"
 struct surfaceData { float4 pos:SV_POSITION; float3 world:TEXCOORD0; float3 normal:TEXCOORD1; LIGHTING_COORDS(2,3) };
 float4 _Tint;
 float _SurfaceLightStrength;
 surfaceData vertSurface(appdata_base v) {
 surfaceData o; UNITY_INITIALIZE_OUTPUT(surfaceData,o);
 o.pos=UnityObjectToClipPos(v.vertex);
 o.world=mul(unity_ObjectToWorld,v.vertex).xyz;
 o.normal=UnityObjectToWorldNormal(v.normal);
 TRANSFER_VERTEX_TO_FRAGMENT(o);
 return o;
 }
 float4 fragSurface(surfaceData i):SV_Target {
 float3 n=normalize(i.normal);
 float3 v=normalize(lerp(_WorldSpaceCameraPos-i.world,UNITY_MATRIX_V[2].xyz,unity_OrthoParams.w));
 float3 l=normalize(UnityWorldSpaceLightDir(i.world));
 float3 h=normalize(l+v);
 float nl=saturate(dot(n,l));
 float fresnel=0.07+0.93*pow(1-saturate(dot(v,h)),5);
 float specular=pow(saturate(dot(n,h)),64)*(66.0/(8.0*UNITY_PI))*fresnel;
 UNITY_LIGHT_ATTENUATION(attenuation,i,i.world);
 float3 response=(_Tint.rgb*0.045+specular)*nl*_LightColor0.rgb*attenuation;
 return float4(response*_SurfaceLightStrength,0);
 }
 ENDCG
 }
 }
}
