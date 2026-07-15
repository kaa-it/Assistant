using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

public class McpServerManager : IAsyncDisposable
{
    // serverName -> (transport, client, tools, connected)
    private readonly Dictionary<string, McpServerInstance> _servers = new();

    // Combined list of all tools from all connected servers (unprefixed names).
    public IList<McpClientTool>? Tools => AllTools;
    private List<McpClientTool>? _allTools;

    public bool IsConnected => _servers.Values.Any(s => s.Connected);
    public int ServerCount => _servers.Count;

    private List<McpClientTool> AllTools
    {
        get
        {
            if (_allTools == null)
            {
                var list = new List<McpClientTool>();

                foreach (var instance in _servers.Values)
                {
                    if (instance.Tools != null)
                    {
                        list.AddRange(instance.Tools);
                    }
                }

                _allTools = list;
            }

            return _allTools;
        }
    }

    public McpServerManager() { }

    public void AddServer(string serverName, string serverCommand, string[]? serverArgs = null)
    {
        var transportOptions = new StdioClientTransportOptions
        {
            Name = serverName,
            Command = serverCommand,
            Arguments = (serverArgs ?? Array.Empty<string>()).ToList(),
        };

        var transport = new StdioClientTransport(transportOptions);
        _servers[serverName] = new McpServerInstance(transport, serverName);
    }

    public async Task<bool> ConnectAsync(string? serverName = null, CancellationToken ct = default)
    {
        if (serverName != null)
        {
            if (!_servers.TryGetValue(serverName, out var instance))
                throw new ArgumentException($"Server '{serverName}' not found.");

            return await ConnectInstanceAsync(instance, ct);
        }

        bool anyConnected = false;
        foreach (var instance in _servers.Values)
        {
            try
            {
                await ConnectInstanceAsync(instance, ct);
                anyConnected = true;
            }
            catch { /* individual server failure is non-fatal */ }
        }

        _allTools = null; // invalidate cache
        return anyConnected;
    }

    private async Task<bool> ConnectInstanceAsync(McpServerInstance instance, CancellationToken ct)
    {
        try
        {
            Console.WriteLine($"Подключение к MCP серверу: {_servers[instance.ServerName]._transport.Name}");

            var client = await McpClient.CreateAsync(instance._transport, null, null, ct);
            var tools = (await client.ListToolsAsync((ModelContextProtocol.RequestOptions?)null, ct)).ToList();

            if (tools == null || tools.Count == 0)
            {
                Console.WriteLine($"MCP сервер {_servers[instance.ServerName]._transport.Name} не предоставил инструментов.");
                instance.Connected = false;
                return false;
            }

            Console.WriteLine($"MCP сервер подключен. Доступно инструментов: {tools.Count}");
            foreach (var tool in tools)
            {
                Console.WriteLine($"  - {tool.Name}: {tool.Description}");
            }

            instance.Client = client;
            instance.Tools = tools;
            instance.Connected = true;

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Предупреждение: не удалось подключить MCP сервер {_servers[instance.ServerName]._transport.Name}: {ex.Message}");
            Console.WriteLine("Чат будет работать в режиме без инструментов (RAG-only).");

            _allTools = null;
            return false;
        }
    }

    public async Task<CallToolResult> CallToolAsync(string name, Dictionary<string, object>? parameters = null)
    {
        // Find which server owns this tool and dispatch to it.
        foreach (var kvp in _servers)
        {
            var serverName = kvp.Key;
            var instance = kvp.Value;

            if (!instance.Connected || instance.Tools == null)
                continue;

            // Check if any tool from this server matches the requested name.
            foreach (var tool in instance.Tools)
            {
                if (tool.Name == name)
                {
                    return await instance.Client!.CallToolAsync(name, parameters as IReadOnlyDictionary<string, object?>, null, null, CancellationToken.None);
                }
            }
        }

        throw new InvalidOperationException($"Tool '{name}' not found in any connected MCP server");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var instance in _servers.Values)
        {
            if (instance.Client != null)
            {
                await instance.Client.DisposeAsync();
            }
        }

        _servers.Clear();
        _allTools = null;
    }

    private class McpServerInstance
    {
        public StdioClientTransport _transport;
        public string ServerName { get; }

        public McpClient? Client { get; set; }
        public IList<McpClientTool>? Tools { get; set; }
        public bool Connected { get; set; }

        public McpServerInstance(StdioClientTransport transport, string serverName)
        {
            _transport = transport;
            ServerName = serverName;
        }
    }
}
