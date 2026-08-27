using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: LocalDecompiler <assembly.dll> <output.cs>");
    return 2;
}
var input = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
var settings = new DecompilerSettings(LanguageVersion.Latest)
{
    ThrowOnAssemblyResolveErrors = false,
    UseSdkStyleProjectFormat = true
};
var resolver = new UniversalAssemblyResolver(input, false, ".NETCoreApp,Version=v6.0");
var dir = Path.GetDirectoryName(input)!;
resolver.AddSearchDirectory(dir);
var decompiler = new CSharpDecompiler(input, resolver, settings);
var code = decompiler.DecompileWholeModuleAsString();
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, code);
Console.WriteLine($"DECOMPILE_OK {output} chars={code.Length}");
return 0;