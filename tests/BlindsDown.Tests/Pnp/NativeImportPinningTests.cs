using System.Reflection;
using System.Runtime.InteropServices;
using BlindsDown.Interop.Media;
using BlindsDown.Interop.Pnp;

namespace BlindsDown.Tests.Pnp;

/// <summary>
/// cfgmgr32.dll is not a KnownDLL, and without a search path the runtime first looks next to the exe: every import
/// of the Plug and Play layer and the camera lookups must be pinned to System32, so a cfgmgr32.dll planted in the
/// exe's folder (say, Downloads) is never loaded.
/// </summary>
public sealed class NativeImportPinningTests
{
    [Theory]
    [InlineData(typeof(CfgMgrNative))]
    [InlineData(typeof(CameraLookupNative))]
    public void EveryImportIsLoadedFromSystem32Only(Type natives)
    {
        MethodInfo[] imports =
        [
            .. natives.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Where(m => m.GetCustomAttribute<LibraryImportAttribute>() is not null),
        ];

        Assert.NotEmpty(imports);
        Assert.All(imports, m => Assert.True(
            m.GetCustomAttribute<DefaultDllImportSearchPathsAttribute>()?.Paths == DllImportSearchPath.System32,
            $"{natives.Name}.{m.Name} is not pinned to System32."));
    }
}
