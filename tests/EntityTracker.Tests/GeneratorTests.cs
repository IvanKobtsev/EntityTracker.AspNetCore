using EntityTracker.Generator;
using EntityTracker.AspNetCore;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
namespace EntityTracker.Tests;
public class GeneratorTests
{
    private static (Compilation Compilation, GeneratorDriverRunResult Result) Generate(string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(TrackedAttribute).Assembly.Location).Append(typeof(Microsoft.AspNetCore.SignalR.Hub).Assembly.Location)
            .Append(typeof(Microsoft.AspNetCore.SignalR.Hub<>).Assembly.Location)
            .Distinct().Select(x => MetadataReference.CreateFromFile(x));
        var compilation = CSharpCompilation.Create("GeneratorTest", [CSharpSyntaxTree.ParseText("#nullable enable\nusing EntityTracker.AspNetCore;\n" + source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new TrackingGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        return (output, driver.GetRunResult());
    }
    [Theory]
    [InlineData("[Tracked] public class Item { public int Id {get;set;} }")]
    [InlineData("[Tracked(PayloadMember=nameof(Payload))] public class Item { public int Id {get;set;} public int? Payload => null; }")]
    [InlineData("[Tracked(PayloadMember=nameof(Payload))] public class Item { public int Id {get;set;} public string? Payload => null; }")]
    [InlineData("public class Base { public System.Guid Id {get;set;} } [Tracked] public class Item : Base {}")]
    [InlineData("[Tracked(Name=\"Alias\", IdMember=nameof(Key), PayloadMember=nameof(Payload))] public class Item { public string Key()=>\"x\"; public Detail Payload => new(); } public class Detail { public int Value {get;set;} }")]
    public void Valid_adapters_compile(string source)
    {
        var (compilation, result) = Generate(source);
        Assert.Empty(result.Diagnostics);
        Assert.Empty(compilation.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error));
        Assert.Contains("AddGeneratedTracking", result.GeneratedTrees.Single().ToString());
    }
    [Theory]
    [InlineData("[Tracked] public class Item {}")]
    [InlineData("[Tracked] public class Item { public object Id => new(); }")]
    [InlineData("[Tracked(PayloadMember=\"Missing\")] public class Item { public int Id {get;set;} }")]
    [InlineData("[Tracked(Name=\"Same\")] public class A {public int Id{get;set;}} [Tracked(Name=\"Same\")] public class B {public int Id{get;set;}}")]
    public void Invalid_configuration_is_diagnosed(string source)
    {
        Assert.Contains(Generate(source).Result.Diagnostics, d => d.Id == "ET001");
    }
}
