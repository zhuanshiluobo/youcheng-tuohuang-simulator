Shader "NoMadCity/UI/ProceduralSelection"
{
 Properties {
  [PerRendererData] _MainTex ("Unused UI Texture", 2D) = "white" {}
  _StencilComp ("Stencil Comparison", Float) = 8
  _Stencil ("Stencil ID", Float) = 0
  _StencilOp ("Stencil Operation", Float) = 0
  _StencilWriteMask ("Stencil Write Mask", Float) = 255
  _StencilReadMask ("Stencil Read Mask", Float) = 255
  _ColorMask ("Color Mask", Float) = 15
  [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0
 }
 SubShader {
  Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" "CanUseSpriteAtlas"="False" }
  Stencil { Ref [_Stencil] Comp [_StencilComp] Pass [_StencilOp] ReadMask [_StencilReadMask] WriteMask [_StencilWriteMask] }
  Cull Off Lighting Off ZWrite Off ZTest [unity_GUIZTestMode]
  Blend SrcAlpha OneMinusSrcAlpha
  ColorMask [_ColorMask]
  Pass {
   CGPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   #pragma target 3.0
   #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
   #pragma multi_compile_local _ UNITY_UI_ALPHACLIP
   #include "UnityCG.cginc"
   #include "UnityUI.cginc"
   struct appdata { float4 vertex:POSITION; fixed4 color:COLOR; float4 shape:TEXCOORD0; float4 state:TEXCOORD1; float4 anim:TEXCOORD2; float4 style:TEXCOORD3; UNITY_VERTEX_INPUT_INSTANCE_ID };
   struct v2f { float4 vertex:SV_POSITION; fixed4 color:COLOR; float4 shape:TEXCOORD0; float4 state:TEXCOORD1; float4 anim:TEXCOORD2; float4 style:TEXCOORD3; float2 local:TEXCOORD4; UNITY_VERTEX_OUTPUT_STEREO };
   float4 _ClipRect;
   v2f vert(appdata v) {
    v2f o; UNITY_SETUP_INSTANCE_ID(v); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
    o.vertex=UnityObjectToClipPos(v.vertex); o.local=v.vertex.xy;
    o.color=v.color; o.shape=v.shape; o.state=v.state; o.anim=v.anim; o.style=v.style; return o;
   }
   // Intersection of axis-aligned rectangle and four 45-degree corner planes.
   float cutDistance(float2 p, float2 halfSize, float cut) {
    float2 q=abs(p)-halfSize;
    return max(max(q.x,q.y),(q.x+q.y+cut)*0.70710678);
   }
   float SelectionOutline(float d,float width,float aa) { return 1-smoothstep(width*.5-aa,width*.5+aa,abs(d)); }
   fixed4 frag(v2f i):SV_Target {
    float2 p=i.shape.xy, h=i.shape.zw;
    float selected=i.state.x, hover=i.state.y, disabled=i.state.z;
    float t=i.anim.x, flash=i.anim.y, width=i.anim.z, cut=i.anim.w;
    float glowSize=max(i.style.x,0.1), motion=i.style.y;
    float d=cutDistance(p,h,cut);
    float aa=max(fwidth(d)*0.75,0.35);
    // Persistent contrast is independent of animation. Local Canvas units, not screen pixels.
    float pulse=lerp(1,0.8+0.2*sin(t*3.9269908),motion*selected);
    float selectedWidth=max(4.5,width*1.65);
    float edge=SelectionOutline(d,lerp(width,selectedWidth,selected),aa);
    float dash=step(.38,frac((p.x+p.y)/12));
    edge*=lerp(1,dash,disabled);
    float outside=max(d,0);
    float glow=exp(-outside*outside/(glowSize*glowSize*.25))*step(0,d);
    glow*=selected*(.50*pulse+.25*flash)*(1-disabled);
    // Start at clipped upper-left / lower-right corner midpoints. Opposite points stay paired.
    float phase=atan2(p.y/max(h.y,1),p.x/max(h.x,1))/6.2831853+.5;
    float2 initialCorner=float2(-h.x+cut*.5,h.y-cut*.5)/max(h,float2(1,1));
    float startPhase=atan2(initialCorner.y,initialCorner.x)/6.2831853+.5;
    // Half-turn in .8s (smooth acceleration -> deceleration), then HOLD .4s.
    // Quintic smoothstep has zero velocity AND acceleration at both ends.
    float beat=t/1.2;
    float moveProgress=saturate(frac(beat)*1.5);
    float eased=moveProgress*moveProgress*moveProgress*(moveProgress*(moveProgress*6-15)+10);
    float halfTurn=.5*(floor(beat)+eased);
    float travel=frac(phase-startPhase-halfTurn);
    float phaseDistance=min(min(travel,1-travel),abs(travel-.5));
    float tracer=(1-smoothstep(.025,.085,phaseDistance))*selected*motion*(1-disabled);
    float sweepCenter=lerp(-h.x-h.y,h.x+h.y,1-flash);
    float sweep=exp(-pow((p.x+p.y-sweepCenter)/max(18,(h.x+h.y)*.09),2))*flash*selected;
    // Inner second keyline gives a clearly different static silhouette when motion is disabled.
    float inner=SelectionOutline(d+selectedWidth*.5+3,1.3,aa)*selected*(1-disabled);
    // v8: no additional focus outline. Original game UI owns navigation feedback.
    float strength=lerp(.25,1,selected)+hover*.15;
    float a=saturate(edge*strength+inner*.90+glow);
    // Warm platinum lift only for selected geometry; retain tint control and original alpha.
    float3 selectedTint=lerp(i.color.rgb,float3(1,.96,.78),.30*selected);
    float3 rgb=lerp(selectedTint,float3(.53,.55,.56),disabled);
    // Static machined-metal details live INSIDE the existing main stroke only.
    // No extra geometry, textures, random flicker, or decoration over the card contents.
    float strokeMask=edge*selected*(1-disabled);
    float bevel=smoothstep(-selectedWidth*.5,selectedWidth*.5,d);
    rgb*=lerp(1,.94+.06*bevel,strokeMask);
    float incision=1-smoothstep(.28-aa,.28+aa,abs(d+.35));
    // Sparse diagonal engraving, antialiased in local Canvas units.
    float engravingCoord=p.x+p.y;
    float engravingDistance=abs(frac(engravingCoord/22+.5)-.5)*22;
    float engravingAA=max(fwidth(engravingCoord)*.75,.35);
    float engraving=1-smoothstep(.45-engravingAA,.45+engravingAA,engravingDistance);
    rgb*=1-strokeMask*(incision*.16+engraving*.12);
    // Reinforced clipped corners with restrained ivory pin-rivets.
    float cornerProximity=(h.x-abs(p.x))+(h.y-abs(p.y))-cut;
    float cornerAccent=(1-smoothstep(2,16,cornerProximity))*strokeMask;
    rgb=lerp(rgb,float3(1,.94,.68),cornerAccent*.35);
    float2 rivetCenter=max(h-cut*.5,float2(0,0));
    float rivetDistance=length(abs(p)-rivetCenter);
    float rivetAA=max(fwidth(rivetDistance)*.75,.35);
    float rivetRing=1-smoothstep(2-rivetAA,2+rivetAA,rivetDistance);
    float rivetCore=1-smoothstep(.9-rivetAA,.9+rivetAA,rivetDistance);
    // Suppress pins if the corner is too small to contain them.
    float rivetMask=strokeMask*smoothstep(3,6,cut);
    rgb*=1-rivetRing*rivetMask*.22;
    rgb=lerp(rgb,float3(1,.98,.86),rivetCore*rivetMask);
    // Moving light remains the clearest layer above the stationary engraving.
    rgb=lerp(rgb,float3(1,1,.94),saturate(tracer+sweep*.7));
    fixed4 result=fixed4(rgb,a*i.color.a);
    #ifdef UNITY_UI_CLIP_RECT
    result.a*=UnityGet2DClipping(i.local,_ClipRect);
    #endif
    #ifdef UNITY_UI_ALPHACLIP
    clip(result.a-.001);
    #endif
    return result;
   }
   ENDCG
  }
 }
}
