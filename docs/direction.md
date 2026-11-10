# Game direction

"Decided" is settled. "Open" is not: do not build on an open item, ask.

## The game in one line

A multiplayer space sandbox: build ships from blocks, mine, and command a fleet across one continuous galaxy that
AI factions are fighting over.

Reference for feel: Avorion (camera scheme, sector map, ship data sheet). A reference, not a clone target.

## Decided: the game

- **PvE first.** The galaxy and its factions are the opponent. PvP is supported as a server setting.
- **One space, no loading.** Sectors are an index, not rooms. No walls, no loading screens, jumps included.
- **Ships are their blocks.** Mass, thrust, turning, power, shield and hull all come from the blocks. Damage is
  per block; a lost block is lost.
- **Two views of the same space.** Pilot view to fly one ship; command (RTS) view to select and order many.
- **You are not your ship.** You can leave a ship in a drone and take control of any ship you own.

## Decided: the world

- **AI factions are real actors.** Each runs its own economy, stations and ships, sets up supply lines, fights
  wars, and gains and loses territory. Players live inside that, they do not trigger it.
- **Players do what factions do.** They build ships, found and build stations, claim stations and destroy them.
- **Players form factions and alliances** with each other. A player faction and an AI faction are the same kind
  of thing: everything in space is owned by a faction.
- **A lost ship is gone.** It is rebuilt at a shipyard from its blueprint, for resources and time.
- **The world advances only while someone is connected.** With nobody online it pauses.
- **Travel.** Sublight inside a sector, faster-than-light between them. Three drive kinds (hyperspace, wormhole,
  rift). No range limit yet; limits come later from modules, power and mass.
- **Resources.** Asteroids carry ore: iron, copper, nickel, titanium, silver, gold. More kinds later.

## Decided: networking

- **1 to 128 players in one world on one server.**
- **A lot of things in space.** Debris, asteroid pieces, stations, ships, items, resources and canisters must be
  cheap to have around in large numbers.
- **Server decides everything that can be cheated.** Clients predict their own ship and get corrected.
- **One code path.** Singleplayer is a server inside the game; multiplayer is the same server on its own.
- **Target feel:** right at 100 ms round trip and 1% loss.

## What that demands of the engine

Not built yet. These are the consequences of the decisions above, in the order they block other work.

1. **One store for every kind of thing**, not just hulls: ships, stations, fighters, asteroid pieces, debris,
   canisters, gas fields. Each is owned by a faction, not by a player; a player acts through their faction.
2. **Things at rest cost nothing to send.** Tell clients once, then stay silent until something disturbs them.
   Asteroids already work this way; debris and canisters follow the same rule.
3. **Send only what is near and what changed**, most important first, inside a bandwidth budget per player.
4. **Two levels of simulation.** Sectors with a player in them run in full. The rest of the galaxy runs as a
   cheap strategic model (faction economy, supply lines, fleets as numbers), so factions keep living without
   128 sectors of physics.
5. **Sectors simulated in parallel** on the job graph: one occupied sector, one job.

Limits that go first: ship designs capped at 64 blocks, full-state snapshots, hull-only storage.

## Planned, in order

1. The entity store and the entity kinds: ships, stations, fighters, asteroids, gas fields.
2. Interest management and delta snapshots.
3. Mining and the loop it feeds: mine, haul, refine, sell, build. Shipyards.
4. Factions: strategic model first, then their ships and stations in occupied sectors.
5. Ship building in game.

## Open

- Whether players can make deals with AI factions (alliance, trade pact, war), or AI factions only react.
- What rank and rights exist inside a player faction: who may fly, order, build or scrap what.
- What PvP rules a server can set: everywhere, by sector, or by consent.
- Saving: how often, and what a crash may lose.
