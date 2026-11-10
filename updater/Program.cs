using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Windows.Forms;

internal static class UpdateRunner
{
    [STAThread] static int Main(string[] args)
    {
        // This helper is copied to the download folder before launch so Setup can replace all app files.
        if (args.Length != 4) return 2;
        string setup = Path.GetFullPath(args[0]), app = Path.GetFullPath(args[1]);
        try
        {
            string hash;
            using (var stream = File.OpenRead(setup)) using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
            if (!String.Equals(hash, args[2], StringComparison.OrdinalIgnoreCase)) throw new IOException("Контрольная сумма установщика изменилась.");
            string arguments = "/SILENT /SUPPRESSMSGBOXES /SP- /NORESTART /UPDATE /LOG=\"" + Path.Combine(Path.GetDirectoryName(setup), "install.log") + "\"";
            if (args[3] == "installed") arguments += " /DIR=\"" + Path.GetDirectoryName(app) + "\"";
            else arguments = "/SP-"; // A portable app transitions through the ordinary installation wizard.
            using (var installer = Process.Start(new ProcessStartInfo(setup, arguments) { UseShellExecute = true }))
            {
                installer.WaitForExit();
                if (installer.ExitCode != 0) throw new IOException("Установщик завершился с кодом " + installer.ExitCode + ". Текущая версия сохранена. Журнал: " + Path.Combine(Path.GetDirectoryName(setup), "install.log"));
            }
            if (args[3] == "installed") Process.Start(new ProcessStartInfo(app, "--background") { UseShellExecute = true });
            return 0;
        }
        catch (Exception ex)
        {
            try { if (File.Exists(app)) Process.Start(new ProcessStartInfo(app, "--background") { UseShellExecute = true }); } catch { }
            MessageBox.Show("Не удалось обновить BlueClipboard.\n\n" + ex.Message, "BlueClipboard", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
