using BlindsDown.Infrastructure;

namespace BlindsDown.Helper;

/// <summary>How asking the helper to unlock before removal went.</summary>
internal enum HelperUnlockOutcome
{
    /// <summary>Every device the helper turned off is on again (or nothing was locked).</summary>
    Unlocked,

    /// <summary>The helper answered, but some device could not be turned back on.</summary>
    Failed,

    /// <summary>The helper didn't answer (not running, or broken).</summary>
    Unreachable,
}

/// <summary>Everything the setup steps need from Windows. <see cref="WindowsHelperSystem"/> does it for real;
/// tests use a fake, because an install needs administrator rights and changes the system.</summary>
internal interface IHelperSystem
{
    bool IsElevated { get; }

    bool ServiceExists();

    bool IsServiceRunning();

    /// <summary>A lock journal exists in the data folder (some device may still be off).</summary>
    bool HasJournal();

    /// <summary>Copies the program into Program Files (staged; see <see cref="StagedInstall"/>).</summary>
    IInstalledFiles InstallFiles();

    /// <summary>Registers the service with its description, restart-on-failure and SID settings.</summary>
    void CreateService();

    /// <summary>Re-applies the service's command line and settings (they may come from an older version).</summary>
    void ConfigureService();

    /// <summary>Starts the service and waits until it runs.</summary>
    void StartService();

    /// <summary>Stops the service and waits until it has stopped. A lock stays in the journal. Does nothing when the
    /// service isn't registered.</summary>
    void StopService();

    /// <summary>Does nothing when the service isn't registered.</summary>
    void DeleteService();

    /// <summary>True once the helper answers on its pipe (and is really the helper).</summary>
    bool WaitUntilHelperAnswers(TimeSpan timeout);

    HelperUnlockOutcome UnlockDevices();

    void WriteUninstallEntry();

    void DeleteUninstallEntry();

    /// <summary>Deletes the install folder if setup made it, otherwise only setup's leftovers in it (see
    /// <see cref="HelperFiles.RemoveInstallFolder"/>); files in use go at the next restart. False if something that
    /// should go stays.</summary>
    bool RemoveFiles();

    /// <summary>Deletes the data folder (journal and logs). False if something stays.</summary>
    bool RemoveDataDirectory();
}

/// <summary>
/// The install, update and removal steps, in order, with rollback. Pure sequencing on top of
/// <see cref="IHelperSystem"/>: every Windows detail lives there. The rules:
///  * Install undoes everything it did when any step fails (newest first), so a failed install leaves nothing.
///  * Update keeps a lock in progress: the service is stopped (devices stay off), files replaced, started again;
///    on failure the old files go back and the old helper is started again.
///  * Uninstall unlocks first. If that fails it still removes everything (devices come back at the next restart)
///    and says so with exit code 3.
/// </summary>
internal static class HelperInstaller
{
    internal static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(20);

    public static int Install(IHelperSystem system)
    {
        if (!system.IsElevated)
        {
            return HelperSetup.ExitNotElevated;
        }

        if (system.ServiceExists())
        {
            Log.Info("The helper is already registered; updating it instead.");
            return Update(system);
        }

        // A journal without a service is left from an earlier install whose removal was cut short. Its devices may
        // still be off: the new helper starts with that lock and is asked to unlock right away.
        bool leftoverLock = system.HasJournal();
        var undo = new Stack<(string What, Action Undo)>();
        try
        {
            // Copying undoes itself when it fails part way.
            IInstalledFiles files = system.InstallFiles();
            undo.Push(("copied files", files.Undo));

            // The other undos go on the list BEFORE their step, so a step that fails half way (registered but not
            // configured, started but stuck starting) is undone too. Undoing a step that never happened does nothing.
            undo.Push(("service registration", system.DeleteService));
            system.CreateService();
            undo.Push(("running service", system.StopService));
            system.StartService();
            if (!system.WaitUntilHelperAnswers(AnswerTimeout))
            {
                throw new InvalidOperationException("The helper service started but its pipe never answered.");
            }

            if (leftoverLock)
            {
                Log.Warn("Found a lock record from an earlier helper; asking the new helper to unlock it.");
                HelperUnlockOutcome outcome = system.UnlockDevices();
                if (outcome == HelperUnlockOutcome.Unlocked)
                {
                    Log.Info("The earlier lock is undone.");
                }
                else
                {
                    Log.Warn($"The earlier lock could not be undone ({outcome}); the helper keeps it, and unlocking from BlindsDown or a restart ends it.");
                }
            }

            system.WriteUninstallEntry();
            undo.Push(("Installed apps entry", system.DeleteUninstallEntry));
            files.Commit();
            Log.Info("The camera helper is installed and running.");
            return HelperSetup.ExitOk;
        }
        catch (Exception ex)
        {
            // Any failure at all, expected or not, must leave nothing behind.
            Log.Error("Installing the helper failed; undoing", ex);
            RollBack(undo);
            return HelperSetup.ExitFailed;
        }
    }

    public static int Update(IHelperSystem system)
    {
        if (!system.IsElevated)
        {
            return HelperSetup.ExitNotElevated;
        }

        if (!system.ServiceExists())
        {
            Log.Info("The helper is not registered; installing it instead.");
            return Install(system);
        }

        var undo = new Stack<(string What, Action Undo)>();
        try
        {
            // Stopping keeps the lock: devices the helper turned off stay off, and the journal remembers them. As in
            // Install, an undo goes on the list before its step: a stop that times out half way still ends with the
            // helper started again, never with a helper that is left stopped and no longer guards the lock.
            undo.Push(("stopped service", system.StartService));
            system.StopService();
            IInstalledFiles files = system.InstallFiles();
            undo.Push(("replaced files", files.Undo));
            system.ConfigureService();
            undo.Push(("restarted service", system.StopService));
            system.StartService();
            if (!system.WaitUntilHelperAnswers(AnswerTimeout))
            {
                throw new InvalidOperationException("The updated helper started but its pipe never answered.");
            }

            files.Commit();
            TryStep("update the Installed apps entry", system.WriteUninstallEntry);
            Log.Info("The camera helper is updated and running.");
            return HelperSetup.ExitOk;
        }
        catch (Exception ex)
        {
            Log.Error("Updating the helper failed; putting the previous version back", ex);
            RollBack(undo);
            return HelperSetup.ExitFailed;
        }
    }

    public static int Uninstall(IHelperSystem system)
    {
        if (!system.IsElevated)
        {
            return HelperSetup.ExitNotElevated;
        }

        bool devicesBack = UnlockBeforeRemoval(system);

        bool registered;
        try
        {
            registered = system.ServiceExists();
        }
        catch (Exception ex)
        {
            Log.Error("Could not ask Windows about the helper service", ex);
            return HelperSetup.ExitFailed;
        }

        if (registered)
        {
            // If it won't stop in time, deleting still works: Windows removes it once it has stopped.
            TryStep("stop the helper service", system.StopService);
            if (!TryStep("remove the helper service", system.DeleteService))
            {
                // Keep the files and the Installed apps entry, so the removal can simply be tried again.
                return HelperSetup.ExitFailed;
            }
        }

        bool complete = TryStep("delete the helper's files", system.RemoveFiles);
        complete &= TryStep("delete the helper's data folder", system.RemoveDataDirectory);
        complete &= TryStep("remove the Installed apps entry", system.DeleteUninstallEntry);
        if (!complete)
        {
            return HelperSetup.ExitFailed;
        }

        Log.Info(devicesBack
            ? "The camera helper is removed."
            : "The camera helper is removed, but some devices could not be turned back on; they come back at the next restart.");
        return devicesBack ? HelperSetup.ExitOk : HelperSetup.ExitDevicesNotRestored;
    }

    /// <summary>True when no device is left off by the helper.</summary>
    private static bool UnlockBeforeRemoval(IHelperSystem system)
    {
        try
        {
            if (!system.ServiceExists())
            {
                // Without a service nothing can turn devices on now; a journal means some may still be off.
                return !system.HasJournal();
            }

            HelperUnlockOutcome outcome = system.UnlockDevices();
            if (outcome == HelperUnlockOutcome.Unreachable && !system.IsServiceRunning())
            {
                // A stopped helper can't unlock: start it (it re-applies its lock), then ask again.
                Log.Info("The helper is not running; starting it so it can turn devices back on.");
                system.StartService();
                outcome = system.WaitUntilHelperAnswers(AnswerTimeout) ? system.UnlockDevices() : HelperUnlockOutcome.Unreachable;
            }

            Log.Info($"Unlock before removal: {outcome}");
            return outcome switch
            {
                HelperUnlockOutcome.Unlocked => true,
                HelperUnlockOutcome.Unreachable => !system.HasJournal(),
                _ => false,
            };
        }
        catch (Exception ex)
        {
            Log.Error("Could not ask the helper to unlock before removal", ex);
            return !SafeHasJournal(system);
        }
    }

    private static bool SafeHasJournal(IHelperSystem system)
    {
        try
        {
            return system.HasJournal();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool TryStep(string what, Action step) => TryStep(what, () =>
    {
        step();
        return true;
    });

    private static bool TryStep(string what, Func<bool> step)
    {
        try
        {
            if (step())
            {
                return true;
            }

            Log.Warn($"Could not completely {what}.");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not {what}", ex);
            return false;
        }
    }

    private static void RollBack(Stack<(string What, Action Undo)> undo)
    {
        while (undo.Count > 0)
        {
            (string what, Action step) = undo.Pop();
            if (TryStep("undo the " + what, step))
            {
                Log.Info($"Undid the {what}.");
            }
        }
    }
}
