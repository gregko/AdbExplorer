using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AdbExplorer.Models;

namespace AdbExplorer.Services
{
    public class FileSystemService
    {
        private readonly AdbService adbService;

        public FileSystemService(AdbService adbService)
        {
            this.adbService = adbService;
        }

        public List<FileItem> GetFiles(string path)
        {
            var files = new List<FileItem>();

            try
            {
                // First, resolve the path if it's a symlink
                var resolvedPath = ResolveSymlink(path);
                System.Diagnostics.Debug.WriteLine($"GetFiles: path={path}, resolvedPath={resolvedPath}");

                // Escape the path for shell use with backslash escaping
                var escapedPath = EscapePathForShell(resolvedPath);

                // First try: standard ls -la with path
                var output = adbService.ExecuteShellCommand($"ls -la {escapedPath} 2>&1");
                System.Diagnostics.Debug.WriteLine($"GetFiles ls output for {path}:\n{output}");

                // If we got no output or it looks empty/error, try alternative: cd to directory first
                // Some emulators (like BlueStacks) work better this way
                if (string.IsNullOrWhiteSpace(output) ||
                    (!output.Contains("total") && !output.Contains("drwx") && !output.Contains("-rw") && !output.Contains("lrwx")))
                {
                    System.Diagnostics.Debug.WriteLine($"GetFiles: trying cd + ls approach for {resolvedPath}");
                    var altOutput = adbService.ExecuteShellCommand($"cd {escapedPath} && ls -la 2>&1");
                    System.Diagnostics.Debug.WriteLine($"GetFiles cd+ls output for {path}:\n{altOutput}");

                    // If alternative approach worked better, use it
                    if (!string.IsNullOrWhiteSpace(altOutput) &&
                        (altOutput.Contains("total") || altOutput.Contains("drwx") || altOutput.Contains("-rw") || altOutput.Contains("lrwx")))
                    {
                        output = altOutput;
                    }
                }

                // Check for permission denied in output - but only if NO valid entries are found
                // Some emulators like BlueStacks may include "Permission denied" in output but still list files
                if (output.Contains("Permission denied") && !output.Contains("total") && !output.Contains("drwx") && !output.Contains("-rw"))
                {
                    throw new UnauthorizedAccessException($"Permission denied: {path}");
                }

                // Split into lines and clean up
                var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                  .Where(l => !string.IsNullOrWhiteSpace(l))
                                  .ToList();

                foreach (var line in lines)
                {
                    // Skip header line
                    if (line.StartsWith("total ")) continue;

                    // Skip permission errors embedded in output
                    if (line.Contains("Permission denied") || line.Contains(": Permission denied"))
                    {
                        // Extract the actual line if permission error is embedded
                        var parts = line.Split(new[] { "ls:" }, StringSplitOptions.None);
                        if (parts.Length > 1 && parts[0].Trim().Length > 0)
                        {
                            var item = ParseLsLine(parts[0].Trim(), resolvedPath);
                            if (item != null)
                            {
                                // Keep the original path for symlinks in the UI
                                if (path != resolvedPath && item.FullPath.StartsWith(resolvedPath))
                                {
                                    item.FullPath = path + item.FullPath.Substring(resolvedPath.Length);
                                }
                                files.Add(item);
                            }
                        }
                        continue;
                    }

                    // Parse normal lines
                    var fileItem = ParseLsLine(line, resolvedPath);
                    if (fileItem != null)
                    {
                        // Keep the original path for symlinks in the UI
                        if (path != resolvedPath && fileItem.FullPath.StartsWith(resolvedPath))
                        {
                            fileItem.FullPath = path + fileItem.FullPath.Substring(resolvedPath.Length);
                        }
                        files.Add(fileItem);
                    }
                }
            }
            catch (Exception ex)
            {
                // Let the exception bubble up to the UI layer
                throw new Exception($"Cannot access {path}: {ex.Message}", ex);
            }

            return files;
        }

        // Resolve symlinks
        private string ResolveSymlink(string path)
        {
            try
            {
                var escapedPath = EscapePathForShell(path);

                // Try readlink -f first (canonical path resolution)
                var result = adbService.ExecuteShellCommand($"readlink -f {escapedPath} 2>/dev/null");
                System.Diagnostics.Debug.WriteLine($"ResolveSymlink: readlink -f {escapedPath} returned: '{result}'");

                if (!string.IsNullOrWhiteSpace(result) && !result.Contains("No such file") && !result.Contains("not found"))
                {
                    var resolved = result.Trim();
                    if (!string.IsNullOrWhiteSpace(resolved) && resolved != path && !resolved.StartsWith("readlink:"))
                    {
                        System.Diagnostics.Debug.WriteLine($"Resolved symlink: {path} -> {resolved}");
                        return resolved;
                    }
                }

                // If readlink -f fails, try 'realpath' as an alternative (available on some Android versions)
                result = adbService.ExecuteShellCommand($"realpath {escapedPath} 2>/dev/null");
                System.Diagnostics.Debug.WriteLine($"ResolveSymlink: realpath {escapedPath} returned: '{result}'");

                if (!string.IsNullOrWhiteSpace(result) && !result.Contains("No such file") && !result.Contains("not found"))
                {
                    var resolved = result.Trim();
                    if (!string.IsNullOrWhiteSpace(resolved) && resolved != path && !resolved.StartsWith("realpath:"))
                    {
                        System.Diagnostics.Debug.WriteLine($"Resolved symlink via realpath: {path} -> {resolved}");
                        return resolved;
                    }
                }

                // If both fail, try a simple readlink (without -f) to see if it's a direct symlink
                result = adbService.ExecuteShellCommand($"readlink {escapedPath} 2>/dev/null");
                System.Diagnostics.Debug.WriteLine($"ResolveSymlink: readlink {escapedPath} returned: '{result}'");

                if (!string.IsNullOrWhiteSpace(result) && !result.Contains("No such file") && !result.Contains("not found"))
                {
                    var resolved = result.Trim();
                    if (!string.IsNullOrWhiteSpace(resolved) && resolved != path && !resolved.StartsWith("readlink:"))
                    {
                        // If it's a relative path, make it absolute
                        if (!resolved.StartsWith("/"))
                        {
                            var parentDir = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/');
                            if (!string.IsNullOrEmpty(parentDir))
                            {
                                resolved = parentDir + "/" + resolved;
                            }
                        }
                        System.Diagnostics.Debug.WriteLine($"Resolved direct symlink: {path} -> {resolved}");
                        return resolved;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ResolveSymlink exception for {path}: {ex.Message}");
            }

            System.Diagnostics.Debug.WriteLine($"ResolveSymlink: returning original path {path}");
            return path;
        }

        private FileItem? ParseLsLine(string line, string parentPath)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"ParseLsLine: parsing '{line}' with parent '{parentPath}'");

                // Skip . and .. entries
                if (line.EndsWith(" .") || line.EndsWith(" .."))
                {
                    System.Diagnostics.Debug.WriteLine($"ParseLsLine: skipping . or .. entry");
                    return null;
                }

                // Handle lines with question marks (inaccessible items)
                if (line.Contains("?????????"))
                {
                    // Extract the name (last part of the line that's not ?)
                    var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0)
                    {
                        var itemName = parts[parts.Length - 1];
                        if (itemName != "?" && itemName != "." && itemName != "..")
                        {
                            var itemFullPath = parentPath == "/" ? $"/{itemName}" : $"{parentPath}/{itemName}";
                            return new FileItem
                            {
                                Name = itemName,
                                FullPath = itemFullPath,
                                Size = 0,
                                Modified = DateTime.Now,
                                Permissions = "d?????????",
                                Owner = "?",
                                Group = "?",
                                IsDirectory = true, // Assume directory for inaccessible items
                                IsAccessible = false, // Mark as not accessible
                                Type = "" // Directories have no type
                            };
                        }
                    }
                    return null;
                }

                // Parse normal ls -la output
                // Match pattern: permissions links owner group size date time name
                var pattern = @"^([dlcbps\-][rwxst\-]{9})\s+(\d+)\s+(\S+)\s+(\S+)\s+(\d+)\s+(.+?)\s+(\S+(?:\s+\S+)*)$";
                var match = Regex.Match(line, pattern);

                if (!match.Success)
                {
                    System.Diagnostics.Debug.WriteLine($"ParseLsLine: pattern 1 failed, trying pattern 2");
                    // Try simpler pattern for different Android versions
                    pattern = @"^([dlcbps\-][rwxst\-]{9})\s+\d+\s+(\S+)\s+(\S+)\s+(\d+)\s+(.+)$";
                    match = Regex.Match(line, pattern);

                    if (!match.Success)
                    {
                        System.Diagnostics.Debug.WriteLine($"ParseLsLine: pattern 2 also failed for: '{line}'");
                        // Try a third pattern that handles cases with optional fields differently
                        // Some Android versions might have different spacing or field order
                        pattern = @"^([dlcbps\-][rwxst\-]{9})\s+(\S+)\s+(\S+)\s+(\d+)\s+(.+)$";
                        match = Regex.Match(line, pattern);

                        if (!match.Success)
                        {
                            System.Diagnostics.Debug.WriteLine($"ParseLsLine: all patterns failed for line: '{line}'");
                            return null;
                        }
                        System.Diagnostics.Debug.WriteLine($"ParseLsLine: pattern 3 matched");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"ParseLsLine: pattern 2 matched");
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"ParseLsLine: pattern 1 matched");
                }

                var permissions = match.Groups[1].Value;
                var owner = match.Groups[3].Value;
                var group = match.Groups[4].Value;
                var size = long.Parse(match.Groups[5].Value);

                // Get the rest of the line after size for date and name
                var remainingIndex = match.Groups[5].Index + match.Groups[5].Length;
                var remaining = line.Substring(remainingIndex).TrimStart();

                // Split remaining into date/time and name
                // Date formats can be: "2008-12-31 19:00" or "Aug 9 14:57"
                string dateStr;
                string name;

                // Try to match date patterns
                // Formats: "2024-01-15 14:23" or "Jan 15 14:23" or "Jan 15  2024"
                var dateMatch = Regex.Match(remaining, @"^(\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}|\w{3}\s+\d{1,2}\s+(?:\d{2}:\d{2}|\s?\d{4}))\s+(.+)$");
                if (dateMatch.Success)
                {
                    dateStr = dateMatch.Groups[1].Value;
                    name = dateMatch.Groups[2].Value;
                }
                else
                {
                    // Fallback: assume first two words are date/time
                    var words = remaining.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (words.Length >= 3)
                    {
                        dateStr = $"{words[0]} {words[1]}";
                        name = string.Join(" ", words.Skip(2));
                    }
                    else
                    {
                        dateStr = DateTime.Now.ToString();
                        name = remaining;
                    }
                }
                System.Diagnostics.Debug.WriteLine($"ParseLsLine: remaining='{remaining}', dateStr='{dateStr}', name='{name}'");

                // Handle symlinks
                string? symlinkTarget = null;
                if (name.Contains(" -> "))
                {
                    var linkParts = name.Split(new[] { " -> " }, StringSplitOptions.None);
                    name = linkParts[0];
                    symlinkTarget = linkParts[1];
                }

                // Skip . and ..
                if (name == "." || name == "..") return null;

                var fullPath = parentPath == "/" ? $"/{name}" : $"{parentPath}/{name}";

                // Determine if it's a directory (including symlinks to directories)
                bool isDirectory = permissions.StartsWith("d") ||
                                  (permissions.StartsWith("l") && IsSymlinkDirectory(fullPath));

                // Extract file extension (without dot) for Type property
                string fileType = "";
                if (!isDirectory && name.Contains('.'))
                {
                    var lastDotIndex = name.LastIndexOf('.');
                    if (lastDotIndex > 0 && lastDotIndex < name.Length - 1)
                    {
                        fileType = name.Substring(lastDotIndex + 1).ToLowerInvariant();
                    }
                }

                return new FileItem
                {
                    Name = name,
                    FullPath = fullPath,
                    Size = size,
                    Modified = ParseAndroidDate(dateStr),
                    Permissions = permissions,
                    Owner = owner,
                    Group = group,
                    IsDirectory = isDirectory,
                    IsAccessible = !permissions.Contains("?"),
                    FileType = permissions.StartsWith("l") ? "symlink" : "",
                    Type = fileType
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to parse line: {line}. Error: {ex.Message}");
                return null;
            }
        }

        private bool IsSymlinkDirectory(string symlinkPath)
        {
            try
            {
                var escapedPath = EscapePathForShell(symlinkPath);
                var result = adbService.ExecuteShellCommand($"test -d {escapedPath} 2>/dev/null && echo 'dir' || echo 'file'");
                System.Diagnostics.Debug.WriteLine($"IsSymlinkDirectory: test -d {escapedPath} returned: '{result.Trim()}'");

                // Handle cases where the command might fail silently or return unexpected output
                var trimmed = result.Trim();
                if (trimmed == "dir")
                    return true;
                if (trimmed == "file")
                    return false;

                // If we get unexpected output, try alternative: ls -ld and check first character
                result = adbService.ExecuteShellCommand($"ls -ld {escapedPath} 2>/dev/null");
                System.Diagnostics.Debug.WriteLine($"IsSymlinkDirectory fallback ls -ld: '{result.Trim()}'");
                if (!string.IsNullOrWhiteSpace(result) && result.Length > 0)
                {
                    // If first char is 'd' (directory) or 'l' (symlink pointing to dir), treat as directory
                    return result[0] == 'd' || (result[0] == 'l' && result.Contains("/"));
                }

                // Default to treating as directory for navigation purposes
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"IsSymlinkDirectory exception for {symlinkPath}: {ex.Message}");
                // Assume it's a directory if we can't determine
                return true;
            }
        }

        private DateTime ParseAndroidDate(string dateStr)
        {
            try
            {
                // Try different date formats that Android might use
                string[] formats = {
                    "yyyy-MM-dd HH:mm",
                    "MMM d HH:mm",
                    "MMM dd HH:mm",
                    "MMM  d HH:mm", // Double space for single digit days
                    "yyyy-MM-dd HH:mm:ss"
                };

                // Clean up the date string
                dateStr = Regex.Replace(dateStr.Trim(), @"\s+", " ");

                foreach (var format in formats)
                {
                    if (DateTime.TryParseExact(dateStr, format,
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out DateTime result))
                    {
                        // If year is missing (MMM d HH:mm format), use current year
                        if (result.Year == 1)
                        {
                            result = new DateTime(DateTime.Now.Year, result.Month, result.Day,
                                                 result.Hour, result.Minute, result.Second);
                        }
                        return result;
                    }
                }
            }
            catch { }

            return DateTime.Now;
        }

        public FolderNode GetFolderTree(string rootPath)
        {
            var root = new FolderNode
            {
                Name = rootPath == "/" ? "Root (/)" : System.IO.Path.GetFileName(rootPath),
                FullPath = rootPath,
                IsAccessible = true
            };

            // Do ONE ls command to get the actual root contents with symlink info
            try
            {
                var output = adbService.ExecuteShellCommand("ls -la / 2>&1");
                var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                  .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("total"))
                                  .ToList();

                foreach (var line in lines)
                {
                    var item = ParseLsLine(line, "/");
                    if (item != null && item.IsDirectory && item.Name != "." && item.Name != "..")
                    {
                        var folderNode = new FolderNode
                        {
                            Name = item.Name,
                            FullPath = item.FullPath,
                            IsAccessible = true
                        };
                        // Add dummy child to show expand arrow
                        folderNode.Children.Add(new FolderNode { Name = "" });
                        root.Children.Add(folderNode);
                    }
                }
            }
            catch
            {
                // Fallback to hardcoded list if ls fails
                var commonDirs = new[] {
                    "acct", "cache", "config", "data", "dev", "mnt", "proc",
                    "sdcard", "storage", "sys", "system", "vendor", "bin",
                    "etc", "sbin", "oem", "apex", "product"
                };

                foreach (var dir in commonDirs)
                {
                    var folderNode = new FolderNode
                    {
                        Name = dir,
                        FullPath = "/" + dir,
                        IsAccessible = true
                    };
                    folderNode.Children.Add(new FolderNode { Name = "" });
                    root.Children.Add(folderNode);
                }
            }

            return root;
        }

        public void CreateFolder(string path)
        {
            // Use single quotes for paths with special characters in rm, cp, mv commands
            var escapedPath = EscapePathForShell(path);
            adbService.ExecuteShellCommand($"mkdir -p {escapedPath}");
            // Note: We intentionally don't chmod the directory after creation.
            // On Android's FUSE filesystem (especially /sdcard/Android/data/),
            // changing permissions can break subsequent adb push operations
            // by causing "remote fchown failed: Operation not permitted" errors.
        }

        public void DeleteItem(string path)
        {
            // Get directory and generate a simple temporary name
            var directory = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(directory)) directory = "/sdcard";
            var tempName = $"{directory}/tmpdel_{DateTime.Now.Ticks}";

            var pathEscaped = EscapePathForShell(path);

            // Check if it's a directory
            var checkDir = adbService.ExecuteShellCommand($"test -d {pathEscaped} && echo dir || echo file");
            bool isDirectory = checkDir.Trim() == "dir";

            // First attempt: rename to a simple name, then delete
            var renameCmd = $"mv {pathEscaped} '{tempName}'";
            var renameResult = adbService.ExecuteShellCommand(renameCmd + " 2>&1");

            // Check if rename succeeded (no error output means success)
            if (string.IsNullOrWhiteSpace(renameResult) || (!renameResult.Contains("cannot") && !renameResult.Contains("failed") && !renameResult.Contains("No such")))
            {
                // Verify the rename actually happened
                var tempExists = adbService.ExecuteShellCommand($"test -e '{tempName}' && echo exists || echo missing");
                if (tempExists.Trim() == "exists")
                {
                    // Rename succeeded, delete the temp file
                    string deleteResult;
                    if (isDirectory)
                        deleteResult = adbService.ExecuteShellCommand($"rm -rf '{tempName}' 2>&1");
                    else
                        deleteResult = adbService.ExecuteShellCommand($"rm -f '{tempName}' 2>&1");

                    var checkDeleted = adbService.ExecuteShellCommand($"test -e '{tempName}' && echo exists || echo gone");
                    if (checkDeleted.Trim() == "exists")
                    {
                        throw new Exception($"Failed to delete renamed file. The file was renamed to {tempName} but could not be deleted. Delete result: {deleteResult}");
                    }
                    return;
                }
                // else: mv produced no error but didn't rename - fall through to direct delete
            }

            // Rename failed or didn't work, try direct deletion
            string directResult;
            if (isDirectory)
                directResult = adbService.ExecuteShellCommand($"rm -rf {pathEscaped} 2>&1");
            else
                directResult = adbService.ExecuteShellCommand($"rm -f {pathEscaped} 2>&1");

            var checkExists = adbService.ExecuteShellCommand($"test -e {pathEscaped} && echo exists || echo gone");
            if (checkExists.Trim() == "exists")
            {
                throw new Exception($"Failed to delete file: {path}. Rename result: {renameResult}. Delete result: {directResult}");
            }
        }
        
        private string EscapePathForShell(string path)
        {
            // Escape shell special characters with backslashes.
            // We can't use double quotes because Windows argv parsing strips them
            // before ADB receives the arguments, leaving special chars unprotected.
            // Backslash escaping works directly and survives Windows argv parsing.
            var sb = new System.Text.StringBuilder(path.Length * 2);
            foreach (char c in path)
            {
                // Characters that need escaping in shell
                if (" \t\"'$`\\!#&|;(){}[]<>?*~^".IndexOf(c) >= 0)
                {
                    sb.Append('\\');
                }
                sb.Append(c);
            }
            return sb.ToString();
        }


        public bool PullFile(string remotePath, string localPath)
        {
            if (adbService.IsRootMode)
            {
                return adbService.PullFileAsRoot(remotePath, localPath);
            }
            return adbService.PullFile(remotePath, localPath);
        }

        public void PushFile(string localPath, string remotePath)
        {
            PushFile(localPath, remotePath, true);
        }

        public void PushFile(string localPath, string remotePath, bool setPermissions)
        {
            if (adbService.IsRootMode)
            {
                adbService.PushFileAsRoot(localPath, remotePath, setPermissions);
                return;
            }
            adbService.PushFile(localPath, remotePath, setPermissions);
        }

        public void SetFilePermissions(string remotePath, string permissions = "660")
        {
            adbService.SetFilePermissions(remotePath, permissions);
        }
    }
}