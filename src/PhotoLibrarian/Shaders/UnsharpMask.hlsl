Texture2D<float4> Source : register(t0);
Texture2D<float4> Blurred : register(t1);
SamplerState SourceSampler : register(s0);
SamplerState BlurredSampler : register(s1);
float Amount;
float Threshold;

float4 main(float4 position : SV_POSITION, float4 scenePosition : SCENE_POSITION,
            float4 sourceCoordinate : TEXCOORD0,
            float4 blurCoordinate : TEXCOORD1) : SV_TARGET
{
    float4 original = Source.Sample(SourceSampler, sourceCoordinate.xy);
    if (original.a == 0) return 0;

    float4 blurred = Blurred.Sample(BlurredSampler, blurCoordinate.xy);
    float3 originalColor = original.rgb / original.a;
    float3 blurredColor = blurred.rgb / max(blurred.a, 0.00001);
    float3 detail = originalColor - blurredColor;
    float largestDifference = max(abs(detail.r), max(abs(detail.g), abs(detail.b)));
    if (largestDifference < Threshold) detail = 0;

    float3 sharpened = saturate(originalColor + Amount * detail);
    return float4(sharpened * original.a, original.a);
}
