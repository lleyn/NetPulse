using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class VisualTests
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            new Application();
            var assembly = Assembly.LoadFrom(args[0]);
            Window window;
            using (var resource = assembly.GetManifestResourceStream("NetPulse.Widget.xaml")) window = (Window)XamlReader.Load(resource);
            // Render the actual XAML without opening or controlling a desktop window.
            var card = (Border)window.Content;
            var expanded = (Grid)window.FindName("ExpandedPanel");
            var compact = (Grid)window.FindName("CompactPanel");
            var graph = (FrameworkElement)Activator.CreateInstance(assembly.GetType("NetPulse.HistoryGraph"), true);
            ((Grid)window.FindName("GraphHost")).Children.Add(graph);
            var sampleType = assembly.GetType("NetPulse.Sample");
            for (int i = 0; i < 60; i++)
            {
                var sample = Activator.CreateInstance(sampleType, true);
                sampleType.GetField("Down").SetValue(sample, 2000000 + 1500000 * Math.Sin(i * 0.43) + 600000 * Math.Sin(i * 1.32));
                sampleType.GetField("Up").SetValue(sample, 600000 + 350000 * Math.Cos(i * 0.36));
                graph.GetType().GetMethod("Add").Invoke(graph, new[] { sample });
            }
            ((TextBlock)window.FindName("DownValue")).Text = "12,4";
            ((TextBlock)window.FindName("DownUnit")).Text = "МБ/с";
            ((TextBlock)window.FindName("UpValue")).Text = "840";
            ((TextBlock)window.FindName("UpUnit")).Text = "КБ/с";
            ((TextBlock)window.FindName("AdapterLabel")).Text = "Демонстрация · Wi-Fi";
            ((TextBlock)window.FindName("CompactDown")).Text = "↓ 12,4 МБ/с";
            ((TextBlock)window.FindName("CompactUp")).Text = "↑ 840 КБ/с";
            card.Resources = window.Resources;
            TextElement.SetFontFamily(card, window.FontFamily);
            TextElement.SetForeground(card, window.Foreground);
            window.Content = null;
            Render(card, 300, 192, Path.Combine(args[1], "preview-expanded.png"));
            foreach (string name in new[] { "DownValue", "DownUnit", "UpValue", "UpUnit", "AdapterLabel" })
            {
                var text = (TextBlock)window.FindName(name);
                if (text.ActualWidth <= 0 || text.ActualHeight <= 0) throw new Exception(name + " did not lay out");
                var point = text.TranslatePoint(new Point(0, 0), card);
                if (point.X + text.ActualWidth > 292 || point.Y + text.ActualHeight > 184) throw new Exception(name + " exceeds the card");
            }
            expanded.Visibility = Visibility.Collapsed;
            compact.Visibility = Visibility.Visible;
            ((Button)window.FindName("CompactButton")).Content = "+";
            Render(card, 300, 100, Path.Combine(args[1], "preview-compact.png"));
            Console.WriteLine("PASS: actual XAML loaded; expanded and compact views rendered; key labels fit the card.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); return 1; }
    }

    private static void Render(FrameworkElement root, int width, int height, string path)
    {
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width * 2, height * 2, 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(path)) encoder.Save(file);
    }
}
