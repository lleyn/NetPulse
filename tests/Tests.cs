using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace NetPulse
{
    internal static class Tests
    {
        private static int assertions;
        private static void Check(bool value, string message)
        {
            assertions++;
            if (!value) throw new Exception(message);
        }
        private static void Near(double actual, double expected, string message)
        { Check(Math.Abs(actual - expected) < 0.001, message + ": " + actual + " != " + expected); }
        private static Adapter A(string id, ulong received, ulong sent, bool hardware = true, bool active = true)
        { return new Adapter { Id = id, Name = id, Received = received, Sent = sent, Hardware = hardware, Active = active, LinkSpeed = 1000000000 }; }
        private static List<Adapter> List(params Adapter[] a) { return a.ToList(); }

        public static int Main()
        {
            try
            {
                var calc = new RateCalculator();
                var sample = calc.Update(List(A("a", 1000000, 500000)), "", 0);
                Check(!sample.Ready && sample.Connected, "First sample must be a baseline");
                Near(sample.Down, 0, "No lifetime-byte spike");
                sample = calc.Update(List(A("a", 1003000, 501000)), "", 2);
                Near(sample.Down, 1500, "Uses actual elapsed time");
                Near(sample.Up, 500, "Upload direction");
                sample = calc.Update(List(A("a", 2, 1)), "", 3);
                Near(sample.Down, 0, "Receive reset"); Near(sample.Up, 0, "Send reset");
                sample = calc.Update(List(A("a", 102, 101), A("b", 999999999, 999999999)), "", 4);
                Near(sample.Down, 100, "New NIC does not contribute historical bytes");
                sample = calc.Update(List(A("b", 1000000999, 1000000199)), "", 5);
                Near(sample.Down, 1000, "NIC removed without spike");
                Near(sample.Up, 200, "Remaining adapter continues");
                sample = calc.Update(List(), "", 6);
                Check(!sample.Connected, "Disconnected state");
                sample = calc.Update(List(A("b", 2000000999, 2000000199)), "", 7);
                Near(sample.Down, 0, "Reconnect baseline");
                sample = calc.Update(List(A("b", 3000000999, 3000000199)), "", 100);
                Near(sample.Down, 0, "Resume from sleep baseline");
                sample = calc.Update(List(A("b", 4000000999, 4000000199)), "b", 101);
                Near(sample.Down, 0, "Selection change baseline");
                sample = calc.Update(List(A("b", 4000001009, 4000000209)), "b", 101);
                Near(sample.Down, 0, "Zero elapsed time");
                calc = new RateCalculator();
                calc.Update(List(A("large", 9007199254740993, 9007199254740993)), "", 0);
                sample = calc.Update(List(A("large", 9007199254741093, 9007199254741043)), "", 0.5);
                Near(sample.Down, 200, "64-bit subtraction before float conversion");
                Near(sample.Up, 100, "Large upload counters");
                var adapters = List(A("physical", 0, 0), A("vpn", 0, 0, false), A("unplugged", 0, 0, true, false));
                Check(NetworkReader.Select(adapters, "").Single().Id == "physical", "Exclude VPN duplication");
                Check(NetworkReader.Select(adapters, "vpn").Single().Id == "vpn", "Manual VPN selection");
                Check(NetworkReader.Select(adapters, "missing").Count == 0, "Missing selection must not silently switch");
                Check(NetworkReader.Select(List(A("vm1", 0, 0, false), A("vm2", 0, 0, false)), "").Count == 1, "Only one virtual fallback");
                calc = new RateCalculator();
                calc.Update(List(A("a", 0, 0), A("b", 0, 0)), "", 0);
                sample = calc.Update(List(A("a", 100, 50), A("b", 200, 70)), "", 1);
                Near(sample.Down, 300, "Multiple physical adapters"); Near(sample.Up, 120, "Multiple uploads");
                Check(RateFormat.Format(125000, true).ToString() == "1,00 Мбит/с", "Bits conversion");
                Check(RateFormat.Format(125000, false).ToString() == "125 КБ/с", "Decimal bytes");
                Check(RateFormat.Format(999999, false).ToString() == "1,00 МБ/с", "Rounding promotes unit");
                Check(RateFormat.Format(0, false).ToString() == "0 Б/с", "Idle format");

                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings-test.xml");
                var settings = new Settings { Left = -550, Top = 150, Bits = true, Compact = true, AdapterId = "sample-guid" };
                Check(settings.Save(path), "Settings save");
                var restored = Settings.Load(path);
                Check(restored.Left == -550 && restored.Top == 150 && restored.Bits && restored.Compact && restored.AdapterId == "sample-guid", "Settings roundtrip");
                settings.Left = 100;
                Check(settings.Save(path) && Settings.Load(path).Left == 100, "Atomic replacement");
                File.WriteAllText(path, "broken XML");
                Check(Settings.Load(path).AlwaysOnTop, "Corrupt settings use defaults");
                File.Delete(path);

                Check(Marshal.SizeOf(typeof(InterfaceRow)) == 1352, "MIB_IF_ROW2 size");
                Check((int)Marshal.OffsetOf(typeof(InterfaceTableHeader), "First") == 8, "Native table alignment");
                Check((int)Marshal.OffsetOf(typeof(InterfaceRow), "InOctets") == 1208, "Native receive offset");
                Check((int)Marshal.OffsetOf(typeof(InterfaceRow), "OutOctets") == 1280, "Native transmit offset");
                var native = NetworkReader.Read();
                Check(native.Count > 0, "Native API provides adapters on this machine");
                int matches = 0;
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    var row = native.FirstOrDefault(a => a.Id.Equals(nic.Id.Trim('{', '}'), StringComparison.OrdinalIgnoreCase));
                    if (row == null || nic.OperationalStatus != OperationalStatus.Up) continue;
                    var stats = nic.GetIPv4Statistics();
                    var after = NetworkReader.Read().First(a => a.Id == row.Id);
                    Check((ulong)stats.BytesReceived >= row.Received && (ulong)stats.BytesReceived <= after.Received, "Live receive counters agree with .NET");
                    Check((ulong)stats.BytesSent >= row.Sent && (ulong)stats.BytesSent <= after.Sent, "Live transmit counters agree with .NET");
                    matches++;
                }
                Check(matches > 0, "At least one active adapter cross-checked");
                Console.WriteLine("PASS: " + assertions + " assertions; " + native.Count + " native adapters; " + matches + " live counter cross-checks.");
                foreach (var a in NetworkReader.Select(native, "")) Console.WriteLine("AUTO: " + a.Name + " (hardware=" + a.Hardware + ")");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); return 1; }
        }
    }
}
