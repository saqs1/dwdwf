using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Collections.Immutable;
using System.Reflection;

if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: LocalCompiler <source.cs> <output.dll> <refsDir>");
    return 2;
}

var sourcePath = Path.GetFullPath(args[0]);
var outputPath = Path.GetFullPath(args[1]);
var refsDir = Path.GetFullPath(args[2]);

var source = File.ReadAllText(sourcePath);
var syntax = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));

var refs = new Dictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);
void AddRef(string path)
{
    try
    {
        if (!File.Exists(path)) return;
        var an = AssemblyName.GetAssemblyName(path);
        var key = an.Name ?? Path.GetFileNameWithoutExtension(path);
        if (!refs.ContainsKey(key)) refs[key] = MetadataReference.CreateFromFile(path);
    }
    catch { }
}

var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "")
    .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
foreach (var p in tpa) AddRef(p);
foreach (var p in Directory.EnumerateFiles(refsDir, "*.dll", SearchOption.AllDirectories)) AddRef(p);

var compilation = CSharpCompilation.Create(
    Path.GetFileNameWithoutExtension(outputPath),
    new[] { syntax },
    refs.Values,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
        optimizationLevel: OptimizationLevel.Release,
        allowUnsafe: true,
        nullableContextOptions: NullableContextOptions.Disable,
        deterministic: true));

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
using var fs = File.Create(outputPath);
var emit = compilation.Emit(fs);
foreach (var d in emit.Diagnostics.OrderBy(d => d.Location.GetLineSpan().StartLinePosition.Line))
    Console.WriteLine(d.ToString());

if (!emit.Success)
{
    Console.Error.WriteLine($"BUILD_FAILED errors={emit.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error)}");
    try { fs.Close(); File.Delete(outputPath); } catch { }
    return 1;
}

Console.WriteLine($"BUILD_OK {outputPath}");
return 0;
