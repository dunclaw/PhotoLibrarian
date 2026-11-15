// Copyright (c) Duncan Lawler. All Rights Reserved.
Texture2D<float4> Source : register(t0);
SamplerState SourceSampler : register(s0);
float2 OutputSize;
float2 OutputOrigin;
float2 PixelToOutputScale;
float4 Vignette;
float3 Grain;
float2 SeedParts;

float smoothRange(float start, float end, float value)
{
    if (start == end) return value >= end ? 1 : 0;
    float t = saturate((value - start) / (end - start));
    return t * t * (3 - 2 * t);
}

float tentIntegral(float value)
{
    if (value <= -1) return 0;
    if (value < 0) return (value + 1) * (value + 1) * 0.5;
    if (value < 1) return 1 - (1 - value) * (1 - value) * 0.5;
    return 1;
}

float hashNoise(int x, int y, uint seed)
{
    uint hash = (uint)x * 0x8da6b343u ^ (uint)y * 0xd8163841u ^ seed;
    hash ^= hash >> 16;
    hash *= 0x7feb352du;
    hash ^= hash >> 15;
    hash *= 0x846ca68bu;
    hash ^= hash >> 16;
    return (float)hash / 4294967295.0 * 2 - 1;
}

float averageNoise(float2 center, float2 footprint, uint seed)
{
    float2 first = center - footprint * 0.5;
    float2 last = center + footprint * 0.5;
    int2 start = (int2)floor(first);
    int2 end = (int2)ceil(last);
    float sum = 0;
    [loop]
    for (int y = start.y; y <= end.y; y++)
    {
        float wy = (tentIntegral(last.y - y) - tentIntegral(first.y - y)) / footprint.y;
        [loop]
        for (int x = start.x; x <= end.x; x++)
        {
            float wx = (tentIntegral(last.x - x) - tentIntegral(first.x - x)) / footprint.x;
            sum += hashNoise(x, y, seed) * wx * wy;
        }
    }
    return sum;
}

float4 main(float4 position : SV_POSITION, float4 scenePosition : SCENE_POSITION,
            float4 coordinate : TEXCOORD0) : SV_TARGET
{
    float4 original = Source.Sample(SourceSampler, coordinate.xy);
    if (original.a == 0) return 0;
    float3 color = original.rgb / original.a;
    float light = dot(color, float3(0.2126, 0.7152, 0.0722));
    float2 globalCenter = scenePosition.xy * PixelToOutputScale + OutputOrigin;
    float shortSide = min(OutputSize.x, OutputSize.y);
    float2 axes = lerp(OutputSize, shortSide, Vignette.z) * 0.5;
    float radius = length((globalCenter - OutputSize * 0.5) / axes) / sqrt(2.0);
    float mask = smoothRange(1 - 0.9 * Vignette.y, 1, radius);
    if (Vignette.x < 0)
    {
        float protection = 1 - Vignette.w * smoothRange(0.6, 1, light);
        color *= 1 + 0.8 * Vignette.x * mask * protection;
    }
    else
    {
        color += (1 - color) * (0.8 * Vignette.x * mask);
    }
    if (Grain.x > 0)
    {
        uint seed = (uint)SeedParts.x | ((uint)SeedParts.y << 16);
        float cell = shortSide * (0.0005 + 0.0095 * Grain.y);
        float2 center = globalCenter / cell;
        float2 footprint = PixelToOutputScale / cell;
        float coarse = averageNoise(center, footprint, seed);
        float fine = 0;
        if (Grain.z > 0)
            fine = averageNoise(center * 2, footprint * 2, seed ^ 0x9e3779b9u);
        float noise = (coarse + Grain.z * 0.5 * fine) / (1 + Grain.z * 0.5);
        color += Grain.x * 0.07 * noise;
    }
    return float4(saturate(color) * original.a, original.a);
}
