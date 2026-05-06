using AdbExplorer.Services;
using Xunit;

namespace AdbExplorer.Tests.Services
{
    public class AppServiceTests
    {
        [Fact]
        public void GetWsaAppSettingsUri_ReturnsPackageSpecificDeepLink()
        {
            string uri = AppService.GetWsaAppSettingsUri("com.android.chrome");

            Assert.Equal("wsa-client://app-settings?package=com.android.chrome", uri);
        }

        [Fact]
        public void IsSuccessfulAdbUninstall_ReturnsTrueOnlyForSuccessLine()
        {
            Assert.True(AppService.IsSuccessfulAdbUninstall("Success\r\n", ""));
            Assert.False(AppService.IsSuccessfulAdbUninstall("Failure [DELETE_FAILED_INTERNAL_ERROR]\r\n", ""));
            Assert.False(AppService.IsSuccessfulAdbUninstall("", "error: device unauthorized\r\n"));
        }
    }
}
