#nullable disable
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json;

namespace RevitMCP.Core.IssueManager
{
    /// <summary>需要跨次啟動保存的登入資料。只有 refresh token 是祕密；access token 永遠只放記憶體。</summary>
    public sealed class StoredCredentials
    {
        public string BaseUrl { get; set; }
        public string RefreshToken { get; set; }
        public DateTimeOffset RefreshExpiresAt { get; set; }
        public string UserId { get; set; }
        public string UserEmail { get; set; }
        public string UserName { get; set; }
        public string UserRole { get; set; }
        public string AccountRole { get; set; }
    }

    public interface ITokenVault
    {
        /// <summary>讀取；沒有檔案、解不開（換了 Windows 使用者）或內容壞掉都回 null，不丟例外。</summary>
        StoredCredentials Load();
        void Save(StoredCredentials credentials);
        void Delete();
    }

    /// <summary>
    /// 以 Windows DPAPI（使用者層級）加密後寫檔；檔案裡沒有任何明文。
    /// 寫入用「暫存檔再取代」避免寫到一半當機留下壞檔。
    /// 直接呼叫 crypt32（P/Invoke）而不是 ProtectedData，因為 Revit 2025+（.NET 8）預設不含該類別。
    /// </summary>
    public sealed class DpapiTokenVault : ITokenVault
    {
        // 額外熵：同一使用者下，別的程式拿到檔案也要多知道這個值才解得開（非祕密，只是增加一道門檻）。
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RevitMCP.IssueManager.RefreshToken.v1");
        private readonly string _path;

        public DpapiTokenVault(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path 不可為空。");
            _path = path;
        }

        public string FilePath { get { return _path; } }

        public static string DefaultPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RevitMCP", "issue-manager", "auth.bin");
        }

        public StoredCredentials Load()
        {
            try
            {
                if (!File.Exists(_path)) return null;
                var blob = File.ReadAllBytes(_path);
                var plain = Dpapi.Unprotect(blob, Entropy);
                return JsonConvert.DeserializeObject<StoredCredentials>(Encoding.UTF8.GetString(plain));
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void Save(StoredCredentials credentials)
        {
            if (credentials == null) throw new ArgumentNullException("credentials");
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var plain = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(credentials));
            var blob = Dpapi.Protect(plain, Entropy);
            var temp = _path + ".tmp";
            File.WriteAllBytes(temp, blob);
            if (File.Exists(_path)) File.Delete(_path);
            File.Move(temp, _path);
        }

        public void Delete()
        {
            try
            {
                if (File.Exists(_path)) File.Delete(_path);
                var temp = _path + ".tmp";
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static class Dpapi
    {
        private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int cbData;
            public IntPtr pbData;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DataBlob dataIn, string description, ref DataBlob entropy,
            IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy,
            IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr handle);

        public static byte[] Protect(byte[] plain, byte[] entropy)
        {
            return Transform(plain, entropy, true);
        }

        public static byte[] Unprotect(byte[] blob, byte[] entropy)
        {
            return Transform(blob, entropy, false);
        }

        private static byte[] Transform(byte[] input, byte[] entropy, bool protect)
        {
            var inBlob = ToBlob(input);
            var entropyBlob = ToBlob(entropy);
            var outBlob = new DataBlob();
            try
            {
                var ok = protect
                    ? CryptProtectData(ref inBlob, "RevitMCP Issue Manager", ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out outBlob)
                    : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out outBlob);
                if (!ok) throw new InvalidOperationException("DPAPI 操作失敗（錯誤碼 " + Marshal.GetLastWin32Error() + "）。");
                var result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                return result;
            }
            finally
            {
                if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
                if (entropyBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(entropyBlob.pbData);
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
            }
        }

        private static DataBlob ToBlob(byte[] bytes)
        {
            var blob = new DataBlob { cbData = bytes.Length, pbData = Marshal.AllocHGlobal(bytes.Length) };
            Marshal.Copy(bytes, 0, blob.pbData, bytes.Length);
            return blob;
        }
    }
}
