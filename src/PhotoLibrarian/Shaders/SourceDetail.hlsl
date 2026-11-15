// Copyright (c) Duncan Lawler. All Rights Reserved.
Texture2D<float4> Source : register(t0);
SamplerState SourceSampler : register(s0);
float Dehaze;
float Texture;

float luminance(float3 color)
{
    return dot(color, float3(0.2126, 0.7152, 0.0722));
}

float4 main(float4 position : SV_POSITION, float4 scenePosition : SCENE_POSITION,
            float4 coordinate : TEXCOORD0) : SV_TARGET
{
    float4 center = Source.Sample(SourceSampler, coordinate.xy);
    if (center.a == 0) return 0;
    // Work in straight, gamma-encoded raster RGB, preserving the original alpha.
    float3 color = center.rgb / center.a;
    float light = luminance(color);
    float3 result = color;

    if (Dehaze != 0)
    {
        float veilSum = 0;
        float weights = 0;
        [unroll]
        for (int y = -2; y <= 2; y++)
        {
            [unroll]
            for (int x = -2; x <= 2; x++)
            {
                float4 sample = Source.Sample(SourceSampler,
                    coordinate.xy + float2(x, y) * 4 * coordinate.zw);
                float3 nearby = sample.rgb / max(sample.a, 0.00001);
                float difference = luminance(nearby) - light;
                float weight = exp(-(x*x + y*y) / 4.0 - difference*difference / 0.02) * sample.a;
                veilSum += min(nearby.r, min(nearby.g, nearby.b)) * weight;
                weights += weight;
            }
        }
        float protectHighlights = 1 - smoothstep(0.65, 0.98, light);
        // A conservative local veil, not scene-depth inference or unclipped RAW recovery.
        float veil = min(0.35, 0.5 * veilSum / weights) * protectHighlights;
        if (Dehaze > 0)
        {
            float removed = Dehaze * veil;
            result = saturate((color - removed) / (1 - removed));
        }
        else
        {
            float added = -Dehaze * 0.25 * protectHighlights;
            result = lerp(color, 1, added);
        }
    }

    if (Texture != 0)
    {
        float fineSum = 0;
        float fineWeights = 0;
        float broadSum = 0;
        float broadWeights = 0;
        float largestDifference = 0;
        [unroll]
        for (int y = -2; y <= 2; y++)
        {
            [unroll]
            for (int x = -2; x <= 2; x++)
            {
                float4 sample = Source.Sample(SourceSampler,
                    coordinate.xy + float2(x, y) * coordinate.zw);
                float nearby = luminance(sample.rgb / max(sample.a, 0.00001));
                float difference = nearby - light;
                float range = exp(-difference*difference / 0.02) * sample.a;
                float fineWeight = exp(-(x*x + y*y) / 0.98) * range;
                float broadWeight = exp(-(x*x + y*y) / 5.12) * range;
                fineSum += nearby * fineWeight;
                fineWeights += fineWeight;
                broadSum += nearby * broadWeight;
                broadWeights += broadWeight;
                largestDifference = max(largestDifference, abs(difference) * sample.a);
            }
        }
        // Difference of two smooth scales rejects DC and attenuates one-pixel noise.
        float band = fineSum / fineWeights - broadSum / broadWeights;
        float gate = (1 - smoothstep(0.12, 0.32, largestDifference))
                   * smoothstep(0.003, 0.01, abs(band))
                   * smoothstep(0, 0.08, light) * (1 - smoothstep(0.92, 1, light));
        result = saturate(result + Texture * 0.7 * band * gate);
    }
    return float4(result * center.a, center.a);
}
