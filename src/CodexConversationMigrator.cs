using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Microsoft.Win32.SafeHandles;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace CodexConversationMigrator
{
    class Conversation
    {
        public string Id, Name, Updated, FilePath;
        public long Size;
        public bool Pinned;
        public List<string> LineageFiles = new List<string>();
    }

    class ManifestItem
    {
        public string id { get; set; }
        public string name { get; set; }
        public string updated_at { get; set; }
        public string archive_path { get; set; }
        public string sha256 { get; set; }
        public long size { get; set; }
        public string rollout_file_name { get; set; }
        public string created_at { get; set; }
        public List<RolloutItem> lineage { get; set; }
    }

    class RolloutItem
    {
        public string archive_path { get; set; }
        public string sha256 { get; set; }
        public long size { get; set; }
        public string rollout_file_name { get; set; }
        public string created_at { get; set; }
        public bool current { get; set; }
    }

    class Manifest
    {
        public int format_version { get; set; }
        public string created_at_utc { get; set; }
        public string source { get; set; }
        public List<ManifestItem> conversations { get; set; }
        public List<WorkspaceItem> workspaces { get; set; }
        public List<ArtifactItem> artifacts { get; set; }
        public List<string> warnings { get; set; }
    }

    class WorkspaceItem
    {
        public string source_root { get; set; }
        public string archive_root { get; set; }
        public string restore_root { get; set; }
    }

    class ArtifactItem
    {
        public string source_path { get; set; }
        public string archive_path { get; set; }
        public string restore_path { get; set; }
        public string sha256 { get; set; }
        public long size { get; set; }
        public string last_write_utc { get; set; }
    }

    class ArtifactRestoreResult
    {
        public int Count;
        public string Root = "";
        public Dictionary<string, string> PathMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    static class SqliteNative
    {
        const int SQLITE_OK = 0, SQLITE_OPEN_READWRITE = 2;
        static bool loaded;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ExecCallback(IntPtr arg, int count, IntPtr values, IntPtr names);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibrary(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetDllDirectory(string path);
        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_close_v2(IntPtr db);
        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);
        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_exec(IntPtr db, byte[] sql, ExecCallback callback, IntPtr arg, out IntPtr error);
        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern void sqlite3_free(IntPtr value);
        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr sqlite3_libversion();

        static void EnsureLoaded()
        {
            if (loaded) return;
            if (IntPtr.Size != 8) throw new PlatformNotSupportedException("此版本仅支持 64 位 Windows。");
            string folder = Path.Combine(Path.GetTempPath(), "CodexConversationMigrator", "sqlite-3.53.4-x64");
            string dll = Path.Combine(folder, "sqlite3.dll");
            Directory.CreateDirectory(folder);
            if (!File.Exists(dll) || new FileInfo(dll).Length != 3285504)
            {
                using (Stream input = Assembly.GetExecutingAssembly().GetManifestResourceStream("sqlite3.dll"))
                {
                    if (input == null) throw new InvalidOperationException("EXE 内缺少 SQLite 组件。");
                    using (var output = new FileStream(dll, FileMode.Create, FileAccess.Write, FileShare.Read)) input.CopyTo(output);
                }
            }
            SetDllDirectory(folder);
            if (LoadLibrary(dll) == IntPtr.Zero) throw new InvalidOperationException("无法加载内置 SQLite 组件，错误码：" + Marshal.GetLastWin32Error());
            loaded = true;
            string version = Marshal.PtrToStringAnsi(sqlite3_libversion());
            if (string.Compare(version, "3.51.3", StringComparison.Ordinal) < 0) throw new InvalidOperationException("SQLite 版本过低：" + version);
        }

        public static void Execute(string dbPath, string sql)
        {
            EnsureLoaded(); IntPtr db; int rc = sqlite3_open_v2(Utf8(dbPath), out db, SQLITE_OPEN_READWRITE, IntPtr.Zero);
            if (rc != SQLITE_OK) throw new InvalidOperationException("无法打开 Codex 状态数据库（SQLite " + rc + "）。请确认 Codex 已完全退出。");
            try { sqlite3_busy_timeout(db, 5000); Exec(db, sql, null); }
            finally { sqlite3_close_v2(db); }
        }

        public static string Scalar(string dbPath, string sql)
        {
            EnsureLoaded(); IntPtr db; int rc = sqlite3_open_v2(Utf8(dbPath), out db, SQLITE_OPEN_READWRITE, IntPtr.Zero);
            if (rc != SQLITE_OK) throw new InvalidOperationException("无法打开 SQLite 数据库：" + rc);
            try
            {
                string value = null;
                ExecCallback callback = delegate(IntPtr arg, int count, IntPtr values, IntPtr names) { if (count > 0) { IntPtr p = Marshal.ReadIntPtr(values); value = PtrUtf8(p); } return 0; };
                Exec(db, sql, callback); GC.KeepAlive(callback); return value;
            }
            finally { sqlite3_close_v2(db); }
        }

        static void Exec(IntPtr db, string sql, ExecCallback callback)
        {
            IntPtr error; int rc = sqlite3_exec(db, Utf8(sql), callback, IntPtr.Zero, out error);
            if (rc != SQLITE_OK)
            {
                string message = PtrUtf8(error); if (error != IntPtr.Zero) sqlite3_free(error);
                throw new InvalidOperationException("SQLite 操作失败（" + rc + "）：" + message);
            }
        }
        static byte[] Utf8(string value) { byte[] raw = Encoding.UTF8.GetBytes(value); byte[] z = new byte[raw.Length + 1]; Buffer.BlockCopy(raw, 0, z, 0, raw.Length); return z; }
        static string PtrUtf8(IntPtr p) { if (p == IntPtr.Zero) return ""; int n = 0; while (Marshal.ReadByte(p, n) != 0) n++; byte[] b = new byte[n]; Marshal.Copy(p, b, 0, n); return Encoding.UTF8.GetString(b); }
    }

    static class MigrationCore
    {
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        static readonly Regex OuterTimestamp = new Regex(@"(?m)^\s*\{\s*""timestamp""\s*:\s*""([^""]+)""", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        public static bool SkipAppServerForTests;
        const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, OPEN_EXISTING = 3, CREATE_ALWAYS = 2, FILE_ATTRIBUTE_DIRECTORY = 0x10, FILE_ATTRIBUTE_REPARSE_POINT = 0x400, INVALID_FILE_ATTRIBUTES = 0xffffffff;
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct FindData
        {
            public uint attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME creationTime, accessTime, writeTime;
            public uint sizeHigh, sizeLow, reserved0, reserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string fileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string alternateName;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFile(string name, uint access, FileShare share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern uint GetFileAttributes(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr FindFirstFile(string pattern, out FindData data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool FindNextFile(IntPtr handle, out FindData data);
        [DllImport("kernel32.dll")] static extern bool FindClose(IntPtr handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool MoveFileEx(string existing, string destination, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool DeleteFile(string path);

        public static string DefaultCodexHome()
        {
            string custom = Environment.GetEnvironmentVariable("CODEX_HOME");
            return !string.IsNullOrWhiteSpace(custom)
                ? Path.GetFullPath(custom)
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        }

        public static List<Conversation> Load(string home)
        {
            var index = new Dictionary<string, Conversation>(StringComparer.OrdinalIgnoreCase);
            string indexPath = Path.Combine(home, "session_index.jsonl");
            if (File.Exists(indexPath))
            {
                foreach (string line in ReadLinesShared(indexPath))
                {
                    try
                    {
                        var d = Json.Deserialize<Dictionary<string, object>>(line);
                        string id = Get(d, "id");
                        if (string.IsNullOrWhiteSpace(id)) continue;
                        index[id] = new Conversation { Id = id, Name = Get(d, "thread_name"), Updated = Get(d, "updated_at") };
                    }
                    catch { }
                }
            }

            var pinned = LoadPinnedIds(home);
            var result = new Dictionary<string, Conversation>(StringComparer.OrdinalIgnoreCase);
            string sessions = Path.Combine(home, "sessions");
            if (!Directory.Exists(sessions)) return new List<Conversation>();
            foreach (string file in Directory.GetFiles(sessions, "*.jsonl", SearchOption.AllDirectories))
            {
                string id = ExtractId(Path.GetFileNameWithoutExtension(file));
                if (string.IsNullOrEmpty(id)) continue;
                FileInfo info = new FileInfo(file);
                DateTime activity = ReadLastActivityUtc(file);
                Conversation c;
                if (!result.TryGetValue(id, out c))
                {
                    Conversation indexed;
                    bool hasIndex = index.TryGetValue(id, out indexed);
                    c = new Conversation
                    {
                        Id = id,
                        Name = hasIndex && !string.IsNullOrWhiteSpace(indexed.Name) ? indexed.Name : "（未命名对话）",
                        Updated = hasIndex && !string.IsNullOrWhiteSpace(indexed.Updated) ? indexed.Updated : DateTime.MinValue.ToString("o"),
                        Pinned = pinned.Contains(id)
                    };
                    result[id] = c;
                }
                c.LineageFiles.Add(file);
                c.Size += info.Length;
                if (c.FilePath == null || activity > ParseDate(c.Updated))
                {
                    c.FilePath = file;
                    c.Updated = activity != DateTime.MinValue ? activity.ToString("o") : info.LastWriteTimeUtc.ToString("o");
                }
            }
            return result.Values.OrderByDescending(x => x.Pinned).ThenByDescending(x => ParseDate(x.Updated)).ToList();
        }

        static DateTime ReadLastActivityUtc(string path)
        {
            const int tailBytes = 8 * 1024 * 1024;
            DateTime latest = DateTime.MinValue;
            try
            {
                using (var stream = OpenLongFile(path, false))
                {
                    int count = (int)Math.Min(stream.Length, tailBytes);
                    stream.Seek(-count, SeekOrigin.End);
                    byte[] buffer = new byte[count];
                    int offset = 0, read;
                    while (offset < count && (read = stream.Read(buffer, offset, count - offset)) > 0) offset += read;
                    string text = Encoding.UTF8.GetString(buffer, 0, offset);
                    foreach (Match match in OuterTimestamp.Matches(text))
                    {
                        DateTime value = ParseDate(match.Groups[1].Value);
                        if (value > latest) latest = value;
                    }
                }
                if (latest != DateTime.MinValue) return latest;
                foreach (string line in ReadLinesShared(path))
                {
                    Match match = OuterTimestamp.Match(line);
                    if (!match.Success) continue;
                    DateTime value = ParseDate(match.Groups[1].Value);
                    if (value > latest) latest = value;
                }
            }
            catch { }
            return latest != DateTime.MinValue ? latest : LongLastWriteUtc(path);
        }

        static HashSet<string> LoadPinnedIds(string home)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string path = Path.Combine(home, ".codex-global-state.json");
            if (!File.Exists(path)) return result;
            try
            {
                var root = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
                object value;
                if (root.TryGetValue("pinned-thread-ids", out value)) AddPinnedValues(result, value);
                object atomObject;
                if (root.TryGetValue("electron-persisted-atom-state", out atomObject))
                {
                    var atom = atomObject as Dictionary<string, object>;
                    if (atom != null && atom.TryGetValue("app-server-pinned-thread-order-v1", out value)) AddPinnedValues(result, value);
                }
            }
            catch { }
            return result;
        }

        static void AddPinnedValues(HashSet<string> target, object value)
        {
            var values = value as System.Collections.IEnumerable;
            if (values == null || value is string) return;
            foreach (object item in values)
            {
                string id = Convert.ToString(item, CultureInfo.InvariantCulture);
                if (IsId(id)) target.Add(id);
            }
        }

        public static void Export(string codexHome, IEnumerable<Conversation> selected, string zipPath)
        {
            Export(codexHome, selected, zipPath, false);
        }

        public static void Export(string codexHome, IEnumerable<Conversation> selected, string zipPath, bool includeFiles)
        {
            Export(codexHome, selected, zipPath, includeFiles, null);
        }

        public static int Export(string codexHome, IEnumerable<Conversation> selected, string zipPath, bool includeFiles, Action<string> progress)
        {
            var list = selected.ToList();
            if (list.Count == 0) throw new InvalidOperationException("请至少选择一个对话。");
            string temp = Path.Combine(Path.GetTempPath(), "codex-migrate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(temp, "sessions"));
            int warningCount = 0;
            try
            {
                var manifest = new Manifest { format_version = 5, created_at_utc = DateTime.UtcNow.ToString("o"), source = "Codex Conversation Migrator", conversations = new List<ManifestItem>(), workspaces = new List<WorkspaceItem>(), artifacts = new List<ArtifactItem>(), warnings = new List<string>() };
                for (int conversationIndex = 0; conversationIndex < list.Count; conversationIndex++)
                {
                    var c = list[conversationIndex]; if (progress != null) progress("正在复制对话 " + (conversationIndex + 1) + "/" + list.Count + "：" + c.Name);
                    var files = (c.LineageFiles ?? new List<string>()).Where(LongFileExists).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => ReadSessionTimestamp(x)).ThenBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                    if (!files.Contains(c.FilePath, StringComparer.OrdinalIgnoreCase)) files.Add(c.FilePath);
                    var lineage = new List<RolloutItem>();
                    for (int partIndex = 0; partIndex < files.Count; partIndex++)
                    {
                        string sourcePath = files[partIndex];
                        string archiveName = "sessions/" + c.Id + "/" + partIndex.ToString("0000", CultureInfo.InvariantCulture) + ".jsonl";
                        string dest = Path.Combine(temp, archiveName.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)); CopyShared(sourcePath, dest);
                        lineage.Add(new RolloutItem { archive_path = archiveName, sha256 = Sha256(dest), size = LongFileLength(dest), rollout_file_name = Path.GetFileName(sourcePath), created_at = ReadSessionTimestamp(dest), current = string.Equals(sourcePath, c.FilePath, StringComparison.OrdinalIgnoreCase) });
                    }
                    RolloutItem current = lineage.FirstOrDefault(x => x.current) ?? lineage.Last();
                    manifest.conversations.Add(new ManifestItem { id = c.Id, name = c.Name ?? "", updated_at = c.Updated ?? "", archive_path = current.archive_path, sha256 = current.sha256, size = current.size, rollout_file_name = current.rollout_file_name, created_at = current.created_at, lineage = lineage });
                }
                if (includeFiles) CollectArtifacts(codexHome, list, temp, manifest, progress);
                warningCount = manifest.warnings.Count;
                if (warningCount > 0) File.WriteAllLines(Path.Combine(temp, "导出警告.txt"), new[] { "以下非关键文件因被占用、无权限、已删除或属于临时锁文件而未打包：", "" }.Concat(manifest.warnings), new UTF8Encoding(false));
                if (progress != null) progress("正在写入迁移清单…");
                File.WriteAllText(Path.Combine(temp, "manifest.json"), Json.Serialize(manifest), new UTF8Encoding(false));
                if (progress != null) progress("正在压缩迁移包，请稍候…");
                string outputFolder = Path.GetDirectoryName(Path.GetFullPath(zipPath));
                string partialZip = Path.Combine(outputFolder, ".codexpack-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
                try
                {
                    ZipFile.CreateFromDirectory(temp, ToLongPath(partialZip), CompressionLevel.Optimal, false);
                    if (LongFileExists(zipPath) && !DeleteFile(ToLongPath(zipPath))) throw new IOException("无法覆盖已有迁移包，Windows 错误码：" + Marshal.GetLastWin32Error());
                    if (!MoveFileEx(ToLongPath(partialZip), ToLongPath(zipPath), 8)) throw new IOException("无法完成迁移包写入，Windows 错误码：" + Marshal.GetLastWin32Error());
                }
                finally { if (LongFileExists(partialZip)) try { DeleteFile(ToLongPath(partialZip)); } catch { } }
                if (progress != null) progress("正在完成校验…");
            }
            finally { try { Directory.Delete(temp, true); } catch { } }
            return warningCount;
        }

        static void CollectArtifacts(string codexHome, List<Conversation> conversations, string temp, Manifest manifest, Action<string> progress)
        {
            const int MaxFiles = 100000;
            const long MaxBytes = 8L * 1024 * 1024 * 1024;
            var sessionFiles = conversations.SelectMany(x => x.LineageFiles != null && x.LineageFiles.Count > 0 ? (IEnumerable<string>)x.LineageFiles : new[] { x.FilePath }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var roots = sessionFiles.Select(ReadSessionCwd).Where(x => IsSafeWorkspace(x, codexHome)).Select(NormalizePath).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x.Length).ToList();
            var workspaces = new List<string>();
            foreach (string root in roots) if (!workspaces.Any(x => IsUnder(root, x))) workspaces.Add(root);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0; int sequence = 0;
            foreach (string root in workspaces)
            {
                string restoreRoot = "workspaces/" + sequence.ToString("0000", CultureInfo.InvariantCulture) + "-" + SafeSegment(new DirectoryInfo(root).Name);
                sequence++;
                manifest.workspaces.Add(new WorkspaceItem { source_root = root, archive_root = "", restore_root = restoreRoot });
                if (progress != null) progress("正在扫描工作目录：" + root);
                foreach (string file in EnumerateWorkspaceFiles(root))
                {
                    string relative = file.Substring(root.TrimEnd(Path.DirectorySeparatorChar).Length).TrimStart(Path.DirectorySeparatorChar);
                    TryAddArtifact(file, restoreRoot + "/" + relative.Replace(Path.DirectorySeparatorChar, '/'), temp, manifest, seen, ref total, MaxFiles, MaxBytes);
                    if (progress != null && manifest.artifacts.Count % 25 == 0) progress("正在打包关联文件：" + manifest.artifacts.Count + " 个，" + FormatBytes(total));
                }
            }
            int external = 0;
            if (progress != null) progress("正在检查对话记录中的外部附件…");
            foreach (string file in sessionFiles.SelectMany(ExtractReferencedFiles).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string full = NormalizePath(file);
                if (workspaces.Any(x => IsUnder(full, x)) || IsUnder(full, codexHome) || !IsSafeExternalReference(full) || IsSensitiveFile(full)) continue;
                string restorePath = "referenced/" + external.ToString("0000", CultureInfo.InvariantCulture) + "/" + SafeSegment(Path.GetFileName(full));
                TryAddArtifact(full, restorePath, temp, manifest, seen, ref total, MaxFiles, MaxBytes); external++;
            }
            if (progress != null) progress("关联文件扫描完成：" + manifest.artifacts.Count + " 个，" + FormatBytes(total));
        }

        static void TryAddArtifact(string source, string restorePath, string temp, Manifest manifest, HashSet<string> seen, ref long total, int maxFiles, long maxBytes)
        {
            try { AddArtifact(source, restorePath, temp, manifest, seen, ref total, maxFiles, maxBytes); }
            catch (Exception ex)
            {
                if (!IsSkippableFileError(ex)) throw;
                manifest.warnings.Add(source + "\r\n  原因：" + FirstErrorMessage(ex));
            }
        }

        static bool IsSkippableFileError(Exception error)
        {
            for (Exception current = error; current != null; current = current.InnerException)
            {
                if (current is UnauthorizedAccessException || current is FileNotFoundException || current is DirectoryNotFoundException || current is PathTooLongException) return true;
                var win32 = current as Win32Exception;
                if (win32 != null && new[] { 2, 3, 5, 32, 33 }.Contains(win32.NativeErrorCode)) return true;
            }
            return false;
        }
        static string FirstErrorMessage(Exception error)
        {
            Exception current = error; while (current.InnerException != null) current = current.InnerException; return current.Message;
        }

        static void AddArtifact(string source, string restorePath, string temp, Manifest manifest, HashSet<string> seen, ref long total, int maxFiles, long maxBytes)
        {
            source = NormalizePath(source);
            if (!LongFileExists(source) || !seen.Add(source) || IsSensitiveFile(source)) return;
            uint attributes = GetFileAttributes(ToLongPath(source)); if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0) return;
            long sourceLength = LongFileLength(source);
            if (manifest.artifacts.Count >= maxFiles) throw new InvalidOperationException("关联文件超过 " + maxFiles + " 个，请精简工作目录后重试，或选择仅导出对话。");
            if (sourceLength > 2L * 1024 * 1024 * 1024) throw new InvalidOperationException("单个关联文件超过 2 GB，无法安全打包：" + source);
            if (total + sourceLength > maxBytes) throw new InvalidOperationException("关联文件总量超过 8 GB，请精简工作目录后重试，或选择仅导出对话。");
            string hash = Sha256(source);
            restorePath = ShortRestorePath(restorePath, hash);
            string archivePath = "artifacts/files/" + hash.Substring(0, 2) + "/" + hash;
            string destination = Path.Combine(temp, archivePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)); if (!File.Exists(destination)) CopyShared(source, destination); total += sourceLength;
            DateTime modified = LongLastWriteUtc(source);
            manifest.artifacts.Add(new ArtifactItem { source_path = source, archive_path = archivePath, restore_path = restorePath, sha256 = hash, size = sourceLength, last_write_utc = modified == DateTime.MinValue ? "" : modified.ToString("o") });
        }

        static IEnumerable<string> EnumerateWorkspaceFiles(string root)
        {
            var skip = new HashSet<string>(new[] { ".git", ".hg", ".svn", "node_modules", "__pycache__", ".pytest_cache", ".mypy_cache", ".gradle", ".cache" }, StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.Count > 0)
            {
                string folder = pending.Pop();
                FindData data; IntPtr find = FindFirstFile(ToLongPath(Path.Combine(folder, "*")), out data);
                if (find == new IntPtr(-1)) continue;
                try
                {
                    do
                    {
                        string name = data.fileName; if (name == "." || name == "..") continue;
                        string child = Path.Combine(folder, name); bool directory = (data.attributes & FILE_ATTRIBUTE_DIRECTORY) != 0, reparse = (data.attributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0;
                        if (directory) { if (!reparse && !skip.Contains(name)) pending.Push(child); }
                        else if (!reparse && !IsSensitiveFile(child)) yield return child;
                    } while (FindNextFile(find, out data));
                }
                finally { FindClose(find); }
            }
        }

        static string ReadSessionCwd(string path)
        {
            foreach (string line in ReadLinesShared(path).Take(30))
            {
                try
                {
                    var root = Json.Deserialize<Dictionary<string, object>>(line); if (Get(root, "type") != "session_meta") continue;
                    object payloadObject; if (!root.TryGetValue("payload", out payloadObject)) continue;
                    return Get(payloadObject as Dictionary<string, object>, "cwd");
                }
                catch { }
            }
            return "";
        }

        static bool IsSafeWorkspace(string path, string codexHome)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || !Directory.Exists(path)) return false;
                string full = NormalizePath(path).TrimEnd(Path.DirectorySeparatorChar), profile = NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)).TrimEnd(Path.DirectorySeparatorChar);
                if (full.Length <= 3 || string.Equals(full, profile, StringComparison.OrdinalIgnoreCase) || IsUnder(codexHome, full) || IsUnder(full, codexHome)) return false;
                string[] broad = { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) };
                return !broad.Any(x => !string.IsNullOrWhiteSpace(x) && string.Equals(full, NormalizePath(x).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }

        static IEnumerable<string> ExtractReferencedFiles(string sessionPath)
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in ReadLinesShared(sessionPath))
            {
                object node; try { node = Json.DeserializeObject(line); } catch { continue; }
                var strings = new List<string>(); CollectStrings(node, strings);
                foreach (string text in strings)
                {
                    foreach (Match match in Regex.Matches(text, @"(?i)(?:file:///)?[a-z]:[\\/][^\r\n\""'<>|]+"))
                    {
                        string candidate = match.Value;
                        if (candidate.StartsWith("file:///", StringComparison.OrdinalIgnoreCase)) candidate = candidate.Substring(8);
                        candidate = Uri.UnescapeDataString(candidate).Replace('/', Path.DirectorySeparatorChar).Trim();
                        string existing = LongestExistingFile(candidate);
                        if (existing != null) found.Add(existing);
                    }
                }
            }
            return found;
        }

        static void CollectStrings(object node, List<string> output)
        {
            string text = node as string; if (text != null) { output.Add(text); return; }
            var dictionary = node as Dictionary<string, object>; if (dictionary != null) { foreach (object value in dictionary.Values) CollectStrings(value, output); return; }
            var array = node as object[]; if (array != null) foreach (object value in array) CollectStrings(value, output);
            var list = node as System.Collections.ArrayList; if (list != null) foreach (object value in list) CollectStrings(value, output);
        }

        static string LongestExistingFile(string candidate)
        {
            candidate = candidate.TrimEnd(' ', '.', ',', ';', ':', ')', ']', '}');
            while (candidate.Length >= 3)
            {
                try { if (LongFileExists(candidate)) return NormalizePath(candidate); } catch { }
                int cut = Math.Max(candidate.LastIndexOf(' '), candidate.LastIndexOf('\t'));
                int punctuation = Math.Max(candidate.LastIndexOf(')'), Math.Max(candidate.LastIndexOf(']'), candidate.LastIndexOf('}')));
                cut = Math.Max(cut, punctuation);
                if (cut < 3) break; candidate = candidate.Substring(0, cut).TrimEnd(' ', '.', ',', ';', ':', ')', ']', '}');
            }
            return null;
        }

        static bool IsSensitiveFile(string path)
        {
            string name = Path.GetFileName(path), lower = name.ToLowerInvariant();
            if (lower == "auth.json" || lower == "credentials" || lower == "credentials.json" || lower == ".env" || lower.StartsWith(".env.") || lower == "id_rsa" || lower == "id_ed25519") return true;
            return lower.EndsWith(".pem") || lower.EndsWith(".key") || lower.EndsWith(".pfx") || lower.EndsWith(".p12") || lower.EndsWith(".lock") || lower.EndsWith(".lck") || lower.EndsWith(".tmp") || lower.StartsWith("~$");
        }
        static bool IsSafeExternalReference(string path)
        {
            string normalized = FromLongPath(path).Replace('/', '\\');
            string[] blocked = { "\\Program Files\\", "\\Program Files (x86)\\", "\\Windows\\", "\\ProgramData\\", "\\$Recycle.Bin\\", "\\System Volume Information\\" };
            return !blocked.Any(x => normalized.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        static string NormalizePath(string path)
        {
            path = FromLongPath(path);
            if (Path.IsPathRooted(path) && path.Length >= 240) return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        static string ToLongPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
            string full = Path.IsPathRooted(path) ? path : Path.GetFullPath(path);
            if (full.Length < 240) return full;
            if (full.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + full.Substring(2);
            return @"\\?\" + full;
        }
        static string FromLongPath(string path)
        {
            if (path == null) return "";
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path.Substring(8);
            return path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) ? path.Substring(4) : path;
        }
        static bool LongFileExists(string path)
        {
            try { uint attributes = GetFileAttributes(ToLongPath(path)); return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) == 0; } catch { return false; }
        }
        static FileStream OpenLongFile(string path, bool write)
        {
            SafeFileHandle handle = CreateFile(ToLongPath(path), write ? GENERIC_WRITE : GENERIC_READ, write ? FileShare.Read : (FileShare.ReadWrite | FileShare.Delete), IntPtr.Zero, write ? CREATE_ALWAYS : OPEN_EXISTING, 0x80, IntPtr.Zero);
            if (handle == null || handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); if (handle != null) handle.Dispose(); throw new IOException("Windows 无法打开文件（错误码 " + error + "）：" + FromLongPath(path), new Win32Exception(error)); }
            return new FileStream(handle, write ? FileAccess.Write : FileAccess.Read, 65536, false);
        }
        static long LongFileLength(string path) { using (var stream = OpenLongFile(path, false)) return stream.Length; }
        static DateTime LongLastWriteUtc(string path)
        {
            FindData data; IntPtr find = FindFirstFile(ToLongPath(path), out data); if (find == new IntPtr(-1)) return DateTime.MinValue;
            try { long value = ((long)data.writeTime.dwHighDateTime << 32) | (uint)data.writeTime.dwLowDateTime; return value > 0 ? DateTime.FromFileTimeUtc(value) : DateTime.MinValue; } catch { return DateTime.MinValue; } finally { FindClose(find); }
        }
        static bool IsUnder(string path, string root)
        {
            try { string p = NormalizePath(path), r = NormalizePath(root); return string.Equals(p, r, StringComparison.OrdinalIgnoreCase) || p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); } catch { return false; }
        }
        static string SafeSegment(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "workspace";
            foreach (char c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
            value = value.Trim(' ', '.'); return value.Length == 0 ? "workspace" : (value.Length > 80 ? value.Substring(0, 80) : value);
        }
        static string ShortRestorePath(string relative, string hash)
        {
            string normalized = relative.Replace('\\', '/');
            if (normalized.Length <= 170 && normalized.Split('/').All(x => x.Length <= 90)) return normalized;
            string[] parts = normalized.Split('/'); string group = parts.Length >= 2 ? parts[0] + "/" + parts[1] : "long-paths";
            return group + "/__long_paths/" + hash.Substring(0, 16) + "/" + SafeSegment(Path.GetFileName(normalized));
        }
        static string FormatBytes(long n)
        {
            if (n >= 1073741824L) return (n / 1073741824d).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
            if (n >= 1048576L) return (n / 1048576d).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            if (n >= 1024L) return (n / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
            return n.ToString(CultureInfo.InvariantCulture) + " B";
        }

        public static string Restore(string zipPath, string codexHome)
        {
            return Restore(zipPath, codexHome, null);
        }

        public static string Restore(string zipPath, string codexHome, Action<string> progress)
        {
            string temp = Path.Combine(Path.GetTempPath(), "codex-restore-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                if (progress != null) progress("正在读取迁移包…");
                SafeExtract(zipPath, temp);
                if (progress != null) progress("正在读取迁移清单…");
                string manifestPath = Path.Combine(temp, "manifest.json");
                if (!File.Exists(manifestPath)) throw new InvalidDataException("迁移包缺少 manifest.json。");
                Manifest manifest = Json.Deserialize<Manifest>(File.ReadAllText(manifestPath, Encoding.UTF8));
                if (manifest == null || (manifest.format_version < 1 || manifest.format_version > 5) || manifest.conversations == null) throw new InvalidDataException("不支持的迁移包格式。");
                Directory.CreateDirectory(codexHome);
                string backup = Path.Combine(codexHome, "migration-backups", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                int artifactCount = manifest.artifacts == null ? 0 : manifest.artifacts.Count;
                int conversationCount = manifest.conversations.Count;
                if (progress != null) progress(string.Format("迁移包包含 {0} 个会话、{1} 个关联文件，正在校验…", conversationCount, artifactCount));
                ArtifactRestoreResult assets = RestoreArtifacts(temp, codexHome, manifest, progress);
                int restored = 0, skipped = 0;
                var newIndexLines = new List<string>();
                var known = ExistingIds(Path.Combine(codexHome, "session_index.jsonl"));
                if (progress != null) progress("正在恢复会话历史链…");
                int conversationIndex = 0;
                foreach (var item in manifest.conversations)
                {
                    conversationIndex++;
                    if (!IsId(item.id)) throw new InvalidDataException("迁移包含无效对话 ID。");
                    List<RolloutItem> parts;
                    if (manifest.format_version >= 5 && item.lineage != null && item.lineage.Count > 0) parts = item.lineage;
                    else parts = new List<RolloutItem> { new RolloutItem { archive_path = item.archive_path, sha256 = item.sha256, size = item.size, rollout_file_name = item.rollout_file_name, created_at = item.created_at, current = true } };
                    if (progress != null) progress(string.Format("正在恢复会话历史链：{0}/{1}（{2} 个文件）", conversationIndex, conversationCount, parts.Count));
                    var existingFiles = FindSessions(codexHome, item.id).ToList();
                    if (existingFiles.Count > 0 && assets.PathMap.Count > 0)
                    {
                        Directory.CreateDirectory(backup);
                        for (int existingIndex = 0; existingIndex < existingFiles.Count; existingIndex++)
                        {
                            string existing = existingFiles[existingIndex];
                            string backupFile = Path.Combine(backup, "session-" + item.id + "-" + existingIndex.ToString("0000", CultureInfo.InvariantCulture) + ".jsonl.bak");
                            if (LongFileExists(backupFile)) DeleteFile(ToLongPath(backupFile)); CopyShared(existing, backupFile);
                            RewriteSessionPaths(existing, assets.PathMap);
                        }
                    }
                    int copiedParts = 0;
                    int partIndex = 0;
                    foreach (RolloutItem part in parts)
                    {
                        partIndex++;
                        string expectedArchivePath = manifest.format_version >= 5 ? "sessions/" + item.id + "/" : "sessions/" + item.id + ".jsonl";
                        if (part == null || (manifest.format_version >= 5 ? !part.archive_path.StartsWith(expectedArchivePath, StringComparison.Ordinal) || !Regex.IsMatch(part.archive_path.Substring(expectedArchivePath.Length), @"^\d{4}\.jsonl$") : !string.Equals(part.archive_path, expectedArchivePath, StringComparison.Ordinal))) throw new InvalidDataException("迁移包含无效历史链文件路径。");
                        string source = Path.Combine(temp, part.archive_path.Replace('/', Path.DirectorySeparatorChar));
                        if (!File.Exists(source) || new FileInfo(source).Length != part.size || !Sha256(source).Equals(part.sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("历史链文件校验失败：" + item.id);
                        string sourceId = ReadSessionId(source); if (!string.Equals(sourceId, item.id, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("历史链文件不属于清单中的会话：" + item.id);
                        DateTime dt = ParseDate(part.created_at);
                        if (dt == DateTime.MinValue) dt = ParseDate(ReadSessionTimestamp(source));
                        if (dt == DateTime.MinValue) dt = ParseDate(item.updated_at);
                        if (dt == DateTime.MinValue) dt = DateTime.UtcNow;
                        DateTime local = dt.ToLocalTime();
                        string folder = Path.Combine(codexHome, "sessions", local.ToString("yyyy"), local.ToString("MM"), local.ToString("dd"));
                        Directory.CreateDirectory(folder);
                        string fileName = manifest.format_version >= 5 ? LineageRolloutName(part.rollout_file_name, item.id) : CanonicalRolloutName(part.rollout_file_name, item.id, local);
                        string dest = Path.Combine(folder, fileName);
                        if (LongFileExists(dest)) continue;
                        CopyShared(source, dest); RewriteSessionPaths(dest, assets.PathMap); copiedParts++;
                        if (progress != null && (partIndex == parts.Count || partIndex % 5 == 0)) progress(string.Format("正在恢复会话历史链：{0}/{1}，已写入 {2}/{3} 个文件", conversationIndex, conversationCount, partIndex, parts.Count));
                    }
                    if (!known.Contains(item.id))
                    {
                        var row = new Dictionary<string, object> { { "id", item.id }, { "thread_name", item.name ?? "（已恢复对话）" }, { "updated_at", item.updated_at ?? DateTime.UtcNow.ToString("o") } };
                        newIndexLines.Add(Json.Serialize(row)); known.Add(item.id);
                    }
                    if (copiedParts > 0) restored++; else skipped++;
                }
                if (newIndexLines.Count > 0)
                {
                    if (progress != null) progress("正在更新会话索引…");
                    string indexPath = Path.Combine(codexHome, "session_index.jsonl");
                    Directory.CreateDirectory(backup);
                    if (File.Exists(indexPath)) File.Copy(indexPath, Path.Combine(backup, "session_index.jsonl.bak"), true);
                    using (var fs = new FileStream(indexPath, FileMode.Append, FileAccess.Write, FileShare.Read))
                    using (var sw = new StreamWriter(fs, new UTF8Encoding(false))) foreach (string line in newIndexLines) sw.WriteLine(line);
                }
                if (progress != null) progress("正在准备 Codex 列表重建…");
                string backfill = ScheduleBackfill(codexHome, backup);
                var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in manifest.conversations) if (!string.IsNullOrWhiteSpace(item.name)) titles[item.id] = item.name;
                if (progress != null) progress("正在调用 Codex App Server 重建列表和标题…");
                int named = SkipAppServerForTests ? 0 : SyncTitlesViaOfficialAppServer(codexHome, titles);
                if (progress != null) progress("正在完成恢复…");
                string assetText = assets.Count > 0 ? string.Format("\n恢复关联文件 {0} 个：{1}", assets.Count, assets.Root) : "\n迁移包中没有关联文件。";
                return string.Format("恢复完成：新增或补全历史链 {0} 个会话，未新增文件 {1} 个，恢复标题 {2} 个。{3}\n{4}\nCodex 官方 App Server 已完成列表重建和标题同步。\n备份目录：{5}", restored, skipped, named, assetText, backfill, backup);
            }
            finally { try { Directory.Delete(temp, true); } catch { } }
        }

        static ArtifactRestoreResult RestoreArtifacts(string extractedRoot, string codexHome, Manifest manifest)
        {
            return RestoreArtifacts(extractedRoot, codexHome, manifest, null);
        }

        static ArtifactRestoreResult RestoreArtifacts(string extractedRoot, string codexHome, Manifest manifest, Action<string> progress)
        {
            var result = new ArtifactRestoreResult();
            if (manifest.format_version < 3 || manifest.artifacts == null || manifest.artifacts.Count == 0) return result;
            if (progress != null) progress("正在恢复关联文件…");
            DateTime created = ParseDate(manifest.created_at_utc); if (created == DateTime.MinValue) created = DateTime.UtcNow;
            string manifestFile = Path.Combine(extractedRoot, "manifest.json");
            string packageTag = File.Exists(manifestFile) ? "-" + Sha256(manifestFile).Substring(0, 8) : "";
            string restoreRoot = Path.Combine(codexHome, "migrated-files", "pack-" + created.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + packageTag);
            Directory.CreateDirectory(restoreRoot); string rootPrefix = Path.GetFullPath(restoreRoot) + Path.DirectorySeparatorChar;
            int artifactIndex = 0;
            foreach (ArtifactItem item in manifest.artifacts)
            {
                artifactIndex++;
                if (item == null || string.IsNullOrWhiteSpace(item.archive_path) || !item.archive_path.StartsWith("artifacts/", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(item.source_path) || !Path.IsPathRooted(item.source_path)) throw new InvalidDataException("迁移包包含无效关联文件记录。");
                string source = Path.GetFullPath(Path.Combine(extractedRoot, item.archive_path.Replace('/', Path.DirectorySeparatorChar)));
                string extractedPrefix = Path.GetFullPath(extractedRoot) + Path.DirectorySeparatorChar;
                if (!source.StartsWith(extractedPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(source) || new FileInfo(source).Length != item.size || !Sha256(source).Equals(item.sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("关联文件校验失败：" + item.archive_path);
                string relative;
                if (manifest.format_version >= 4)
                {
                    if (string.IsNullOrWhiteSpace(item.restore_path)) throw new InvalidDataException("迁移包缺少关联文件恢复路径。");
                    relative = item.restore_path.Replace('/', Path.DirectorySeparatorChar);
                }
                else relative = item.archive_path.Substring("artifacts/".Length).Replace('/', Path.DirectorySeparatorChar);
                string destination = Path.GetFullPath(Path.Combine(restoreRoot, relative));
                if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("迁移包含不安全的关联文件路径。");
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                if (File.Exists(destination))
                {
                    if (!Sha256(destination).Equals(item.sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("恢复目录中存在不同内容的同名文件：" + destination);
                }
                else File.Copy(source, destination, false);
                DateTime modified = ParseDate(item.last_write_utc); if (modified != DateTime.MinValue) try { File.SetLastWriteTimeUtc(destination, modified); } catch { }
                result.PathMap[NormalizePath(item.source_path)] = destination; result.Count++;
                if (progress != null && (artifactIndex == manifest.artifacts.Count || artifactIndex % 10 == 0)) progress(string.Format("正在恢复关联文件：{0}/{1}", artifactIndex, manifest.artifacts.Count));
            }
            if (progress != null) progress(string.Format("正在建立关联文件路径映射（{0} 个文件）…", result.Count));
            if (manifest.workspaces != null) foreach (WorkspaceItem workspace in manifest.workspaces)
            {
                if (workspace == null || string.IsNullOrWhiteSpace(workspace.source_root)) continue;
                string relative;
                if (manifest.format_version >= 4) { if (string.IsNullOrWhiteSpace(workspace.restore_root)) continue; relative = workspace.restore_root.Replace('/', Path.DirectorySeparatorChar); }
                else { if (string.IsNullOrWhiteSpace(workspace.archive_root) || !workspace.archive_root.StartsWith("artifacts/", StringComparison.Ordinal)) continue; relative = workspace.archive_root.Substring("artifacts/".Length).Replace('/', Path.DirectorySeparatorChar); }
                string destination = Path.GetFullPath(Path.Combine(restoreRoot, relative));
                if (destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) result.PathMap[NormalizePath(workspace.source_root)] = destination;
            }
            string checkFile = Path.Combine(restoreRoot, "迁移文件说明.txt");
            if (!File.Exists(checkFile)) File.WriteAllText(checkFile, "这些文件由 Codex 对话迁移工具从另一台电脑恢复。\r\n对话记录中的原绝对路径已改写到此目录。\r\n请勿移动本目录，否则对话中的旧文件链接可能再次失效。\r\n", new UTF8Encoding(false));
            result.Root = restoreRoot;
            if (progress != null) progress(string.Format("关联文件恢复完成：{0} 个", result.Count));
            return result;
        }

        static void RewriteSessionPaths(string sessionPath, Dictionary<string, string> pathMap)
        {
            if (pathMap == null || pathMap.Count == 0) return;
            string original;
            using (var fs = OpenLongFile(sessionPath, false)) using (var sr = new StreamReader(fs, Encoding.UTF8, true)) original = sr.ReadToEnd();
            string updated = original;
            foreach (var pair in pathMap.OrderByDescending(x => x.Key.Length))
            {
                string oldPath = pair.Key, newPath = pair.Value;
                updated = updated.Replace(JsonEscaped(oldPath), JsonEscaped(newPath));
                updated = updated.Replace(oldPath.Replace('\\', '/'), newPath.Replace('\\', '/'));
                updated = updated.Replace(oldPath, newPath);
            }
            if (string.Equals(original, updated, StringComparison.Ordinal)) return;
            string temporary = sessionPath + ".rw.tmp";
            if (LongFileExists(temporary)) DeleteFile(ToLongPath(temporary));
            byte[] rewritten = new UTF8Encoding(false).GetBytes(updated);
            using (var output = OpenLongFile(temporary, true)) output.Write(rewritten, 0, rewritten.Length);
            try { if (!MoveFileEx(ToLongPath(temporary), ToLongPath(sessionPath), 9)) throw new IOException("无法替换改写后的会话文件，Windows 错误码：" + Marshal.GetLastWin32Error()); }
            finally { if (LongFileExists(temporary)) try { DeleteFile(ToLongPath(temporary)); } catch { } }
        }

        static string JsonEscaped(string value)
        {
            string encoded = Json.Serialize(value); return encoded.Length >= 2 ? encoded.Substring(1, encoded.Length - 2) : value;
        }

        public static string RepairSidebarIndex(string codexHome)
        {
            if (!Directory.Exists(Path.Combine(codexHome, "sessions"))) throw new DirectoryNotFoundException("没有找到 sessions 目录。");
            string backup = Path.Combine(codexHome, "migration-backups", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            int renamed = 0;
            foreach (string file in Directory.GetFiles(Path.Combine(codexHome, "sessions"), "rollout-restored-*.jsonl", SearchOption.AllDirectories))
            {
                string id = ExtractId(Path.GetFileName(file)); if (id == null) continue;
                DateTime dt = ParseDate(ReadSessionTimestamp(file)); if (dt == DateTime.MinValue) dt = File.GetCreationTimeUtc(file);
                string dest = Path.Combine(Path.GetDirectoryName(file), CanonicalRolloutName(null, id, dt.ToLocalTime()));
                if (!File.Exists(dest)) { File.Move(file, dest); renamed++; }
            }
            string result = ScheduleBackfill(codexHome, backup);
            int named = SkipAppServerForTests ? 0 : SyncTitlesViaOfficialAppServer(codexHome, LoadIndexTitles(Path.Combine(codexHome, "session_index.jsonl")));
            return string.Format("修复完成：规范化 {0} 个旧恢复文件，恢复标题 {1} 个。\n{2}\n现在重新打开 Codex 即可。\n备份目录：{3}", renamed, named, result, backup);
        }

        public static int SyncTitlesFromIndex(string codexHome)
        {
            return SyncTitlesViaOfficialAppServer(codexHome, LoadIndexTitles(Path.Combine(codexHome, "session_index.jsonl")));
        }

        static Dictionary<string, string> LoadIndexTitles(string indexPath)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(indexPath)) return result;
            foreach (string line in ReadLinesShared(indexPath))
            {
                try
                {
                    var row = Json.Deserialize<Dictionary<string, object>>(line); string id = Get(row, "id"), name = Get(row, "thread_name");
                    if (IsId(id) && !string.IsNullOrWhiteSpace(name)) result[id] = name;
                }
                catch { }
            }
            return result;
        }

        static int SyncTitlesViaOfficialAppServer(string codexHome, Dictionary<string, string> titles)
        {
            if (titles.Count == 0) return 0;
            string exe = FindCodexExe();
            if (exe == null) throw new FileNotFoundException("未找到 Codex 官方 codex.exe，无法同步原始标题。请确认目标电脑已安装 Codex。");
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "app-server --listen stdio://",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            psi.EnvironmentVariables["CODEX_HOME"] = Path.GetFullPath(codexHome);
            var errors = new StringBuilder();
            using (var process = new Process { StartInfo = psi })
            {
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (!string.IsNullOrEmpty(e.Data) && errors.Length < 4000) errors.AppendLine(e.Data); };
                if (!process.Start()) throw new InvalidOperationException("无法启动 Codex App Server。");
                process.BeginErrorReadLine();
                try
                {
                    int requestId = 1;
                    SendRpc(process, new Dictionary<string, object> { { "method", "initialize" }, { "id", requestId }, { "params", new Dictionary<string, object> { { "clientInfo", new Dictionary<string, object> { { "name", "codex_conversation_migrator" }, { "title", "Codex Conversation Migrator" }, { "version", "2.8" } } } } } });
                    WaitRpc(process, requestId);
                    SendRpc(process, new Dictionary<string, object> { { "method", "initialized" }, { "params", new Dictionary<string, object>() } });
                    process.StandardInput.BaseStream.Close();
                    if (!process.WaitForExit(10000)) { try { process.Kill(); } catch { } }
                }
                catch
                {
                    try { process.StandardInput.BaseStream.Close(); } catch { }
                    if (!process.HasExited) try { process.Kill(); } catch { }
                    throw;
                }
            }
            string sqliteHome = Environment.GetEnvironmentVariable("CODEX_SQLITE_HOME");
            if (string.IsNullOrWhiteSpace(sqliteHome)) sqliteHome = codexHome;
            string db = Path.Combine(sqliteHome, "state_5.sqlite");
            if (!File.Exists(db)) throw new FileNotFoundException("Codex App Server 未生成状态数据库。", db);
            var valid = titles.Where(x => IsId(x.Key) && !string.IsNullOrWhiteSpace(x.Value)).ToList();
            var sql = new StringBuilder("BEGIN IMMEDIATE; UPDATE threads SET name=CASE id ");
            foreach (var pair in valid) sql.Append("WHEN ").Append(SqlText(pair.Key)).Append(" THEN ").Append(SqlText(pair.Value)).Append(' ');
            sql.Append("ELSE name END WHERE id IN (").Append(string.Join(",", valid.Select(x => SqlText(x.Key)).ToArray())).Append("); COMMIT; PRAGMA wal_checkpoint(TRUNCATE);");
            SqliteNative.Execute(db, sql.ToString());
            string conditions = string.Join(" OR ", valid.Select(x => "(id=" + SqlText(x.Key) + " AND name=" + SqlText(x.Value) + ")").ToArray());
            string countText = SqliteNative.Scalar(db, "SELECT count(*) FROM threads WHERE " + conditions + ";");
            int success; if (!int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out success) || success == 0) throw new InvalidOperationException("列表已重建，但没有匹配到可恢复标题的对话记录。" + (errors.Length > 0 ? "\n" + errors.ToString() : ""));
            string check = SqliteNative.Scalar(db, "PRAGMA quick_check;");
            if (!string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("标题同步后的数据库完整性检查未通过：" + check);
            return success;
        }

        static string SqlText(string value) { return "'" + (value ?? "").Replace("\0", "").Replace("'", "''") + "'"; }

        static void SendRpc(Process process, Dictionary<string, object> message)
        {
            byte[] data = Encoding.UTF8.GetBytes(Json.Serialize(message) + "\n");
            process.StandardInput.BaseStream.Write(data, 0, data.Length); process.StandardInput.BaseStream.Flush();
        }

        static void WaitRpc(Process process, int expectedId)
        {
            while (true)
            {
                bool timedOut = false;
                using (var timer = new System.Threading.Timer(delegate { timedOut = true; try { if (!process.HasExited) process.Kill(); } catch { } }, null, 30000, System.Threading.Timeout.Infinite))
                {
                    string line = process.StandardOutput.ReadLine();
                    if (line == null) { if (timedOut) throw new TimeoutException("等待 Codex App Server 响应超时。"); throw new EndOfStreamException("Codex App Server 提前退出。"); }
                    Dictionary<string, object> response;
                    try { response = Json.Deserialize<Dictionary<string, object>>(line); } catch { continue; }
                    object idObject; if (!response.TryGetValue("id", out idObject) || idObject == null || Convert.ToInt32(idObject, CultureInfo.InvariantCulture) != expectedId) continue;
                    object error; if (response.TryGetValue("error", out error) && error != null) throw new InvalidOperationException("Codex App Server 返回错误：" + Json.Serialize(error));
                    return;
                }
            }
        }

        static string FindCodexExe()
        {
            var candidates = new List<string>();
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string bin = Path.Combine(local, "OpenAI", "Codex", "bin");
            if (Directory.Exists(bin)) try { candidates.AddRange(Directory.GetFiles(bin, "codex.exe", SearchOption.AllDirectories)); } catch { }
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string folder in path.Split(Path.PathSeparator)) try { string file = Path.Combine(folder.Trim(), "codex.exe"); if (File.Exists(file)) candidates.Add(file); } catch { }
            string plugin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "plugins", ".plugin-appserver", "codex.exe");
            if (File.Exists(plugin)) candidates.Add(plugin);
            return candidates.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(x => { try { return File.GetLastWriteTimeUtc(x); } catch { return DateTime.MinValue; } }).FirstOrDefault();
        }

        static string ScheduleBackfill(string codexHome, string backup)
        {
            string sqliteHome = Environment.GetEnvironmentVariable("CODEX_SQLITE_HOME");
            if (string.IsNullOrWhiteSpace(sqliteHome)) sqliteHome = codexHome;
            string db = Path.Combine(sqliteHome, "state_5.sqlite");
            if (!File.Exists(db)) return "目标端尚无状态数据库，Codex 首次启动时会自动建立索引。";
            Directory.CreateDirectory(backup);
            BackupFile(db, Path.Combine(backup, "state_5.sqlite.bak"));
            BackupFile(db + "-wal", Path.Combine(backup, "state_5.sqlite-wal.bak"));
            BackupFile(db + "-shm", Path.Combine(backup, "state_5.sqlite-shm.bak"));
            string check = SqliteNative.Scalar(db, "PRAGMA quick_check;");
            if (!string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Codex 状态数据库完整性检查未通过：" + check);
            try
            {
                SqliteNative.Execute(db, "BEGIN IMMEDIATE; INSERT INTO backfill_state(id,status,last_watermark,last_success_at,updated_at) VALUES(1,'pending',NULL,NULL,CAST(strftime('%s','now') AS INTEGER)) ON CONFLICT(id) DO UPDATE SET status='pending',last_watermark=NULL,last_success_at=NULL,updated_at=excluded.updated_at; COMMIT; PRAGMA wal_checkpoint(TRUNCATE);");
            }
            catch { try { SqliteNative.Execute(db, "ROLLBACK;"); } catch { } throw; }
            check = SqliteNative.Scalar(db, "PRAGMA quick_check;");
            if (!string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新后的数据库完整性检查未通过：" + check);
            string status = SqliteNative.Scalar(db, "SELECT status FROM backfill_state WHERE id=1;");
            if (!string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("未能启用 Codex 启动回填。");
            return "已通过事务启用 Codex 官方启动回填。";
        }

        static void BackupFile(string source, string destination) { if (File.Exists(source)) File.Copy(source, destination, true); }
        static string CanonicalRolloutName(string candidate, string id, DateTime local)
        {
            string pattern = "^rollout-\\d{4}-\\d{2}-\\d{2}T\\d{2}-\\d{2}-\\d{2}(?:-[^-]+)*-" + Regex.Escape(id) + "\\.jsonl$";
            if (!string.IsNullOrWhiteSpace(candidate) && Path.GetFileName(candidate) == candidate && Regex.IsMatch(candidate, pattern, RegexOptions.IgnoreCase)) return candidate;
            return "rollout-" + local.ToString("yyyy-MM-ddTHH-mm-ss") + "-" + id + ".jsonl";
        }

        static string LineageRolloutName(string candidate, string id)
        {
            if (string.IsNullOrWhiteSpace(candidate) || Path.GetFileName(candidate) != candidate || !candidate.StartsWith("rollout-", StringComparison.OrdinalIgnoreCase) || !candidate.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) || candidate.IndexOf(id, StringComparison.OrdinalIgnoreCase) < 0 || candidate.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new InvalidDataException("迁移包包含无效历史链文件名。");
            return candidate;
        }

        static string ReadSessionTimestamp(string path)
        {
            foreach (string line in ReadLinesShared(path).Take(20))
            {
                try
                {
                    var root = Json.Deserialize<Dictionary<string, object>>(line); if (Get(root, "type") != "session_meta") continue;
                    object payloadObject; if (!root.TryGetValue("payload", out payloadObject)) continue;
                    var payload = payloadObject as Dictionary<string, object>; string value = Get(payload, "timestamp");
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
                catch { }
            }
            return "";
        }

        static string ReadSessionId(string path)
        {
            foreach (string line in ReadLinesShared(path).Take(30))
            {
                try
                {
                    var root = Json.Deserialize<Dictionary<string, object>>(line); if (Get(root, "type") != "session_meta") continue;
                    object payloadObject; if (!root.TryGetValue("payload", out payloadObject)) continue;
                    var payload = payloadObject as Dictionary<string, object>; string value = Get(payload, "id");
                    if (string.IsNullOrWhiteSpace(value)) value = Get(payload, "session_id");
                    if (IsId(value)) return value;
                }
                catch { }
            }
            return "";
        }

        static IEnumerable<string> ReadLinesShared(string path)
        {
            using (var fs = OpenLongFile(path, false))
            using (var sr = new StreamReader(fs, Encoding.UTF8, true)) { string line; while ((line = sr.ReadLine()) != null) yield return line; }
        }
        static void CopyShared(string source, string dest)
        {
            try { using (var a = OpenLongFile(source, false)) using (var b = OpenLongFile(dest, true)) a.CopyTo(b); }
            catch (Exception ex) { throw new IOException("复制文件失败：" + FromLongPath(source) + "\n目标：" + FromLongPath(dest), ex); }
        }
        static string Get(Dictionary<string, object> d, string key) { object v; return d != null && d.TryGetValue(key, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : ""; }
        static DateTime ParseDate(string s) { DateTime d; return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out d) ? d : DateTime.MinValue; }
        static bool IsId(string s) { Guid g; return Guid.TryParse(s, out g); }
        static string ExtractId(string name) { for (int i = 0; i + 36 <= name.Length; i++) { string x = name.Substring(i, 36); if (IsId(x)) return x; } return null; }
        static string Sha256(string path)
        {
            try { using (var h = SHA256.Create()) using (var f = OpenLongFile(path, false)) return BitConverter.ToString(h.ComputeHash(f)).Replace("-", "").ToLowerInvariant(); }
            catch (Exception ex) { throw new IOException("计算文件校验值失败：" + FromLongPath(path), ex); }
        }
        static HashSet<string> ExistingIds(string indexPath) { var r = new HashSet<string>(StringComparer.OrdinalIgnoreCase); if (File.Exists(indexPath)) foreach (string line in ReadLinesShared(indexPath)) try { var d = Json.Deserialize<Dictionary<string, object>>(line); string id = Get(d, "id"); if (IsId(id)) r.Add(id); } catch { } return r; }
        static IEnumerable<string> FindSessions(string home, string id) { string p = Path.Combine(home, "sessions"); return Directory.Exists(p) ? Directory.GetFiles(p, "*" + id + "*.jsonl", SearchOption.AllDirectories).Where(x => string.Equals(ReadSessionId(x), id, StringComparison.OrdinalIgnoreCase)) : Enumerable.Empty<string>(); }
        static string FindSession(string home, string id) { return FindSessions(home, id).FirstOrDefault(); }
        static void SafeExtract(string zip, string destination)
        {
            string root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
            using (var z = ZipFile.OpenRead(zip)) foreach (var e in z.Entries)
            {
                string target = Path.GetFullPath(Path.Combine(destination, e.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("迁移包包含不安全路径。");
                if (string.IsNullOrEmpty(e.Name)) Directory.CreateDirectory(target); else { Directory.CreateDirectory(Path.GetDirectoryName(target)); e.ExtractToFile(target, true); }
            }
        }
    }

    enum CodexCloseChoice { Cancel, CloseOnly, CloseAndRestart }

    class CodexCloseDialog : Form
    {
        public CodexCloseChoice Choice = CodexCloseChoice.Cancel;
        public CodexCloseDialog(string processText)
        {
            Text = "请先退出 Codex"; Width = 620; Height = 230; StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false; Font = new Font("Microsoft YaHei UI", 9F);
            var icon = new PictureBox { Image = SystemIcons.Warning.ToBitmap(), SizeMode = PictureBoxSizeMode.AutoSize, Location = new Point(20, 28) };
            var message = new Label { AutoSize = false, Location = new Point(72, 20), Size = new Size(520, 100), Text = "检测到 Codex 仍在运行：\r\n" + processText + "\r\n\r\n请选择关闭方式。Chrome 插件后台不会被关闭。" };
            var close = new Button { Text = "关闭 Codex", Size = new Size(125, 34), Location = new Point(174, 140) };
            var restart = new Button { Text = "关闭并在完成后重启", Size = new Size(185, 34), Location = new Point(307, 140) };
            var cancel = new Button { Text = "取消", Size = new Size(80, 34), Location = new Point(500, 140), DialogResult = DialogResult.Cancel };
            close.Click += delegate { Choice = CodexCloseChoice.CloseOnly; DialogResult = DialogResult.OK; Close(); };
            restart.Click += delegate { Choice = CodexCloseChoice.CloseAndRestart; DialogResult = DialogResult.OK; Close(); };
            CancelButton = cancel; Controls.Add(icon); Controls.Add(message); Controls.Add(close); Controls.Add(restart); Controls.Add(cancel);
        }
    }

    class ProgressForm : Form
    {
        readonly Label status = new Label();
        public ProgressForm(string title)
        {
            Text = title; Width = 520; Height = 145; StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ControlBox = false; ShowInTaskbar = false; Font = new Font("Microsoft YaHei UI", 9F);
            status.Text = "正在准备…"; status.AutoEllipsis = true; status.SetBounds(18, 18, 470, 42);
            var bar = new ProgressBar { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 24 }; bar.SetBounds(18, 68, 470, 20);
            Controls.Add(status); Controls.Add(bar);
        }
        public void SetStatus(string text)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((MethodInvoker)delegate { if (!IsDisposed) status.Text = text; }); } catch { }
        }
    }

    class MainForm : Form
    {
        readonly ListView list = new ListView();
        readonly Label pathLabel = new Label();
        string codexHome;
        public MainForm()
        {
            Text = "Codex 会话选择迁移工具 v3.4"; Width = 920; Height = 600; StartPosition = FormStartPosition.CenterScreen; Font = new Font("Microsoft YaHei UI", 9F); MinimumSize = new Size(760, 450);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0), Padding = new Padding(0) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 78F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58F));
            Controls.Add(layout);
            var top = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12), Margin = new Padding(0) };
            var title = new Label { Text = "Codex 会话选择迁移工具 v3.4", Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold), AutoSize = true, Location = new Point(12, 8) };
            pathLabel.AutoSize = true; pathLabel.Location = new Point(14, 47); pathLabel.ForeColor = Color.DimGray;
            top.Controls.Add(title); top.Controls.Add(pathLabel); layout.Controls.Add(top, 0, 0);
            list.Dock = DockStyle.Fill; list.Margin = new Padding(0); list.View = View.Details; list.CheckBoxes = true; list.FullRowSelect = true; list.GridLines = true; list.HideSelection = false; list.ShowItemToolTips = true;
            list.Columns.Add("会话标题（勾选需要迁移的会话）", 350); list.Columns.Add("状态", 65); list.Columns.Add("更新时间", 155); list.Columns.Add("大小", 85); list.Columns.Add("会话 ID", 245); layout.Controls.Add(list, 0, 1);
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0), WrapContents = false, AutoScroll = true };
            bottom.Controls.Add(Button("刷新列表", delegate { RefreshList(); }));
            bottom.Controls.Add(Button("全选", delegate { foreach (ListViewItem x in list.Items) x.Checked = true; }));
            bottom.Controls.Add(Button("导出勾选的会话…", ExportClicked));
            bottom.Controls.Add(Button("从迁移包恢复…", RestoreClicked));
            bottom.Controls.Add(Button("修复列表和标题", RepairClicked));
            bottom.Controls.Add(Button("更换 Codex 数据目录…", ChangeHome)); layout.Controls.Add(bottom, 0, 2);
            list.Resize += delegate { ResizeColumns(); };
            codexHome = MigrationCore.DefaultCodexHome(); Shown += delegate { RefreshList(); };
        }
        Button Button(string text, EventHandler click) { var b = new Button { Text = text, AutoSize = true, Height = 32, Margin = new Padding(4) }; b.Click += click; return b; }
        void RefreshList()
        {
            try { pathLabel.Text = "当前电脑会话数据：" + codexHome; list.BeginUpdate(); list.Items.Clear(); foreach (var c in MigrationCore.Load(codexHome)) { var i = new ListViewItem(c.Name); DateTime d; i.SubItems.Add(c.Pinned ? "置顶" : ""); i.SubItems.Add(DateTime.TryParse(c.Updated, out d) ? d.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : c.Updated); i.SubItems.Add(FormatSize(c.Size)); i.SubItems.Add(c.Id); i.Tag = c; i.ToolTipText = (c.Pinned ? "置顶会话\n" : "") + c.Name + "\n" + c.Id; list.Items.Add(i); } list.EndUpdate(); ResizeColumns(); Text = "Codex 会话选择迁移工具 v3.4（去重后 " + list.Items.Count + " 个会话）"; } catch (Exception ex) { list.EndUpdate(); MessageBox.Show(ex.Message, "读取失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
        void ResizeColumns() { if (list.Columns.Count != 5) return; int fixedWidth = 65 + 155 + 85 + 245; list.Columns[0].Width = Math.Max(180, list.ClientSize.Width - fixedWidth - 5); }
        void ExportClicked(object sender, EventArgs e)
        {
            var selected = list.Items.Cast<ListViewItem>().Where(x => x.Checked).Select(x => (Conversation)x.Tag).ToList(); if (selected.Count == 0) { MessageBox.Show("请先勾选要导出的对话。"); return; }
            DialogResult mode = MessageBox.Show("是否同时打包这些对话的关联文件？\n\n“是”：包含对话工作目录，以及记录中仍然存在的外部附件。恢复时会放入目标电脑的 .codex\\migrated-files，并自动改写对话中的旧路径。\n“否”：只导出对话记录。\n\n.git、node_modules、缓存和常见密钥文件不会打包。", "选择导出内容", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (mode == DialogResult.Cancel) return; bool includeFiles = mode == DialogResult.Yes;
            using (var d = new SaveFileDialog { Filter = "Codex 迁移包 (*.codexpack.zip)|*.codexpack.zip|ZIP 文件 (*.zip)|*.zip", FileName = "Codex对话-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".codexpack.zip" })
            {
                if (d.ShowDialog() != DialogResult.OK) return;
                Exception failure = null; string lastStatus = "正在准备…"; int warnings = 0;
                using (var progress = new ProgressForm("正在导出 Codex 对话"))
                using (var worker = new BackgroundWorker())
                {
                    worker.DoWork += delegate(object workSender, DoWorkEventArgs work) { work.Result = MigrationCore.Export(codexHome, selected, d.FileName, includeFiles, delegate(string state) { lastStatus = state; progress.SetStatus(state); }); };
                    worker.RunWorkerCompleted += delegate(object completedSender, RunWorkerCompletedEventArgs completed) { failure = completed.Error; if (failure == null && completed.Result != null) warnings = (int)completed.Result; progress.Close(); };
                    progress.Shown += delegate { worker.RunWorkerAsync(); };
                    progress.ShowDialog(this);
                }
                if (failure != null)
                {
                    string log = WriteExportErrorLog(d.FileName, lastStatus, failure);
                    MessageBox.Show(failure.Message + (log.Length > 0 ? "\n\n详细错误日志：\n" + log : ""), "导出失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                else MessageBox.Show("已导出 " + selected.Count + " 个对话" + (includeFiles ? "及关联文件" : "") + "：\n" + d.FileName + (warnings > 0 ? "\n\n已跳过 " + warnings + " 个被占用、无权限或非关键临时文件；详情见迁移包内的《导出警告.txt》。" : ""), "导出完成", MessageBoxButtons.OK, warnings > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            }
        }
        static string WriteExportErrorLog(string outputPath, string stage, Exception error)
        {
            try
            {
                string folder = Path.GetDirectoryName(outputPath); if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) folder = AppDomain.CurrentDomain.BaseDirectory;
                string log = Path.Combine(folder, "Codex迁移导出错误-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
                File.WriteAllText(log, "Codex 会话选择迁移工具 v3.4\r\n时间：" + DateTime.Now.ToString("o") + "\r\n阶段：" + stage + "\r\n输出：" + outputPath + "\r\n\r\n" + error, new UTF8Encoding(false)); return log;
            }
            catch { return ""; }
        }
        void RestoreClicked(object sender, EventArgs e)
        {
            using (var d = new OpenFileDialog { Filter = "Codex 迁移包 (*.zip)|*.zip" }) if (d.ShowDialog() == DialogResult.OK)
            {
                if (MessageBox.Show("将恢复对话、关联文件和左侧列表。\n关联文件会保存到 .codex\\migrated-files；若同 ID 对话已存在，只会备份后更新其中的迁移文件路径，不覆盖对话内容。\n工具不会导入登录凭据。\n\n继续吗？", "确认恢复", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                bool? restart; try { restart = PrepareCodexForOperation(); } catch (Exception ex) { MessageBox.Show(ex.Message, "关闭 Codex 失败", MessageBoxButtons.OK, MessageBoxIcon.Error); return; } if (!restart.HasValue) return;
                string result = null; Exception failure = null; string lastStatus = "正在准备…";
                try
                {
                    using (var progress = new ProgressForm("正在恢复 Codex 对话"))
                    using (var worker = new BackgroundWorker())
                    {
                        worker.DoWork += delegate(object workSender, DoWorkEventArgs work) { work.Result = MigrationCore.Restore(d.FileName, codexHome, delegate(string state) { lastStatus = state; progress.SetStatus(state); }); };
                        worker.RunWorkerCompleted += delegate(object completedSender, RunWorkerCompletedEventArgs completed) { failure = completed.Error; if (failure == null && completed.Result != null) result = (string)completed.Result; progress.Close(); };
                        progress.Shown += delegate { worker.RunWorkerAsync(); };
                        progress.ShowDialog(this);
                    }
                }
                catch (Exception ex) { failure = ex; }
                finally { if (restart.Value) TryRestartCodex(); }
                if (failure == null) { RefreshList(); MessageBox.Show(result, "恢复结果", MessageBoxButtons.OK, MessageBoxIcon.Information); }
                else MessageBox.Show(failure.Message + "\n\n失败阶段：" + lastStatus, "恢复失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        void RepairClicked(object sender, EventArgs e)
        {
            if (MessageBox.Show("此操作会备份状态数据库，调用 Codex 官方 App Server 重建左侧列表，并从迁移索引恢复原始标题。\n适用于“工具中可见但左侧不可见”或标题变成 Files mentioned 的对话。\n\n继续吗？", "修复列表和标题", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            bool? restart; try { restart = PrepareCodexForOperation(); } catch (Exception ex) { MessageBox.Show(ex.Message, "关闭 Codex 失败", MessageBoxButtons.OK, MessageBoxIcon.Error); return; } if (!restart.HasValue) return;
            string result = null; Exception failure = null;
            try { result = MigrationCore.RepairSidebarIndex(codexHome); RefreshList(); } catch (Exception ex) { failure = ex; }
            finally { if (restart.Value) TryRestartCodex(); }
            if (failure == null) MessageBox.Show(result, "修复完成", MessageBoxButtons.OK, MessageBoxIcon.Information); else MessageBox.Show(failure.Message, "修复失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        bool? PrepareCodexForOperation()
        {
            List<Process> active = FindActiveCodexProcesses(); if (active.Count == 0) return false;
            string processText = string.Join("\r\n", active.Select(x => x.ProcessName + " (PID " + x.Id + ")").Distinct().Take(8).ToArray());
            CodexCloseChoice choice; using (var dialog = new CodexCloseDialog(processText)) { dialog.ShowDialog(this); choice = dialog.Choice; }
            if (choice == CodexCloseChoice.Cancel) return null;
            StopCodexProcesses(active);
            return choice == CodexCloseChoice.CloseAndRestart;
        }
        static List<Process> FindActiveCodexProcesses()
        {
            int current = Process.GetCurrentProcess().Id; var active = new List<Process>();
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    string n = p.ProcessName.ToLowerInvariant(); if (p.Id == current) continue;
                    if (n != "codex" && n != "codex-code-mode-host" && n != "chatgpt") continue;
                    string executable = ""; try { executable = p.MainModule == null ? "" : (p.MainModule.FileName ?? ""); } catch { if (n == "codex") active.Add(p); continue; }
                    if (executable.IndexOf("\\.codex\\plugins\\", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (n == "chatgpt" && executable.IndexOf("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) < 0 && executable.IndexOf("\\OpenAI\\Codex\\", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    active.Add(p);
                }
                catch { }
            }
            return active;
        }
        static void StopCodexProcesses(List<Process> active)
        {
            foreach (Process p in active.OrderBy(x => x.ProcessName.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase) ? 0 : 1)) try { if (!p.HasExited) p.CloseMainWindow(); } catch { }
            System.Threading.Thread.Sleep(700);
            foreach (Process p in active) try { if (!p.HasExited) p.Kill(); } catch (Exception ex) { throw new InvalidOperationException("无法结束 " + p.ProcessName + " (PID " + p.Id + ")：" + ex.Message, ex); }
            DateTime limit = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < limit)
            {
                var remainingNow = FindActiveCodexProcesses(); if (remainingNow.Count == 0) return;
                foreach (Process p in remainingNow) try { if (!p.HasExited) p.Kill(); } catch { }
                System.Threading.Thread.Sleep(100);
            }
            var remaining = FindActiveCodexProcesses(); if (remaining.Count > 0) throw new InvalidOperationException("仍有 Codex 进程无法关闭：\n" + string.Join("\n", remaining.Select(x => x.ProcessName + " (PID " + x.Id + ")").ToArray()));
        }
        void TryRestartCodex()
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = "shell:AppsFolder\\OpenAI.Codex_2p2nqsd0c76g0!App", UseShellExecute = true });
            }
            catch (Exception ex) { MessageBox.Show("任务已完成，但自动重启 Codex 失败：\n" + ex.Message + "\n\n请手动启动 Codex。", "无法重启 Codex", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
        void ChangeHome(object sender, EventArgs e) { using (var d = new FolderBrowserDialog { Description = "选择 Codex 数据目录（通常是用户目录下的 .codex）", SelectedPath = codexHome }) if (d.ShowDialog() == DialogResult.OK) { codexHome = d.SelectedPath; RefreshList(); } }
        static string FormatSize(long n) { if (n >= 1048576) return (n / 1048576d).ToString("0.0") + " MB"; if (n >= 1024) return (n / 1024d).ToString("0.0") + " KB"; return n + " B"; }
    }

    static class Program
    {
        [STAThread] static void Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--self-test") { Environment.Exit(SelfTest()); return; }
            if (args.Length == 2 && args[0] == "--sync-home") { try { Environment.Exit(MigrationCore.SyncTitlesFromIndex(args[1]) > 0 ? 0 : 20); } catch (Exception ex) { try { File.WriteAllText(Path.Combine(args[1], "sync-error.txt"), ex.ToString(), Encoding.UTF8); } catch { } Environment.Exit(21); } return; }
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); Application.Run(new MainForm());
        }
        static int SelfTest()
        {
            string root = Path.Combine(Path.GetTempPath(), "codex-migrator-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                MigrationCore.SkipAppServerForTests = true;
                string source = Path.Combine(root, "source"), target = Path.Combine(root, "target"), day = Path.Combine(source, "sessions", "2026", "08", "27"), workspace = Path.Combine(root, "project"), attachment = Path.Combine(root, "attached image.png"), busyFile = Path.Combine(root, "busy-artifact.txt");
                Directory.CreateDirectory(day);
                string id = "11111111-2222-3333-4444-555555555555";
                Directory.CreateDirectory(Path.Combine(workspace, "outputs")); string generated = Path.Combine(workspace, "outputs", "报告.txt"); File.WriteAllText(generated, "generated", Encoding.UTF8); File.WriteAllText(attachment, "attached", Encoding.UTF8); File.WriteAllText(busyFile, "busy", Encoding.UTF8);
                string deepFolder = Path.Combine(workspace, new string('a', 45), new string('b', 45), new string('c', 45)); Directory.CreateDirectory(deepFolder); string deepFile = Path.Combine(deepFolder, "deep-result.txt"); File.WriteAllText(deepFile, "deep", Encoding.UTF8);
                string escapedWorkspace = workspace.Replace("\\", "\\\\"), escapedGenerated = generated.Replace("\\", "\\\\"), escapedAttachment = attachment.Replace("\\", "\\\\"), escapedBusy = busyFile.Replace("\\", "\\\\");
                string rollout = Path.Combine(day, "rollout-2026-08-27T09-00-00-" + id + ".jsonl");
                File.WriteAllText(rollout, "{\"timestamp\":\"2026-08-27T01:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"" + id + "\",\"timestamp\":\"2026-08-27T01:00:00Z\",\"cwd\":\"" + escapedWorkspace + "\",\"originator\":\"test\",\"cli_version\":\"test\",\"source\":\"vscode\",\"model_provider\":\"openai\"}}\n{\"timestamp\":\"2026-08-27T01:01:00Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"生成文件：" + escapedGenerated + "\\n附件：" + escapedAttachment + "\\n占用文件：" + escapedBusy + "\"}]}}\n", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(source, "session_index.jsonl"), "{\"id\":\"" + id + "\",\"thread_name\":\"测试对话\",\"updated_at\":\"2026-08-27T01:00:00Z\"}\n", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(source, ".codex-global-state.json"), "{\"pinned-thread-ids\":[\"" + id + "\"]}", new UTF8Encoding(false));
                string continuation = Path.Combine(day, "rollout-2026-08-27T10-00-00-" + id + "_aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee.jsonl");
                File.Copy(rollout, continuation); File.AppendAllText(continuation, "{\"timestamp\":\"2026-08-27T02:00:00Z\",\"type\":\"event_msg\",\"payload\":{}}\n", new UTF8Encoding(false)); File.SetLastWriteTimeUtc(continuation, DateTime.UtcNow.AddMinutes(1));
                var conversations = MigrationCore.Load(source);
                if (conversations.Count != 1 || conversations[0].Name != "测试对话" || !conversations[0].Pinned || conversations[0].FilePath != continuation || DateTime.Parse(conversations[0].Updated).ToUniversalTime() != new DateTime(2026, 8, 27, 2, 0, 0, DateTimeKind.Utc)) return 11;
                Directory.CreateDirectory(target); string stateDb = Path.Combine(target, "state_5.sqlite"); File.WriteAllBytes(stateDb, new byte[0]);
                SqliteNative.Execute(stateDb, "CREATE TABLE backfill_state(id INTEGER PRIMARY KEY CHECK(id=1),status TEXT NOT NULL,last_watermark TEXT,last_success_at INTEGER,updated_at INTEGER NOT NULL); INSERT INTO backfill_state VALUES(1,'complete','x',1,1);");
                string pack = Path.Combine(root, new string('p', 145) + ".zip"); var progressEvents = new List<string>(); int exportWarnings;
                using (var busyLock = new FileStream(busyFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) exportWarnings = MigrationCore.Export(source, conversations, pack, true, progressEvents.Add);
                if (exportWarnings != 1 || progressEvents.Count < 3 || !progressEvents.Any(x => x.Contains("压缩"))) return 19; var restoreProgress = new List<string>(); MigrationCore.Restore(pack, target, restoreProgress.Add); if (restoreProgress.Count < 5 || !restoreProgress.Any(x => x.Contains("恢复会话历史链")) || !restoreProgress.Any(x => x.Contains("完成恢复"))) return 21;
                var restored = MigrationCore.Load(target);
                if (restored.Count != 1 || restored[0].Id != id || restored[0].Name != "测试对话") return 12;
                string migratedRoot = Path.Combine(target, "migrated-files");
                if (!Directory.Exists(migratedRoot) || Directory.GetFiles(migratedRoot, "报告.txt", SearchOption.AllDirectories).Length != 1) return 16;
                if (Directory.GetFiles(migratedRoot, "attached image.png", SearchOption.AllDirectories).Length != 1) return 18;
                if (Directory.GetFiles(migratedRoot, "deep-result.txt", SearchOption.AllDirectories).Length != 1) return 20;
                string restoredText = File.ReadAllText(restored[0].FilePath, Encoding.UTF8);
                if (!restoredText.Contains("migrated-files") || restoredText.Contains(escapedGenerated) || restoredText.Contains(escapedAttachment)) return 17;
                if (SqliteNative.Scalar(stateDb, "SELECT status FROM backfill_state WHERE id=1;") != "pending") return 14;
                if (!restored[0].FilePath.EndsWith(Path.GetFileName(continuation), StringComparison.OrdinalIgnoreCase) || restored[0].LineageFiles.Count != 2) return 15;
                string removedLineage = restored[0].LineageFiles.First(x => !string.Equals(x, restored[0].FilePath, StringComparison.OrdinalIgnoreCase)); File.Delete(removedLineage);
                MigrationCore.Restore(pack, target);
                if (MigrationCore.Load(target).Count != 1 || MigrationCore.Load(target)[0].LineageFiles.Count != 2) return 13;
                string canonical = MigrationCore.Load(target)[0].FilePath;
                string olderPart = MigrationCore.Load(target)[0].LineageFiles.FirstOrDefault(x => !string.Equals(x, canonical, StringComparison.OrdinalIgnoreCase));
                if (olderPart != null) File.Delete(olderPart);
                string legacy = Path.Combine(Path.GetDirectoryName(canonical), "rollout-restored-" + id + ".jsonl");
                File.Move(canonical, legacy); SqliteNative.Execute(stateDb, "UPDATE backfill_state SET status='complete';");
                MigrationCore.RepairSidebarIndex(target);
                var repaired = MigrationCore.Load(target);
                if (repaired.Count != 1 || Path.GetFileName(repaired[0].FilePath).StartsWith("rollout-restored-")) return 16;
                if (SqliteNative.Scalar(stateDb, "SELECT status FROM backfill_state WHERE id=1;") != "pending") return 17;
                return 0;
            }
            catch (Exception ex) { try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "self-test-error.txt"), ex.ToString(), Encoding.UTF8); } catch { } return 10; }
            finally { try { Directory.Delete(root, true); } catch { } }
        }
    }
}
