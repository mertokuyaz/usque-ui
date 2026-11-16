using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Usque.Core;

// Static, persistent filters: unlike a dynamic WFP session, terminating this
// process does not release the kill switch. Updates are atomic transactions.
public sealed class WfpKillSwitch(AppPaths paths) : IKillSwitch
{
    private static readonly Guid ProviderKey = new("57d78a76-3bd9-498b-b385-52a8b4777ada");
    private static readonly Guid SubLayerKey = new("f241d0c2-f4ad-419e-b599-4f08e445c241");
    private static readonly Guid LocalInterface = new("4cd62a49-59c3-4969-b7f3-bda5d32890a4");
    private static readonly Guid RemoteAddress = new("b235ae9a-1d64-49b8-a44c-5ff3d9095045");
    private static readonly Guid Protocol = new("3971ef2b-623e-4f9a-8cb1-6e79b806b9a7");
    private static readonly Guid LocalPort = new("0c1ba1af-5765-453f-af22-a8f791ac775b");
    private static readonly Guid RemotePort = new("c35a604d-d22b-4e1a-91b4-68f674ee674b");
    private static readonly Guid Flags = new("632ce23b-5167-435c-86d7-e903684aa80c");
    private static readonly Guid AppId = new("d78e1e87-8644-4ea5-9437-d809ecefc971");
    private const uint NotFound = 0x80320003, AlreadyExists = 0x80320009;
    private readonly object _sync = new();

    public bool IsEnabled
    {
        get
        {
            lock (_sync)
            {
                using var engine = new EngineHandle();
                foreach (var layer in KillSwitchPolicy.Layers)
                {
                    var key = FilterKey(layer, "Block");
                    var result = Native.FwpmFilterGetByKey0(engine.Value, ref key, out var filter);
                    if (result == NotFound) continue;
                    Check(result, "Read kill-switch state");
                    Native.FwpmFreeMemory0(ref filter);
                    return true;
                }
                return false;
            }
        }
    }

    public void Arm(string endpoint, bool http2)
    {
        var policy = KillSwitchPolicy.Build(endpoint, http2, paths.Engine,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "svchost.exe"));
        Transaction(handle =>
        {
            EnsureProvider(handle);
            DeleteFilters(handle);
            foreach (var rule in policy) AddFilter(handle, rule);
        });
    }

    public void AllowTunnel(ulong luid)
    {
        if (luid == 0) throw new ArgumentException("A valid tunnel LUID is required.", nameof(luid));
        Transaction(handle =>
        {
            foreach (var layer in KillSwitchPolicy.Layers)
            {
                DeleteFilter(handle, FilterKey(layer, "Tunnel"));
                AddFilter(handle, new("Tunnel", layer, true, TunnelLuid: luid));
            }
        });
    }

    public void Disarm() => Transaction(DeleteFilters);

    private void Transaction(Action<IntPtr> operation)
    {
        lock (_sync)
        {
            using var engine = new EngineHandle();
            Check(Native.FwpmTransactionBegin0(engine.Value, 0), "Begin firewall transaction");
            try
            {
                operation(engine.Value);
                Check(Native.FwpmTransactionCommit0(engine.Value), "Commit firewall transaction");
            }
            catch { Native.FwpmTransactionAbort0(engine.Value); throw; }
        }
    }

    private static void EnsureProvider(IntPtr handle)
    {
        using var memory = new Arena();
        var provider = new Native.Provider { Key = ProviderKey, Display = memory.Display("Usque Desktop kill switch"), Flags = 1 };
        var result = Native.FwpmProviderAdd0(handle, ref provider, IntPtr.Zero);
        if (result != AlreadyExists) Check(result, "Create firewall provider");
        var sublayer = new Native.SubLayer
        {
            Key = SubLayerKey, Display = memory.Display("Usque Desktop protection"), Flags = 1,
            Provider = memory.Struct(ProviderKey), Weight = 0xFFFF
        };
        result = Native.FwpmSubLayerAdd0(handle, ref sublayer, IntPtr.Zero);
        if (result != AlreadyExists) Check(result, "Create firewall sublayer");
    }

    private static void DeleteFilters(IntPtr handle)
    {
        foreach (var layer in KillSwitchPolicy.Layers)
            foreach (var name in KillSwitchPolicy.RuleNames) DeleteFilter(handle, FilterKey(layer, name));
    }
    private static void DeleteFilter(IntPtr handle, Guid key)
    {
        var result = Native.FwpmFilterDeleteByKey0(handle, ref key);
        if (result != NotFound) Check(result, "Remove owned firewall filter");
    }
    private static Guid FilterKey(FirewallLayer layer, string name) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"UsqueDesktop-v1/{layer.Key}/{name}"))[..16]);

    private static void AddFilter(IntPtr handle, FirewallRule rule)
    {
        using var memory = new Arena();
        var conditions = new List<Native.Condition>();
        void Add(Guid key, Native.Value value, uint match = 0) => conditions.Add(new() { Field = key, Match = match, Value = value });
        if (rule.Loopback) Add(Flags, Number(3, 1), 6); // UINT32, FLAGS_ALL_SET, IS_LOOPBACK
        if (rule.TunnelLuid is { } luid) Add(LocalInterface, Pointer(4, memory.Struct(luid)));
        if (rule.Endpoint is { } endpoint)
            Add(RemoteAddress, Number(3, BinaryPrimitives.ReadUInt32BigEndian(IPAddress.Parse(endpoint).GetAddressBytes())));
        if (rule.Protocol is { } protocol) Add(Protocol, Number(1, protocol));
        if (rule.LocalPort is { } local) Add(LocalPort, Number(2, local));
        if (rule.RemotePort is { } remote) Add(RemotePort, Number(2, remote));
        IntPtr app = IntPtr.Zero;
        try
        {
            if (rule.Application is { } application)
            {
                Check(Native.FwpmGetAppIdFromFileName0(application, out app), "Resolve firewall application identity");
                Add(AppId, Pointer(12, app)); // FWP_BYTE_BLOB_TYPE
            }
            var filter = new Native.Filter
            {
                Key = FilterKey(rule.Layer, rule.Name), Display = memory.Display($"Usque Desktop / {rule.Name}"),
                Flags = 1, Provider = memory.Struct(ProviderKey), Layer = rule.Layer.Key, SubLayer = SubLayerKey,
                Weight = Number(1, rule.Permit ? 15u : 0u),
                ConditionCount = (uint)conditions.Count, Conditions = memory.Array(conditions),
                Action = new() { Type = rule.Permit ? 0x1002u : 0x1001u }
            };
            Check(Native.FwpmFilterAdd0(handle, ref filter, IntPtr.Zero, out _), $"Install {rule.Name} firewall filter");
        }
        finally { if (app != IntPtr.Zero) Native.FwpmFreeMemory0(ref app); }
    }
    private static Native.Value Number(uint type, uint value) => new() { Type = type, Data = new() { Number = value } };
    private static Native.Value Pointer(uint type, IntPtr value) => new() { Type = type, Data = new() { Pointer = value } };
    private static void Check(uint result, string operation)
    {
        if (result != 0) throw new Win32Exception(unchecked((int)result), $"{operation} failed (0x{result:X8}). Run as administrator and ensure Base Filtering Engine is running.");
    }

    private sealed class EngineHandle : IDisposable
    {
        public IntPtr Value { get; }
        public EngineHandle()
        {
            Check(Native.FwpmEngineOpen0(null, 10, IntPtr.Zero, IntPtr.Zero, out var handle), "Open Windows Filtering Platform");
            Value = handle;
        }
        public void Dispose() => Native.FwpmEngineClose0(Value);
    }
    private sealed class Arena : IDisposable
    {
        private readonly List<IntPtr> _allocations = [];
        private IntPtr Allocate(int size)
        {
            var pointer = Marshal.AllocHGlobal(size);
            _allocations.Add(pointer);
            return pointer;
        }
        public IntPtr Struct<T>(T value) where T : struct
        {
            var pointer = Allocate(Marshal.SizeOf<T>());
            Marshal.StructureToPtr(value, pointer, false);
            return pointer;
        }
        public IntPtr Array<T>(List<T> values) where T : struct
        {
            if (values.Count == 0) return IntPtr.Zero;
            var size = Marshal.SizeOf<T>();
            var pointer = Allocate(size * values.Count);
            for (var i = 0; i < values.Count; i++) Marshal.StructureToPtr(values[i], pointer + size * i, false);
            return pointer;
        }
        public Native.Display Display(string name)
        {
            var pointer = Marshal.StringToHGlobalUni(name);
            _allocations.Add(pointer);
            return new() { Name = pointer };
        }
        public void Dispose() { foreach (var pointer in _allocations) Marshal.FreeHGlobal(pointer); }
    }

    // Layouts mirror fwpmtypes.h / fwptypes.h (64-bit Windows SDK).
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Blob { public uint Size; public IntPtr Data; }
        [StructLayout(LayoutKind.Sequential)] internal struct Display { public IntPtr Name, Description; }
        [StructLayout(LayoutKind.Explicit)] internal struct Union { [FieldOffset(0)] public ulong Number; [FieldOffset(0)] public IntPtr Pointer; }
        [StructLayout(LayoutKind.Sequential)] internal struct Value { public uint Type; public Union Data; }
        [StructLayout(LayoutKind.Sequential)] internal struct Condition { public Guid Field; public uint Match; public Value Value; }
        [StructLayout(LayoutKind.Sequential)] internal struct Action { public uint Type; public Guid Key; }
        [StructLayout(LayoutKind.Explicit)] internal struct Context { [FieldOffset(0)] public ulong Raw; [FieldOffset(0)] public Guid Provider; }
        [StructLayout(LayoutKind.Sequential)] internal struct Filter
        {
            public Guid Key; public Display Display; public uint Flags; public IntPtr Provider;
            public Blob ProviderData; public Guid Layer, SubLayer; public Value Weight;
            public uint ConditionCount; public IntPtr Conditions; public Action Action;
            public Context Context; public IntPtr Reserved; public ulong Id; public Value EffectiveWeight;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct Provider
        {
            public Guid Key; public Display Display; public uint Flags; public Blob Data; public IntPtr ServiceName;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct SubLayer
        {
            public Guid Key; public Display Display; public uint Flags; public IntPtr Provider; public Blob Data; public ushort Weight;
        }
        [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)] internal static extern uint FwpmEngineOpen0(string? server, uint auth, IntPtr identity, IntPtr session, out IntPtr handle);
        [DllImport("fwpuclnt.dll")] internal static extern uint FwpmEngineClose0(IntPtr handle);
        [DllImport("fwpuclnt.dll")] internal static extern uint FwpmTransactionBegin0(IntPtr handle, uint flags);
        [DllImport("fwpuclnt.dll")] internal static extern uint FwpmTransactionCommit0(IntPtr handle);
        [DllImport("fwpuclnt.dll")] internal static extern uint FwpmTransactionAbort0(IntPtr handle);
        [DllImport("fwpuclnt.dll")] internal static extern uint FwpmProviderAdd0(IntPtr handle, ref Provider provider, IntPtr security);
        [DllImport("fwpuclnt.dll")] internal static extern uint FwpmSubLayerAdd0(IntPtr handle, ref SubLayer sublayer, IntPtr security);
        [DllImport("fwpuclnt.dll")] internal static extern uint FwpmFilterAdd0(IntPtr handle, ref Filter filter, IntPtr security, out ulong id);
        [DllImport("fwpuclnt.dll")] internal static extern uint FwpmFilterDeleteByKey0(IntPtr handle, ref Guid key);
        [DllImport("fwpuclnt.dll")] internal static extern uint FwpmFilterGetByKey0(IntPtr handle, ref Guid key, out IntPtr filter);
        [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)] internal static extern uint FwpmGetAppIdFromFileName0(string filename, out IntPtr appId);
        [DllImport("fwpuclnt.dll")] internal static extern void FwpmFreeMemory0(ref IntPtr memory);
    }
}
