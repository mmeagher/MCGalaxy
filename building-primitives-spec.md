# Building Primitives for OllamaAgent — Claude Code Briefing

---

## Implementation status

### Stage 1 — Complete (branch: `dev`)

All C# primitives are implemented in `plugins/OllamaAgent.cs`:

- `/box`, `/walls`, `/floor`, `/roof`, `/column`, `/clear`, `/door`, `/window`
- Shared `FillRegion` helper with set-then-broadcast pattern
- `NormalizeAndClamp` — swaps + clamps coords so corner order doesn't matter
- Safety limits: `MaxBlocksPerOp = 2000`, `MaxBlocksPerTurn = 8000`; operations exceeding either are skipped with a server log warning
- `ProcessReply` updated to route all new commands and track `blocksThisTurn`
- System prompt updated with compact command reference and CRITICAL RULES — **no recipes added yet** (deliberate, to keep prompt size and prefill latency minimal)

**Current behaviour:** primitives execute correctly server-side. The model (gemma3) has mixed results using them unprompted — it can handle simple requests (platform, wall, tower) but struggles with multi-primitive structures without recipe guidance.

---

### Stage 2 — Pending

Add 2–3 recipes to the system prompt. Recommended candidates:

1. **Cabin** — most requested structure type, exercises floor/walls/roof/door/window in sequence
2. **Stone tower** — exercises box+clear pattern, teaches interior hollowing
3. **Tree** — tiny recipe, useful for decoration and testing column+box

Before adding, fix these two bugs found in this spec:

- **Castle gatehouse, Step 4:** `/clear cx+1 cy+7 cz 65` — `/clear` takes no block argument. Should use `/place cx+N cy+7 cz 0` for each merlon gap, or omit the crenellations step.
- **Watchtower, Step 4 (ladder):** `/column cx cy cz 51` — only 4 arguments, missing `y2` and `z` is in the wrong position. Should be `/column cx cy cy+7 cz 51` (matching the pillar height).

Performance note: adding 2–3 recipes will increase system prompt size noticeably. Monitor response latency — if it becomes unacceptable, consider Stage 2b below.

---

### Stage 3 — Pending (if Stage 2 is insufficient)

If the model still struggles after recipes are added, or if prompt size becomes a performance problem:

- **Selective recipe injection:** detect keywords in the player's message (`"cabin"`, `"tower"`, `"bridge"`) and inject only the relevant recipe, rather than including all recipes in every request.
- **Remaining recipes:** add bridge, watchtower, fountain, greenhouse etc. only if the model needs them as examples.
- **Per-message context injection (Part 3 of this spec):** move the bot's position and nearby-block scan out of the system prompt and into a dynamic prefix on each user message. This prevents the system prompt from being invalidated by position changes each turn, potentially enabling Ollama's KV cache to reuse the static portion.

---

## Context

This document is a specification for modifying the `OllamaAgent.cs` plugin in the MCGalaxy fork at `https://github.com/mmeagher/MCGalaxy`. The plugin connects MCGalaxy bots to a local Ollama LLM so players can talk to bots and the bots can build in the world.

The plugin currently works: bots respond to `@BotName <message>`, maintain conversation history, call Ollama's `/api/chat` endpoint, and can execute `/move` and `/place` commands embedded in the LLM's reply. The problem is that building anything meaningful requires the LLM to emit hundreds of individual `/place x y z blockId` commands, which small models (7–13B) cannot do reliably. They lose track of coordinates, miscalculate dimensions, and overflow a single response.

## The solution: building primitives

Instead of asking the LLM to place blocks one at a time, give it high-level building commands that the plugin resolves into batch block operations server-side. A cabin that would need ~200 `/place` calls becomes 5–6 primitive calls. This reduces the LLM's task from "generate precise coordinates for every block" to "pick the right primitive, calculate two corner coordinates, choose a material." Small models can handle this.

The system prompt must include the full ClassiCube block palette, the complete set of available primitives with their syntax, and — critically — concrete building recipes that show how to compose primitives into structures. The recipes serve as few-shot examples that the model can adapt.

## What to modify

There are two parts to this change:

1. **Reply processing in the plugin (C#):** Add parsing and execution for the new primitive commands alongside the existing `/move` and `/place`. Each primitive takes two corners (or equivalent geometry) and a block ID, iterates over the region, calls `level.SetTile()` and `level.BroadcastRevert()` for each affected block. Must include bounds checking, coordinate normalisation, and a per-operation and per-turn block cap to prevent the LLM from filling the entire map.

2. **System prompt:** Replace the current minimal prompt with a comprehensive reference that includes the coordinate system, the full block palette, every primitive's syntax, material recommendations by use case, and building recipes with actual coordinate arithmetic.

---

## Part 1: Building primitives to implement

All primitives follow the same pattern: parse the command string, validate and clamp coordinates to level bounds, iterate over the affected region, call `SetTile`/`BroadcastRevert`. Coordinates should be normalised so the order of corners doesn't matter (swap if min > max).

### Safety limits

```
MaxBlocksPerOp   = 2000   // max blocks any single primitive can affect
MaxBlocksPerTurn = 8000   // total blocks across all primitives in one LLM reply
```

Skip any operation that would exceed these limits. Log a warning to the server console.

### 1.1 `/box <x1> <y1> <z1> <x2> <y2> <z2> <block>`

Fills a solid cuboid from corner (x1,y1,z1) to corner (x2,y2,z2) with the given block.

Use case: solid foundations, filled walls, platforms, solid pillars, any filled rectangular volume.

Implementation: triple nested loop over x, y, z from min to max of each axis.

### 1.2 `/walls <x1> <y1> <z1> <x2> <y2> <z2> <block>`

Fills only the four vertical faces of the cuboid. The interior is untouched.

Use case: building walls for rooms, houses, towers — the most common structural operation.

Implementation: same as `/box` but skip any block where `x > x1 && x < x2 && z > z1 && z < z2`.

### 1.3 `/floor <x1> <z1> <x2> <z2> <y> <block>`

Fills a horizontal plane at a single Y height from (x1,z1) to (x2,z2).

Note the parameter order: the Y comes after the two XZ corners because it's a flat surface at a fixed height.

Use case: floors, ceilings, flat roofs, platforms, bridges.

Implementation: double nested loop over x and z at fixed y.

### 1.4 `/roof <x1> <z1> <x2> <z2> <y> <block>`

Identical to `/floor` — this is a semantic alias so the LLM's output reads naturally ("lay a floor" vs "add a roof"). The plugin should route both to the same handler.

### 1.5 `/column <x> <y1> <y2> <z> <block>`

A vertical line of blocks at a single (x,z) position from y1 up to y2.

Use case: pillars, support columns, log-frame corners, chimneys, lampposts.

Implementation: single loop over y from min to max.

### 1.6 `/clear <x1> <y1> <z1> <x2> <y2> <z2>`

Fills a cuboid region with air (block 0). Equivalent to `/box ... 0` but without needing to specify the block.

Use case: carving out room interiors, making doorways, clearing space before building.

Implementation: delegate to the `/box` handler with block=0.

### 1.7 `/door <x> <y> <z>` (optional convenience)

Places air at (x,y,z) and (x,y+1,z) — a two-block-high opening. Saves the LLM from having to emit two separate `/place` calls for every door.

### 1.8 `/window <x> <y> <z>` (optional convenience)

Places glass (block 20) at (x,y,z). A minor convenience but reduces errors when the LLM forgets the glass block ID.

---

## Part 2: System prompt specification

The system prompt is the single most important factor in build quality. It must be comprehensive enough that the LLM doesn't need to guess, but structured so it doesn't waste context window on irrelevant information.

### 2.1 Coordinate system

```
X = east–west (increases east)
Y = up–down (increases upward — this is the height axis)
Z = north–south (increases south)

Ground level varies by map. The bot's current position is provided in each message.
Level dimensions are provided so the model can stay in bounds.

When building, pick a starting corner and calculate all other coordinates relative to it.
Floor goes at Y=cy. Walls go from Y=cy+1 up. Roof goes at the top.
```

### 2.2 Block palette

The full ClassiCube block reference. Group by use case so the LLM can find the right material quickly.

```
=== STRUCTURAL ===
1  stone              — heavy masonry, castle walls, foundations
4  cobblestone        — rustic walls, paths, medieval builds
5  wood_planks        — floors, interior walls, warm builds
17 log                — structural frames, corners, pillars, cabin walls
45 brick              — formal walls, chimneys, industrial
52 sandstone          — desert builds, warm-toned walls
65 stone_brick        — refined masonry, temples, government buildings
43 double_slab        — thick floors, countertops, formal surfaces
44 slab               — thin roofs, shelves, trim
63 pillar             — columns, classical architecture
42 iron_block         — modern/industrial, structural steel
41 gold_block         — decorative accent, temples, treasure

=== NATURAL / TERRAIN ===
0  air                — empty space (use for doors, windows, clearing)
2  grass              — ground surface, green roofs
3  dirt               — ground fill, garden beds
12 sand               — beaches, desert terrain, paths
13 gravel             — paths, rough ground
8  water              — lakes, moats, fountains
9  still_water        — calm water features, pools
10 lava               — forges, traps, volcanic
11 still_lava         — contained lava features
53 snow               — winter terrain, snow-capped roofs
60 ice                — frozen lakes, decorative

=== TRANSPARENT / DECORATIVE ===
20 glass              — windows (the only transparent block)
47 bookshelf          — libraries, interior decoration
64 crate              — storage areas, docks, workshops
37 dandelion          — garden flower
38 rose               — garden flower
39 mushroom           — forest floor, caves
40 red_mushroom       — forest floor, caves
6  sapling            — gardens, saplings
18 leaves             — trees, hedges, garden walls, green roofs
19 sponge             — decorative, absorbent
51 rope               — hanging decorations, bridges

=== COLOURS (wool blocks) ===
21 red     22 orange    23 yellow    24 lime
25 green   26 teal      27 aqua      28 cyan
29 blue    30 indigo    31 violet    32 magenta
33 pink    34 black     35 gray      36 white
55 light_pink   56 forest_green   57 brown   58 deep_blue   59 turquoise

=== RARE / SPECIAL ===
7  bedrock            — indestructible, admin only
14 gold_ore   15 iron_ore   16 coal_ore
46 tnt                — explosive
48 mossy_cobblestone  — ruins, aged builds
49 obsidian           — dark, heavy
50 cobblestone_slab   — thin cobblestone surface
54 fire               — decorative fire
61 ceramic_tile       — clean floors, bathrooms
62 magma              — volcanic, nether-like
```

### 2.3 Primitive command reference

Include in the system prompt exactly as the LLM should use them:

```
=== BUILDING COMMANDS ===
Include these as separate lines in your reply. Use only integer coordinates.

/move <x> <y> <z>
  Walk to those coordinates.

/place <x> <y> <z> <block>
  Place a single block. Good for details, doors, windows.

/box <x1> <y1> <z1> <x2> <y2> <z2> <block>
  Fill a solid cuboid from corner1 to corner2.

/walls <x1> <y1> <z1> <x2> <y2> <z2> <block>
  Build 4 vertical walls (hollow inside) between two corners.

/floor <x1> <z1> <x2> <z2> <y> <block>
  Flat horizontal surface at height y.

/roof <x1> <z1> <x2> <z2> <y> <block>
  Same as /floor. Use for roofs and ceilings.

/column <x> <y1> <y2> <z> <block>
  Vertical pillar from y1 up to y2 at position (x,z).

/clear <x1> <y1> <z1> <x2> <y2> <z2>
  Remove all blocks in a region (fill with air).

/door <x> <y> <z>
  Make a 2-high opening (air) at (x,y,z) and (x,y+1,z).

/window <x> <y> <z>
  Place glass at (x,y,z).
```

### 2.4 Building recipes

These are the most critical part of the prompt. They serve as few-shot examples. Each recipe uses a named reference point `(cx, cy, cz)` and shows the exact sequence of primitives. The system prompt must instruct the LLM to substitute actual coordinates.

**Important instruction to include in the prompt:**

```
CRITICAL RULES:
- Replace cx, cy, cz with ACTUAL INTEGER NUMBERS. Never write "cx+3" in a command.
- Calculate coordinates before writing commands. For example, if cx=10 and you need cx+6, write 16.
- Always describe your plan in 1-2 sentences BEFORE emitting any commands.
- Y increases upward. Floors go at cy, walls start at cy+1.
- Use /clear first if the area is not empty.
```

---

#### Recipe: Cabin (7×5 footprint, 4 walls high)

Dimensions: 7 blocks wide (X), 5 blocks deep (Z), 4 blocks of wall height, plus roof. Total footprint including walls.

Starting from corner (cx, cy, cz) where cy is ground level:

```
Step 1 — Floor (wood planks):
/floor cx cz cx+6 cz+4 cy 5

Step 2 — Walls (log, 4 blocks high starting above the floor):
/walls cx cy+1 cz cx+6 cy+4 cz+4 17

Step 3 — Roof (slab, one layer above the walls):
/floor cx cz cx+6 cz+4 cy+5 44

Step 4 — Door (front wall, centred, 2 high):
/door cx+3 cy+1 cz

Step 5 — Windows (side walls, at eye level):
/window cx cy+3 cz+2
/window cx+6 cy+3 cz+2

Step 6 — Back windows:
/window cx+2 cy+3 cz+4
/window cx+4 cy+3 cz+4
```

Materials: 5=wood_planks (floor), 17=log (walls), 44=slab (roof), 20=glass (windows).

Total primitives: 8. Blocks affected: ~140 (floor 35 + walls ~100 + roof 35 − interior + details).

---

#### Recipe: Stone tower (5×5 footprint, 10 high)

Starting from corner (cx, cy, cz):

```
Step 1 — Solid base and outer shell (cobblestone):
/box cx cy cz cx+4 cy+9 cz+4 4

Step 2 — Hollow interior (clear 3x3 column inside):
/clear cx+1 cy+1 cz+1 cx+3 cy+8 cz+1+2

Step 3 — Door:
/door cx+2 cy+1 cz

Step 4 — Windows on each face at level 5:
/window cx+2 cy+5 cz
/window cx+2 cy+5 cz+4
/window cx cy+5 cz+2
/window cx+4 cy+5 cz+2

Step 5 — Crenellations (top edge, every other block):
/place cx cy+10 cz 4
/place cx+2 cy+10 cz 4
/place cx+4 cy+10 cz 4
/place cx cy+10 cz+4 4
/place cx+2 cy+10 cz+4 4
/place cx+4 cy+10 cz+4 4
/place cx cy+10 cz+2 4
/place cx+4 cy+10 cz+2 4
```

Materials: 4=cobblestone.

---

#### Recipe: Small house with pitched roof (9×7 footprint, 4 walls + 3 roof)

Starting from corner (cx, cy, cz):

```
Step 1 — Foundation (stone, single layer):
/floor cx cz cx+8 cz+6 cy 1

Step 2 — Floor (wood planks, on top of foundation):
/floor cx+1 cz+1 cx+7 cz+5 cy+1 5

Step 3 — Walls (brick, 4 high from above floor):
/walls cx cy+2 cz cx+8 cy+5 cz+6 45

Step 4 — Interior clearing (in case walls filled interior):
/clear cx+1 cy+2 cz+1 cx+7 cy+5 cz+5

Step 5 — Roof layer 1 (widest, slab):
/floor cx-1 cz cx+9 cz+6 cy+6 44

Step 6 — Roof layer 2 (narrower):
/floor cx cz+1 cx+8 cz+5 cy+7 44

Step 7 — Roof ridge:
/floor cx+1 cz+2 cx+7 cz+4 cy+8 44

Step 8 — Front door:
/door cx+4 cy+2 cz

Step 9 — Windows (front):
/window cx+2 cy+3 cz
/window cx+6 cy+3 cz

Step 10 — Windows (sides):
/window cx cy+3 cz+3
/window cx+8 cy+3 cz+3

Step 11 — Windows (back):
/window cx+2 cy+3 cz+6
/window cx+6 cy+3 cz+6
```

Materials: 1=stone (foundation), 5=wood_planks (floor), 45=brick (walls), 44=slab (roof).

---

#### Recipe: Garden wall / fence (along X axis)

Starting from (cx, cy, cz), 15 blocks long, 3 high:

```
/box cx cy cz cx+14 cy+2 cz 4
```

With gate (3 blocks wide centred):

```
/box cx cy cz cx+5 cy+2 cz 4
/box cx+9 cy cz cx+14 cy+2 cz 4
/floor cx+6 cz cx+8 cz cy+2 44
```

Materials: 4=cobblestone (wall), 44=slab (gate lintel).

---

#### Recipe: Bridge (spanning Z axis, 3 wide, length variable)

Starting from (cx, cy, cz), bridge length = 12 blocks:

```
Step 1 — Deck (wood planks):
/floor cx cz cx+2 cz+11 cy 5

Step 2 — Railings (left and right, 2 high):
/box cx cy+1 cz cx cy+2 cz+11 17
/box cx+2 cy+1 cz cx+2 cy+2 cz+11 17

Step 3 — Clear the walkway between railings:
/clear cx+1 cy+1 cz cx+1 cy+2 cz+11

Step 4 — Support pillars (if over a gap):
/column cx cy-4 cy-1 cz 17
/column cx+2 cy-4 cy-1 cz 17
/column cx cy-4 cy-1 cz+11 17
/column cx+2 cy-4 cy-1 cz+11 17
```

Materials: 5=wood_planks (deck), 17=log (railings and supports).

---

#### Recipe: Watchtower platform (open-air, 3×3 footprint, 8 high)

Starting from (cx, cy, cz):

```
Step 1 — Four corner pillars:
/column cx cy cy+7 cz 17
/column cx+2 cy cy+7 cz 17
/column cx cy cy+7 cz+2 17
/column cx+2 cy cy+7 cz+2 17

Step 2 — Platform floor at top:
/floor cx cz cx+2 cz+2 cy+7 5

Step 3 — Platform railings (one block high on the edges):
/box cx cy+8 cz cx+2 cy+8 cz 17
/box cx cy+8 cz+2 cx+2 cy+8 cz+2 17
/box cx cy+8 cz cx cy+8 cz+2 17
/box cx+2 cy+8 cz cx+2 cy+8 cz+2 17

Step 4 — Ladder (rope blocks on one pillar):
/column cx cy cz 51
```

Materials: 17=log (pillars, railings), 5=wood_planks (platform), 51=rope (ladder).

---

#### Recipe: Market stall / shop (5×4 footprint, open front)

Starting from (cx, cy, cz), front faces -Z:

```
Step 1 — Floor:
/floor cx cz cx+4 cz+3 cy 5

Step 2 — Back wall:
/box cx cy+1 cz+3 cx+4 cy+3 cz+3 5

Step 3 — Side walls:
/box cx cy+1 cz cx cy+3 cz+3 5
/box cx+4 cy+1 cz cx+4 cy+3 cz+3 5

Step 4 — Roof (overhangs front by 1 block):
/floor cx-1 cz-1 cx+5 cz+4 cy+4 44

Step 5 — Counter (slab at front):
/floor cx+1 cz cx+3 cz cy+1 44

Step 6 — Shelves (bookshelf block on back wall interior):
/place cx+1 cy+1 cz+3 47
/place cx+2 cy+1 cz+3 47
/place cx+3 cy+1 cz+3 47

Step 7 — Crates on floor:
/place cx+1 cy+1 cz+2 64
/place cx+3 cy+1 cz+2 64
```

Materials: 5=wood_planks (walls/floor), 44=slab (roof/counter), 47=bookshelf (shelves), 64=crate.

---

#### Recipe: Tree (decorative, ~5 high)

Starting from (cx, cy, cz) where cy is ground level:

```
Step 1 — Trunk:
/column cx cy+1 cy+3 cz 17

Step 2 — Leaf canopy (3x3x2 centred on trunk top):
/box cx-1 cy+4 cz-1 cx+1 cy+5 cz+1 18

Step 3 — Top leaf:
/place cx cy+6 cz 18
```

Materials: 17=log (trunk), 18=leaves (canopy).

---

#### Recipe: Street lamp

Starting from (cx, cy, cz):

```
/column cx cy+1 cy+3 cz 42
/place cx cy+4 cz 23
```

Materials: 42=iron_block (post), 23=yellow (lamp).

---

#### Recipe: Fountain (5×5 footprint)

Starting from (cx, cy, cz):

```
Step 1 — Basin rim:
/box cx cy cz cx+4 cy+1 cz+4 1
/clear cx+1 cy cz+1 cx+3 cy+1 cz+3

Step 2 — Water inside basin:
/floor cx+1 cz+1 cx+3 cz+3 cy 9

Step 3 — Centre pillar:
/column cx+2 cy+1 cy+3 cz+2 1

Step 4 — Water on top of pillar:
/place cx+2 cy+4 cz+2 9
```

Materials: 1=stone (rim and pillar), 9=still_water.

---

#### Recipe: Castle gatehouse (11×7, 8 high, with archway)

Starting from (cx, cy, cz):

```
Step 1 — Main structure (stone brick):
/box cx cy cz cx+10 cy+7 cz+6 65

Step 2 — Interior hollowing:
/clear cx+1 cy+1 cz+1 cx+9 cy+6 cz+5

Step 3 — Archway passage (through Z axis, 3 wide, 4 high):
/clear cx+4 cy+1 cz cx+6 cy+4 cz+6

Step 4 — Crenellations (top):
/clear cx+1 cy+7 cz 65
/clear cx+3 cy+7 cz 65
/clear cx+5 cy+7 cz 65
/clear cx+7 cy+7 cz 65
/clear cx+9 cy+7 cz 65

Step 5 — Guard windows:
/window cx+2 cy+5 cz
/window cx+8 cy+5 cz
/window cx+2 cy+5 cz+6
/window cx+8 cy+5 cz+6
/window cx cy+5 cz+3
/window cx+10 cy+5 cz+3
```

Materials: 65=stone_brick.

Note: Step 4 crenellation clearing should use individual `/place cx+N cy+7 cz 0` for each merlon gap to avoid clearing the entire row. Adjust the approach based on desired crenellation pattern — the idea is alternating solid/air along the top edge.

---

#### Recipe: Greenhouse (7×5, glass walls, wood frame)

Starting from (cx, cy, cz):

```
Step 1 — Floor (dirt for planting):
/floor cx cz cx+6 cz+4 cy 3

Step 2 — Log frame corners (4 columns):
/column cx cy+1 cy+4 cz 17
/column cx+6 cy+1 cy+4 cz 17
/column cx cy+1 cy+4 cz+4 17
/column cx+6 cy+1 cy+4 cz+4 17

Step 3 — Glass walls:
/walls cx cy+1 cz cx+6 cy+3 cz+4 20

Step 4 — Glass roof:
/floor cx cz cx+6 cz+4 cy+4 20

Step 5 — Door:
/door cx+3 cy+1 cz

Step 6 — Interior flowers:
/place cx+1 cy+1 cz+1 38
/place cx+3 cy+1 cz+2 37
/place cx+5 cy+1 cz+1 38
/place cx+2 cy+1 cz+3 37
/place cx+4 cy+1 cz+3 38
```

Materials: 3=dirt (floor), 17=log (frame), 20=glass (walls/roof), 37=dandelion, 38=rose.

---

## Part 3: Per-message context injection

In addition to the static system prompt, each user message should be prepended with a dynamic context block giving the bot's current state:

```
[Context: You are at (32, 10, 48). Level is 128x64x128 (max: 127,63,127).
Players: Mark(30,10,50) Parker(45,10,40).
Ground below: (30,9,46)=2 (31,9,47)=2 (32,9,48)=2 (33,9,47)=2 ...]
```

This means the LLM always knows where it is (even after moving), the level boundaries, and what's around it — without bloating the system prompt with stale information.

The ground scan should cover a 7×7 area around the bot at foot level (Y-1), capped at ~20 entries to avoid prompt bloat.

---

## Part 4: Implementation notes for Claude Code

### Files to modify

The plugin is a single file: `plugins/OllamaAgent.cs`. It compiles in-game with `/pcompile OllamaAgent`. It cannot reference external assemblies — it uses only `System.*` and `MCGalaxy.*`. HTTP calls use `HttpWebRequest`, not `HttpClient`.

### MCGalaxy API reference

These are the key APIs used by the building primitives:

- `bot.level.SetTile(ushort x, ushort y, ushort z, byte block)` — set a single block in the level
- `bot.level.BroadcastRevert(ushort x, ushort y, ushort z)` — send the block at (x,y,z) to all players in the level
- `bot.level.GetBlock(ushort x, ushort y, ushort z)` — returns `BlockID` (which is `ushort`), returns `Block.Invalid` if out of bounds
- `bot.level.Width`, `.Height`, `.Length` — level dimensions (ushort)
- `bot.level.MaxX`, `.MaxY`, `.MaxZ` — maximum valid coordinate for each axis (Width-1 etc.)
- `Block.Air = 0`
- `Position.FromFeetBlockCoords(int x, int y, int z)` — convert block coords to internal position format for bot movement
- `bot.TargetPos`, `bot.movement = true` — make the bot walk toward a position
- `Chat.Message(ChatScope.Level, string msg, Level level, ChatMessageFilter filter)` — send text to all players in a level
- `bot.ColoredName` — the bot's name with colour code prefix
- `bot.Pos.BlockX`, `.BlockY`, `.BlockZ` — bot's current block coordinates

### Block operations pattern

For batch operations, set all tiles first, then broadcast all reverts. This is more efficient than interleaving SetTile/BroadcastRevert:

```csharp
// Set phase
for (int x = x1; x <= x2; x++)
for (int y = y1; y <= y2; y++)
for (int z = z1; z <= z2; z++) {
    lvl.SetTile((ushort)x, (ushort)y, (ushort)z, (byte)block);
}

// Broadcast phase
for (int x = x1; x <= x2; x++)
for (int y = y1; y <= y2; y++)
for (int z = z1; z <= z2; z++) {
    lvl.BroadcastRevert((ushort)x, (ushort)y, (ushort)z);
}
```

### Coordinate validation pattern

Every primitive should:
1. Parse all arguments, returning early (with 0 blocks placed) if parsing fails
2. Normalise corners: `if (a > b) { int t = a; a = b; b = t; }`
3. Clamp to level bounds: `if (lo < 0) lo = 0; if (hi > lvl.MaxX) hi = lvl.MaxX;`
4. Calculate volume and check against `MaxBlocksPerOp` and `MaxBlocksPerTurn`
5. Execute the block operations

### Reply parsing

The LLM's reply is split on newlines. Each line is checked for a command prefix (`/box `, `/walls `, etc.). Lines that don't match any command are collected as dialogue and broadcast as the bot's speech. The existing `ProcessReply` method handles this — add new command prefixes to the if/else chain.

Each command handler should return the number of blocks placed, and a running total should be maintained to enforce `MaxBlocksPerTurn`.

### The `/door` and `/window` convenience commands

These are trivial:

```csharp
// /door <x> <y> <z>  — 2-high air opening
void TryDoor(PlayerBot bot, string line) {
    // parse x, y, z
    // place air at (x, y, z) and (x, y+1, z)
}

// /window <x> <y> <z> — single glass block
void TryWindow(PlayerBot bot, string line) {
    // parse x, y, z
    // place glass (20) at (x, y, z)
}
```

These exist so the LLM doesn't have to remember that air=0 and glass=20 for the most common detail operations.

---

## Summary of standard designs

| Structure | Footprint (X×Z) | Height (walls) | Total height | Key primitives | Primary materials |
|-----------|-----------------|----------------|--------------|----------------|-------------------|
| Cabin | 7×5 | 4 | 6 | floor, walls, roof, door, window | 5, 17, 44 |
| Stone tower | 5×5 | 9 | 10+ | box, clear, window, place | 4 |
| House with pitched roof | 9×7 | 4 | 9 | floor×2, walls, clear, floor×3, door, window | 1, 5, 45, 44 |
| Garden wall | N×1 | 3 | 3 | box | 4 |
| Bridge | 3×N | 1 deck + 2 rail | 3 | floor, box, clear, column | 5, 17 |
| Watchtower | 3×3 | 7 pillars | 9 | column×4, floor, box, column | 17, 5, 42 |
| Market stall | 5×4 | 3 (open front) | 5 | floor, box×2, floor, place | 5, 44, 47, 64 |
| Tree | 3×3 canopy | 3 trunk | 7 | column, box, place | 17, 18 |
| Fountain | 5×5 | 1 basin | 5 | box, clear, floor, column, place | 1, 9 |
| Castle gatehouse | 11×7 | 7 | 8 | box, clear×2, place, window | 65 |
| Greenhouse | 7×5 | 3 glass | 5 | floor, column×4, walls, floor, door, place | 3, 17, 20 |
| Street lamp | 1×1 | 4 | 5 | column, place | 42, 23 |