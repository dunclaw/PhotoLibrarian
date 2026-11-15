Texture2D<float4> Source : register(t0);
SamplerState SourceSampler : register(s0);
float ShowShadows;
float ShowHighlights;

float4 main(float4 position : SV_POSITION, float4 scenePosition : SCENE_POSITION,
            float4 sourceCoordinate : TEXCOORD0) : SV_TARGET
{
    float4 pixel = Source.Sample(SourceSampler, sourceCoordinate.xy);
    if (pixel.a <= 0) return pixel;
    float3 rgb = saturate(pixel.rgb / pixel.a);
    // Endpoints of the displayed, rounded 8-bit SDR channels, not sensor saturation.
    float maximum = max(rgb.r, max(rgb.g, rgb.b));
    if (ShowHighlights > 0 && maximum >= 254.5 / 255.0)
        return float4(pixel.a, 0, 0, pixel.a);
    if (ShowShadows > 0 && maximum <= 0.5 / 255.0)
        return float4(0, 0, pixel.a, pixel.a);
    return pixel;
}
