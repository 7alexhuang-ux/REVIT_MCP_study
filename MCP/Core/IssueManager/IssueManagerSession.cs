#nullable disable
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using Autodesk.Revit.UI;

namespace RevitMCP.Core.IssueManager
{
    /// <summary>用系統預設瀏覽器開網址；只接受 http／https。</summary>
    internal sealed class SystemBrowserLauncher : IBrowserLauncher
    {
        public void Open(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new InvalidOperationException("無法開啟瀏覽器：網址格式不正確。");
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
    }

    /// <summary>
    /// 一個 Revit 工作階段共用一份登入狀態與 HttpClient（refresh token 輪替時絕不能兩份並行換發）。
    /// 網址沒設定時不建立任何網路物件，視窗只顯示設定說明。
    /// </summary>
    internal static class IssueManagerSession
    {
        private static readonly object Gate = new object();
        private static HttpClient _http;
        private static HttpClient _uploadHttp;
        private static AddinAuthService _auth;
        private static AddinApiClient _api;
        private static string _baseUrl;

        public static UploadWindowServices Create(UIApplication uiApp, ProjectSnapshot snapshot)
        {
            var services = new UploadWindowServices
            {
                Browser = new SystemBrowserLauncher(),
                Log = message => Logger.Info("[IssueManager] " + message),
                OpenUrl = url => new SystemBrowserLauncher().Open(url)
            };

            string baseUrl;
            try
            {
                baseUrl = IssueManagerSettings.ResolveBaseUrl(Environment.GetEnvironmentVariable, IssueManagerSettings.DefaultConfigPath());
            }
            catch (ArgumentException ex)
            {
                services.ConfigurationError = ex.Message;
                return services;
            }
            if (baseUrl == null)
            {
                services.ConfigurationError = "尚未設定 Issue Manager 網址。請設定環境變數 " + IssueManagerSettings.EnvironmentVariable
                    + "，或在 " + IssueManagerSettings.DefaultConfigPath() + " 寫入 {\"baseUrl\": \"https://…\"}，再重新開啟視窗。";
                return services;
            }

            lock (Gate)
            {
                if (_auth == null || !string.Equals(_baseUrl, baseUrl, StringComparison.OrdinalIgnoreCase))
                {
                    var version = Assembly.GetExecutingAssembly().GetName().Version.ToString();
                    var client = ClientInfo.Create(Environment.MachineName + " · Revit " + uiApp.Application.VersionNumber, version);
                    _http = CreateHttpClient(TimeSpan.FromSeconds(60));
                    _uploadHttp = CreateHttpClient(TimeSpan.FromMinutes(15));
                    _auth = new AddinAuthService(_http, new DpapiTokenVault(DpapiTokenVault.DefaultPath()), baseUrl, client,
                        log: message => Logger.Info("[IssueManager] " + message));
                    _auth.TryRestore();
                    _api = new AddinApiClient(_http, _auth, client, _uploadHttp);
                    _baseUrl = baseUrl;
                }
                services.Auth = _auth;
                services.Api = _api;
            }

            var capturedApi = services.Api;
            services.CreateFlow = () => new UploadFlow(capturedApi, new RevitBundleExportSteps(uiApp, snapshot), new RevitApiThreadRunner(),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitMCP", "issue-manager", "bundles"));
            return services;
        }

        private static HttpClient CreateHttpClient(TimeSpan timeout)
        {
            // 不自動跟隨轉址：權杖端點與上傳網址被轉址到別處，等於把 Bearer 或授權碼送出去。
            var handler = new HttpClientHandler { AllowAutoRedirect = false };
            return new HttpClient(handler) { Timeout = timeout };
        }
    }
}
