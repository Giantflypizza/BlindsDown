using BlindsDown.Core.Locking;
using BlindsDown.Helper;
using BlindsDown.Infrastructure;

namespace BlindsDown.Tests.NoNetwork;

/// <summary>
/// The "BlindsDown never connects to the internet" guarantee, checked on the compiled program in CI.
/// Every assembly built from this repository that ships in the app (the "project" entries of
/// BlindsDown.deps.json) is read as metadata and IL, never loaded or run.
///
/// If one of these fails, the message lists each offending reference and why it is banned. Don't
/// relax a rule to make it pass: remove the code, or open an issue to discuss an exception.
/// </summary>
public sealed class NoNetworkTests
{
    private static readonly string AppAssemblyPath = typeof(SafeShell).Assembly.Location;
    private static readonly string CoreAssemblyPath = typeof(LockEngine).Assembly.Location;
    private static readonly string DepsJsonPath = Path.ChangeExtension(AppAssemblyPath, ".deps.json");

    /// <summary>SafeShell is the one audited place that may start programs (taken from the type, so a rename can't silently skip the rule).</summary>
    private static readonly string SafeShellType = typeof(SafeShell).FullName!;

    /// <summary>The one place that registers and starts the helper service (always BlindsDown.exe --service).</summary>
    private static readonly string ServiceControlType = typeof(HelperServiceControl).FullName!;

    /// <summary>The one place that opens a pipe by path: the helper's local pipe, \\.\pipe\...</summary>
    private static readonly string PipeClientType = typeof(HelperClient).FullName!;

    /// <summary>SERVICE_FAILURE_ACTIONS: its command would run as LocalSystem whenever the helper service fails.</summary>
    private static readonly string FailureActionsType = typeof(HelperNative.ServiceFailureActions).FullName!;

    /// <summary>This test project: the app may share its internal code with it, because it never ships.</summary>
    private static readonly string TestAssemblyName = typeof(NoNetworkTests).Assembly.GetName().Name!;

    [Fact]
    public void ScansEveryAssemblyBuiltFromThisRepository()
    {
        IReadOnlyList<string> paths = AppAssemblyPaths();

        Assert.Contains(paths, p => Path.GetFileName(p) == "BlindsDown.dll");
        Assert.Contains(paths, p => Path.GetFileName(p) == "BlindsDown.Core.dll");
        foreach (string path in paths)
        {
            using var scanner = new AssemblyScanner(path);
            string modules = string.Join(", ", scanner.PInvokes().Select(p => NoNetworkPolicy.NormalizeLibrary(p.Module)).Distinct().Order());
            string comClasses = string.Join(", ", scanner.ComImportClasses().Select(c => $"{c.TypeName} {{{c.Clsid}}}"));
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{scanner.FileName}: {scanner.CallSites().Count} call sites; native DLLs: [{modules}]; COM classes: [{comClasses}]");
        }
    }

    [Fact]
    public void NoReferenceToNetworkingAssemblies() =>
        AssertNone(
            "BlindsDown must not reference networking assemblies (System.Net.*, System.ServiceModel*, Microsoft.AspNetCore*, Windows.Web*, Windows.Networking*).",
            ScanApp(NoNetworkPolicy.BannedAssemblyReferences));

    [Fact]
    public void NoTypesFromNetworkingNamespaces() =>
        AssertNone(
            "BlindsDown must not use or define types in networking namespaces (System.Net*, Windows.Networking*, Windows.Web*, System.ServiceModel*, Microsoft.AspNetCore*).",
            ScanApp(NoNetworkPolicy.BannedNamespaces));

    [Fact]
    public void NoTypesOrMembersThatCanFetchUrlsOrLoadCode() =>
        AssertNone(
            "BlindsDown must not use APIs that can fetch URLs, open a browser or a help file, activate COM by CLSID/ProgID, reach another computer "
            + "(pipe clients, machine-name overloads), browse network locations (file dialogs), or load or call code by name at run time (where this scan can't follow it).",
            ScanApp(NoNetworkPolicy.BannedTypesAndMembers));

    [Fact]
    public void PInvokesOnlyIntoAllowlistedSystemDlls() =>
        AssertNone(
            $"BlindsDown may only call into these Windows DLLs: {string.Join(", ", NoNetworkPolicy.AllowedNativeLibraries.Order())}.",
            ScanApp(NoNetworkPolicy.DisallowedNativeLibraries));

    [Fact]
    public void NoNativeFunctionsThatEscapeTheAllowlists() =>
        AssertNone(
            "BlindsDown must not import native functions that load DLLs, create COM objects by CLSID, open URLs, connect to other computers or run code in other processes.",
            ScanApp(NoNetworkPolicy.BannedNativeEntryPoints));

    [Fact]
    public void ComClassesOnlyFromAllowlist() =>
        AssertNone(
            "BlindsDown may only create these COM classes: "
            + string.Join("; ", NoNetworkPolicy.AllowedComClasses.Select(c => $"{{{c.Key.ToString().ToUpperInvariant()}}} {c.Value}")) + ".",
            ScanApp(NoNetworkPolicy.DisallowedComClasses));

    [Fact]
    public void ProgramsAreStartedOnlyBySafeShell() =>
        AssertNone(
            $"Only {SafeShellType} may start programs (Process.Start, ShellExecute, CreateProcess, WinExec). Add a fixed Settings page to SafeShell instead.",
            ScanApp(a => NoNetworkPolicy.ProcessStartsOutside(a, SafeShellType)));

    [Fact]
    public void ServicesAreRegisteredAndStartedOnlyByTheHelperServiceCode() =>
        AssertNone(
            $"Only {ServiceControlType} may register, change or start a Windows service (CreateService, ChangeServiceConfig, ChangeServiceConfig2, "
            + "StartService, ServiceController.Start): Windows runs the registered command line as LocalSystem, and it must only ever be BlindsDown.exe --service.",
            ScanApp(a => NoNetworkPolicy.ServiceStartsOutside(a, ServiceControlType)));

    [Fact]
    public void TheHelperServiceNeverGetsAFailureCommand() =>
        AssertNone(
            $"Nothing may set {FailureActionsType}.Command: Windows would run that command as LocalSystem whenever the helper service fails.",
            ScanApp(a => NoNetworkPolicy.NonNullFieldWrites(a, FailureActionsType, nameof(HelperNative.ServiceFailureActions.Command))));

    [Fact]
    public void MachineNamesAreAlwaysThisComputer() =>
        AssertNone(
            "Native functions that take a computer name (OpenSCManagerW, OpenEventLogW, ...) must be called with a constant null there, "
            + "which means this computer; pass null directly in the call, not through a variable or a helper parameter.",
            ScanApp(NoNetworkPolicy.RemoteMachineCalls));

    [Fact]
    public void OnlyTheHelperClientOpensPipesByPath() =>
        AssertNone(
            $"Only {PipeClientType} may call CreateFileW, WaitNamedPipeW and the other native functions that open a path, "
            + @"because a path can name another computer (\\server\pipe\name); it opens only the helper's local pipe.",
            ScanApp(a => NoNetworkPolicy.PathOpenersOutside(a, PipeClientType)));

    [Fact]
    public void NativeFunctionsAreCalledOnlyFromTheirOwnAssembly() =>
        AssertNone(
            "The rules above find the callers of a native function in the assembly that declares it, so no P/Invoke or LibraryImport method may be "
            + $"public or protected, and app assemblies may share their internal code only with {TestAssemblyName}. Keep native declarations "
            + "internal to the assembly that calls them.",
            ScanApp(a => NoNetworkPolicy.NativeImportsCallableFromOtherAssemblies(a, TestAssemblyName)));

    [Fact]
    public void NoThirdPartyRuntimePackages()
    {
        Assert.True(File.Exists(DepsJsonPath), $"Expected {DepsJsonPath} next to BlindsDown.dll.");

        AssertNone(
            "Only Microsoft's .NET runtime, code from this repository and the reviewed packages "
            + $"({string.Join(", ", NoNetworkPolicy.AllowedRuntimePackages.Keys.Order())}) may ship in BlindsDown.",
            NoNetworkPolicy.DisallowedPackages(File.ReadAllText(DepsJsonPath), Path.GetFileName(DepsJsonPath)));
    }

    /// <summary>The two app assemblies the tests reference directly, plus any other project assembly
    /// listed in BlindsDown.deps.json (so a new project is scanned without anyone remembering to add it).</summary>
    private static IReadOnlyList<string> AppAssemblyPaths()
    {
        string directory = Path.GetDirectoryName(AppAssemblyPath)!;
        IEnumerable<string> fromDeps = File.Exists(DepsJsonPath)
            ? NoNetworkPolicy.ProjectAssemblies(File.ReadAllText(DepsJsonPath)).Select(f => Path.Combine(directory, f))
            : [];
        return [.. new[] { AppAssemblyPath, CoreAssemblyPath }.Concat(fromDeps).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static List<string> ScanApp(Func<AssemblyScanner, IEnumerable<string>> rule)
    {
        var offenders = new List<string>();
        foreach (string path in AppAssemblyPaths())
        {
            Assert.True(File.Exists(path), $"Expected app assembly {path}.");
            using var scanner = new AssemblyScanner(path);
            offenders.AddRange(rule(scanner));
        }

        return offenders;
    }

    private static void AssertNone(string rule, IEnumerable<string> offenders)
    {
        List<string> distinct = [.. offenders.Distinct()];
        if (distinct.Count > 0)
        {
            Assert.Fail($"{rule}{Environment.NewLine}{distinct.Count} offender(s):{Environment.NewLine}  - {string.Join(Environment.NewLine + "  - ", distinct)}");
        }
    }
}
