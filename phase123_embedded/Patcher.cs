using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class Program
{
    static int Main(string[] args)
    {
        if (args.Length != 2) { Console.Error.WriteLine("usage: patcher <input> <output>"); return 2; }
        var rp = new ReaderParameters { ReadWrite = false, InMemory = true };
        using var asm = AssemblyDefinition.ReadAssembly(args[0], rp);
        var module = asm.MainModule;
        var plugin = module.Types.FirstOrDefault(t => t.Name == "Plugin");
        var loader = module.Types.FirstOrDefault(t => t.Name == "OliverLoader");
        if (plugin == null || loader == null) throw new Exception("Plugin/OliverLoader type missing after merge");
        var load = plugin.Methods.FirstOrDefault(m => m.Name == "Load" && !m.IsStatic && m.HasBody);
        var init = loader.Methods.FirstOrDefault(m => m.Name == "Initialize" && m.IsStatic);
        if (load == null || init == null) throw new Exception("Load/Initialize method missing");
        if (load.Body.Instructions.Any(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference mr && mr.Name == "Initialize" && mr.DeclaringType.Name == "OliverLoader"))
            throw new Exception("Already patched");
        var il = load.Body.GetILProcessor();
        var first = load.Body.Instructions.First();
        il.InsertBefore(first, il.Create(OpCodes.Call, init));
        asm.Write(args[1]);
        Console.WriteLine("Patched Plugin.Load -> OliverLoader.Initialize");
        return 0;
    }
}
