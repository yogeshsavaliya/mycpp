using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Il2CppRecover.Tests.Helpers;

public static class RoslynRunner
{
    public static object? Invoke(string source, string typeName, string methodName, object? instance, params object?[] arguments)
    {
        var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(path =>
            {
                var name = Path.GetFileName(path);
                return name is "System.Runtime.dll" or "System.Private.CoreLib.dll" or "System.Console.dll" or "netstandard.dll";
            })
            .Select(path => MetadataReference.CreateFromFile(path))
            .Cast<MetadataReference>()
            .ToList();
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "RecoveredFixture",
            [tree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary).WithOptimizationLevel(OptimizationLevel.Release));
        using var pe = new MemoryStream();
        var emit = compilation.Emit(pe);
        if (!emit.Success)
        {
            var errors = string.Join(Environment.NewLine, emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
            throw new InvalidOperationException("Recovered C# did not compile:" + Environment.NewLine + errors + Environment.NewLine + source);
        }
        var assembly = Assembly.Load(pe.ToArray());
        var type = assembly.GetType(typeName) ?? throw new InvalidOperationException("Missing type " + typeName);
        var method = type.GetMethod(methodName) ?? throw new InvalidOperationException("Missing method " + methodName);
        return method.Invoke(instance, arguments);
    }
}
