# TidyPots

A tiny Slay the Spire 2 mod that closes the gaps in your potion belt.

Drink the middle potion of three and the game leaves a hole: `[A][ ][C]`. With TidyPots the
belt always shows your potions packed to the left, `[A][C][ ]`, and a new potion lands in the
next free space on the right.

## Hold to discard

The Discard button in a potion's popup now has to be **held for one second**. A red fill sweeps
across the button and the potion sloshes while you hold it; let go early (or slide off the
button) and the sound stops and nothing happens. No more losing a potion to a misclick. Works
with a controller too: hold select.

## Co-op safe

The reordering is **purely visual**. Your real potion slots never move, and co-op tells the
other players which slot a potion is in, so rearranging the real slots would put the players
out of step. TidyPots only changes where each slot is drawn on your screen, so everyone stays
in sync and saves are unchanged. Hold-to-discard only delays the click; the discard itself is
the game's own. Other players don't need the mod.

## Install

Copy the `TidyPots` folder into `Slay the Spire 2/mods/`, or install through Vortex.

## Console

With any mod loaded, the game's dev console (backtick) is unlocked:

- `tidy diag`: shows the real slots next to the on-screen order.
- `tidy apply`: forces a re-sort.
- `tidy slosh <n>`: plays discard slosh sound n.

## Licence

MIT, by Tomasapan.
