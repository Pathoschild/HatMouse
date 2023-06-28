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
3. Replace `scripts/game keys.csv` with the list of game keys. It must have these fields (with a
   header row matching these exact names):

   field     | effect
   --------- | ------
   `Source`  | Who donated the game key. This is ignored by the code, it's just tracked so they can provide the key when a winner claims it.
   `Title`   | The game key name, ideally with text like `Steam Key` or `(Steam)` removed.
   `Ignored` | _(Optional)_ If `true`, don't show this game key (e.g. for games which no longer exist).
   `Claimed` | _(Optional)_ If `true`, the game key is struck out and marked claimed in the prize list.
   `AppId`<br />`BundleId` | _(Optional)_ Link the game key to a specific store page on Steam. This matches the number in the store page URL after `app/` (for `OverrideAppId`) or `sub/` (for `OverrideBundleId`). If both are omitted, the script will use the Steam API to find the matching game if possible (though that doesn't work for DLC or bundles).
   `Price`   | _(Optional)_ Show this USD price instead of the one fetched from the Steam API (e.g. for unlisted games).
   `Description` | _(Optional)_ Show this description instead of the one fetched from the Steam API. You can use HTML in this field (e.g. to link to multiple store pages).
   `StorePageUrl` | _(Optional)_ The full URL to the store page to link the game key to, for non-Steam games. The script will still try to match it to Steam to fill in the other info, so you should set the `Price` column to match the custom store too.
   `Comments` | Arbitrary comments ignored by the code (e.g. to explain why we're overriding fields).

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
