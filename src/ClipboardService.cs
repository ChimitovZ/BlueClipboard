using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BlueClipboard
{
    internal static class Images
    {
        public static byte[] Encode(BitmapSource source)
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
            using (var stream = new MemoryStream()) { encoder.Save(stream); return stream.ToArray(); }
        }
        public static BitmapSource Decode(byte[] bytes)
        {
            using (var stream = new MemoryStream(bytes))
            {
                var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
            }
        }
        public static byte[] Thumb(BitmapSource source)
        {
            double scale = Math.Min(1, 320.0 / Math.Max(source.PixelWidth, source.PixelHeight));
            var small = new TransformedBitmap(source, new ScaleTransform(scale, scale)); small.Freeze(); return Encode(small);
        }
    }
    internal sealed class ClipboardService
    {
        private readonly Dispatcher dispatcher;
        private readonly Store store;
        private readonly Action<string> notice;
        private readonly Action changed;
        private readonly DispatcherTimer retry;
        private int attempts;
        private uint ignoredSequence, capturedSequence;
        private Task pending = Task.FromResult(0);
        public bool Paused;
        public int MaxItems = 20000;
        public long MaxBytes = 2L * 1024 * 1024 * 1024;
        public ClipboardService(Store store, Dispatcher dispatcher, Action changed, Action<string> notice)
        {
            this.store = store; this.dispatcher = dispatcher; this.changed = changed; this.notice = notice;
            retry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(65) };
            retry.Tick += delegate { retry.Stop(); Capture(); };
        }
        public Task Pending { get { return pending; } }
        public void OnUpdate() { attempts = 0; retry.Stop(); Capture(); }
        private void Capture()
        {
            uint sequence = Native.GetClipboardSequenceNumber();
            if (Paused || sequence == ignoredSequence || sequence == capturedSequence) return;
            try
            {
                IDataObject obj = Clipboard.GetDataObject();
                if (obj == null) return;
                if (obj.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing")) return;
                if (obj.GetDataPresent("CanIncludeInClipboardHistory"))
                {
                    var flag = obj.GetData("CanIncludeInClipboardHistory") as MemoryStream;
                    if (flag != null && flag.Length >= 4 && BitConverter.ToInt32(flag.ToArray(), 0) == 0) return;
                }
                BitmapSource image = null; string text = null;
                if (obj.GetDataPresent("PNG"))
                {
                    var png = obj.GetData("PNG") as MemoryStream;
                    if (png != null && png.Length <= 64 * 1024 * 1024) { try { image = Images.Decode(png.ToArray()); } catch (Exception) { /* Try standard bitmap below. */ } }
                }
                if (image == null && Clipboard.ContainsImage()) image = Clipboard.GetImage();
                if (image == null && Clipboard.ContainsText(TextDataFormat.UnicodeText)) text = Clipboard.GetText(TextDataFormat.UnicodeText);
                if (image == null && String.IsNullOrEmpty(text)) return;
                if (image != null && (long)image.PixelWidth * image.PixelHeight > 40000000) { notice("Изображение больше 40 Мп — запись пропущена."); capturedSequence = sequence; return; }
                if (text != null && text.Length > 8 * 1024 * 1024) { notice("Текст больше 8 млн символов — запись пропущена."); capturedSequence = sequence; return; }
                if (image != null) { image = image.Clone(); image.Freeze(); }
                // Do not combine formats from different clipboard generations.
                if (sequence != Native.GetClipboardSequenceNumber()) { ScheduleRetry(); return; }
                string source = Native.SourceName(); long created = DateTime.UtcNow.Ticks;
                BitmapSource capturedImage = image; string capturedText = text;
                int limit = MaxItems; long byteLimit = MaxBytes;
                capturedSequence = sequence;
                pending = pending.ContinueWith(delegate
                {
                    try
                    {
                        var clip = new Clip { Kind = capturedImage == null ? "text" : "image", Text = capturedText ?? "", Source = source, Created = created };
                        if (capturedImage != null)
                        {
                            clip.Data = Images.Encode(capturedImage); clip.Thumbnail = Images.Thumb(capturedImage); clip.Width = capturedImage.PixelWidth; clip.Height = capturedImage.PixelHeight;
                            clip.Bytes = clip.Data.Length + clip.Thumbnail.Length;
                        }
                        else clip.Bytes = System.Text.Encoding.UTF8.GetByteCount(clip.Text);
                        store.Add(clip, limit, byteLimit);
                        dispatcher.BeginInvoke(changed);
                    }
                    catch (Exception ex) { dispatcher.BeginInvoke(new Action(delegate { notice("Не удалось сохранить запись: " + ex.Message); })); }
                }, TaskScheduler.Default);
            }
            catch (ExternalException) { ScheduleRetry(); }
            catch (Exception ex) { notice("Не удалось прочитать буфер: " + ex.Message); }
        }
        private void ScheduleRetry()
        {
            if (++attempts <= 10) retry.Start(); else notice("Буфер занят другим приложением. Текущая запись пропущена.");
        }
        public async Task Put(Clip clip)
        {
            if (clip == null) throw new InvalidOperationException("Запись уже удалена.");
            var data = new DataObject();
            if (clip.Kind == "image")
            {
                var image = Images.Decode(clip.Data);
                data.SetImage(image);
                data.SetData("PNG", new MemoryStream(clip.Data));
            }
            else data.SetText(clip.Text, TextDataFormat.UnicodeText);
            for (int attempt = 0; ; attempt++)
            {
                try { Clipboard.SetDataObject(data, true); ignoredSequence = Native.GetClipboardSequenceNumber(); return; }
                catch (ExternalException) { if (attempt >= 7) throw; }
                await Task.Delay(60);
            }
        }
        public void Stop() { Paused = true; retry.Stop(); }
    }
}
