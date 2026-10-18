using BlindsDown.Core.Helper;
using BlindsDown.Core.Locking;
using BlindsDown.Helper;

namespace BlindsDown.Tests.Helper;

/// <summary>
/// The order of the install, update and removal steps, and what is undone when one fails, on a fake system that
/// records every step (a real install needs administrator rights and changes the computer).
/// </summary>
public sealed class HelperInstallerTests
{
    private readonly FakeHelperSystem _system = new();

    [Fact]
    public void InstallRunsEveryStepInOrder()
    {
        Assert.Equal(HelperSetup.ExitOk, HelperInstaller.Install(_system));

        Assert.Equal(["InstallFiles", "CreateService", "StartService", "WaitUntilHelperAnswers", "WriteUninstallEntry", "Commit"], _system.Steps);
        Assert.True(_system.FilesInstalled);
        Assert.True(_system.Registered);
        Assert.True(_system.Running);
        Assert.True(_system.EntryWritten);
    }

    [Fact]
    public void NothingHappensWithoutAdministratorRights()
    {
        _system.IsElevated = false;

        Assert.Equal(HelperSetup.ExitNotElevated, HelperInstaller.Install(_system));
        Assert.Equal(HelperSetup.ExitNotElevated, HelperInstaller.Update(_system));
        Assert.Equal(HelperSetup.ExitNotElevated, HelperInstaller.Uninstall(_system));
        Assert.Empty(_system.Steps);
    }

    [Theory]
    [InlineData("InstallFiles")]
    [InlineData("CreateService")]
    [InlineData("StartService")]
    [InlineData("WaitUntilHelperAnswers")]
    [InlineData("WriteUninstallEntry")]
    public void FailedInstallLeavesNothingBehind(string failingStep)
    {
        _system.FailAt = failingStep;

        Assert.Equal(HelperSetup.ExitFailed, HelperInstaller.Install(_system));

        Assert.False(_system.FilesInstalled);
        Assert.False(_system.Registered);
        Assert.False(_system.Running);
        Assert.False(_system.EntryWritten);
        Assert.DoesNotContain("Commit", _system.Steps);
    }

    [Fact]
    public void FailedInstallIsUndoneNewestFirst()
    {
        _system.FailAt = "WriteUninstallEntry";

        HelperInstaller.Install(_system);

        Assert.Equal(
            ["InstallFiles", "CreateService", "StartService", "WaitUntilHelperAnswers", "WriteUninstallEntry", "StopService", "DeleteService", "Undo"],
            _system.Steps);
    }

    [Fact]
    public void RegistrationThatFailsHalfWayIsRemovedToo()
    {
        // E.g. the service was registered, then setting its description or restart rules failed.
        _system.FailHalfWay = "CreateService";

        Assert.Equal(HelperSetup.ExitFailed, HelperInstaller.Install(_system));

        Assert.False(_system.Registered);
        Assert.False(_system.FilesInstalled);
        Assert.Equal(["InstallFiles", "CreateService", "DeleteService", "Undo"], _system.Steps);
    }

    [Fact]
    public void StartThatFailsHalfWayIsStoppedAndRemoved()
    {
        // E.g. the service was still starting when the wait ran out, and came up afterwards.
        _system.FailHalfWay = "StartService";

        Assert.Equal(HelperSetup.ExitFailed, HelperInstaller.Install(_system));

        Assert.False(_system.Running);
        Assert.False(_system.Registered);
        Assert.False(_system.FilesInstalled);
        Assert.Equal(["InstallFiles", "CreateService", "StartService", "StopService", "DeleteService", "Undo"], _system.Steps);
    }

    [Fact]
    public void LeftoverLockThatCanNotBeUndoneStillLeavesAWorkingHelper()
    {
        // The new helper holds that lock; BlindsDown's unlock (or a restart) ends it. Removing the helper again
        // would leave nothing able to turn those devices back on before a restart.
        _system.Journal = true;
        _system.UnlockOutcome = HelperUnlockOutcome.Failed;

        Assert.Equal(HelperSetup.ExitOk, HelperInstaller.Install(_system));

        Assert.True(_system.Registered);
        Assert.True(_system.Running);
    }

    [Fact]
    public void HelperThatNeverAnswersIsNotLeftInstalled()
    {
        _system.Answers = false;

        Assert.Equal(HelperSetup.ExitFailed, HelperInstaller.Install(_system));
        Assert.False(_system.Registered);
        Assert.False(_system.FilesInstalled);
    }

    [Fact]
    public void LeftoverLockFromAnEarlierHelperIsUnlockedAfterInstall()
    {
        _system.Journal = true;

        Assert.Equal(HelperSetup.ExitOk, HelperInstaller.Install(_system));

        Assert.Equal(["InstallFiles", "CreateService", "StartService", "WaitUntilHelperAnswers", "UnlockDevices", "WriteUninstallEntry", "Commit"], _system.Steps);
    }

    [Fact]
    public void InstallOverAnExistingHelperUpdatesIt()
    {
        _system.Registered = true;
        _system.Running = true;
        _system.FilesInstalled = true;

        Assert.Equal(HelperSetup.ExitOk, HelperInstaller.Install(_system));

        Assert.Equal(["StopService", "InstallFiles", "ConfigureService", "StartService", "WaitUntilHelperAnswers", "Commit", "WriteUninstallEntry"], _system.Steps);
    }

    [Fact]
    public void UpdateKeepsTheLockAndRestartsTheHelper()
    {
        _system.Registered = true;
        _system.Running = true;
        _system.Journal = true;

        Assert.Equal(HelperSetup.ExitOk, HelperInstaller.Update(_system));

        Assert.True(_system.Running);
        Assert.True(_system.Journal);
        Assert.DoesNotContain("UnlockDevices", _system.Steps);
    }

    [Fact]
    public void FailedUpdatePutsThePreviousHelperBackAndStartsIt()
    {
        _system.Registered = true;
        _system.Running = true;
        _system.Answers = false;

        Assert.Equal(HelperSetup.ExitFailed, HelperInstaller.Update(_system));

        Assert.Equal(
            ["StopService", "InstallFiles", "ConfigureService", "StartService", "WaitUntilHelperAnswers", "StopService", "Undo", "StartService"],
            _system.Steps);
        Assert.True(_system.Running);
        Assert.True(_system.Registered);
    }

    [Theory]
    [InlineData("StopService")]
    [InlineData("InstallFiles")]
    [InlineData("ConfigureService")]
    [InlineData("StartService")]
    public void UpdateThatFailsEarlyKeepsTheHelperRunning(string failingStep)
    {
        _system.Registered = true;
        _system.Running = true;
        _system.FailAt = failingStep;
        _system.FailOnce = true;

        Assert.Equal(HelperSetup.ExitFailed, HelperInstaller.Update(_system));

        Assert.True(_system.Registered);
        Assert.True(_system.Running);
    }

    [Fact]
    public void StopThatFailsHalfWayStillEndsWithTheHelperRunning()
    {
        // A stop that timed out may still complete afterwards: a helper left stopped would no longer guard the lock.
        _system.Registered = true;
        _system.Running = true;
        _system.FailHalfWay = "StopService";

        Assert.Equal(HelperSetup.ExitFailed, HelperInstaller.Update(_system));

        Assert.Equal(["StopService", "StartService"], _system.Steps);
        Assert.True(_system.Running);
    }

    [Fact]
    public void UpdatedHelperThatFailsHalfWayThroughStartingIsReplacedByThePreviousOne()
    {
        _system.Registered = true;
        _system.Running = true;
        _system.FailHalfWay = "StartService";

        Assert.Equal(HelperSetup.ExitFailed, HelperInstaller.Update(_system));

        Assert.Equal(["StopService", "InstallFiles", "ConfigureService", "StartService", "StopService", "Undo", "StartService"], _system.Steps);
        Assert.True(_system.Running);
    }

    [Fact]
    public void UpdateWithoutAnInstalledHelperInstallsIt()
    {
        Assert.Equal(HelperSetup.ExitOk, HelperInstaller.Update(_system));

        Assert.Contains("CreateService", _system.Steps);
        Assert.True(_system.Registered);
    }

    [Fact]
    public void FailedEntryUpdateDoesNotUndoAWorkingUpdate()
    {
        _system.Registered = true;
        _system.Running = true;
        _system.FailAt = "WriteUninstallEntry";

        Assert.Equal(HelperSetup.ExitOk, HelperInstaller.Update(_system));
        Assert.DoesNotContain("Undo", _system.Steps);
    }

    [Fact]
    public void UninstallUnlocksFirstThenRemovesEverything()
    {
        Installed();

        Assert.Equal(HelperSetup.ExitOk, HelperInstaller.Uninstall(_system));

        Assert.Equal(["UnlockDevices", "StopService", "DeleteService", "RemoveFiles", "RemoveDataDirectory", "DeleteUninstallEntry"], _system.Steps);
        Assert.False(_system.Registered);
        Assert.False(_system.FilesInstalled);
        Assert.False(_system.EntryWritten);
        Assert.False(_system.Journal);
    }

    [Fact]
    public void UninstallStillRemovesEverythingWhenDevicesCouldNotBeTurnedBackOn()
    {
        Installed();
        _system.UnlockOutcome = HelperUnlockOutcome.Failed;

        Assert.Equal(HelperSetup.ExitDevicesNotRestored, HelperInstaller.Uninstall(_system));

        Assert.False(_system.Registered);
        Assert.False(_system.FilesInstalled);
        Assert.False(_system.EntryWritten);
    }

    [Fact]
    public void StoppedHelperIsStartedSoItCanUnlock()
    {
        Installed();
        _system.Running = false;
        _system.Journal = true;

        Assert.Equal(HelperSetup.ExitOk, HelperInstaller.Uninstall(_system));

        Assert.Equal(
            ["UnlockDevices", "StartService", "WaitUntilHelperAnswers", "UnlockDevices", "StopService", "DeleteService", "RemoveFiles", "RemoveDataDirectory", "DeleteUninstallEntry"],
            _system.Steps);
    }

    [Theory]
    [InlineData(false, HelperSetup.ExitOk)]
    [InlineData(true, HelperSetup.ExitDevicesNotRestored)]
    public void UnreachableHelperMattersOnlyWhenALockIsRecorded(bool journal, int expected)
    {
        Installed();
        _system.Answers = false;
        _system.Journal = journal;

        Assert.Equal(expected, HelperInstaller.Uninstall(_system));
        Assert.False(_system.Registered);
    }

    [Fact]
    public void ServiceThatCanNotBeRemovedKeepsTheFilesAndTheEntryForAnotherTry()
    {
        Installed();
        _system.FailAt = "DeleteService";

        Assert.Equal(HelperSetup.ExitFailed, HelperInstaller.Uninstall(_system));

        Assert.True(_system.FilesInstalled);
        Assert.True(_system.EntryWritten);
    }

    [Fact]
    public void ServiceThatWontStopIsStillRemoved()
    {
        Installed();
        _system.FailAt = "StopService";

        Assert.Equal(HelperSetup.ExitOk, HelperInstaller.Uninstall(_system));
        Assert.False(_system.Registered);
        Assert.False(_system.EntryWritten);
    }

    [Fact]
    public void LeftoverFilesAreReportedAsFailure()
    {
        Installed();
        _system.FilesRemovable = false;

        Assert.Equal(HelperSetup.ExitFailed, HelperInstaller.Uninstall(_system));

        // Everything else still went.
        Assert.False(_system.Registered);
        Assert.False(_system.EntryWritten);
    }

    [Fact]
    public void UninstallWithoutAServiceCleansUpWhatIsLeft()
    {
        _system.FilesInstalled = true;
        _system.EntryWritten = true;

        Assert.Equal(HelperSetup.ExitOk, HelperInstaller.Uninstall(_system));

        Assert.Equal(["RemoveFiles", "RemoveDataDirectory", "DeleteUninstallEntry"], _system.Steps);
    }

    [Fact]
    public void UninstallWithoutAServiceButWithALockSaysDevicesMayStillBeOff()
    {
        _system.Journal = true;

        Assert.Equal(HelperSetup.ExitDevicesNotRestored, HelperInstaller.Uninstall(_system));
    }

    [Fact]
    public void HelperThatSaysNoMeansDevicesMayStillBeOff() =>
        Assert.Equal(HelperUnlockOutcome.Failed, WindowsHelperSystem.UnlockOutcome(new HelperResponse { Ok = false, Error = "test" }));

    [Fact]
    public void NothingLockedCountsAsUnlocked() =>
        Assert.Equal(HelperUnlockOutcome.Unlocked, WindowsHelperSystem.UnlockOutcome(new HelperResponse { Ok = true }));

    [Theory]
    [InlineData(RestoreOutcome.Exact, true)]
    [InlineData(RestoreOutcome.ChangedByOthers, true)]
    [InlineData(RestoreOutcome.Failed, false)]
    [InlineData(RestoreOutcome.Pending, false)] // unplugged: no helper is left to turn it back on
    public void EveryDeviceMustBeBackForARemovalToSayAllIsWell(RestoreOutcome camera, bool allBack)
    {
        var report = new RestoreReport(
            [
                new RestoreLine(RestoreOutcome.Exact, "Front camera", string.Empty, DeviceKind.Camera),
                new RestoreLine(camera, "USB camera", string.Empty, DeviceKind.Camera),
            ],
            DeviceCount: 2,
            CoversCameras: true);

        Assert.Equal(
            allBack ? HelperUnlockOutcome.Unlocked : HelperUnlockOutcome.Failed,
            WindowsHelperSystem.UnlockOutcome(new HelperResponse { Ok = true, Report = report }));
    }

    private void Installed()
    {
        _system.Registered = true;
        _system.Running = true;
        _system.FilesInstalled = true;
        _system.EntryWritten = true;
    }

    /// <summary>A pretend computer: one service, its files, its Installed apps entry and its lock journal.</summary>
    private sealed class FakeHelperSystem : IHelperSystem
    {
        public List<string> Steps { get; } = [];

        public bool IsElevated { get; set; } = true;

        public bool Registered { get; set; }

        public bool Running { get; set; }

        public bool FilesInstalled { get; set; }

        public bool EntryWritten { get; set; }

        public bool Journal { get; set; }

        public bool Answers { get; set; } = true;

        public bool FilesRemovable { get; set; } = true;

        public HelperUnlockOutcome UnlockOutcome { get; set; } = HelperUnlockOutcome.Unlocked;

        /// <summary>The step that throws.</summary>
        public string? FailAt { get; set; }

        /// <summary>Throw only the first time (so an undo that repeats the step works).</summary>
        public bool FailOnce { get; set; }

        /// <summary>The step that does its work and then throws, once: a step that fails half way.</summary>
        public string? FailHalfWay { get; set; }

        public bool ServiceExists() => Registered;

        public bool IsServiceRunning() => Running;

        public bool HasJournal() => Journal;

        public IInstalledFiles InstallFiles()
        {
            Step("InstallFiles");
            bool hadFiles = FilesInstalled;
            FilesInstalled = true;
            return new FakeFiles(this, hadFiles);
        }

        public void CreateService()
        {
            Step("CreateService");
            Registered = true;
            AfterStep("CreateService");
        }

        public void ConfigureService() => Step("ConfigureService");

        public void StartService()
        {
            Step("StartService");
            Running = Registered;
            AfterStep("StartService");
        }

        public void StopService()
        {
            Step("StopService");
            Running = false;
            AfterStep("StopService");
        }

        public void DeleteService()
        {
            Step("DeleteService");
            Registered = false;
            Running = false;
        }

        public bool WaitUntilHelperAnswers(TimeSpan timeout)
        {
            Step("WaitUntilHelperAnswers");
            return Answers && Running;
        }

        public HelperUnlockOutcome UnlockDevices()
        {
            Step("UnlockDevices");
            if (!Running || !Answers)
            {
                return HelperUnlockOutcome.Unreachable;
            }

            if (UnlockOutcome == HelperUnlockOutcome.Unlocked)
            {
                Journal = false;
            }

            return UnlockOutcome;
        }

        public void WriteUninstallEntry()
        {
            Step("WriteUninstallEntry");
            EntryWritten = true;
        }

        public void DeleteUninstallEntry()
        {
            Step("DeleteUninstallEntry");
            EntryWritten = false;
        }

        public bool RemoveFiles()
        {
            Step("RemoveFiles");
            FilesInstalled = !FilesRemovable;
            return FilesRemovable;
        }

        public bool RemoveDataDirectory()
        {
            Step("RemoveDataDirectory");
            Journal = false;
            return true;
        }

        public void Step(string name)
        {
            Steps.Add(name);
            if (name == FailAt)
            {
                if (FailOnce)
                {
                    FailAt = null;
                }

                throw new InvalidOperationException($"{name} failed (test)");
            }
        }

        private void AfterStep(string name)
        {
            if (name == FailHalfWay)
            {
                FailHalfWay = null;
                throw new InvalidOperationException($"{name} failed half way (test)");
            }
        }

        private sealed class FakeFiles(FakeHelperSystem system, bool hadFiles) : IInstalledFiles
        {
            public void Commit() => system.Step("Commit");

            public void Undo()
            {
                system.Step("Undo");
                system.FilesInstalled = hadFiles;
            }
        }
    }
}
