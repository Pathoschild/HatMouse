<Query Kind="Program">
  <NuGetReference>CsvHelper</NuGetReference>
  <NuGetReference>Newtonsoft.Json</NuGetReference>
  <NuGetReference>Pathoschild.Http.FluentClient</NuGetReference>
  <Namespace>Newtonsoft.Json</Namespace>
  <Namespace>Pathoschild.Http.Client</Namespace>
  <Namespace>System.Threading.Tasks</Namespace>
  <Namespace>System.Web</Namespace>
  <Namespace>CsvHelper</Namespace>
  <Namespace>System.Globalization</Namespace>
  <Namespace>Newtonsoft.Json.Linq</Namespace>
  <Namespace>Newtonsoft.Json.Serialization</Namespace>
</Query>

/*********
** Settings
*********/
/// <summary>The absolute path to the directory containing this script.</summary>
readonly static string ScriptDir = Path.GetDirectoryName(Util.CurrentQueryPath);

/// <summary>A CSV file containing the games to search. This should have two columns: the source (i.e. who can redeem the key) and the game title to search.</summary>
readonly static string SearchFile = Path.Combine(ScriptDir, "game keys.csv");

/// <summary>The folder in which to store cached data.</summary>
readonly static string CacheDirPath = Path.Combine(ScriptDir, "cache");

/// <summary>The absolute path to the directory containing the web files.</summary>
readonly static string WebDir = Path.Combine(ScriptDir, "..");

/// <summary>If a game has multiple package prices, ignore packages whose names contain any of these case-insensitive strings (unless that would remove all of them).</summary>
readonly static List<string> IgnoreSubPackageNames = new()
{
	"2-Pack",
	"3-Pack",
	"Digital Book",
	"Double Pack",
	"Expansion Pass",
	"Multiplayer Standalone",
	"Sights and Sounds",
	"Soundtrack",
	"Wallpaper"
};


/*********
** Script
*********/
async Task Main()
{
	// read list of raw files
	Console.WriteLine("Reading search file...");
	List<GameRecord> games = this.ReadSearchData().ToList();

	// init
	var client = new SteamApiClient();
	Directory.CreateDirectory(CacheDirPath);

	// fetch app IDs
	{
		var searchCache = await this.GetOrCreateCachedAsync("_search", () => Task.FromResult(new Dictionary<string, int?>()));
		var progress = new Util.ProgressBar("Fetching basic game info...", true).Dump();

		int fetched = 0;
		for (int i = 0; i < games.Count; i++)
		{
			var game = games[i];
			progress.Caption = $"Fetching basic info for game {i + 1} of {games.Count} ({game.SearchTitle})...";
			progress.Fraction = (i * 1d) / games.Count;

			// skip: explicitly marked no match
			if (game.AppId == -1)
			{
				game.AppId = 0;
				continue;
			}

			// skipped: explicity set
			if (game.AppId > 0 || game.BundleId > 0)
				continue;

			// fetch app ID
			if (!searchCache.TryGetValue(game.SearchTitle, out int? appId))
			{
				fetched++;
				searchCache[game.SearchTitle] = appId = await this.FetchAppIdAsync(client, game.SearchTitle);
				this.WriteCached("_search", searchCache);
			}
			game.AppId = appId ?? 0;

			// avoid rate limits
			if (fetched % 100 == 99)
			{
				progress.Caption += " | Pausing 5 seconds to avoid rate limits...";
				Thread.Sleep(5000);
			}
		}

		progress.Fraction = 1;
		Console.WriteLine($"Fetched basic game info for {games.Count} games.");
	}

	// fetch game info
	{
		var progress = new Util.ProgressBar("Fetching game details...", true).Dump();

		int fetched = 0;
		for (int i = 0; i < games.Count; i++)
		{
			var game = games[i];
			progress.Caption = $"Fetching details for game {i + 1} of {games.Count} ({game.SearchTitle})...";
			progress.Fraction = (i * 1d) / games.Count;

			if (game.AppId > 0)
			{
				JObject rawData = await this.GetOrCreateCachedAsync($"app-{game.AppId}", () =>
				{
					fetched++;
					return client.GetRawGameInfo(game.AppId);
				});

				if (rawData != null)
					client.PopulateGameInfo(game, rawData);
			}
			else if (game.BundleId > 0)
			{
				JObject rawData = await this.GetOrCreateCachedAsync($"bundle-{game.BundleId}", () =>
				{
					fetched++;
					return client.GetRawBundleInfo(game.BundleId);
				});

				if (rawData != null)
					client.PopulateBundleInfo(game, rawData);
			}

			if (fetched % 100 == 99)
			{
				progress.Caption += " | Pausing 5 seconds to avoid rate limits...";
				Thread.Sleep(5000);
			}
		}

		progress.Fraction = 1;
		Console.WriteLine($"Fetched details for {games.Count} games.");
	}

	// ignore free games
	for (int i = 0; i < games.Count; i++)
	{
		GameRecord game = games[i];
		if (game.IsFree)
		{
			Util.WordRun(false, "Ignored free game ", new Hyperlinq(game.GetUrl(), game.Title), $" for game key '{game.SearchTitle}'.").Dump();
			games.RemoveAt(i);
			i--;
		}
	}

	// build exports
	Console.WriteLine("Creating export files...");
	this.WriteCsvExportFile(games);
	this.WritePublicJsonExportFile(games);

	Console.WriteLine("Done!");
}


/*********
** Helper methods
*********/
/// <summary>Read the list of games to find from the <see cref="SearchFile"/> CSV file.</summary>
GameRecord[] ReadSearchData()
{
	if (!File.Exists(SearchFile))
		throw new InvalidOperationException($"Can't find the search data at {SearchFile}.");

	string rawCsv = File.ReadAllText(SearchFile, Encoding.UTF8);
	using StringReader reader = new(rawCsv);
	using CsvReader parser = new(reader, CultureInfo.InvariantCulture);

	return parser
		.GetRecords<CsvGameRecord>()
		.Select(row =>
		{
			if (row.Ignored ?? false)
			{
				Console.WriteLine($"Ignored disabled game key '{row.Title}' (comments: '{row.Comments}').");
				return null;
			}
			
			return new GameRecord
			{
				Source = row.Source.Trim(),
				SearchTitle = row.Title.Trim(),
				AppId = row.AppId ?? 0,
				BundleId = row.BundleId ?? 0,
				OverridePrice = !string.IsNullOrWhiteSpace(row.Price) ? row.Price.Trim().Trim('$') : null,
				OverrideDescription = !string.IsNullOrWhiteSpace(row.Description) ? row.Description.Trim() : null,
				Claimed = row.Claimed ?? false
			};
		})
		.Where(p => p is not null)
		.OrderBy(p => p.SearchTitle, StringComparer.OrdinalIgnoreCase)
		.ToArray();
}

/// <summary>Save the fetched game info to the CSV export file for the internal spreadsheet.</summary>
/// <param name="game">The game data to save.</param>
void WriteCsvExportFile(IList<GameRecord> games)
{
	string exportPath = Path.Combine(ScriptDir, "results.csv");
	using FileStream fileStream = File.OpenWrite(exportPath);
	using StreamWriter streamWriter = new StreamWriter(fileStream, Encoding.UTF8);
	using CsvWriter writer = new(streamWriter, CultureInfo.InvariantCulture);
	writer.WriteRecords(games);
}

/// <summary>Save the fetched game info to the JSON export file for the public prize page.</summary>
/// <param name="game">The game data to save.</param>
void WritePublicJsonExportFile(IList<GameRecord> games)
{
	// get export models
	List<PublicGameRecord> exportGames = games
		.Select(game => new PublicGameRecord(game))
		.ToList();

	// group duplicates
	{
		Dictionary<(int, bool), PublicGameRecord> byAppId = new();
		Dictionary<(int, bool), PublicGameRecord> byBundleId = new();

		for (int i = 0; i < exportGames.Count; i++)
		{
			var game = exportGames[i];
			if (!(game.AppId > 0 || game.BundleId > 0))
				continue;
			
			var lookup = game.AppId > 0 ? byAppId : byBundleId;
			int id = game.AppId > 0 ? game.AppId.Value : game.BundleId.Value;

			var key = (id, game.Claimed ?? false);
			if (lookup.TryGetValue(key, out PublicGameRecord mainRecord))
			{
				mainRecord.Keys.AddRange(game.Keys);
				exportGames.RemoveAt(i);
				i--;
			}
			else
				lookup[key] = game;
		}
	}

	// save to file
	JsonSerializerSettings settings = new()
	{
		Formatting = Newtonsoft.Json.Formatting.Indented,
		NullValueHandling = NullValueHandling.Ignore,
		ContractResolver = new DefaultContractResolver
		{
			NamingStrategy = new CamelCaseNamingStrategy()
		}
	};

	File.WriteAllText(
		Path.Combine(WebDir, "config", "games.json"),
		JsonConvert.SerializeObject(exportGames, settings)
	);
}

/// <summary>Fetch the app ID for a game title.</summary>
/// <param name="client">The Steam API client.</param>
/// <param name="title">The game title to search for.</param>
async Task<int?> FetchAppIdAsync(SteamApiClient client, string title)
{
	// find exact match
	SteamSearchResult[] results = await client.SearchAsync(title);
	string searchedTitle = title;

	// try match without "Steam key" or equivalent
	if (results.Length == 0)
	{
		string newTitle = Regex.Replace(title, @"[\s\(]*(?:Steam Edition|Steam Keys?|Steam)[\s\)]*", " ", RegexOptions.IgnoreCase).Trim();
		if (newTitle != title)
		{
			results = await client.SearchAsync(newTitle);
			if (results.Length > 0)
				searchedTitle = newTitle;
		}
	}

	// choose best match
	switch (results.Length)
	{
		case 0:
			return null;

		case 1:
			return results[0].AppId;

		default:
			// choose exact match if possible
			// For example, searching "Tomb Raider" will return "Rise of the Tomb Raider", "Tomb Raider", etc
			foreach (var result in results)
			{
				if (string.Equals(result.Title, title, StringComparison.OrdinalIgnoreCase) || string.Equals(result.Title, searchedTitle, StringComparison.OrdinalIgnoreCase))
					return result.AppId;
			}

			// otherwise ask user
			Console.WriteLine($"Steam returned multiple matches for the search '{title}':");
			for (int i = 0; i < results.Length; i++)
			{
				var entry = results[i];
				new Hyperlinq($"https://store.steampowered.com/app/{entry.AppId}", $"[{i + 1}] {entry.Title}").Dump();
			}
			Console.WriteLine($"[{results.Length + 1}] none of these");

			while (true)
			{
				int choice = Util.ReadLine<int>("Please choose which game to match:", 1, Enumerable.Range(1, results.Length));
				if (choice == results.Length + 1)
					return null;
				if (choice > 0 && choice <= results.Length)
					return results[choice - 1].AppId;
			}
	}
}

/// <summary>Get a cached file from the filesystem, or fetch the data and create it.</summary>
/// <param name="key">The cache key.</param>
/// <param name="fetch">Get the data if it's not cached yet.</param>
async Task<T> GetOrCreateCachedAsync<T>(string key, Func<Task<T>> fetch)
{
	string filePath = Path.Combine(CacheDirPath, key + ".json");

	if (File.Exists(filePath))
	{
		return JsonConvert.DeserializeObject<T>(
			File.ReadAllText(filePath)
		);
	}

	T data = await fetch();
	this.WriteCached(key, data);

	return data;
}

/// <summary>Write data to the cache directory.</summary>
/// <param name="key">The cache key.</param>
/// <param name="data">The data to cache.</param>
async void WriteCached<T>(string key, T data)
{
	string filePath = Path.Combine(CacheDirPath, key + ".json");

	File.WriteAllText(
		filePath,
		JsonConvert.SerializeObject(data, Newtonsoft.Json.Formatting.Indented)
	);
}

/// <summary>An API client for fetching game info from the Steam API.</summary>
public class SteamApiClient : FluentClient
{
	/// <summary>Fetch app IDs matching a game title from the Steam API, if found.</summary>
	/// <param name="searchName">The game title to search for.</param>
	public async Task<SteamSearchResult[]> SearchAsync(string searchName)
	{
		string encodedName = HttpUtility.UrlEncode(searchName).Replace("+", "%20");
		IResponse response = await this.RateLimit(async () => await this.GetAsync("https://steamcommunity.com/actions/SearchApps/" + encodedName));
		var json = await response.AsRawJsonArray();

		return json
			.Select(raw => new SteamSearchResult(raw.Value<int>("appid"), raw.Value<string>("name")))
			.ToArray();
	}

	/// <summary>Fetch raw game info from the Steam API.</summary>
	/// <param name="appId">The game app ID to fetch.</param>
	public async Task<JObject> GetRawGameInfo(int appId)
	{
		if (appId == 0)
			return null;

		IResponse response = await this.RateLimit(async () => await this.GetAsync("https://store.steampowered.com/api/appdetails").WithArguments(new { appids = appId, cc = "usd", l = "en" }));
		JObject json = await response.AsRawJsonObject();
		if (!json.TryGetValue(appId.ToString(), out JToken wrappedData) || !wrappedData.Value<bool>("success"))
			return null;

		return wrappedData.Value<JObject>("data");
	}

	/// <summary>Fetch raw bundle info from the Steam API.</summary>
	/// <param name="appId">The game bundle ID to fetch.</param>
	public async Task<JObject> GetRawBundleInfo(int bundleId)
	{
		if (bundleId == 0)
			return null;

		IResponse response = await this.RateLimit(async () => await this.GetAsync("https://store.steampowered.com/api/packagedetails").WithArguments(new { packageids = bundleId }));
		JObject json = await response.AsRawJsonObject();
		if (!json.TryGetValue(bundleId.ToString(), out JToken wrappedData) || !wrappedData.Value<bool>("success"))
			return null;

		return wrappedData.Value<JObject>("data");
	}

	/// <summary>Populate a game record from the raw data returned by <see cref="GetRawGameInfo" />.</summary>
	/// <param name="game">The game record to populate.</param>
	/// <param name="data">The raw game data returned by <see cref="GetRawGameData" />.</param>
	public bool PopulateGameInfo(GameRecord game, JObject data)
	{
		if (game.AppId == 0)
			return false;

		game.Title = HttpUtility.HtmlDecode(data.Value<string>("name"));
		game.CapsuleImage = data.Value<string>("capsule_imagev5");

		game.Type = data.Value<string>("type");
		game.Description = HttpUtility.HtmlDecode(data.Value<string>("short_description"));
		game.Languages = HttpUtility.HtmlDecode(data.Value<string>("supported_languages"));

		game.IsFree = data.Value<bool>("is_free");
		if (!game.IsFree && game.OverridePrice is null && this.TryInteractivelyGetGamePrice(data, game.Title, game.GetUrl(), out decimal price, out string currency))
		{
			game.Price = price.ToString("0.00");
			game.PriceCurrency = currency;
		}

		var platform = data.Value<JToken>("platforms");
		game.IsLinux = platform.Value<bool>("linux");
		game.IsMac = platform.Value<bool>("mac");
		game.IsWindows = platform.Value<bool>("windows");

		var metacritic = data.Value<JToken>("metacritic");
		if (metacritic != null)
		{
			game.MetaCriticScore = metacritic.Value<int>("score");
			game.MetaCriticUrl = metacritic.Value<string>("url");
		}

		var categories = data.Value<JArray>("categories");
		if (categories != null)
			game.Categories = string.Join(", ", categories.Select(p => p.Value<string>("description")));

		var genres = data.Value<JArray>("genres");
		if (genres != null)
			game.Genres = string.Join(", ", genres.Select(p => p.Value<string>("description")));

		game.ReleaseDate = data.Value<JToken>("release_date")?.Value<string>("date");
		game.ContentWarnings = data.Value<JObject>("content_descriptors")?.Value<JArray>("ids").Values<int>().ToArray();

		return true;
	}

	/// <summary>Get the price of a game from the Steam API data, interactively asking the user if there are multiple prices and we can't auto-select one.</summary>
	/// <param name="data">The raw game data returned by <see cref="GetRawGameData" />.</param>
	/// <param name="title">The game's title from the Steam API.</param>
	/// <param name="url">The game's store page URL.</param>
	/// <param name="price">The game price, if found.</param>
	/// <param name="currency">The price currency, if found.</param>
	/// <returns>Returns whether a price was successfully read from the game data.</returns>
	public bool TryInteractivelyGetGamePrice(JObject data, string title, string url, out decimal price, out string currency)
	{
		List<(decimal Price, string Currency, string Label)> prices = new();

		// default price (not necessarily for the base game if it has package prices too)
		decimal? defaultPrice = null;
		string defaultCurrency = "USD";
		{
			JToken priceOverview = data.Value<JToken>("price_overview");
			if (priceOverview != null)
			{
				defaultPrice = priceOverview.Value<decimal>("initial") / 100;
				defaultCurrency = priceOverview.Value<string>("currency");
				prices.Add((defaultPrice.Value, defaultCurrency, "default price"));
			}
		}

		// package groups (e.g. "Base Game", "Deluxe Edition", etc)
		{
			JArray packageGroups = data.Value<JArray>("package_groups");
			if (packageGroups != null)
			{
				foreach (JToken packageGroup in packageGroups)
				{
					if (packageGroup.Value<bool?>("is_recurring_subscription") is true)
						continue;

					string packageTitle = packageGroup.Value<string>("title");

					foreach (JObject sub in packageGroup.Value<JArray>("subs"))
					{
						string subLabel = sub.Value<string>("option_text");
						if (packageTitle != null)
							subLabel = $"{packageTitle}: {subLabel}";

						string percentDiscount = sub.Value<string>("percent_savings_text");

						// extract original price from package label (since it's not saved separately), else show discounted price with info in label
						decimal curPrice;
						Match discountedLabelMatch = Regex.Match(subLabel, @"^(.+) - (?:<span class=""discount_original_price"">\$([\d\.]+)</span> )?\$([\d\.]+)$");
						if (discountedLabelMatch.Success)
						{
							subLabel = discountedLabelMatch.Groups[1].Value;
							curPrice = discountedLabelMatch.Groups[2].Success
								? decimal.Parse(discountedLabelMatch.Groups[2].Value)  // <s>original price</s> discounted price
								: decimal.Parse(discountedLabelMatch.Groups[3].Value); // original price
						}
						else
						{
							curPrice = sub.Value<decimal>("price_in_cents_with_discount");

							if (!string.IsNullOrWhiteSpace(percentDiscount))
								subLabel += $" - {percentDiscount} off";
						}

						prices.Add((curPrice, defaultCurrency, $"package: {subLabel}"));
					}
				}
			}
		}

		// filter out soundtracks, etc
		if (prices.Count > 1)
		{
			var filteredPrices = prices.ToList();
			for (int i = filteredPrices.Count - 1; i >= 0; i--)
			{
				var match = filteredPrices[i];
				foreach (string ignoreText in IgnoreSubPackageNames)
				{
					if (match.Label.Contains(ignoreText, StringComparison.OrdinalIgnoreCase))
					{
						filteredPrices.RemoveAt(i);
						break;
					}
				}
			}

			if (filteredPrices.Count > 0 && filteredPrices.Count < prices.Count)
				prices = filteredPrices;
		}

		// select best price
		switch (prices.Count)
		{
			case 0:
				price = 0;
				currency = null;
				return false;

			case 1:
				price = prices[0].Price;
				currency = prices[0].Currency;
				return true;

			default:
				// We can auto-select a price in two cases:
				//   - all prices are the same;
				//   - or all package prices are higher than the default price (since the default price should always include the base game, so they're likely special editions)
				if (defaultPrice != null)
				{
					bool anyLower = false;
					foreach (var match in prices)
					{
						if (match.Price < defaultPrice && match.Currency == defaultCurrency)
						{
							anyLower = true;
							break;
						}
					}

					if (!anyLower)
					{
						price = defaultPrice.Value;
						currency = defaultCurrency;
						return true;
					}
				}

				// else choose a price interactively
				{
					const string indent = "    ";
					Util.WordRun(false, indent, new Hyperlinq(url, title), " has multiple prices available:").Dump();
					for (int i = 0; i < prices.Count; i++)
					{
						var priceMatch = prices[i];
						$"{indent}  • [{i + 1}] {priceMatch.Currency} {priceMatch.Price} — {priceMatch.Label}".Dump();
					}
					Console.WriteLine($"{indent}Or enter the USD price to use (in the form 00.00)\n");

					while (true)
					{
						string input = Util.ReadLine("Select an option or enter a USD price:", "1");

						if (input.Contains('.'))
						{
							if (decimal.TryParse(input, out decimal curPrice))
							{
								price = curPrice;
								currency = "USD";
								return true;
							}
						}
						else if (int.TryParse(input, out int option))
						{
							if (option > 0 && option <= prices.Count)
							{
								var match = prices[option - 1];
								price = match.Price;
								currency = match.Currency;
								return true;
							}
						}
					}
				}
		}
	}

	/// <summary>Populate a game record from the raw data returned by <see cref="GetRawBundleInfo" />.</summary>
	/// <param name="game">The game record to populate.</param>
	/// <param name="data">The raw bundle data returned by <see cref="GetRawBundleInfo" />.</param>
	public bool PopulateBundleInfo(GameRecord game, JObject data)
	{
		if (game.BundleId == 0)
			return false;

		game.Title = data.Value<string>("name");
		game.CapsuleImage = data.Value<string>("small_logo");

		game.Type = "bundle";

		var prices = data.Value<JToken>("price");
		if (prices != null)
		{
			decimal price = prices.Value<decimal>("initial");

			game.PriceCurrency = prices.Value<string>("currency");
			game.Price = (price / 100).ToString("0.00");
		}

		var platform = data.Value<JToken>("platforms");
		game.IsLinux = platform.Value<bool>("linux");
		game.IsMac = platform.Value<bool>("mac");
		game.IsWindows = platform.Value<bool>("windows");

		game.ReleaseDate = data.Value<JToken>("release_date")?.Value<string>("date");

		return true;
	}

	/// <summary>Fetch a response from the Steam API, and handle any rate limit errors by pausing and retrying.</summary>
	/// <param name="fetch">Fetch a response from the API.</param>
	private async Task<T> RateLimit<T>(Func<Task<T>> fetch)
	{
		while (true)
		{
			try
			{
				return await fetch();
			}
			catch (ApiException ex) when (ex.Status == System.Net.HttpStatusCode.TooManyRequests)
			{
				Console.WriteLine("Hit API rate limit, pausing for 10 seconds...");
				Thread.Sleep(10_000);
			}
		}
	}
}

public record SteamSearchResult(int AppId, string Title);

public class CsvGameRecord
{
	// game key
	public string Source { get; set; }
	public string Title { get; set; }
	public bool? Ignored { get; set; }
	public bool? Claimed { get; set; }

	// override game info
	public int? AppId { get; set; }
	public int? BundleId { get; set; }
	public string Price { get; set; }
	public string Description { get; set; }

	// comments
	public string Comments { get; set; }
}

public class GameRecord
{
	public string Source { get; set; }
	public string SearchTitle { get; set; }

	public int AppId { get; set; }
	public int BundleId { get; set; }

	public string Title { get; set; }
	public string CapsuleImage { get; set; }

	public string Type { get; set; }
	public string Description { get; set; }
	public string OverrideDescription { get; set; }

	public string Languages { get; set; }

	public bool IsFree { get; set; }
	public string PriceCurrency { get; set; }
	public string Price { get; set; }
	public string OverridePrice { get; set; }

	public bool IsLinux { get; set; }
	public bool IsMac { get; set; }
	public bool IsWindows { get; set; }
	public int MetaCriticScore { get; set; }
	public string MetaCriticUrl { get; set; }
	public string Categories { get; set; }
	public string Genres { get; set; }
	public string ReleaseDate { get; set; }
	public int[] ContentWarnings { get; set; }

	public bool Claimed { get; set; }

	/// <summary>Get the URL to the game's Steam store page, if any.</summary>
	public string GetUrl()
	{
		if (this.AppId > 0)
			return "https://store.steampowered.com/app/" + this.AppId;

		if (this.BundleId > 0)
			return "https://store.steampowered.com/sub/" + this.BundleId;

		return null;
	}
}

public class PublicGameRecord
{
	public List<string> Keys { get; }
	public int? AppId { get; }
	public int? BundleId { get; }
	public string Image { get; }
	public string Type { get; }
	public string Description { get; }
	public string OverrideDescription { get; }
	public string PriceCurrency { get; }
	public string Price { get; }
	public int MetaCriticScore { get; }
	public string MetaCriticUrl { get; }

	public string[] Platforms { get; }
	public string[] Categories { get; }
	public string[] Genres { get; }

	public string ReleaseDate { get; }
	public int[] ContentWarnings { get; }

	public bool? Claimed { get; set; }

	public PublicGameRecord(GameRecord game)
	{
		this.Keys = new List<string> { game.SearchTitle };
		this.AppId = game.AppId > 0 ? game.AppId : null;
		this.BundleId = game.BundleId > 0 ? game.BundleId : null;
		this.Image = game.CapsuleImage;
		this.Type = game.Type;
		this.Description = game.Description;
		this.OverrideDescription = game.OverrideDescription;

		if (game.OverridePrice != null)
		{
			this.Price = game.OverridePrice;
			this.PriceCurrency = "USD";
		}
		else
		{
			this.Price = game.Price;
			this.PriceCurrency = game.PriceCurrency;
		}

		this.MetaCriticScore = game.MetaCriticScore;
		this.MetaCriticUrl = this.GetMetaCriticUrl(game.MetaCriticUrl);
		this.Platforms = this.GetPlatforms(game);
		this.Categories = game.Categories?.Split(',', StringSplitOptions.TrimEntries) ?? Array.Empty<string>();
		this.Genres = game.Genres?.Split(',', StringSplitOptions.TrimEntries) ?? Array.Empty<string>();
		this.ReleaseDate = this.GetReleaseYear(game.ReleaseDate);
		this.ContentWarnings = game.ContentWarnings?.Length > 0 ? game.ContentWarnings : null;

		this.Claimed = game.Claimed
			? true
			: null;

		if (this.Type == "dlc")
			this.Type = "DLC";
	}

	/// <summary>Normalize a MetaCritical review page URL.</summary>
	/// <param name="url">The raw URL from the Steam API.</param>
	public string GetMetaCriticUrl(string url)
	{
		if (string.IsNullOrWhiteSpace(url))
			return null;

		int queryIndex = url.IndexOf('?');
		if (queryIndex > 0)
			url = url.Substring(0, queryIndex); // remove the unneeded ?ftag= tracking argument

		return url;
	}

	/// <summary>Get the list of OSes supported by this game.</summary>
	/// <param name="game">The game data.</param>
	private string[] GetPlatforms(GameRecord game)
	{
		List<string> platforms = new();

		if (game.IsLinux)
			platforms.Add("Linux");
		if (game.IsMac)
			platforms.Add("macOS");
		if (game.IsWindows)
			platforms.Add("Windows");

		return platforms.ToArray();
	}

	/// <summary>Extract the release year from a raw date string returned by the Steam API.</summary>
	/// <param name="releaseDate">The raw date string to parse.</param>
	private string GetReleaseYear(string releaseDate)
	{
		// empty date
		if (string.IsNullOrWhiteSpace(releaseDate))
			return null;
		if (releaseDate is "Coming soon")
			return releaseDate;

		// English date
		if (DateTime.TryParse(releaseDate, out DateTime date))
			return date.Year.ToString();

		// localized date
		Match match = Regex.Match(releaseDate, @"^\d+ [a-z]+ (\d{4})$", RegexOptions.IgnoreCase);
		if (match.Success)
			return match.Groups[1].Value;

		// invalid
		Console.WriteLine($"Invalid release date: {releaseDate}");
		return releaseDate;
	}
}
