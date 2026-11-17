// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Models;
using System.Text.Json;
using XmpCore;
using XmpCore.Options;

namespace PhotoLibrarian.Core.Services;

/// <summary>Reads and updates a complete version recipe without discarding opaque XMP.</summary>
internal static class PhotoVersionRecipePacket
{
    internal static PhotoVersionRecipeStatus TryRead(
        string packet, bool isRaw, out EditParameters? parameters)
    {
        parameters = null;
        var status = TryParse(packet, isRaw, out _, out var parsed);
        if (status == PhotoVersionRecipeStatus.Success)
            parameters = parsed;
        return status;
    }

    internal static PhotoVersionRecipeStatus TryUpdate(
        string packet, bool isRaw, EditParameters parameters, out string? updatedPacket)
    {
        updatedPacket = null;
        var status = TryParse(packet, isRaw, out var xmp, out _);
        if (status != PhotoVersionRecipeStatus.Success)
            return status;
        if (parameters.ToneCurves is null || parameters.ColorAdjustments is null ||
            parameters.ColorAdjustments.MixerBands is null ||
            parameters.ColorAdjustments.MixerBands.Any(band => band is null) ||
            parameters.MonochromeMix is null ||
            parameters.RetouchStrokes is null ||
            !HasFiniteValues(parameters) ||
            !Enum.IsDefined(parameters.Effect) || !Enum.IsDefined(parameters.SharpeningMode))
            return PhotoVersionRecipeStatus.InvalidMetadata;
        try
        {
            if (isRaw)
                RawToneMappingService.ValidateRecipe(parameters);
            EditParametersSerializer.WriteToXmp(xmp!, parameters);
            // Validate the written representation using the same reader consumers use.
            _ = EditParametersSerializer.ReadFromXmp(xmp!);
            updatedPacket = XmpMetaFactory.SerializeToString(
                xmp!, new SerializeOptions()).TrimStart('\uFEFF');
            return PhotoVersionRecipeStatus.Success;
        }
        catch (NotSupportedException)
        {
            return PhotoVersionRecipeStatus.Unsupported;
        }
        catch (Exception exception) when (exception is XmpException or InvalidDataException or ArgumentException)
        {
            return PhotoVersionRecipeStatus.InvalidMetadata;
        }
    }

    internal static PhotoVersionRecipeStatus ValidateReplacement(
        string originalPacket, string replacementPacket, bool isRaw)
    {
        var status = TryParse(originalPacket, isRaw, out var original, out _);
        if (status != PhotoVersionRecipeStatus.Success)
            return status;
        status = TryParse(replacementPacket, isRaw, out var replacement, out _);
        if (status != PhotoVersionRecipeStatus.Success)
            return status;

        EditParametersSerializer.ClearFromXmp(original!);
        EditParametersSerializer.ClearFromXmp(replacement!);
        var options = new SerializeOptions { Sort = true, OmitPacketWrapper = true };
        var originalOpaque = XmpMetaFactory.SerializeToString(original!, options);
        var replacementOpaque = XmpMetaFactory.SerializeToString(replacement!, options);
        return string.Equals(originalOpaque, replacementOpaque, StringComparison.Ordinal)
            ? PhotoVersionRecipeStatus.Success
            : PhotoVersionRecipeStatus.Conflict;
    }

    private static PhotoVersionRecipeStatus TryParse(
        string packet, bool isRaw, out IXmpMeta? xmp, out EditParameters? parameters)
    {
        xmp = null;
        parameters = null;
        if (string.IsNullOrWhiteSpace(packet))
            return PhotoVersionRecipeStatus.InvalidMetadata;
        try
        {
            var parsed = XmpMetaFactory.ParseFromString(packet.TrimStart('\uFEFF'));
            if (parsed.DoesPropertyExist(EditParameters.XmpNamespace, "pledit:GeometryVersion") ||
                parsed.DoesPropertyExist(EditParameters.XmpNamespace, "pledit:GeometryV1"))
                return PhotoVersionRecipeStatus.Unsupported;
            var opaqueOnly = parsed.Clone();
            EditParametersSerializer.ClearFromXmp(opaqueOnly);
            foreach (var property in parsed.Properties)
            {
                if (property.Namespace == EditParameters.XmpNamespace &&
                    !string.IsNullOrEmpty(property.Path) &&
                    property.Options.HasQualifiers &&
                    !opaqueOnly.DoesPropertyExist(property.Namespace, property.Path))
                    return PhotoVersionRecipeStatus.Unsupported;
            }
            var monochromeStatus = InspectMonochromeMix(parsed);
            if (monochromeStatus != PhotoVersionRecipeStatus.Success)
                return monochromeStatus;
            if ((parsed.DoesPropertyExist(EditParameters.XmpNamespace, "pledit:RawToneMapping") &&
                  parsed.GetPropertyString(EditParameters.XmpNamespace, "pledit:RawToneMapping") != RawToneMappingService.RecipeVersion) ||
                 (parsed.DoesPropertyExist(EditParameters.XmpNamespace, "pledit:RawDevelopment") &&
                  parsed.GetPropertyString(EditParameters.XmpNamespace, "pledit:RawDevelopment") != RawDevelopmentService.RecipeVersion))
                return PhotoVersionRecipeStatus.Unsupported;
            if (parsed.DoesPropertyExist(EditParameters.XmpNamespace, "pledit:SharpeningMode") &&
                (!Enum.TryParse<SharpeningMode>(
                    parsed.GetPropertyString(EditParameters.XmpNamespace, "pledit:SharpeningMode"), out var mode) ||
                 !Enum.IsDefined(mode)))
                return PhotoVersionRecipeStatus.Unsupported;
            var cropFields = new[] { "pledit:CropLeft", "pledit:CropTop", "pledit:CropWidth", "pledit:CropHeight" };
            var cropFieldCount = cropFields.Count(field =>
                parsed.DoesPropertyExist(EditParameters.XmpNamespace, field));
            if (cropFieldCount != 0 && cropFieldCount != cropFields.Length)
                return PhotoVersionRecipeStatus.InvalidMetadata;
            foreach (var property in new[] { "pledit:SpotRetouchV1", "pledit:ToneCurvesV1", "pledit:ColorAdjustmentsV1" })
            {
                if (!parsed.DoesPropertyExist(EditParameters.XmpNamespace, property))
                    continue;
                using var json = JsonDocument.Parse(parsed.GetPropertyString(EditParameters.XmpNamespace, property));
                if (HasDuplicateProperties(json.RootElement))
                    return PhotoVersionRecipeStatus.InvalidMetadata;
                if ((property == "pledit:ColorAdjustmentsV1" && HasUnsupportedColorFields(json.RootElement)) ||
                    (property == "pledit:ToneCurvesV1" && HasUnsupportedToneCurveFields(json.RootElement)))
                    return PhotoVersionRecipeStatus.Unsupported;
                if (property == "pledit:ColorAdjustmentsV1" &&
                    json.RootElement.ValueKind == JsonValueKind.Object &&
                    json.RootElement.TryGetProperty("MixerBands", out var bands) &&
                    bands.ValueKind == JsonValueKind.Array &&
                    bands.EnumerateArray().Any(band => band.ValueKind == JsonValueKind.Null))
                    return PhotoVersionRecipeStatus.InvalidMetadata;
            }
            if (parsed.DoesPropertyExist(EditParameters.XmpNamespace, "pledit:SpotRetouchV1"))
            {
                var retouchStatus = InspectRetouchFields(
                    parsed.GetPropertyString(EditParameters.XmpNamespace, "pledit:SpotRetouchV1"));
                if (retouchStatus != PhotoVersionRecipeStatus.Success)
                    return retouchStatus;
            }
            var recipe = EditParametersSerializer.ReadFromXmp(parsed);
            if (isRaw)
                RawToneMappingService.ValidateRecipe(recipe);
            if (!HasFiniteValues(recipe))
                return PhotoVersionRecipeStatus.InvalidMetadata;
            xmp = parsed;
            parameters = recipe;
            return PhotoVersionRecipeStatus.Success;
        }
        catch (NotSupportedException)
        {
            return PhotoVersionRecipeStatus.Unsupported;
        }
        catch (Exception exception) when (exception is XmpException or InvalidDataException or ArgumentException or JsonException)
        {
            return PhotoVersionRecipeStatus.InvalidMetadata;
        }
    }

    private static PhotoVersionRecipeStatus InspectMonochromeMix(IXmpMeta xmp)
    {
        const string versionProperty = "pledit:MonochromeMixVersion";
        const string recipeProperty = "pledit:MonochromeMixV1";
        var hasVersion = xmp.DoesPropertyExist(EditParameters.XmpNamespace, versionProperty);
        var hasRecipe = xmp.DoesPropertyExist(EditParameters.XmpNamespace, recipeProperty);
        if (hasVersion && xmp.GetPropertyString(EditParameters.XmpNamespace, versionProperty) != "1")
            return PhotoVersionRecipeStatus.Unsupported;
        if (hasVersion != hasRecipe)
            return PhotoVersionRecipeStatus.InvalidMetadata;
        if (!hasRecipe)
            return PhotoVersionRecipeStatus.Success;

        using var document = JsonDocument.Parse(
            xmp.GetPropertyString(EditParameters.XmpNamespace, recipeProperty));
        if (HasDuplicateProperties(document.RootElement))
            return PhotoVersionRecipeStatus.InvalidMetadata;
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return PhotoVersionRecipeStatus.InvalidMetadata;
        return document.RootElement.EnumerateObject().Any(property =>
            property.Name != nameof(MonochromeMix.LuminanceBands))
                ? PhotoVersionRecipeStatus.Unsupported
                : PhotoVersionRecipeStatus.Success;
    }

    private static bool HasFiniteValues(EditParameters recipe)
    {
        double[] values =
        [
            recipe.Brightness, recipe.Contrast, recipe.Exposure, recipe.RawExposureEv, recipe.RawWhitePoint,
            recipe.Highlights, recipe.Shadows, recipe.Saturation, recipe.Temperature, recipe.Tint,
            recipe.Clarity, recipe.Sharpness, recipe.NoiseReduction, recipe.UnsharpMaskAmount,
            recipe.UnsharpMaskRadius, recipe.UnsharpMaskThreshold, recipe.BlackPoint, recipe.WhitePoint,
            recipe.Midtones, recipe.RotationAngle
        ];
        return values.All(double.IsFinite) &&
            (recipe.Crop is null ||
             (double.IsFinite(recipe.Crop.Left) && double.IsFinite(recipe.Crop.Top) &&
              double.IsFinite(recipe.Crop.Width) && double.IsFinite(recipe.Crop.Height)));
    }

    private static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value))
                    return true;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                if (HasDuplicateProperties(item))
                    return true;
        }
        return false;
    }

    private static bool HasUnsupportedColorFields(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var property in root.EnumerateObject())
        {
            switch (property.Name)
            {
                case nameof(ColorAdjustments.MixerBands):
                    if (property.Value.ValueKind == JsonValueKind.Array &&
                        property.Value.EnumerateArray().Any(band => HasUnknownFields(band,
                            nameof(ColorMixerBandAdjustment.HueShiftDegrees),
                            nameof(ColorMixerBandAdjustment.Saturation),
                            nameof(ColorMixerBandAdjustment.Luminance))))
                        return true;
                    break;
                case nameof(ColorAdjustments.Shadows):
                case nameof(ColorAdjustments.Midtones):
                case nameof(ColorAdjustments.Highlights):
                    if (HasUnknownFields(property.Value,
                        nameof(ColorGradingWheel.HueDegrees), nameof(ColorGradingWheel.Amount)))
                        return true;
                    break;
                case nameof(ColorAdjustments.Blending):
                case nameof(ColorAdjustments.Balance):
                    break;
                default:
                    return true;
            }
        }
        return false;
    }

    private static bool HasUnsupportedToneCurveFields(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name is not (nameof(ToneCurves.Rgb) or nameof(ToneCurves.Red) or
                nameof(ToneCurves.Green) or nameof(ToneCurves.Blue)))
                return true;
            if (property.Value.ValueKind == JsonValueKind.Array &&
                property.Value.EnumerateArray().Any(point => HasUnknownFields(point,
                    nameof(ToneCurvePoint.X), nameof(ToneCurvePoint.Y))))
                return true;
        }
        return false;
    }

    private static bool HasUnknownFields(JsonElement element, string first, string second, string? third = null) =>
        element.ValueKind == JsonValueKind.Object &&
        element.EnumerateObject().Any(property =>
            property.Name != first && property.Name != second && property.Name != third);

    private static PhotoVersionRecipeStatus InspectRetouchFields(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return PhotoVersionRecipeStatus.InvalidMetadata;
        foreach (var stroke in document.RootElement.EnumerateArray())
        {
            if (stroke.ValueKind != JsonValueKind.Object)
                return PhotoVersionRecipeStatus.InvalidMetadata;
            foreach (var property in stroke.EnumerateObject())
            {
                if (property.Name is not ("ImageWidth" or "ImageHeight" or "Radius" or "Points"))
                    return PhotoVersionRecipeStatus.Unsupported;
                if (property.Name != "Points" || property.Value.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var point in property.Value.EnumerateArray())
                {
                    if (point.ValueKind != JsonValueKind.Object)
                        return PhotoVersionRecipeStatus.InvalidMetadata;
                    foreach (var coordinate in point.EnumerateObject())
                    {
                        if (coordinate.Name is not ("X" or "Y"))
                            return PhotoVersionRecipeStatus.Unsupported;
                    }
                }
            }
        }
        return PhotoVersionRecipeStatus.Success;
    }
}
