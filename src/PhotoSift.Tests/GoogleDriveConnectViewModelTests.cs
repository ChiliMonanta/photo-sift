using System.Collections.Concurrent;
using System.Net;
using PhotoSift.Core.GoogleDrive.Auth;
using PhotoSift.Desktop.ViewModels;

namespace PhotoSift.Tests;

public sealed class GoogleDriveConnectViewModelTests
{
    private const string TokenResponse =
        """{"access_token":"access-1","expires_in":3600,"refresh_token":"refresh-1","scope":"https://www.googleapis.com/auth/drive.metadata.readonly"}""";

    [Fact]
    public void Connecting_updates_the_view_on_the_UI_thread_without_a_restart()
    {
        var store = new FakeTokenStore();
        var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Json(HttpStatusCode.OK, TokenResponse));
        var connection = new DriveConnection(
            new GoogleOAuthClient(new HttpClient(handler), new OAuthClientCredentials("id", "secret")),
            store,
            _ => new FakeDriveApi());

        UiThread.Run(async () =>
        {
            var uiThread = Environment.CurrentManagedThreadId;
            var vm = new GoogleDriveViewModel(connection, new FakeBrowser().OpenAsync);
            var changesOffUiThread = new List<string?>();
            vm.PropertyChanged += (_, e) =>
            {
                if (Environment.CurrentManagedThreadId != uiThread)
                {
                    changesOffUiThread.Add(e.PropertyName);
                }
            };

            await vm.ConnectCommand.ExecuteAsync(null);

            Assert.Empty(changesOffUiThread);
            Assert.True(vm.IsConnected);
            Assert.Equal("Connected to Google Drive as user@example.com (read-only).", vm.StatusText);
            Assert.True(vm.BrowseCommand.CanExecute(null));
            Assert.NotNull(store.Saved);
        });
    }

    /// <summary>Runs async code on one thread with a message loop, like a UI thread.</summary>
    private sealed class UiThread : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

        public static void Run(Func<Task> body)
        {
            var previous = Current;
            var context = new UiThread();
            SetSynchronizationContext(context);
            try
            {
                var task = body();
                task.ContinueWith(_ => context._queue.CompleteAdding(), TaskScheduler.Default);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                foreach (var (callback, state) in context._queue.GetConsumingEnumerable(timeout.Token))
                {
                    callback(state);
                }

                task.GetAwaiter().GetResult();
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();
    }
}
