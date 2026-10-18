Shader "YC/Collection/NucleicCrystalGlass"
{
    Properties
    {
        _Color ("晶体吸收色", Color) = (0.9,0.85,1,1)
        _Glossiness ("切面光滑度", Range(0,1)) = 0.96
        _Refraction ("屏幕折射偏移", Range(0,0.04)) = 0.014
        _Reflection ("切面反射强度", Range(0,2)) = 0.8
        _Transmission ("内部透光", Range(0,1.5)) = 0.95
        _BackgroundNeutrality ("背景透射中性色比例", Range(0,1)) = 0.9
        _StudioCube ("中性摄影棚反射", Cube) = "black" {}
    }
    SubShader
    {
        Tags { "Queue"="Transparent+40" "RenderType"="Transparent" }
        GrabPass { "_NucleicCrystalBackground" }
        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            Cull Back ZWrite Off Blend One Zero
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fwdbase
            #include "UnityCG.cginc"
            #include "Lighting.cginc"
            #include "AutoLight.cginc"
            struct v2f
            {
                float4 pos:SV_POSITION;
                float4 grab:TEXCOORD0;
                float3 world:TEXCOORD1;
                float3 normal:TEXCOORD2;
                LIGHTING_COORDS(3,4)
            };
            sampler2D _NucleicCrystalBackground;
            samplerCUBE _StudioCube;
            float4 _Color;
            float _Glossiness, _Refraction, _Reflection, _Transmission, _BackgroundNeutrality;
            v2f vert(appdata_full v)
            {
                v2f o;
                o.pos=UnityObjectToClipPos(v.vertex);
                o.grab=ComputeGrabScreenPos(o.pos);
                o.world=mul(unity_ObjectToWorld,v.vertex).xyz;
                o.normal=UnityObjectToWorldNormal(v.normal);
                TRANSFER_VERTEX_TO_FRAGMENT(o);
                return o;
            }
            float4 frag(v2f i):SV_Target
            {
                float3 n=normalize(i.normal);
                float3 v=normalize(lerp(_WorldSpaceCameraPos-i.world,UNITY_MATRIX_V[2].xyz,unity_OrthoParams.w));
                float fresnel=0.04+0.96*pow(1-saturate(dot(n,v)),5);
                float2 offset=mul((float3x3)UNITY_MATRIX_V,n).xy*_Refraction;
                float3 behind=tex2D(_NucleicCrystalBackground,i.grab.xy/i.grab.w+offset).rgb;
                // 收藏室雾色保留在场景中，仅控制它透过这件无色/紫色晶体时的染色程度。
                float grey=dot(behind,float3(0.2126,0.7152,0.0722));
                behind=lerp(behind,grey.xxx,_BackgroundNeutrality);
                float3 absorption=lerp(_Color.rgb,sqrt(max(_Color.rgb,0)),0.5);
                float3 r=reflect(-v,n);
                float3 reflected=texCUBElod(_StudioCube,float4(r,(1-_Glossiness)*6)).rgb;
                float3 internalDirection=refract(-v,n,1.0/1.48);
                float3 internalReflection=texCUBElod(_StudioCube,float4(internalDirection,(1-_Glossiness)*6)).rgb;
                // 保留亮核透射，减少无结构的背景雾覆盖切面反射。
                float brightContent=smoothstep(0.72,0.95,max(behind.r,max(behind.g,behind.b)));
                float3 transmitted=behind*absorption*_Transmission*lerp(0.48,1,brightContent);
                float3 light=normalize(UnityWorldSpaceLightDir(i.world));
                float3 halfVector=normalize(light+v);
                UNITY_LIGHT_ATTENUATION(attenuation,i,i.world);
                float highlight=pow(saturate(dot(n,halfVector)),lerp(32,160,_Glossiness))*saturate(dot(n,light));
                float3 color=transmitted*(1-fresnel*0.8)
                    +reflected*lerp(1,absorption,0.25)*_Reflection*(0.40+fresnel*0.85)
                    +internalReflection*absorption*_Reflection*0.22
                    +_Color.rgb*0.055+_LightColor0.rgb*attenuation*highlight*0.035;
                float peak=max(color.r,max(color.g,color.b));
                float excess=max(peak-0.65,0);
                float bounded=peak<=0.65?peak:0.65+excess/(1+excess/0.35);
                color*=bounded/max(peak,0.00001);
                return float4(color,1);
            }
            ENDCG
        }
    }
    FallBack Off
}
