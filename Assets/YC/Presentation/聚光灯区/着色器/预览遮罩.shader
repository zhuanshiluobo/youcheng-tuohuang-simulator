Shader "YC/收藏室/预览遮罩"
{
    Properties { _Opacity ("遮黑程度", Range(0,1)) = 0 }
    SubShader
    {
        Tags { "Queue"="Overlay" "RenderType"="Transparent" "IgnoreProjector"="True" "DisableBatching"="True" }
        Pass
        {
            Cull Off ZWrite Off ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float _Opacity;
            float4 vert(float4 position : POSITION) : SV_POSITION
            {
                // 四顶点直接覆盖当前预览渲染目标，不依赖相机尺寸和位置。
                return float4(position.xy, 0, 1);
            }
            float4 frag() : SV_Target { return float4(0, 0, 0, saturate(_Opacity)); }
            ENDCG
        }
    }
    Fallback Off
}