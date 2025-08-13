using System;

namespace AdbExplorer.Models
{
    public class FavoriteItem
    {
        public string Path { get; set; }
        public string DisplayName { get; set; }
        public DateTime DateAdded { get; set; }
        public bool IsPlaceholder { get; set; }

        public FavoriteItem()
        {
            Path = string.Empty;
            DisplayName = string.Empty;
            DateAdded = DateTime.Now;
            IsPlaceholder = false;
        }

        public FavoriteItem(string path, string displayName)
        {
            Path = path;
            DisplayName = displayName;
            DateAdded = DateTime.Now;
            IsPlaceholder = false;
        }
        
        public static FavoriteItem CreatePlaceholder()
        {
            return new FavoriteItem
            {
                Path = string.Empty,
                DisplayName = "Favorites:",
                IsPlaceholder = true
            };
        }
    }
}