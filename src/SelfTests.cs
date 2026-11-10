using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BlueClipboard
{
    internal static class SelfTests
    {
        private static int assertions;
        private static void Check(bool success, string label) { if (!success) throw new Exception("FAIL: " + label); assertions++; }
        private static Clip Text(string text, long tick) { return new Clip { Kind = "text", Text = text, Source = "Проверка", Created = tick, Bytes = System.Text.Encoding.UTF8.GetByteCount(text) }; }
        public static int Run()
        {
            string folder = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "artifacts"));
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "self-test-" + Guid.NewGuid().ToString("N") + ".db");
            string report = Path.Combine(folder, "self-test.txt");
            var timer = Stopwatch.StartNew();
            try
            {
                using (var store = new Store(path))
                {
                    Check(store.Count() == 0, "empty database");
                    long a = store.Add(Text("Привет, мир!\nСтрока №2 🚀", 1), 0, 0);
                    long b = store.Add(Text("SELECT '100%_'; -- literal", 2), 0, 0);
                    Check(store.Get(a).Text == "Привет, мир!\nСтрока №2 🚀", "Unicode, newline and emoji roundtrip");
                    Check(store.Search("ПРИВЕТ", "all", 100, 0).Count == 1, "Cyrillic case-insensitive search");
                    Check(store.Search("%_", "all", 100, 0).Count == 1, "literal wildcard search");
                    Check(store.Search("' OR 1=1 --", "all", 100, 0).Count == 0, "parameterized query");
                    Check(store.Add(Text("Привет, мир!\nСтрока №2 🚀", 3), 0, 0) == a && store.Count() == 2, "deduplication");
                    store.Pin(b, true);
                    Check(store.Search("", "all", 100, 0)[0].Id == b, "pinned ordering");
                    Check(store.Search("", "pinned", 100, 0).Count == 1, "pinned filter");
                    byte[] pixels = { 20, 90, 220, 255, 255, 255, 255, 255, 90, 20, 60, 255, 0, 0, 0, 0 };
                    var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8); image.Freeze();
                    byte[] png = Images.Encode(image), thumb = Images.Thumb(image);
                    long im = store.Add(new Clip { Kind = "image", Text = "", Source = "Paint", Created = 4, Width = 2, Height = 2, Data = png, Thumbnail = thumb, Bytes = png.Length + thumb.Length }, 0, 0);
                    Check(store.Get(im).Data.SequenceEqual(png), "lossless PNG storage");
                    Check(Images.Decode(store.Get(im).Data).PixelWidth == 2, "image decoding");
                    Check(store.Search("", "image", 100, 0).Count == 1, "image filter");
                    Check(store.Search("", "text", 100, 0).Count == 2, "text filter");
                    Check(store.Search("paint", "all", 100, 0).Count == 1, "source search");
                    Check(store.Search("", "all", 1, 1).Count == 1, "pagination");
                    string longText = new string('Ж', 30000) + "конец";
                    long big = store.Add(Text(longText, 5), 0, 0);
                    Check(store.Search("конец", "all", 100, 0).Count == 1, "search beyond displayed preview");
                    Check(store.Get(big).Text == longText, "full text retained");
                    store.Trim(2, 0);
                    Check(store.Count() == 2 && store.Get(b) != null && store.Get(big) != null, "count eviction preserves pins and recent entries");
                    store.Trim(0, 1);
                    Check(store.Count() == 1 && store.Get(b) != null, "byte eviction preserves pins above limit");
                    store.Set("test", "тест");
                }
                using (var store = new Store(path))
                {
                    Check(store.Count() == 1 && store.GetSetting("test", "") == "тест", "restart persistence");
                    Check(store.GetSetting("missing", "fallback") == "fallback", "settings defaults");
                    store.Add(Text("delete me", 20), 0, 0); store.ClearUnpinned();
                    Check(store.Count() == 1, "clear preserves pins");
                    store.Delete(store.Search("", "all", 1, 0)[0].Id);
                    Check(store.Count() == 0, "delete");
                    Parallel.For(0, 60, i => store.Add(Text("Parallel " + i, 100 + i), 0, 0));
                    Check(store.Count() == 60, "concurrent writers serialized");
                    Check(store.Search("parallel", "all", 100, 0).Count == 60, "concurrent writes readable");
                }
                Check(Marshal.SizeOf(typeof(Native.Input)) == 40, "x64 SendInput layout");
                using (var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml"))
                {
                    var window = (Window)XamlReader.Load(stream);
                    foreach (string name in new[] { "ClipList", "SearchBox", "PasteButton", "SettingsPanel", "SizeSetting", "DetailImage" }) Check(window.FindName(name) != null, "XAML control " + name);
                    window.Measure(new Size(800, 660)); window.Arrange(new Rect(0, 0, 800, 660)); window.UpdateLayout();
                    var render = new RenderTargetBitmap(800, 660, 96, 96, PixelFormats.Pbgra32); render.Render(window);
                    File.WriteAllBytes(Path.Combine(folder, "empty-window.png"), Images.Encode(render));
                }
                File.WriteAllText(report, "PASS: " + assertions + " assertions\r\nElapsed: " + timer.ElapsedMilliseconds + " ms\r\nStorage, persistence, Unicode search, deduplication, limits, pinning, PNG, concurrency, native input layout, XAML layout.\r\n", System.Text.Encoding.UTF8);
                return 0;
            }
            catch (Exception ex) { File.WriteAllText(report, ex.ToString()); return 1; }
            finally
            {
                foreach (string suffix in new[] { "", "-wal", "-shm" }) { try { File.Delete(path + suffix); } catch { } }
            }
        }
        public static void Seed(Store store)
        {
            long now = DateTime.UtcNow.Ticks;
            store.Add(Text("Хорошие идеи не должны теряться.\n\nBlueClipboard сохраняет текст и изображения, чтобы вы могли вернуться к ним в любой момент.", now - TimeSpan.FromMinutes(5).Ticks), 0, 0);
            store.Add(new Clip { Kind = "text", Text = "Цвет интерфейса\n#0078D4 · Windows blue", Source = "Figma", Created = now - TimeSpan.FromMinutes(12).Ticks, Bytes = 60 }, 0, 0);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(226, 239, 251)), null, new Rect(0, 0, 640, 360));
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0, 120, 212)), null, new Rect(260, 0, 380, 360));
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(133, 201, 249)), null, new Point(290, 180), 130, 130);
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(12, 74, 133)), null, new Rect(420, 160, 220, 200));
            }
            var bitmap = new RenderTargetBitmap(640, 360, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze();
            byte[] data = Images.Encode(bitmap), thumb = Images.Thumb(bitmap);
            store.Add(new Clip { Kind = "image", Text = "", Source = "Paint", Created = now - TimeSpan.FromMinutes(8).Ticks, Width = 640, Height = 360, Data = data, Thumbnail = thumb, Bytes = data.Length + thumb.Length }, 0, 0);
            long pinned = store.Add(new Clip { Kind = "text", Text = "План на сегодня\n\n01  Собрать идеи\n02  Сохранить важное\n03  Сделать красиво", Source = "Блокнот", Created = now - TimeSpan.FromHours(1).Ticks, Bytes = 150 }, 0, 0);
            store.Pin(pinned, true);
            store.Add(new Clip { Kind = "text", Text = "https://learn.microsoft.com/windows/", Source = "Microsoft Edge", Created = now - TimeSpan.FromMinutes(25).Ticks, Bytes = 42 }, 0, 0);
        }
    }
}
