using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Web;

namespace PhotoSift.Core.GoogleDrive.Auth;

/// <summary>
/// Receives the OAuth redirect from the browser on <c>http://127.0.0.1:&lt;random port&gt;/</c>
/// (RFC 8252 loopback redirect). Only listens on the loopback interface, and only accepts a
/// redirect that carries the expected <c>state</c>; other requests are answered and ignored.
/// </summary>
public sealed class LoopbackReceiver : IDisposable
{
    private const int MaxRequestHeadLength = 16 * 1024;

    private readonly TcpListener _listener;

    private LoopbackReceiver(TcpListener listener)
    {
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    public string RedirectUri => $"http://127.0.0.1:{Port}/";

    public static LoopbackReceiver Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new LoopbackReceiver(listener);
    }

    /// <summary>
    /// Waits for the redirect with <paramref name="expectedState"/> and returns its query parameters
    /// (<c>code</c>, or <c>error</c> when the user declined).
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> WaitForRedirectAsync(string expectedState, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            var result = await HandleAsync(client, expectedState, cancellationToken).ConfigureAwait(false);
            if (result is not null)
            {
                return result;
            }
        }
    }

    private static async Task<IReadOnlyDictionary<string, string>?> HandleAsync(
        TcpClient client, string expectedState, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var stream = client.GetStream();

        string? requestLine;
        try
        {
            requestLine = await ReadRequestHeadAsync(stream, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when ((ex is IOException or OperationCanceledException) && !cancellationToken.IsCancellationRequested)
        {
            return null; // A slow or broken connection; keep waiting for the real redirect.
        }

        var parts = requestLine?.Split(' ');
        if (parts is not { Length: 3 } || parts[0] != "GET" ||
            !(parts[1] == "/" || parts[1].StartsWith("/?", StringComparison.Ordinal)))
        {
            await RespondAsync(stream, "404 Not Found", "Not found.", cancellationToken).ConfigureAwait(false);
            return null;
        }

        var query = HttpUtility.ParseQueryString(parts[1].Length > 1 ? parts[1][2..] : string.Empty);
        var parameters = query.AllKeys.OfType<string>().ToDictionary(k => k, k => query[k] ?? string.Empty, StringComparer.Ordinal);

        if (!parameters.TryGetValue("state", out var state) || state != expectedState ||
            !(parameters.ContainsKey("code") || parameters.ContainsKey("error")))
        {
            await RespondAsync(stream, "400 Bad Request",
                "This sign-in link is not valid. Go back to PhotoSift and try again.", cancellationToken).ConfigureAwait(false);
            return null;
        }

        var message = parameters.ContainsKey("code")
            ? "PhotoSift is connected to Google Drive. You can close this tab and return to PhotoSift."
            : "Google Drive was not connected. You can close this tab and return to PhotoSift.";
        await RespondAsync(stream, "200 OK", message, cancellationToken).ConfigureAwait(false);
        return parameters;
    }

    /// <summary>
    /// Reads the request line and headers, and returns the request line. The headers are not used,
    /// but they must be read: closing the connection with unread data resets it, and the browser
    /// would then show a connection error instead of the response.
    /// </summary>
    private static async Task<string?> ReadRequestHeadAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var head = new StringBuilder();
        var buffer = new byte[1024];
        while (head.Length < MaxRequestHeadLength)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            head.Append(Encoding.Latin1.GetString(buffer, 0, read));
            var text = head.ToString();
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal) || text.Contains("\n\n", StringComparison.Ordinal))
            {
                return text[..text.IndexOf('\n')].TrimEnd('\r');
            }
        }

        return null;
    }

    private static async Task RespondAsync(NetworkStream stream, string status, string message, CancellationToken cancellationToken)
    {
        var html = $"<!doctype html><html><head><meta charset=\"utf-8\"><title>PhotoSift</title></head>" +
                   $"<body style=\"font-family:sans-serif;margin:3em\"><p>{WebUtility.HtmlEncode(message)}</p></body></html>";
        var body = Encoding.UTF8.GetBytes(html);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\n" +
            "Cache-Control: no-store\r\nConnection: close\r\n\r\n");
        try
        {
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The browser closed the connection; the redirect itself was still received.
        }
    }

    public void Dispose() => _listener.Stop();
}
