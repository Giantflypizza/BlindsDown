using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using BlindsDown.Infrastructure;
using Microsoft.Win32.SafeHandles;

namespace BlindsDown.Helper;

/// <summary>Who may make the setup's mutex, and the security it is made with.</summary>
/// <param name="Sddl">The new mutex's security descriptor.</param>
/// <param name="IsTrustedOwner">Whether a mutex that already exists, with this owner, comes from a setup run.</param>
internal sealed record SetupLockSecurity(string Sddl, Func<SecurityIdentifier, bool> IsTrustedOwner)
{
    /// <summary>MUTEX_ALL_ACCESS.</summary>
    private const string MutexAllAccess = "0x1f0001";

    /// <summary>Setup runs are elevated: owned by Administrators, and only SYSTEM and Administrators may use it. A
    /// mutex that already exists counts only when SYSTEM or Administrators own it, which a normal program can't
    /// arrange (Windows lets it name only its own account as owner).</summary>
    public static SetupLockSecurity AdministratorsOnly { get; } = new(
        $"O:BAD:P(A;;{MutexAllAccess};;;SY)(A;;{MutexAllAccess};;;BA)",
        owner => owner.IsWellKnown(WellKnownSidType.LocalSystemSid) || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));

    /// <summary>Tests only (they run unelevated): owned by and open to <paramref name="user"/>, which is trusted too.</summary>
    internal static SetupLockSecurity ForTests(SecurityIdentifier user) => new(
        $"O:{user.Value}D:P(A;;{MutexAllAccess};;;{user.Value})",
        owner => owner == user || AdministratorsOnly.IsTrustedOwner(owner));
}

/// <summary>
/// One install, update or removal of the helper at a time on this PC, from any Windows session. Two runs at once
/// would undo each other's work: a failed install's rollback would stop and delete the service the other one just
/// registered. A named mutex, held for the whole run; released (or abandoned, if the run crashes) when it ends.
/// </summary>
/// <remarks>
/// Any program can make a mutex with this name first and hold it, which would make every setup run wait and fail. So
/// the mutex is made with security that lets only SYSTEM and Administrators use it, and one that already exists is
/// waited for only when SYSTEM or Administrators own it (<see cref="SetupLockSecurity.AdministratorsOnly"/>). Any
/// other mutex of that name is some other program's: setup goes ahead without a turn rather than let it block setup.
/// </remarks>
internal sealed class HelperSetupTurn : IDisposable
{
    /// <summary>"Global\": the same object in every session, so an administrator's run in another session counts too.</summary>
    internal const string MutexName = @"Global\BlindsDown-6c2a3f9e-HelperSetup";

    private Mutex? _mutex;

    private HelperSetupTurn(Mutex? mutex) => _mutex = mutex;

    /// <summary>Waits up to <paramref name="wait"/> for an earlier run to end. Null when it is still running.</summary>
    public static HelperSetupTurn? TryTake(string name, TimeSpan wait) => TryTake(name, wait, SetupLockSecurity.AdministratorsOnly);

    /// <summary>As <see cref="TryTake(string, TimeSpan)"/>, with the given security (tests).</summary>
    internal static HelperSetupTurn? TryTake(string name, TimeSpan wait, SetupLockSecurity security)
    {
        Mutex mutex;
        try
        {
            // This handle keeps the object alive while it is checked and opened again as a .NET mutex, so the object
            // checked is the object used.
            using SafeWaitHandle handle = CreateOrOpen(name, security.Sddl, out bool existed);
            if (existed)
            {
                SecurityIdentifier? owner = OwnerOf(handle);
                if (owner is null || !security.IsTrustedOwner(owner))
                {
                    // Some other program made it (and may hold it forever). Only setup runs, which are elevated, ever
                    // take a turn, so going ahead without one is safer than letting that program block setup.
                    Log.Warn($"The setup lock was made by {(owner is null ? "an unknown account" : "another program")}, not by a setup run; going ahead without it.");
                    return new HelperSetupTurn(null);
                }
            }

            mutex = Mutex.OpenExisting(name);
        }
        catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            // Some other program made an object with this name that setup may not use.
            Log.Warn($"Could not use the setup lock ({ex.Message}); going ahead without it.");
            return new HelperSetupTurn(null);
        }

        try
        {
            if (!mutex.WaitOne(wait))
            {
                mutex.Dispose();
                return null;
            }
        }
        catch (AbandonedMutexException)
        {
            // An earlier run ended without letting go (it crashed or was killed). The turn is this run's now.
            Log.Warn("An earlier setup run ended unexpectedly.");
        }

        return new HelperSetupTurn(mutex);
    }

    /// <summary>
    /// Makes the mutex with <paramref name="sddl"/>'s security, or opens the one that already exists
    /// (<paramref name="existed"/>; its security is then left as it is). The handle may wait on it, release it and
    /// read its owner. Throws <see cref="Win32Exception"/> when that isn't allowed, or the security is invalid (an
    /// owner this account may not name, say).
    /// </summary>
    internal static unsafe SafeWaitHandle CreateOrOpen(string name, string sddl, out bool existed)
    {
        using NativeSecurityDescriptor security = NativeSecurityDescriptor.FromSddl(sddl);
        bool addedRef = false;
        nint raw;
        int error;
        try
        {
            security.DangerousAddRef(ref addedRef);
            var attributes = new HelperNative.SecurityAttributes
            {
                Length = (uint)sizeof(HelperNative.SecurityAttributes),
                SecurityDescriptor = (void*)security.DangerousGetHandle(),
                InheritHandle = 0,
            };
            fixed (char* text = name)
            {
                raw = HelperNative.CreateMutexExW(
                    &attributes, text, 0, HelperNative.Synchronize | HelperNative.MutexModifyState | HelperNative.ReadControl);
                error = Marshal.GetLastPInvokeError();
            }
        }
        finally
        {
            if (addedRef)
            {
                security.DangerousRelease();
            }
        }

        if (raw == 0)
        {
            throw new Win32Exception(error);
        }

        existed = error == HelperNative.ErrorAlreadyExists;
        return new SafeWaitHandle(raw, ownsHandle: true);
    }

    public void Dispose()
    {
        Mutex? mutex = _mutex;
        _mutex = null;
        if (mutex is not null)
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }

    /// <summary>The mutex's owner, or null when it can't be read.</summary>
    private static unsafe SecurityIdentifier? OwnerOf(SafeWaitHandle handle)
    {
        bool addedRef = false;
        try
        {
            handle.DangerousAddRef(ref addedRef);
            void* owner = null;
            void* descriptor = null;
            if (HelperNative.GetSecurityInfo(handle.DangerousGetHandle(), HelperNative.SeKernelObject, HelperNative.OwnerSecurityInformation, &owner, null, null, null, &descriptor) != 0)
            {
                return null;
            }

            try
            {
                return owner is null ? null : new SecurityIdentifier((nint)owner);
            }
            finally
            {
                _ = HelperNative.LocalFree((nint)descriptor);
            }
        }
        finally
        {
            if (addedRef)
            {
                handle.DangerousRelease();
            }
        }
    }
}
