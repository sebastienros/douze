using Fluid;

namespace Douze
{
    internal partial class Options
    {
        public string SourcePath { get; set; } = ".";
        public string OutputPath { get; set; } = "_site";
        public string DataFolder { get; set; } = "_data";
        public string IncludesFolder { get; set; } = "_includes";
        public FluidParser FluidParser { get; } = new ();
        public Dictionary<string, IFileProcessor> ContentMappings { get; } = new();
        public Dictionary<string, IFileProcessor> DataMappings { get; } = new();
        public Dictionary<string, object?> PropertyBag { get; } = new();
    }
}
