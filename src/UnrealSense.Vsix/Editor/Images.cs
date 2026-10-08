using Microsoft.VisualStudio.Core.Imaging;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.Text.Adornments;

namespace UnrealSense.Extension.Editor
{
    internal static class Images
    {
        public static ImageId ToImageId(this ImageMoniker moniker) => new ImageId(moniker.Guid, moniker.Id);

        public static readonly ImageElement Specifier = new ImageElement(KnownMonikers.Attribute.ToImageId(), "Specifier");
        public static readonly ImageElement Meta = new ImageElement(KnownMonikers.Reference.ToImageId(), "Metadata");
        public static readonly ImageElement Value = new ImageElement(KnownMonikers.EnumerationItemPublic.ToImageId(), "Value");
        public static readonly ImageElement Category = new ImageElement(KnownMonikers.FolderClosed.ToImageId(), "Category");
        public static readonly ImageElement Function = new ImageElement(KnownMonikers.MethodPublic.ToImageId(), "Function");
        public static readonly ImageElement Property = new ImageElement(KnownMonikers.PropertyPublic.ToImageId(), "Property");
        public static readonly ImageElement Class = new ImageElement(KnownMonikers.ClassPublic.ToImageId(), "Class");
        public static readonly ImageElement Module = new ImageElement(KnownMonikers.Module.ToImageId(), "Module");
    }
}
