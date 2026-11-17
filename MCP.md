# AHK Manager MCP server

The portable ZIP includes a local Model Context Protocol server. Configure your client to start `App/backend/agent/ahk-manager.exe` with the argument `mcp` and use the `stdio` transport.

For Codex, add this entry to `config.toml` and set the path to your extracted portable folder.

```toml
[mcp_servers.ahk-manager]
command = 'C:\Apps\AHK Manager Portable\App\backend\agent\ahk-manager.exe'
args = ['mcp']
```

For clients that use an `mcpServers` JSON object, add this entry.

```json
{
  "mcpServers": {
    "ahk-manager": {
      "command": "C:\\Apps\\AHK Manager Portable\\App\\backend\\agent\\ahk-manager.exe",
      "args": ["mcp"]
    }
  }
}
```

After moving the portable folder, update the executable path in the client configuration.

## Discover and call tools

The client can initialize and list tools from a standard session, even while Manager is closed. Live calls require an already elevated process in the same Windows account and session as Manager. If a call returns `ElevationRequired`, check the token under which your client starts the server. Use `app_start` to start Manager in the background before making other live calls.

MCP tool names replace the dot in the corresponding CLI command with an underscore. For example, `scripts.list` becomes `scripts_list`, and `settings.save` becomes `settings_save`. Tool descriptions and input schemas identify the fields, valid values and review requirements. Opening an editor or a containing folder through `scripts_operate` still opens the requested desktop application.

To start a registered script through MCP:

1. Call `scripts_list` and obtain the script ID and current catalog revision from `result`.
2. Call `commands_preview` with `command` set to `scripts.operate` and `arguments` containing `scriptId`, `revision` and `operation` set to `Start`.
3. Review the returned target. Call `scripts_operate` with those same arguments and the returned `confirmationToken`.
4. Check `outcome` and `code` before taking a dependent action.

Confirmation tokens belong to the reviewed arguments and host generation. If the selection, file or host changes, obtain a fresh preview. The same process applies to reviewed removal, settings, rollback and data movement commands.

## Results and recovery

Tool results contain the [CLI response envelope](CLI.md#input-and-output) as JSON text and, from protocol version `2025-06-18`, structured content. Read `outcome`, `code` and `result` together. `Accepted` can mean that an operation is still running. Keep the operation ID and call `operations_get` to read its durable receipt.

Manager can commit an operation before the MCP client receives its reply. If the connection closes during an update, reconnect after Manager restarts and query the receipt. If the operation ID was lost, inspect the current version and update history before retrying. Disconnect other MCP sessions before applying an update so the updater can replace the portable application files.

The client launches the server locally. The server communicates over standard input and output and uses the existing authenticated connection to Manager for live operations.
