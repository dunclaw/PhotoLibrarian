using XmpCore;
using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.Core.Services;

/// <summary>
/// Writes face regions + names to XMP using the MWG Region Schema standard.
/// This is the same format used by Windows Photo Gallery, Picasa, digiKam, etc.
/// Schema: mwg-rs:Regions/mwg-rs:RegionList with mwg-rs:Area and stArea:* properties.
/// </summary>
public static class MwgRegionWriter
{
    private const string MwgRs = "http://www.metadataworkinggroup.com/schemas/regions/";
    private const string StArea = "http://ns.adobe.com/xmp/sType/Area#";
    private const string StDim = "http://ns.adobe.com/xmp/sType/Dimensions#";

    /// <summary>
    /// Writes face regions to an XMP sidecar file following MWG Region Schema.
    /// </summary>
    public static async Task WriteFaceRegionsAsync(string imagePath, IEnumerable<FaceRegion> faces, int imageWidth, int imageHeight)
    {
        await Task.Run(() =>
        {
            var sidecarPath = Path.ChangeExtension(imagePath, ".xmp");
            IXmpMeta xmp;

            try
            {
                if (File.Exists(sidecarPath))
                {
                    var xml = File.ReadAllText(sidecarPath);
                    xmp = XmpMetaFactory.ParseFromString(xml);
                }
                else
                {
                    xmp = XmpMetaFactory.Create();
                }
            }
            catch
            {
                xmp = XmpMetaFactory.Create();
            }

            XmpMetaFactory.SchemaRegistry.RegisterNamespace(MwgRs, "mwg-rs");
            XmpMetaFactory.SchemaRegistry.RegisterNamespace(StArea, "stArea");
            XmpMetaFactory.SchemaRegistry.RegisterNamespace(StDim, "stDim");

            // Set applied-to dimensions
            var regionsPath = "mwg-rs:Regions";
            try { xmp.DeleteProperty(MwgRs, regionsPath); } catch { }

            xmp.SetStructField(MwgRs, regionsPath, MwgRs, "mwg-rs:AppliedToDimensions", null,
                new XmpCore.Options.PropertyOptions { IsStruct = true });
            xmp.SetStructField(MwgRs, $"{regionsPath}/mwg-rs:AppliedToDimensions",
                StDim, "stDim:w", imageWidth.ToString());
            xmp.SetStructField(MwgRs, $"{regionsPath}/mwg-rs:AppliedToDimensions",
                StDim, "stDim:h", imageHeight.ToString());
            xmp.SetStructField(MwgRs, $"{regionsPath}/mwg-rs:AppliedToDimensions",
                StDim, "stDim:unit", "pixel");

            // Write RegionList array
            int index = 1;
            foreach (var face in faces)
            {
                var itemPath = $"{regionsPath}/mwg-rs:RegionList[{index}]";

                xmp.AppendArrayItem(MwgRs, $"{regionsPath}/mwg-rs:RegionList",
                    new XmpCore.Options.PropertyOptions { IsArray = true, IsArrayOrdered = true },
                    null, new XmpCore.Options.PropertyOptions { IsStruct = true });

                // Name
                if (!string.IsNullOrEmpty(face.PersonName))
                {
                    xmp.SetStructField(MwgRs, itemPath, MwgRs, "mwg-rs:Name", face.PersonName);
                }

                // Type = Face
                xmp.SetStructField(MwgRs, itemPath, MwgRs, "mwg-rs:Type", "Face");

                // Area (normalized coordinates - center point + dimensions)
                xmp.SetStructField(MwgRs, itemPath, MwgRs, "mwg-rs:Area", null,
                    new XmpCore.Options.PropertyOptions { IsStruct = true });

                var areaPath = $"{itemPath}/mwg-rs:Area";
                // MWG uses center-point + width/height (all normalized 0-1)
                double cx = face.X + face.Width / 2;
                double cy = face.Y + face.Height / 2;

                xmp.SetStructField(MwgRs, areaPath, StArea, "stArea:x", cx.ToString("F6"));
                xmp.SetStructField(MwgRs, areaPath, StArea, "stArea:y", cy.ToString("F6"));
                xmp.SetStructField(MwgRs, areaPath, StArea, "stArea:w", face.Width.ToString("F6"));
                xmp.SetStructField(MwgRs, areaPath, StArea, "stArea:h", face.Height.ToString("F6"));
                xmp.SetStructField(MwgRs, areaPath, StArea, "stArea:unit", "normalized");

                index++;
            }

            var serialized = XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions());
            File.WriteAllText(sidecarPath, serialized);
        });
    }
}
