using static BlindsDown.Interop.Pnp.CfgMgrNative;

namespace BlindsDown.Interop.Pnp;

/// <summary>The real <see cref="ICfgMgr"/>: thin, checked wrappers around <see cref="CfgMgrNative"/>.</summary>
internal sealed unsafe class CfgMgrApi : ICfgMgr
{
    public uint GetDeviceInterfaces(Guid interfaceClass, out IReadOnlyList<string> interfacePaths) =>
        CfgMgrBuffers.ReadList(
            () =>
            {
                Guid guid = interfaceClass;
                uint length = 0;
                uint cr = CM_Get_Device_Interface_List_SizeW(&length, &guid, null, CmGetDeviceInterfaceListAllDevices);
                return (cr, length);
            },
            buffer =>
            {
                Guid guid = interfaceClass;
                fixed (char* list = buffer)
                {
                    return CM_Get_Device_Interface_ListW(&guid, null, list, (uint)buffer.Length, CmGetDeviceInterfaceListAllDevices);
                }
            },
            out interfacePaths);

    public string? GetInterfaceInstanceId(string interfacePath)
    {
        if (string.IsNullOrEmpty(interfacePath) || interfacePath.Contains('\0', StringComparison.Ordinal))
        {
            return null;
        }

        uint cr = CfgMgrBuffers.ReadProperty(
            buffer =>
            {
                DevPropKey key = PnpKeys.InstanceId;
                uint type = 0;
                uint size = (uint)buffer.Length;
                fixed (char* path = interfacePath)
                fixed (byte* data = buffer)
                {
                    uint result = CM_Get_Device_Interface_PropertyW(path, &key, &type, data, &size, 0);
                    return (result, type, size);
                }
            },
            out uint propertyType,
            out byte[] value);
        return cr == CrSuccess ? CfgMgrBuffers.DecodeString(propertyType, value) : null;
    }

    public uint LocateDevNode(string instanceId, bool includeNotPresent, out uint devInst)
    {
        devInst = 0;

        // CM_Locate_DevNodeW treats a null or empty ID as "the root of the device tree". An embedded null would
        // cut the ID short, possibly to nothing, so such IDs never reach Windows.
        if (string.IsNullOrWhiteSpace(instanceId)
            || instanceId.Length >= MaxDeviceIdLength
            || instanceId.Contains('\0', StringComparison.Ordinal))
        {
            return CrInvalidDeviceId;
        }

        uint found = 0;
        uint cr;
        fixed (char* id = instanceId)
        {
            cr = CM_Locate_DevNodeW(&found, id, includeNotPresent ? CmLocateDevNodePhantom : CmLocateDevNodeNormal);
        }

        devInst = cr == CrSuccess ? found : 0;
        return cr;
    }

    public uint GetStatus(uint devInst, out uint status, out uint problem)
    {
        uint statusBits = 0;
        uint problemNumber = 0;
        uint cr = CM_Get_DevNode_Status(&statusBits, &problemNumber, devInst, 0);
        status = cr == CrSuccess ? statusBits : 0;
        problem = cr == CrSuccess ? problemNumber : 0;
        return cr;
    }

    public string? GetString(uint devInst, DevPropKey key) =>
        ReadDevNodeProperty(devInst, key, out uint type, out byte[] value) == CrSuccess ? CfgMgrBuffers.DecodeString(type, value) : null;

    public Guid? GetGuid(uint devInst, DevPropKey key) =>
        ReadDevNodeProperty(devInst, key, out uint type, out byte[] value) == CrSuccess ? CfgMgrBuffers.DecodeGuid(type, value) : null;

    public bool? GetBoolean(uint devInst, DevPropKey key) =>
        ReadDevNodeProperty(devInst, key, out uint type, out byte[] value) == CrSuccess ? CfgMgrBuffers.DecodeBoolean(type, value) : null;

    public uint? GetUInt32(uint devInst, DevPropKey key) =>
        ReadDevNodeProperty(devInst, key, out uint type, out byte[] value) == CrSuccess ? CfgMgrBuffers.DecodeUInt32(type, value) : null;

    public uint DisableDevNode(uint devInst, uint flags) => CM_Disable_DevNode(devInst, flags);

    public uint EnableDevNode(uint devInst) => CM_Enable_DevNode(devInst, 0);

    private static uint ReadDevNodeProperty(uint devInst, DevPropKey key, out uint type, out byte[] value) =>
        CfgMgrBuffers.ReadProperty(
            buffer =>
            {
                DevPropKey property = key;
                uint propertyType = 0;
                uint size = (uint)buffer.Length;
                fixed (byte* data = buffer)
                {
                    uint result = CM_Get_DevNode_PropertyW(devInst, &property, &propertyType, data, &size, 0);
                    return (result, propertyType, size);
                }
            },
            out type,
            out value);
}
