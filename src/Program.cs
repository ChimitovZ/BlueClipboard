using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace BlueClipboard
{
    internal static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Contains("--self-test")) return SelfTests.Run();
            bool smoke = args.Contains("--smoke-test");
            bool demo = args.Contains("--demo") || smoke;
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlueClipboard");
            int dirArg = Array.IndexOf(args, "--data-dir");
            if (dirArg >= 0 && dirArg + 1 < args.Length) folder = Path.GetFullPath(args[dirArg + 1]);
            if (demo) folder = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "artifacts", "demo"));
            bool created;
            using (var mutex = new Mutex(true, demo ? "Local\\BlueClipboard.Demo" : "Local\\BlueClipboard.Desktop", out created))
            {
                if (!created)
                {
                    IntPtr existing = Native.FindWindow(null, demo ? "BlueClipboard · Демо" : "BlueClipboard");
                    if (existing != IntPtr.Zero) Native.PostMessage(existing, Native.OpenMessage, IntPtr.Zero, IntPtr.Zero);
                    return 0;
                }
                try
                {
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
                    using (var store = new Store(Path.Combine(folder, "history.db")))
                    {
                        if (demo && store.Count() == 0) SelfTests.Seed(store);
                        var controller = new Controller(app, store, folder, demo);
                        app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e) { controller.Notice("Ошибка: " + e.Exception.Message); e.Handled = true; if (smoke) { File.WriteAllText(Path.Combine(folder, "smoke.txt"), e.Exception.ToString()); app.Shutdown(1); } };
                        controller.Start(args.Contains("--background"));
                        if (smoke)
                        {
                            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                            timer.Tick += async delegate
                            {
                                timer.Stop();
                                var list = (ListBox)app.MainWindow.FindName("ClipList");
                                var detail = (TextBox)app.MainWindow.FindName("DetailText");
                                bool ok = list.Items.Count >= 4 && detail.Text.Length > 0;
                                app.MainWindow.UpdateLayout();
                                var render = new System.Windows.Media.Imaging.RenderTargetBitmap((int)app.MainWindow.ActualWidth, (int)app.MainWindow.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                                render.Render(app.MainWindow);
                                File.WriteAllBytes(Path.Combine(folder, "preview.png"), Images.Encode(render));
                                var window = app.MainWindow;
                                var resize = (Button)window.FindName("RestoreButton");
                                var bounds = window.RestoreBounds;
                                resize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                                bool maximized = window.WindowState == WindowState.Maximized;
                                window.Hide();
                                Native.PostMessage(new WindowInteropHelper(window).Handle, Native.OpenMessage, IntPtr.Zero, IntPtr.Zero);
                                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                                maximized &= window.IsVisible && window.WindowState == WindowState.Maximized;
                                resize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                                bool restored = window.WindowState == WindowState.Normal && window.RestoreBounds == bounds;
                                bool pixelScroll = VirtualizingPanel.GetScrollUnit(list) == ScrollUnit.Pixel && VirtualizingPanel.GetIsVirtualizing(list);
                                File.WriteAllText(Path.Combine(folder, "smoke.txt"),
                                    ok && maximized && restored && pixelScroll
                                    ? "PASS: startup, history, preview, virtualized pixel scrolling, maximize, reopen and restore."
                                    : String.Format("FAIL: history/preview={0}, maximize/reopen={1}, restore={2}, pixel scrolling={3}.", ok, maximized, restored, pixelScroll));
                                await controller.Quit();
                            };
                            timer.Start();
                        }
                        int result = app.Run();
                        if (result != 0) return result;
                    }
                    return 0;
                }
                catch (Exception ex) { MessageBox.Show("Не удалось запустить BlueClipboard.\n\n" + ex.Message, "BlueClipboard", MessageBoxButton.OK, MessageBoxImage.Error); return 1; }
            }
        }
    }

    internal sealed class Controller
    {
        private readonly Application app;
        private readonly Store store;
        private readonly string folder;
        private readonly bool demo;
        private readonly Window window;
        private readonly ClipboardService clipboard;
        private HwndSource source;
        private IntPtr handle, target;
        private IntPtr winVHook;
        private readonly Native.KeyboardHook winVCallback;
        private bool winVPressed;
        private Forms.NotifyIcon tray;
        private System.Drawing.Icon trayIcon;
        private readonly DispatcherTimer updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        private bool checkingUpdate;
        private bool exiting, busy, modal, loading, captureEnabled;
        private int requestId, previewId;
        private string filter = "all";
        private readonly ObservableCollection<Clip> items = new ObservableCollection<Clip>();
        private readonly DispatcherTimer searchTimer, statusTimer;
        private T Ui<T>(string name) where T : FrameworkElement { return (T)window.FindName(name); }
        public Controller(Application app, Store store, string folder, bool demo)
        {
            this.app = app; this.store = store; this.folder = folder; this.demo = demo;
            winVCallback = HandleWinV;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml")) window = (Window)XamlReader.Load(stream);
            if (demo) window.Title = "BlueClipboard · Демо";
            app.MainWindow = window;
            clipboard = new ClipboardService(store, window.Dispatcher, delegate { if (window.IsVisible) Refresh(false); }, Notice);
            int limit; long megabytes;
            clipboard.MaxItems = Int32.TryParse(store.GetSetting("maxItems", "20000"), out limit) && limit >= 0 ? limit : 20000;
            clipboard.MaxBytes = Int64.TryParse(store.GetSetting("maxMB", "2048"), out megabytes) && megabytes >= 0 && megabytes <= 1048576 ? megabytes * 1048576L : 2147483648L;
            searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
            searchTimer.Tick += delegate { searchTimer.Stop(); Refresh(true); };
            statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
            statusTimer.Tick += delegate { statusTimer.Stop(); DefaultStatus(); };
            Wire();
            Ui<Button>("RestoreButton").Click += delegate { ToggleMaximized(); };
            window.StateChanged += delegate
            {
                bool maximized = window.WindowState == WindowState.Maximized;
                var button = Ui<Button>("RestoreButton");
                button.Content = maximized ? "\uE923" : "\uE922";
                button.ToolTip = maximized ? "Восстановить размер" : "Развернуть";
                System.Windows.Automation.AutomationProperties.SetName(button, (string)button.ToolTip);
            };
            Ui<Button>("UpdateButton").Click += async delegate { await CheckUpdates(false); };
            Ui<TextBlock>("VersionLabel").Text = "Версия " + AppInfo.VersionText;
        }
        public void Start(bool background)
        {
            DefaultStatus();
            handle = new WindowInteropHelper(window).EnsureHandle();
            source = HwndSource.FromHwnd(handle); source.AddHook(Messages);
            if (!demo)
            {
                captureEnabled = Native.AddClipboardFormatListener(handle);
                DefaultStatus();
                if (!captureEnabled) Notice("Не удалось подключить наблюдение за буфером. Перезапустите приложение.");
                if (!Native.RegisterHotKey(handle, 1, 0x4006, 0x56)) Notice("Ctrl+Shift+V занято другой программой. Панель доступна из трея.");
                CreateTray(); clipboard.OnUpdate();
                if (store.GetSetting("winV", "false") == "true" && !SetWinV(true))
                    Notice("Не удалось включить Win+V. Панель доступна через Ctrl+Shift+V и из трея.");
                if (AppInfo.IsInstalled) { updateTimer.Tick += async delegate { updateTimer.Interval = TimeSpan.FromHours(12); if (!window.IsVisible && store.GetSetting("autoUpdate", "true") == "true") await CheckUpdates(true); }; updateTimer.Start(); }
            }
            if (!background) ShowPanel(IntPtr.Zero);
            UpdateActions();
        }
        private void Wire()
        {
            Ui<ListBox>("ClipList").ItemsSource = items;
            Ui<ListBox>("ClipList").SelectionChanged += delegate { Preview(); };
            Ui<ListBox>("ClipList").MouseDoubleClick += async delegate(object sender, MouseButtonEventArgs e)
            {
                if (ItemsControl.ContainerFromElement(Ui<ListBox>("ClipList"), e.OriginalSource as DependencyObject) is ListBoxItem) await Use(true);
            };
            Ui<TextBox>("SearchBox").TextChanged += delegate { Ui<TextBlock>("SearchHint").Visibility = Ui<TextBox>("SearchBox").Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; searchTimer.Stop(); searchTimer.Start(); };
            WireFilter("AllFilter", "all"); WireFilter("TextFilter", "text"); WireFilter("ImageFilter", "image"); WireFilter("PinnedFilter", "pinned");
            Ui<Button>("CloseButton").Click += delegate { window.Hide(); };
            Ui<Button>("PauseButton").Click += delegate { Pause(); };
            Ui<Button>("PasteButton").Click += async delegate { await Use(true); };
            Ui<Button>("CopyButton").Click += async delegate { await Use(false); };
            Ui<Button>("PinButton").Click += async delegate { Clip clip = Selected; if (clip == null) return; await Task.Run(delegate { store.Pin(clip.Id, !clip.Pinned); }); Refresh(false); };
            Ui<Button>("DeleteButton").Click += async delegate
            {
                Clip clip = Selected; if (clip == null) return;
                if (clip.Pinned && !Confirm("Удалить закреплённую запись?")) return;
                await Task.Run(delegate { store.Delete(clip.Id); }); Refresh(false);
            };
            Ui<Button>("MoreButton").Click += delegate { Refresh(false, true); };
            Ui<Button>("SettingsButton").Click += delegate { OpenSettings(); };
            Ui<Button>("BackButton").Click += delegate { Ui<Border>("SettingsPanel").Visibility = Visibility.Collapsed; Ui<TextBox>("SearchBox").Focus(); };
            Ui<Button>("SaveSettingsButton").Click += async delegate { await SaveSettings(); };
            Ui<Button>("FolderButton").Click += delegate { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true }); };
            Ui<Button>("ClearButton").Click += async delegate
            {
                if (!Confirm("Удалить все незакреплённые записи из истории? Это действие нельзя отменить.")) return;
                Ui<Button>("ClearButton").IsEnabled = false;
                try { await clipboard.Pending; await Task.Run(delegate { store.ClearUnpinned(); }); Refresh(true); Notice("Незакреплённые записи удалены."); }
                finally { Ui<Button>("ClearButton").IsEnabled = true; }
            };
            Ui<Border>("TitleBar").MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                if (!(e.OriginalSource is TextBlock || e.OriginalSource is Border)) return;
                if (e.ClickCount == 2) ToggleMaximized();
                else if (e.ButtonState == MouseButtonState.Pressed && window.WindowState == WindowState.Normal) window.DragMove();
            };
            window.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) { if (!exiting) { e.Cancel = true; window.Hide(); } };
            window.Deactivated += delegate { if (!modal && !busy && !demo && Ui<Border>("SettingsPanel").Visibility != Visibility.Visible) window.Hide(); };
            window.PreviewKeyDown += async delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) { e.Handled = true; if (Ui<Border>("SettingsPanel").IsVisible) Ui<Border>("SettingsPanel").Visibility = Visibility.Collapsed; else window.Hide(); }
                else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { Ui<TextBox>("SearchBox").Focus(); Ui<TextBox>("SearchBox").SelectAll(); e.Handled = true; }
                else if (e.Key == Key.C && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) { e.Handled = true; await Use(false); }
                else if (Ui<Border>("SettingsPanel").Visibility != Visibility.Visible && (Ui<TextBox>("SearchBox").IsKeyboardFocused || Ui<ListBox>("ClipList").IsKeyboardFocusWithin))
                {
                    if (e.Key == Key.Enter) { e.Handled = true; await Use(true); }
                    else if ((e.Key == Key.Down || e.Key == Key.Up) && items.Count > 0)
                    {
                        e.Handled = true; var list = Ui<ListBox>("ClipList"); list.SelectedIndex = Math.Max(0, Math.Min(items.Count - 1, list.SelectedIndex + (e.Key == Key.Down ? 1 : -1))); list.ScrollIntoView(list.SelectedItem);
                    }
                }
            };
        }
        private void WireFilter(string name, string value) { Ui<RadioButton>(name).Checked += delegate { filter = value; Refresh(true); }; }
        private Clip Selected { get { return Ui<ListBox>("ClipList").SelectedItem as Clip; } }
        private IntPtr Messages(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == 0x8002 && !demo) { window.Dispatcher.BeginInvoke(new Action(async delegate { await Quit(); })); handled = true; }
            else if (msg == 0x031D) { clipboard.OnUpdate(); handled = true; }
            else if (msg == 0x0312 || msg == Native.OpenMessage)
            {
                IntPtr previous = msg == Native.OpenMessage && w != IntPtr.Zero ? w : Native.GetForegroundWindow();
                if (window.IsVisible && window.IsActive) window.Hide(); else ShowPanel(previous);
                handled = true;
            }
            return IntPtr.Zero;
        }
        private void ShowPanel(IntPtr previous)
        {
            uint pid; Native.GetWindowThreadProcessId(previous, out pid);
            target = previous != handle && pid != (uint)Process.GetCurrentProcess().Id ? previous : IntPtr.Zero;
            Ui<TextBlock>("TargetLabel").Text = target == IntPtr.Zero ? "Скопируйте запись и вставьте через Ctrl+V" : "Вставка в приложение, из которого открыта панель";
            Ui<Border>("SettingsPanel").Visibility = Visibility.Collapsed;
            Ui<TextBox>("SearchBox").Text = "";
            Ui<RadioButton>("AllFilter").IsChecked = true; filter = "all";
            window.Show();
            if (window.WindowState == WindowState.Minimized) Native.ShowWindow(handle, 9); // SW_RESTORE
            window.Activate(); Native.SetForegroundWindow(handle);
            Ui<TextBox>("SearchBox").Focus(); Refresh(true); UpdateActions();
        }
        private void ToggleMaximized()
        {
            window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }
        private bool SetWinV(bool enabled)
        {
            if (enabled && winVHook == IntPtr.Zero)
                winVHook = Native.SetWindowsHookEx(13, winVCallback, Native.GetModuleHandle(null), 0); // WH_KEYBOARD_LL
            else if (!enabled && winVHook != IntPtr.Zero && Native.UnhookWindowsHookEx(winVHook))
            {
                winVHook = IntPtr.Zero;
                winVPressed = false;
            }
            bool active = winVHook != IntPtr.Zero;
            Ui<TextBlock>("HotkeyLabel").Text = active ? "Win + V" : "Ctrl + Shift + V";
            if (tray != null) tray.Text = active ? "BlueClipboard · Win+V / Ctrl+Shift+V" : "BlueClipboard · Ctrl+Shift+V";
            return active == enabled;
        }
        private IntPtr HandleWinV(int code, IntPtr message, IntPtr data)
        {
            if (code == 0 && Marshal.ReadInt32(data) == 0x56) // KBDLLHOOKSTRUCT.vkCode
            {
                bool down = message.ToInt64() == 0x0100 || message.ToInt64() == 0x0104;
                if (winVPressed)
                {
                    winVPressed = down;
                    return new IntPtr(1);
                }
                if (down && (Native.Down(0x5B) || Native.Down(0x5C)) &&
                    !Native.Down(0x10) && !Native.Down(0x11) && !Native.Down(0x12) && !Native.Down(0x56))
                {
                    if (Native.PostMessage(handle, Native.OpenMessage, Native.GetForegroundWindow(), IntPtr.Zero))
                    {
                        winVPressed = true;
                        // Mark Win as part of a chord so releasing it does not open Start.
                        Native.SendInput(2, new[] { Native.Key(0xFF, false), Native.Key(0xFF, true) }, Marshal.SizeOf(typeof(Native.Input)));
                        return new IntPtr(1);
                    }
                }
            }
            return Native.CallNextHookEx(winVHook, code, message, data);
        }
        private async void Refresh(bool reset, bool more = false)
        {
            if (exiting || (more && loading)) return;
            int request = ++requestId; loading = true;
            long selectedId = reset || Selected == null ? 0 : Selected.Id;
            string query = Ui<TextBox>("SearchBox").Text, selectedFilter = filter;
            int offset = more ? items.Count : 0;
            try
            {
                var result = await Task.Run(delegate { return store.Search(query, selectedFilter, 101, offset); });
                long count = await Task.Run(delegate { return store.Count(); });
                if (request != requestId || exiting) return;
                if (!more) items.Clear();
                foreach (Clip c in result.Take(100)) items.Add(c);
                Ui<Button>("MoreButton").Visibility = result.Count > 100 ? Visibility.Visible : Visibility.Collapsed;
                Ui<TextBlock>("CountLabel").Text = count.ToString("N0") + " записей";
                Ui<StackPanel>("EmptyState").Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                bool noHistory = count == 0 && query.Length == 0 && filter == "all";
                Ui<TextBlock>("EmptyTitle").Text = noHistory ? "История начинается с Ctrl+C" : "Здесь пока ничего нет";
                Ui<TextBlock>("EmptyBody").Text = noHistory ? "Скопируйте текст или изображение. Оно появится здесь и останется после перезапуска." : "Попробуйте другой запрос или фильтр. Закреплённые записи всегда будут под рукой.";
                if (!more) Ui<ListBox>("ClipList").SelectedItem = items.FirstOrDefault(c => c.Id == selectedId) ?? items.FirstOrDefault();
            }
            catch (Exception ex) { Notice("Не удалось прочитать историю: " + ex.Message); }
            finally { if (request == requestId) loading = false; UpdateActions(); }
        }
        private async void Preview()
        {
            int request = ++previewId; Clip clip = Selected; UpdateActions();
            Ui<TextBox>("DetailText").Text = ""; Ui<System.Windows.Controls.Image>("DetailImage").Source = null;
            Ui<System.Windows.Controls.Image>("DetailImage").Visibility = Visibility.Collapsed;
            Ui<TextBlock>("DetailPlaceholder").Visibility = clip == null ? Visibility.Visible : Visibility.Collapsed;
            Ui<TextBlock>("DetailMeta").Text = clip == null ? "Выберите запись слева" : clip.Details + "\n" + clip.Meta;
            if (clip == null) return;
            try
            {
                Clip full = await Task.Run(delegate { return store.Get(clip.Id); });
                if (request != previewId || full == null) return;
                if (full.Kind == "text") Ui<TextBox>("DetailText").Text = full.Text;
                else
                {
                    var image = await Task.Run(delegate { return Images.Decode(full.Data); });
                    if (request != previewId) return;
                    Ui<System.Windows.Controls.Image>("DetailImage").Source = image; Ui<System.Windows.Controls.Image>("DetailImage").Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex) { Notice("Не удалось открыть запись: " + ex.Message); }
        }
        private void UpdateActions()
        {
            bool enabled = Selected != null && !busy;
            foreach (string name in new[] { "PasteButton", "CopyButton", "PinButton", "DeleteButton" }) Ui<Button>(name).IsEnabled = enabled;
            Ui<Button>("PasteButton").Content = target == IntPtr.Zero ? "Копировать  ↵" : "Вставить  ↵";
            Ui<Button>("PinButton").Foreground = new SolidColorBrush(Selected != null && Selected.Pinned ? Color.FromRgb(0, 120, 212) : Color.FromRgb(90, 105, 125));
            Ui<Button>("PinButton").ToolTip = Selected != null && Selected.Pinned ? "Открепить" : "Закрепить";
        }
        private async Task Use(bool paste)
        {
            Clip clip = Selected; if (clip == null || busy) return;
            busy = true; UpdateActions();
            try
            {
                Clip full = await Task.Run(delegate { return store.Get(clip.Id); });
                await clipboard.Put(full);
                if (!paste || target == IntPtr.Zero) { Notice("Скопировано. Вставьте через Ctrl+V."); return; }
                IntPtr destination = target;
                if (!Native.IsWindow(destination)) { Notice("Приложение закрыто. Запись скопирована — используйте Ctrl+V."); return; }
                // Wait for physical shortcut/Enter keys to be released, without releasing user keys synthetically.
                for (int i = 0; i < 80 && Native.ModifiersDown(); i++) await Task.Delay(25);
                if (Native.ModifiersDown()) { Notice("Запись скопирована. Отпустите клавиши и нажмите Ctrl+V."); return; }
                window.Hide();
                if (Native.IsIconic(destination)) Native.ShowWindow(destination, 9);
                Native.SetForegroundWindow(destination);
                await Task.Delay(100);
                if (Native.GetForegroundWindow() != destination || !Native.Paste())
                {
                    window.Show(); window.Activate(); Notice("Не удалось вставить автоматически. Запись в буфере: используйте Ctrl+V в нужном приложении.");
                }
            }
            catch (Exception ex) { Notice("Не удалось скопировать: " + ex.Message); }
            finally { busy = false; UpdateActions(); }
        }
        public void Notice(string message) { Ui<TextBlock>("StatusLabel").Text = message; Ui<TextBlock>("StatusLabel").ToolTip = message; statusTimer.Stop(); statusTimer.Start(); }
        private void DefaultStatus()
        {
            Ui<TextBlock>("StatusLabel").Text = demo ? "●  Демо · тестовые записи, наблюдение выключено" : clipboard.Paused ? "Ⅱ  Запись на паузе" : !captureEnabled ? "Наблюдение недоступно — перезапустите приложение" : "●  Буфер под наблюдением · только на этом компьютере";
            Ui<TextBlock>("StatusLabel").ToolTip = null;
        }
        private void Pause()
        {
            clipboard.Paused = !clipboard.Paused;
            Ui<Button>("PauseButton").Content = clipboard.Paused ? "▶  Продолжить" : "Ⅱ  Пауза";
            if (tray != null) tray.Text = clipboard.Paused ? "BlueClipboard — запись на паузе" : "BlueClipboard — Ctrl+Shift+V";
            DefaultStatus();
        }
        private bool Confirm(string text)
        {
            modal = true;
            try { return MessageBox.Show(window, text, "BlueClipboard", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes; }
            finally { modal = false; }
        }
        private void OpenSettings()
        {
            Ui<CheckBox>("AutoUpdate").IsChecked = AppInfo.IsInstalled && store.GetSetting("autoUpdate", "true") == "true";
            Ui<CheckBox>("AutoUpdate").IsEnabled = AppInfo.IsInstalled && !demo;
            using (var key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run")) Ui<CheckBox>("StartupSetting").IsChecked = key != null && key.GetValue("BlueClipboard") != null;
            Ui<CheckBox>("StartupSetting").IsEnabled = !demo;
            Ui<CheckBox>("WinVSetting").IsChecked = winVHook != IntPtr.Zero;
            Ui<CheckBox>("WinVSetting").IsEnabled = !demo;
            SelectTag(Ui<ComboBox>("LimitSetting"), clipboard.MaxItems.ToString()); SelectTag(Ui<ComboBox>("SizeSetting"), (clipboard.MaxBytes / 1048576).ToString());
            Ui<Border>("SettingsPanel").Visibility = Visibility.Visible;
        }
        private static void SelectTag(ComboBox box, string tag) { foreach (ComboBoxItem item in box.Items) if ((string)item.Tag == tag) { box.SelectedItem = item; return; } }
        private async Task SaveSettings()
        {
            bool winV = Ui<CheckBox>("WinVSetting").IsChecked == true;
            int maxItems = Int32.Parse((string)((ComboBoxItem)Ui<ComboBox>("LimitSetting").SelectedItem).Tag);
            long maxMB = Int64.Parse((string)((ComboBoxItem)Ui<ComboBox>("SizeSetting").SelectedItem).Tag);
            if (((maxItems > 0 && (clipboard.MaxItems == 0 || maxItems < clipboard.MaxItems)) || (maxMB > 0 && (clipboard.MaxBytes == 0 || maxMB * 1048576 < clipboard.MaxBytes))) && !Confirm("Уменьшить лимит? Самые старые незакреплённые записи сверх нового лимита будут удалены.")) return;
            Ui<Button>("SaveSettingsButton").IsEnabled = false;
            try
            {
                if (!demo)
                {
                    using (var key = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run"))
                    {
                        if (Ui<CheckBox>("StartupSetting").IsChecked == true) key.SetValue("BlueClipboard", "\"" + Assembly.GetExecutingAssembly().Location + "\" --background --data-dir \"" + folder + "\"");
                        else key.DeleteValue("BlueClipboard", false);
                    }
                }
                store.Set("autoUpdate", Ui<CheckBox>("AutoUpdate").IsChecked == true ? "true" : "false");
                clipboard.MaxItems = maxItems; clipboard.MaxBytes = maxMB * 1048576;
                await clipboard.Pending;
                await Task.Run(delegate { store.Trim(maxItems, maxMB * 1048576); });
                if (!demo)
                {
                    if (!SetWinV(winV))
                    {
                        Notice(winV ? "Не удалось включить Win+V." : "Не удалось отключить Win+V.");
                        return;
                    }
                    store.Set("winV", winV ? "true" : "false");
                }
                Ui<Border>("SettingsPanel").Visibility = Visibility.Collapsed; Refresh(false); Notice("Настройки сохранены.");
            }
            catch (Exception ex) { Notice("Не удалось сохранить настройки: " + ex.Message); }
            finally { Ui<Button>("SaveSettingsButton").IsEnabled = true; }
        }
        private async Task CheckUpdates(bool automatic)
        {
            if (checkingUpdate || exiting || demo) return;
            checkingUpdate = true;
            var status = Ui<TextBlock>("UpdateStatus");
            var button = Ui<Button>("UpdateButton");
            button.IsEnabled = false;
            status.Text = "Проверяю GitHub Releases…";
            try
            {
                using (var service = new UpdateService())
                using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
                {
                    var update = await service.Check(timeout.Token);
                    if (update == null) { status.Text = "Установлена актуальная версия."; return; }
                    status.Text = "Доступна версия " + update.Version;
                    if (!automatic && !Confirm("Установить BlueClipboard " + update.Version + "?")) return;
                    var progress = new Progress<int>(percent => status.Text = "Загрузка: " + percent + "%");
                    string setup = await service.Download(update, folder, progress, timeout.Token);
                    if (exiting || (automatic && (window.IsVisible || store.GetSetting("autoUpdate", "true") != "true"))) return;
                    string runner = Path.Combine(Path.GetDirectoryName(setup), "BlueClipboard.Update.exe");
                    File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BlueClipboard.Update.exe"), runner);
                    string arguments = "\"" + setup + "\" \"" + Assembly.GetExecutingAssembly().Location + "\" " + update.Asset.Digest.Substring(7) + (AppInfo.IsInstalled ? " installed" : " portable");
                    Process.Start(new ProcessStartInfo(runner, arguments) { UseShellExecute = false, CreateNoWindow = true });
                    status.Text = "Установщик запущен.";
                }
            }
            catch (Exception ex) { status.Text = "Проверка обновлений: " + ex.Message; }
            finally { checkingUpdate = false; button.IsEnabled = true; }
        }
        private void CreateTray()
        {
            trayIcon = MakeIcon();
            tray = new Forms.NotifyIcon { Icon = trayIcon, Visible = true, Text = "BlueClipboard — Ctrl+Shift+V" };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Открыть историю", null, delegate { ShowPanel(IntPtr.Zero); });
            menu.Items.Add("Пауза / продолжить запись", null, delegate { Pause(); });
            menu.Items.Add("Настройки", null, delegate { ShowPanel(IntPtr.Zero); OpenSettings(); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Выйти", null, async delegate { await Quit(); });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { ShowPanel(IntPtr.Zero); };
            app.SessionEnding += delegate { clipboard.Stop(); clipboard.Pending.Wait(2000); };
        }
        private static System.Drawing.Icon MakeIcon()
        {
            using (var bitmap = new System.Drawing.Bitmap(32, 32))
            {
                using (var g = System.Drawing.Graphics.FromImage(bitmap))
                {
                    g.Clear(System.Drawing.Color.FromArgb(0, 120, 212));
                    using (var pen = new System.Drawing.Pen(System.Drawing.Color.White, 2)) { g.DrawRectangle(pen, 9, 8, 14, 19); g.DrawLine(pen, 12, 15, 20, 15); g.DrawLine(pen, 12, 20, 20, 20); }
                    g.FillRectangle(System.Drawing.Brushes.White, 12, 5, 8, 5);
                }
                IntPtr ptr = bitmap.GetHicon(); try { return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(ptr).Clone(); } finally { DestroyIcon(ptr); }
            }
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr ptr);
        internal async Task Quit()
        {
            if (exiting) return; exiting = true;
            clipboard.Stop(); Native.RemoveClipboardFormatListener(handle); Native.UnregisterHotKey(handle, 1);
            if (winVHook != IntPtr.Zero) SetWinV(false);
            if (tray != null) { tray.Visible = false; tray.Dispose(); trayIcon.Dispose(); }
            searchTimer.Stop(); statusTimer.Stop(); updateTimer.Stop();
            await clipboard.Pending;
            window.Close(); app.Shutdown();
        }
    }
}
