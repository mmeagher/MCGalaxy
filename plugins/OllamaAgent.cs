// OllamaAgent.cs — LLM-controlled bots via Ollama
//
// Deploy:
//   1. Place this file in the server's plugins/ directory
//   2. In-game (as owner): /compile plugin OllamaAgent
//   3. In-game (as owner): /plugin load OllamaAgent
//
// Usage:
//   Create a bot with /bot add <name>, then players address it with:
//     @<BotName> hello there
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
    const string OllamaModel = "llama3";
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
        // Trigger: message begins with @BotName followed by a space
        if (message.Length == 0 || message[0] != '@') return;
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
        List<OllamaMsg> history = GetOrCreateHistory(bot);

        // Snapshot the history for the HTTP call (avoids holding the lock during I/O)
        List<OllamaMsg> snapshot;
        lock (history) {
            history.Add(new OllamaMsg("user",
                string.Format("[{0} says]: {1}", sender.name, userText)));
            snapshot = new List<OllamaMsg>(history);
        }

        try {
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

    string BuildSystemPrompt(PlayerBot bot) {
        Position pos = bot.Pos;
        return string.Format(
            "You are a Minecraft Classic bot named {0} living in a voxel world. " +
            "Players talk to you by typing @{1} followed by their message. " +
            "You can respond in natural language and optionally take actions. " +
            "\n\nAvailable actions (include as separate lines in your reply):" +
            "\n  /move <x> <y> <z>              — walk to block coordinates" +
            "\n  /place <x> <y> <z> <blockId>   — place or remove a block" +
            "\n\nCommon block IDs: 0=air, 1=stone, 2=grass, 3=dirt, 4=cobblestone, " +
            "5=wood, 7=bedrock, 12=sand, 13=gravel, 17=leaves." +
            "\n\nOnly include action lines when they make sense; otherwise just reply with dialogue. " +
            "Keep replies concise and in-character. " +
            "Your current position is block ({2}, {3}, {4}).",
            bot.DisplayName, bot.name,
            pos.BlockX, pos.BlockY, pos.BlockZ);
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
