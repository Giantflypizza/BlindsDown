using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace BlindsDown.Helper;

/// <summary>
/// Who may use the helper's pipe, and how the tray app recognises the real helper.
/// The pipe's access list: deny NETWORK logons everything (on top of PIPE_REJECT_REMOTE_CLIENTS); SYSTEM and
/// Administrators full control; INTERACTIVE users (whoever is signed in at the PC) read and write only. "Read and
/// write" deliberately leaves out FILE_CREATE_PIPE_INSTANCE: with it, a normal program could add its own instance
/// to the helper's pipe name and answer the tray app's requests in the helper's place.
/// </summary>
internal static class HelperPipeAcl
{
    /// <summary>FILE_GENERIC_READ | FILE_GENERIC_WRITE (0x0012019F) without FILE_CREATE_PIPE_INSTANCE (0x4, which is
    /// FILE_APPEND_DATA's meaning on a pipe).</summary>
    internal const uint ClientAccessMask = 0x0012019B;

    internal const uint FileCreatePipeInstance = 0x00000004;

    /// <summary>The production pipe's access list (see the class comment).</summary>
    internal const string ServiceDacl = "D:P(D;;GA;;;NU)(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x0012019b;;;IU)";

    /// <summary>The production pipe's security descriptor: <see cref="ServiceDacl"/>, owned by SYSTEM. The owner is
    /// set, not left to the service's default owner (Administrators for SYSTEM on current Windows, but a default, not
    /// a promise), because the tray app recognises the real helper by it. A LocalSystem process can always name
    /// SYSTEM as owner; a normal program can't (Windows refuses with ERROR_INVALID_OWNER).</summary>
    internal const string ServiceSddl = "O:SY" + ServiceDacl;

    internal const string LocalSystemSid = "S-1-5-18";

    internal const string AdministratorsSid = "S-1-5-32-544";

    /// <summary>For tests, which run unelevated: only the current user (owner by default), network still denied.</summary>
    internal static string TestSddl(SecurityIdentifier user) => $"D:P(D;;GA;;;NU)(A;;GA;;;{user.Value})";

    /// <summary>
    /// Null when a pipe server looks like the real helper; otherwise the reason it doesn't. The real helper's pipe
    /// is owned by SYSTEM or Administrators (a normal program can't create objects with either owner) and its server
    /// process runs in session 0, where only services run (a program started by a signed-in user never does).
    /// </summary>
    internal static string? ServerProblem(string? ownerSid, uint sessionId)
    {
        if (ownerSid is not (LocalSystemSid or AdministratorsSid))
        {
            return $"the pipe is owned by {ownerSid ?? "an unknown account"}, not by SYSTEM or Administrators";
        }

        if (sessionId != 0)
        {
            return $"the program behind the pipe runs in session {sessionId}, not in the services' session 0";
        }

        return null;
    }
}

/// <summary>A security descriptor built from SDDL, in the LocalAlloc memory Windows functions expect.</summary>
internal sealed unsafe class NativeSecurityDescriptor : SafeHandleZeroOrMinusOneIsInvalid
{
    private NativeSecurityDescriptor()
        : base(ownsHandle: true)
    {
    }

    /// <summary>Throws <see cref="Win32Exception"/> for invalid SDDL.</summary>
    public static NativeSecurityDescriptor FromSddl(string sddl)
    {
        var descriptor = new NativeSecurityDescriptor();
        void* memory = null;
        fixed (char* text = sddl)
        {
            if (HelperNative.ConvertStringSecurityDescriptorToSecurityDescriptorW(text, HelperNative.SddlRevision1, &memory, null) == 0)
            {
                int error = Marshal.GetLastPInvokeError();
                descriptor.Dispose();
                throw new Win32Exception(error, "Windows rejected the pipe's security descriptor.");
            }
        }

        descriptor.SetHandle((nint)memory);
        return descriptor;
    }

    protected override bool ReleaseHandle() => HelperNative.LocalFree(handle) == 0;
}
