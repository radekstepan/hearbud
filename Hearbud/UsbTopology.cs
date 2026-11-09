using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using CSCore.CoreAudioAPI;
using CSCore.Win32;

namespace Hearbud
{
    /// <summary>
    /// Startup check for the failure mode that produced the "ten clicks to
    /// close" report: a USB microphone and the pointing device behind the
    /// same hub. While the mic streams, the hub can drop HID reports, so
    /// clicks never reach Windows (and therefore never reach this app).
    /// Nothing can recover input the OS never received, so the only useful
    /// action is to detect the topology and warn before it happens.
    /// Best-effort: every native call is guarded and a probe failure just
    /// means "no warning", never a startup error.
    /// </summary>
    public static class UsbTopologyProbe
    {
        private const int DIGCF_PRESENT = 0x02;
        private const int DIGCF_ALLCLASSES = 0x04;
        private const int ERROR_INSUFFICIENT_BUFFER = 122;
        private const uint DEVPROP_TYPE_STRING = 0x12;
        private const uint DEVPROP_TYPE_GUID = 0x0D;

        private static readonly DEVPROPKEY DevInstanceId =
            new(new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), 256);
        private static readonly DEVPROPKEY DevParent =
            new(new Guid("4340a6c5-93fa-4706-972c-7b648008a5a7"), 8);
        private static readonly DEVPROPKEY DevClass =
            new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 9);
        private static readonly DEVPROPKEY DevService =
            new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 6);
        private static readonly DEVPROPKEY DevFriendlyName =
            new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
        private static readonly DEVPROPKEY DevContainerId =
            new(new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);

        // MMDevice property stores expose the backing device instance id under
        // this key; the container id is the documented fallback.
        private static readonly Guid AudioDeviceInstanceIdKey =
            new("b3f8fa53-0004-438e-9003-51a46e139bfc");
        private static readonly Guid DeviceContainerIdKey =
            new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c");
        private static readonly Guid UsbSubgroup =
            new("2a737441-1930-4402-8d77-b2bebba308a3");
        private static readonly Guid UsbSuspendSetting =
            new("48e6b7a6-50f5-4782-a5d4-53bb8f07e226");

        /// <summary>
        /// Returns a human-readable warning when <paramref name="endpointId"/>
        /// and a mouse share a non-root USB hub, or null when the layout looks
        /// safe / cannot be determined.
        /// </summary>
        public static string? Check(string? endpointId, string? endpointFriendlyName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(endpointId)) return null;

                Dictionary<string, UsbNode> tree = EnumerateDeviceTree();
                if (tree.Count == 0) return null;

                string? micId = ResolveMicNodeId(endpointId, endpointFriendlyName, tree);
                if (micId == null || !tree.TryGetValue(micId, out UsbNode? micNode)) return null;
                if (!micId.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)) return null;

                var parents = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                var services = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                var mice = new List<UsbNode>();
                foreach (UsbNode node in tree.Values)
                {
                    parents[node.Id] = node.ParentId;
                    services[node.Id] = node.Service;
                    if (string.Equals(node.Class, "Mouse", StringComparison.OrdinalIgnoreCase)) mice.Add(node);
                }

                foreach (UsbNode mouse in mice)
                {
                    string? sharedId = FindSharedHub(parents, services, micId, new[] { mouse.Id });
                    if (sharedId == null || !tree.TryGetValue(sharedId, out UsbNode? hub)) continue;
                    return BuildWarning(micNode, mouse, hub, SelectiveSuspendEnabled());
                }
            }
            catch
            {
                // Never let diagnostics affect startup.
            }
            return null;
        }

        /// <summary>
        /// Diagnostic dump of what the probe resolved: the microphone node,
        /// every mouse node, and each USB hub chain. Intended for logs/support;
        /// never throws.
        /// </summary>
        public static string Describe(string? endpointId, string? endpointFriendlyName)
        {
            var sb = new StringBuilder();
            try
            {
                Dictionary<string, UsbNode> tree = EnumerateDeviceTree();
                sb.AppendLine(CultureInfo.InvariantCulture, $"devices={tree.Count}");
                string? micId = string.IsNullOrWhiteSpace(endpointId)
                    ? null
                    : ResolveMicNodeId(endpointId, endpointFriendlyName, tree);
                sb.AppendLine(CultureInfo.InvariantCulture, $"micNode={micId ?? "(none)"}");
                if (micId != null)
                {
                    string? current = micId;
                    for (int depth = 0; depth < 16 && current != null; depth++)
                    {
                        if (!tree.TryGetValue(current, out UsbNode? node))
                        {
                            sb.AppendLine(CultureInfo.InvariantCulture,
                                $"  micChain[{depth}]={current} (not enumerated)");
                            break;
                        }
                        sb.AppendLine(CultureInfo.InvariantCulture,
                            $"  micChain[{depth}]={node.Id} class={node.Class} svc={node.Service} " +
                            $"parent={node.ParentId} name={node.FriendlyName}");
                        current = node.ParentId;
                    }
                }
                foreach (UsbNode node in tree.Values)
                {
                    bool candidate = string.Equals(node.Class, "Mouse", StringComparison.OrdinalIgnoreCase) ||
                                     node.Id.Contains("VID_1BCF", StringComparison.OrdinalIgnoreCase) ||
                                     (node.FriendlyName?.Contains("mouse", StringComparison.OrdinalIgnoreCase) ?? false);
                    if (!candidate) continue;
                    sb.AppendLine(CultureInfo.InvariantCulture,
                        $"mouseCandidate={node.Id} class={node.Class} svc={node.Service} name={node.FriendlyName}");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"probe error: {ex.GetType().Name}: {ex.Message}");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Strips the MMDevice "{n}." prefix and any "#{interface}" suffix from
        /// a device instance id. Public for the unit tests.
        /// </summary>
        public static string? NormalizeEndpointInstanceId(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string id = raw.Trim();
            int dot = id.IndexOf('.', StringComparison.Ordinal);
            if (id[0] == '{' && dot > 0 && dot < 8) id = id[(dot + 1)..];
            int iface = id.IndexOf("#{", StringComparison.Ordinal);
            if (iface > 0) id = id[..iface];
            return id.Trim();
        }

        private static string? ResolveMicNodeId(
            string endpointId, string? endpointFriendlyName, IReadOnlyDictionary<string, UsbNode> tree)
        {
            string? id = NormalizeEndpointInstanceId(TryGetEndpointString(endpointId, AudioDeviceInstanceIdKey));
            if (id != null)
            {
                if (tree.ContainsKey(id)) return id;
                // The endpoint may name an interface; climb to the nearest
                // ancestor that SetupAPI enumerated.
                string? current = id;
                for (int i = 0; i < 8 && current != null; i++)
                {
                    int slash = current.LastIndexOf('\\');
                    if (slash <= 0) break;
                    current = current[..slash];
                    if (tree.ContainsKey(current)) return current;
                }
            }

            Guid? container = TryGetEndpointContainerId(endpointId);
            if (container is Guid cid)
            {
                UsbNode? match = null;
                int count = 0;
                foreach (UsbNode node in tree.Values)
                {
                    if (node.ContainerId != cid) continue;
                    if (!node.Id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)) continue;
                    count++;
                    if (match == null || (!IsUsbAudioNode(match) && IsUsbAudioNode(node))) match = node;
                }
                if (match != null && (count == 1 || IsUsbAudioNode(match))) return match.Id;
            }

            if (!string.IsNullOrWhiteSpace(endpointFriendlyName))
            {
                string probe = endpointFriendlyName;
                int open = probe.LastIndexOf('('), close = probe.LastIndexOf(')');
                if (open >= 0 && close > open) probe = probe[(open + 1)..close];
                foreach (UsbNode node in tree.Values)
                {
                    if (!node.Id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!IsUsbAudioNode(node) || string.IsNullOrEmpty(node.FriendlyName)) continue;
                    if (node.FriendlyName.Contains(probe, StringComparison.OrdinalIgnoreCase) ||
                        probe.Contains(node.FriendlyName, StringComparison.OrdinalIgnoreCase))
                        return node.Id;
                }
            }
            return null;
        }

        private static bool IsUsbAudioNode(UsbNode node)
        {
            if (string.Equals(node.Service, "usbaudio", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(node.Service, "usbaudio2", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(node.Class, "MEDIA", StringComparison.OrdinalIgnoreCase)) return true;
            return node.Id.StartsWith(@"USB\Class_01", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns the id of a non-root USB hub shared by the microphone and
        /// any of the mouse devices, or null. Pure topology math (no P/Invoke)
        /// so the risky-layout rule is unit-testable.
        /// </summary>
        public static string? FindSharedHub(
            IReadOnlyDictionary<string, string?> parentById,
            IReadOnlyDictionary<string, string?> serviceById,
            string micId,
            IEnumerable<string> mouseIds)
        {
            List<string> micHubs = HubIds(parentById, serviceById, micId);
            foreach (string mouseId in mouseIds)
            {
                List<string> mouseHubs = HubIds(parentById, serviceById, mouseId);
                foreach (string hub in micHubs)
                {
                    if (IsRootHub(hub)) continue;
                    foreach (string mouseHub in mouseHubs)
                    {
                        if (string.Equals(mouseHub, hub, StringComparison.OrdinalIgnoreCase))
                            return hub;
                    }
                }
            }
            return null;
        }

        private static List<string> HubIds(
            IReadOnlyDictionary<string, string?> parentById,
            IReadOnlyDictionary<string, string?> serviceById,
            string deviceId)
        {
            var hubs = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? current = deviceId;
            for (int depth = 0; depth < 32 && current != null && seen.Add(current); depth++)
            {
                serviceById.TryGetValue(current, out string? service);
                if (IsHub(current, service)) hubs.Add(current);
                if (!parentById.TryGetValue(current, out string? parent)) break;
                current = parent;
            }
            return hubs;
        }

        private static bool IsHub(string id, string? service)
        {
            if (IsRootHub(id)) return true;
            return string.Equals(service, "USBHUB", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(service, "USBHUB3", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsRootHub(string id)
            => id.StartsWith(@"USB\ROOT_HUB", StringComparison.OrdinalIgnoreCase);

        private static string BuildWarning(UsbNode mic, UsbNode mouse, UsbNode hub, bool suspendEnabled)
        {
            string micName = FirstNonEmpty(mic.FriendlyName, "microphone");
            string mouseName = FirstNonEmpty(mouse.FriendlyName, "mouse");
            string hubName = FirstNonEmpty(hub.FriendlyName, hub.Id);
            string message =
                $"Microphone \"{micName}\" and {mouseName} share USB hub \"{hubName}\". " +
                "Mouse clicks can be dropped while the microphone is open; use separate USB ports.";
            if (suspendEnabled) message += " USB selective suspend is enabled.";
            return message.Length <= 512 ? message : message[..512];
        }

        private static string FirstNonEmpty(string? value, string fallback)
            => string.IsNullOrWhiteSpace(value) ? fallback : value;

        private static Dictionary<string, UsbNode> EnumerateDeviceTree()
        {
            var tree = new Dictionary<string, UsbNode>(StringComparer.OrdinalIgnoreCase);
            IntPtr set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return tree;
            try
            {
                var data = new SP_DEVINFO_DATA { CbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                for (uint index = 0; SetupDiEnumDeviceInfo(set, index, ref data); index++)
                {
                    string? id = GetStringProperty(set, ref data, DevInstanceId);
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    tree[id] = new UsbNode(
                        id,
                        GetStringProperty(set, ref data, DevParent),
                        GetStringProperty(set, ref data, DevClass),
                        GetStringProperty(set, ref data, DevService),
                        GetStringProperty(set, ref data, DevFriendlyName),
                        GetGuidProperty(set, ref data, DevContainerId));
                }
            }
            finally
            {
                _ = SetupDiDestroyDeviceInfoList(set);
            }
            return tree;
        }

        private static string? TryGetEndpointString(string endpointId, Guid keyGuid)
        {
            object? value = TryGetEndpointProperty(endpointId, keyGuid, 2);
            return value as string;
        }

        private static Guid? TryGetEndpointContainerId(string endpointId)
        {
            object? value = TryGetEndpointProperty(endpointId, DeviceContainerIdKey, 2);
            if (value is Guid guid) return guid;
            if (value is string text && Guid.TryParse(text, out Guid parsed)) return parsed;
            return null;
        }

        private static object? TryGetEndpointProperty(string endpointId, Guid keyGuid, int propertyId)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDevice(endpointId);
                using var store = device.OpenPropertyStore(StorageAccess.Read);
                return store.GetValue(new PropertyKey(keyGuid, propertyId)).GetValue();
            }
            catch
            {
                return null;
            }
        }

        private static bool SelectiveSuspendEnabled()
        {
            IntPtr scheme = IntPtr.Zero;
            try
            {
                if (PowerGetActiveScheme(IntPtr.Zero, out scheme) != 0 || scheme == IntPtr.Zero) return false;
                Guid subgroup = UsbSubgroup;
                Guid setting = UsbSuspendSetting;
                if (PowerReadACValueIndex(IntPtr.Zero, scheme, ref subgroup, ref setting, out uint value) != 0) return false;
                return value != 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (scheme != IntPtr.Zero) _ = LocalFree(scheme);
            }
        }

        private static string? GetStringProperty(IntPtr set, ref SP_DEVINFO_DATA data, DEVPROPKEY key)
        {
            if (!GetPropertyBytes(set, ref data, key, out uint type, out byte[]? bytes)) return null;
            if (type != DEVPROP_TYPE_STRING || bytes == null) return null;
            int end = bytes.Length;
            while (end >= 2 && bytes[end - 1] == 0 && bytes[end - 2] == 0) end -= 2;
            return end <= 0 ? "" : Encoding.Unicode.GetString(bytes, 0, end);
        }

        private static Guid? GetGuidProperty(IntPtr set, ref SP_DEVINFO_DATA data, DEVPROPKEY key)
        {
            if (!GetPropertyBytes(set, ref data, key, out uint type, out byte[]? bytes)) return null;
            if (type != DEVPROP_TYPE_GUID || bytes == null || bytes.Length < 16) return null;
            return new Guid(bytes.AsSpan(0, 16));
        }

        private static bool GetPropertyBytes(
            IntPtr set, ref SP_DEVINFO_DATA data, DEVPROPKEY key, out uint type, out byte[]? bytes)
        {
            bytes = null;
            type = 0;
            if (SetupDiGetDevicePropertyW(
                    set, ref data, ref key, out type, IntPtr.Zero, 0, out int required, 0))
                return true;
            if (Marshal.GetLastWin32Error() != ERROR_INSUFFICIENT_BUFFER || required <= 0) return false;

            IntPtr buffer = Marshal.AllocHGlobal(required);
            try
            {
                if (!SetupDiGetDevicePropertyW(
                        set, ref data, ref key, out type, buffer, required, out int written, 0))
                    return false;
                int length = Math.Min(required, written);
                if (length <= 0) return true;
                bytes = new byte[length];
                Marshal.Copy(buffer, bytes, 0, length);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private sealed class UsbNode
        {
            public UsbNode(
                string id, string? parentId, string? deviceClass, string? service,
                string? friendlyName, Guid? containerId)
            {
                Id = id;
                ParentId = parentId;
                Class = deviceClass;
                Service = service;
                FriendlyName = friendlyName;
                ContainerId = containerId;
            }

            public string Id { get; }
            public string? ParentId { get; }
            public string? Class { get; }
            public string? Service { get; }
            public string? FriendlyName { get; }
            public Guid? ContainerId { get; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DEVPROPKEY
        {
            public Guid FmtId;
            public uint Pid;

            public DEVPROPKEY(Guid fmtId, uint pid)
            {
                FmtId = fmtId;
                Pid = pid;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public int CbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevsW(
            IntPtr classGuid, string? enumerator, IntPtr hwndParent, int flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiEnumDeviceInfo(
            IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiGetDevicePropertyW(
            IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, ref DEVPROPKEY propertyKey,
            out uint propertyType, IntPtr propertyBuffer, int propertyBufferSize,
            out int requiredSize, uint flags);

        [DllImport("powrprof.dll")]
        private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

        [DllImport("powrprof.dll")]
        private static extern uint PowerReadACValueIndex(
            IntPtr rootPowerKey, IntPtr schemeGuid, ref Guid subGroupGuid,
            ref Guid powerSettingGuid, out uint acValueIndex);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);
    }
}
