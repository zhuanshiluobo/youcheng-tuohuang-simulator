#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.IO;

namespace YC.Presentation
{
    // 只解析启动参数，不创建目录或探测用户存档；选择结果由原唯一Store使用。
    internal static class PerformanceSavePath
    {
        private const string Option = "--yc-performance-save-root";

        internal static string Select(string normalRoot, string[] arguments)
        {
            if (arguments == null) throw new ArgumentNullException(nameof(arguments));
            string requested = null;
            bool supplied = false;
            foreach (string argument in arguments)
            {
                if (argument == null || !argument.StartsWith(Option, StringComparison.OrdinalIgnoreCase)) continue;
                if (supplied) throw new InvalidOperationException("采样存档目录参数只能提供一次。");
                supplied = true;
                if (!argument.StartsWith(Option + "=", StringComparison.Ordinal))
                    throw new InvalidOperationException("采样存档目录参数必须使用 --yc-performance-save-root=<绝对目录>。");
                requested = argument.Substring(Option.Length + 1);
            }
            if (!supplied) return normalRoot;
            if (string.IsNullOrWhiteSpace(requested) || !Path.IsPathFullyQualified(requested) ||
                requested.StartsWith(@"\\?\", StringComparison.Ordinal) || requested.StartsWith(@"\\.\", StringComparison.Ordinal))
                throw new InvalidOperationException("采样存档目录必须是普通文件系统中的完整绝对目录。");

            string selected, normal;
            try
            {
                ValidateNames(requested);
                selected = Path.GetFullPath(requested);
                normal = Path.GetFullPath(normalRoot);
                string root = Path.GetPathRoot(selected);
                if (selected.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ==
                    root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                    throw new InvalidOperationException("采样存档目录不能是磁盘或共享根目录。");
                ValidateNames(selected);
            }
            catch (Exception error) when (error is ArgumentException || error is NotSupportedException || error is PathTooLongException)
            { throw new InvalidOperationException("采样存档目录参数无效。", error); }
            if (Contains(selected, normal) || Contains(normal, selected))
                throw new InvalidOperationException("采样存档目录必须与正常存档目录及其父子目录分离。");
            return selected;
        }

        private static void ValidateNames(string path)
        {
            foreach (string part in path.Substring(Path.GetPathRoot(path).Length).Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (part == "." || part == "..") continue;
                if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    Path.DirectorySeparatorChar == '\\' && (part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal) || ReservedWindowsName(part)))
                    throw new InvalidOperationException("采样存档目录包含无效目录名。");
            }
        }

        private static bool Contains(string candidate, string parent)
        {
            var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            string prefix = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), prefix, comparison) ||
                candidate.StartsWith(prefix + Path.DirectorySeparatorChar, comparison);
        }

        private static bool ReservedWindowsName(string name)
        {
            string stem = name.Split('.')[0].ToUpperInvariant();
            return stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" ||
                stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] >= '1' && stem[3] <= '9';
        }
    }
}
#endif
