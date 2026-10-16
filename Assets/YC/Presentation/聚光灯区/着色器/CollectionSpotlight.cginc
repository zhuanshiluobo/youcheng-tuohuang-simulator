#ifndef YC_COLLECTION_SPOTLIGHT_INCLUDED
#define YC_COLLECTION_SPOTLIGHT_INCLUDED
float4 _SpotPositionRange, _SpotDirectionCos, _SpotColor;
float _SpotInnerCos, _KeyLightFactor, _UseRarityBackground;
// 地板与体积散射共用锥形边缘和距离截止，不能各画一个独立的圆。
float CollectionSpotMask(float3 world)
{
    float3 offset = world - _SpotPositionRange.xyz;
    float distance = length(offset);
    float cosine = dot(offset / max(distance,0.0001),_SpotDirectionCos.xyz);
    float cone = smoothstep(_SpotDirectionCos.w,max(_SpotDirectionCos.w+0.0001,_SpotInnerCos),cosine);
    return cone*(1-smoothstep(_SpotPositionRange.w*0.9,_SpotPositionRange.w,distance));
}
#endif
