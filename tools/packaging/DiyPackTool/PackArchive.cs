using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

internal static class SafePaths
{
    public static string DirectoryReady(string path)
    {
        string full = Path.GetFullPath(path), existing = full;
        while (!Directory.Exists(existing) && !File.Exists(existing))
            existing = Path.GetDirectoryName(existing) ?? throw new InvalidDataException("无法定位已有父目录：" + full);
        if (!Directory.Exists(existing)) throw new InvalidDataException("父路径不是目录：" + existing);
        RejectLinks(existing); Directory.CreateDirectory(full); RejectLinks(full);
        return full;
    }

    public static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("路径不能包含目录联接或符号链接：" + current);
    }

    public static string Child(string root, string relative)
    {
        string parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string result = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("路径越出目标目录：" + relative);
        return result;
    }

    public static string Sha256(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static void RequireSha256(string actual, string expected)
    {
        if (expected.Length != 64 || expected.Any(c => !Uri.IsHexDigit(c)) || !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ZIP 的 SHA256 与发布清单不一致。");
    }
}

internal sealed record PublishedFile(string Path, long Bytes, string Sha256);
internal sealed record ReleaseManifest(int SchemaVersion, string PackId, string Version, string DisplayName, string Author,
    string Description, string ContentHash, string ZipFile, long ZipBytes, string ZipSha256, string CoreVersion,
    IReadOnlyList<string> RequiredCapabilities, IReadOnlyList<PackageDependency> Dependencies, IReadOnlyList<PublishedFile> Files);
internal sealed record InstallReceipt(int SchemaVersion, string PackId, string Version, string ContentHash, string ZipSha256,
    string Product, string Company, string CoreVersion, string Outcome, string InstalledUtc,
    IReadOnlyList<string> ExistingVersions, IReadOnlyList<PublishedFile> Files);
internal sealed record InstalledPackageResult(InstallReceipt Receipt, IReadOnlyList<GameplayCapabilityIssue> UnavailableGameplay);

internal static class PackArchive
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly DateTimeOffset ZipDate = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static ReleaseManifest Publish(GameContentBridge game, string source, string output)
    {
        var frozen = game.Load(source);
        string destination = SafePaths.DirectoryReady(output);
        string name = frozen.PackId + "-" + frozen.Version + ".zip";
        string zip = SafePaths.Child(destination, name), manifestPath = SafePaths.Child(destination, frozen.PackId + "-" + frozen.Version + ".release.json");
        if (File.Exists(zip) || File.Exists(manifestPath)) throw new IOException("发布目标已存在，不能覆盖。");
        string temporaryZip = Path.Combine(destination, ".diy-release-" + Guid.NewGuid().ToString("N") + ".tmp");
        using (var stream = new FileStream(temporaryZip, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            foreach (var file in frozen.Files.OrderBy(x => x.Path, StringComparer.Ordinal))
            {
                game.ValidateRelative(file.Path);
                var entry = archive.CreateEntry(file.Path, CompressionLevel.Optimal);
                entry.LastWriteTime = ZipDate; entry.ExternalAttributes = 0;
                using var outputStream = entry.Open(); outputStream.Write(file.Bytes);
            }
        File.Move(temporaryZip, zip);
        var files = frozen.Files.Select(file => new PublishedFile(file.Path, file.Bytes.LongLength,
            Convert.ToHexString(SHA256.HashData(file.Bytes)).ToLowerInvariant())).ToArray();
        var result = new ReleaseManifest(1, frozen.PackId, frozen.Version, frozen.Name, frozen.Author, frozen.Description,
            frozen.ContentHash, name, new FileInfo(zip).Length, SafePaths.Sha256(zip), game.CoreVersion,
            frozen.RequiredCapabilities, frozen.Dependencies, files);
        WriteNewJson(manifestPath, result);
        return result;
    }

    public static string Export(string zip, string expectedHash, string output)
    {
        string source = Path.GetFullPath(zip); SafePaths.RejectLinks(source);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        SafePaths.RequireSha256(Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant(), expectedHash);
        string directory = SafePaths.DirectoryReady(output), target = SafePaths.Child(directory, Path.GetFileName(source));
        if (File.Exists(target))
        {
            SafePaths.RejectLinks(target);
            if (string.Equals(SafePaths.Sha256(target), expectedHash, StringComparison.OrdinalIgnoreCase)) return "内容相同，已保留现有 ZIP：" + target;
            throw new IOException("导出目标已存在且内容不同，未覆盖：" + target);
        }
        string temporary = Path.Combine(directory, ".diy-export-" + Guid.NewGuid().ToString("N") + ".tmp");
        input.Position = 0;
        using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { input.CopyTo(destination); destination.Flush(true); }
        SafePaths.RequireSha256(SafePaths.Sha256(temporary), expectedHash);
        File.Move(temporary, target);
        return "已导出：" + target;
    }

    public static FrozenPackage Verify(GameContentBridge game, string zip, string expectedZipHash, string expectedPackId,
        string expectedVersion, string expectedContentHash, string output)
    {
        string source = Path.GetFullPath(zip); SafePaths.RejectLinks(source);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        SafePaths.RequireSha256(Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant(), expectedZipHash);
        game.ValidatePackageIdentity(expectedPackId, expectedVersion);
        string destination = Path.GetFullPath(output);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("校验输出已存在，不能覆盖：" + destination);
        SafePaths.DirectoryReady(destination);
        ExtractChecked(game, input, destination);
        var result = game.Load(destination);
        if (result.PackId != expectedPackId || result.Version != expectedVersion ||
            !string.Equals(result.ContentHash, expectedContentHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("包内身份或内容哈希与发布清单不一致。");
        return result;
    }

    public static InstalledPackageResult Install(GameContentBridge game, string zip, string expectedZipHash, string expectedPackId,
        string expectedVersion, string expectedContentHash, string dataRoot, bool keepStage, bool simulateReceiptFailure = false)
    {
        string source = Path.GetFullPath(zip); SafePaths.RejectLinks(source);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        SafePaths.RequireSha256(Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant(), expectedZipHash);
        game.ValidatePackageIdentity(expectedPackId, expectedVersion);
        if (expectedContentHash.Length != 64 || expectedContentHash.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("发布清单的内容哈希无效。");
        expectedContentHash = expectedContentHash.ToLowerInvariant();
        // 即使目标版本已存在，也先完整核验 ZIP，不能让跳过路径绕过越界和损坏检查。
        ExtractChecked(game, input, null);
        string root = SafePaths.DirectoryReady(dataRoot);
        string stageParent = SafePaths.DirectoryReady(Path.Combine(root, "ModStaging"));
        string locks = SafePaths.DirectoryReady(Path.Combine(stageParent, "locks"));
        using var packageLock = LockPackage(Path.Combine(locks, expectedPackId + ".lock"));
        string stage = SafePaths.DirectoryReady(Path.Combine(stageParent, Guid.NewGuid().ToString("N")));
        string payload = SafePaths.DirectoryReady(Path.Combine(stage, "payload"));
        ExtractChecked(game, input, payload);
        var frozen = game.Load(payload);
        if (frozen.PackId != expectedPackId || frozen.Version != expectedVersion || frozen.ContentHash != expectedContentHash)
            throw new InvalidDataException("包内身份或内容哈希与发布清单不一致；暂存内容已保留。");
        // 提前检查回执目录，避免已知的路径或权限问题发生在正式发布之后。
        string receipts = SafePaths.DirectoryReady(Path.Combine(root, "ModInstallReceipts", frozen.PackId, frozen.Version));
        string mods = SafePaths.DirectoryReady(Path.Combine(root, "Mods"));
        string packDirectory = SafePaths.DirectoryReady(SafePaths.Child(mods, frozen.PackId));
        string destination = SafePaths.Child(packDirectory, frozen.Version);
        var existingVersions = new List<string>();
        foreach (string directory in Directory.EnumerateDirectories(packDirectory))
        {
            string name = Path.GetFileName(directory);
            try { game.ValidatePackageIdentity(frozen.PackId, name); existingVersions.Add(name); }
            catch (InvalidDataException) { /* 非版本目录不参与提示，也不读取其内容。 */ }
        }
        existingVersions.Sort(StringComparer.Ordinal);
        string outcome;
        if (Directory.Exists(destination) || File.Exists(destination)) { CheckExisting(game, destination, frozen.ContentHash); outcome = "same_content_skipped"; }
        else
        {
            try { Directory.Move(payload, destination); outcome = "installed"; }
            catch (IOException) when (Directory.Exists(destination) || File.Exists(destination))
            { CheckExisting(game, destination, frozen.ContentHash); outcome = "same_content_skipped"; }
        }
        InstallReceipt receipt;
        try
        {
#if DEBUG
            if (simulateReceiptFailure) throw new IOException("隔离夹具模拟回执写入失败");
#endif
            receipt = SaveReceipt(game, frozen, expectedZipHash, receipts, outcome, existingVersions);
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is InvalidDataException)
        {
            if (outcome != "installed") throw new IOException("回执写入失败；已有版本保持不变。", error);
            bool rolledBack = false;
            string rollbackReason = "";
            try
            {
                // 只撤回本次完整发布且内容未变化的目录，移动回同卷隔离暂存；绝不删除或覆盖用户内容。
                if (Directory.Exists(payload) || File.Exists(payload)) throw new IOException("暂存目标已被占用");
                SafePaths.RejectLinks(stage);
                CheckExisting(game, destination, frozen.ContentHash);
                Directory.Move(destination, payload);
                rolledBack = true;
            }
            catch (Exception rollback) when (rollback is IOException || rollback is UnauthorizedAccessException || rollback is InvalidDataException)
            { rollbackReason = rollback.Message; }
            throw new IOException(rolledBack ? "回执写入失败，已将本次新版本撤回暂存；原有版本未改变。" :
                "回执写入失败，无法安全撤回本次版本，已保留现场供检查：" + rollbackReason, error);
        }
        if (!keepStage)
        {
            try { RemoveOwnedStage(stage, payload, outcome == "installed" ? null : frozen); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is InvalidDataException)
            { Console.Error.WriteLine("暂存目录未清理，已保留：" + stage + "：" + error.Message); }
        }
        return new InstalledPackageResult(receipt, frozen.UnavailableGameplay);
    }

    private static InstallReceipt SaveReceipt(GameContentBridge game, FrozenPackage frozen, string zipHash, string receipts, string outcome, IReadOnlyList<string> existingVersions)
    {
        var files = frozen.Files.Select(file => new PublishedFile(file.Path, file.Bytes.LongLength,
            Convert.ToHexString(SHA256.HashData(file.Bytes)).ToLowerInvariant())).ToArray();
        var receipt = new InstallReceipt(1, frozen.PackId, frozen.Version, frozen.ContentHash, zipHash.ToLowerInvariant(),
            game.Product, game.Company, game.CoreVersion, outcome, DateTimeOffset.UtcNow.ToString("O"), existingVersions, files);
        SafePaths.RejectLinks(receipts);
        WriteNewJson(Path.Combine(receipts, Guid.NewGuid().ToString("N") + ".json"), receipt);
        return receipt;
    }

    private static FrozenPackage CheckExisting(GameContentBridge game, string path, string expectedHash)
    {
        if (!Directory.Exists(path)) throw new IOException("目标版本位置已被文件占用：" + path);
        SafePaths.RejectLinks(path);
        FrozenPackage existing;
        try { existing = game.Load(path); }
        catch (Exception error) when (error is InvalidDataException || error is IOException || error is ArgumentException)
        { throw new IOException("同版本现有内容已损坏或被修改，未覆盖：" + path, error); }
        if (existing.ContentHash != expectedHash) throw new IOException("同版本已有不同内容，未覆盖：" + path);
        return existing;
    }

    private static FileStream LockPackage(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            if (File.Exists(path)) SafePaths.RejectLinks(path);
            try
            {
                var handle = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                try { SafePaths.RejectLinks(path); return handle; }
                catch { handle.Dispose(); throw; }
            }
            catch (IOException error) when (attempt < 40 && ((error.HResult & 0xffff) == 32 || (error.HResult & 0xffff) == 33))
            { System.Threading.Thread.Sleep(250); }
        }
    }

    private static void RemoveOwnedStage(string stage, string payload, FrozenPackage? expected)
    {
        string root = Path.GetFullPath(stage), prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        SafePaths.RejectLinks(root);
        if (expected == null)
        {
            if (Directory.EnumerateFileSystemEntries(root).Any()) throw new InvalidDataException("暂存目录包含额外内容");
            Directory.Delete(root, false); return;
        }
        var rootEntries = Directory.EnumerateFileSystemEntries(root).Select(Path.GetFullPath).ToArray();
        if (rootEntries.Length != 1 || !string.Equals(rootEntries[0], Path.GetFullPath(payload), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("暂存目录包含额外内容");
        var actual = new Dictionary<string, string>(StringComparer.Ordinal);
        var directories = new List<string>();
        var actualDirectories = new HashSet<string>(StringComparer.Ordinal);
        void Walk(string directory, string relative)
        {
            SafePaths.RejectLinks(directory);
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                string full = Path.GetFullPath(entry);
                if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("暂存路径越界");
                SafePaths.RejectLinks(full);
                string name = relative + Path.GetFileName(full);
                if (Directory.Exists(full))
                {
                    actualDirectories.Add(name);
                    Walk(full, name + "/");
                    directories.Add(full);
                }
                else actual.Add(name, full);
            }
        }
        Walk(payload, "");
        var expectedDirectories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in expected.Files)
        {
            int separator = file.Path.LastIndexOf('/');
            while (separator > 0)
            {
                expectedDirectories.Add(file.Path.Substring(0, separator));
                separator = file.Path.LastIndexOf('/', separator - 1);
            }
        }
        if (!actualDirectories.SetEquals(expectedDirectories)) throw new InvalidDataException("暂存目录发生变化");
        if (actual.Count != expected.Files.Count) throw new InvalidDataException("暂存文件发生变化");
        foreach (var file in expected.Files)
            if (!actual.TryGetValue(file.Path, out string? path) ||
                SafePaths.Sha256(path) != Convert.ToHexString(SHA256.HashData(file.Bytes)).ToLowerInvariant())
                throw new InvalidDataException("暂存文件发生变化：" + file.Path);
        // 所有目标均为本次创建、逐项核验的绝对路径；不用递归删除。
        foreach (string path in actual.Values) { SafePaths.RejectLinks(path); File.Delete(path); }
        foreach (string directory in directories.OrderByDescending(x => x.Length)) { SafePaths.RejectLinks(directory); Directory.Delete(directory, false); }
        SafePaths.RejectLinks(payload); Directory.Delete(payload, false);
        if (Directory.EnumerateFileSystemEntries(root).Any()) throw new InvalidDataException("暂存目录包含额外内容");
        Directory.Delete(root, false);
    }

    private static void ExtractChecked(GameContentBridge game, FileStream input, string? payload)
    {
        input.Position = 0;
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > game.MaximumFiles * 4) throw new InvalidDataException("ZIP 条目数量超过上限。");
        var paths = new Dictionary<string, (string Spelling, bool IsDirectory)>(StringComparer.OrdinalIgnoreCase);
        var explicitEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int files = 0; long total = 0;
        foreach (var entry in archive.Entries)
        {
            bool isDirectory = entry.FullName.EndsWith('/');
            string relative = isDirectory ? entry.FullName[..^1] : entry.FullName;
            game.ValidateRelative(relative);
            if (!explicitEntries.Add(relative)) throw new InvalidDataException("ZIP 存在重复或大小写冲突路径：" + relative);
            int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (entry.IsEncrypted || unixType != 0 && unixType != 0x8000 && unixType != 0x4000 ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("ZIP 不能包含加密项或链接：" + relative);
            if (isDirectory && (entry.Length != 0 || unixType == 0x8000) || !isDirectory && unixType == 0x4000)
                throw new InvalidDataException("ZIP 条目类型与路径不一致：" + relative);
            var parts = relative.Split('/');
            for (int i = 1; i < parts.Length; i++) Register(string.Join('/', parts.Take(i)), true);
            Register(relative, isDirectory);
            if (isDirectory) continue;
            if (++files > game.MaximumFiles || entry.Length < 0 || entry.Length > game.MaximumFileBytes || total + entry.Length > game.MaximumTotalBytes)
                throw new InvalidDataException("ZIP 文件数量或解压大小超过限制：" + relative);
            total += entry.Length;
            using var source = entry.Open();
            FileStream? created = null;
            if (payload != null)
            {
                string target = SafePaths.Child(payload, relative);
                SafePaths.DirectoryReady(Path.GetDirectoryName(target)!);
                created = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            using var output = created;
            var buffer = new byte[65536]; long actual = 0; uint crc = 0xffffffff;
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                actual += read;
                if (actual > entry.Length || actual > game.MaximumFileBytes) throw new InvalidDataException("ZIP 条目超出声明大小：" + relative);
                crc = UpdateCrc(crc, buffer, read); output?.Write(buffer, 0, read);
            }
            if (actual != entry.Length || ~crc != entry.Crc32) throw new InvalidDataException("ZIP 条目长度或 CRC 不符：" + relative);
        }
        if (files == 0) throw new InvalidDataException("ZIP 不包含包文件。");

        void Register(string name, bool directory)
        {
            if (paths.TryGetValue(name, out var existing))
            {
                if (!directory || !existing.IsDirectory || existing.Spelling != name)
                    throw new InvalidDataException("ZIP 存在文件/目录或大小写冲突：" + name);
            }
            else paths.Add(name, (name, directory));
        }
    }

    private static uint UpdateCrc(uint crc, byte[] data, int length)
    {
        for (int i = 0; i < length; i++) crc = CrcTable[(crc ^ data[i]) & 0xff] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++) value = (value & 1) == 0 ? value >> 1 : (value >> 1) ^ 0xedb88320;
            table[i] = value;
        }
        return table;
    }

    private static void WriteNewJson<T>(string path, T value)
    {
        string parent = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string temp = Path.Combine(parent, ".diy-receipt-" + Guid.NewGuid().ToString("N") + ".tmp");
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, value, JsonOptions); stream.Flush(true); }
        File.Move(temp, path);
    }
}

internal static class WindowsDataRoot
{
    private static readonly Guid LocalLow = new("A520A1A4-1780-4FF6-BD18-167343C5AF16");
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(in Guid id, uint flags, IntPtr token, out IntPtr path);
    [DllImport("ole32.dll")] private static extern void CoTaskMemFree(IntPtr memory);

    public static string ForGame(GameContentBridge game)
    {
        int result = SHGetKnownFolderPath(LocalLow, 0, IntPtr.Zero, out IntPtr pointer);
        if (result != 0 || pointer == IntPtr.Zero) throw new InvalidOperationException("无法定位当前用户的 LocalLow 目录。");
        try
        {
            string root = Marshal.PtrToStringUni(pointer) ?? throw new InvalidOperationException("LocalLow 路径为空。");
            return Path.GetFullPath(string.IsNullOrEmpty(game.Company) ? Path.Combine(root, game.Product) : Path.Combine(root, game.Company, game.Product));
        }
        finally { CoTaskMemFree(pointer); }
    }
}
