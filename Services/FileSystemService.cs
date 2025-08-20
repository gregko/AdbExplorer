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

                // For ls command, we need to escape the path properly
                // Use double quotes for the ls command as it handles paths better
                var escapedPath = "\"" + resolvedPath.Replace("\"", "\\\"").Replace("$", "\\$").Replace("`", "\\`") + "\"";
                var output = adbService.ExecuteShellCommand($"ls -la {escapedPath} 2>&1");

                // Check for permission denied in output
                if (output.Contains("Permission denied") && !output.Contains("total"))
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
                // For readlink, use double quotes
                var escapedPath = "\"" + path.Replace("\"", "\\\"").Replace("$", "\\$").Replace("`", "\\`") + "\"";
                var result = adbService.ExecuteShellCommand($"readlink -f {escapedPath} 2>/dev/null");
                if (!string.IsNullOrWhiteSpace(result) && !result.Contains("No such file"))
                {
                    var resolved = result.Trim();
                    if (!string.IsNullOrWhiteSpace(resolved) && resolved != path)
                    {
                        System.Diagnostics.Debug.WriteLine($"Resolved symlink: {path} -> {resolved}");
                        return resolved;
                    }
                }
            }
            catch
            {
                // If readlink fails, just use the original path
            }

            return path;
        }

        private FileItem? ParseLsLine(string line, string parentPath)
        {
            try
            {
                // Skip . and .. entries
                if (line.EndsWith(" .") || line.EndsWith(" ..")) return null;

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
                                IsAccessible = false // Mark as not accessible
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
                    // Try simpler pattern for different Android versions
                    pattern = @"^([dlcbps\-][rwxst\-]{9})\s+\d+\s+(\S+)\s+(\S+)\s+(\d+)\s+(.+)$";
                    match = Regex.Match(line, pattern);

                    if (!match.Success)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to parse: {line}");
                        return null;
                    }
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
                var dateMatch = Regex.Match(remaining, @"^(\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}|\w{3}\s+\d{1,2}\s+\d{2}:\d{2})\s+(.+)$");
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
                    FileType = permissions.StartsWith("l") ? "symlink" : ""
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
                // Use double quotes for test command
                var escapedPath = "\"" + symlinkPath.Replace("\"", "\\\"").Replace("$", "\\$").Replace("`", "\\`") + "\"";
                var result = adbService.ExecuteShellCommand($"test -d {escapedPath} 2>/dev/null && echo 'dir' || echo 'file'");
                return result.Trim() == "dir";
            }
            catch
            {
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
            
            // Set directory permissions to 770 (drwxrwx---)
            try
            {
                adbService.ExecuteShellCommand($"chmod 770 {escapedPath}");
            }
            catch
            {
                // Ignore permission errors, some paths may not allow chmod
            }
        }

        public void DeleteItem(string path)
        {
            // For delete operations, use single quotes to handle special characters
            var escapedPath = EscapePathForShell(path);

            // Check if directory
            var checkDir = adbService.ExecuteShellCommand($"test -d {escapedPath} && echo dir || echo file");

            if (checkDir.Trim() == "dir")
            {
                adbService.ExecuteShellCommand($"rm -rf {escapedPath}");
            }
            else
            {
                adbService.ExecuteShellCommand($"rm -f {escapedPath}");
            }
        }

        // Helper method for escaping paths in shell commands that modify files (rm, cp, mv)
        private string EscapePathForShell(string path)
        {
            // Use single quotes and escape any single quotes in the path
            // This handles special characters better for rm, cp, mv commands
            return "'" + path.Replace("'", "'\\''") + "'";
        }

        public bool PullFile(string remotePath, string localPath)
        {
            return adbService.PullFile(remotePath, localPath);
        }

        public void PushFile(string localPath, string remotePath)
        {
            adbService.PushFile(localPath, remotePath);
        }
    }
}