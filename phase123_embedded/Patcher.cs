using System;
using System.Collections.Generic;
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

        using var resolver = new OliverAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(input))!);
        string il2cppRefs = Path.GetFullPath("deps-util/IL2CPP_net6");
        if (Directory.Exists(il2cppRefs)) resolver.AddSearchDirectory(il2cppRefs);

        var rp = new ReaderParameters
        {
            ReadWrite = false,
            InMemory = true,
            AssemblyResolver = resolver
        };

        using var asm = AssemblyDefinition.ReadAssembly(input, rp);
        ModuleDefinition module = asm.MainModule;
        resolver.PrepareAssemblyCSharpStub(module);

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

internal sealed class OliverAssemblyResolver : IAssemblyResolver
{
    private readonly DefaultAssemblyResolver _fallback = new DefaultAssemblyResolver();
    private AssemblyDefinition? _assemblyCSharpStub;

    public void AddSearchDirectory(string path)
    {
        if (Directory.Exists(path)) _fallback.AddSearchDirectory(path);
    }

    public void PrepareAssemblyCSharpStub(ModuleDefinition source)
    {
        var name = new AssemblyNameDefinition("Assembly-CSharp", new Version(0, 0, 0, 0));
        _assemblyCSharpStub = AssemblyDefinition.CreateAssembly(name, "Assembly-CSharp", ModuleKind.Dll);
        ModuleDefinition stub = _assemblyCSharpStub.MainModule;

        var enumTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (TypeDefinition type in source.Types)
            CollectConstantEnumTypes(type, enumTypes);

        var refs = source.GetTypeReferences()
            .Select(Unwrap)
            .Where(IsAssemblyCSharpType)
            .GroupBy(t => t.FullName, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        foreach (TypeReference tr in refs)
        {
            bool isEnum = enumTypes.Contains(tr.FullName);
            AddStubType(stub, tr, isEnum);
        }
    }

    private static void CollectConstantEnumTypes(TypeDefinition type, HashSet<string> enumTypes)
    {
        foreach (MethodDefinition method in type.Methods)
        {
            foreach (ParameterDefinition parameter in method.Parameters)
            {
                if (!parameter.HasConstant) continue;
                TypeReference tr = Unwrap(parameter.ParameterType);
                if (IsAssemblyCSharpType(tr)) enumTypes.Add(tr.FullName);
            }
        }
        foreach (TypeDefinition nested in type.NestedTypes)
            CollectConstantEnumTypes(nested, enumTypes);
    }

    private static TypeReference Unwrap(TypeReference type)
    {
        while (type is TypeSpecification spec) type = spec.ElementType;
        return type;
    }

    private static bool IsAssemblyCSharpType(TypeReference type)
    {
        IMetadataScope? scope = type.Scope;
        return scope is AssemblyNameReference anr && anr.Name == "Assembly-CSharp";
    }

    private static void AddStubType(ModuleDefinition stub, TypeReference reference, bool isEnum)
    {
        if (reference.DeclaringType != null)
        {
            TypeReference parentRef = Unwrap(reference.DeclaringType);
            TypeDefinition parent = FindOrCreateTopLevel(stub, parentRef.Namespace, parentRef.Name, false);
            if (parent.NestedTypes.Any(t => t.Name == reference.Name)) return;
            TypeReference baseType = isEnum ? stub.ImportReference(typeof(Enum)) : stub.TypeSystem.Object;
            var nested = new TypeDefinition(
                string.Empty,
                reference.Name,
                Mono.Cecil.TypeAttributes.NestedPublic | (isEnum ? Mono.Cecil.TypeAttributes.Sealed : Mono.Cecil.TypeAttributes.Class),
                baseType);
            if (isEnum) AddEnumValueField(stub, nested);
            parent.NestedTypes.Add(nested);
            return;
        }

        FindOrCreateTopLevel(stub, reference.Namespace, reference.Name, isEnum);
    }

    private static TypeDefinition FindOrCreateTopLevel(ModuleDefinition stub, string ns, string name, bool isEnum)
    {
        TypeDefinition? existing = stub.Types.FirstOrDefault(t => t.Namespace == ns && t.Name == name);
        if (existing != null)
        {
            if (isEnum && existing.BaseType?.FullName != typeof(Enum).FullName)
            {
                existing.BaseType = stub.ImportReference(typeof(Enum));
                existing.Attributes |= Mono.Cecil.TypeAttributes.Sealed;
                AddEnumValueField(stub, existing);
            }
            return existing;
        }

        TypeReference baseType = isEnum ? stub.ImportReference(typeof(Enum)) : stub.TypeSystem.Object;
        var created = new TypeDefinition(
            ns ?? string.Empty,
            name,
            Mono.Cecil.TypeAttributes.Public | (isEnum ? Mono.Cecil.TypeAttributes.Sealed : Mono.Cecil.TypeAttributes.Class),
            baseType);
        if (isEnum) AddEnumValueField(stub, created);
        stub.Types.Add(created);
        return created;
    }

    private static void AddEnumValueField(ModuleDefinition stub, TypeDefinition type)
    {
        if (type.Fields.Any(f => f.Name == "value__")) return;
        type.Fields.Add(new FieldDefinition(
            "value__",
            Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.SpecialName | Mono.Cecil.FieldAttributes.RTSpecialName,
            stub.TypeSystem.Int32));
    }

    public AssemblyDefinition Resolve(AssemblyNameReference name)
    {
        if (name.Name == "Assembly-CSharp" && _assemblyCSharpStub != null) return _assemblyCSharpStub;
        return _fallback.Resolve(name);
    }

    public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
    {
        if (name.Name == "Assembly-CSharp" && _assemblyCSharpStub != null) return _assemblyCSharpStub;
        return _fallback.Resolve(name, parameters);
    }

    public void Dispose()
    {
        _assemblyCSharpStub?.Dispose();
        _fallback.Dispose();
    }
}
