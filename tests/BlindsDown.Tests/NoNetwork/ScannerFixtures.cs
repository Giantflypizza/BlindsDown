using System.CodeDom.Compiler;
using System.Diagnostics;
using System.Globalization;
using System.Linq.Expressions;
using System.Runtime.InteropServices;

namespace BlindsDown.Tests.NoNetwork;

/// <summary>
/// Known IL for <see cref="ScannerSelfTests"/> to find in this test assembly. NOTHING HERE IS EVER
/// CALLED: the scanner only reads the compiled instructions. GetModuleHandleW and GetDoubleClickTime
/// were chosen because one needs argument marshalling (so LibraryImport generates a wrapper) and one
/// doesn't; OpenSCManagerW and CreateFileW are only declared, to show which calls of them the rules
/// accept. The "side door" methods use banned mechanisms only on harmless targets.
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

    /// <summary>A constant null machine name, the way BlindsDown writes it: this computer. Accepted.</summary>
    public static unsafe nint OpensThisComputersServiceManager(uint access) => OpenSCManager(null, null, access);

    /// <summary>The same through an import without a generated wrapper. Accepted.</summary>
    public static unsafe nint OpensThisComputersServiceManagerDirectly() => OpenSCManagerDirect(null, null, 1);

    /// <summary>A variable in a later argument doesn't matter: only the machine name has to be null. Accepted.</summary>
    public static unsafe nint OpensThisComputersServiceManagerWithADatabase(char* database) => OpenSCManager(null, database, 1);

    /// <summary>A machine name from a string: another computer. Rejected.</summary>
    public static unsafe nint OpensAnotherComputersServiceManager(string machine)
    {
        fixed (char* name = machine)
        {
            return OpenSCManager(name, null, 1);
        }
    }

    /// <summary>Null on one path, a name on the other: the scan can't know which one runs. Rejected.</summary>
    public static unsafe nint OpensAServiceManagerChosenAtRunTime(bool remote, char* machine) => OpenSCManager(remote ? machine : null, null, 1);

    /// <summary>A function pointer can be called later with any machine name. Rejected.</summary>
    public static unsafe delegate*<char*, char*, uint, nint> TakesTheServiceManagerFunction() => &OpenSCManagerDirect;

    /// <summary>Opens a path natively; a path can name another computer.</summary>
    public static unsafe nint OpensAPathNatively(char* path) => CreateFile(path, 0, 0, null, 3, 0, 0);

    /// <summary>A field that must stay null, written with a constant null. Accepted.</summary>
    public static unsafe FailureActions SetsTheCommandToNull() => new() { ResetPeriod = 1, Command = null };

    /// <summary>The same field written with a real value. Rejected.</summary>
    public static unsafe FailureActions SetsTheCommand(char* command) => new() { Command = command };

    /// <summary>The field's address, through which anything can be stored. Rejected.</summary>
    public static unsafe char** TakesTheCommandsAddress(FailureActions* actions) => &actions->Command;

    [LibraryImport("kernel32", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandle(string? moduleName);

    [LibraryImport("user32", EntryPoint = "GetDoubleClickTime")]
    private static partial uint GetDoubleClickTime();

    [LibraryImport("advapi32", EntryPoint = "OpenSCManagerW", SetLastError = true)]
    private static unsafe partial nint OpenSCManager(char* machineName, char* databaseName, uint desiredAccess);

    [LibraryImport("advapi32", EntryPoint = "OpenSCManagerW")]
    private static unsafe partial nint OpenSCManagerDirect(char* machineName, char* databaseName, uint desiredAccess);

    [LibraryImport("kernel32", EntryPoint = "CreateFileW", SetLastError = true)]
    private static unsafe partial nint CreateFile(char* fileName, uint access, uint share, void* security, uint creation, uint flags, nint template);

    /// <summary>Shaped like SERVICE_FAILURE_ACTIONS.</summary>
    internal unsafe struct FailureActions
    {
        public uint ResetPeriod;
        public char* Command;
    }

    /// <summary>An instance method that claims to be LibraryImport output. The generator only writes static
    /// wrappers, so the scanner must not look through this one to its (virtual) callers.</summary>
    internal sealed class FakeWrapper
    {
        [GeneratedCode("Microsoft.Interop.LibraryImportGenerator", "0.0.0")]
        public uint Run() => GetDoubleClickTime();
    }
}

/// <summary>
/// Native functions that code in another assembly could call, where the caller rules can't see it. Never called,
/// like <see cref="ScannerFixtures"/>.
/// </summary>
public static partial class PublicNativeFixtures
{
    /// <summary>Blittable and without SetLastError: the declaration itself becomes the P/Invoke.</summary>
    [LibraryImport("user32", EntryPoint = "GetCaretBlinkTime")]
    public static partial uint CaretBlinkTime();

    /// <summary>With SetLastError the generator writes a wrapper with this name and visibility around a private P/Invoke.</summary>
    [LibraryImport("user32", EntryPoint = "GetDoubleClickTime", SetLastError = true)]
    public static partial uint DoubleClickTime();
}
