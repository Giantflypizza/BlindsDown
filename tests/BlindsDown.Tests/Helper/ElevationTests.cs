using System.Security.Principal;
using BlindsDown.Helper;
using BlindsDown.Infrastructure;

namespace BlindsDown.Tests.Helper;

/// <summary>
/// Starting BlindsDown itself with administrator rights: only the three fixed helper options, each optionally with
/// the tray app's request ID, only through SafeShell. Nothing is ever started from these tests: every call here is
/// refused before Windows is asked.
/// </summary>
public sealed class ElevationTests
{
    /// <summary>A request ID of the form the tray app hands a setup run (Guid.ToString("N")).</summary>
    private const string Id = "0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData(nameof(ElevatedTask.InstallHelper), "--install-helper")]
    [InlineData(nameof(ElevatedTask.UpdateHelper), "--update-helper")]
    [InlineData(nameof(ElevatedTask.UninstallHelper), "--uninstall-helper")]
    public void EachTaskHasOneFixedOption(string taskName, string expected)
    {
        ElevatedTask task = Enum.Parse<ElevatedTask>(taskName);

        Assert.Equal(expected, ElevatedLauncher.Arguments(task));
        Assert.True(SafeShell.IsAllowedElevatedArguments(ElevatedLauncher.Arguments(task)));
    }

    [Fact]
    public void EveryTaskIsOnSafeShellsAllowlist()
    {
        Assert.All(Enum.GetValues<ElevatedTask>(), task => Assert.True(SafeShell.IsAllowedElevatedArguments(ElevatedLauncher.Arguments(task))));
        Assert.Throws<ArgumentOutOfRangeException>(() => ElevatedLauncher.Arguments((ElevatedTask)99));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("--service")]
    [InlineData("--restore")]
    [InlineData("--INSTALL-HELPER")]
    [InlineData(" --install-helper")]
    [InlineData("--install-helper ")]
    [InlineData("--install-helper --service")]
    [InlineData("--install-helper\0")]
    [InlineData("--install-helper & calc")]
    [InlineData("--uninstall-helper\" \"C:\\Windows\\System32\\cmd.exe")]
    [InlineData("--update")]
    [InlineData("ms-settings:sound")]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData("https://blindsdown-test.invalid/")]
    public void AnyOtherOptionIsRefusedWithoutStartingAnything(string? arguments)
    {
        Assert.False(SafeShell.IsAllowedElevatedArguments(arguments));
        Assert.Throws<ArgumentException>(() => SafeShell.StartSelfElevated(arguments!, 0));
    }

    [Theory]
    [InlineData(nameof(ElevatedTask.InstallHelper), "--install-helper --request-id 0123456789abcdef0123456789abcdef")]
    [InlineData(nameof(ElevatedTask.UpdateHelper), "--update-helper --request-id 0123456789abcdef0123456789abcdef")]
    [InlineData(nameof(ElevatedTask.UninstallHelper), "--uninstall-helper --request-id 0123456789abcdef0123456789abcdef")]
    public void EachTaskCanCarryTheTrayAppsRequestId(string taskName, string expected)
    {
        ElevatedTask task = Enum.Parse<ElevatedTask>(taskName);

        Assert.Equal(expected, ElevatedLauncher.Arguments(task, Id));
        Assert.True(SafeShell.IsAllowedElevatedArguments(expected));
        Assert.Equal(ElevatedLauncher.Arguments(task), ElevatedLauncher.Arguments(task, requestId: null));
    }

    [Fact]
    public void EveryIdOfTheTrayAppsFormIsOnSafeShellsAllowlist()
    {
        // The tray app's IDs are Guid.ToString("N"): 32 lowercase hex characters.
        for (int i = 0; i < 100; i++)
        {
            string id = Guid.NewGuid().ToString("N");
            Assert.True(ElevatedLauncher.IsRequestId(id));
            Assert.All(Enum.GetValues<ElevatedTask>(), task => Assert.True(SafeShell.IsAllowedElevatedArguments(ElevatedLauncher.Arguments(task, id))));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("0123456789abcdef0123456789abcdeF")]
    [InlineData("0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdef0")]
    [InlineData("01234567-89ab-cdef-0123-456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdef-2")]
    [InlineData(" 0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcde ")]
    [InlineData("0123456789abcdef0123456789abcdef --service")]
    [InlineData("0123456789abcdef\"123456789abcdef")]
    [InlineData("g123456789abcdef0123456789abcdef")]
    [InlineData("٠123456789abcdef0123456789abcdef")]
    public void AMalformedRequestIdIsRefusedBeforeAnythingStarts(string requestId)
    {
        Assert.False(ElevatedLauncher.IsRequestId(requestId));
        Assert.All(Enum.GetValues<ElevatedTask>(), task =>
        {
            Assert.Throws<ArgumentException>(() => ElevatedLauncher.Arguments(task, requestId));

            // Thrown by the call itself, before a thread or a permission prompt is started.
            Assert.Throws<ArgumentException>(() => { _ = ElevatedLauncher.RunAsync(task, owner: null, requestId); });
        });
    }

    [Theory]
    [InlineData("--install-helper --request-id 0123456789abcdef0123456789abcdef")]
    [InlineData("--update-helper --request-id 0123456789abcdef0123456789abcdef")]
    [InlineData("--uninstall-helper --request-id 0123456789abcdef0123456789abcdef")]
    [InlineData("--install-helper --request-id 00000000000000000000000000000000")]
    [InlineData("--uninstall-helper --request-id ffffffffffffffffffffffffffffffff")]
    public void AHelperOptionMayCarryOneRequestId(string arguments) => Assert.True(SafeShell.IsAllowedElevatedArguments(arguments));

    [Theory]
    [InlineData("--install-helper --request-id 0123456789ABCDEF0123456789abcdef")]
    [InlineData("--install-helper --request-id 0123456789abcdef0123456789abcdeF")]
    [InlineData("--install-helper --request-id 0123456789abcdef0123456789abcde")]
    [InlineData("--install-helper --request-id 0123456789abcdef0123456789abcdef0")]
    [InlineData("--install-helper --request-id 01234567-89ab-cdef-0123-456789abcdef")]
    [InlineData("--install-helper --request-id 0123456789abcdef0123456789abcdef-2")]
    [InlineData("--install-helper --request-id g123456789abcdef0123456789abcdef")]
    [InlineData("--install-helper --request-id ٠123456789abcdef0123456789abcdef")]
    [InlineData("--install-helper --request-id \"0123456789abcdef0123456789abcdef\"")]
    [InlineData("--install-helper --request-id")]
    [InlineData("--install-helper --request-id ")]
    [InlineData("--install-helper  --request-id 0123456789abcdef0123456789abcdef")]
    [InlineData("--install-helper --request-id  0123456789abcdef0123456789abcdef")]
    [InlineData("--install-helper\t--request-id 0123456789abcdef0123456789abcdef")]
    [InlineData(" --install-helper --request-id 0123456789abcdef0123456789abcdef")]
    [InlineData("--install-helper --request-id 0123456789abcdef0123456789abcdef ")]
    [InlineData("--install-helper --request-id 0123456789abcdef0123456789abcdef\n")]
    [InlineData("--install-helper --request-id 0123456789abcdef0123456789abcdef\r\n")]
    [InlineData("--install-helper --request-id 0123456789abcdef0123456789abcdef\0")]
    [InlineData("--install-helper --request-id=0123456789abcdef0123456789abcdef")]
    [InlineData("--install-helper --REQUEST-ID 0123456789abcdef0123456789abcdef")]
    [InlineData("--install-helper --requestid 0123456789abcdef0123456789abcdef")]
    [InlineData("--install-helper --request 0123456789abcdef0123456789abcdef")]
    [InlineData("--install-helper 0123456789abcdef0123456789abcdef")]
    [InlineData("--INSTALL-HELPER --request-id 0123456789abcdef0123456789abcdef")]
    [InlineData("--service --request-id 0123456789abcdef0123456789abcdef")]
    [InlineData("--restore --request-id 0123456789abcdef0123456789abcdef")]
    [InlineData("--install-helper --request-id 0123456789abcdef0123456789abcdef --service")]
    [InlineData("--install-helper --request-id 0123456789abcdef0123456789abcdef --request-id 0123456789abcdef0123456789abcdef")]
    [InlineData("--install-helper --request-id 0123456789abcdef0123456789abcdef & calc")]
    [InlineData("--install-helper --update-helper --request-id 0123456789abcdef0123456789abcdef")]
    [InlineData("--request-id 0123456789abcdef0123456789abcdef")]
    [InlineData("--request-id 0123456789abcdef0123456789abcdef --install-helper")]
    [InlineData("0123456789abcdef0123456789abcdef")]
    public void AnythingElseWithARequestIdIsRefusedWithoutStartingAnything(string arguments)
    {
        Assert.False(SafeShell.IsAllowedElevatedArguments(arguments));
        Assert.Throws<ArgumentException>(() => SafeShell.StartSelfElevated(arguments, 0));
    }

    [Fact]
    public void SettingsPagesAndElevatedOptionsAreSeparateAllowlists()
    {
        Assert.False(SafeShell.IsAllowed("--install-helper"));
        Assert.False(SafeShell.IsAllowed("--install-helper --request-id " + Id));
        Assert.False(SafeShell.IsAllowedElevatedArguments(SafeShell.SoundUri));
    }

    [Theory]
    [InlineData(@"C:\Tools\BlindsDown\BlindsDown.exe", @"C:\Tools\BlindsDown\", true)]
    [InlineData(@"C:\Tools\BlindsDown\BlindsDown (1).EXE", @"C:\Tools\BlindsDown", true)]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe", @"C:\Tools\BlindsDown\", false)]
    [InlineData(@"C:\Tools\BlindsDown\sub\BlindsDown.exe", @"C:\Tools\BlindsDown\", false)]
    [InlineData(@"C:\Tools\BlindsDown\BlindsDown.dll", @"C:\Tools\BlindsDown\", false)]
    [InlineData(@"BlindsDown.exe", @"C:\Tools\BlindsDown\", false)]
    [InlineData(@"C:\Tools\BlindsDown\BlindsDown.exe", @"Tools\BlindsDown\", false)]
    public void OnlyTheAppsOwnExeIsEverStartedElevated(string exe, string appDirectory, bool expected) =>
        Assert.Equal(expected, SafeShell.IsAppExecutable(exe, appDirectory));

    [Fact]
    public void TheTestRunnerIsNeverTakenForBlindsDown()
    {
        // The runner's exe sits in the same folder as BlindsDown.dll here, so "an exe in the app folder" alone would
        // let it be started elevated or installed as the helper. (StartSelfElevated starts nothing without
        // OwnExecutable; it is deliberately not called here with an allowed option.)
        Assert.False(SafeShell.IsBlindsDownTheProgram());
        Assert.Null(SafeShell.OwnExecutable());
        Assert.Throws<InvalidOperationException>(HelperFiles.CurrentPlan);
    }

    [Fact]
    public void ElevationIsReadFromTheProcessToken()
    {
        // With UAC, an administrator's unelevated token has Administrators only as a deny-only group, so "is in
        // role" and "is elevated" agree; the token's elevation flag is what BlindsDown asks.
        using WindowsIdentity me = WindowsIdentity.GetCurrent();
        bool inAdministrators = new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator);

        Assert.Equal(inAdministrators, ProcessElevation.IsElevated());
    }
}
