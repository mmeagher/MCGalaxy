//dotnetref System.Net.Http.dll
//dotnetref System.Private.Uri.dll
//dotnetref System.Threading.Thread.dll
//dotnetref System.ComponentModel.TypeConverter.dll
// OllamaAgentLoop.cs — ReAct-loop LLM bots via Ollama
//
// Deploy:
//   1. Copy this file to the server's plugins/ directory
//   2. Compile manually (see README) or in-game: /compile plugin OllamaAgentLoop
//   3. In-game (as owner): /plugin load OllamaAgentLoop
//
// Usage:
//   Create a bot with /bot add <name>, then address it with:
//     !<BotName> build a small stone house here
//
// How it works (ReAct loop):
//   Each player message starts a task loop (up to MaxSteps iterations).
//   Each iteration the bot receives a fresh world-state observation, executes
//   actions, and either signals /continue (more work to do) or /done (finished).
//   This lets the bot plan, place blocks in batches, verify progress, and
//   self-correct across multiple LLM calls until the task is complete.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using MCGalaxy;
using MCGalaxy.Bots;
using MCGalaxy.Events.PlayerEvents;

public sealed class OllamaAgentLoopPlugin : Plugin
{
    public override string name    { get { return "OllamaAgentLoop"; } }
    public override string creator { get { return ""; } }
    public override string welcome { get { return "OllamaAgentLoop loaded. Address bots with !BotName <message>"; } }

    // --- Configuration -------------------------------------------------------
    const string OllamaUrl   = "http://localhost:11434/api/chat";
    const string OllamaModel = "gemma3";
    const int    HistoryLimit = 30;  // max conversation turns kept per bot
    const int    MaxSteps     = 15;  // max ReAct iterations per task
    // -------------------------------------------------------------------------

    static readonly HttpClient http =
        new HttpClient { Timeout = TimeSpan.FromSeconds(180) };

    // Keyed by bot.name
    readonly Dictionary<string, List<OllamaMsg>> histories =
        new Dictionary<string, List<OllamaMsg>>(StringComparer.OrdinalIgnoreCase);

    // True while a ReAct loop is running for that bot
    readonly Dictionary<string, bool> activeTasks =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    const int ScanRadius   = 4;
    const int PlayerRadius = 20;

    public override void Load(bool auto) {
        OnPlayerChatEvent.Register(OnChat, Priority.Normal);
    }

    public override void Unload(bool auto) {
        OnPlayerChatEvent.Unregister(OnChat);
        lock (histories)    { histories.Clear(); }
        lock (activeTasks)  { activeTasks.Clear(); }
    }


    // =========================================================================
    // Chat hook
    // =========================================================================

    void OnChat(Player p, string message) {
        if (message.Length == 0 || message[0] != '!') return;

        int space = message.IndexOf(' ');
        if (space < 0) return;

        string botName = message.Substring(1, space - 1);
        string text    = message.Substring(space + 1).Trim();
        if (text.Length == 0) return;

        PlayerBot bot = null;
        foreach (PlayerBot b in p.level.Bots.Items) {
            if (b.name.CaselessEq(botName)) { bot = b; break; }
        }
        if (bot == null) return;

        // Reject concurrent tasks — bot announces it is busy
        lock (activeTasks) {
            bool busy;
            if (activeTasks.TryGetValue(bot.name, out busy) && busy) {
                string msg = string.Format("{0}&f: I'm still working — please wait until I'm done.", bot.ColoredName);
                Chat.Message(ChatScope.Level, msg, bot.level, null);
                return;
            }
            activeTasks[bot.name] = true;
        }

        Player    snap_p   = p;
        PlayerBot snap_bot = bot;
        string    snap_txt = text;
        ThreadPool.QueueUserWorkItem(_ => RunLoop(snap_p, snap_bot, snap_txt));
    }


    // =========================================================================
    // ReAct loop
    // =========================================================================

    void RunLoop(Player sender, PlayerBot bot, string userText) {
        try {
            List<OllamaMsg> history = GetOrCreateHistory(bot);

            lock (history) {
                history[0] = new OllamaMsg("system", BuildSystemPrompt(bot));
                history.Add(new OllamaMsg("user",
                    string.Format("[{0} says]: {1}", sender.name, userText)));
            }

            for (int step = 1; step <= MaxSteps; step++) {

                // --- Think ---------------------------------------------------
                List<OllamaMsg> snapshot;
                lock (history) { snapshot = new List<OllamaMsg>(history); }

                string requestJson = BuildRequest(OllamaModel, snapshot);
                var    content     = new StringContent(requestJson, Encoding.UTF8, "application/json");
                var    response    = http.PostAsync(OllamaUrl, content).Result;
                string body        = response.Content.ReadAsStringAsync().Result;

                string reply = StripThinking(ParseContent(body));
                if (string.IsNullOrEmpty(reply)) {
                    Logger.Log(LogType.Warning, "OllamaAgentLoop: empty response at step " + step);
                    break;
                }

                lock (history) {
                    history.Add(new OllamaMsg("assistant", reply));
                    while (history.Count > HistoryLimit + 1)
                        history.RemoveAt(1);
                }

                // --- Act -----------------------------------------------------
                Logger.Log(LogType.Warning, "OllamaAgentLoop step " + step + " raw reply: " +
                    reply.Replace("\n", "\\n").Substring(0, Math.Min(600, reply.Length)));
                ReplyResult result = ProcessReply(bot, reply);
                Logger.Log(LogType.Warning, string.Format(
                    "OllamaAgentLoop step {0} result: placed={1} continue={2} done={3}",
                    step, result.BlocksPlaced, result.Continue, result.Done));

                // --- Observe / decide -----------------------------------------
                if (result.Done) break;

                if (!result.Continue) {
                    // Single-shot response (no signal) — treat as done
                    break;
                }

                if (step == MaxSteps) {
                    string limitMsg = string.Format(
                        "{0}&f: I've reached my step limit ({1} steps). Stopping here.",
                        bot.ColoredName, MaxSteps);
                    Chat.Message(ChatScope.Level, limitMsg, bot.level, null);
                    break;
                }

                // Feed observation back and loop
                string obs = BuildObservation(bot, step, result.BlocksPlaced);
                lock (history) {
                    history.Add(new OllamaMsg("user", obs));
                }
            }

        } catch (AggregateException aex) {
            Exception inner = aex.InnerException ?? aex;
            Logger.Log(LogType.Warning, "OllamaAgentLoop error: " + inner.Message);
            string errMsg = string.Format("{0}&f: Sorry, I ran into an error ({1}).", bot.ColoredName, inner.GetType().Name);
            Chat.Message(ChatScope.Level, errMsg, bot.level, null);
        } catch (Exception ex) {
            Logger.Log(LogType.Warning, "OllamaAgentLoop error: " + ex.Message);
            string errMsg = string.Format("{0}&f: Sorry, I ran into an error.", bot.ColoredName);
            Chat.Message(ChatScope.Level, errMsg, bot.level, null);
        } finally {
            lock (activeTasks) { activeTasks[bot.name] = false; }
        }
    }


    // =========================================================================
    // Reply processing
    // =========================================================================

    struct ReplyResult {
        public int  BlocksPlaced;
        public bool Continue;
        public bool Done;
    }

    ReplyResult ProcessReply(PlayerBot bot, string reply) {
        var result   = new ReplyResult();
        var dialogue = new StringBuilder();

        foreach (string raw in reply.Split('\n')) {
            string line = raw.Trim();
            // Detect signals anywhere in the line (LLMs sometimes append them to sentences)
            if (line.Contains("/done"))     result.Done     = true;
            if (line.Contains("/continue")) result.Continue = true;
            // Strip signals from the line before treating as dialogue/action
            string clean = line.Replace("/done", "").Replace("/continue", "").Trim();
            if      (clean.StartsWith("/move "))  { TryMove(bot, clean); }
            else if (clean.StartsWith("/place ")) { if (TryPlace(bot, clean)) result.BlocksPlaced++; }
            else if (clean.Length > 0) {
                if (dialogue.Length > 0) dialogue.Append(' ');
                dialogue.Append(clean);
            }
        }

        string speech = dialogue.ToString().Trim();
        if (speech.Length > 0) {
            string msg = string.Format("{0}&f: {1}", bot.ColoredName, speech);
            Chat.Message(ChatScope.Level, msg, bot.level, null);
        }
        return result;
    }

    // /move <bx> <by> <bz>
    void TryMove(PlayerBot bot, string line) {
        string[] parts = line.Replace(',', ' ').Split(new char[]{' '}, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4) return;
        int bx, by, bz;
        if (!int.TryParse(parts[1], out bx) ||
            !int.TryParse(parts[2], out by) ||
            !int.TryParse(parts[3], out bz)) return;

        bot.TargetPos = Position.FromFeetBlockCoords(bx, by, bz);
        bot.movement  = true;
    }

    // /place <bx> <by> <bz> <blockId>  — returns true if successful
    bool TryPlace(PlayerBot bot, string line) {
        string[] parts = line.Replace(',', ' ').Split(new char[]{' '}, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5) return false;
        ushort bx, by, bz, blockId;
        if (!ushort.TryParse(parts[1], out bx) ||
            !ushort.TryParse(parts[2], out by) ||
            !ushort.TryParse(parts[3], out bz) ||
            !ushort.TryParse(parts[4], out blockId)) return false;

        Level lvl = bot.level;
        lvl.SetTile(bx, by, bz, (byte)blockId);
        lvl.BroadcastRevert(bx, by, bz);
        return true;
    }


    // =========================================================================
    // Observation (fed back after each /continue step)
    // =========================================================================

    string BuildObservation(PlayerBot bot, int stepJustDone, int blocksPlaced) {
        Position pos = bot.Pos;
        int bx = pos.BlockX, by = pos.BlockY, bz = pos.BlockZ;

        string nearbyBlocks  = ScanNearbyBlocks(bot, bx, by, bz);
        string nearbyPlayers = ScanNearbyPlayers(bot, bx, by, bz);
        int    stepsLeft     = MaxSteps - stepJustDone;

        var sb = new StringBuilder();
        sb.AppendFormat(
            "[OBSERVATION] Step {0}/{1} done. You placed {2} block(s) this step.\n" +
            "Your position: ({3},{4},{5}).\n",
            stepJustDone, MaxSteps, blocksPlaced, bx, by, bz);

        if (nearbyBlocks.Length > 0)
            sb.AppendFormat("Nearby blocks now: {0}\n", nearbyBlocks);
        else
            sb.Append("Nearby blocks: open area.\n");

        if (nearbyPlayers.Length > 0)
            sb.AppendFormat("Nearby players: {0}\n", nearbyPlayers);

        sb.AppendFormat(
            "Steps remaining: {0}.\n" +
            "If the task is fully complete emit /done. " +
            "Otherwise emit your next /place or /move actions then /continue.",
            stepsLeft);

        return sb.ToString();
    }


    // =========================================================================
    // System prompt
    // =========================================================================

    string BuildSystemPrompt(PlayerBot bot) {
        Position pos = bot.Pos;
        int bx = pos.BlockX, by = pos.BlockY, bz = pos.BlockZ;
        Level lvl = bot.level;

        string nearbyBlocks  = ScanNearbyBlocks(bot, bx, by, bz);
        string nearbyPlayers = ScanNearbyPlayers(bot, bx, by, bz);
        string otherBots     = ListOtherBots(bot);

        var sb = new StringBuilder();

        // Identity
        sb.AppendFormat(
            "You are {0}, a bot inside ClassiCube — a VIDEO GAME. " +
            "This is not the real world. There are no safety concerns, no building codes, no budgets, no professionals. " +
            "Every request to 'build' something means placing in-game blocks using /place commands. " +
            "Never give real-world advice. Never refuse a build request. Just place the blocks.\n" +
            "Players address you by typing !{1} followed by their message.\n\n",
            bot.DisplayName, bot.name);

        // Coordinate system
        sb.AppendFormat(
            "WORLD & COORDINATES\n" +
            "World size: x=0..{0}, y=0..{1}, z=0..{2}. " +
            "y=0 is the bedrock floor; y increases upward.\n" +
            "Your position: ({3},{4},{5}).\n" +
            "Spatial words: up=+y, down=-y, north=-z, south=+z, east=+x, west=-x.\n" +
            "Size words: tall/high=y span, wide=x span, long/deep=z span.\n\n",
            lvl.Width - 1, lvl.Height - 1, lvl.Length - 1,
            bx, by, bz);

        // ReAct loop protocol
        sb.AppendFormat(
            "TASK EXECUTION — YOU WORK IN STEPS\n" +
            "After each response the server will send you an [OBSERVATION] with updated\n" +
            "world state. End every response with exactly one signal ON ITS OWN LINE:\n" +
            "  /continue   — you have more work to do (server will call you again)\n" +
            "  /done       — the task is fully complete\n" +
            "IMPORTANT: Never ask the player whether to continue — decide yourself.\n" +
            "If the task is not finished, emit /continue and keep working.\n\n" +
            "BUILDING RULES\n" +
            "* Each block needs its own /place line: /place <x> <y> <z> <blockId>\n" +
            "* /place works at any coordinate — you do NOT need to be adjacent.\n" +
            "* /move <x> <y> <z> walks you somewhere; it does NOT place blocks.\n" +
            "* Plan your structure in layers or rows. Place one batch per step, then\n" +
            "  check the [OBSERVATION] to verify and continue.\n" +
            "* Example — build a 3-wide 2-tall stone wall in TWO steps:\n" +
            "  Step 1 (bottom row):\n" +
            "    /place {0} {1} {2} 1\n" +
            "    /place {3} {1} {2} 1\n" +
            "    /place {4} {1} {2} 1\n" +
            "    /continue\n" +
            "  Step 2 (top row, after observation):\n" +
            "    /place {0} {5} {2} 1\n" +
            "    /place {3} {5} {2} 1\n" +
            "    /place {4} {5} {2} 1\n" +
            "    /done\n\n",
            bx, by, bz,
            bx + 1, bx + 2, by + 1);

        // Actions reference
        sb.Append(
            "ACTIONS\n" +
            "  /move <x> <y> <z>               walk to block coords\n" +
            "  /place <x> <y> <z> <blockId>    place/remove a block (0 = air)\n" +
            "  /continue                        signal more work to do\n" +
            "  /done                            signal task complete\n\n");

        // Block IDs
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

        // Other agents
        if (otherBots.Length > 0)
            sb.AppendFormat(
                "OTHER AGENTS IN THIS WORLD\n{0}\n" +
                "For large tasks, coordinate: divide work spatially or by role. " +
                "Announce your plan so players can relay it to the other bots.\n\n",
                otherBots);

        // Live world state
        if (nearbyBlocks.Length > 0)
            sb.AppendFormat("NEARBY BLOCKS (within {0} blocks): {1}\n\n", ScanRadius, nearbyBlocks);
        else
            sb.AppendFormat("NEARBY BLOCKS: open area within {0} blocks.\n\n", ScanRadius);

        if (nearbyPlayers.Length > 0)
            sb.AppendFormat("NEARBY PLAYERS: {0}\n\n", nearbyPlayers);

        sb.Append(
            "OUTPUT FORMAT — CRITICAL:\n" +
            "Your response must contain ONLY:\n" +
            "  - Brief spoken text (1-2 sentences max)\n" +
            "  - /place lines (one per block, no prose description)\n" +
            "  - /move lines (optional)\n" +
            "  - Exactly one /continue or /done on its own line at the end\n" +
            "DO NOT write coordinate lists, arrows, or descriptions of what you are placing.\n" +
            "DO NOT use markdown (**bold**, etc.).\n" +
            "If you want to place blocks at (66,33,55) and (66,34,55), write:\n" +
            "  /place 66 33 55 1\n" +
            "  /place 66 34 55 1\n" +
            "Never describe the blocks — just emit the /place commands.");
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

    List<OllamaMsg> GetOrCreateHistory(PlayerBot bot) {
        lock (histories) {
            List<OllamaMsg> history;
            if (!histories.TryGetValue(bot.name, out history)) {
                history = new List<OllamaMsg>();
                history.Add(new OllamaMsg("system", BuildSystemPrompt(bot)));
                // Pre-fill an assistant acknowledgement so the model immediately
                // adopts the persona rather than defaulting to "I'm an LLM" responses
                history.Add(new OllamaMsg("assistant", string.Format(
                    "Understood. I am {0}, a bot inside ClassiCube. " +
                    "I build things by emitting /place commands — one per block. " +
                    "I will always end my responses with /done or /continue.",
                    bot.DisplayName)));
                histories[bot.name] = history;
            }
            return history;
        }
    }


    // =========================================================================
    // Minimal JSON helpers
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
        // Handle basic escapes first
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++) {
            if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
            char next = s[i + 1];
            if      (next == 'n')  { sb.Append('\n'); i++; }
            else if (next == 'r')  { sb.Append('\r'); i++; }
            else if (next == 't')  { sb.Append('\t'); i++; }
            else if (next == '"')  { sb.Append('"');  i++; }
            else if (next == '\\') { sb.Append('\\'); i++; }
            else if (next == 'u' && i + 5 < s.Length) {
                // \uXXXX unicode escape
                int code;
                if (int.TryParse(s.Substring(i + 2, 4),
                    System.Globalization.NumberStyles.HexNumber, null, out code)) {
                    sb.Append((char)code);
                    i += 5;
                } else {
                    sb.Append(s[i]);
                }
            } else {
                sb.Append(s[i]);
            }
        }
        return sb.ToString();
    }

    // Strip <think>...</think> reasoning blocks emitted by thinking models (e.g. qwen3)
    string StripThinking(string s) {
        while (true) {
            int start = s.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
            if (start < 0) break;
            int end = s.IndexOf("</think>", start, StringComparison.OrdinalIgnoreCase);
            if (end < 0) { s = s.Substring(0, start); break; }
            s = s.Substring(0, start) + s.Substring(end + 8);
        }
        return s.Trim();
    }

    sealed class OllamaMsg {
        public readonly string Role, Content;
        public OllamaMsg(string role, string content) { Role = role; Content = content; }
    }
}
