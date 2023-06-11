let app;

const prizeList = async function() {
    // init data
    const data = {
        config: await $.getJSON("config/config.json?r=3"),
        games: await $.getJSON("config/games.json?r=3"),
        showLogos: false,
        showMature: false,
        filters: {
            platforms: {
                value: {
                    linux: { value: true, label: "Linux" },
                    macOS: { value: true },
                    windows: { value: true, label: "Windows" }
                }
            }
        },
        search: ""
    };

    // init filters
    Object.entries(data.filters).forEach(([groupKey, filterGroup]) => {
        filterGroup.label = filterGroup.label || groupKey;
        Object.entries(filterGroup.value).forEach(([filterKey, filter]) => {
            filter.id = ("filter_" + groupKey + "_" + filterKey).replace(/[^a-zA-Z0-9]/g, "_");
            filter.label = filter.label || filterKey;
        });
    });

    // split game entries per key
    {
        const splitGames = [];
        for (let game of data.games)
        {
            if (game.keys.length == 1)
            {
                game.title = game.keys[0];
                game.keys = null;
                splitGames.push(game);
            }
            else {
                for (let i = 0; i < game.keys.length; i++) {
                    const clone = { ...game };
                    clone.title = game.keys[i];
                    clone.keys = null;
                    splitGames.push(clone);
                }
            }
        }
        data.games = splitGames;
    }

    // init games
    {
        const slugsUsed = {};
        const maxDescriptionLen = 150;
        for (let i = 0; i < data.games.length; i++) {
            const game = data.games[i];

            // set slug
            game.slug = game.title.toLowerCase().replace(/[^a-z0-9]+/g, '');
            if (slugsUsed[game.slug])
                game.slug += "_" + (++slugsUsed[game.slug]);
            else
                slugsUsed[game.slug] = 1;

            // set display info
            game.visible = data.showMature || !game.contentWarnings;
            game.searchableText = [game.title, game.type, game.description, ...game.categories, ...game.genres, game.releaseDate].join(" ").toLowerCase();
            game.truncatedDescription = game.description?.length > maxDescriptionLen
                ? game.description.slice(0, maxDescriptionLen) + "…"
                : null;

            // set URL
            if (game.appId)
                game.url = "https://store.steampowered.com/app/" + game.appId;
            else if (game.bundleId)
                game.url = "https://store.steampowered.com/sub/" + game.bundleId;

            // set prize group
            for (let key of Object.keys(data.config.prizeGroups))
            {
                const minPrice = data.config.prizeGroups[key];
                if (!minPrice || game.price >= minPrice)
                {
                    game.prizeGroup = key;
                    break;
                }
            }

            // mark claimed if applicable
            game.claimed = false;
            if (game.appId > 0 || game.bundleId > 0) {
                const list = game.appId > 0
                    ? data.config.claimed.appIds
                    : data.config.claimed.bundleIds;
                const id = game.appId > 0
                    ? game.appId
                    : game.bundleId;

                const index = list.indexOf(id);
                if (index > -1) {
                    game.claimed = true;
                    list.splice(index, 1);
                }
            }
        }
    }

    // sort games by prize group, then by title
    data.games = data.games.sort(function (a, b) {
        if (a.prizeGroup != b.prizeGroup)
            return a.prizeGroup.localeCompare(b.prizeGroup);

        return a.title.localeCompare(b.title);
    });

    // init app
    app = new Vue({
        el: "#app",
        data: data,
        mounted: function () {
            // enable table sorting
            $(".game-list").tablesorter({
                cssHeader: "header",
                cssAsc: "headerSortUp",
                cssDesc: "headerSortDown"
            });

            // put focus in textbox for quick search
            if (!location.hash)
                $("#search-box").focus();

            // jump to anchor (since table is added after page load)
            this.fixHashPosition();
        },
        methods: {
            /**
             * Update the visibility of all games based on the current search text and filters.
             */
            applyFilters: function () {
                // get search terms
                const words = data.search.toLowerCase().split(" ");

                // apply criteria
                for (let i = 0; i < data.games.length; i++) {
                    const game = data.games[i];
                    game.visible = true;

                    // check filters
                    game.visible = this.matchesFilters(game, words);
                }
            },

            /**
             * Fix the window position for the current hash.
             */
            fixHashPosition: function () {
                if (!location.hash)
                    return;

                const row = $(location.hash);
                const target = row.prev().get(0) || row.get(0);
                if (target)
                    target.scrollIntoView();
            },

            /**
             * Get whether a game matches the current filters.
             * @param {object} game The game to check.
             * @param {string[]} searchWords The search words to match.
             * @returns {bool} Whether the game matches the filters.
             */
            matchesFilters: function (game, searchWords) {
                const filters = data.filters;

                // check hash
                if (location.hash === "#" + game.slug)
                    return true;

                // check platform
                const hasValidPlatform =
                    !game.platforms.length
                    || (filters.platforms.value.linux.value && game.platforms.includes("Linux"))
                    || (filters.platforms.value.macOS.value && game.platforms.includes("macOS"))
                    || (filters.platforms.value.windows.value && game.platforms.includes("Windows"));
                if (!hasValidPlatform)
                    return false;

                // check search terms
                for (let w = 0; w < searchWords.length; w++) {
                    if (game.searchableText.indexOf(searchWords[w]) === -1)
                        return false;
                }

                return true;
            }
        }
    });
    app.applyFilters();
    app.fixHashPosition();
    window.addEventListener("hashchange", function() {
        app.applyFilters();
        app.fixHashPosition();
    });
};
