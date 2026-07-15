using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

public class McpServerManager : IAsyncDisposable
{
    private readonly StdioClientTransport _transport;
    private McpClient? _client;
    private bool _connected;

    public IList<McpClientTool>? Tools { get; private set; }
    public bool IsConnected => _client != null && _connected;

    public McpServerManager(string serverCommand, string[]? serverArgs = null)
    {
        var transportOptions = new StdioClientTransportOptions
        {
            Name = "AssistantMcpServer",
            Command = serverCommand,
            Arguments = (serverArgs ?? Array.Empty<string>()).ToList(),
        };

        _transport = new(transportOptions);
    }

    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            Console.WriteLine($"Подключение к MCP серверу: {_transport.Name}");

            _client = await McpClient.CreateAsync(_transport, null, null, ct);

            Tools = (await _client.ListToolsAsync((ModelContextProtocol.RequestOptions?)null, ct)).ToList();

            if (Tools == null || Tools.Count == 0)
            {
                Console.WriteLine("MCP сервер не предоставил инструментов.");
                _connected = false;
                return false;
            }

            Console.WriteLine($"MCP сервер подключен. Доступно инструментов: {Tools.Count}");
            foreach (var tool in Tools)
            {
                Console.WriteLine($"  - {tool.Name}: {tool.Description}");
            }

            _connected = true;
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Предупреждение: не удалось подключить MCP сервер: {ex.Message}");
            Console.WriteLine("Чат будет работать в режиме без инструментов (RAG-only).");
            _connected = false;
            Tools = null;
            return false;
        }
    }

    public async Task<CallToolResult> CallToolAsync(string name, Dictionary<string, object>? parameters = null)
    {
        if (_client == null || !_connected)
            throw new InvalidOperationException("MCP сервер не подключен");

        return await _client.CallToolAsync(name, parameters as IReadOnlyDictionary<string, object?>, null, null, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client != null)
        {
            await _client.DisposeAsync();
            _connected = false;
        }
    }
}
