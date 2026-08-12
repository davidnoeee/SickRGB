using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SickRGB.Obs;

/// <summary>
/// Talks to OBS Studio over its built-in websocket server.
///
/// OBS has carried this server since version 28, so nothing has to be installed; it only
/// has to be switched on, under Tools then WebSocket Server Settings.
///
/// The shape of the protocol is a handshake followed by two independent streams: requests
/// we make and answers that come back, and events OBS pushes when something changes. State
/// is therefore built once by asking, and then kept current by listening. Asking repeatedly
/// would be simpler and is what a polling design would do, but it would also be wrong: OBS
/// reports a scene change the instant it happens, and a light that follows the stream going
/// live should come on then rather than up to a second later.
///
/// Nothing here controls OBS. Every request used is a getter.
/// </summary>
public sealed class ObsClient : IAsyncDisposable
{
    /// <summary>
    /// Which event categories to receive.
    ///
    /// General(1) for OBS shutting down, Config(2) for a scene collection swap, Scenes(4),
    /// Inputs(8) for mute changes, Outputs(64) for streaming, recording and the virtual
    /// camera, and InputActiveStateChanged(1 &lt;&lt; 17) for a camera appearing on air.
    ///
    /// That last one has to be named explicitly. It is classed as high volume and is
    /// deliberately left out of the protocol's own "All" value, so subscribing to
    /// everything would still leave a camera indicator that never updates.
    /// </summary>
    private const int EventSubscriptions = 1 | 2 | 4 | 8 | 64 | (1 << 17);

    /// <summary>Close codes that mean trying again cannot help.</summary>
    private const int CloseAuthenticationFailed = 4009;
    private const int CloseUnsupportedRpcVersion = 4010;
    private const int CloseSessionInvalidated = 4011;

    private readonly string _host;
    private readonly int _port;
    private readonly string _password;

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();

    private ClientWebSocket? _socket;
    private Task? _supervisor;
    private int _requestId;

    /// <summary>
    /// The current view of OBS.
    ///
    /// Swapped whole rather than mutated. The render loop reads this from another thread
    /// sixty times a second, and replacing a reference is atomic, so it can never observe
    /// a half-applied update and neither side needs a lock.
    /// </summary>
    private volatile ObsSnapshot _snapshot = ObsSnapshot.Disconnected;

    public ObsSnapshot Snapshot => _snapshot;

    /// <summary>Which input names the caller cares about, so the rest can be ignored.</summary>
    private volatile string _micName = "";
    private volatile string _cameraName = "";

    public ObsClient(string host, int port, string password)
    {
        _host = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
        _port = port > 0 ? port : 4455;
        _password = password ?? "";
    }

    /// <summary>
    /// Names the microphone and camera to watch.
    ///
    /// Changing these does not reconnect. OBS reports mute and activity changes for every
    /// input it has, so this only decides which of them are worth recording.
    /// </summary>
    public void SetTargets(string micName, string cameraName)
    {
        _micName = micName ?? "";
        _cameraName = cameraName ?? "";
    }

    public void Start()
    {
        _supervisor ??= Task.Run(SuperviseAsync);
    }

    /// <summary>
    /// Keeps a connection up for as long as the app is running.
    ///
    /// OBS is closed most of the time, so this has to fail quietly and keep waiting. The
    /// backoff exists for that reason rather than for load: a tight retry loop against a
    /// closed OBS puts a line in its log and a notification in its tray on every attempt.
    /// </summary>
    private async Task SuperviseAsync()
    {
        int[] backoff = { 1000, 2000, 4000, 8000, 15000 };
        int attempt = 0;

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(_cts.Token).ConfigureAwait(false);

                // A session that reached the point of being identified is evidence the
                // settings are right, so the next outage starts over from a short wait.
                attempt = 0;
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                return;
            }
            catch (ObsTerminalException ex)
            {
                Publish(_snapshot with { Connected = false, Status = ex.Message });
                return;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OBS] session ended: {ex.Message}");
                Publish(ObsSnapshot.Disconnected with { Status = DescribeFailure(ex) });
            }

            int wait = backoff[Math.Min(attempt, backoff.Length - 1)];
            attempt++;

            try { await Task.Delay(wait, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private string DescribeFailure(Exception ex) => ex switch
    {
        WebSocketException => $"OBS is not answering on {_host}:{_port}. It may not be running, or its "
                            + "websocket server may be switched off under Tools, WebSocket Server Settings.",
        _ => $"Not connected to OBS: {ex.Message}",
    };

    private sealed class ObsTerminalException(string message) : Exception(message);

    // ---------------------------------------------------------------- session

    private async Task RunSessionAsync(CancellationToken ct)
    {
        using var socket = new ClientWebSocket();

        // OBS offers a JSON and a msgpack subprotocol. Naming the JSON one is what keeps
        // the server from choosing the binary one.
        socket.Options.AddSubProtocol("obswebsocket.json");
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(5));

        await socket.ConnectAsync(new Uri($"ws://{_host}:{_port}"), connectCts.Token).ConfigureAwait(false);

        _socket = socket;

        await HandshakeAsync(socket, ct).ConfigureAwait(false);
        await PrimeAsync(ct).ConfigureAwait(false);

        // Nothing in the protocol tells us the peer has gone; a machine that sleeps or a
        // cable pulled out looks exactly like an idle connection. A cheap getter on a timer
        // is the only way to notice.
        using var heartbeat = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, heartbeat.Token);

        var pump = ReceiveLoopAsync(socket, linked.Token);
        var beat = HeartbeatAsync(linked.Token);

        var finished = await Task.WhenAny(pump, beat).ConfigureAwait(false);
        heartbeat.Cancel();

        try { await finished.ConfigureAwait(false); }
        finally
        {
            _socket = null;
            FailAllPending();
        }
    }

    private async Task HandshakeAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var hello = await ReceiveOneAsync(socket, ct).ConfigureAwait(false)
                    ?? throw new IOException("OBS closed the connection before saying hello.");

        if (!hello.RootElement.TryGetProperty("op", out var op) || op.GetInt32() != 0)
            throw new IOException("OBS did not open with the message the protocol expects.");

        var d = hello.RootElement.GetProperty("d");
        int rpcVersion = d.TryGetProperty("rpcVersion", out var rpc) ? rpc.GetInt32() : 1;

        string? authentication = null;

        if (d.TryGetProperty("authentication", out var auth))
        {
            if (_password.Length == 0)
                throw new ObsTerminalException(
                    "OBS is asking for a password. Copy it from Tools, WebSocket Server Settings, "
                  + "Show Connect Info, and paste it into the Stream Status settings.");

            string salt = auth.GetProperty("salt").GetString() ?? "";
            string challenge = auth.GetProperty("challenge").GetString() ?? "";
            authentication = ComputeAuth(_password, salt, challenge);
        }

        hello.Dispose();

        var identify = new Dictionary<string, object?>
        {
            ["rpcVersion"] = Math.Min(rpcVersion, 1),
            ["eventSubscriptions"] = EventSubscriptions,
        };
        if (authentication is not null) identify["authentication"] = authentication;

        await SendAsync(new { op = 1, d = identify }, ct).ConfigureAwait(false);

        var identified = await ReceiveOneAsync(socket, ct).ConfigureAwait(false)
                         ?? throw new IOException("OBS closed the connection during the handshake.");

        try
        {
            if (!identified.RootElement.TryGetProperty("op", out var op2) || op2.GetInt32() != 2)
                throw new IOException("OBS refused the connection.");
        }
        finally { identified.Dispose(); }
    }

    /// <summary>
    /// The challenge response OBS expects.
    ///
    /// Base64 of the SHA-256 of the password and salt, then base64 of the SHA-256 of that
    /// string and the challenge. The inner digest is turned into text before the second
    /// round; hashing the raw bytes instead produces a value OBS rejects, and the failure
    /// looks exactly like a wrong password.
    /// </summary>
    private static string ComputeAuth(string password, string salt, string challenge)
    {
        string secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    // ---------------------------------------------------------------- priming

    /// <summary>
    /// Asks for everything once, because events only report changes.
    ///
    /// Without this the lights stay wrong until the user happens to toggle something, which
    /// on a stream that is already live could be a very long time.
    /// </summary>
    private async Task PrimeAsync(CancellationToken ct)
    {
        var snapshot = new ObsSnapshot { Connected = true, Status = "Connected to OBS." };

        var muted = new Dictionary<string, bool>(StringComparer.Ordinal);
        var active = new Dictionary<string, bool>(StringComparer.Ordinal);

        try
        {
            if (await RequestAsync("GetStreamStatus", null, ct).ConfigureAwait(false) is { } stream)
                snapshot = snapshot with { Streaming = Bool(stream, "outputActive") };

            if (await RequestAsync("GetRecordStatus", null, ct).ConfigureAwait(false) is { } record)
                snapshot = snapshot with
                {
                    Recording = Bool(record, "outputActive"),
                    RecordingPaused = Bool(record, "outputPaused"),
                };

            if (await RequestAsync("GetVirtualCamStatus", null, ct).ConfigureAwait(false) is { } cam)
                snapshot = snapshot with { VirtualCamera = Bool(cam, "outputActive") };

            if (await RequestAsync("GetCurrentProgramScene", null, ct).ConfigureAwait(false) is { } scene)
                snapshot = snapshot with { ProgramScene = SceneNameOf(scene) };

            if (await RequestAsync("GetSceneList", null, ct).ConfigureAwait(false) is { } scenes)
                snapshot = snapshot with { Scenes = ReadSceneNames(scenes) };

            if (await RequestAsync("GetInputList", null, ct).ConfigureAwait(false) is { } inputs)
                snapshot = snapshot with { Inputs = ReadInputs(inputs) };

            // Mute and activity are per input, so they are only worth asking about for the
            // ones the user actually pointed a light at.
            string mic = _micName, camera = _cameraName;

            if (mic.Length > 0)
            {
                var muteReply = await RequestAsync("GetInputMute",
                    new Dictionary<string, object?> { ["inputName"] = mic }, ct).ConfigureAwait(false);
                if (muteReply is { } m) muted[mic] = Bool(m, "inputMuted");
            }

            if (camera.Length > 0)
            {
                var activeReply = await RequestAsync("GetSourceActive",
                    new Dictionary<string, object?> { ["sourceName"] = camera }, ct).ConfigureAwait(false);
                if (activeReply is { } a) active[camera] = Bool(a, "videoActive");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OBS] priming failed: {ex.Message}");
        }

        Publish(snapshot with { InputMuted = muted, InputActive = active });
    }

    // ---------------------------------------------------------------- receiving

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            using var message = await ReceiveOneAsync(socket, ct).ConfigureAwait(false);
            if (message is null) break;

            try { Dispatch(message.RootElement); }
            catch (Exception ex) { Debug.WriteLine($"[OBS] could not handle a message: {ex.Message}"); }
        }

        if (socket.CloseStatus is { } status)
        {
            int code = (int)status;
            string reason = socket.CloseStatusDescription ?? "";

            if (code is CloseAuthenticationFailed)
                throw new ObsTerminalException(
                    "OBS rejected the password. Copy it again from Tools, WebSocket Server Settings, "
                  + "Show Connect Info.");

            if (code is CloseUnsupportedRpcVersion)
                throw new ObsTerminalException(
                    "This version of OBS speaks a websocket protocol SickRGB does not. OBS 28 or newer "
                  + "is needed.");

            if (code is CloseSessionInvalidated)
                throw new ObsTerminalException(
                    "OBS disconnected SickRGB. Reopen the Stream Status settings to connect again.");

            throw new IOException($"OBS closed the connection ({code} {reason}).");
        }
    }

    private void Dispatch(JsonElement root)
    {
        if (!root.TryGetProperty("op", out var opElement)) return;
        int op = opElement.GetInt32();

        if (op == 7)   // a reply to something we asked
        {
            var d = root.GetProperty("d");
            string id = d.TryGetProperty("requestId", out var rid) ? rid.GetString() ?? "" : "";

            if (_pending.TryRemove(id, out var waiter))
            {
                bool ok = d.TryGetProperty("requestStatus", out var status)
                       && status.TryGetProperty("result", out var result)
                       && result.GetBoolean();

                // Cloned because the document this element belongs to is disposed as soon
                // as this returns, and the waiter reads it afterwards on another thread.
                if (ok && d.TryGetProperty("responseData", out var response))
                    waiter.TrySetResult(response.Clone());
                else
                    waiter.TrySetResult(default);
            }
            return;
        }

        if (op != 5) return;   // events only from here

        var payload = root.GetProperty("d");
        string type = payload.TryGetProperty("eventType", out var t) ? t.GetString() ?? "" : "";
        var data = payload.TryGetProperty("eventData", out var e) ? e : default;

        var current = _snapshot;

        switch (type)
        {
            case "StreamStateChanged":
                Publish(current with { Streaming = Bool(data, "outputActive") });
                break;

            case "RecordStateChanged":
                Publish(current with
                {
                    Recording = Bool(data, "outputActive"),
                    RecordingPaused = data.ValueKind == JsonValueKind.Object
                                   && data.TryGetProperty("outputPaused", out var p)
                                   && p.ValueKind == JsonValueKind.True,
                });
                break;

            // Lower case c in the event name, capital C in the request name. Not a typo.
            case "VirtualcamStateChanged":
                Publish(current with { VirtualCamera = Bool(data, "outputActive") });
                break;

            case "CurrentProgramSceneChanged":
                Publish(current with { ProgramScene = SceneNameOf(data) });
                break;

            case "SceneListChanged":
                Publish(current with { Scenes = ReadSceneNames(data) });
                break;

            case "InputMuteStateChanged":
            {
                string name = Text(data, "inputName");

                // Every input reports here, including desktop audio. Without this filter a
                // microphone lamp would react to sounds nobody asked it to follow.
                if (name.Length == 0 || name != _micName) break;

                var muted = new Dictionary<string, bool>(current.InputMuted, StringComparer.Ordinal)
                {
                    [name] = Bool(data, "inputMuted"),
                };
                Publish(current with { InputMuted = muted });
                break;
            }

            case "InputActiveStateChanged":
            {
                string name = Text(data, "inputName");
                if (name.Length == 0 || name != _cameraName) break;

                var active = new Dictionary<string, bool>(current.InputActive, StringComparer.Ordinal)
                {
                    [name] = Bool(data, "videoActive"),
                };
                Publish(current with { InputActive = active });
                break;
            }

            case "ExitStarted":
                Publish(ObsSnapshot.Disconnected with { Status = "OBS is closing." });
                break;

            // A scene collection swap renames everything, so the cached lists and the
            // current scene are all stale at once. Ask again rather than patch.
            case "CurrentSceneCollectionChanged":
                _ = Task.Run(() => PrimeAsync(_cts.Token));
                break;
        }
    }

    // ---------------------------------------------------------------- transport

    private async Task<JsonDocument?> ReceiveOneAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();

        while (true)
        {
            WebSocketReceiveResult result;
            try { result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return null; }

            if (result.MessageType == WebSocketMessageType.Close) return null;

            stream.Write(buffer, 0, result.Count);

            // A message can arrive in several frames, and parsing a partial one throws.
            if (result.EndOfMessage) break;
        }

        if (stream.Length == 0) return null;

        try { return JsonDocument.Parse(stream.ToArray()); }
        catch (JsonException) { return null; }
    }

    private async Task SendAsync(object payload, CancellationToken ct)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open) return;

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload);

        // One sender at a time: interleaving two messages on a websocket corrupts both.
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally { _sendGate.Release(); }
    }

    /// <summary>Sends one request and waits for its answer. Null means it failed or timed out.</summary>
    private async Task<JsonElement?> RequestAsync(string type, Dictionary<string, object?>? data,
                                                  CancellationToken ct)
    {
        string id = Interlocked.Increment(ref _requestId).ToString();
        var waiter = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiter;

        try
        {
            var request = new Dictionary<string, object?>
            {
                ["requestType"] = type,
                ["requestId"] = id,
            };
            if (data is not null) request["requestData"] = data;

            await SendAsync(new { op = 6, d = request }, ct).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            var completed = await Task.WhenAny(waiter.Task, Task.Delay(Timeout.Infinite, timeout.Token))
                                      .ConfigureAwait(false);

            if (completed != waiter.Task) return null;

            var value = await waiter.Task.ConfigureAwait(false);
            return value.ValueKind == JsonValueKind.Undefined ? null : value;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OBS] request {type} failed: {ex.Message}");
            return null;
        }
        finally { _pending.TryRemove(id, out _); }
    }

    /// <summary>
    /// A cheap getter on a timer, purely to notice a peer that has gone away.
    ///
    /// A websocket to a machine that slept, or whose cable was pulled, looks exactly like
    /// an idle one. Without this the lights would sit on stale state indefinitely.
    /// </summary>
    private async Task HeartbeatAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));

        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            if (await RequestAsync("GetVersion", null, ct).ConfigureAwait(false) is null)
                throw new IOException("OBS stopped answering.");
        }
    }

    private void FailAllPending()
    {
        foreach (var key in _pending.Keys)
            if (_pending.TryRemove(key, out var waiter)) waiter.TrySetResult(default);
    }

    private void Publish(ObsSnapshot snapshot) => _snapshot = snapshot;

    // ---------------------------------------------------------------- json helpers

    private static bool Bool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
     && element.TryGetProperty(name, out var value)
     && value.ValueKind == JsonValueKind.True;

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
     && element.TryGetProperty(name, out var value)
     && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    /// <summary>
    /// The scene name, whichever of the two field names this OBS uses.
    ///
    /// The request and the event have historically disagreed about which one they send,
    /// and reading only one of them means the scene indicator works in some versions and
    /// not others.
    /// </summary>
    private static string SceneNameOf(JsonElement element)
    {
        string name = Text(element, "sceneName");
        return name.Length > 0 ? name : Text(element, "currentProgramSceneName");
    }

    private static List<string> ReadSceneNames(JsonElement element)
    {
        var names = new List<string>();

        if (element.ValueKind != JsonValueKind.Object
         || !element.TryGetProperty("scenes", out var scenes)
         || scenes.ValueKind != JsonValueKind.Array) return names;

        foreach (var scene in scenes.EnumerateArray())
        {
            string name = Text(scene, "sceneName");
            if (name.Length > 0) names.Add(name);
        }

        // OBS lists scenes with the newest first, which is not the order they appear in.
        names.Reverse();
        return names;
    }

    private static List<ObsInput> ReadInputs(JsonElement element)
    {
        var inputs = new List<ObsInput>();

        if (element.ValueKind != JsonValueKind.Object
         || !element.TryGetProperty("inputs", out var list)
         || list.ValueKind != JsonValueKind.Array) return inputs;

        foreach (var input in list.EnumerateArray())
        {
            string name = Text(input, "inputName");
            if (name.Length == 0) continue;

            string kind = Text(input, "unversionedInputKind");
            if (kind.Length == 0) kind = Text(input, "inputKind");

            inputs.Add(new ObsInput(name, kind));
        }

        return inputs;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        try
        {
            if (_socket is { State: WebSocketState.Open } socket)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None)
                            .ConfigureAwait(false);
        }
        catch { /* going away regardless */ }

        try { if (_supervisor is not null) await _supervisor.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch { }

        _cts.Dispose();
        _sendGate.Dispose();
    }
}
