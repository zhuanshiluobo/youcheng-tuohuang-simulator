using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

static void Require(bool value, string reason)
{
    if (!value) throw new InvalidOperationException(reason);
}

static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

static void RejectLinks(string path)
{
    for (var current = new DirectoryInfo(Path.GetFullPath(path)); current != null; current = current.Parent)
        Require((current.Attributes & FileAttributes.ReparsePoint) == 0, "夹具路径包含目录联接：" + current.FullName);
}

Require(OperatingSystem.IsWindows(), "该夹具依赖 Windows 禁止删除共享。");
Require(args.Length == 5, "需要工具、游戏、示例目录、输出目录和项目目录。");
string tool = Path.GetFullPath(args[0]), game = Path.GetFullPath(args[1]);
string examples = Path.GetFullPath(args[2]), output = Path.GetFullPath(args[3]), project = Path.GetFullPath(args[4]);
string allowed = Path.Combine(project, "TestResults") + Path.DirectorySeparatorChar;
Require(output.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) && !Directory.Exists(output), "输出必须是新的项目隔离目录。");
string parent = Path.GetDirectoryName(output)!;
Require(Directory.Exists(parent), "输出父目录不存在。");
RejectLinks(parent);
Directory.CreateDirectory(output);

var assembly = Assembly.LoadFrom(tool);
var bridgeType = assembly.GetType("GameContentBridge", true)!;
var bridge = Activator.CreateInstance(bridgeType, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
    null, new object?[] { game, "NoMadCity" }, null)!;
var cleanup = assembly.GetType("PackArchive", true)!.GetMethod("RemoveOwnedStage", BindingFlags.NonPublic | BindingFlags.Static)!;
var results = new List<object>();

void Verify(string name, string example, string mutation, string expectedType, string expectedMessage)
{
    string stage = Path.Combine(output, name, "stage"), payload = Path.Combine(stage, "payload");
    Directory.CreateDirectory(payload);
    object frozen = bridgeType.GetMethod("Load")!.Invoke(bridge, new object?[] { Path.Combine(examples, example) })!;
    var entries = ((IEnumerable)frozen.GetType().GetProperty("Files")!.GetValue(frozen)!).Cast<object>().ToArray();
    Require(entries.Length > 0, "示例包没有文件。");
    var paths = new List<string>();
    foreach (object entry in entries)
    {
        string relative = (string)entry.GetType().GetProperty("Path")!.GetValue(entry)!;
        byte[] bytes = (byte[])entry.GetType().GetProperty("Bytes")!.GetValue(entry)!;
        string target = Path.GetFullPath(Path.Combine(payload, relative.Replace('/', Path.DirectorySeparatorChar)));
        Require(target.StartsWith(payload + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !File.Exists(target), "夹具路径越界或重复。");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, bytes);
        paths.Add(target);
    }

    string? extra = null;
    if (mutation == "extra-dir") extra = Path.Combine(payload, "用户新增空目录");
    if (mutation == "nested-dir")
    {
        Require(Directory.Exists(Path.Combine(payload, "lua")), "嵌套样例缺少现有目录。");
        extra = Path.Combine(payload, "lua", "用户新增空目录");
    }
    if (extra != null) Directory.CreateDirectory(extra);
    if (mutation == "extra-file")
    {
        string extraFile = Path.Combine(payload, "用户新增.txt");
        File.WriteAllText(extraFile, "不属于包的文件");
        paths.Add(extraFile);
    }
    if (mutation == "modified-file") File.AppendAllText(paths.Single(), "\n ");
    var hashes = paths.ToDictionary(path => path, Hash, StringComparer.Ordinal);
    var directories = Directory.EnumerateDirectories(stage, "*", SearchOption.AllDirectories)
        .Select(Path.GetFullPath).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    Exception? observed = null;
    var locks = new List<FileStream>();
    try
    {
        // 每个文件都禁止删除共享；即使待测代码错误进入删除循环，也无法删掉第一个文件。
        foreach (string path in paths) locks.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
        try { cleanup.Invoke(null, new object?[] { stage, payload, frozen }); }
        catch (TargetInvocationException error) { observed = error.InnerException ?? error; }
    }
    finally { foreach (var stream in locks) stream.Dispose(); }
    Exception actual = observed ?? throw new InvalidOperationException("清理未按夹具预期停止。");
    bool preserved = Directory.Exists(stage) && paths.All(path => File.Exists(path) && Hash(path) == hashes[path]) &&
        (extra == null || Directory.Exists(extra)) &&
        directories.SequenceEqual(Directory.EnumerateDirectories(stage, "*", SearchOption.AllDirectories)
            .Select(Path.GetFullPath).OrderBy(x => x, StringComparer.Ordinal));
    bool passed = preserved && actual.GetType().Name == expectedType &&
        (expectedMessage.Length == 0 || actual.Message.Contains(expectedMessage, StringComparison.Ordinal));
    results.Add(new { name, passed, preserved, fileCount = paths.Count, actualException = actual.GetType().Name, actual.Message });
    Require(passed, "暂存保护检查失败：" + name + "：" + actual);
}

Verify("额外空目录", "仅模式", "extra-dir", "InvalidDataException", "暂存目录发生变化");
Verify("嵌套额外空目录", "事件试验", "nested-dir", "InvalidDataException", "暂存目录发生变化");
Verify("额外文件", "仅模式", "extra-file", "InvalidDataException", "暂存文件发生变化");
Verify("已有文件被修改", "仅模式", "modified-file", "InvalidDataException", "暂存文件发生变化");
Verify("合法嵌套目录", "事件试验", "", "IOException", "");

Console.WriteLine(JsonSerializer.Serialize(new { passed = true, cases = results,
    scope = "禁止删除共享的隔离文件；合法目录通过检查后由文件锁阻止真实删除。" },
    new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
return 0;
