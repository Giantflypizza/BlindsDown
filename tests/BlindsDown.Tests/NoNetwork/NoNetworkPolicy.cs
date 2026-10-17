using System.Collections.Frozen;
using System.Reflection.Metadata;
using System.Text.Json;

namespace BlindsDown.Tests.NoNetwork;

/// <summary>
/// The rules behind "BlindsDown never connects to the internet", applied to compiled assemblies.
/// BannedSymbols.txt stops banned APIs at compile time; these rules check the OUTPUT, so they also
/// catch what an analyzer can't see: transitive type forwards, P/Invoke targets, COM classes, IL
/// call sites, and packages that would ship with the app.
///
/// Every rule returns offender descriptions ("BlindsDown.dll: ... - why"), so a failing test says
/// exactly what to remove and why. An empty result means the rule holds.
/// </summary>
internal static class NoNetworkPolicy
{
    /// <summary>
    /// The only native DLLs BlindsDown may P/Invoke into. None of them is a networking library
    /// (ws2_32, winhttp, wininet, urlmon, dnsapi, iphlpapi, netapi32, ... are all absent).
    /// </summary>
    public static readonly FrozenSet<string> AllowedNativeLibraries = new[]
    {
        "user32", "kernel32", "advapi32", "shell32", "shlwapi", "shcore", "ole32", "oleaut32", "combase",
        "propsys", "cfgmgr32", "setupapi", "ntdll", "mfplat", "mfsensorgroup", "dwmapi", "uxtheme", "gdi32",
        "winmm", "comctl32", "wtsapi32",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The only COM classes BlindsDown may create. A COM class can be anything (WinHTTP, MSXML,
    /// BITS...), so each one is listed by CLSID with what it is.</summary>
    public static readonly FrozenDictionary<Guid, string> AllowedComClasses = new Dictionary<Guid, string>
    {
        [new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] = "MMDeviceEnumerator (Core Audio: lists microphones)",
        [new Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")] = "PolicyConfigClient (Stage 2: the Sound settings endpoint disable)",
    }.ToFrozenDictionary();

    /// <summary>
    /// NuGet packages allowed to ship inside the app, each with the reason. Everything else that ships
    /// comes from the .NET runtime itself or from this repository. Adding a package here is a deliberate,
    /// reviewed decision.
    /// </summary>
    public static readonly FrozenDictionary<string, string> AllowedRuntimePackages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["System.ServiceProcess.ServiceController"] = "Microsoft's Windows service API, for the Stage 2 helper service",
        ["System.Diagnostics.EventLog"] = "Microsoft package required by System.ServiceProcess.ServiceController",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] BannedAssemblyPrefixes =
        ["System.Net", "System.ServiceModel", "Microsoft.AspNetCore", "Windows.Web", "Windows.Networking"];

    private static readonly string[] BannedNamespacePrefixes =
        ["System.Net", "System.ServiceModel", "Microsoft.AspNetCore", "Windows.Web", "Windows.Networking"];

    private const string ComActivation = "creates COM/WinRT objects by CLSID or name at run time, bypassing the [ComImport] class allowlist";
    private const string RunTimeCode = "loads code at run time that this scan can't see";
    private const string UrlLoader = "a string here is a URI and can be http://";
    private const string ByName = "calls a member chosen by name at run time, which this scan can't check (e.g. Process.Start found with GetMethod); call it directly";
    private const string RemoteMachine = "the machine-name parameter connects to another computer over the network; use the overload without it";
    private const string OtherProcess = "runs code inside another process, which may be one that has network access";

    /// <summary>
    /// Whole namespaces that are side doors around the narrower rules: they reach banned functionality
    /// under names this scan would otherwise not recognise. Child namespaces count too.
    /// </summary>
    private static readonly FrozenDictionary<string, string> BannedTypeNamespaces = new Dictionary<string, string>
    {
        // Part of the shared framework, so a C# project can call it without adding any reference.
        ["Microsoft.VisualBasic"] = "the Visual Basic runtime wraps banned APIs: Interaction.CreateObject/GetObject activate COM by ProgID or moniker, "
            + "Interaction.Shell starts programs, Devices.Network downloads files over HTTP, Devices.Audio.Play accepts URLs",
        ["Microsoft.CSharp.RuntimeBinder"] = "'dynamic' " + ByName,
        ["System.Reflection.Emit"] = "generates code at run time that this scan can't see",
    }.ToFrozenDictionary();

    /// <summary>Types that can fetch URLs, open a browser, host web content or call code by name. Nested types count too.</summary>
    private static readonly FrozenDictionary<string, string> BannedTypes = new Dictionary<string, string>
    {
        ["System.Windows.Forms.WebBrowser"] = "hosts a web browser",
        ["System.Windows.Forms.WebBrowserBase"] = "hosts a web browser",
        ["System.Windows.Forms.AxHost"] = "hosts ActiveX controls by CLSID (a web browser is one), " + ComActivation,
        ["System.Windows.Forms.PictureBox"] = "has a hidden HttpClient and loads images from URLs; draw images yourself",
        ["System.Windows.Forms.LinkLabel"] = "clickable links open a browser; show URLs as selectable text",
        ["System.Windows.Forms.Help"] = "Help.ShowHelp opens URLs",
        ["System.Windows.Forms.HelpProvider"] = "opens help URLs",
        ["System.Xml.XmlUrlResolver"] = "fetches URLs",
        ["System.Xml.Resolvers.XmlPreloadedResolver"] = "falls back to XmlUrlResolver, which fetches URLs",
        ["System.Runtime.InteropServices.NativeLibrary"] = "loads native DLLs at run time, bypassing the DLL allowlist",
        ["System.Reflection.MethodInvoker"] = ByName,
        ["System.Reflection.ConstructorInvoker"] = ByName,

        // Windows builds the chain with CertGetCertificateChain, which downloads missing issuer certificates
        // and revocation lists (AIA, CRL, OCSP) from the URLs written in the certificate.
        ["System.Security.Cryptography.X509Certificates.X509Chain"] = "building a certificate chain downloads certificates and revocation lists from the internet",
    }.ToFrozenDictionary();

    /// <summary>Single members that can reach the network, start other code, or load or call code this scan can't see.</summary>
    private static readonly MemberRule[] BannedMembers =
    [
        new("System.Media.SoundPlayer", ".ctor", "only play sounds from in-memory streams: " + UrlLoader, Parameters: "System.String"),
        new("System.Media.SoundPlayer", "set_SoundLocation", "only play sounds from in-memory streams: " + UrlLoader),
        new("System.Media.SoundPlayer", "LoadAsync", "only play sounds from in-memory streams"),
        new("System.Drawing.Image", "FromFile", "can read from network (UNC) paths; load images from embedded resources"),
        new("System.Type", "GetTypeFromCLSID", ComActivation),
        new("System.Type", "GetTypeFromProgID", ComActivation),
        new("System.Runtime.InteropServices.Marshal", "GetTypeFromCLSID", ComActivation),
        new("System.Runtime.InteropServices.Marshal", "BindToMoniker", ComActivation + "; a moniker can be a URL"),
        new("Microsoft.Win32.RegistryKey", "OpenRemoteBaseKey", "connects to another computer's registry over the network"),
        new("System.Security.Cryptography.X509Certificates.X509Certificate2", "Verify", "builds a certificate chain with online revocation checks"),

        new("System.Xml.XmlReader", "Create", UrlLoader, FirstParameterIsString: true),
        new("System.Xml.XmlTextReader", ".ctor", UrlLoader, FirstParameterIsString: true),
        new("System.Xml.XmlDocument", "Load", UrlLoader, FirstParameterIsString: true),
        new("System.Xml.Linq.XDocument", "Load", UrlLoader, FirstParameterIsString: true),
        new("System.Xml.Linq.XElement", "Load", UrlLoader, FirstParameterIsString: true),
        new("System.Xml.XPath.XPathDocument", ".ctor", UrlLoader, FirstParameterIsString: true),
        new("System.Xml.Xsl.XslCompiledTransform", "Load", UrlLoader, FirstParameterIsString: true),
        new("System.Xml.Xsl.XslCompiledTransform", "Transform", UrlLoader, FirstParameterIsString: true),
        new("System.Xml.Schema.XmlSchemaSet", "Add", UrlLoader + " (the schema URI)", Parameters: "System.String,System.String"),
        new("System.Data.DataSet", "ReadXml", UrlLoader, FirstParameterIsString: true),
        new("System.Data.DataSet", "ReadXmlSchema", UrlLoader, FirstParameterIsString: true),
        new("System.Data.DataTable", "ReadXml", UrlLoader, FirstParameterIsString: true),
        new("System.Data.DataTable", "ReadXmlSchema", UrlLoader, FirstParameterIsString: true),

        new("System.Type", "GetType", RunTimeCode + " (it loads assemblies by name); use typeof()", FirstParameterIsString: true),
        new("System.Activator", "CreateInstance", RunTimeCode + " (it loads assemblies by name)", FirstParameterIsString: true),
        new("System.Activator", "CreateInstanceFrom", RunTimeCode),
        new("System.AppDomain", "Load", RunTimeCode),
        new("System.AppDomain", "CreateInstance", RunTimeCode + " (it loads assemblies by name)"),
        new("System.AppDomain", "CreateInstanceAndUnwrap", RunTimeCode + " (it loads assemblies by name)"),
        new("System.AppDomain", "CreateInstanceFrom", RunTimeCode),
        new("System.AppDomain", "CreateInstanceFromAndUnwrap", RunTimeCode),
        new("System.AppDomain", "ExecuteAssembly", RunTimeCode),
        new("System.AppDomain", "ExecuteAssemblyByName", RunTimeCode),
        new("System.Reflection.Assembly", "Load", RunTimeCode),
        new("System.Reflection.Assembly", "LoadFrom", RunTimeCode),
        new("System.Reflection.Assembly", "LoadFile", RunTimeCode),
        new("System.Reflection.Assembly", "UnsafeLoadFrom", RunTimeCode),
        new("System.Reflection.Assembly", "LoadWithPartialName", RunTimeCode),
        new("System.Reflection.Assembly", "GetType", "finds a type by name; use typeof()", FirstParameterIsString: true),
        new("System.Reflection.Assembly", "CreateInstance", "creates a type chosen by name; use new"),
        new("System.Reflection.Module", "GetType", "finds a type by name; use typeof()", FirstParameterIsString: true),
        new("System.Runtime.Loader.AssemblyLoadContext", "LoadFromAssemblyName", RunTimeCode),
        new("System.Runtime.Loader.AssemblyLoadContext", "LoadFromAssemblyPath", RunTimeCode),
        new("System.Runtime.Loader.AssemblyLoadContext", "LoadFromNativeImagePath", RunTimeCode),
        new("System.Runtime.Loader.AssemblyLoadContext", "LoadFromStream", RunTimeCode),
        new("System.Runtime.Loader.AssemblyLoadContext", "LoadUnmanagedDllFromPath", RunTimeCode),

        // Calling code chosen by name. Looking members up (GetProperty, GetConstructor) stays allowed: the
        // System.Text.Json source generator does that for attribute metadata, and a lookup alone calls nothing.
        new("System.Reflection.MethodBase", "Invoke", ByName),
        new("System.Reflection.MethodInfo", "Invoke", ByName),
        new("System.Reflection.ConstructorInfo", "Invoke", ByName),
        new("System.Reflection.MethodInfo", "CreateDelegate", ByName),
        new("System.Delegate", "CreateDelegate", ByName),
        new("System.Reflection.PropertyInfo", "GetValue", ByName + " (a property is a method)"),
        new("System.Reflection.PropertyInfo", "SetValue", ByName + " (a property is a method)"),
        new("System.Reflection.FieldInfo", "SetValue", "changes a field chosen by name at run time (e.g. a private URL field)"),
        new("System.Reflection.FieldInfo", "SetValueDirect", "changes a field chosen by name at run time (e.g. a private URL field)"),
        new("System.Type", "InvokeMember", ByName),
        new("System.RuntimeMethodHandle", "GetFunctionPointer", ByName + " through a raw function pointer"),
        new("System.Linq.Expressions.LambdaExpression", "Compile", "compiles code at run time that this scan can't see"),
        new("System.Linq.Expressions.Expression", "Compile", "compiles code at run time that this scan can't see"),

        // The machine-name overloads of local APIs (the same category as RegConnectRegistry and WTSOpenServer below).
        new("System.Diagnostics.Process", "GetProcesses", RemoteMachine, Parameters: "System.String"),
        new("System.Diagnostics.Process", "GetProcessById", RemoteMachine, Parameters: "System.Int32,System.String"),
        new("System.Diagnostics.Process", "GetProcessesByName", RemoteMachine, Parameters: "System.String,System.String"),
        new("System.ServiceProcess.ServiceController", ".ctor", RemoteMachine, Parameters: "System.String,System.String"),
        new("System.ServiceProcess.ServiceController", "GetServices", RemoteMachine, Parameters: "System.String"),
        new("System.ServiceProcess.ServiceController", "GetDevices", RemoteMachine, Parameters: "System.String"),
        new("System.ServiceProcess.ServiceController", "set_MachineName", RemoteMachine),
        new("System.Diagnostics.EventLog", ".ctor", RemoteMachine, Parameters: "System.String,System.String"),
        new("System.Diagnostics.EventLog", ".ctor", RemoteMachine, Parameters: "System.String,System.String,System.String"),
        new("System.Diagnostics.EventLog", "GetEventLogs", RemoteMachine, Parameters: "System.String"),
        new("System.Diagnostics.EventLog", "Exists", RemoteMachine, Parameters: "System.String,System.String"),
        new("System.Diagnostics.EventLog", "SourceExists", RemoteMachine, Parameters: "System.String,System.String"),
        new("System.Diagnostics.EventLog", "Delete", RemoteMachine, Parameters: "System.String,System.String"),
        new("System.Diagnostics.EventLog", "DeleteEventSource", RemoteMachine, Parameters: "System.String,System.String"),
        new("System.Diagnostics.EventLog", "LogNameFromSourceName", RemoteMachine, Parameters: "System.String,System.String"),
        new("System.Diagnostics.EventLog", "CreateEventSource", RemoteMachine, Parameters: "System.String,System.String,System.String"),
        new("System.Diagnostics.EventLog", "set_MachineName", RemoteMachine),
        new("System.Diagnostics.EventSourceCreationData", "set_MachineName", RemoteMachine),
    ];

    /// <summary>
    /// Native functions that start programs. Like Process.Start they may only be reached through SafeShell,
    /// whose fixed list of Windows Settings pages is the one audited way BlindsDown opens anything.
    /// Names are matched with or without the A/W suffix.
    /// </summary>
    private static readonly FrozenSet<string> NativeProcessStarters = new[]
    {
        "ShellExecute", "ShellExecuteEx", "ShellExec_RunDLL", "CreateProcess", "CreateProcessAsUser", "CreateProcessWithLogon",
        "CreateProcessWithToken", "CreateProcessInternal", "WinExec", "LoadModule", "WinHelp", "NtCreateUserProcess",
        "RtlCreateUserProcess", "NtCreateProcess", "NtCreateProcessEx",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Native functions, most of them exported by ALLOWED DLLs, that would undo the other guarantees: loading
    /// arbitrary DLLs (escapes the DLL allowlist), creating COM objects by CLSID (escapes the COM allowlist),
    /// opening URLs, talking to another computer, or running code inside another process. Names are matched
    /// with or without the A/W suffix.
    /// </summary>
    private static readonly FrozenDictionary<string, string> BannedEntryPoints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["LoadLibrary"] = "loads arbitrary DLLs at run time, bypassing the DLL allowlist",
        ["LoadLibraryEx"] = "loads arbitrary DLLs at run time, bypassing the DLL allowlist",
        ["LoadPackagedLibrary"] = "loads arbitrary DLLs at run time, bypassing the DLL allowlist",
        ["LdrLoadDll"] = "loads arbitrary DLLs at run time, bypassing the DLL allowlist",
        ["GetProcAddress"] = "calls arbitrary native functions, bypassing the DLL allowlist",
        ["LdrGetProcedureAddress"] = "calls arbitrary native functions, bypassing the DLL allowlist",
        ["CoCreateInstance"] = ComActivation,
        ["CoCreateInstanceEx"] = ComActivation + "; can create objects on another computer (DCOM)",
        ["CoCreateInstanceFromApp"] = ComActivation,
        ["CoGetClassObject"] = ComActivation,
        ["CoGetObject"] = ComActivation + "; a moniker can be a URL",
        ["CoGetInstanceFromFile"] = ComActivation,
        ["CoGetInstanceFromIStorage"] = ComActivation,
        ["SHCoCreateInstance"] = ComActivation,
        ["OleCreate"] = ComActivation,
        ["OleCreateEx"] = ComActivation,
        ["OleCreateFromFile"] = ComActivation,
        ["OleCreateFromFileEx"] = ComActivation,
        ["OleCreateLink"] = ComActivation + "; a link can be a URL moniker",
        ["OleCreateLinkEx"] = ComActivation + "; a link can be a URL moniker",
        ["OleCreateLinkToFile"] = ComActivation,
        ["OleCreateLinkToFileEx"] = ComActivation,
        ["OleLoad"] = ComActivation,
        ["OleLoadFromStream"] = ComActivation,
        ["MkParseDisplayName"] = ComActivation + "; a moniker can be a URL",
        ["MkParseDisplayNameEx"] = ComActivation + "; a moniker can be a URL",
        ["BindMoniker"] = ComActivation + "; a moniker can be a URL",
        ["CreateURLMoniker"] = "binds to URLs",
        ["CreateURLMonikerEx"] = "binds to URLs",
        ["OleLoadPicturePath"] = "loads pictures from URLs",
        ["MFCreateSourceResolver"] = "Media Foundation's source resolver opens media from URLs, including http://",
        ["MFCreateSourceReaderFromURL"] = "opens media from URLs, including http://",
        ["RoGetActivationFactory"] = ComActivation,
        ["RoActivateInstance"] = ComActivation,
        ["DllGetClassObject"] = ComActivation,
        ["RegConnectRegistry"] = "connects to another computer's registry over the network",
        ["RegConnectRegistryEx"] = "connects to another computer's registry over the network",
        ["WTSOpenServer"] = "connects to another computer",
        ["WTSOpenServerEx"] = "connects to another computer",
        ["CM_Connect_Machine"] = "connects to another computer",

        // winmm is allowlisted for PlaySound, but its MCI command interface ("open http://... type mpegvideo")
        // plays media through DirectShow, which reads from URLs.
        ["mciSendString"] = "the media control interface can open media from URLs",
        ["mciSendCommand"] = "the media control interface can open media from URLs",

        // Code injected into another process (a browser, say) would use that process's network access.
        ["CreateRemoteThread"] = OtherProcess,
        ["CreateRemoteThreadEx"] = OtherProcess,
        ["WriteProcessMemory"] = OtherProcess,
        ["NtWriteVirtualMemory"] = OtherProcess,
        ["NtCreateThreadEx"] = OtherProcess,
        ["RtlCreateUserThread"] = OtherProcess,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>"user32", "USER32.dll" and "user32.DLL" are the same library.</summary>
    public static string NormalizeLibrary(string module)
    {
        string name = module.Trim().ToLowerInvariant();
        return name.EndsWith(".dll", StringComparison.Ordinal) ? name[..^4] : name;
    }

    /// <summary>(a) No reference to a networking assembly at all.</summary>
    public static IEnumerable<string> BannedAssemblyReferences(AssemblyScanner assembly) =>
        assembly.AssemblyReferenceNames()
            .Where(name => BannedAssemblyPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .Select(name => $"{assembly.FileName}: references assembly {name}");

    /// <summary>
    /// (b) No type from a networking namespace, wherever it lives. This matters on its own because some
    /// networking types are forwarded from non-networking assemblies (System.Net.WebUtility lives in
    /// System.Runtime), so (a) alone would miss them.
    /// </summary>
    public static IEnumerable<string> BannedNamespaces(AssemblyScanner assembly) =>
        assembly.TypeReferences().Select(t => (Type: t, Verb: "uses"))
            .Concat(assembly.TypeDefinitions().Select(t => (Type: t, Verb: "defines")))
            .Where(x => BannedNamespacePrefixes.Any(p => x.Type.Namespace.StartsWith(p, StringComparison.Ordinal)))
            .Select(x => $"{assembly.FileName}: {x.Verb} {x.Type.FullName}");

    /// <summary>(c) No type or member that can fetch URLs, open a browser, or load or call code this scan can't see.</summary>
    public static IEnumerable<string> BannedTypesAndMembers(AssemblyScanner assembly)
    {
        foreach (ScannedType type in assembly.TypeReferences())
        {
            if (BannedTypeReason(type) is string reason)
            {
                yield return $"{assembly.FileName}: uses {type.FullName} - {reason}";
            }
        }

        foreach (ScannedMember member in assembly.MemberReferences())
        {
            if (BannedMemberReason(member) is string reason)
            {
                yield return $"{assembly.FileName}: uses {member} - {reason}";
            }
        }
    }

    /// <summary>Why rule (c) rejects a referenced type, or null if it is allowed.</summary>
    public static string? BannedTypeReason(ScannedType type)
    {
        foreach ((string banned, string reason) in BannedTypes)
        {
            if (type.FullName == banned || type.FullName.StartsWith(banned + "+", StringComparison.Ordinal))
            {
                return reason;
            }
        }

        foreach ((string banned, string reason) in BannedTypeNamespaces)
        {
            if (type.Namespace == banned || type.Namespace.StartsWith(banned + ".", StringComparison.Ordinal))
            {
                return reason;
            }
        }

        return null;
    }

    /// <summary>Why rule (c) rejects a referenced method, or null if it is allowed.</summary>
    public static string? BannedMemberReason(ScannedMember member) =>
        Array.Find(BannedMembers, rule => rule.Matches(member))?.Reason;

    /// <summary>Why rule (d) rejects a native function wherever it is called from, or null if it is allowed.</summary>
    public static string? BannedEntryPointReason(string entryPoint) =>
        TryMatchEntryPoint(BannedEntryPoints.Keys, entryPoint, out string? name) ? BannedEntryPoints[name] : null;

    /// <summary>True for native functions that start programs: rule (f) lets only SafeShell reach them.</summary>
    public static bool IsNativeProcessStarter(string entryPoint) => TryMatchEntryPoint(NativeProcessStarters, entryPoint, out _);

    /// <summary>(d) Every P/Invoke (DllImport, or the stub LibraryImport generates) targets an allowlisted system DLL.</summary>
    public static IEnumerable<string> DisallowedNativeLibraries(AssemblyScanner assembly) =>
        assembly.PInvokes()
            .Where(p => !AllowedNativeLibraries.Contains(NormalizeLibrary(p.Module)))
            .Select(p => $"{assembly.FileName}: {p.Method} calls into {p.Module}!{p.EntryPoint}")
            .Concat(assembly.ModuleReferenceNames()
                .Where(m => !AllowedNativeLibraries.Contains(NormalizeLibrary(m)))
                .Select(m => $"{assembly.FileName}: references native module {m}"));

    /// <summary>
    /// (d, continued) Allowed DLLs still export a few functions that would undo the other rules. Imports by
    /// ordinal ("#12") are rejected because their target can't be checked by name.
    /// </summary>
    public static IEnumerable<string> BannedNativeEntryPoints(AssemblyScanner assembly)
    {
        foreach (PInvokeImport p in assembly.PInvokes())
        {
            if (p.EntryPoint.StartsWith('#'))
            {
                yield return $"{assembly.FileName}: {p.Method} imports {p.Module} by ordinal {p.EntryPoint} - import by name so it can be checked";
            }
            else if (BannedEntryPointReason(p.EntryPoint) is string reason)
            {
                yield return $"{assembly.FileName}: {p.Method} imports {p.Module}!{p.EntryPoint} - {reason}";
            }
        }
    }

    /// <summary>(e) Every [ComImport] class has an allowlisted CLSID.</summary>
    public static IEnumerable<string> DisallowedComClasses(AssemblyScanner assembly) =>
        assembly.ComImportClasses()
            .Where(c => c.Clsid is not Guid clsid || !AllowedComClasses.ContainsKey(clsid))
            .Select(c => c.Clsid is null
                ? $"{assembly.FileName}: [ComImport] class {c.TypeName} has no readable [Guid]"
                : $"{assembly.FileName}: [ComImport] class {c.TypeName} creates CLSID {{{c.Clsid.Value.ToString().ToUpperInvariant()}}}");

    /// <summary>
    /// (f) Programs are started only from <paramref name="allowedType"/> (SafeShell). Checked on the IL:
    /// every call, delegate creation or method token that refers to System.Diagnostics.Process.Start (any
    /// overload, static or instance), and every caller of a native process-starting function, must be
    /// code of that type (including its lambdas and nested types).
    /// </summary>
    public static IEnumerable<string> ProcessStartsOutside(AssemblyScanner assembly, string allowedType)
    {
        IReadOnlyList<CallSite> sites = assembly.CallSites();
        foreach (CallSite site in sites)
        {
            if (site.Callee.DeclaringType == "System.Diagnostics.Process" && site.Callee.Name == "Start" && !site.Caller.IsInType(allowedType))
            {
                yield return $"{assembly.FileName}: {site.Caller} calls {site.Callee} ({site.Instruction})";
            }
        }

        foreach (PInvokeImport p in assembly.PInvokes())
        {
            if (!IsNativeProcessStarter(p.EntryPoint))
            {
                continue;
            }

            foreach (CallSite site in EffectiveCallers(assembly, p))
            {
                if (!site.Caller.IsInType(allowedType))
                {
                    yield return $"{assembly.FileName}: {site.Caller} calls native {p.Module}!{p.EntryPoint} ({site.Instruction})";
                }
            }
        }
    }

    /// <summary>
    /// The code that really calls a P/Invoke. LibraryImport compiles into an inner P/Invoke stub plus a
    /// generated wrapper that marshals arguments; the wrapper is not "the caller", whoever calls the wrapper is.
    /// </summary>
    public static IEnumerable<CallSite> EffectiveCallers(AssemblyScanner assembly, PInvokeImport pInvoke)
    {
        ILookup<MethodDefinitionHandle, CallSite> callersOf = assembly.CallSites()
            .Where(s => !s.CalleeHandle.IsNil)
            .ToLookup(s => s.CalleeHandle);
        var visited = new HashSet<MethodDefinitionHandle> { pInvoke.Handle };
        var pending = new Stack<MethodDefinitionHandle>();
        pending.Push(pInvoke.Handle);
        while (pending.Count > 0)
        {
            foreach (CallSite site in callersOf[pending.Pop()])
            {
                if (!assembly.IsLibraryImportWrapper(site.CallerHandle))
                {
                    yield return site;
                }
                else if (visited.Add(site.CallerHandle))
                {
                    pending.Push(site.CallerHandle);
                }
            }
        }
    }

    /// <summary>
    /// (g) Nothing from outside Microsoft and this repository ships in the app. Reads an app's .deps.json,
    /// which lists every library the app loads: "project" (this repository, scanned by the other rules),
    /// "package" (NuGet; only <see cref="AllowedRuntimePackages"/>), and "runtimepack" (the .NET runtime
    /// itself, present when the app is built self-contained).
    ///
    /// Honest note: a self-contained BlindsDown.exe bundles the whole .NET runtime, and that runtime
    /// includes .NET's own networking DLLs (System.Net.Http.dll and friends). They are on disk but never
    /// loaded: the app's code doesn't reference them (rules a-f) and the runtime tripwire reports it if
    /// one is ever loaded.
    /// </summary>
    public static IEnumerable<string> DisallowedPackages(string depsJson, string fileName)
    {
        using JsonDocument document = JsonDocument.Parse(depsJson);
        if (!document.RootElement.TryGetProperty("libraries", out JsonElement libraries))
        {
            yield return $"{fileName}: no \"libraries\" section";
            yield break;
        }

        foreach (JsonProperty library in libraries.EnumerateObject())
        {
            string type = library.Value.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? string.Empty : string.Empty;
            string id = library.Name.Split('/')[0];
            string? problem = type switch
            {
                "project" => null,
                "package" when AllowedRuntimePackages.ContainsKey(id) => null,
                "package" => "a NuGet package that is not on the reviewed allowlist",
                "runtimepack" when id.StartsWith("runtimepack.Microsoft.NETCore.App.Runtime.", StringComparison.OrdinalIgnoreCase)
                    || id.StartsWith("runtimepack.Microsoft.WindowsDesktop.App.Runtime.", StringComparison.OrdinalIgnoreCase) => null,
                "runtimepack" => "a runtime pack other than .NET and Windows Desktop (e.g. ASP.NET Core)",
                _ => $"a library of type \"{type}\" (e.g. a referenced DLL file) that nobody reviewed",
            };
            if (problem is not null)
            {
                yield return $"{fileName}: ships {library.Name} - {problem}";
            }
        }
    }

    /// <summary>The DLL file names of the "project" libraries in a .deps.json: the assemblies built from this repository.</summary>
    public static IReadOnlyList<string> ProjectAssemblies(string depsJson)
    {
        using JsonDocument document = JsonDocument.Parse(depsJson);
        JsonElement root = document.RootElement;
        var projects = root.GetProperty("libraries").EnumerateObject()
            .Where(l => l.Value.TryGetProperty("type", out JsonElement t) && t.GetString() == "project")
            .Select(l => l.Name)
            .ToHashSet(StringComparer.Ordinal);

        var files = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty target in root.GetProperty("targets").EnumerateObject())
        {
            foreach (JsonProperty library in target.Value.EnumerateObject())
            {
                if (projects.Contains(library.Name) && library.Value.TryGetProperty("runtime", out JsonElement runtime))
                {
                    foreach (JsonProperty file in runtime.EnumerateObject())
                    {
                        files.Add(Path.GetFileName(file.Name));
                    }
                }
            }
        }

        return [.. files];
    }

    private static bool TryMatchEntryPoint(IEnumerable<string> names, string entryPoint, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? matched)
    {
        // Windows exports most text functions twice: FooA (ANSI) and FooW (UTF-16).
        string withoutSuffix = entryPoint.Length > 1 && entryPoint[^1] is 'A' or 'W' ? entryPoint[..^1] : entryPoint;
        matched = names.FirstOrDefault(n =>
            string.Equals(n, entryPoint, StringComparison.OrdinalIgnoreCase)
            || string.Equals(n, withoutSuffix, StringComparison.OrdinalIgnoreCase));
        return matched is not null;
    }

    /// <summary>"Ns.Type{System.Int32}" -> "Ns.Type": a call on a generic type names one instantiation of it.</summary>
    private static string WithoutTypeArguments(string typeName)
    {
        int brace = typeName.IndexOf('{', StringComparison.Ordinal);
        return brace < 0 ? typeName : typeName[..brace];
    }

    /// <summary>A banned member: one overload (<paramref name="Parameters"/>), the overloads whose first
    /// parameter is a string, or every overload. <paramref name="Type"/> is written without generic
    /// arguments, so "System.Linq.Expressions.Expression" also matches Expression{System.Func{System.Int32}}.</summary>
    private sealed record MemberRule(string Type, string Name, string Reason, string? Parameters = null, bool FirstParameterIsString = false)
    {
        public bool Matches(ScannedMember member) =>
            WithoutTypeArguments(member.DeclaringType) == Type
            && member.Name == Name
            && member.Parameters is not null
            && (Parameters is null || member.Parameters == Parameters)
            && (!FirstParameterIsString || member.Parameters.Split(',')[0] == "System.String");
    }
}
