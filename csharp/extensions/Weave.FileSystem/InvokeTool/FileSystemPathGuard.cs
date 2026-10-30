namespace Weave.Tools.Connectors;

internal static class FileSystemPathGuard
{
    public static string ResolveSafePath(string root, string relativePath, bool sandbox = true)
    {
        if (relativePath.Contains('\0', StringComparison.Ordinal))
            throw new ArgumentException("Path contains null bytes");

        relativePath = relativePath.Replace('\\', '/');

        if (relativePath.StartsWith('/'))
            throw new ArgumentException("Path must be relative, not absolute");

        if (relativePath.Contains("://", StringComparison.Ordinal))
            throw new ArgumentException("Path contains a URL scheme");

        if (relativePath.Length >= 2 && relativePath[1] == ':')
            throw new ArgumentException("Path contains a drive letter");

        if (relativePath.Contains(':', StringComparison.Ordinal))
            throw new ArgumentException("Path contains illegal characters");

        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));

        VerifyContainment(root, fullPath);

        if (sandbox)
            VerifyNoSymlinkEscape(root, fullPath);

        return fullPath;
    }

    public static void VerifyContainment(string root, string fullPath)
    {
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) &&
            !fullPath.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Path traversal detected");
        }
    }

    private static void VerifyNoSymlinkEscape(string root, string fullPath)
    {
        var relativePart = Path.GetRelativePath(root, fullPath);
        if (relativePart == ".")
            return;

        var segments = relativePart.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = root;

        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);

            if (!Path.Exists(current))
                break;

            var fileInfo = new FileInfo(current);
            if (fileInfo.LinkTarget is not null)
            {
                var resolved = File.ResolveLinkTarget(current, returnFinalTarget: true)
                    ?? throw new ArgumentException($"Sandbox violation: symlink at '{segment}' could not be resolved");

                var resolvedPath = Path.GetFullPath(resolved.FullName);
                VerifyContainment(root, resolvedPath);
                current = resolvedPath;
            }

            if (Directory.Exists(current))
            {
                var dirInfo = new DirectoryInfo(current);
                if (dirInfo.LinkTarget is not null)
                {
                    var resolved = Directory.ResolveLinkTarget(current, returnFinalTarget: true)
                        ?? throw new ArgumentException($"Sandbox violation: junction at '{segment}' could not be resolved");

                    var resolvedPath = Path.GetFullPath(resolved.FullName);
                    VerifyContainment(root, resolvedPath);
                    current = resolvedPath;
                }
            }
        }
    }
}
