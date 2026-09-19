using XauScalp.Domain;

namespace XauScalp.Domain.Tests;

public sealed class DomainArchitectureTests
{
    [Fact]
    public void DomainAssembly_DoesNotReferenceOtherXauScalpProjects()
    {
        string[] forbiddenReferences = typeof(DomainAssemblyMarker)
            .Assembly
            .GetReferencedAssemblies()
            .Select(static assembly => assembly.Name)
            .Where(static name => name is not null && name.StartsWith("XauScalp.", StringComparison.Ordinal))
            .Cast<string>()
            .ToArray();

        Assert.Empty(forbiddenReferences);
    }

    [Fact]
    public void DomainAssemblyMarker_IsAvailable()
    {
        Assert.Equal("Domain", DomainAssemblyMarker.ComponentName);
    }
}
