Texture2D<float4> Source : register(t0);
SamplerState SourceSampler : register(s0);

float4 Band0;
float4 Band1;
float4 Band2;
float4 Band3;
float4 Band4;
float4 Band5;
float4 Band6;
float4 Band7;
float4 ShadowGrade;
float4 MidtoneGrade;
float4 HighlightGrade;
float Blending;
float Balance;

static const float Pi = 3.14159265358979323846;

float GetHueCenter(int index)
{
    if (index == 0) return 29.0;
    if (index == 1) return 57.0;
    if (index == 2) return 110.0;
    if (index == 3) return 142.0;
    if (index == 4) return 195.0;
    if (index == 5) return 264.0;
    if (index == 6) return 293.0;
    return 328.0;
}

float4 GetBand(int index)
{
    if (index == 0) return Band0;
    if (index == 1) return Band1;
    if (index == 2) return Band2;
    if (index == 3) return Band3;
    if (index == 4) return Band4;
    if (index == 5) return Band5;
    if (index == 6) return Band6;
    return Band7;
}

float3 ToLinearSrgb(float3 color)
{
    float3 low = color / 12.92;
    float3 high = pow((color + 0.055) / 1.055, 2.4);
    return lerp(high, low, step(color, 0.04045));
}

float3 ToSrgb(float3 color)
{
    color = saturate(color);
    float3 low = color * 12.92;
    float3 high = 1.055 * pow(color, 1.0 / 2.4) - 0.055;
    return lerp(high, low, step(color, 0.0031308));
}

float3 LinearSrgbToOkLab(float3 color)
{
    float3 lms;
    lms.x = 0.4122214708 * color.r + 0.5363325363 * color.g + 0.0514459929 * color.b;
    lms.y = 0.2119034982 * color.r + 0.6806995451 * color.g + 0.1073969566 * color.b;
    lms.z = 0.0883024619 * color.r + 0.2817188376 * color.g + 0.6299787005 * color.b;
    lms = pow(max(lms, 0), 1.0 / 3.0);

    return float3(
        0.2104542553 * lms.x + 0.7936177850 * lms.y - 0.0040720468 * lms.z,
        1.9779984951 * lms.x - 2.4285922050 * lms.y + 0.4505937099 * lms.z,
        0.0259040371 * lms.x + 0.7827717662 * lms.y - 0.8086757660 * lms.z);
}

float3 OkLabToLinearSrgb(float3 color)
{
    float l = color.x + 0.3963377774 * color.y + 0.2158037573 * color.z;
    float m = color.x - 0.1055613458 * color.y - 0.0638541728 * color.z;
    float s = color.x - 0.0894841775 * color.y - 1.2914855480 * color.z;
    l = l * l * l;
    m = m * m * m;
    s = s * s * s;

    return float3(
        4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
        -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
        -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s);
}

float HueDistance(float first, float second)
{
    float distance = abs(first - second);
    return min(distance, 360.0 - distance);
}

float SmoothStepSafe(float edge0, float edge1, float value)
{
    float t = saturate((value - edge0) / max(edge1 - edge0, 0.00001));
    return t * t * (3.0 - 2.0 * t);
}

float4 main(float4 position : SV_POSITION, float4 scenePosition : SCENE_POSITION,
            float4 coordinate : TEXCOORD0) : SV_TARGET
{
    float4 sample = Source.Sample(SourceSampler, coordinate.xy);
    if (sample.a == 0) return 0;

    float3 encoded = saturate(sample.rgb / sample.a);
    float3 lab = LinearSrgbToOkLab(ToLinearSrgb(encoded));
    float chroma = length(lab.yz);
    if (chroma > 0.00001)
    {
        float hue = atan2(lab.z, lab.y) * 180.0 / Pi;
        if (hue < 0) hue += 360.0;

        float hueShift = 0;
        float saturationShift = 0;
        float luminanceShift = 0;
        [unroll]
        for (int bandIndex = 0; bandIndex < 8; bandIndex++)
        {
            float center = GetHueCenter(bandIndex);
            float distance = HueDistance(hue, center);
            float weight = exp(-0.5 * (distance / 30.0) * (distance / 30.0));
            float4 band = GetBand(bandIndex);
            hueShift += band.x * weight;
            saturationShift += band.y * weight;
            luminanceShift += band.z * weight;
        }

        hue += clamp(hueShift, -180.0, 180.0);
        chroma *= max(0, 1.0 + clamp(saturationShift, -1.0, 1.0));
        lab.x = saturate(lab.x + clamp(luminanceShift, -1.0, 1.0) * 0.15);
        lab.y = cos(hue * Pi / 180.0) * chroma;
        lab.z = sin(hue * Pi / 180.0) * chroma;
    }

    float pivot = 0.5 + Balance * 0.25;
    float halfBlend = 0.02 + Blending * 0.25;
    float shadowEnd = clamp(pivot - halfBlend, 0.001, 0.999);
    float highlightStart = clamp(pivot + halfBlend, 0.001, 0.999);
    float shadowWeight = 1.0 - SmoothStepSafe(0.0, shadowEnd, lab.x);
    float highlightWeight = SmoothStepSafe(highlightStart, 1.0, lab.x);
    float midtoneWeight = max(0, 1.0 - shadowWeight - highlightWeight);
    float totalWeight = max(shadowWeight + midtoneWeight + highlightWeight, 0.00001);
    lab.y += (shadowWeight * ShadowGrade.x + midtoneWeight * MidtoneGrade.x +
              highlightWeight * HighlightGrade.x) / totalWeight;
    lab.z += (shadowWeight * ShadowGrade.y + midtoneWeight * MidtoneGrade.y +
              highlightWeight * HighlightGrade.y) / totalWeight;

    float3 rendered = ToSrgb(OkLabToLinearSrgb(lab)) * sample.a;
    return float4(rendered, sample.a);
}
