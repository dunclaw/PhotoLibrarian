// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using XmpCore;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class PhotoVersionRecipePacketTests
{
    [Fact]
    public void UpdatePreservesOpaquePropertiesAndColorAdjustments()
    {
        const string futureNamespace = "https://photolibrarian.test/future/";
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(futureNamespace, "future");
        var xmp = XmpMetaFactory.Create();
        xmp.SetProperty(futureNamespace, "future:opaque", "keep");
        xmp.SetQualifier(futureNamespace, "future:opaque", futureNamespace, "future:detail", "also keep");
        var original = new EditParameters { Exposure = 0.25 };
        original.ColorAdjustments.MixerBands[2].Saturation = 0.6;
        EditParametersSerializer.WriteToXmp(xmp, original);
        var packet = XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions());

        Assert.Equal(PhotoVersionRecipeStatus.Success,
            PhotoVersionRecipePacket.TryRead(packet, false, out var recipe));
        recipe!.Exposure = -0.5;
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            PhotoVersionRecipePacket.TryUpdate(packet, false, recipe, out var updated));
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            PhotoVersionRecipePacket.TryRead(updated!, false, out var result));
        Assert.Equal(-0.5, result!.Exposure);
        Assert.Equal(0.6, result.ColorAdjustments.MixerBands[2].Saturation);
        Assert.Equal("keep", XmpMetaFactory.ParseFromString(updated!)
            .GetPropertyString(futureNamespace, "future:opaque"));
        Assert.Equal("also keep", XmpMetaFactory.ParseFromString(updated!)
            .GetQualifier(futureNamespace, "future:opaque", futureNamespace, "future:detail").Value);
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            PhotoVersionRecipePacket.ValidateReplacement(packet, updated!, false));
        var changed = XmpMetaFactory.ParseFromString(updated!);
        changed.DeleteProperty(futureNamespace, "future:opaque");
        var missingOpaque = XmpMetaFactory.SerializeToString(changed, new XmpCore.Options.SerializeOptions());
        Assert.Equal(PhotoVersionRecipeStatus.Conflict,
            PhotoVersionRecipePacket.ValidateReplacement(packet, missingOpaque, false));
    }

    [Fact]
    public void UnsupportedOrMalformedRecipesCannotBeUpdated()
    {
        var xmp = XmpMetaFactory.Create();
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(EditParameters.XmpNamespace, EditParameters.XmpPrefix);
        xmp.SetProperty(EditParameters.XmpNamespace, "pledit:ColorAdjustmentsVersion", "2");
        var unsupported = XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions());

        Assert.Equal(PhotoVersionRecipeStatus.Unsupported,
            PhotoVersionRecipePacket.TryUpdate(unsupported, false, new EditParameters(), out var updated));
        Assert.Null(updated);
        Assert.Equal(PhotoVersionRecipeStatus.InvalidMetadata,
            PhotoVersionRecipePacket.TryRead("<broken", false, out var recipe));
        Assert.Null(recipe);
    }

    [Fact]
    public void UnsupportedRawDevelopmentAndInvalidRawInputsAreRejected()
    {
        var xmp = XmpMetaFactory.Create();
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(EditParameters.XmpNamespace, EditParameters.XmpPrefix);
        xmp.SetProperty(EditParameters.XmpNamespace, "pledit:RawDevelopment", "future");
        var packet = XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions());
        Assert.Equal(PhotoVersionRecipeStatus.Unsupported,
            PhotoVersionRecipePacket.TryRead(packet, true, out _));
        Assert.Equal(PhotoVersionRecipeStatus.Unsupported,
            PhotoVersionRecipePacket.TryRead(packet, false, out _));
        xmp.DeleteProperty(EditParameters.XmpNamespace, "pledit:RawDevelopment");
        packet = XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions());
        Assert.Equal(PhotoVersionRecipeStatus.InvalidMetadata,
            PhotoVersionRecipePacket.TryUpdate(packet, true,
                new EditParameters { RawWhitePoint = double.NaN }, out var updated));
        Assert.Null(updated);
        Assert.Equal(PhotoVersionRecipeStatus.InvalidMetadata,
            PhotoVersionRecipePacket.TryUpdate(packet, false,
                new EditParameters { Exposure = double.NaN }, out _));
    }

    [Fact]
    public void UnknownSharpeningModeCannotBeReplacedWithLegacyDefaults()
    {
        var xmp = XmpMetaFactory.Create();
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(EditParameters.XmpNamespace, EditParameters.XmpPrefix);
        xmp.SetProperty(EditParameters.XmpNamespace, "pledit:SharpeningMode", "FutureMode");
        var packet = XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions());

        Assert.Equal(PhotoVersionRecipeStatus.Unsupported,
            PhotoVersionRecipePacket.TryUpdate(packet, false, new EditParameters(), out var updated));
        Assert.Null(updated);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void UnintegratedMonochromeRecipeCannotRenderAsNeutral(bool hasVersion, bool hasData)
    {
        var xmp = XmpMetaFactory.Create();
        EditParametersSerializer.WriteToXmp(xmp, new EditParameters());
        if (hasVersion)
            xmp.SetProperty(EditParameters.XmpNamespace, "pledit:MonochromeMixVersion", "1");
        if (hasData)
            xmp.SetProperty(EditParameters.XmpNamespace, "pledit:MonochromeMixV1",
                """{"LuminanceBands":[0.5,0,0,0,0,0,0,0]}""");
        var packet = XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions());

        Assert.Equal(PhotoVersionRecipeStatus.Unsupported,
            PhotoVersionRecipePacket.TryRead(packet, false, out _));
        Assert.Equal(PhotoVersionRecipeStatus.Unsupported,
            PhotoVersionRecipePacket.TryUpdate(packet, false, new EditParameters(), out _));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void UnintegratedGeometryRecipeCannotRenderAsNeutral(bool hasVersion, bool hasData)
    {
        var xmp = XmpMetaFactory.Create();
        EditParametersSerializer.WriteToXmp(xmp, new EditParameters());
        if (hasVersion)
            xmp.SetProperty(EditParameters.XmpNamespace, "pledit:GeometryVersion", "1");
        if (hasData)
            xmp.SetProperty(EditParameters.XmpNamespace, "pledit:GeometryV1",
                """{"HorizontalPerspective":0.1,"VerticalPerspective":0,"Aspect":0,"Scale":1,"OffsetX":0,"OffsetY":0}""");
        var packet = XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions());

        Assert.Equal(PhotoVersionRecipeStatus.Unsupported,
            PhotoVersionRecipePacket.TryRead(packet, false, out _));
        Assert.Equal(PhotoVersionRecipeStatus.Unsupported,
            PhotoVersionRecipePacket.TryUpdate(packet, false, new EditParameters(), out _));
    }

    [Fact]
    public void QualifiersOnKnownRecipeFieldsAreNotSilentlyDiscarded()
    {
        const string futureNamespace = "https://photolibrarian.test/qualifiers/";
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(futureNamespace, "futurequal");
        var xmp = XmpMetaFactory.Create();
        EditParametersSerializer.WriteToXmp(xmp, new EditParameters());
        xmp.SetQualifier(EditParameters.XmpNamespace, "pledit:Exposure",
            futureNamespace, "futurequal:meaning", "keep");
        var packet = XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions());

        Assert.Equal(PhotoVersionRecipeStatus.Unsupported,
            PhotoVersionRecipePacket.TryRead(packet, false, out _));
        Assert.Equal(PhotoVersionRecipeStatus.Unsupported,
            PhotoVersionRecipePacket.TryUpdate(packet, false, new EditParameters(), out _));
    }

    [Theory]
    [InlineData("CropTop")]
    [InlineData("CropLeft")]
    public void IncompleteCropCannotBeReplacedWithDefaults(string field)
    {
        var xmp = XmpMetaFactory.Create();
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(EditParameters.XmpNamespace, EditParameters.XmpPrefix);
        xmp.SetPropertyDouble(EditParameters.XmpNamespace, $"pledit:{field}", 0.25);
        var packet = XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions());

        Assert.Equal(PhotoVersionRecipeStatus.InvalidMetadata,
            PhotoVersionRecipePacket.TryUpdate(packet, false, new EditParameters(), out var updated));
        Assert.Null(updated);
    }

    [Fact]
    public void DuplicateFieldsInColorRecipeAreRejected()
    {
        var xmp = XmpMetaFactory.Create();
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(EditParameters.XmpNamespace, EditParameters.XmpPrefix);
        xmp.SetProperty(EditParameters.XmpNamespace, "pledit:ColorAdjustmentsVersion", "1");
        xmp.SetProperty(EditParameters.XmpNamespace, "pledit:ColorAdjustmentsV1",
            """{"Blending":0.5,"Blending":0.7}""");
        var packet = XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions());

        Assert.Equal(PhotoVersionRecipeStatus.InvalidMetadata,
            PhotoVersionRecipePacket.TryUpdate(packet, false, new EditParameters(), out var updated));
        Assert.Null(updated);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnknownNestedAdjustmentFieldsAreUnsupported(bool color)
    {
        var xmp = XmpMetaFactory.Create();
        var parameters = new EditParameters();
        parameters.ColorAdjustments.MixerBands[0].Saturation = 0.1;
        parameters.ToneCurves.Red[1] = new ToneCurvePoint(1, 0.9);
        EditParametersSerializer.WriteToXmp(xmp, parameters);
        var property = color ? "pledit:ColorAdjustmentsV1" : "pledit:ToneCurvesV1";
        var json = System.Text.Json.Nodes.JsonNode.Parse(
            xmp.GetPropertyString(EditParameters.XmpNamespace, property))!;
        if (color)
            json["MixerBands"]![0]!["FutureField"] = 1;
        else
            json["Red"]![0]!["FutureField"] = 1;
        xmp.SetProperty(EditParameters.XmpNamespace, property, json.ToJsonString());
        var packet = XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions());

        Assert.Equal(PhotoVersionRecipeStatus.Unsupported,
            PhotoVersionRecipePacket.TryRead(packet, false, out _));
        Assert.Equal(PhotoVersionRecipeStatus.Unsupported,
            PhotoVersionRecipePacket.TryUpdate(packet, false, parameters, out _));
    }

    [Fact]
    public void NullColorMixerBandIsInvalidInsteadOfCrashingOrDroppingData()
    {
        var xmp = XmpMetaFactory.Create();
        var parameters = new EditParameters();
        parameters.ColorAdjustments.MixerBands[0].Saturation = 0.1;
        EditParametersSerializer.WriteToXmp(xmp, parameters);
        var json = System.Text.Json.Nodes.JsonNode.Parse(
            xmp.GetPropertyString(EditParameters.XmpNamespace, "pledit:ColorAdjustmentsV1"))!;
        json["MixerBands"]![0] = null;
        xmp.SetProperty(EditParameters.XmpNamespace, "pledit:ColorAdjustmentsV1", json.ToJsonString());
        var packet = XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions());

        Assert.Equal(PhotoVersionRecipeStatus.InvalidMetadata,
            PhotoVersionRecipePacket.TryRead(packet, false, out _));
        Assert.Equal(PhotoVersionRecipeStatus.InvalidMetadata,
            PhotoVersionRecipePacket.TryUpdate(packet, false, parameters, out _));
    }

    [Fact]
    public void NullMixerBandInUpdatedRecipeIsRejected()
    {
        var xmp = XmpMetaFactory.Create();
        EditParametersSerializer.WriteToXmp(xmp, new EditParameters());
        var packet = XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions());
        var parameters = new EditParameters();
        parameters.ColorAdjustments.MixerBands[0] = null!;

        Assert.Equal(PhotoVersionRecipeStatus.InvalidMetadata,
            PhotoVersionRecipePacket.TryUpdate(packet, false, parameters, out var updated));
        Assert.Null(updated);
    }

    [Fact]
    public void UnknownRetouchDataIsNotSilentlyDropped()
    {
        var xmp = XmpMetaFactory.Create();
        EditParametersSerializer.WriteToXmp(xmp, new EditParameters
        {
            RetouchStrokes = [new RetouchStroke(100, 100, 5, [new RetouchPoint(20, 20)])]
        });
        var strokes = System.Text.Json.Nodes.JsonNode.Parse(
            xmp.GetPropertyString(EditParameters.XmpNamespace, "pledit:SpotRetouchV1"))!;
        strokes[0]!["future"] = "preserve";
        xmp.SetProperty(EditParameters.XmpNamespace, "pledit:SpotRetouchV1", strokes.ToJsonString());
        var packet = XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions());

        Assert.Equal(PhotoVersionRecipeStatus.Unsupported,
            PhotoVersionRecipePacket.TryUpdate(packet, false, new EditParameters(), out var updated));
        Assert.Null(updated);
        xmp.SetProperty(EditParameters.XmpNamespace, "pledit:SpotRetouchV1",
            """[{"ImageWidth":100,"ImageHeight":100,"Radius":5,"Radius":8,"Points":[{"X":20,"Y":20}]}]""");
        packet = XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions());
        Assert.Equal(PhotoVersionRecipeStatus.InvalidMetadata,
            PhotoVersionRecipePacket.TryRead(packet, false, out _));
    }

    [Fact]
    public void UpdatingCopyRecipeLeavesMainAndOpaqueDataIntact()
    {
        const string futureNamespace = "https://photolibrarian.test/future/";
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(futureNamespace, "future");
        var xmp = XmpMetaFactory.Create();
        xmp.SetProperty(futureNamespace, "future:opaque", "keep");
        var parameters = new EditParameters { Exposure = 0.25 };
        parameters.ColorAdjustments.MixerBands[3].Luminance = -0.5;
        EditParametersSerializer.WriteToXmp(xmp, parameters);
        var packet = XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions());
        var mainId = Guid.NewGuid();
        var copyId = Guid.NewGuid();
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryCreate(
            Guid.NewGuid(), "pixels-v1:fixture", mainId, "Original", packet,
            copyId, "Copy", out var value));
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryRead(value!, out var document));
        parameters.Exposure = -0.75;
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            PhotoVersionRecipePacket.TryUpdate(packet, false, parameters, out var updatedPacket));
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            document!.TryUpdateRecipe(copyId, document.Revision, updatedPacket!, out var updatedFamily));
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            PhotoVersionDocument.TryRead(updatedFamily!, out var reread));
        Assert.Equal(PhotoVersionRecipeStatus.Success, reread!.TryGetRecipe(mainId, out var main));
        Assert.Equal(packet, main);
        Assert.Equal(PhotoVersionRecipeStatus.Success, reread.TryGetRecipe(copyId, out var copy));
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionRecipePacket.TryRead(copy!, false, out var copyRecipe));
        Assert.Equal(-0.75, copyRecipe!.Exposure);
        Assert.Equal(-0.5, copyRecipe.ColorAdjustments.MixerBands[3].Luminance);
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            PhotoVersionRecipePacket.ValidateReplacement(packet, copy!, false));
    }
}
