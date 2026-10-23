using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace FCommerce.ArchitectureTests;

/// <summary>Finds methods whose IL contains a given instruction, by decoding method bodies of an assembly file.</summary>
internal static class IlScanner
{
    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    /// <summary>"Type::Method" of every method (outside compiler-generated helper types) that uses one of the opcodes.</summary>
    public static IEnumerable<string> MethodsUsing(Assembly assembly, params OpCode[] opCodes)
    {
        var wanted = opCodes.Select(o => o.Value).ToHashSet();
        using var stream = File.OpenRead(assembly.Location);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var found = new List<string>();

        foreach (var typeHandle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(typeHandle);
            var typeName = metadata.GetString(type.Name);
            if (IsCompilerHelper(typeName))
            {
                continue;
            }

            foreach (var methodHandle in type.GetMethods())
            {
                var method = metadata.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0 || IsIteratorReset(typeName, metadata.GetString(method.Name)))
                {
                    continue;
                }

                var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
                if (Uses(il, wanted))
                {
                    found.Add($"{metadata.GetString(type.Namespace)}.{typeName}::{metadata.GetString(method.Name)}");
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Compiler-emitted helper types: &lt;PrivateImplementationDetails&gt; (e.g. throw helpers for unreachable switch arms)
    /// and &lt;&gt;z__* (read-only wrappers for collection expressions). Lambda closures (&lt;&gt;c, display classes) and
    /// async/iterator state machines hold user code and are scanned.
    /// </summary>
    private static bool IsCompilerHelper(string typeName) =>
        typeName.StartsWith("<PrivateImplementationDetails>", StringComparison.Ordinal)
        || typeName.StartsWith("<>z__", StringComparison.Ordinal);

    /// <summary>Iterator state machines ("&lt;Name&gt;d__N") implement IEnumerator.Reset by throwing NotSupportedException.</summary>
    private static bool IsIteratorReset(string typeName, string methodName) =>
        typeName.StartsWith('<') && typeName.Contains(">d__", StringComparison.Ordinal)
        && methodName == "System.Collections.IEnumerator.Reset";

    private static bool Uses(byte[] il, HashSet<short> wanted)
    {
        var offset = 0;
        while (offset < il.Length)
        {
            short value = il[offset] == 0xFE ? (short)(0xFE00 | il[offset + 1]) : il[offset];
            var opCode = OpCodesByValue[value];
            if (wanted.Contains(opCode.Value))
            {
                return true;
            }

            offset += opCode.Size + OperandSize(opCode, il, offset + opCode.Size);
        }

        return false;
    }

    private static int OperandSize(OpCode opCode, byte[] il, int operandOffset) =>
        opCode.OperandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, operandOffset)),
            _ => 4,
        };
}
