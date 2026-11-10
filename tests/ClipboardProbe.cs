using System;
using System.Drawing;
using System.Windows.Forms;

// An isolated target for manual/UI integration tests. It never reads clipboard on startup.
internal class ClipboardProbe : Form
{
    private readonly TextBox editor = new TextBox { Multiline = true, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 14), AccessibleName = "Поле для проверки вставки" };
    private readonly Label result = new Label { Dock = DockStyle.Bottom, Height = 38, Text = "Готово к проверке", AccessibleName = "Результат проверки" };
    [STAThread] static void Main() { Application.EnableVisualStyles(); Application.Run(new ClipboardProbe()); }
    ClipboardProbe()
    {
        Text = "BlueClipboard — проверка вставки"; Width = 640; Height = 400;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 45 };
        var copyText = new Button { Text = "Копировать тестовый текст", Width = 200 };
        copyText.Click += delegate { Clipboard.SetText("BlueClipboard: проверка вставки — Привет, мир! 🚀"); result.Text = "Текст скопирован"; editor.Focus(); };
        var copyImage = new Button { Text = "Копировать изображение", Width = 190 };
        copyImage.Click += delegate
        {
            using (var image = new Bitmap(320, 180)) { using (var g = Graphics.FromImage(image)) { g.Clear(Color.FromArgb(0,120,212)); g.FillEllipse(Brushes.White, 100,30,120,120); } Clipboard.SetImage(image); }
            result.Text = "Изображение 320 × 180 скопировано"; editor.Focus();
        };
        var inspect = new Button { Text = "Проверить изображение", Width = 190 };
        inspect.Click += delegate { using (var image = Clipboard.GetImage()) result.Text = image == null ? "FAIL: изображения нет" : "PASS: изображение " + image.Width + " × " + image.Height; };
        buttons.Controls.AddRange(new Control[] { copyText, copyImage, inspect }); buttons.Height = 76;
        editor.TextChanged += delegate { result.Text = editor.Text.Contains("BlueClipboard: проверка вставки") ? "PASS: текст вставлен в исходное поле" : "Готово к проверке"; };
        Controls.Add(editor); Controls.Add(result); Controls.Add(buttons);
    }
}
