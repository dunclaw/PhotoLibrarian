Texture2D<float4> Source : register(t0);
Texture2D<float4> Base : register(t1);
SamplerState SourceSampler : register(s0);
SamplerState BaseSampler : register(s1);
float Gain;

float4 main(float4 position : SV_POSITION, float4 scenePosition : SCENE_POSITION,
            float4 sourceCoordinate : TEXCOORD0,
            float4 baseCoordinate : TEXCOORD1) : SV_TARGET
{
    float4 source = Source.Sample(SourceSampler, sourceCoordinate.xy);
    float4 base = Base.Sample(BaseSampler, baseCoordinate.xy);
    if (source.a <= 0) return 0;

    float3 sourceColor = source.rgb / source.a;
    float3 baseColor = base.rgb / max(base.a, 0.00001);
    float luminance = dot(sourceColor, float3(0.2126, 0.7152, 0.0722));
    float shadowGate = smoothstep(0.01, 0.10, luminance);
    float highlightGate = 1.0 - smoothstep(0.90, 0.995, luminance);
    float toneGate = shadowGate * highlightGate;
    float3 adjusted = saturate(sourceColor + (sourceColor - baseColor) * Gain * toneGate);

    return float4(adjusted * source.a, source.a);
}
