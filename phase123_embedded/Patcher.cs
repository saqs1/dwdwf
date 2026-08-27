using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class Program
{
    private const string ResourceName = "OliverS2EEmbeddedHelper.dll";

    static int Main(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("usage: patcher <original-s2e.dll> <helper.dll> <output.dll>");
            return 2;
        }

        string input = args[0];
        string helperPath = args[1];
        string output = args[2];

        if (!File.Exists(input)) throw new FileNotFoundException("Original S2E DLL not found", input);
        if (!File.Exists(helperPath)) throw new FileNotFoundException("Embedded helper DLL not found", helperPath);

        var rp = new ReaderParameters { ReadWrite = false, InMemory = true };
        using var asm = AssemblyDefinition.ReadAssembly(input, rp);
        ModuleDefinition module = asm.MainModule;

        TypeDefinition plugin = module.Types.FirstOrDefault(t => t.Name == "Plugin");
        if (plugin == null) throw new Exception("Original S2E Plugin type was not found");

        MethodDefinition load = plugin.Methods.FirstOrDefault(m => m.Name == "Load" && !m.IsStatic && m.HasBody);
        if (load == null) throw new Exception("Original S2E Plugin.Load method was not found");

        for (int i = module.Resources.Count - 1; i >= 0; i--)
        {
            if (module.Resources[i].Name == ResourceName)
                module.Resources.RemoveAt(i);
        }
        module.Resources.Add(new EmbeddedResource(
            ResourceName,
            Mono.Cecil.ManifestResourceAttributes.Private,
            File.ReadAllBytes(helperPath)));

        TypeDefinition oldLoader = module.Types.FirstOrDefault(t => t.Name == "OliverEmbeddedLoader");
        if (oldLoader != null) module.Types.Remove(oldLoader);

        MethodDefinition init = AddSafeEmbeddedLoader(module);

        if (load.Body.Instructions.Any(i =>
            i.OpCode == OpCodes.Call &&
            i.Operand is MethodReference mr &&
            mr.Name == init.Name &&
            mr.DeclaringType.Name == init.DeclaringType.Name))
            throw new Exception("Plugin.Load is already patched");

        ILProcessor loadIl = load.Body.GetILProcessor();
        Instruction first = load.Body.Instructions.First();
        loadIl.InsertBefore(first, loadIl.Create(OpCodes.Call, init));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        asm.Write(output);
        Console.WriteLine("Embedded Oliver Phase123 helper and patched Plugin.Load safely.");
        return 0;
    }

    private static MethodDefinition AddSafeEmbeddedLoader(ModuleDefinition module)
    {
        var loader = new TypeDefinition(
            string.Empty,
            "OliverEmbeddedLoader",
            Mono.Cecil.TypeAttributes.Public |
            Mono.Cecil.TypeAttributes.Abstract |
            Mono.Cecil.TypeAttributes.Sealed |
            Mono.Cecil.TypeAttributes.BeforeFieldInit,
            module.TypeSystem.Object);
        module.Types.Add(loader);

        var method = new MethodDefinition(
            "Initialize",
            Mono.Cecil.MethodAttributes.Public |
            Mono.Cecil.MethodAttributes.Static |
            Mono.Cecil.MethodAttributes.HideBySig,
            module.TypeSystem.Void);
        loader.Methods.Add(method);
        method.Body.InitLocals = true;

        TypeReference assemblyType = module.ImportReference(typeof(Assembly));
        TypeReference streamType = module.ImportReference(typeof(Stream));
        TypeReference runtimeType = module.ImportReference(typeof(Type));
        TypeReference methodInfoType = module.ImportReference(typeof(MethodInfo));

        var hostVar = new VariableDefinition(assemblyType);
        var streamVar = new VariableDefinition(streamType);
        var dataVar = new VariableDefinition(new ArrayType(module.TypeSystem.Byte));
        var totalVar = new VariableDefinition(module.TypeSystem.Int32);
        var readVar = new VariableDefinition(module.TypeSystem.Int32);
        var helperVar = new VariableDefinition(assemblyType);
        var typeVar = new VariableDefinition(runtimeType);
        var initVar = new VariableDefinition(methodInfoType);
        method.Body.Variables.Add(hostVar);
        method.Body.Variables.Add(streamVar);
        method.Body.Variables.Add(dataVar);
        method.Body.Variables.Add(totalVar);
        method.Body.Variables.Add(readVar);
        method.Body.Variables.Add(helperVar);
        method.Body.Variables.Add(typeVar);
        method.Body.Variables.Add(initVar);

        MethodReference getExecutingAssembly = module.ImportReference(
            typeof(Assembly).GetMethod(nameof(Assembly.GetExecutingAssembly), BindingFlags.Public | BindingFlags.Static)!);
        MethodReference getResourceStream = module.ImportReference(
            typeof(Assembly).GetMethod(nameof(Assembly.GetManifestResourceStream), new[] { typeof(string) })!);
        MethodReference streamLength = module.ImportReference(
            typeof(Stream).GetProperty(nameof(Stream.Length))!.GetMethod!);
        MethodReference streamRead = module.ImportReference(
            typeof(Stream).GetMethod(nameof(Stream.Read), new[] { typeof(byte[]), typeof(int), typeof(int) })!);
        MethodReference assemblyLoad = module.ImportReference(
            typeof(Assembly).GetMethod(nameof(Assembly.Load), new[] { typeof(byte[]) })!);
        MethodReference assemblyGetType = module.ImportReference(
            typeof(Assembly).GetMethod(nameof(Assembly.GetType), new[] { typeof(string), typeof(bool) })!);
        MethodReference typeGetMethod = module.ImportReference(
            typeof(Type).GetMethod(nameof(Type.GetMethod), new[] { typeof(string), typeof(BindingFlags) })!);
        MethodReference methodInvoke = module.ImportReference(
            typeof(MethodBase).GetMethod(nameof(MethodBase.Invoke), new[] { typeof(object), typeof(object[]) })!);

        ILProcessor il = method.Body.GetILProcessor();

        Instruction tryStart = il.Create(OpCodes.Nop);
        Instruction loopCheck = il.Create(OpCodes.Ldloc, totalVar);
        Instruction loopEnd = il.Create(OpCodes.Nop);
        Instruction safeLeave = il.Create(OpCodes.Leave, loopEnd);
        Instruction tryEnd = il.Create(OpCodes.Nop);
        Instruction handlerStart = il.Create(OpCodes.Pop);
        Instruction ret = il.Create(OpCodes.Ret);

        il.Append(tryStart);
        il.Append(il.Create(OpCodes.Call, getExecutingAssembly));
        il.Append(il.Create(OpCodes.Stloc, hostVar));
        il.Append(il.Create(OpCodes.Ldloc, hostVar));
        il.Append(il.Create(OpCodes.Ldstr, ResourceName));
        il.Append(il.Create(OpCodes.Callvirt, getResourceStream));
        il.Append(il.Create(OpCodes.Stloc, streamVar));
        il.Append(il.Create(OpCodes.Ldloc, streamVar));
        il.Append(il.Create(OpCodes.Brfalse, safeLeave));

        il.Append(il.Create(OpCodes.Ldloc, streamVar));
        il.Append(il.Create(OpCodes.Callvirt, streamLength));
        il.Append(il.Create(OpCodes.Conv_I4));
        il.Append(il.Create(OpCodes.Newarr, module.TypeSystem.Byte));
        il.Append(il.Create(OpCodes.Stloc, dataVar));
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Stloc, totalVar));

        il.Append(loopCheck);
        il.Append(il.Create(OpCodes.Ldloc, dataVar));
        il.Append(il.Create(OpCodes.Ldlen));
        il.Append(il.Create(OpCodes.Conv_I4));
        il.Append(il.Create(OpCodes.Bge, loopEnd));
        il.Append(il.Create(OpCodes.Ldloc, streamVar));
        il.Append(il.Create(OpCodes.Ldloc, dataVar));
        il.Append(il.Create(OpCodes.Ldloc, totalVar));
        il.Append(il.Create(OpCodes.Ldloc, dataVar));
        il.Append(il.Create(OpCodes.Ldlen));
        il.Append(il.Create(OpCodes.Conv_I4));
        il.Append(il.Create(OpCodes.Ldloc, totalVar));
        il.Append(il.Create(OpCodes.Sub));
        il.Append(il.Create(OpCodes.Callvirt, streamRead));
        il.Append(il.Create(OpCodes.Stloc, readVar));
        il.Append(il.Create(OpCodes.Ldloc, readVar));
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Ble, loopEnd));
        il.Append(il.Create(OpCodes.Ldloc, totalVar));
        il.Append(il.Create(OpCodes.Ldloc, readVar));
        il.Append(il.Create(OpCodes.Add));
        il.Append(il.Create(OpCodes.Stloc, totalVar));
        il.Append(il.Create(OpCodes.Br, loopCheck));

        il.Append(loopEnd);
        il.Append(il.Create(OpCodes.Ldloc, totalVar));
        il.Append(il.Create(OpCodes.Ldloc, dataVar));
        il.Append(il.Create(OpCodes.Ldlen));
        il.Append(il.Create(OpCodes.Conv_I4));
        il.Append(il.Create(OpCodes.Bne_Un, safeLeave));

        il.Append(il.Create(OpCodes.Ldloc, dataVar));
        il.Append(il.Create(OpCodes.Call, assemblyLoad));
        il.Append(il.Create(OpCodes.Stloc, helperVar));
        il.Append(il.Create(OpCodes.Ldloc, helperVar));
        il.Append(il.Create(OpCodes.Ldstr, "OliverBootstrap"));
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Callvirt, assemblyGetType));
        il.Append(il.Create(OpCodes.Stloc, typeVar));
        il.Append(il.Create(OpCodes.Ldloc, typeVar));
        il.Append(il.Create(OpCodes.Brfalse, safeLeave));

        il.Append(il.Create(OpCodes.Ldloc, typeVar));
        il.Append(il.Create(OpCodes.Ldstr, "Init"));
        il.Append(il.Create(OpCodes.Ldc_I4, (int)(BindingFlags.Public | BindingFlags.Static)));
        il.Append(il.Create(OpCodes.Callvirt, typeGetMethod));
        il.Append(il.Create(OpCodes.Stloc, initVar));
        il.Append(il.Create(OpCodes.Ldloc, initVar));
        il.Append(il.Create(OpCodes.Brfalse, safeLeave));
        il.Append(il.Create(OpCodes.Ldloc, initVar));
        il.Append(il.Create(OpCodes.Ldnull));
        il.Append(il.Create(OpCodes.Ldnull));
        il.Append(il.Create(OpCodes.Callvirt, methodInvoke));
        il.Append(il.Create(OpCodes.Pop));
        il.Append(safeLeave);

        il.Append(tryEnd);
        il.Append(handlerStart);
        il.Append(il.Create(OpCodes.Leave, ret));
        il.Append(ret);

        safeLeave.Operand = ret;
        method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            CatchType = module.ImportReference(typeof(Exception)),
            TryStart = tryStart,
            TryEnd = tryEnd,
            HandlerStart = handlerStart,
            HandlerEnd = ret
        });

        return method;
    }
}
