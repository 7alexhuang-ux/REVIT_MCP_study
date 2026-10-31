#nullable disable
using System;
using System.Security.Cryptography;
using System.Text;

namespace RevitMCP.Core.IssueManager
{
    /// <summary>
    /// OAuth 2.0 PKCE（RFC 7636，S256）與 state 產生；另提供固定時間字串比對。
    /// 這裡的值（verifier／state）都是祕密，不得寫進日誌或例外訊息。
    /// </summary>
    public static class Pkce
    {
        public const int VerifierMinLength = 43;
        public const int VerifierMaxLength = 128;

        /// <summary>Base64url，無填充（RFC 4648 §5）。</summary>
        public static string Base64Url(byte[] data)
        {
            return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        /// <summary>32 位元組亂數 → 43 字元 verifier（落在 RFC 7636 的 43–128 範圍）。</summary>
        public static string CreateVerifier() { return Base64Url(RandomBytes(32)); }

        /// <summary>32 位元組亂數 → 43 字元 state（網站端要求 8–128 字元 [A-Za-z0-9._~-]）。</summary>
        public static string CreateState() { return Base64Url(RandomBytes(32)); }

        /// <summary>code_challenge = BASE64URL(SHA256(ASCII(code_verifier)))。</summary>
        public static string ComputeChallenge(string verifier)
        {
            if (!IsValidVerifier(verifier)) throw new ArgumentException("code_verifier 格式不合法（需 43–128 字元，僅限 A-Z a-z 0-9 - . _ ~）。");
            using (var sha = SHA256.Create())
            {
                return Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
            }
        }

        public static bool IsValidVerifier(string verifier)
        {
            if (verifier == null || verifier.Length < VerifierMinLength || verifier.Length > VerifierMaxLength) return false;
            foreach (var c in verifier)
            {
                var ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '.' || c == '_' || c == '~';
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>固定時間比對（長度不同直接 false；net48 沒有 CryptographicOperations.FixedTimeEquals）。</summary>
        public static bool SecureEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            var x = Encoding.UTF8.GetBytes(a);
            var y = Encoding.UTF8.GetBytes(b);
            if (x.Length != y.Length) return false;
            var diff = 0;
            for (var i = 0; i < x.Length; i++) diff |= x[i] ^ y[i];
            return diff == 0;
        }

        private static byte[] RandomBytes(int count)
        {
            var buffer = new byte[count];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(buffer);
            }
            return buffer;
        }
    }
}
