using System.Xml.Linq;
using Xunit;

namespace UniversalConverter.Core.Tests;

public sealed class ArchitectureTests
{
    [Theory]
    [InlineData("Domain", "")]
    [InlineData("Contracts", "Domain")]
    [InlineData("Application", "Contracts,Domain")]
    [InlineData("Infrastructure", "Application,Contracts,Domain")]
    [InlineData("Desktop", "Application,Contracts,Domain,Infrastructure")]
    public void LayerReferencesFollowArchitecture(string layer, string expected)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "UniversalConverter.slnx")))
            root = root.Parent ?? throw new DirectoryNotFoundException("Solution root not found.");

        var name = "UniversalConverter." + layer;
        var project = XDocument.Load(Path.Combine(root.FullName, "src", name, name + ".csproj"));
        var references = project.Descendants("ProjectReference").Select(reference =>
            Path.GetFileNameWithoutExtension(reference.Attribute("Include")!.Value)["UniversalConverter.".Length..]);
        Assert.Equal(expected, string.Join(",", references.Order(StringComparer.Ordinal)));
        if (layer is "Domain" or "Contracts" or "Application")
            Assert.Empty(project.Descendants("PackageReference"));
    }
}
