Shader "YC/Collection/Local Volume"
{
 Properties {
 _KeyLightFactor ("顶灯能量联动", Float) = 1
 _Color ("散射颜色", Color) = (1,0.58,0.24,1)
 _Density ("雾密度", Range(0,4)) = 0.28
 _Shape ("形状 0光锥 1低雾", Range(0,1)) = 0
 _NoiseScale ("雾团尺度", Float) = 5
 _Speed ("流动速度", Range(0,0.2)) = 0.025
 [HideInInspector] _FloorY ("旧地板高度（兼容保留）", Float) = -0.67
 [HideInInspector] _FloorSlope ("旧地板倾斜（兼容保留）", Float) = 0.42
 }
 SubShader {
 // 地板和不透明藏品之后、晶体透明外壳之前，保证折射仍能看到雾。
 Tags { "Queue"="Transparent-100" "RenderType"="Transparent" }
 Pass {
 Cull Front ZWrite Off ZTest Always
 Blend One OneMinusSrcAlpha
 CGPROGRAM
 #pragma vertex vert
 #pragma fragment frag
 #pragma target 3.0
 #include "UnityCG.cginc"
 #include "CollectionSpotlight.cginc"
 struct v2f {float4 pos:SV_POSITION;float3 world:TEXCOORD0;float4 screen:TEXCOORD1;};
 float4 _Color, _FloorPlane;
 float _Density,_Shape,_NoiseScale,_Speed;
 UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
 float hash(float3 p){return frac(sin(dot(p,float3(127.1,311.7,74.7)))*43758.5453);}
 float noise(float3 p){float3 a=floor(p),f=frac(p);f=f*f*(3-2*f);return lerp(lerp(lerp(hash(a),hash(a+float3(1,0,0)),f.x),lerp(hash(a+float3(0,1,0)),hash(a+float3(1,1,0)),f.x),f.y),lerp(lerp(hash(a+float3(0,0,1)),hash(a+float3(1,0,1)),f.x),lerp(hash(a+float3(0,1,1)),hash(a+1),f.x),f.y),f.z);}
 v2f vert(appdata_base v){v2f o;o.pos=UnityObjectToClipPos(v.vertex);o.world=mul(unity_ObjectToWorld,v.vertex).xyz;o.screen=ComputeScreenPos(o.pos);return o;}
 float4 frag(v2f i):SV_Target {
 if(_KeyLightFactor<=0 || _SpotPositionRange.w<=0)return 0;
 float3 forward=-UNITY_MATRIX_V[2].xyz;
 float3 rd=normalize(lerp(i.world-_WorldSpaceCameraPos,forward,unity_OrthoParams.w));
 float3 ro=lerp(_WorldSpaceCameraPos,i.world-rd*dot(i.world-_WorldSpaceCameraPos,rd),unity_OrthoParams.w);
 float3 o=mul(unity_WorldToObject,float4(ro,1)).xyz;
 float3 d=mul((float3x3)unity_WorldToObject,rd);
 float3 safeD=lerp(-1,1,step(0,d))*max(abs(d),1e-6);
 float3 t0=(-0.5-o)/safeD,t1=(0.5-o)/safeD;
 float3 nearT=min(t0,t1),farT=max(t0,t1);
 float enter=max(0,max(nearT.x,max(nearT.y,nearT.z)));
 float exit=min(farT.x,min(farT.y,farT.z));
 float3 lightOffset=ro-_SpotPositionRange.xyz;
 float lightB=dot(lightOffset,rd);
 float discriminant=lightB*lightB-dot(lightOffset,lightOffset)+_SpotPositionRange.w*_SpotPositionRange.w;
 if(discriminant<=0)return 0;
 float root=sqrt(discriminant);
 enter=max(enter,-lightB-root);exit=min(exit,-lightB+root);
 // 从实际地板 Transform 得到平面，移动或倾斜地板后仍然贴合。
 float floorOrigin=dot(_FloorPlane.xyz,ro)+_FloorPlane.w;
 float floorDirection=dot(_FloorPlane.xyz,rd);
 float floorDistance=1e20;
 if(abs(floorDirection)>0.00001) {
 floorDistance=-floorOrigin/floorDirection;
 if(floorDirection>0)enter=max(enter,floorDistance);else exit=min(exit,floorDistance);
 } else if(floorOrigin<0)return 0;
 float raw=SAMPLE_DEPTH_TEXTURE_PROJ(_CameraDepthTexture,UNITY_PROJ_COORD(i.screen));
 float perspDepth=LinearEyeDepth(raw);
 #if defined(UNITY_REVERSED_Z)
 raw=1-raw;
 #endif
 float orthoDepth=lerp(_ProjectionParams.y,_ProjectionParams.z,raw);
 float eyeDepth=lerp(perspDepth,orthoDepth,unity_OrthoParams.w);
 float surfaceDistance=eyeDepth/max(dot(rd,forward),0.001);
 // 稀有度雾是背景。遇到地板前的实体藏品时不覆盖该像素，避免再次染色。
 float surfaceHeight=dot(_FloorPlane.xyz,ro+rd*surfaceDistance)+_FloorPlane.w;
 if(_UseRarityBackground>0.5 && floorDistance>0 && surfaceHeight>0.08 && surfaceDistance<floorDistance)return 0;
 exit=min(exit,surfaceDistance);
 if(exit<=enter)return 0;
 float stepSize=(exit-enter)/64;
 float optical=0;
 [loop] for(int s=0;s<64;s++) {
 float distance=enter+(s+0.5)*stepSize;
 float3 p=o+d*distance;
 float3 world=ro+rd*distance;
 float height=dot(_FloorPlane.xyz,world)+_FloorPlane.w;
 float fog=pow(saturate(1-length(p*2)),2);
 // 光锥扩大只改变覆盖范围，不拉伸雾的纹理。
 float3 noisePosition=lerp((world-_SpotPositionRange.xyz)/14,p,_Shape);
 float cloud=noise(noisePosition*_NoiseScale+float3(_Time.y*_Speed,0,_Time.y*_Speed*0.6));
 optical+=CollectionSpotMask(world)*lerp(1,fog,_Shape)*lerp(0.55,1.25,cloud)*_Density*stepSize*
     smoothstep(0,0.015,height)*lerp(0.3,1.0,smoothstep(0,0.45,height));
 }
 float alpha=1-exp(-optical*max(_KeyLightFactor,0));
 // 稀有度背景只保留原散射材质的明暗，不与原有橙色相乘造成偏色。
 float3 scatterColor=_Color.rgb;
 if(_UseRarityBackground>0.5)scatterColor=dot(_Color.rgb,float3(0.2126,0.7152,0.0722));
 return float4(scatterColor*_SpotColor.rgb*alpha,alpha);
 }
 ENDCG
 }
 }
}
