Texture2D<float4> Source : register(t0);
SamplerState SourceSampler : register(s0);
float Threshold;

float3 Shrink(float3 coefficient)
{
    float magnitude = max(abs(coefficient.r), max(abs(coefficient.g), abs(coefficient.b)));
    return coefficient * (max(magnitude - Threshold, 0) / max(magnitude, 0.00001));
}

float3 Reconstruct(
    float3 topLeft,
    float3 topRight,
    float3 bottomLeft,
    float3 bottomRight,
    float horizontalSign,
    float verticalSign)
{
    float3 low = (topLeft + topRight + bottomLeft + bottomRight) * 0.5;
    float3 horizontal = (topLeft - topRight + bottomLeft - bottomRight) * 0.5;
    float3 vertical = (topLeft + topRight - bottomLeft - bottomRight) * 0.5;
    float3 diagonal = (topLeft - topRight - bottomLeft + bottomRight) * 0.5;
    return 0.5 * (low + horizontalSign * Shrink(horizontal) +
        verticalSign * Shrink(vertical) +
        horizontalSign * verticalSign * Shrink(diagonal));
}

float3 SampleColor(float2 coordinate)
{
    float4 sample = Source.Sample(SourceSampler, coordinate);
    return sample.rgb / max(sample.a, 0.00001);
}

float4 main(float4 position : SV_POSITION, float4 scenePosition : SCENE_POSITION,
            float4 coordinate : TEXCOORD0) : SV_TARGET
{
    float4 center = Source.Sample(SourceSampler, coordinate.xy);
    if (center.a == 0) return 0;

    float2 step = coordinate.zw;
    float2 left = coordinate.xy - float2(step.x, 0);
    float2 right = coordinate.xy + float2(step.x, 0);

    float3 topLeft = SampleColor(left - float2(0, step.y));
    float3 top = SampleColor(coordinate.xy - float2(0, step.y));
    float3 topRight = SampleColor(right - float2(0, step.y));
    float3 middleLeft = SampleColor(left);
    float3 middleRight = SampleColor(right);
    float3 bottomLeft = SampleColor(left + float2(0, step.y));
    float3 bottom = SampleColor(coordinate.xy + float2(0, step.y));
    float3 bottomRight = SampleColor(right + float2(0, step.y));

    float3 centerColor = center.rgb / center.a;
    float3 upperLeft = Reconstruct(topLeft, top, middleLeft, centerColor, -1, -1);
    float3 upperRight = Reconstruct(top, topRight, centerColor, middleRight, 1, -1);
    float3 lowerLeft = Reconstruct(middleLeft, centerColor, bottomLeft, bottom, -1, 1);
    float3 lowerRight = Reconstruct(centerColor, middleRight, bottom, bottomRight, 1, 1);
    float3 reconstructed = (upperLeft + upperRight + lowerLeft + lowerRight) * 0.25;

    return float4(saturate(reconstructed) * center.a, center.a);
}
