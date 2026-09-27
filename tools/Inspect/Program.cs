// Reflection-only inspector for RimWorld's Assembly-CSharp.dll.
// Usage:
//   Inspect find <substring>              types whose full name contains substring
//   Inspect type <Full.Type.Name>         fields + methods declared on a type
//   Inspect calls <Full.Type.Name> <Method>  methods called (in IL order) by a method
//   Inspect callers <Full.Type.Name> <Method> methods anywhere that call it
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;

const string Managed = @"E:\SteamLibrary\steamapps\common\RimWorld\RimWorldWin64_Data\Managed";

// INSPECT_ASM=<path to a mod's dll> inspects that assembly instead; its folder and Harmony's are searched for references.
var target = Environment.GetEnvironmentVariable("INSPECT_ASM");
var searchDirs = new List<string> { Managed };
if (!string.IsNullOrEmpty(target))
{
    searchDirs.Add(Path.GetDirectoryName(target)!);
    searchDirs.Add(@"E:\SteamLibrary\steamapps\workshop\content941009463077\Current\Assemblies");
}
AssemblyLoadContext.Default.Resolving += (ctx, name) =>
{
    if (name.Name == "mscorlib" || name.Name == "netstandard" || name.Name!.StartsWith("System"))
        return null;
    var p = searchDirs.Select(d => Path.Combine(d, name.Name + ".dll")).FirstOrDefault(File.Exists);
    return p != null ? ctx.LoadFromAssemblyPath(p) : null;
};
var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(string.IsNullOrEmpty(target) ? Path.Combine(Managed, "Assembly-CSharp.dll") : target);

Type[] AllTypes()
{
    try { return asm.GetTypes(); }
    catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null).ToArray(); }
}

const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                         BindingFlags.Static | BindingFlags.DeclaredOnly;

string Sig(MethodBase m)
{
    string ps;
    try { ps = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}")); }
    catch { ps = "?"; }
    var ret = m is MethodInfo mi ? SafeName(() => mi.ReturnType.Name) + " " : "";
    var mods = (m.IsPublic ? "public " : m.IsFamily ? "protected " : m.IsAssembly ? "internal " : "private ")
               + (m.IsStatic ? "static " : "") + (m.IsVirtual ? "virtual " : "");
    return $"{mods}{ret}{m.DeclaringType?.FullName}.{m.Name}({ps})";
}
string SafeName(Func<string> f) { try { return f(); } catch { return "?"; } }

var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
    .Select(f => (OpCode)f.GetValue(null)).ToDictionary(o => (ushort)o.Value);

IEnumerable<MethodBase> Callees(MethodBase m)
{
    var body = m.GetMethodBody();
    if (body == null) yield break;
    var il = body.GetILAsByteArray();
    int i = 0;
    while (i < il.Length)
    {
        ushort v = il[i++];
        if (v == 0xFE) v = (ushort)(0xFE00 | il[i++]);
        if (!opcodes.TryGetValue(v, out var op)) yield break;
        int size = op.OperandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, i),
            _ => 4,
        };
        if (op.OperandType == OperandType.InlineMethod)
        {
            MethodBase target = null;
            try
            {
                target = m.Module.ResolveMethod(BitConverter.ToInt32(il, i),
                    m.DeclaringType?.IsGenericType == true ? m.DeclaringType.GetGenericArguments() : null,
                    m.IsGenericMethod ? m.GetGenericArguments() : null);
            }
            catch { }
            if (target != null) yield return target;
        }
        i += size;
    }
}

MethodBase[] Find(string typeName, string method)
{
    var t = asm.GetType(typeName) ?? throw new Exception("type not found: " + typeName);
    return t.GetMethods(All).Cast<MethodBase>().Concat(t.GetConstructors(All)).Where(m => m.Name == method).ToArray();
}

switch (args[0])
{
    case "find":
        foreach (var t in AllTypes().Where(t => t.FullName.Contains(args[1], StringComparison.OrdinalIgnoreCase)).OrderBy(t => t.FullName))
            Console.WriteLine(t.FullName);
        break;
    case "type":
    {
        var t = asm.GetType(args[1]) ?? throw new Exception("type not found");
        Console.WriteLine($"{t.FullName} : {t.BaseType?.FullName}");
        foreach (var f in t.GetFields(All))
            Console.WriteLine($"  field {(f.IsPublic ? "public " : f.IsAssembly ? "internal " : f.IsFamily ? "protected " : "private ")}{(f.IsStatic ? "static " : "")}{SafeName(() => f.FieldType.Name)} {f.Name}");
        foreach (var p in t.GetProperties(All))
            Console.WriteLine($"  prop  {SafeName(() => p.PropertyType.Name)} {p.Name}");
        foreach (var m in t.GetMethods(All).OrderBy(m => m.Name))
            Console.WriteLine("  " + Sig(m));
        break;
    }
    case "calls":
        foreach (var m in Find(args[1], args[2]))
        {
            Console.WriteLine(Sig(m));
            foreach (var c in Callees(m)) Console.WriteLine("    -> " + Sig(c));
        }
        break;
    case "il":
        foreach (var m in Find(args[1], args[2]))
        {
            Console.WriteLine(Sig(m));
            var il = m.GetMethodBody()?.GetILAsByteArray();
            if (il == null) continue;
            int i = 0;
            while (i < il.Length)
            {
                int at = i;
                ushort v = il[i++];
                if (v == 0xFE) v = (ushort)(0xFE00 | il[i++]);
                if (!opcodes.TryGetValue(v, out var op)) { Console.WriteLine($"  {at:X4} ?? {v:X}"); break; }
                string operand = "";
                switch (op.OperandType)
                {
                    case OperandType.ShortInlineI: operand = ((sbyte)il[i]).ToString(); i += 1; break;
                    case OperandType.ShortInlineVar: operand = il[i].ToString(); i += 1; break;
                    case OperandType.ShortInlineBrTarget: operand = $"-> {i + 1 + (sbyte)il[i]:X4}"; i += 1; break;
                    case OperandType.InlineBrTarget: operand = $"-> {i + 4 + BitConverter.ToInt32(il, i):X4}"; i += 4; break;
                    case OperandType.InlineType: case OperandType.InlineTok:
                        try { operand = m.Module.ResolveType(BitConverter.ToInt32(il, i),
                                  m.DeclaringType?.IsGenericType == true ? m.DeclaringType.GetGenericArguments() : null,
                                  m.IsGenericMethod ? m.GetGenericArguments() : null).Name; } catch { operand = "tok"; }
                        i += 4; break;
                    case OperandType.InlineVar: operand = BitConverter.ToInt16(il, i).ToString(); i += 2; break;
                    case OperandType.InlineI: operand = BitConverter.ToInt32(il, i).ToString(); i += 4; break;
                    case OperandType.ShortInlineR: operand = BitConverter.ToSingle(il, i).ToString(); i += 4; break;
                    case OperandType.InlineR: operand = BitConverter.ToDouble(il, i).ToString(); i += 8; break;
                    case OperandType.InlineI8: operand = BitConverter.ToInt64(il, i).ToString(); i += 8; break;
                    case OperandType.InlineNone: break;
                    case OperandType.InlineSwitch: i += 4 + 4 * BitConverter.ToInt32(il, i); break;
                    case OperandType.InlineMethod:
                        try { var t = m.Module.ResolveMethod(BitConverter.ToInt32(il, i)); operand = $"{t.DeclaringType?.Name}.{t.Name}"; } catch { operand = "?"; }
                        i += 4; break;
                    case OperandType.InlineField:
                        try { var f = m.Module.ResolveField(BitConverter.ToInt32(il, i)); operand = $"{f.DeclaringType?.Name}.{f.Name}"; } catch { operand = "?"; }
                        i += 4; break;
                    case OperandType.InlineString:
                        try { operand = "\"" + m.Module.ResolveString(BitConverter.ToInt32(il, i)) + "\""; } catch { operand = "?"; }
                        i += 4; break;
                    default: operand = "tok"; i += 4; break;
                }
                Console.WriteLine($"  {at:X4} {op.Name,-12} {operand}");
            }
        }
        break;
    case "declaring":
        // Every type that declares a method with the given name (i.e. all overrides).
        foreach (var t in AllTypes().OrderBy(t => t.FullName))
            foreach (var m in t.GetMethods(All).Where(m => m.Name == args[1]))
                Console.WriteLine(Sig(m));
        break;
    case "fieldrefs":
    {
        // Every method that loads, stores or takes the address of the given field.
        var field = asm.GetType(args[1])?.GetField(args[2], All) ?? throw new Exception("field not found");
        foreach (var t in AllTypes())
            foreach (var m in t.GetMethods(All).Cast<MethodBase>().Concat(t.GetConstructors(All)))
            {
                byte[] il;
                try { il = m.GetMethodBody()?.GetILAsByteArray(); } catch { continue; }
                if (il == null) continue;
                int i = 0;
                var hits = new List<string>();
                while (i < il.Length)
                {
                    ushort v = il[i++];
                    if (v == 0xFE) v = (ushort)(0xFE00 | il[i++]);
                    if (!opcodes.TryGetValue(v, out var op)) break;
                    int size = op.OperandType switch
                    {
                        OperandType.InlineNone => 0,
                        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                        OperandType.InlineVar => 2,
                        OperandType.InlineI8 or OperandType.InlineR => 8,
                        OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, i),
                        _ => 4,
                    };
                    if (op.OperandType == OperandType.InlineField)
                    {
                        try
                        {
                            if (m.Module.ResolveField(BitConverter.ToInt32(il, i)) == field)
                                hits.Add(op.Name);
                        }
                        catch { }
                    }
                    i += size;
                }
                if (hits.Count > 0)
                    Console.WriteLine($"{Sig(m)}   [{string.Join(",", hits.Distinct())}]");
            }
        break;
    }
    case "callers":
    {
        var targets = Find(args[1], args[2]).ToHashSet();
        foreach (var t in AllTypes())
            foreach (var m in t.GetMethods(All).Cast<MethodBase>().Concat(t.GetConstructors(All)))
            {
                bool hit = false;
                try { hit = Callees(m).Any(c => targets.Contains(c)); } catch { }
                if (hit) Console.WriteLine(Sig(m));
            }
        break;
    }
}
