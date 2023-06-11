**HatMouse** is a quick and dirty site to show Steam game prizes for events on the Stardew Valley
Discord. Currently hosted at https://hatmouse.smapi.io.

# Setup
## Build prize list
Our prizes usually consist of Steam keys from sites like Humble Bundle donated by server staff
members. These generally only have a display name (like "Bastion Steam Key"), with no Steam link or
other info.

To build the prize list:

1. Install the latest version of [LINQPad](https://www.linqpad.net/).
2. If you're not based in the US, connect to a US-based VPN if you want prices consistently in USD.
3. Replace `scripts/game keys.csv` with the list of game keys.

   This should have three columns:
   * `Source` is the staff member who donated the game key. This is ignored by the code, it's just
     tracked so they can provide the key when a winner claims it.
   * `Title` is the game key name, ideally with text like `Steam Key` or `(Steam)` removed.
   * `OverrideAppId` (for games and DLC) and `OverrideBundleId` (for bundles) match it to a
     specific Steam store page. They appear in the URL after `app/` and `sub/` respectively.
     
     If blank, the script will try to match the game automatically through the Steam API. That only
     works for games though, not DLC or bundles.

4. Open `scripts/build prize list.linq` in LINQPad.
5. Click the ▶ button to fetch info from the Steam API.

The fetched data will be saved to two files:
* `scripts/results.csv` is a CSV file that can be imported into Excel or Google Sheets;
* `config/games.json` is the data that'll be used by the web UI.

This will cache the fetched info. To fetch the latest info instead, delete the `scripts/cache`
folder.

## Configure web UI
You can edit `config/config.json` to change a few settings:

* `defaultCurrency` is the main currency for the prices in your prize list. If a prize uses a
  different currency, it'll be shown in the table (e.g. `89.00 HKD` for Hong Kong Dollars).
* `prizeGroups` groups prizes into sections based on the Steam retail price. The key is the display
  label, and the value is the minimum price. Each game uses the first matching group, and there
  should always be a final group with a price of `0` for any games that don't fit in another group.
* `claimed` is the app/bundle IDs which have already been claimed by a winner. Each ID will only
  remove one entry from the prize list (e.g. if two of the same game were claimed, list the ID
  twice).

You can edit `index.html` directly to change the intro text.

## Run web UI locally
The site is just HTML/CSS/JavaScript, so there's many ways to run it. Here is one though:

1. Install [Visual Studio Code](https://code.visualstudio.com).
2. Install the [Live Server](https://marketplace.visualstudio.com/items?itemName=ritwickdey.LiveServer)
   extension.
3. Open the `HatMouse` folder in Visual Studio Code.
4. Right-click `index.html` and choose _Open with Live Server_ to launch the site in your default
   browser.

# License
All content is covered by the [MIT license](LICENSE), with exceptions:

* `favicon.ico` is a Stardew Valley game sprite, so it's copyright ConcernedApe.
