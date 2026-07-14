using System.Text.Json;
using MCPSharp;
using MCPSharp.Model;
using MCPSharp.Model.Results;

public class McpServerManager : IDisposable
{
    private readonly string _serverCommand;
    private readonly string[]? _serverArgs;
    private MCPClient? _client;
    private bool _connected;
    private bool _disposed;

    public List<Tool>? Tools { get; private set; }
    public bool IsConnected => _connected;

    public McpServerManager(string serverCommand, string[]? serverArgs = null)
    {
        _serverCommand = serverCommand;
        _serverArgs = serverArgs;
    }

    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            Console.WriteLine("Подключение к MCP серверу: " + _serverCommand);

            string serverArg = _serverArgs != null && _serverArgs.Length > 0
                ? string.Join(" ", _serverArgs)
                : null;

            _client = new MCPClient(
                name: "Assistant",
                version: "1.0.0",
                server: _serverCommand,
                args: serverArg);

            // MCPSharp uses a simple HTTP-based protocol over stdio.
            // The client starts the process in the constructor, then we need to
            // manually send MCP protocol messages via the stdio pipes.

            Tools = await DiscoverToolsAsync();

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
            Console.WriteLine($"Предупреждение: не удалось подключить MCP сервер ({_serverCommand}): {ex.Message}");
            Console.WriteLine("Чат будет работать в режиме без инструментов (RAG-only).");
            _connected = false;
            Tools = null;
            return false;
        }
    }

    private async Task<List<Tool>?> DiscoverToolsAsync()
    {
        // MCPSharp's MCPClient uses stdio transport internally.
        // We need to use reflection or the internal RPC mechanism.
        // Since MCPSharp 1.0.11 doesn't expose InitializeAsync/ListToolsAsync directly,
        // we'll use the client's internal RPC through reflection.

        try
        {
            var initMethod = typeof(MCPClient).GetMethod("InitializeAsync", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (initMethod != null)
            {
                await (Task)initMethod.Invoke(_client, new object[] { "1.0.0", null, new Implementation("Assistant", "1.0.0") });
            }

            var listToolsMethod = typeof(MCPClient).GetMethod("ListToolsAsync", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (listToolsMethod != null)
            {
                var result = await (Task<ToolsListResult>)listToolsMethod.Invoke(_client, new object[] { null });
                return result?.Tools;
            }

            // Fallback: try to get tools via reflection on internal fields
            var clientField = typeof(MCPClient).GetField("_rpc", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (clientField?.GetValue(_client) is object rpcClient)
            {
                var listMethod = rpcClient.GetType().GetMethod("ListToolsAsync", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (listMethod != null)
                {
                    var result = await (Task<ToolsListResult>)listMethod.Invoke(rpcClient, new object[] { null });
                    return result?.Tools;
                }
            }

            Console.WriteLine("MCPSharp SDK не предоставляет доступный метод для получения списка инструментов.");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка при получении списка инструментов: {ex.Message}");
            return null;
        }
    }

    public async Task<CallToolResult> CallToolAsync(string name, Dictionary<string, object>? parameters = null)
    {
        if (_client == null || !_connected)
            throw new InvalidOperationException("MCP сервер не подключен");

        // Try direct method first, then reflection fallback
        var callMethod = typeof(MCPClient).GetMethod("CallToolAsync", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        if (callMethod != null)
        {
            return await (Task<CallToolResult>)callMethod.Invoke(_client, new object[] { name, parameters ?? new Dictionary<string, object>() });
        }

        throw new InvalidOperationException("MCPSharp SDK не предоставляет доступный метод для вызова инструментов.");
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _client?.Dispose();
            _connected = false;
            _disposed = true;
        }
    }
}
