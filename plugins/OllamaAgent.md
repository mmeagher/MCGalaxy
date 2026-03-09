# OllamaAgent

A MCGalaxy plugin that gives in-game bots conversational AI powered by a local [Ollama](https://ollama.com) LLM. Bots can chat with players, walk around the world, and place or remove blocks in a single response.

For multi-step building tasks (structures that require planning across multiple LLM calls) see **OllamaAgentLoop**.

## Requirements

- MCGalaxy server (with the Compiler plugin enabled — included by default)
- [Ollama](https://ollama.com) running locally with at least one model pulled (e.g. `ollama pull gemma3`)

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
| `OllamaModel` | `gemma3` | Model name (must be pulled in Ollama) |
| `HistoryLimit` | `20` | Max conversation turns kept per bot |

## Bot actions

The LLM can embed action lines anywhere in its reply. All other text is spoken as dialogue.

| Action line | Effect |
|---|---|
| `/move <x> <y> <z>` | Walk the bot to the given block coordinates |
| `/place <x> <y> <z> <blockId>` | Place or remove a block in the world (`0` = air/remove) |

Full block ID reference is included in the system prompt — see `OllamaAgent-system-prompt.md`.

## World awareness

The system prompt automatically includes:

- **World size** and the bot's current position
- **Nearby blocks** (within 4 blocks in each direction) — non-air blocks listed as `(x,y,z)=blockId`
- **Nearby players** (within 20 blocks) — name and position
- **Other bots** in the same level — name and position, with multi-agent coordination guidance

These are refreshed on every request so the bot always has current state.

## Architecture

| Component | Detail |
|---|---|
| Event hook | `OnPlayerChatEvent` — fires on every player chat message |
| Trigger | Message starts with `!<BotName> ` where the bot exists in the player's level |
| Ollama call | `POST /api/chat` with `"stream": false`, run on a `ThreadPool` thread so the game loop is never blocked |
| Memory | Per-bot `List<OllamaMsg>` starting with a system prompt (refreshed each call); trimmed to `HistoryLimit` turns |
| Bot movement | Sets `bot.TargetPos` and `bot.movement = true` — uses the existing MCGalaxy bot movement scheduler |
| Block placement | `level.SetTile` + `level.BroadcastRevert` to update all players in the level |

## Choosing between OllamaAgent and OllamaAgentLoop

| | OllamaAgent | OllamaAgentLoop |
|---|---|---|
| LLM calls per task | 1 | Up to 15 (configurable) |
| Suitable for | Chat, small builds | Large structures, multi-step plans |
| Signals required | None | `/continue` / `/done` |
| Concurrency guard | None | Yes — one task per bot at a time |
| Timeout | 30 s | 180 s |

> Only load one plugin at a time — both respond to the same `!BotName` trigger.
