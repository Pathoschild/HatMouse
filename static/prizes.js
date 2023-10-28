let app;

const prizeList = async function() {
    const timestamp = Math.round(new Date().getTime() / 60000); // minutes since epoch

    // init data
    const data = {
        config: await $.getJSON("config/config.json", { r: timestamp }),
        games: await $.getJSON("config/games.json", { r: timestamp }),
        showLogos: false,
        showGamesWithContentWarnings: false,
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
            game.visible = true;
            game.searchableText = [game.title, game.type, game.description, ...game.categories, ...game.genres, game.releaseDate].join(" ").toLowerCase();
            game.truncatedDescription = game.description?.length > maxDescriptionLen
                ? game.description.slice(0, maxDescriptionLen) + "…"
                : null;

            // parse content warnings
            if (game.contentWarnings?.length)
            {
                const warnings = [];
                const lookup = Object.fromEntries(game.contentWarnings.map(id => [id, true]));

                // adult content (only show one)
                if (lookup[3]) {
                    warnings.push("adult-only sexual content");
                    delete lookup[3];
                    delete lookup[4];
                    delete lookup[1];
                }
                else if (lookup[4]) {
                    warnings.push("frequent nudity or sexual content");
                    delete lookup[4];
                    delete lookup[1];
                }
                else if (lookup[1]) {
                    warnings.push("some nudity or sexual content");
                    delete lookup[1];
                }

                // other
                if (lookup[2]) {
                    warnings.push("frequent violence or gore");
                    delete lookup[2];
                }
                if (lookup[5]) {
                    warnings.push("general mature content");
                    delete lookup[5];
                }
                for (const id of Object.keys(lookup))
                    warnings.push ("unknown (" + id + ")");

                game.contentWarningText = warnings.join(", ") + ".";
                game.contentWarningText = game.contentWarningText[0].toUpperCase() + game.contentWarningText.slice(1);
            }
            else
                game.contentWarningText = "";

            // set URL
            if (game.overrideStorePageUrl)
                game.url = game.overrideStorePageUrl;
            else if (game.appId)
                game.url = "https://store.steampowered.com/app/" + game.appId;
            else if (game.bundleId)
                game.url = "https://store.steampowered.com/sub/" + game.bundleId;

            // set prize group
            for (const prizeGroup of data.config.prizeGroups)
            {
                const minPrice = prizeGroup.minPrice;
                if (!minPrice || game.price >= minPrice)
                {
                    game.prizeGroup = prizeGroup.key;
                    break;
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
        computed: {
            gamesByPrizeGroup() {
                const groups = [];

                for (const prizeGroup of data.config.prizeGroups) {
                    const allGames = data.games.filter(game => game.prizeGroup == prizeGroup.key);
                    const visibleGames = allGames.filter(game => game.visible);

                    groups.push({
                        groupName: prizeGroup.key,
                        groupVisible: prizeGroup.visible,

                        games: visibleGames,
                        countVisible: visibleGames.length,
                        countHidden: allGames.length - visibleGames.length
                    });
                }

                return groups;
            }
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

                // check content filter
                if (!data.showGamesWithContentWarnings && game.contentWarnings)
                    return false;

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
