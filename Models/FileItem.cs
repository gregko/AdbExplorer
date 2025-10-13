using System;

namespace AdbExplorer.Models
{
    [Serializable]
    public class FileItem
    {
        public string Name { get; set; } = "";
        public string FullPath { get; set; } = "";
        public long Size { get; set; }
        public DateTime Modified { get; set; }
        public string Permissions { get; set; } = "";
        public string Owner { get; set; } = "";
        public string Group { get; set; } = "";
        public bool IsDirectory { get; set; }
        public bool IsAccessible { get; set; } = true;
        public string FileType { get; set; } = "";
    }
}
