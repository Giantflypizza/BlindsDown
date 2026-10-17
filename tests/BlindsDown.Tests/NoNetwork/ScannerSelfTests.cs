using System.Reflection.Metadata;

namespace BlindsDown.Tests.NoNetwork;

/// <summary>
/// Proves the no-network scanner actually finds what it looks for. A scanner that silently finds
/// nothing would make <see cref="NoNetworkTests"/> pass forever, so every rule is run against
/// Microsoft's own framework assemblies that are KNOWN to do the banned things (they are allowed to:
/// they are the networking stack), and against <see cref="ScannerFixtures"/> for exact IL shapes.
/// The framework files are only read, never loaded or run.
/// </summary>
public sealed class ScannerSelfTests
{
    private const string SafeShell = "BlindsDown.Infrastructure.SafeShell";

    private static readonly string RuntimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
    private static readonly string DesktopDirectory = Path.GetDirectoryName(typeof(System.Windows.Forms.Form).Assembly.Location)!;

    [Fact]
    public void FindsNetworkingAssemblyReferences()
    {
        List<string> http = Scan(Runtime("System.Net.Http.dll"), NoNetworkPolicy.BannedAssemblyReferences);
        Assert.Contains("System.Net.Http.dll: references assembly System.Net.Primitives", http);
        Assert.Contains("System.Net.Http.dll: references assembly System.Net.Sockets", http);

        // WinForms itself references System.Net.Http: PictureBox can download images. That is exactly why
        // BlindsDown bans PictureBox and why this rule looks at what the app references, not at what ships.
        Assert.Contains("System.Windows.Forms.dll: references assembly System.Net.Http", Scan(Desktop("System.Windows.Forms.dll"), NoNetworkPolicy.BannedAssemblyReferences));

        Assert.Empty(Scan(Runtime("System.Collections.dll"), NoNetworkPolicy.BannedAssemblyReferences));
    }

    [Fact]
    public void FindsTypesFromNetworkingNamespaces()
    {
        Assert.Contains("System.Net.Http.dll: uses System.Net.Sockets.Socket", Scan(Runtime("System.Net.Http.dll"), NoNetworkPolicy.BannedNamespaces));
        Assert.Contains("System.Windows.Forms.dll: uses System.Net.Http.HttpClient", Scan(Desktop("System.Windows.Forms.dll"), NoNetworkPolicy.BannedNamespaces));
        Assert.Contains("System.Net.Http.dll: defines System.Net.Http.HttpClient", Scan(Runtime("System.Net.Http.dll"), NoNetworkPolicy.BannedNamespaces));
        Assert.Empty(Scan(Runtime("System.Collections.dll"), NoNetworkPolicy.BannedNamespaces));
    }

    [Fact]
    public void FindsTypesAndMembersThatCanFetchUrlsOrLoadCode()
    {
        List<string> designer = Scan(Desktop("System.Windows.Forms.Design.dll"), NoNetworkPolicy.BannedTypesAndMembers);
        Assert.Contains(designer, o => o.StartsWith("System.Windows.Forms.Design.dll: uses System.Windows.Forms.PictureBox -", StringComparison.Ordinal));
        Assert.Contains(designer, o => o.StartsWith("System.Windows.Forms.Design.dll: uses System.Windows.Forms.LinkLabel -", StringComparison.Ordinal));

        // My.Computer.Audio.Play(location) builds a SoundPlayer from a string; the Stream overload is fine.
        List<string> vbForms = Scan(Desktop("Microsoft.VisualBasic.Forms.dll"), NoNetworkPolicy.BannedTypesAndMembers);
        Assert.Contains(vbForms, o => o.Contains("System.Media.SoundPlayer..ctor(System.String)", StringComparison.Ordinal));
        Assert.DoesNotContain(vbForms, o => o.Contains("System.Media.SoundPlayer..ctor(System.IO.Stream)", StringComparison.Ordinal));

        // VB's CreateObject/GetObject activate COM by ProgID and by moniker.
        List<string> vbCore = Scan(Runtime("Microsoft.VisualBasic.Core.dll"), NoNetworkPolicy.BannedTypesAndMembers);
        Assert.Contains(vbCore, o => o.Contains("System.Type.GetTypeFromProgID(System.String)", StringComparison.Ordinal));
        Assert.Contains(vbCore, o => o.Contains("System.Runtime.InteropServices.Marshal.BindToMoniker(System.String)", StringComparison.Ordinal));

        Assert.Contains(Scan(Runtime("Microsoft.CSharp.dll"), NoNetworkPolicy.BannedTypesAndMembers), o => o.Contains("System.Type.GetTypeFromCLSID(System.Guid)", StringComparison.Ordinal));

        List<string> wpf = Scan(Desktop("PresentationFramework.dll"), NoNetworkPolicy.BannedTypesAndMembers);
        Assert.Contains(wpf, o => o.Contains("uses System.Xml.XmlUrlResolver", StringComparison.Ordinal));
        Assert.Contains(wpf, o => o.Contains("System.Media.SoundPlayer.LoadAsync()", StringComparison.Ordinal));
        Assert.Contains(wpf, o => o.Contains("uses System.Runtime.InteropServices.NativeLibrary", StringComparison.Ordinal));

        // Chain building downloads certificates and revocation lists; the HTTP stack uses it for TLS.
        Assert.Contains(Scan(Runtime("System.Net.Http.dll"), NoNetworkPolicy.BannedTypesAndMembers), o => o.Contains("uses System.Security.Cryptography.X509Certificates.X509Chain -", StringComparison.Ordinal));

        Assert.Empty(Scan(Runtime("System.Collections.dll"), NoNetworkPolicy.BannedTypesAndMembers));
    }

    [Fact]
    public void FindsSideDoorsAroundTheNarrowRules()
    {
        // The Visual Basic runtime ships with WinForms and reaches the network itself (My.Computer.Network
        // downloads with WebClient and pings), yet code calling it would reference no System.Net assembly.
        List<string> vbForms = Scan(Desktop("Microsoft.VisualBasic.Forms.dll"), NoNetworkPolicy.BannedAssemblyReferences);
        Assert.Contains("Microsoft.VisualBasic.Forms.dll: references assembly System.Net.WebClient", vbForms);
        Assert.Contains("Microsoft.VisualBasic.Forms.dll: references assembly System.Net.Ping", vbForms);

        // C# code in this test assembly that uses each side door on a harmless target (ScannerFixtures).
        List<string> fixtures = Scan(typeof(ScannerFixtures).Assembly.Location, NoNetworkPolicy.BannedTypesAndMembers);
        Assert.Contains(fixtures, o => o.Contains("uses Microsoft.VisualBasic.Strings - the Visual Basic runtime", StringComparison.Ordinal));
        Assert.Contains(fixtures, o => o.Contains("uses Microsoft.CSharp.RuntimeBinder.", StringComparison.Ordinal));
        Assert.Contains(fixtures, o => o.Contains("uses System.Reflection.MethodBase.Invoke(System.Object,System.Object[]) -", StringComparison.Ordinal));
        Assert.Contains(fixtures, o => o.Contains("uses System.Linq.Expressions.Expression{System.Func{System.Int32}}.Compile() -", StringComparison.Ordinal));
        Assert.Contains(fixtures, o => o.Contains("uses System.Diagnostics.Process.GetProcessById(System.Int32,System.String) - the machine-name parameter", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("System.Diagnostics.Process", "GetProcessById", "System.Int32,System.String", true)]
    [InlineData("System.Diagnostics.Process", "GetProcessById", "System.Int32", false)]
    [InlineData("System.Diagnostics.Process", "GetProcesses", "System.String", true)]
    [InlineData("System.Diagnostics.Process", "GetProcesses", "", false)]
    [InlineData("System.Diagnostics.Process", "GetProcessesByName", "System.String", false)]
    [InlineData("System.ServiceProcess.ServiceController", ".ctor", "System.String,System.String", true)]
    [InlineData("System.ServiceProcess.ServiceController", ".ctor", "System.String", false)]
    [InlineData("System.Diagnostics.EventLog", ".ctor", "System.String,System.String,System.String", true)]
    [InlineData("System.Diagnostics.EventLog", "SourceExists", "System.String", false)]
    [InlineData("System.Media.SoundPlayer", ".ctor", "System.String", true)]
    [InlineData("System.Media.SoundPlayer", ".ctor", "System.IO.Stream", false)]
    [InlineData("System.Xml.XmlReader", "Create", "System.String,System.Xml.XmlReaderSettings", true)]
    [InlineData("System.Xml.XmlReader", "Create", "System.IO.Stream,System.Xml.XmlReaderSettings", false)]
    [InlineData("System.Xml.XPath.XPathDocument", ".ctor", "System.String", true)]
    [InlineData("System.Data.DataSet", "ReadXml", "System.String", true)]
    [InlineData("System.Type", "GetType", "System.String", true)]
    [InlineData("System.Type", "GetType", "", false)]
    [InlineData("System.Reflection.Assembly", "GetType", "System.String,System.Boolean", true)]
    [InlineData("System.Reflection.Assembly", "GetName", "", false)]
    [InlineData("System.Reflection.PropertyInfo", "SetValue", "System.Object,System.Object", true)]
    [InlineData("System.Delegate", "CreateDelegate", "System.Type,System.Reflection.MethodInfo", true)]
    [InlineData("System.RuntimeMethodHandle", "GetFunctionPointer", "", true)]
    [InlineData("System.AppDomain", "CreateInstanceAndUnwrap", "System.String,System.String", true)]
    [InlineData("System.AppDomain", "GetAssemblies", "", false)]
    [InlineData("System.Linq.Expressions.LambdaExpression", "Compile", "", true)]

    // Lookups without invoking stay allowed: the System.Text.Json source generator emits these.
    [InlineData("System.Type", "GetProperty", "System.String,System.Reflection.BindingFlags,System.Reflection.Binder,System.Type,System.Type[],System.Reflection.ParameterModifier[]", false)]
    [InlineData("System.Type", "GetConstructor", "System.Reflection.BindingFlags,System.Reflection.Binder,System.Type[],System.Reflection.ParameterModifier[]", false)]
    public void ClassifiesMemberReferences(string type, string name, string parameters, bool banned) =>
        Assert.Equal(banned, NoNetworkPolicy.BannedMemberReason(new ScannedMember(type, name, parameters)) is not null);

    [Theory]
    [InlineData("System.Windows.Forms.PictureBox", "System.Windows.Forms", true)]
    [InlineData("System.Windows.Forms.LinkLabel+Link", "System.Windows.Forms", true)]
    [InlineData("System.Windows.Forms.Label", "System.Windows.Forms", false)]
    [InlineData("Microsoft.VisualBasic.Devices.Network", "Microsoft.VisualBasic.Devices", true)]
    [InlineData("Microsoft.VisualBasicX.Thing", "Microsoft.VisualBasicX", false)]
    [InlineData("System.Reflection.Emit.DynamicMethod", "System.Reflection.Emit", true)]
    [InlineData("System.Reflection.MethodInvoker", "System.Reflection", true)]
    [InlineData("System.Reflection.MethodInfo", "System.Reflection", false)]
    public void ClassifiesTypeReferences(string fullName, string ns, bool banned) =>
        Assert.Equal(banned, NoNetworkPolicy.BannedTypeReason(new ScannedType(fullName, ns)) is not null);

    [Theory]
    [InlineData("mciSendStringW", true)]
    [InlineData("mciSendCommandA", true)]
    [InlineData("PlaySoundW", false)]
    [InlineData("CoCreateInstance", true)]
    [InlineData("SHCoCreateInstance", true)]
    [InlineData("OleCreateLink", true)]
    [InlineData("OleCreatePictureIndirect", false)]
    [InlineData("CreateRemoteThreadEx", true)]
    [InlineData("WriteProcessMemory", true)]
    [InlineData("ReadProcessMemory", false)]
    [InlineData("LoadLibraryExW", true)]
    [InlineData("GetModuleHandleW", false)]
    public void ClassifiesNativeEntryPoints(string entryPoint, bool banned) =>
        Assert.Equal(banned, NoNetworkPolicy.BannedEntryPointReason(entryPoint) is not null);

    [Theory]
    [InlineData("ShellExecuteExW", true)]
    [InlineData("CreateProcessInternalW", true)]
    [InlineData("NtCreateProcess", true)]
    [InlineData("WinHelpW", true)]
    [InlineData("ShellExec_RunDLLW", true)]
    [InlineData("Shell_NotifyIconW", false)]
    [InlineData("OpenProcess", false)]
    public void RecognisesNativeProgramStarters(string entryPoint, bool starter) =>
        Assert.Equal(starter, NoNetworkPolicy.IsNativeProcessStarter(entryPoint));

    [Fact]
    public void FindsPInvokesIntoDllsOutsideTheAllowlist()
    {
        List<string> sockets = Scan(Runtime("System.Net.Sockets.dll"), NoNetworkPolicy.DisallowedNativeLibraries);
        Assert.Contains(sockets, o => o.Contains("calls into ws2_32.dll!", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(sockets, o => o.Contains("kernel32", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(Scan(Runtime("System.Net.Http.dll"), NoNetworkPolicy.DisallowedNativeLibraries), o => o.Contains("calls into winhttp.dll!WinHttpOpen", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("user32", "user32")]
    [InlineData("USER32.dll", "user32")]
    [InlineData(" Kernel32.DLL ", "kernel32")]
    [InlineData("hhctrl.ocx", "hhctrl.ocx")]
    public void NormalizesNativeLibraryNames(string module, string expected) =>
        Assert.Equal(expected, NoNetworkPolicy.NormalizeLibrary(module));

    [Fact]
    public void FindsNativeFunctionsThatEscapeTheAllowlists()
    {
        Assert.Contains(Scan(Runtime("System.Private.CoreLib.dll"), NoNetworkPolicy.BannedNativeEntryPoints), o => o.Contains("kernel32.dll!LoadLibraryExW", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(Scan(Runtime("Microsoft.Win32.Registry.dll"), NoNetworkPolicy.BannedNativeEntryPoints), o => o.Contains("!RegConnectRegistryW", StringComparison.Ordinal));
        Assert.Contains(Scan(Desktop("System.Private.Windows.Core.dll"), NoNetworkPolicy.BannedNativeEntryPoints), o => o.Contains("!CoCreateInstance -", StringComparison.Ordinal));
    }

    [Fact]
    public void FindsComClassesOutsideTheAllowlist()
    {
        Assert.Contains(
            "Accessibility.dll: [ComImport] class Accessibility.CAccPropServicesClass creates CLSID {B5F8350B-0548-48B1-A6EE-88BD00B4A5E7}",
            Scan(Desktop("Accessibility.dll"), NoNetworkPolicy.DisallowedComClasses));
        Assert.Contains(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), NoNetworkPolicy.AllowedComClasses.Keys);
    }

    [Fact]
    public void FindsProcessStartCallsOutsideTheAllowedType()
    {
        // Application.Restart() relaunches the app with Process.Start (a MemberRef call into another assembly).
        List<string> winForms = Scan(Desktop("System.Windows.Forms.dll"), a => NoNetworkPolicy.ProcessStartsOutside(a, SafeShell));
        Assert.Contains("System.Windows.Forms.dll: System.Windows.Forms.Application.Restart() calls System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo) (call)", winForms);

        // The same code is accepted when it is inside the allowed type.
        Assert.Empty(Scan(Desktop("System.Windows.Forms.dll"), a => NoNetworkPolicy.ProcessStartsOutside(a, "System.Windows.Forms.Application")));

        // Inside System.Diagnostics.Process.dll the overloads call each other (MethodDef calls), and the
        // native ShellExecuteExW is reached through a LibraryImport wrapper that must be looked through.
        List<string> process = Scan(Runtime("System.Diagnostics.Process.dll"), a => NoNetworkPolicy.ProcessStartsOutside(a, SafeShell));
        Assert.Contains("System.Diagnostics.Process.dll: System.Diagnostics.Process.Start(System.String) calls System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo) (call)", process);
        Assert.Contains(process, o => o.Contains("calls native shell32.dll!ShellExecuteExW", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(process, o => o.Contains("calls native kernel32.dll!CreateProcessW", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(process, o => o.StartsWith("System.Diagnostics.Process.dll: Interop+", StringComparison.Ordinal));
    }

    [Fact]
    public void DecodesCallsConstructorsAndDelegates()
    {
        using var scanner = new AssemblyScanner(typeof(ScannerFixtures).Assembly.Location);
        List<string> sites = SitesFrom(scanner, nameof(ScannerFixtures.CallsConstructsAndTakesDelegate));

        Assert.Contains("newobj System.Collections.Generic.List{System.Int32}..ctor()", sites);
        Assert.Contains("call System.Math.Abs(System.Int32)", sites);
        Assert.Contains("callvirt System.Collections.Generic.List{System.Int32}.Add(`0)", sites);
        Assert.Contains("ldftn System.Math.Max(System.Int32,System.Int32)", sites);
    }

    [Fact]
    public void DecodesPastSwitchJumpTables()
    {
        using var scanner = new AssemblyScanner(typeof(ScannerFixtures).Assembly.Location);
        Assert.Contains("call System.Environment.get_ProcessorCount()", SitesFrom(scanner, nameof(ScannerFixtures.CallsAfterSwitch)));
    }

    [Theory]
    [InlineData("System.Private.CoreLib.dll")]
    [InlineData("System.Windows.Forms.dll")]
    public void DecodesEveryMethodBodyOfLargeFrameworkAssemblies(string fileName)
    {
        // Hundreds of thousands of instructions of every kind: a wrong operand size anywhere would
        // desynchronise the decoder and throw (unknown opcode or invalid token).
        string path = File.Exists(Runtime(fileName)) ? Runtime(fileName) : Desktop(fileName);
        using var scanner = new AssemblyScanner(path);
        Assert.True(scanner.CallSites().Count > 50_000, $"Only {scanner.CallSites().Count} call sites decoded in {fileName}.");
    }

    [Fact]
    public void SeesLibraryImportStubsAndTheirRealCallers()
    {
        using var scanner = new AssemblyScanner(typeof(ScannerFixtures).Assembly.Location);
        List<PInvokeImport> pInvokes = [.. scanner.PInvokes().Where(p => p.Method.IsInType(typeof(ScannerFixtures).FullName!))];

        PInvokeImport marshalling = Assert.Single(pInvokes, p => p.EntryPoint == "GetModuleHandleW");
        Assert.Equal("kernel32", NoNetworkPolicy.NormalizeLibrary(marshalling.Module));
        Assert.Contains(NoNetworkPolicy.EffectiveCallers(scanner, marshalling), s => s.Caller.Name == nameof(ScannerFixtures.CallsMarshallingLibraryImport));
        Assert.Contains(scanner.Metadata.MethodDefinitions, scanner.IsLibraryImportWrapper);

        PInvokeImport blittable = Assert.Single(pInvokes, p => p.EntryPoint == "GetDoubleClickTime");
        Assert.Equal("user32", NoNetworkPolicy.NormalizeLibrary(blittable.Module));
        Assert.Contains(NoNetworkPolicy.EffectiveCallers(scanner, blittable), s => s.Caller.Name == nameof(ScannerFixtures.CallsBlittableLibraryImport));

        Assert.Empty(NoNetworkPolicy.DisallowedNativeLibraries(scanner));
    }

    [Fact]
    public void LooksThroughOnlyStaticLibraryImportWrappers()
    {
        using var scanner = new AssemblyScanner(typeof(ScannerFixtures).Assembly.Location);
        MethodDefinitionHandle fake = Assert.Single(
            scanner.Metadata.MethodDefinitions,
            h => scanner.Describe(h).DeclaringType == typeof(ScannerFixtures.FakeWrapper).FullName && scanner.Describe(h).Name == nameof(ScannerFixtures.FakeWrapper.Run));
        PInvokeImport blittable = Assert.Single(
            scanner.PInvokes(),
            p => p.Method.IsInType(typeof(ScannerFixtures).FullName!) && p.EntryPoint == "GetDoubleClickTime");

        // It carries the generator's attribute but is an instance method, so it counts as a real caller.
        Assert.False(scanner.IsLibraryImportWrapper(fake));
        Assert.Contains(NoNetworkPolicy.EffectiveCallers(scanner, blittable), s => s.CallerHandle == fake);
    }

    [Fact]
    public void DepsCheckRejectsThirdPartyPackagesAndUnknownRuntimes()
    {
        const string deps = """
            {
              "libraries": {
                "BlindsDown/1.0.0": { "type": "project" },
                "System.ServiceProcess.ServiceController/10.0.0": { "type": "package" },
                "Newtonsoft.Json/13.0.3": { "type": "package" },
                "runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.0": { "type": "runtimepack" },
                "runtimepack.Microsoft.AspNetCore.App.Runtime.win-x64/10.0.0": { "type": "runtimepack" },
                "Vendor.Sdk/1.0.0": { "type": "reference" }
              }
            }
            """;

        List<string> offenders = [.. NoNetworkPolicy.DisallowedPackages(deps, "Test.deps.json")];

        Assert.Equal(3, offenders.Count);
        Assert.Contains(offenders, o => o.StartsWith("Test.deps.json: ships Newtonsoft.Json/13.0.3 -", StringComparison.Ordinal));
        Assert.Contains(offenders, o => o.StartsWith("Test.deps.json: ships runtimepack.Microsoft.AspNetCore.App.Runtime.win-x64/10.0.0 -", StringComparison.Ordinal));
        Assert.Contains(offenders, o => o.StartsWith("Test.deps.json: ships Vendor.Sdk/1.0.0 -", StringComparison.Ordinal));
    }

    [Fact]
    public void DepsCheckListsProjectAssembliesFromEveryTarget()
    {
        const string deps = """
            {
              "targets": {
                ".NETCoreApp,Version=v10.0": {},
                ".NETCoreApp,Version=v10.0/win-x64": {
                  "BlindsDown/1.0.0": { "runtime": { "BlindsDown.dll": {} } },
                  "BlindsDown.Core/1.0.0": { "runtime": { "BlindsDown.Core.dll": {} } },
                  "runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.0": { "runtime": { "System.Net.Http.dll": {} } }
                }
              },
              "libraries": {
                "BlindsDown/1.0.0": { "type": "project" },
                "BlindsDown.Core/1.0.0": { "type": "project" },
                "runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.0": { "type": "runtimepack" }
              }
            }
            """;

        Assert.Equal(["BlindsDown.Core.dll", "BlindsDown.dll"], NoNetworkPolicy.ProjectAssemblies(deps));
    }

    private static string Runtime(string fileName) => Path.Combine(RuntimeDirectory, fileName);

    private static string Desktop(string fileName) => Path.Combine(DesktopDirectory, fileName);

    private static List<string> Scan(string path, Func<AssemblyScanner, IEnumerable<string>> rule)
    {
        Assert.True(File.Exists(path), $"Framework assembly not found: {path}");
        using var scanner = new AssemblyScanner(path);
        return [.. rule(scanner)];
    }

    private static List<string> SitesFrom(AssemblyScanner scanner, string methodName) =>
        [.. scanner.CallSites()
            .Where(s => s.Caller.IsInType(typeof(ScannerFixtures).FullName!) && s.Caller.Name == methodName)
            .Select(s => $"{s.Instruction} {s.Callee}")];
}
