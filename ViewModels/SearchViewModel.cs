using CommunityToolkit.Mvvm.ComponentModel;
using AdbExplorer.Models;
using System;

namespace AdbExplorer.ViewModels
{
    public partial class SearchViewModel : ObservableObject
    {
        [ObservableProperty]
        private string searchQuery = "";

        [ObservableProperty]
        private bool isRecursive = false;

        public bool Filter(object item)
        {
            if (string.IsNullOrEmpty(SearchQuery))
                return true;

            if (item is FileItem fileItem)
            {
                return fileItem.Name.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }
    }
}
