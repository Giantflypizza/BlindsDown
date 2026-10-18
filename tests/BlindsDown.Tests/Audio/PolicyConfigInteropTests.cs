using System.Reflection;
using System.Runtime.InteropServices;
using BlindsDown.Interop.CoreAudio;

namespace BlindsDown.Tests.Audio;

/// <summary>
/// The IPolicyConfig declaration. Classic COM interop maps methods to vtable slots by declaration order, so a missing
/// or misplaced slot would make "turn this microphone off" call some other method. Pure reflection: nothing is created.
/// </summary>
public class PolicyConfigInteropTests
{
    /// <summary>The vtable after IUnknown, as EarTrumpet, SoundSwitch and AudioDeviceCmdlets declare it.</summary>
    private static readonly string[] WindowsOrder =
    [
        "GetMixFormat",
        "GetDeviceFormat",
        "ResetDeviceFormat",
        "SetDeviceFormat",
        "GetProcessingPeriod",
        "SetProcessingPeriod",
        "GetShareMode",
        "SetShareMode",
        "GetPropertyValue",
        "SetPropertyValue",
        "SetDefaultEndpoint",
        "SetEndpointVisibility",
    ];

    [Fact]
    public void IdentifiersAreTheWindowsOnes()
    {
        Assert.Equal(new Guid("F8679F50-850A-41CF-9C72-430F290290C8"), typeof(IPolicyConfig).GUID);
        Assert.Equal(new Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9"), typeof(PolicyConfigClientCoClass).GUID);
        Assert.True(typeof(IPolicyConfig).IsImport);
        Assert.True(typeof(PolicyConfigClientCoClass).IsImport);
        Assert.Equal(ComInterfaceType.InterfaceIsIUnknown, typeof(IPolicyConfig).GetCustomAttribute<InterfaceTypeAttribute>()?.Value);
    }

    [Fact]
    public void EverySlotIsDeclaredInWindowsOrder()
    {
        MethodInfo[] declared = Declared(typeof(IPolicyConfig));

        Assert.Equal(WindowsOrder, declared.Select(m => m.Name));
        Assert.Equal(3, Marshal.GetStartComSlot(typeof(IPolicyConfig)));
        Assert.Equal(2 + WindowsOrder.Length, Marshal.GetEndComSlot(typeof(IPolicyConfig)));
        Assert.All(declared, m =>
        {
            Assert.Equal(typeof(int), m.ReturnType);
            Assert.True((m.MethodImplementationFlags & MethodImplAttributes.PreserveSig) != 0, $"{m.Name} must return its HRESULT");
        });
    }

    [Theory]
    [InlineData("SetDefaultEndpoint", 13)]
    [InlineData("SetEndpointVisibility", 14)]
    public void TheTwoMethodsBlindsDownCallsSitInTheirSlots(string method, int slot)
    {
        int index = Array.FindIndex(Declared(typeof(IPolicyConfig)), m => m.Name == method);

        Assert.Equal(slot, Marshal.GetStartComSlot(typeof(IPolicyConfig)) + index);
    }

    [Fact]
    public void CalledMethodsPassAWideStringAndAnInt()
    {
        ParameterInfo[] visibility = typeof(IPolicyConfig).GetMethod(nameof(IPolicyConfig.SetEndpointVisibility))!.GetParameters();
        ParameterInfo[] standard = typeof(IPolicyConfig).GetMethod(nameof(IPolicyConfig.SetDefaultEndpoint))!.GetParameters();

        Assert.Equal([typeof(string), typeof(int)], visibility.Select(p => p.ParameterType));
        Assert.Equal(UnmanagedType.LPWStr, visibility[0].GetCustomAttribute<MarshalAsAttribute>()?.Value);
        Assert.Equal([typeof(string), typeof(DeviceRole)], standard.Select(p => p.ParameterType));
        Assert.Equal(UnmanagedType.LPWStr, standard[0].GetCustomAttribute<MarshalAsAttribute>()?.Value);
        Assert.Equal(typeof(int), Enum.GetUnderlyingType(typeof(DeviceRole)));
    }

    [Fact]
    public void EveryOtherSlotCannotBeCalled()
    {
        foreach (MethodInfo method in Declared(typeof(IPolicyConfig)))
        {
            ObsoleteAttribute? obsolete = method.GetCustomAttribute<ObsoleteAttribute>();
            bool called = method.Name is "SetDefaultEndpoint" or "SetEndpointVisibility";
            Assert.True(called ? obsolete is null : obsolete?.IsError == true, $"{method.Name}: obsolete-as-error must be {!called}");
        }
    }

    private static MethodInfo[] Declared(Type comInterface) => [.. comInterface.GetMethods().OrderBy(m => m.MetadataToken)];
}
