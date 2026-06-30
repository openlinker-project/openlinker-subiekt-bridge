using System.Reflection;
using NetArchTest.Rules;
using Xunit;

namespace Subiekt.Bridge.Architecture.Tests;

/// <summary>
/// Enforces the hexagonal dependency rule. Assertions are limited to the rules
/// that hold in the current transitional state: the inner layers (Domain,
/// Application) must stay clean, and no Infrastructure layer may depend on the
/// Api (composition root). We intentionally do NOT assert that Api avoids
/// Sfera/SQL — for now Api legitimately still contains all of that code.
/// </summary>
public class DependencyRuleTests
{
    private static readonly Assembly DomainAssembly =
        typeof(Subiekt.Bridge.Domain.DomainAssemblyMarker).Assembly;

    private static readonly Assembly ApplicationAssembly =
        typeof(Subiekt.Bridge.Application.ApplicationAssemblyMarker).Assembly;

    private static readonly Assembly SferaAssembly =
        typeof(Subiekt.Bridge.Infrastructure.Sfera.SferaAssemblyMarker).Assembly;

    private static readonly Assembly SqlAssembly =
        typeof(Subiekt.Bridge.Infrastructure.Sql.SqlAssemblyMarker).Assembly;

    private static readonly Assembly PersistenceAssembly =
        typeof(Subiekt.Bridge.Infrastructure.Persistence.PersistenceAssemblyMarker).Assembly;

    private const string ApplicationNamespace = "Subiekt.Bridge.Application";
    private const string InfrastructureNamespace = "Subiekt.Bridge.Infrastructure";
    private const string ApiNamespace = "Subiekt.Bridge.Api";
    private const string AspNetCoreNamespace = "Microsoft.AspNetCore";
    private const string SqlClientNamespace = "Microsoft.Data.SqlClient";
    private const string SferaNamespace = "InsERT";

    private static void AssertNoDependency(Assembly assembly, params string[] forbidden)
    {
        var result = Types.InAssembly(assembly)
            .Should()
            .NotHaveDependencyOnAny(forbidden)
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            $"Dependency rule violated. Offending types: " +
            string.Join(", ", result.FailingTypeNames ?? System.Array.Empty<string>()));
    }

    [Fact]
    public void Domain_ShouldNotDependOnAnyOuterLayer()
    {
        AssertNoDependency(
            DomainAssembly,
            ApplicationNamespace,
            InfrastructureNamespace,
            ApiNamespace,
            AspNetCoreNamespace,
            SqlClientNamespace,
            SferaNamespace);
    }

    [Fact]
    public void Application_ShouldNotDependOnInfrastructureApiOrExternalAdapters()
    {
        AssertNoDependency(
            ApplicationAssembly,
            InfrastructureNamespace,
            ApiNamespace,
            AspNetCoreNamespace,
            SferaNamespace);
    }

    [Fact]
    public void InfrastructureSfera_ShouldNotDependOnApi()
    {
        AssertNoDependency(SferaAssembly, ApiNamespace);
    }

    [Fact]
    public void InfrastructureSql_ShouldNotDependOnApi()
    {
        AssertNoDependency(SqlAssembly, ApiNamespace);
    }

    [Fact]
    public void InfrastructurePersistence_ShouldNotDependOnApi()
    {
        AssertNoDependency(PersistenceAssembly, ApiNamespace);
    }
}
