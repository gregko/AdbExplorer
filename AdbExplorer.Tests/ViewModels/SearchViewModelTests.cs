using Xunit;
using AdbExplorer.ViewModels;
using AdbExplorer.Models;
using System.Collections.ObjectModel;
using AdbExplorer.Helpers;

namespace AdbExplorer.Tests.ViewModels
{
    public class SearchViewModelTests
    {
        [Fact]
        public void SearchQuery_ShouldRaisePropertyChanged()
        {
            var vm = new SearchViewModel();
            Assert.PropertyChanged(vm, nameof(vm.SearchQuery), () => vm.SearchQuery = "test");
        }

        [Fact]
        public void IsRecursive_ShouldRaisePropertyChanged()
        {
            var vm = new SearchViewModel();
            Assert.PropertyChanged(vm, nameof(vm.IsRecursive), () => vm.IsRecursive = true);
        }

        [Fact]
        public void FilterPredicate_ShouldMatchFileName_CaseInsensitive()
        {
            var vm = new SearchViewModel();
            vm.SearchQuery = "TeSt";

            var file = new FileItem { Name = "ThisIsATestFile.txt" };
            var noMatch = new FileItem { Name = "OtherFile.txt" };

            // We expect the ViewModel to provide a predicate or filter function
            // For now, let's assume it exposes a method or property logic we can test
            // Or we test that changing query triggers some event.
            
            // Actually, usually we expose a Predicate<object> for CollectionView
            // Let's assume Filter method
            
            Assert.True(vm.Filter(file));
            Assert.False(vm.Filter(noMatch));
        }

        [Fact]
        public void Filter_ShouldReturnTrue_WhenQueryIsEmpty()
        {
            var vm = new SearchViewModel();
            vm.SearchQuery = "";
            var file = new FileItem { Name = "AnyFile.txt" };
            Assert.True(vm.Filter(file));
        }

        [Fact]
        public void Filter_ShouldMatchSubstrings()
        {
             var vm = new SearchViewModel();
            vm.SearchQuery = "era";
            
            Assert.True(vm.Filter(new FileItem { Name = "Camera" }));
            Assert.True(vm.Filter(new FileItem { Name = "Opera.mp3" }));
            Assert.False(vm.Filter(new FileItem { Name = "Car.png" }));
        }
    }
}
