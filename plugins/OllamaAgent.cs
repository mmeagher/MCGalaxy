// OllamaAgent.cs — LLM-controlled bots via Ollama
//
// Deploy:
//   1. Place this file in the server's plugins/ directory
//   2. In-game (as owner): /compile plugin OllamaAgent
//   3. In-game (as owner): /plugin load OllamaAgent
//
// Usage:
//   Create a bot with /bot add <name>, then players address it with:
//     !<BotName> hello there
//   (Note: @ is used by MCGalaxy for whispers, so ! is used instead)
//   The bot will respond via level chat and optionally move or place blocks.
//
// Configuration:
//   Edit OllamaUrl and OllamaModel below to match your Ollama setup.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using MCGalaxy;
using MCGalaxy.Bots;
using MCGalaxy.Events.PlayerEvents;

public sealed class OllamaAgentPlugin : Plugin
{
    public override string name    { get { return "OllamaAgent"; } }
    public override string creator { get { return ""; } }
    public override string welcome { get { return "OllamaAgent loaded. Address bots with @BotName <message>"; } }

    // --- Configuration -------------------------------------------------------
    const string OllamaUrl   = "http://localhost:11434/api/chat";
    const string OllamaModel = "gemma3";
    const int    HistoryLimit = 20; // max conversation turns kept per bot
    // -------------------------------------------------------------------------

    static readonly HttpClient http =
        new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

    // Keyed by bot.name; each list starts with a system-prompt message
    readonly Dictionary<string, List<OllamaMsg>> histories =
        new Dictionary<string, List<OllamaMsg>>(StringComparer.OrdinalIgnoreCase);

    public override void Load(bool auto) {
        OnPlayerChatEvent.Register(OnChat, Priority.Normal);
    }

    public override void Unload(bool auto) {
        OnPlayerChatEvent.Unregister(OnChat);
        lock (histories) { histories.Clear(); }
    }


    // =========================================================================
    // Chat hook
    // =========================================================================

    void OnChat(Player p, string message) {
        // Trigger: message begins with !BotName followed by a space
        // (@ is intercepted by MCGalaxy as a whisper before the chat event fires)
        if (message.Length == 0 || message[0] != '!') return;

        int space = message.IndexOf(' ');
        if (space < 0) return;

        string botName = message.Substring(1, space - 1);
        string text    = message.Substring(space + 1).Trim();
        if (text.Length == 0) return;

        // Find the named bot in the player's current level
        PlayerBot bot = null;
        foreach (PlayerBot b in p.level.Bots.Items) {
            if (b.name.CaselessEq(botName)) { bot = b; break; }
        }
        if (bot == null) return;

        // Fire-and-forget on a thread pool thread so we don't block the game loop
        Player    snap_p   = p;
        PlayerBot snap_bot = bot;
        string    snap_txt = text;
        ThreadPool.QueueUserWorkItem(_ => QueryOllama(snap_p, snap_bot, snap_txt));
    }


    // =========================================================================
    // Ollama interaction
    // =========================================================================

    void QueryOllama(Player sender, PlayerBot bot, string userText) {
        try {
            List<OllamaMsg> history = GetOrCreateHistory(bot);

            // Snapshot the history for the HTTP call (avoids holding the lock during I/O)
            // Always refresh the system prompt (index 0) so world/player state is current
            List<OllamaMsg> snapshot;
            lock (history) {
                history[0] = new OllamaMsg("system", BuildSystemPrompt(bot));
                history.Add(new OllamaMsg("user",
                    string.Format("[{0} says]: {1}", sender.name, userText)));
                snapshot = new List<OllamaMsg>(history);
            }

            string requestJson = BuildRequest(OllamaModel, snapshot);
            var    content     = new StringContent(requestJson, Encoding.UTF8, "application/json");
            var    response    = http.PostAsync(OllamaUrl, content).Result;
            string body        = response.Content.ReadAsStringAsync().Result;

            string reply = ParseContent(body);
            if (string.IsNullOrEmpty(reply)) {
                Logger.Log(LogType.Warning, "OllamaAgent: received empty content from Ollama");
                return;
            }

            lock (history) {
                history.Add(new OllamaMsg("assistant", reply));
                // Keep history bounded — always preserve the system prompt at index 0
                while (history.Count > HistoryLimit + 1)
                    history.RemoveAt(1);
            }

            ProcessReply(bot, reply);

        } catch (Exception ex) {
            Logger.Log(LogType.Warning, "OllamaAgent error: " + ex.Message);
        }
    }

    List<OllamaMsg> GetOrCreateHistory(PlayerBot bot) {
        lock (histories) {
            List<OllamaMsg> history;
            if (!histories.TryGetValue(bot.name, out history)) {
                history = new List<OllamaMsg>();
                history.Add(new OllamaMsg("system", BuildSystemPrompt(bot)));
                histories[bot.name] = history;
            }
            return history;
        }
    }


    // =========================================================================
    // Reply processing
    //
    // The LLM may embed action lines anywhere in its response:
    //   /move <bx> <by> <bz>           — walk the bot to those block coords
    //   /place <bx> <by> <bz> <blockId>— place a block in the world
    //
    // Everything else is treated as spoken dialogue sent to level chat.
    // =========================================================================

    void ProcessReply(PlayerBot bot, string reply) {
        var dialogue = new StringBuilder();

        foreach (string raw in reply.Split('\n')) {
            string line = raw.Trim();
            if (line.StartsWith("/move ")) {
                TryMove(bot, line);
            } else if (line.StartsWith("/place ")) {
                TryPlace(bot, line);
            } else if (line.Length > 0) {
                if (dialogue.Length > 0) dialogue.Append(' ');
                dialogue.Append(line);
            }
        }

        string speech = dialogue.ToString().Trim();
        if (speech.Length == 0) return;

        // Broadcast as level chat attributed to the bot
        string msg = string.Format("{0}&f: {1}", bot.ColoredName, speech);
        Chat.Message(ChatScope.Level, msg, bot.level, null);
    }

    // /move <bx> <by> <bz>
    void TryMove(PlayerBot bot, string line) {
        string[] parts = line.Split(' ');
        if (parts.Length < 4) return;
        int bx, by, bz;
        if (!int.TryParse(parts[1], out bx) ||
            !int.TryParse(parts[2], out by) ||
            !int.TryParse(parts[3], out bz)) return;

        bot.TargetPos = Position.FromFeetBlockCoords(bx, by, bz);
        bot.movement  = true;
    }

    // /place <bx> <by> <bz> <blockId>
    // Common IDs: 0=air 1=stone 2=grass 3=dirt 4=cobble 5=wood 7=bedrock 17=leaves
    void TryPlace(PlayerBot bot, string line) {
        string[] parts = line.Split(' ');
        if (parts.Length < 5) return;
        ushort bx, by, bz, blockId;
        if (!ushort.TryParse(parts[1], out bx) ||
            !ushort.TryParse(parts[2], out by) ||
            !ushort.TryParse(parts[3], out bz) ||
            !ushort.TryParse(parts[4], out blockId)) return;

        Level lvl = bot.level;
        lvl.SetTile(bx, by, bz, (byte)blockId);
        lvl.BroadcastRevert(bx, by, bz); // sends the updated block to all players in the level
    }


    // =========================================================================
    // System prompt
    // =========================================================================

    const int ScanRadius   = 4;  // blocks in each direction for world scan
    const int PlayerRadius = 20; // blocks for nearby-player detection

    string BuildSystemPrompt(PlayerBot bot) {
        Position pos = bot.Pos;
        int bx = pos.BlockX, by = pos.BlockY, bz = pos.BlockZ;
        Level lvl = bot.level;

        string nearbyBlocks  = ScanNearbyBlocks(bot, bx, by, bz);
        string nearbyPlayers = ScanNearbyPlayers(bot, bx, by, bz);
        string otherBots     = ListOtherBots(bot);

        var sb = new StringBuilder();

        // Identity & game context
        sb.AppendFormat(
            "You are {0}, an AI agent inside ClassiCube — a creative block-building game " +
            "where the world is made entirely of 1x1x1 metre cubes on a fixed integer grid. " +
            "Players address you by typing !{1} followed by their message.\n\n",
            bot.DisplayName, bot.name);

        // Coordinate system & spatial translation
        sb.AppendFormat(
            "WORLD & COORDINATES\n" +
            "World size: x=0..{0}, y=0..{1}, z=0..{2}. " +
            "y=0 is the bedrock floor; y increases upward.\n" +
            "Your position: ({3},{4},{5}).\n" +
            "Spatial words map to axes: up=+y, down=-y, north=-z, south=+z, east=+x, west=-x.\n" +
            "Size words: tall/high=y span, wide=x span, long/deep=z span.\n" +
            "To build a wall 5 blocks wide facing east at your feet, place blocks at " +
            "({3},{4},{5}), ({3},{4},{6}), ({3},{4},{7}), ({3},{4},{8}), ({3},{4},{9}).\n\n",
            lvl.Width - 1, lvl.Height - 1, lvl.Length - 1,
            bx, by, bz,
            bz + 1, bz + 2, bz + 3, bz + 4);

        // Actions
        sb.AppendFormat(
            "ACTIONS (emit as plain lines in your reply, one per line)\n" +
            "  /move <x> <y> <z>               walk to those block coords\n" +
            "  /place <x> <y> <z> <blockId>    place a block (blockId 0 = remove)\n\n" +
            "BUILDING RULES — read carefully:\n" +
            "* Each block requires its own /place line with exact absolute coordinates.\n" +
            "* You can place blocks anywhere in the world without being adjacent — you do NOT need to be standing next to a block to place it.\n" +
            "* To build a structure, compute every block coordinate yourself and emit one /place per block.\n" +
            "* Example — a 3-wide, 2-tall stone wall running east from your position ({0},{1},{2}):\n" +
            "    /place {0} {1} {2} 1\n" +
            "    /place {3} {1} {2} 1\n" +
            "    /place {4} {1} {2} 1\n" +
            "    /place {0} {5} {2} 1\n" +
            "    /place {3} {5} {2} 1\n" +
            "    /place {4} {5} {2} 1\n" +
            "* Only emit /move when you want to reposition yourself. It does NOT place blocks.\n" +
            "* Never describe what you are building with action lines — just emit the lines.\n\n",
            bx, by, bz,
            bx + 1, bx + 2, by + 1);

        // Full block reference
        sb.Append(
            "BLOCK IDs\n" +
            "Natural:  0=air, 1=stone, 2=grass, 3=dirt, 4=cobblestone, 7=bedrock,\n" +
            "          12=sand, 13=gravel, 14=gold_ore, 15=iron_ore, 16=coal_ore\n" +
            "Wood/plant: 5=planks, 17=log, 18=leaves, 6=sapling, 19=sponge\n" +
            "Fluid:    8=water, 9=still_water, 10=lava, 11=still_lava\n" +
            "Processed: 20=glass, 41=gold_block, 42=iron_block, 43=double_slab,\n" +
            "           44=slab, 45=brick, 46=tnt, 47=bookshelf,\n" +
            "           48=mossy_cobblestone, 49=obsidian\n" +
            "Cloth:    21=red, 22=orange, 23=yellow, 24=chartreuse, 25=green,\n" +
            "          26=spring_green, 27=cyan, 28=capri, 29=ultramarine,\n" +
            "          30=violet, 31=purple, 32=magenta, 33=rose,\n" +
            "          34=dark_gray, 35=light_gray, 36=white\n" +
            "Plants:   37=dandelion, 38=rose_flower, 39=brown_mushroom, 40=red_mushroom\n\n");

        // Multi-agent coordination
        if (otherBots.Length > 0)
            sb.AppendFormat(
                "OTHER AGENTS IN THIS WORLD\n{0}\n" +
                "When a task is large, coordinate: divide the work spatially (e.g. you take the " +
                "west half, another bot takes the east half) or by role (builder vs. decorator). " +
                "Mention your plan so players can relay it to the other bots.\n\n",
                otherBots);

        // Live world state
        if (nearbyBlocks.Length > 0)
            sb.AppendFormat("NEARBY BLOCKS (within {0} blocks): {1}\n\n", ScanRadius, nearbyBlocks);
        else
            sb.AppendFormat("NEARBY BLOCKS: open area within {0} blocks.\n\n", ScanRadius);

        if (nearbyPlayers.Length > 0)
            sb.AppendFormat("NEARBY PLAYERS: {0}\n\n", nearbyPlayers);

        sb.Append("Keep replies concise and in-character.");
        return sb.ToString();
    }

    string ScanNearbyBlocks(PlayerBot bot, int cx, int cy, int cz) {
        var sb = new StringBuilder();
        Level lvl = bot.level;
        for (int dx = -ScanRadius; dx <= ScanRadius; dx++)
        for (int dy = -ScanRadius; dy <= ScanRadius; dy++)
        for (int dz = -ScanRadius; dz <= ScanRadius; dz++) {
            int x = cx + dx, y = cy + dy, z = cz + dz;
            if (x < 0 || y < 0 || z < 0) continue;
            ushort block = lvl.GetBlock((ushort)x, (ushort)y, (ushort)z);
            if (block == Block.Air) continue;
            if (sb.Length > 0) sb.Append(", ");
            sb.AppendFormat("({0},{1},{2})={3}", x, y, z, block);
        }
        return sb.ToString();
    }

    string ScanNearbyPlayers(PlayerBot bot, int cx, int cy, int cz) {
        var sb = new StringBuilder();
        foreach (Player p in PlayerInfo.Online.Items) {
            if (p.level != bot.level) continue;
            int dx = p.Pos.BlockX - cx, dy = p.Pos.BlockY - cy, dz = p.Pos.BlockZ - cz;
            if ((int)Math.Sqrt(dx*dx + dy*dy + dz*dz) > PlayerRadius) continue;
            if (sb.Length > 0) sb.Append(", ");
            sb.AppendFormat("{0} at ({1},{2},{3})", p.DisplayName, p.Pos.BlockX, p.Pos.BlockY, p.Pos.BlockZ);
        }
        return sb.ToString();
    }

    string ListOtherBots(PlayerBot self) {
        var sb = new StringBuilder();
        foreach (PlayerBot b in self.level.Bots.Items) {
            if (b.name.CaselessEq(self.name)) continue;
            if (sb.Length > 0) sb.Append(", ");
            sb.AppendFormat("{0} at ({1},{2},{3})", b.DisplayName, b.Pos.BlockX, b.Pos.BlockY, b.Pos.BlockZ);
        }
        return sb.ToString();
    }


    // =========================================================================
    // Minimal JSON helpers (avoids taking on external dependencies)
    // =========================================================================

    string BuildRequest(string model, List<OllamaMsg> msgs) {
        var sb = new StringBuilder();
        sb.Append("{\"model\":\"").Append(model)
          .Append("\",\"stream\":false,\"messages\":[");

        for (int i = 0; i < msgs.Count; i++) {
            if (i > 0) sb.Append(',');
            sb.Append("{\"role\":\"").Append(msgs[i].Role)
              .Append("\",\"content\":\"").Append(JsonEscape(msgs[i].Content))
              .Append("\"}");
        }

        sb.Append("]}");
        return sb.ToString();
    }

    // Ollama non-streaming response shape:
    //   { "message": { "role": "assistant", "content": "..." }, ... }
    string ParseContent(string json) {
        int idx = json.IndexOf("\"content\":");
        if (idx < 0) return null;
        int start = json.IndexOf('"', idx + 10) + 1;
        if (start <= 0) return null;
        int end = FindStringEnd(json, start);
        if (end < 0) return null;
        return JsonUnescape(json.Substring(start, end - start));
    }

    int FindStringEnd(string s, int from) {
        for (int i = from; i < s.Length; i++) {
            if (s[i] == '\\') { i++; continue; }
            if (s[i] == '"')  return i;
        }
        return -1;
    }

    string JsonEscape(string s) {
        return s.Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");
    }

    string JsonUnescape(string s) {
        return s.Replace("\\n",  "\n")
                .Replace("\\r",  "\r")
                .Replace("\\t",  "\t")
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\");
    }

    sealed class OllamaMsg {
        public readonly string Role, Content;
        public OllamaMsg(string role, string content) { Role = role; Content = content; }
    }
}
