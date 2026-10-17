using System.CodeDom.Compiler;
using System.Diagnostics;
using System.Globalization;
using System.Linq.Expressions;
using System.Runtime.InteropServices;

namespace BlindsDown.Tests.NoNetwork;

/// <summary>
/// Known IL for <see cref="ScannerSelfTests"/> to find in this test assembly. NOTHING HERE IS EVER
/// CALLED: the scanner only reads the compiled instructions. The native functions are harmless
/// read-only queries, chosen because one needs argument marshalling (so LibraryImport generates a
/// wrapper) and one doesn't. The "side door" methods use banned mechanisms only on harmless targets.
/// </summary>
internal static partial class ScannerFixtures
{
    /// <summary>newobj (List constructor), call (Math.Abs), callvirt (List.Add), ldftn (Math.Max as a delegate).</summary>
    public static Func<int, int, int> CallsConstructsAndTakesDelegate()
    {
        var list = new List<int> { Math.Abs(-1) };
        _ = list.Count;
        return Math.Max;
    }

    /// <summary>A dense switch compiles to the variable-length IL "switch" instruction; the call after it
    /// is only found if the decoder skipped the jump table correctly.</summary>
    public static string CallsAfterSwitch(int value)
    {
        string text = value switch
        {
            0 => "zero",
            1 => "one",
            2 => "two",
            3 => "three",
            4 => "four",
            _ => "many",
        };
        return text + value.ToString(CultureInfo.InvariantCulture) + Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture);
    }

    public static nint CallsMarshallingLibraryImport() => GetModuleHandle(null);

    public static uint CallsBlittableLibraryImport() => GetDoubleClickTime();

    /// <summary>The Visual Basic runtime is part of the shared framework, so C# can call it with no reference added.</summary>
    public static string UsesVisualBasicRuntime() => Microsoft.VisualBasic.Strings.StrReverse("abc");

    /// <summary>'dynamic' compiles to Microsoft.CSharp.RuntimeBinder call sites that pick members by name.</summary>
    public static object UsesDynamic()
    {
        dynamic value = 1;
        return value + 1;
    }

    /// <summary>A method found by name and invoked: the shape that would reach Process.Start without naming it.</summary>
    public static object? InvokesByReflection() =>
        typeof(Math).GetMethod(nameof(Math.Abs), [typeof(int)])!.Invoke(null, [-1]);

    /// <summary>Expression{T}.Compile is called on a generic instantiation (a TypeSpec parent).</summary>
    public static int CompilesExpression()
    {
        Expression<Func<int>> expression = () => 1;
        return expression.Compile()();
    }

    /// <summary>The machine-name overload ("." is this PC, but the scan can't know the value).</summary>
    public static string ProcessByIdWithMachineName() => Process.GetProcessById(Environment.ProcessId, ".").ProcessName;

    [LibraryImport("kernel32", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandle(string? moduleName);

    [LibraryImport("user32", EntryPoint = "GetDoubleClickTime")]
    private static partial uint GetDoubleClickTime();

    /// <summary>An instance method that claims to be LibraryImport output. The generator only writes static
    /// wrappers, so the scanner must not look through this one to its (virtual) callers.</summary>
    internal sealed class FakeWrapper
    {
        [GeneratedCode("Microsoft.Interop.LibraryImportGenerator", "0.0.0")]
        public uint Run() => GetDoubleClickTime();
    }
}
