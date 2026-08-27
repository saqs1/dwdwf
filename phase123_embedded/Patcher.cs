using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class Program
{
    static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("usage: patcher <input> <output>");
            return 2;
        }

        var rp = new ReaderParameters { ReadWrite = false, InMemory = true };
        using var asm = AssemblyDefinition.ReadAssembly(args[0], rp);
        var module = asm.MainModule;

        var plugin = module.Types.FirstOrDefault(t => t.Name == "Plugin");
        var bootstrap = module.Types.FirstOrDefault(t => t.Name == "OliverBootstrap");
        if (plugin == null || bootstrap == null)
            throw new Exception("Plugin/OliverBootstrap type missing after merge");

        var load = plugin.Methods.FirstOrDefault(m => m.Name == "Load" && !m.IsStatic && m.HasBody);
        var init = bootstrap.Methods.FirstOrDefault(m => m.Name == "Init" && m.IsStatic && m.Parameters.Count == 0);
        if (load == null || init == null)
            throw new Exception("Plugin.Load/OliverBootstrap.Init method missing");

        if (load.Body.Instructions.Any(i =>
            i.OpCode == OpCodes.Call &&
            i.Operand is MethodReference mr &&
            mr.Name == "Init" &&
            mr.DeclaringType.Name == "OliverBootstrap"))
            throw new Exception("Already patched");

        var il = load.Body.GetILProcessor();
        var first = load.Body.Instructions.First();
        il.InsertBefore(first, il.Create(OpCodes.Call, module.ImportReference(init)));

        asm.Write(args[1]);
        Console.WriteLine("Patched Plugin.Load -> OliverBootstrap.Init");
        return 0;
    }
}
