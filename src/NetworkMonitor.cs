using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;

namespace NetPulse
{
    // Native 64-bit counters include IPv4 and IPv6, without packet capture.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct InterfaceRow
    {
        public ulong Luid;
        public uint Index;
        public Guid Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Alias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Description;
        public uint PhysicalAddressLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PhysicalAddress;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PermanentAddress;
        public uint Mtu, Type, TunnelType, MediaType, PhysicalMediumType, AccessType, DirectionType;
        public byte Flags;
        public uint OperStatus, AdminStatus, MediaConnectState;
        public Guid NetworkGuid;
        public uint ConnectionType;
        public ulong TransmitLinkSpeed, ReceiveLinkSpeed;
        public ulong InOctets, InUcastPkts, InNUcastPkts, InDiscards, InErrors, InUnknownProtos;
        public ulong InUcastOctets, InMulticastOctets, InBroadcastOctets;
        public ulong OutOctets, OutUcastPkts, OutNUcastPkts, OutDiscards, OutErrors;
        public ulong OutUcastOctets, OutMulticastOctets, OutBroadcastOctets, OutQLen;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct InterfaceTableHeader { public uint Count; public InterfaceRow First; }

    internal sealed class Adapter
    {
        public string Id, Name, Description;
        public bool Active, Hardware;
        public ulong Received, Sent, LinkSpeed;
    }

    internal static class NetworkReader
    {
        [DllImport("iphlpapi.dll")] private static extern uint GetIfTable2(out IntPtr table);
        [DllImport("iphlpapi.dll")] private static extern void FreeMibTable(IntPtr table);

        public static List<Adapter> Read()
        {
            IntPtr table;
            uint error = GetIfTable2(out table);
            if (error != 0) throw new Win32Exception((int)error);
            try
            {
                int count = Marshal.ReadInt32(table);
                int offset = (int)Marshal.OffsetOf(typeof(InterfaceTableHeader), "First");
                int size = Marshal.SizeOf(typeof(InterfaceRow));
                var result = new List<Adapter>();
                for (int i = 0; i < count; i++)
                {
                    var row = (InterfaceRow)Marshal.PtrToStructure(IntPtr.Add(table, offset + i * size), typeof(InterfaceRow));
                    if (row.Type == 24 || (row.Flags & 2) != 0 || row.Id == Guid.Empty) continue;
                    result.Add(new Adapter {
                        Id = row.Id.ToString(), Name = row.Alias, Description = row.Description,
                        Active = row.OperStatus == 1,
                        Hardware = (row.Flags & 1) != 0 && (row.Flags & 128) == 0 && row.Type != 131,
                        Received = row.InOctets, Sent = row.OutOctets,
                        LinkSpeed = Math.Max(row.TransmitLinkSpeed, row.ReceiveLinkSpeed)
                    });
                }
                return result.OrderByDescending(a => a.Active).ThenBy(a => a.Name).ToList();
            }
            finally { FreeMibTable(table); }
        }

        public static List<Adapter> Select(List<Adapter> adapters, string id)
        {
            if (!String.IsNullOrEmpty(id)) return adapters.Where(a => a.Id == id && a.Active).ToList();
            var hardware = adapters.Where(a => a.Active && a.Hardware).ToList();
            if (hardware.Count > 0) return hardware;
            // A VM may expose no hardware NIC. Pick ONE virtual NIC, never sum
            // tunnels and their underlay, which would double-count the same bytes.
            return adapters.Where(a => a.Active).OrderByDescending(a => a.LinkSpeed).ThenBy(a => a.Id).Take(1).ToList();
        }
    }

    internal sealed class Sample
    {
        public double Down, Up;
        public bool Connected, Ready;
        public string Label;
        public List<Adapter> Adapters;
    }

    internal sealed class RateCalculator
    {
        private Dictionary<string, Adapter> previous = new Dictionary<string, Adapter>();
        private double lastTime = Double.NaN;
        private string lastSelection;

        public Sample Update(List<Adapter> adapters, string selection, double time)
        {
            var selected = NetworkReader.Select(adapters, selection);
            double seconds = time - lastTime;
            bool usable = selection == lastSelection && !Double.IsNaN(lastTime) && seconds > 0 && seconds <= 10;
            double down = 0, up = 0;
            bool ready = false;
            foreach (var adapter in selected)
            {
                Adapter old;
                if (usable && previous.TryGetValue(adapter.Id, out old))
                {
                    // Counter resets and adapter reconnections establish a new baseline.
                    if (adapter.Received >= old.Received) down += (adapter.Received - old.Received) / seconds;
                    if (adapter.Sent >= old.Sent) up += (adapter.Sent - old.Sent) / seconds;
                    ready = true;
                }
            }
            previous = selected.ToDictionary(a => a.Id);
            lastTime = time;
            lastSelection = selection;
            string label = selected.Count == 1 ? selected[0].Name : selected.Count > 1 ? "Сеть · " + selected.Count + " адаптера" : "Нет подключения";
            if (selected.Count == 0 && !String.IsNullOrEmpty(selection)) label = "Адаптер отключён";
            return new Sample { Down = down, Up = up, Connected = selected.Count > 0, Ready = ready, Label = label, Adapters = adapters };
        }
    }

    internal struct RateText
    {
        public string Value, Unit;
        public override string ToString() { return Value + " " + Unit; }
    }

    internal static class RateFormat
    {
        public static RateText Format(double bytes, bool bits)
        {
            double value = Math.Max(0, bytes) * (bits ? 8 : 1);
            string[] units = bits ? new[] { "бит/с", "Кбит/с", "Мбит/с", "Гбит/с", "Тбит/с" } : new[] { "Б/с", "КБ/с", "МБ/с", "ГБ/с", "ТБ/с" };
            int unit = 0;
            while (value >= 1000 && unit < units.Length - 1) { value /= 1000; unit++; }
            string pattern = unit == 0 || value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00";
            // Promote after rounding too: never display 1000 KБ/с.
            int decimals = pattern == "0" ? 0 : pattern == "0.0" ? 1 : 2;
            if (Math.Round(value, decimals) >= 1000 && unit < units.Length - 1) { value /= 1000; unit++; pattern = "0.00"; }
            return new RateText { Value = value.ToString(pattern, CultureInfo.GetCultureInfo("ru-RU")), Unit = units[unit] };
        }
    }
}
