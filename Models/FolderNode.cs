using System.Collections.ObjectModel;

namespace AdbExplorer.Models
{
    public class FolderNode
    {
        public string Name { get; set; } = "";
        public string FullPath { get; set; } = "";
        public bool IsAccessible { get; set; } = true;
        public ObservableCollection<FolderNode> Children { get; set; }

        public FolderNode()
        {
            Children = new ObservableCollection<FolderNode>();
        }
    }
}
