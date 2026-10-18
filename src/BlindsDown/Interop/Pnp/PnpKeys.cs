using static BlindsDown.Interop.Pnp.CfgMgrNative;

namespace BlindsDown.Interop.Pnp;

/// <summary>The device interface classes, setup classes and device properties the Plug and Play layer reads.</summary>
internal static class PnpKeys
{
    /// <summary>KSCATEGORY_VIDEO_CAMERA: what apps see as a camera (colour cameras, and virtual ones).</summary>
    public static readonly Guid VideoCameraInterface = new("E5323777-F976-4F5B-9B55-B94699C46E44");

    /// <summary>KSCATEGORY_SENSOR_CAMERA: infrared (Windows Hello) and depth sensors, hidden from normal camera apps.</summary>
    public static readonly Guid SensorCameraInterface = new("24E552D7-6523-47F7-A647-D3465BF1F5CA");

    /// <summary>KSCATEGORY_VIDEO: every video filter. Older camera drivers register only here (plus capture).</summary>
    public static readonly Guid VideoInterface = new("6994AD05-93EF-11D0-A3CC-00A0C9223196");

    /// <summary>KSCATEGORY_CAPTURE: filters that capture (audio or video).</summary>
    public static readonly Guid CaptureInterface = new("65E8773D-8F56-11D0-A3B9-00A0C9223196");

    /// <summary>DEVINTERFACE_AUDIO_CAPTURE: the interface every microphone endpoint (SWD\MMDEVAPI\{0.0.1.*}) exposes.</summary>
    public static readonly Guid AudioCaptureInterface = new("2EEF81BE-33FA-4800-9670-1CD474972C3F");

    /// <summary>DEVINTERFACE_AUDIO_RENDER: the interface every speaker/headphone endpoint exposes.</summary>
    public static readonly Guid AudioRenderInterface = new("E6327CAD-DCEC-4949-AE8A-991E976A79D2");

    /// <summary>Setup class "Camera" (Windows 10 1607+ webcams).</summary>
    public static readonly Guid CameraClass = new("CA3E7AB9-B4C3-4AE6-8251-579EF933890F");

    /// <summary>Setup class "Image": older webcams, but also scanners (which have no video capture interface).</summary>
    public static readonly Guid ImageClass = new("6BDD1FC6-810F-11D0-BEC7-08002BE2092F");

    private static readonly Guid DeviceProperties = new("A45C254E-DF1C-4EFD-8020-67D146A850E0");
    private static readonly Guid BusProperties = new("540B947E-8B40-45BC-A8A2-6A0B894CBDA2");

    /// <summary>DEVPKEY_Device_DeviceDesc: the driver's generic name, e.g. "USB Video Device".</summary>
    public static readonly DevPropKey DeviceDesc = new(DeviceProperties, 2);

    /// <summary>DEVPKEY_Device_ClassGuid.</summary>
    public static readonly DevPropKey ClassGuid = new(DeviceProperties, 10);

    /// <summary>DEVPKEY_Device_ConfigFlags (CONFIGFLAG_*).</summary>
    public static readonly DevPropKey ConfigFlags = new(DeviceProperties, 12);

    /// <summary>DEVPKEY_Device_FriendlyName: what Device Manager shows, e.g. "USB Webcam". Often missing.</summary>
    public static readonly DevPropKey FriendlyName = new(DeviceProperties, 14);

    /// <summary>DEVPKEY_Device_BusReportedDeviceDesc: the product name the hardware itself reports.</summary>
    public static readonly DevPropKey BusReportedDeviceDesc = new(BusProperties, 4);

    /// <summary>DEVPKEY_Device_IsPresent: connected right now.</summary>
    public static readonly DevPropKey IsPresent = new(BusProperties, 5);

    /// <summary>DEVPKEY_Device_Parent: the parent's instance ID (for a USB function, the composite USB device).</summary>
    public static readonly DevPropKey Parent = new(new Guid("4340A6C5-93FA-4706-972C-7B648008A5A7"), 8);

    /// <summary>DEVPKEY_Device_InstanceId. Read from an interface: the device that exposes it (in the case of the
    /// interface's symbolic link, often lower case). Read from a devnode: its own canonical spelling.</summary>
    public static readonly DevPropKey InstanceId = new(new Guid("78C34FC8-104A-4ACA-9EA4-524D52996E57"), 256);
}
