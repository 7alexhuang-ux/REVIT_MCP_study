using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using RevitMCP.Core.IssueManager;

namespace RevitMCP.Tests.IssueManager
{
    [TestFixture]
    public class PkceTests
    {
        // RFC 7636 附錄 B 的測試向量
        private const string RfcVerifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        private const string RfcChallenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

        [Test]
        public void X02_ChallengeMatchesRfc7636TestVector()
        {
            Assert.AreEqual(RfcChallenge, Pkce.ComputeChallenge(RfcVerifier));
        }

        [Test]
        public void GeneratedVerifierIsValidAndUnique()
        {
            var a = Pkce.CreateVerifier();
            var b = Pkce.CreateVerifier();
            Assert.AreNotEqual(a, b);
            Assert.IsTrue(Pkce.IsValidVerifier(a));
            Assert.AreEqual(43, a.Length);
            Assert.IsTrue(Regex.IsMatch(Pkce.ComputeChallenge(a), "^[A-Za-z0-9_-]{43}$"));
        }

        [Test]
        public void StateMatchesServerRequirements()
        {
            var state = Pkce.CreateState();
            Assert.IsTrue(Regex.IsMatch(state, "^[A-Za-z0-9._~-]{8,128}$"));
            Assert.AreNotEqual(state, Pkce.CreateState());
        }

        [TestCase("")]
        [TestCase("short")]
        [TestCase("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjX+")] // 非法字元 +
        public void InvalidVerifierIsRejected(string verifier)
        {
            Assert.IsFalse(Pkce.IsValidVerifier(verifier));
            Assert.Throws<ArgumentException>(() => Pkce.ComputeChallenge(verifier));
        }

        [Test]
        public void SecureEqualsComparesContentAndLength()
        {
            Assert.IsTrue(Pkce.SecureEquals("abc", "abc"));
            Assert.IsFalse(Pkce.SecureEquals("abc", "abd"));
            Assert.IsFalse(Pkce.SecureEquals("abc", "abcd"));
            Assert.IsFalse(Pkce.SecureEquals(null, "abc"));
        }
    }

    [TestFixture]
    public class LoopbackCallbackListenerTests
    {
        private static async Task<string> RawGetAsync(int port, string target, string host = null)
        {
            using (var client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
                var stream = client.GetStream();
                var request = "GET " + target + " HTTP/1.1\r\nHost: " + (host ?? "127.0.0.1:" + port) + "\r\nConnection: close\r\n\r\n";
                var bytes = Encoding.ASCII.GetBytes(request);
                await stream.WriteAsync(bytes, 0, bytes.Length);
                var buffer = new byte[4096];
                var read = await stream.ReadAsync(buffer, 0, buffer.Length);
                var text = Encoding.UTF8.GetString(buffer, 0, read);
                return text.Substring(0, text.IndexOf("\r\n", StringComparison.Ordinal));
            }
        }

        [Test]
        public void X03_BindsLoopbackOnly()
        {
            using (var listener = LoopbackCallbackListener.Start("state-1234567"))
            {
                Assert.AreEqual(IPAddress.Loopback, listener.LocalAddress);
                Assert.That(listener.Port, Is.InRange(1024, 65535));
            }
        }

        [Test]
        public async Task X03_ValidCallbackReturnsCode()
        {
            using (var listener = LoopbackCallbackListener.Start("state-valid-1"))
            {
                var wait = listener.WaitForCallbackAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
                var status = await RawGetAsync(listener.Port, "/callback?code=imac_abc%2Bdef&state=state-valid-1");
                var result = await wait;
                StringAssert.Contains("200", status);
                Assert.AreEqual("imac_abc+def", result.Code);
                Assert.IsNull(result.Error);
            }
        }

        [Test]
        public async Task X03_StateMismatchIsRejectedAndListenerKeepsWaitingForTheRightOne()
        {
            using (var listener = LoopbackCallbackListener.Start("state-right-12"))
            {
                var wait = listener.WaitForCallbackAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
                var bad = await RawGetAsync(listener.Port, "/callback?code=evil&state=state-wrong-12");
                StringAssert.Contains("400", bad);
                Assert.IsFalse(wait.IsCompleted, "state 不符不得結束等待");
                var missing = await RawGetAsync(listener.Port, "/callback?code=evil");
                StringAssert.Contains("400", missing);
                var good = await RawGetAsync(listener.Port, "/callback?code=real&state=state-right-12");
                StringAssert.Contains("200", good);
                Assert.AreEqual("real", (await wait).Code);
            }
        }

        [Test]
        public async Task X03_WrongHostWrongPathAndWrongMethodAreRejected()
        {
            using (var listener = LoopbackCallbackListener.Start("state-host-12"))
            {
                var wait = listener.WaitForCallbackAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
                StringAssert.Contains("400", await RawGetAsync(listener.Port, "/callback?code=x&state=state-host-12", "evil.example:" + listener.Port));
                StringAssert.Contains("404", await RawGetAsync(listener.Port, "/favicon.ico"));
                StringAssert.Contains("404", await RawGetAsync(listener.Port, "/callback/extra?code=x&state=state-host-12"));
                Assert.IsFalse(wait.IsCompleted);
            }
        }

        [Test]
        public async Task AccessDeniedCallbackReturnsError()
        {
            using (var listener = LoopbackCallbackListener.Start("state-deny-123"))
            {
                var wait = listener.WaitForCallbackAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
                await RawGetAsync(listener.Port, "/callback?error=access_denied&state=state-deny-123");
                var result = await wait;
                Assert.AreEqual("access_denied", result.Error);
                Assert.IsNull(result.Code);
            }
        }

        [Test]
        public async Task X03_TimeoutClosesTheListener()
        {
            var listener = LoopbackCallbackListener.Start("state-timeout-1");
            var port = listener.Port;
            Assert.ThrowsAsync<TimeoutException>(async () => await listener.WaitForCallbackAsync(TimeSpan.FromMilliseconds(300), CancellationToken.None));
            await Task.Delay(100);
            using (var client = new TcpClient())
            {
                Assert.ThrowsAsync<SocketException>(async () => await client.ConnectAsync(IPAddress.Loopback, port), "逾時後埠應已關閉");
            }
        }

        [Test]
        public void CancellationStopsWaiting()
        {
            using (var listener = LoopbackCallbackListener.Start("state-cancel-1"))
            using (var cts = new CancellationTokenSource())
            {
                var wait = listener.WaitForCallbackAsync(TimeSpan.FromSeconds(30), cts.Token);
                cts.Cancel();
                Assert.CatchAsync<OperationCanceledException>(async () => await wait);
            }
        }
    }
}
