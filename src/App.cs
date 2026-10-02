using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

[assembly: AssemblyTitle("NetPulse")]
[assembly: AssemblyDescription("Компактный монитор скорости сети")]
[assembly: AssemblyVersion("1.0.0.0")]

namespace NetPulse
{
    internal static class Program
    {
        [STAThread]
        public static int Main()
        {
            string identity = WindowsIdentity.GetCurrent().User.Value;
            bool created;
            using (var mutex = new Mutex(true, @"Local\NetPulse." + identity, out created))
            using (var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\NetPulse.Show." + identity))
            {
                if (!created) { showEvent.Set(); return 0; }
                try
                {
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    var widget = new Widget(app);
                    var wait = ThreadPool.RegisterWaitForSingleObject(showEvent, delegate {
                        if (!app.Dispatcher.HasShutdownStarted) app.Dispatcher.BeginInvoke(new Action(widget.Show));
                    }, null, Timeout.Infinite, false);
                    try { widget.Show(); app.Run(); }
                    finally { wait.Unregister(null); widget.Dispose(); }
                    return 0;
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Не удалось запустить NetPulse.\n\n" + ex.Message, "NetPulse", MessageBoxButton.OK, MessageBoxImage.Error);
                    return 1;
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }

    internal sealed class HistoryGraph : FrameworkElement
    {
        private readonly List<Sample> history = new List<Sample>();
        private static readonly Brush Blue = BrushOf("#80C8FF"), Purple = BrushOf("#B6A2FF"), GridBrush = BrushOf("#323C50");

        public void Add(Sample sample)
        {
            history.Add(sample);
            if (history.Count > 60) history.RemoveAt(0);
            InvalidateVisual();
        }
        public void Clear() { history.Clear(); InvalidateVisual(); }
        private static Brush BrushOf(string color) { var b = (Brush)new BrushConverter().ConvertFromString(color); b.Freeze(); return b; }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight - 3;
            if (w <= 0 || h <= 0) return;
            var guide = new Pen(GridBrush, 0.5);
            dc.DrawLine(guide, new Point(0, h), new Point(w, h));
            dc.DrawLine(guide, new Point(0, h / 2), new Point(w, h / 2));
            double maximum = Math.Max(1024, history.Count == 0 ? 0 : history.Max(s => Math.Max(s.Down, s.Up))) * 1.12;
            DrawSeries(dc, w, h, maximum, true, Blue);
            DrawSeries(dc, w, h, maximum, false, Purple);
        }

        private void DrawSeries(DrawingContext dc, double w, double h, double max, bool down, Brush brush)
        {
            if (history.Count < 2) return;
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                for (int i = 0; i < history.Count; i++)
                {
                    var point = new Point((60 - history.Count + i) * w / 59, h - (down ? history[i].Down : history[i].Up) / max * (h - 2));
                    if (i == 0) ctx.BeginFigure(point, false, false);
                    else ctx.LineTo(point, true, false);
                }
            }
            geometry.Freeze();
            dc.DrawGeometry(null, new Pen(brush, 1.5) { LineJoin = PenLineJoin.Round }, geometry);
        }
    }

    internal sealed class Widget : IDisposable
    {
        private readonly Application app;
        private readonly Window window;
        private readonly Settings settings;
        private readonly string settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NetPulse.settings.xml");
        private readonly RateCalculator calculator = new RateCalculator();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly HistoryGraph graph = new HistoryGraph();
        private readonly DispatcherTimer timer;
        private readonly Forms.NotifyIcon tray;
        private Sample lastSample;
        private bool polling, disposed;
        private double lastSampleTime;
        private System.Drawing.Icon trayIcon;

        public Widget(Application app)
        {
            this.app = app;
            settings = Settings.Load(settingsPath);
            using (var xaml = Assembly.GetExecutingAssembly().GetManifestResourceStream("NetPulse.Widget.xaml"))
                window = (Window)XamlReader.Load(xaml);
            app.MainWindow = window;
            window.Topmost = settings.AlwaysOnTop;
            ((Grid)window.FindName("GraphHost")).Children.Add(graph);
            ((Button)window.FindName("CompactButton")).Click += delegate { ToggleCompact(); };
            ((Button)window.FindName("MenuButton")).Click += delegate { OpenMenu(); };
            window.MouseLeftButtonDown += OnDrag;
            window.MouseRightButtonUp += delegate { OpenMenu(); };
            window.KeyDown += delegate(object sender, KeyEventArgs e) {
                if (e.Key == Key.Escape) { window.Hide(); e.Handled = true; }
                if (e.Key == Key.Apps || (e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift)) { OpenMenu(); e.Handled = true; }
            };
            window.Closing += delegate { Save(); };
            window.Closed += delegate { app.Shutdown(); };
            window.SourceInitialized += delegate {
                HwndSource.FromHwnd(new WindowInteropHelper(window).Handle).AddHook(WindowMessage);
            };
            ApplyCompact();
            var work = SystemParameters.WorkArea;
            window.Left = IsFinite(settings.Left) ? settings.Left : work.Right - window.Width - 20;
            window.Top = IsFinite(settings.Top) ? settings.Top : work.Bottom - window.Height - 20;
            window.Loaded += delegate { ClampToScreen(false); Poll(); };
            trayIcon = CreateTrayIcon();
            window.Icon = Imaging.CreateBitmapSourceFromHIcon(trayIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            tray = new Forms.NotifyIcon { Icon = trayIcon, Text = "NetPulse · скорость сети", Visible = true };
            tray.DoubleClick += delegate { window.Dispatcher.BeginInvoke(new Action(Show)); };
            var trayMenu = new Forms.ContextMenuStrip();
            trayMenu.Items.Add("Показать / скрыть", null, delegate { window.Dispatcher.BeginInvoke(new Action(delegate { if (window.IsVisible) window.Hide(); else Show(); })); });
            trayMenu.Items.Add("Компактный режим", null, delegate { window.Dispatcher.BeginInvoke(new Action(delegate { ToggleCompact(); Show(); })); });
            trayMenu.Items.Add(new Forms.ToolStripSeparator());
            trayMenu.Items.Add("Выход", null, delegate { window.Dispatcher.BeginInvoke(new Action(window.Close)); });
            tray.ContextMenuStrip = trayMenu;
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += delegate { Poll(); };
            timer.Start();
        }

        private static bool IsFinite(double v) { return !Double.IsNaN(v) && !Double.IsInfinity(v) && Math.Abs(v) < 100000; }
        private TextBlock Text(string name) { return (TextBlock)window.FindName(name); }

        public void Show()
        {
            if (disposed) return;
            window.Show();
            window.WindowState = WindowState.Normal;
            ClampToScreen(false);
            window.Activate();
        }

        private async void Poll()
        {
            if (polling || disposed) return;
            polling = true;
            try
            {
                var adapters = await Task.Run(() => NetworkReader.Read());
                if (disposed) return;
                double now = clock.Elapsed.TotalSeconds;
                if (now - lastSampleTime > 10) graph.Clear();
                lastSampleTime = now;
                lastSample = calculator.Update(adapters, settings.AdapterId, now);
                graph.Add(lastSample);
                Render();
            }
            catch (Exception ex)
            {
                if (!disposed)
                {
                    lastSample = null;
                    Text("DownValue").Text = Text("UpValue").Text = "—";
                    Text("DownUnit").Text = Text("UpUnit").Text = "";
                    Text("CompactDown").Text = "↓ —";
                    Text("CompactUp").Text = "↑ —";
                    Text("AdapterLabel").Text = "Счётчики недоступны";
                    Text("AdapterLabel").ToolTip = ex.Message;
                    ((System.Windows.Shapes.Ellipse)window.FindName("StatusDot")).Fill = Brushes.SandyBrown;
                    tray.Text = "NetPulse · счётчики недоступны";
                    graph.Clear();
                }
            }
            finally { polling = false; }
        }

        private void Render()
        {
            if (lastSample == null) return;
            var down = RateFormat.Format(lastSample.Down, settings.Bits);
            var up = RateFormat.Format(lastSample.Up, settings.Bits);
            bool pending = lastSample.Connected && !lastSample.Ready;
            Text("DownValue").Text = pending ? "—" : down.Value;
            Text("DownUnit").Text = down.Unit;
            Text("UpValue").Text = pending ? "—" : up.Value;
            Text("UpUnit").Text = up.Unit;
            Text("CompactDown").Text = "↓ " + (pending ? "—" : down.ToString());
            Text("CompactUp").Text = "↑ " + (pending ? "—" : up.ToString());
            Text("AdapterLabel").Text = lastSample.Label;
            Text("AdapterLabel").ToolTip = lastSample.Label + "\nСетевой трафик, включая локальную сеть";
            ((System.Windows.Shapes.Ellipse)window.FindName("StatusDot")).Fill = new SolidColorBrush(lastSample.Connected ? Color.FromRgb(108, 221, 177) : Color.FromRgb(143, 151, 168));
            string tooltip = lastSample.Connected ? "NetPulse · ↓ " + down + "  ↑ " + up : "NetPulse · " + lastSample.Label;
            tray.Text = tooltip.Length > 63 ? tooltip.Substring(0, 63) : tooltip;
        }

        private void ToggleCompact() { settings.Compact = !settings.Compact; ApplyCompact(); ClampToScreen(false); Save(); }
        private void ApplyCompact()
        {
            ((Grid)window.FindName("ExpandedPanel")).Visibility = settings.Compact ? Visibility.Collapsed : Visibility.Visible;
            ((Grid)window.FindName("CompactPanel")).Visibility = settings.Compact ? Visibility.Visible : Visibility.Collapsed;
            window.Height = settings.Compact ? 100 : 192;
            var button = (Button)window.FindName("CompactButton");
            button.Content = settings.Compact ? "+" : "−";
            button.ToolTip = settings.Compact ? "Развернуть" : "Компактный режим";
            System.Windows.Automation.AutomationProperties.SetName(button, settings.Compact ? "Развернуть" : "Компактный режим");
        }

        private void OnDrag(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if (e.ClickCount == 2) { ToggleCompact(); return; }
            try { window.DragMove(); ClampToScreen(true); Save(); }
            catch (InvalidOperationException) { }
        }

        private MenuItem Item(string title, Action action, bool checkable, bool check)
        {
            var item = new MenuItem { Header = title, IsCheckable = checkable, IsChecked = check };
            item.Click += delegate { action(); };
            return item;
        }

        private void OpenMenu()
        {
            var menu = new ContextMenu { PlacementTarget = (Button)window.FindName("MenuButton"), Placement = PlacementMode.Bottom, FontFamily = window.FontFamily, FontSize = 12 };
            menu.Items.Add(Item("Поверх всех окон", delegate { settings.AlwaysOnTop = !settings.AlwaysOnTop; window.Topmost = settings.AlwaysOnTop; Save(); }, true, settings.AlwaysOnTop));
            menu.Items.Add(Item("Компактный режим", ToggleCompact, true, settings.Compact));
            var units = new MenuItem { Header = "Единицы скорости" };
            units.Items.Add(Item("Байты/с · КБ/с · МБ/с", delegate { settings.Bits = false; Render(); Save(); }, true, !settings.Bits));
            units.Items.Add(Item("Биты/с · Кбит/с · Мбит/с", delegate { settings.Bits = true; Render(); Save(); }, true, settings.Bits));
            menu.Items.Add(units);
            var adapters = new MenuItem { Header = "Сетевой адаптер" };
            adapters.Items.Add(Item("Авто · физические адаптеры", delegate { SelectAdapter(""); }, true, String.IsNullOrEmpty(settings.AdapterId)));
            if (lastSample != null)
            {
                adapters.Items.Add(new Separator());
                foreach (var adapter in lastSample.Adapters)
                {
                    var selected = adapter;
                    var item = Item(adapter.Name + (adapter.Active ? "" : " · отключён"), delegate { SelectAdapter(selected.Id); }, true, settings.AdapterId == adapter.Id);
                    item.ToolTip = adapter.Description;
                    adapters.Items.Add(item);
                }
            }
            menu.Items.Add(adapters);
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Скрыть в трей", delegate { window.Hide(); }, false, false));
            menu.Items.Add(Item("О NetPulse", ShowAbout, false, false));
            menu.Items.Add(Item("Выход", window.Close, false, false));
            menu.IsOpen = true;
        }

        private void SelectAdapter(string id)
        {
            settings.AdapterId = id;
            graph.Clear();
            if (lastSample != null) { lastSample = calculator.Update(lastSample.Adapters, id, clock.Elapsed.TotalSeconds); Render(); }
            Save();
        }

        private void ShowAbout()
        {
            MessageBox.Show(window, "NetPulse 1.0\n\nТекущая скорость загрузки и отдачи.\nОбновление: раз в секунду. График: 60 секунд.\n\nПеретаскивайте окно мышью.\nДвойной щелчок — компактный режим.\nEsc — скрыть в трей. Значок в трее — вернуть.\n\n1 КБ = 1000 байт. 1 байт = 8 бит.\nУчитывается также трафик локальной сети.\nЭто монитор активности, а не тест скорости интернета.\n\nНастройки сохраняются рядом с приложением.", "О NetPulse", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Save()
        {
            settings.Left = window.Left; settings.Top = window.Top;
            if (!settings.Save(settingsPath))
                ((Button)window.FindName("MenuButton")).ToolTip = "Настройки не сохранены: перенесите приложение в папку с доступом для записи.";
        }

        [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

        private void ClampToScreen(bool snap)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
            Rect rect;
            if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info) || !GetWindowRect(hwnd, out rect)) return;
            int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
            int x = Math.Max(info.Work.Left, Math.Min(rect.Left, info.Work.Right - width));
            int y = Math.Max(info.Work.Top, Math.Min(rect.Top, info.Work.Bottom - height));
            if (snap)
            {
                if (Math.Abs(x - info.Work.Left) < 22) x = info.Work.Left;
                if (Math.Abs(y - info.Work.Top) < 22) y = info.Work.Top;
                if (Math.Abs(x + width - info.Work.Right) < 22) x = info.Work.Right - width;
                if (Math.Abs(y + height - info.Work.Bottom) < 22) y = info.Work.Bottom - height;
            }
            if (x != rect.Left || y != rect.Top) SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, 0x15);
        }

        private IntPtr WindowMessage(IntPtr hwnd, int msg, IntPtr wparam, IntPtr lparam, ref bool handled)
        {
            if (msg == 0x007E || msg == 0x001A || msg == 0x02E0)
                window.Dispatcher.BeginInvoke(new Action(delegate { ClampToScreen(false); }), DispatcherPriority.Loaded);
            return IntPtr.Zero;
        }

        private static System.Drawing.Icon CreateTrayIcon()
        {
            using (var bitmap = new System.Drawing.Bitmap(32, 32))
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            using (var down = new System.Drawing.Pen(System.Drawing.Color.FromArgb(128, 200, 255), 3))
            using (var up = new System.Drawing.Pen(System.Drawing.Color.FromArgb(182, 162, 255), 3))
            {
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.Clear(System.Drawing.Color.FromArgb(29, 36, 51));
                graphics.DrawLine(down, 10, 7, 10, 25);
                graphics.DrawLines(down, new[] { new System.Drawing.Point(5, 19), new System.Drawing.Point(10, 25), new System.Drawing.Point(15, 19) });
                graphics.DrawLine(up, 23, 7, 23, 25);
                graphics.DrawLines(up, new[] { new System.Drawing.Point(18, 13), new System.Drawing.Point(23, 7), new System.Drawing.Point(28, 13) });
                IntPtr handle = bitmap.GetHicon();
                try { return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(handle).Clone(); }
                finally { DestroyIcon(handle); }
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            timer.Stop();
            tray.Visible = false;
            tray.ContextMenuStrip.Dispose();
            tray.Dispose();
            trayIcon.Dispose();
        }
    }
}
