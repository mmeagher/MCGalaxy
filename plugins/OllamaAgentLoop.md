# OllamaAgentLoop

A MCGalaxy plugin that gives in-game bots a **ReAct loop** (Reason → Act → Observe) for multi-step building tasks powered by a local [Ollama](https://ollama.com) LLM.

Each player message starts a task loop. The bot emits `/place` commands, receives an observation of updated world state, and iterates until the task is complete or the step limit is reached. This lets the bot plan, place blocks in batches, verify progress, and self-correct.

For simple chat and single-response builds see **OllamaAgent**.

## Requirements

- MCGalaxy server (with the Compiler plugin enabled — included by default)
- [Ollama](https://ollama.com) running locally with at least one model pulled (e.g. `ollama pull gemma3`)

## Deployment

1. Place `OllamaAgentLoop.cs` in the server's `plugins/` directory
2. In-game as owner, compile and load it:

```
/compile plugin OllamaAgentLoop
/plugin load OllamaAgentLoop
```

No server restart is required.

## Usage

Create a bot and address it with `!<BotName>`:

```
/bot add Kevin
!Kevin build a stone wall 5 blocks wide and 3 blocks tall here
```

The bot will:
1. Plan and emit `/place` commands for the first batch of blocks
2. End its response with `/continue`
3. Receive an `[OBSERVATION]` with updated world state (blocks placed, new position, nearby blocks)
4. Keep working until it emits `/done` or reaches the step limit

If the bot is already working on a task it will announce that it's busy and ignore new requests until the task finishes.

## Configuration

Edit the constants near the top of `OllamaAgentLoop.cs` before compiling:

| Constant | Default | Description |
|---|---|---|
| `OllamaUrl` | `http://localhost:11434/api/chat` | Ollama API endpoint |
| `OllamaModel` | `gemma3` | Model name (must be pulled in Ollama) |
| `HistoryLimit` | `30` | Max conversation turns kept per bot |
| `MaxSteps` | `15` | Max ReAct iterations per task |

## How the loop works

```
Player: !Kevin build a cabin

Step 1  → LLM thinks, emits /place commands for floor layer, ends with /continue
          → Server executes placements, sends [OBSERVATION]
Step 2  → LLM emits /place commands for walls, ends with /continue
          → Server executes placements, sends [OBSERVATION]
...
Step N  → LLM finishes, ends with /done
          → Loop stops
```

Each `[OBSERVATION]` includes:
- How many blocks were placed that step
- The bot's current position
- Updated nearby block state
- Steps remaining

## Bot actions

| Action line | Effect |
|---|---|
| `/move <x> <y> <z>` | Walk the bot to the given block coordinates |
| `/place <x> <y> <z> <blockId>` | Place or remove a block in the world (`0` = air/remove) |
| `/continue` | Signal that more work remains — triggers the next observation |
| `/done` | Signal that the task is complete — loop stops |

Full block ID reference is included in the system prompt — see `OllamaAgentLoop-system-prompt.md`.

## World awareness

The system prompt automatically includes:

- **World size** and the bot's current position
- **Nearby blocks** (within 4 blocks in each direction) — non-air blocks listed as `(x,y,z)=blockId`
- **Nearby players** (within 20 blocks) — name and position
- **Other bots** in the same level — name and position, with multi-agent coordination guidance

These are refreshed at the start of each task and updated in every observation.

## Architecture

| Component | Detail |
|---|---|
| Event hook | `OnPlayerChatEvent` — fires on every player chat message |
| Trigger | Message starts with `!<BotName> ` where the bot exists in the player's level |
| Concurrency guard | `activeTasks` dict — ignores new requests while a loop is running for that bot |
| Ollama call | `POST /api/chat` with `"stream": false`, 180 s timeout; runs on a `ThreadPool` thread |
| Memory | Per-bot history seeded with system prompt + assistant pre-fill; trimmed to `HistoryLimit` turns |
| Observation | After each `/continue`, world state is fed back as a `user` message so the LLM can self-correct |
| Bot movement | Sets `bot.TargetPos` and `bot.movement = true` |
| Block placement | `level.SetTile` + `level.BroadcastRevert` |

## Choosing between OllamaAgent and OllamaAgentLoop

| | OllamaAgent | OllamaAgentLoop |
|---|---|---|
| LLM calls per task | 1 | Up to 15 (configurable) |
| Suitable for | Chat, small builds | Large structures, multi-step plans |
| Signals required | None | `/continue` / `/done` |
| Concurrency guard | None | Yes — one task per bot at a time |
| Timeout | 30 s | 180 s |

> Only load one plugin at a time — both respond to the same `!BotName` trigger.
