Texture2D<float4> Source : register(t0);
SamplerState SourceSampler : register(s0);
float Strength;

// Direct2D supplies normalized coordinates in xy and one source-pixel step in zw.
// Keep SCENE_POSITION in the signature: Direct2D reserves its slot before TEXCOORD0.
float4 main(float4 position : SV_POSITION, float4 scenePosition : SCENE_POSITION,
            float4 coordinate : TEXCOORD0) : SV_TARGET
{
    float4 center = Source.Sample(SourceSampler, coordinate.xy);
    if (center.a == 0) return 0;

    float3 centerColor = center.rgb / center.a;
    float sigma = 0.02 + 0.10 * Strength;
    float3 sum = 0;
    float weightSum = 0;

    [unroll]
    for (int y = -2; y <= 2; y++)
    {
        [unroll]
        for (int x = -2; x <= 2; x++)
        {
            float4 sample = Source.Sample(SourceSampler, coordinate.xy + float2(x, y) * coordinate.zw);
            float3 color = sample.rgb / max(sample.a, 0.00001);
            float3 delta = color - centerColor;
            float spatial = (x * x + y * y) / (2.0 * 1.25 * 1.25);
            float range = dot(delta, delta) / (6.0 * sigma * sigma);
            // Alpha weights exclude hidden colors; the center alpha is never blurred.
            float weight = exp(-spatial - range) * sample.a;
            sum += color * weight;
            weightSum += weight;
        }
    }

    return float4(lerp(centerColor, sum / weightSum, Strength) * center.a, center.a);
}
