# Poképark local test build

Open **Poképark** from Home. The app chooses an initial group once at startup if there are no previous residents, then saves it. Entering or leaving the park never rerolls. If the initial app start has no available Pokémon, invite them from their boxes later. Existing residents from the earlier test build are retained.

## Invite and meet residents

Select a Pokémon in a save box or the Bank, open its action menu, and choose **Send to Poképark**. This saves a visual resident; the original Pokémon stays untouched. Up to 12 residents can live in the park. Saved residents remain even when another save is opened or their source entry is removed.

Tap a resident, or select with left/right or L/R and press A, to meet it. The card shows its current mood/activity, a playful park personality, favorite things and a small park story. These are fictional scene traits, not official species lore or changes to the Pokémon's Nature/friendship. Say hello for a reaction; choose Leave the park to remove its visual copy. An intentionally emptied park stays empty.

Residents alternate short strolls and stationary pauses. Their direction follows their movement. PMD Walk and Idle animations are loaded independently; missing Idle holds a still pose rather than playing Walk while stationary. Existing bundled sprites remain available offline; if no walking sheet is available, the resident rests instead of sliding with a static sprite. See [sprite attribution](POKEPARK_SPRITES.md).

## Android widget

Visit the park, then use Cocoon's widget picker to add **Poképark** under PKForge. The meadow fills the widget with no title strip or inner border. It is resizable and uses higher-resolution, forward-only ambient Idle frames, with no reversed walking. Tap it to open the park. Android owns the short animation loop; no rendering service stays active in the background. Actual launcher rendering needs testing on Cocoon.

## Device checks

1. Reopen the park and restart the app: residents remain the same.
2. Send a Pokémon from a save and from the Bank; confirm originals stay in place.
3. Check left/right/up/down movement and stationary Idle animation.
4. Tap residents; test gamepad selection, greetings and removal.
5. Empty the park and restart: no automatic refill.
6. Add/resize the widget; verify the meadow fills it and Pokémon are legible.
7. Test offline sprites and background/foreground animation lifecycle.
