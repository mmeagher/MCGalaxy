# OllamaAgent

A MCGalaxy plugin that gives in-game bots conversational AI powered by a local [Ollama](https://ollama.com) LLM. Bots can chat with players, walk around the world, and place or remove blocks.

## Requirements

- MCGalaxy server (with the Compiler plugin enabled — included by default)
- [Ollama](https://ollama.com) running locally with at least one model pulled (e.g. `ollama pull llama3`)

## Deployment

1. Place `OllamaAgent.cs` in the server's `plugins/` directory
2. In-game as owner, compile and load it:

```
/compile plugin OllamaAgent
/plugin load OllamaAgent
```

No server restart is required.

## Usage

Create a bot with the standard MCGalaxy bot command, then players address it by prefixing their message with `!<BotName>`:

```
/bot add Sage
!Sage what should I build here?
```

> **Note:** `@name` is reserved by MCGalaxy for player whispers and is intercepted before the chat event fires, so `!` is used instead.

The bot responds in level chat as `Sage: ...`. Each bot maintains its own conversation history, so multiple bots can run independently in the same or different levels.

## Configuration

Edit the constants near the top of `OllamaAgent.cs` before compiling:

| Constant | Default | Description |
|---|---|---|
| `OllamaUrl` | `http://localhost:11434/api/chat` | Ollama API endpoint |
| `OllamaModel` | `llama3` | Model name (must be pulled in Ollama) |
| `HistoryLimit` | `20` | Max conversation turns kept per bot |

## Bot actions

The LLM can embed action lines anywhere in its reply. All other text is spoken as dialogue.

| Action line | Effect |
|---|---|
| `/move <x> <y> <z>` | Walk the bot to the given block coordinates |
| `/place <x> <y> <z> <blockId>` | Place or remove a block in the world |

Common block IDs: `0`=air, `1`=stone, `2`=grass, `3`=dirt, `4`=cobblestone, `5`=wood, `7`=bedrock, `12`=sand, `13`=gravel, `17`=leaves.

The system prompt instructs the LLM to only emit action lines when they make sense, but you can make this stricter by editing `BuildSystemPrompt` in the source.

## Architecture

| Component | Detail |
|---|---|
| Event hook | `OnPlayerChatEvent` — fires on every player chat message |
| Trigger | Message starts with `!<BotName> ` where the bot exists in the player's level |
| Ollama call | `POST /api/chat` with `"stream": false`, run on a `ThreadPool` thread so the game loop is never blocked |
| Memory | Per-bot `List<OllamaMsg>` starting with a system prompt; trimmed to `HistoryLimit` turns |
| Bot movement | Sets `bot.TargetPos` and `bot.movement = true` — uses the existing MCGalaxy bot movement scheduler |
| Block placement | `level.SetTile` + `level.BroadcastRevert` to update all players in the level |

## Extending the plugin

**Give the bot world awareness** — scan blocks around the bot's position and include them in the system prompt:

```csharp
// Inside BuildSystemPrompt, after getting pos:
var sb = new StringBuilder();
for (int dx = -3; dx <= 3; dx++)
for (int dy = -3; dy <= 3; dy++)
for (int dz = -3; dz <= 3; dz++) {
    ushort bx = (ushort)(pos.BlockX + dx),
           by = (ushort)(pos.BlockY + dy),
           bz = (ushort)(pos.BlockZ + dz);
    BlockID block = bot.level.GetBlock(bx, by, bz);
    if (block != Block.Air)
        sb.AppendFormat("({0},{1},{2})={3} ", bx, by, bz, block);
}
// Append sb.ToString() to the system prompt
```

**Give the bot player awareness** — list nearby players in the system prompt:

```csharp
foreach (Player p in PlayerInfo.Online.Items) {
    if (p.level != bot.level) continue;
    // include p.name and p.Pos.BlockX/Y/Z in the prompt
}
```

**Reset a bot's memory** — unload and reload the plugin, or add a `/botforget <name>` command that calls `histories.Remove(name)`.
