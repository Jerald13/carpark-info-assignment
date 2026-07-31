using System.Reflection;
using NetArchTest.Rules;

namespace CarparkInfo.Application.UnitTests.Architecture;

/// <summary>
/// The layering rules from ARCHITECTURE.md section 7, expressed as executable tests.
///
/// The README grades two flexibility axes explicitly:
///   R16 - "changing of data access technology"
///   R17 - "changing of interface file format from csv to JSON"
///
/// Both are ports-and-adapters properties that hold only while the dependency direction holds.
/// Documenting layering in a README does not preserve it; a failing build does. A pull request
/// that violates the architecture breaks here rather than being noticed in review, or not at all.
/// </summary>
public sealed class ArchitectureTests
{
    private static readonly Assembly Domain = typeof(Domain.AssemblyMarker).Assembly;
    private static readonly Assembly Application = typeof(AssemblyMarker).Assembly;
    private static readonly Assembly Infrastructure = typeof(Infrastructure.AssemblyMarker).Assembly;

    private const string DomainNamespace = "CarparkInfo.Domain";
    private const string ApplicationNamespace = "CarparkInfo.Application";
    private const string InfrastructureNamespace = "CarparkInfo.Infrastructure";
    private const string ApiNamespace = "CarparkInfo.Api";

    [Fact]
    public void Domain_depends_on_nothing()
    {
        var result = Types.InAssembly(Domain)
            .ShouldNot()
            .HaveDependencyOnAny(ApplicationNamespace, InfrastructureNamespace, ApiNamespace)
            .GetResult();

        result.FailingTypeNames.Should().BeNullOrEmpty(
            "Domain is the innermost layer and must reference no other project");
    }

    [Fact]
    public void Application_does_not_depend_on_Infrastructure()
    {
        var result = Types.InAssembly(Application)
            .ShouldNot()
            .HaveDependencyOnAny(InfrastructureNamespace, ApiNamespace)
            .GetResult();

        result.FailingTypeNames.Should().BeNullOrEmpty(
            "dependencies point inward; Infrastructure implements Application's ports, never the reverse");
    }

    [Fact]
    public void Application_does_not_depend_on_EntityFrameworkCore()
    {
        var result = Types.InAssembly(Application)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        result.FailingTypeNames.Should().BeNullOrEmpty(
            "R16: naming an EF Core type in Application would make swapping the data-access "
            + "technology a rewrite rather than a new adapter");
    }

    [Fact]
    public void Domain_does_not_depend_on_EntityFrameworkCore()
    {
        var result = Types.InAssembly(Domain)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        result.FailingTypeNames.Should().BeNullOrEmpty(
            "entities are persistence-ignorant");
    }

    [Fact]
    public void Application_and_Domain_do_not_depend_on_AspNetCore()
    {
        foreach (var assembly in new[] { Domain, Application })
        {
            var result = Types.InAssembly(assembly)
                .ShouldNot()
                .HaveDependencyOn("Microsoft.AspNetCore")
                .GetResult();

            result.FailingTypeNames.Should().BeNullOrEmpty(
                $"{assembly.GetName().Name} must be usable from the batch job, which has no HTTP host");
        }
    }

    [Fact]
    public void Infrastructure_does_not_depend_on_the_Api()
    {
        var result = Types.InAssembly(Infrastructure)
            .ShouldNot()
            .HaveDependencyOn(ApiNamespace)
            .GetResult();

        result.FailingTypeNames.Should().BeNullOrEmpty(
            "Infrastructure is shared by the API and the batch job and must not know either host");
    }

    [Fact]
    public void Repositories_do_not_leak_IQueryable_across_the_boundary()
    {
        var leaking = Application.GetTypes()
            .Where(t => t.IsInterface && t.Name.EndsWith("Repository", StringComparison.Ordinal))
            .SelectMany(t => t.GetMethods())
            .Where(m => m.ReturnType.Name.StartsWith("IQueryable", StringComparison.Ordinal))
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .ToList();

        leaking.Should().BeEmpty(
            "an IQueryable crossing the port would make the caller depend on the provider's "
            + "translation capabilities, so the data-access swap in R16 would be fiction");
    }

    [Fact]
    public void Ports_are_declared_in_the_Application_layer()
    {
        var misplaced = Infrastructure.GetTypes()
            .Where(t => t.IsInterface && t.IsPublic)
            .Where(t => t.Name.EndsWith("Repository", StringComparison.Ordinal)
                     || t.Name.EndsWith("UnitOfWork", StringComparison.Ordinal)
                     || t.Name.EndsWith("RecordSource", StringComparison.Ordinal))
            .Select(t => t.FullName!)
            .ToList();

        misplaced.Should().BeEmpty(
            "interfaces belong where they are consumed (Application/Abstractions), not where "
            + "they are implemented - that inversion is what makes adapters swappable");
    }
}
