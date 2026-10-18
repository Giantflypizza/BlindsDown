using BlindsDown.Core.Devices;
using BlindsDown.Devices.Pnp;
using BlindsDown.Interop.Pnp;
using static BlindsDown.Interop.Pnp.CfgMgrNative;

namespace BlindsDown.Tests.Pnp;

/// <summary>A scripted device tree for the Plug and Play logic. Nothing here reaches Windows.</summary>
internal sealed class FakeCfgMgr : ICfgMgr
{
    private const uint DnDriverLoaded = 0x2;

    private readonly List<FakeNode> _nodes = [];
    private readonly Dictionary<Guid, List<(string Path, string? InstanceId)>> _interfaces = [];

    /// <summary>Interface classes whose listing fails with this CONFIGRET.</summary>
    public Dictionary<Guid, uint> InterfaceListErrors { get; } = [];

    public List<(string InstanceId, uint Flags)> DisableCalls { get; } = [];

    public List<string> EnableCalls { get; } = [];

    public FakeNode Add(string instanceId)
    {
        var node = new FakeNode(instanceId, (uint)_nodes.Count + 1);
        _nodes.Add(node);
        return node;
    }

    /// <summary>Registers an interface the way Windows names it: "\\?\" + ID with '#' for '\' + "#{class}\reference".
    /// <paramref name="reportedId"/> is what the interface's InstanceId property says (Windows lower-cases it).</summary>
    public void AddInterface(Guid interfaceClass, string instanceId, string reference = "GLOBAL", string? reportedId = null)
    {
        string path = @"\\?\" + instanceId.Replace('\\', '#') + "#" + interfaceClass.ToString("B") + @"\" + reference;
        if (!_interfaces.TryGetValue(interfaceClass, out List<(string Path, string? InstanceId)>? list))
        {
            list = [];
            _interfaces[interfaceClass] = list;
        }

        list.Add((path, reportedId ?? instanceId));
    }

    /// <summary>An interface whose device can't be read.</summary>
    public void AddOrphanInterface(Guid interfaceClass, string path)
    {
        if (!_interfaces.TryGetValue(interfaceClass, out List<(string Path, string? InstanceId)>? list))
        {
            list = [];
            _interfaces[interfaceClass] = list;
        }

        list.Add((path, null));
    }

    public uint GetDeviceInterfaces(Guid interfaceClass, out IReadOnlyList<string> interfacePaths)
    {
        if (InterfaceListErrors.TryGetValue(interfaceClass, out uint error))
        {
            interfacePaths = [];
            return error;
        }

        interfacePaths = _interfaces.TryGetValue(interfaceClass, out List<(string Path, string? InstanceId)>? list) ? list.Select(i => i.Path).ToList() : [];
        return CrSuccess;
    }

    public string? GetInterfaceInstanceId(string interfacePath) =>
        _interfaces.Values.SelectMany(list => list).FirstOrDefault(i => i.Path == interfacePath).InstanceId;

    public uint LocateDevNode(string instanceId, bool includeNotPresent, out uint devInst)
    {
        FakeNode? node = _nodes.Find(n => string.Equals(n.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase));
        devInst = 0;
        if (node is null || (!node.Present && !includeNotPresent))
        {
            return CrNoSuchDevNode;
        }

        devInst = node.DevInst;
        return CrSuccess;
    }

    public uint GetStatus(uint devInst, out uint status, out uint problem)
    {
        FakeNode node = Node(devInst);
        status = 0;
        problem = 0;
        if (node.StatusError is { } error)
        {
            return error;
        }

        if (!node.Present)
        {
            return CrNoSuchDevNode;
        }

        status = node.Status;
        problem = node.Problem;
        return CrSuccess;
    }

    public string? GetString(uint devInst, DevPropKey key)
    {
        FakeNode node = Node(devInst);
        return Same(key, PnpKeys.FriendlyName) ? node.FriendlyName
            : Same(key, PnpKeys.BusReportedDeviceDesc) ? node.BusReportedName
            : Same(key, PnpKeys.DeviceDesc) ? node.Description
            : Same(key, PnpKeys.Parent) ? node.Parent
            : Same(key, PnpKeys.InstanceId) ? node.InstanceId
            : null;
    }

    public Guid? GetGuid(uint devInst, DevPropKey key) => Same(key, PnpKeys.ClassGuid) ? Node(devInst).SetupClass : null;

    public bool? GetBoolean(uint devInst, DevPropKey key) => Same(key, PnpKeys.IsPresent) ? Node(devInst).Present : null;

    public uint? GetUInt32(uint devInst, DevPropKey key) => Same(key, PnpKeys.ConfigFlags) ? Node(devInst).ConfigFlags : null;

    public uint DisableDevNode(uint devInst, uint flags)
    {
        FakeNode node = Node(devInst);
        DisableCalls.Add((node.InstanceId, flags));
        if (node.OnDisable is { } onDisable)
        {
            onDisable(node);
        }
        else if (node.DisableResult == CrSuccess)
        {
            node.MarkDisabled();
        }

        return node.DisableResult;
    }

    public uint EnableDevNode(uint devInst)
    {
        FakeNode node = Node(devInst);
        EnableCalls.Add(node.InstanceId);
        if (node.OnEnable is { } onEnable)
        {
            onEnable(node);
        }
        else if (node.EnableResult == CrSuccess)
        {
            node.MarkEnabled();
        }

        return node.EnableResult;
    }

    private static bool Same(DevPropKey a, DevPropKey b) => a.FormatId == b.FormatId && a.PropertyId == b.PropertyId;

    private FakeNode Node(uint devInst) => _nodes[(int)devInst - 1];

    /// <summary>One device. Starts connected and running.</summary>
    internal sealed class FakeNode(string instanceId, uint devInst)
    {
        public string InstanceId { get; } = instanceId;

        public uint DevInst { get; } = devInst;

        public bool Present { get; set; } = true;

        public uint Status { get; set; } = DnStarted | DnDriverLoaded;

        public uint Problem { get; set; }

        /// <summary>When set, reading the status fails with this CONFIGRET.</summary>
        public uint? StatusError { get; set; }

        public string? FriendlyName { get; set; }

        public string? BusReportedName { get; set; }

        public string? Description { get; set; }

        public string? Parent { get; set; }

        public Guid? SetupClass { get; set; }

        public uint ConfigFlags { get; set; }

        /// <summary>What CM_Disable_DevNode returns. Without <see cref="OnDisable"/>, CR_SUCCESS disables the device.</summary>
        public uint DisableResult { get; set; } = CrSuccess;

        public Action<FakeNode>? OnDisable { get; set; }

        public uint EnableResult { get; set; } = CrSuccess;

        public Action<FakeNode>? OnEnable { get; set; }

        public FakeNode MarkDisabled()
        {
            Status = DnHasProblem | DnDriverLoaded;
            Problem = CmProbDisabled;
            return this;
        }

        public FakeNode MarkEnabled()
        {
            Status = DnStarted | DnDriverLoaded;
            Problem = 0;
            return this;
        }
    }
}

/// <summary>Scripted audio endpoints (what the registry would say).</summary>
internal sealed class FakeEndpoints : IAudioEndpointHardwareSource
{
    public List<AudioEndpointHardware> Endpoints { get; } = [];

    public bool Fail { get; set; }

    public FakeEndpoints Capture(string hardwareId)
    {
        Endpoints.Add(new AudioEndpointHardware(IsCapture: true, hardwareId));
        return this;
    }

    public FakeEndpoints Render(string hardwareId)
    {
        Endpoints.Add(new AudioEndpointHardware(IsCapture: false, hardwareId));
        return this;
    }

    public IReadOnlyList<AudioEndpointHardware> Read() =>
        Fail ? throw new DeviceOperationException("Simulated registry failure") : Endpoints;
}
