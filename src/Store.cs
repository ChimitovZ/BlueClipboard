using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace BlueClipboard
{
    public sealed class Clip
    {
        public long Id { get; set; }
        public string Kind { get; set; }
        public string Text { get; set; }
        public string Source { get; set; }
        public long Created { get; set; }
        public bool Pinned { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public long Bytes { get; set; }
        public byte[] Data { get; set; }
        public byte[] Thumbnail { get; set; }
        public BitmapSource Preview { get; set; }
        public string Title { get { return Kind == "image" ? "Изображение · " + Width + " × " + Height : (Text ?? "").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' '); } }
        public string Badge { get { return Kind == "image" ? "ИЗОБРАЖЕНИЕ" : "ТЕКСТ"; } }
        public string PinLabel { get { return Pinned ? "● Закреплено" : ""; } }
        public string Meta { get { return Source + "  ·  " + new DateTime(Created, DateTimeKind.Utc).ToLocalTime().ToString("dd.MM · HH:mm"); } }
        public string Details { get { return Kind == "image" ? Width + " × " + Height + " px · " + FormatBytes(Bytes) : Bytes.ToString("N0") + " байт · обычный текст"; } }
        public static string FormatBytes(long bytes) { return bytes >= 1048576 ? (bytes / 1048576.0).ToString("0.0") + " МБ" : (bytes / 1024.0).ToString("0.#") + " КБ"; }
    }

    // A single connection guarded by gate. Every statement is parameterized and finalized.
    // winsqlite3 is shipped with Windows 10; no downloaded native dependencies are needed.
    public sealed class Store : IDisposable
    {
        private IntPtr db;
        private readonly object gate = new object();
        public Store(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            int rc = Sql.sqlite3_open16(path, out db);
            if (rc != 0) { string error = Error(); Dispose(); throw new IOException(error); }
            Sql.sqlite3_busy_timeout(db, 5000);
            Execute("PRAGMA journal_mode=WAL");
            Execute("PRAGMA synchronous=FULL");
            Execute("PRAGMA secure_delete=ON");
            Execute("CREATE TABLE IF NOT EXISTS clips (id INTEGER PRIMARY KEY, hash TEXT NOT NULL UNIQUE, kind TEXT NOT NULL, text TEXT NOT NULL, source TEXT NOT NULL, search TEXT NOT NULL, created INTEGER NOT NULL, pinned INTEGER NOT NULL DEFAULT 0, width INTEGER NOT NULL, height INTEGER NOT NULL, bytes INTEGER NOT NULL, data BLOB, thumbnail BLOB)");
            Execute("CREATE INDEX IF NOT EXISTS clips_order ON clips(pinned DESC, created DESC, id DESC)");
            Execute("CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL)");
        }
        private string Error() { return db == IntPtr.Zero ? "Не удалось открыть базу истории." : Marshal.PtrToStringUni(Sql.sqlite3_errmsg16(db)); }
        private IntPtr Prepare(string sql, params object[] values)
        {
            IntPtr stmt;
            if (Sql.sqlite3_prepare16_v2(db, sql, -1, out stmt, IntPtr.Zero) != 0) throw new IOException(Error());
            try
            {
                for (int i = 0; i < values.Length; i++)
                {
                    object value = values[i]; int rc;
                    if (value == null) rc = Sql.sqlite3_bind_null(stmt, i + 1);
                    else if (value is byte[]) { byte[] bytes = (byte[])value; rc = Sql.sqlite3_bind_blob(stmt, i + 1, bytes, bytes.Length, new IntPtr(-1)); }
                    else if (value is string) { string s = (string)value; rc = Sql.sqlite3_bind_text16(stmt, i + 1, s, s.Length * 2, new IntPtr(-1)); }
                    else rc = Sql.sqlite3_bind_int64(stmt, i + 1, Convert.ToInt64(value));
                    if (rc != 0) throw new IOException(Error());
                }
                return stmt;
            }
            catch { Sql.sqlite3_finalize(stmt); throw; }
        }
        private int Step(IntPtr stmt)
        {
            int rc = Sql.sqlite3_step(stmt);
            if (rc != 100 && rc != 101) throw new IOException(Error());
            return rc;
        }
        private void Execute(string sql, params object[] values)
        {
            lock (gate) { IntPtr stmt = Prepare(sql, values); try { while (Step(stmt) == 100) { } } finally { Sql.sqlite3_finalize(stmt); } }
        }
        private long Scalar(string sql, params object[] values)
        {
            lock (gate) { IntPtr stmt = Prepare(sql, values); try { return Step(stmt) == 100 ? Sql.sqlite3_column_int64(stmt, 0) : 0; } finally { Sql.sqlite3_finalize(stmt); } }
        }
        private static string Str(IntPtr stmt, int index)
        {
            IntPtr ptr = Sql.sqlite3_column_text16(stmt, index);
            return ptr == IntPtr.Zero ? "" : Marshal.PtrToStringUni(ptr, Sql.sqlite3_column_bytes16(stmt, index) / 2);
        }
        private static byte[] Blob(IntPtr stmt, int index)
        {
            int len = Sql.sqlite3_column_bytes(stmt, index);
            if (len == 0) return null;
            byte[] bytes = new byte[len]; Marshal.Copy(Sql.sqlite3_column_blob(stmt, index), bytes, 0, len); return bytes;
        }
        public static string Hash(Clip clip)
        {
            using (var hash = SHA256.Create()) return clip.Kind + ":" + Convert.ToBase64String(hash.ComputeHash(clip.Kind == "image" ? clip.Data : Encoding.UTF8.GetBytes(clip.Text)));
        }
        public long Add(Clip clip, int maxItems, long maxBytes)
        {
            lock (gate)
            {
                Execute("BEGIN IMMEDIATE");
                try
                {
                    string hash = Hash(clip);
                    long id = Scalar("SELECT id FROM clips WHERE hash=?", hash);
                    if (id != 0) Execute("UPDATE clips SET created=?,source=?,search=? WHERE id=?", clip.Created, clip.Source, (clip.Text + " " + clip.Source + (clip.Kind == "image" ? " изображение" : "")).ToLowerInvariant(), id);
                    else
                    {
                        Execute("INSERT INTO clips(hash,kind,text,source,search,created,width,height,bytes,data,thumbnail) VALUES(?,?,?,?,?,?,?,?,?,?,?)", hash, clip.Kind, clip.Text ?? "", clip.Source, ((clip.Text ?? "") + " " + clip.Source + (clip.Kind == "image" ? " изображение" : "")).ToLowerInvariant(), clip.Created, clip.Width, clip.Height, clip.Bytes, clip.Data, clip.Thumbnail);
                        id = Sql.sqlite3_last_insert_rowid(db);
                    }
                    Trim(maxItems, maxBytes);
                    Execute("COMMIT"); return id;
                }
                catch { Execute("ROLLBACK"); throw; }
            }
        }
        public void Trim(int maxItems, long maxBytes)
        {
            lock (gate)
            {
                long count = Count(), bytes = TotalBytes();
                while ((maxItems > 0 && count > maxItems) || (maxBytes > 0 && bytes > maxBytes))
                {
                    long id = Scalar("SELECT id FROM clips WHERE pinned=0 ORDER BY created,id LIMIT 1");
                    if (id == 0) break; // Pinned entries are never silently evicted.
                    long size = Scalar("SELECT bytes FROM clips WHERE id=?", id);
                    Execute("DELETE FROM clips WHERE id=?", id); count--; bytes -= size;
                }
            }
        }
        public List<Clip> Search(string query, string filter, int limit, int offset)
        {
            lock (gate)
            {
                var result = new List<Clip>();
                string where = filter == "pinned" ? " AND pinned=1" : (filter == "text" || filter == "image" ? " AND kind='" + filter + "'" : "");
                IntPtr stmt = Prepare("SELECT id,kind,substr(text,1,350),source,created,pinned,width,height,bytes,thumbnail FROM clips WHERE instr(search,?)>0" + where + " ORDER BY pinned DESC,created DESC,id DESC LIMIT ? OFFSET ?", (query ?? "").ToLowerInvariant(), limit, offset);
                try
                {
                    while (Step(stmt) == 100)
                    {
                        var c = new Clip { Id = Sql.sqlite3_column_int64(stmt, 0), Kind = Str(stmt, 1), Text = Str(stmt, 2), Source = Str(stmt, 3), Created = Sql.sqlite3_column_int64(stmt, 4), Pinned = Sql.sqlite3_column_int64(stmt, 5) != 0, Width = (int)Sql.sqlite3_column_int64(stmt, 6), Height = (int)Sql.sqlite3_column_int64(stmt, 7), Bytes = Sql.sqlite3_column_int64(stmt, 8), Thumbnail = Blob(stmt, 9) };
                        if (c.Thumbnail != null) { try { c.Preview = Images.Decode(c.Thumbnail); } catch { /* A damaged thumbnail must not hide the rest of history. */ } }
                        result.Add(c);
                    }
                }
                finally { Sql.sqlite3_finalize(stmt); }
                return result;
            }
        }
        public Clip Get(long id)
        {
            lock (gate)
            {
                IntPtr stmt = Prepare("SELECT text,data,kind,width,height FROM clips WHERE id=?", id);
                try { return Step(stmt) == 100 ? new Clip { Id = id, Text = Str(stmt, 0), Data = Blob(stmt, 1), Kind = Str(stmt, 2), Width = (int)Sql.sqlite3_column_int64(stmt, 3), Height = (int)Sql.sqlite3_column_int64(stmt, 4) } : null; }
                finally { Sql.sqlite3_finalize(stmt); }
            }
        }
        public long Count() { return Scalar("SELECT count(*) FROM clips"); }
        public long TotalBytes() { return Scalar("SELECT coalesce(sum(bytes),0) FROM clips"); }
        public void Pin(long id, bool pin) { Execute("UPDATE clips SET pinned=? WHERE id=?", pin ? 1 : 0, id); }
        public void Delete(long id) { Execute("DELETE FROM clips WHERE id=?", id); }
        public void ClearUnpinned() { Execute("DELETE FROM clips WHERE pinned=0"); Execute("PRAGMA wal_checkpoint(TRUNCATE)"); Execute("VACUUM"); }
        public void Set(string key, string value) { Execute("INSERT OR REPLACE INTO settings(key,value) VALUES(?,?)", key, value); }
        public string GetSetting(string key, string fallback)
        {
            lock (gate) { IntPtr stmt = Prepare("SELECT value FROM settings WHERE key=?", key); try { return Step(stmt) == 100 ? Str(stmt, 0) : fallback; } finally { Sql.sqlite3_finalize(stmt); } }
        }
        public void Dispose() { lock (gate) { if (db != IntPtr.Zero) { Sql.sqlite3_close(db); db = IntPtr.Zero; } } }
    }
    internal static class Sql
    {
        const string Dll = "winsqlite3.dll";
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] internal static extern int sqlite3_open16(string name, out IntPtr db);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_close(IntPtr db);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_errmsg16(IntPtr db);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_busy_timeout(IntPtr db, int ms);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] internal static extern int sqlite3_prepare16_v2(IntPtr db, string sql, int bytes, out IntPtr stmt, IntPtr tail);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_step(IntPtr stmt);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] internal static extern int sqlite3_bind_text16(IntPtr stmt, int i, string text, int bytes, IntPtr destructor);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_blob(IntPtr stmt, int i, byte[] blob, int bytes, IntPtr destructor);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_int64(IntPtr stmt, int i, long value);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_null(IntPtr stmt, int i);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_column_text16(IntPtr stmt, int i);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_column_bytes16(IntPtr stmt, int i);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern long sqlite3_column_int64(IntPtr stmt, int i);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_column_blob(IntPtr stmt, int i);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_column_bytes(IntPtr stmt, int i);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern long sqlite3_last_insert_rowid(IntPtr db);
    }
}
