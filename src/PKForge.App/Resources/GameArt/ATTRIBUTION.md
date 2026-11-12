# Luminescent Platinum artwork

`luminescent-platinum.png` uses the Luminescent Platinum community artwork by
samurainoodles, published on SteamGridDB on 2023-07-23:
https://www.steamgriddb.com/grid/351886

The hero artwork is a landscape crop of the same source image. It is included
solely as bundled game-library artwork, alongside the existing SteamGridDB
assets in this directory.

# SteamGridDB game media

Cartridge icons (`GameArt/`), second-screen logos (`GameLogo/`), and hero
banners (`GameHero/`) were fetched once from SteamGridDB at build time using a
local, gitignored API key. Prefer the `icons` asset type for cartridges and the
`logos` asset type for the square second screen; logos must keep transparency.


# Pokémon Radical Red artwork

`radical-red.png` (cartridge icon) and the transparent second-screen logo use
artwork published on SteamGridDB:
icon https://cdn2.steamgriddb.com/icon/9d818a0585a5b089adc25793fd466874.png and
logo https://cdn2.steamgriddb.com/logo/89c30ca363566155b1dec2102063c957.png.
Included solely as bundled game-library artwork.

# Pokémon Compass artwork

`compass.png` uses the Pokémon Compass artwork by Xrles, published on
SteamGridDB for game https://www.steamgriddb.com/game/5440320:
icon https://www.steamgriddb.com/icon/49085 (cartridge) and grid
https://www.steamgriddb.com/grid/494345 (hero crop). The second-screen logo
is a PKForge-typeset wordmark (MPLUSRounded1c ExtraBold) on transparency;

# Pokémon Unbound artwork

`unbound.png` uses the Pokémon Unbound artwork published on SteamGridDB for game
https://www.steamgriddb.com/game/5274554: the grid by TheFrye
(https://www.steamgriddb.com/grid/721334, cartridge icon and hero crop) and the
logo by NESDUB (https://www.steamgriddb.com/logo/159625, transparent second-screen
logo). Included solely as bundled game-library artwork.
SteamGridDB hosts no transparent logo for this game. Included solely as bundled
game-library artwork.

# Hand-picked art refresh (September 21, 2026)

The cartridge icons (`GameArt/`) and second-screen logos (`GameLogo/`) for 49 games
were re-picked by hand from SteamGridDB by the project owner, replacing the
automatically fetched set. Source art remains SteamGridDB community submissions under
their respective licenses; individual asset credits stay on steamgriddb.com. Hero
banners (`GameHero/`) are unchanged. Bundled art cache version bumped to v7.

# ROM hack art (November 2026)

Provided by the project maintainer from community sources and converted to the bundled
formats (cartridge: trimmed and fit into 256x256 on transparency; logo: trimmed and fit
to about 640x340, never upscaled past 2x). One line per file:

- `GameArt/black-pearl-emerald.png`: Black Pearl Emerald cartridge. Source: provided by the maintainer.
- `GameArt/crystal-advance-redux.png`: Crystal Advance Redux cartridge. Source: provided by the maintainer.
- `GameArt/elite-redux.png`: Elite Redux cartridge (title screen). Source: provided by the maintainer.
- `GameArt/emerald-enhanced.png`: Emerald Enhanced cartridge. Source: provided by the maintainer.
- `GameArt/emerald-ex.png`: Emerald Ex cartridge (title screen). Source: provided by the maintainer.
- `GameArt/emerald-exceeded.png`: Emerald Exceeded cartridge (box art). Source: provided by the maintainer.
- `GameArt/emerald-seaglass.png`: Emerald Seaglass cartridge (box art). Source: provided by the maintainer.
- `GameArt/fire-emerald.png`: Fire Emerald cartridge (promo art). Source: provided by the maintainer.
- `GameArt/fire-red-extended.png`: Fire Red Extended cartridge (box art). Source: provided by the maintainer.
- `GameArt/heart-and-soul.png`: Heart and Soul cartridge. Source: provided by the maintainer.
- `GameArt/lazarus.png`: Lazarus cartridge. Source: provided by the maintainer.
- `GameArt/lucid.png`: Lucid cartridge (box art). Source: provided by the maintainer.
- `GameArt/origin-heartgold.png`: Origin HeartGold cartridge (opening art). Source: provided by the maintainer.
- `GameArt/perfect-emerald.png`: Perfect Emerald cartridge (title screen). Source: provided by the maintainer.
- `GameArt/quetzal.png`: Quetzal cartridge. Source: provided by the maintainer.
- `GameArt/recharged-yellow.png`: Recharged Yellow cartridge (box art). Source: provided by the maintainer.
- `GameArt/rowe.png`: R.O.W.E. cartridge (box art). Source: provided by the maintainer.
- `GameArt/soul-gold.png`: Soul Gold cartridge (box art). Source: provided by the maintainer.
- `GameArt/team-rocket-edition.png`: Team Rocket Edition cartridge. Source: provided by the maintainer.
- `GameLogo/crystal-advance-redux.png`: Crystal Advance Redux logo. Source: provided by the maintainer.
- `GameLogo/elite-redux.png`: Elite Redux logo. Source: provided by the maintainer.
- `GameLogo/emerald-enhanced.png`: Emerald Enhanced logo. Source: provided by the maintainer.
- `GameLogo/emerald-exceeded.png`: Emerald Exceeded logo. Source: provided by the maintainer.
- `GameLogo/fire-red-extended.png`: Fire Red Extended logo. Source: provided by the maintainer.
- `GameLogo/heart-and-soul.png`: Heart and Soul logo. Source: provided by the maintainer.
- `GameLogo/lazarus.png`: Lazarus logo. Source: provided by the maintainer.
- `GameLogo/lucid.png`: Lucid logo. Source: provided by the maintainer.
- `GameLogo/origin-heartgold.png`: Origin HeartGold logo. Source: provided by the maintainer.
- `GameLogo/quetzal.png`: Quetzal logo. Source: provided by the maintainer.
- `GameLogo/recharged-yellow.png`: Recharged Yellow logo. Source: provided by the maintainer.
- `GameLogo/rowe.png`: R.O.W.E. logo. Source: provided by the maintainer.
- `GameLogo/team-rocket-edition.png`: Team Rocket Edition logo. Source: provided by the maintainer.

Engine names map to these files by slug (`GameArt.GetAssetSlug`): "Heart and Soul 2.0"
uses `heart-and-soul` and "R.O.W.E." uses `rowe`. Bundled art cache version bumped to v11.
