using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;

namespace NGitLab.Tests.RepositoryClient {
    public class RepositoryClientPaginationTests {
        [TestCase(false, 2)]
        [TestCase(true, 1)]
        public void GetCommitsControlsPagination(bool firstPageOnly, int expectedCount) {
            using (var server = new CommitServer()) {
                var commits = server.Repository.GetCommits("main", firstPageOnly, perPage: 50).ToArray();
                Assert.That(commits.Length, Is.EqualTo(expectedCount));
                Assert.That(server.Requests.Count, Is.EqualTo(expectedCount));
                Assert.That(server.Requests.First(), Does.Contain("per_page=50"));
                Assert.That(server.Requests.First(), Does.Contain("ref_name=main"));
            }
        }

        [Test]
        public void ExistingGetCommitsOverloadStillFetchesAllPages() {
            using (var server = new CommitServer()) {
                Assert.That(server.Repository.GetCommits("main", 50).Count(), Is.EqualTo(2));
                Assert.That(server.Requests.Count, Is.EqualTo(2));
            }
        }

        [Test]
        public void FirstPageOnlyRespectsRequestedPage() {
            using (var server = new CommitServer()) {
                Assert.That(server.Repository.GetCommits("main", true, page: 2).Single().Message, Is.EqualTo("second"));
                Assert.That(server.Requests.Count, Is.EqualTo(1));
                Assert.That(server.Requests.Single(), Does.Contain("page=2"));
            }
        }

        [Test]
        public void FirstPageOnlyHandlesEmptyHistory() {
            using (var server = new CommitServer()) {
                Assert.That(server.Repository.GetCommits("empty", true), Is.Empty);
                Assert.That(server.Requests.Count, Is.EqualTo(1));
            }
        }

        [TestCase("abc1234")]
        [TestCase("feature/formulas")]
        [TestCase("abcdef0123456789abcdef0123456789abcdef01")]
        public void GetCommitByRevisionUsesOneRequest(string revision) {
            using (var server = new CommitServer()) {
                Assert.That(server.Repository.GetCommit(revision).Message, Is.EqualTo("selected"));
                Assert.That(server.Requests.Count, Is.EqualTo(1));
                Assert.That(server.Requests.Single(),
                    Is.EqualTo("/api/v4/projects/1/repository/commits/" + Uri.EscapeDataString(revision)));
            }
        }

        [Test]
        public void ExistingGetCommitBySha1StillUsesOneRequest() {
            using (var server = new CommitServer()) {
                var sha = new Sha1("abcdef0123456789abcdef0123456789abcdef01");
                Assert.That(server.Repository.GetCommit(sha).Message, Is.EqualTo("selected"));
                Assert.That(server.Requests.Count, Is.EqualTo(1));
                Assert.That(server.Requests.Single(), Does.EndWith("/commits/" + sha));
            }
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" ")]
        public void GetCommitRejectsEmptyRevision(string revision) {
            using (var server = new CommitServer()) {
                Assert.Throws<ArgumentException>(() => server.Repository.GetCommit(revision));
                Assert.That(server.Requests, Is.Empty);
            }
        }

        private sealed class CommitServer : IDisposable {
            private readonly TcpListener listener;
            private readonly Task serverTask;
            private readonly string baseUrl;
            public readonly ConcurrentQueue<string> Requests = new ConcurrentQueue<string>();
            public readonly IRepositoryClient Repository;

            public CommitServer() {
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                baseUrl = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
                Repository = GitLabClient.Connect(baseUrl, "test-token").GetRepository(1);
                serverTask = Task.Run((Func<Task>)Serve);
            }

            private async Task Serve() {
                while (true) {
                    TcpClient client;
                    try {
                        client = await listener.AcceptTcpClientAsync();
                    }
                    catch (SocketException) {
                        return;
                    }
                    catch (ObjectDisposedException) {
                        return;
                    }

                    using (client)
                    using (var stream = client.GetStream())
                    using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true)) {
                        var requestLine = await reader.ReadLineAsync();
                        string header;
                        do {
                            header = await reader.ReadLineAsync();
                        } while (!string.IsNullOrEmpty(header));

                        var path = requestLine.Split(' ')[1];
                        Requests.Enqueue(path);
                        var isList = path.Contains("/commits?");
                        var secondPage = path.Contains("page=2");
                        var json = isList
                            ? (path.Contains("ref_name=empty") ? "[]" :
                                "[{\"message\":\"" + (secondPage ? "second" : "first") + "\"}]")
                            : "{\"message\":\"selected\"}";
                        var body = Encoding.UTF8.GetBytes(json);
                        var nextLink = isList && !secondPage
                            ? "Link: <" + baseUrl + "/api/v4/projects/1/repository/commits?page=2>; rel=\"next\"\r\n"
                            : "";
                        var response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n"
                            + "Content-Length: " + body.Length + "\r\nConnection: close\r\n" + nextLink + "\r\n");
                        await stream.WriteAsync(response, 0, response.Length);
                        await stream.WriteAsync(body, 0, body.Length);
                    }
                }
            }

            public void Dispose() {
                listener.Stop();
                serverTask.GetAwaiter().GetResult();
            }
        }
    }
}
