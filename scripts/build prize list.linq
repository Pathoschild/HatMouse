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
			if (row.IgnoreKey ?? false)
			{
				Console.WriteLine($"Ignored disabled game key '{row.Title}' (comments: '{row.Comments}').");
				return null;
			}
			
			return new GameRecord
			{
				Source = row.Source.Trim(),
				SearchTitle = row.Title.Trim(),
				AppId = row.OverrideAppId ?? 0,
				BundleId = row.OverrideBundleId ?? 0,
				OverridePrice = !string.IsNullOrWhiteSpace(row.OverridePrice) ? row.OverridePrice.Trim().Trim('$') : null,
				OverrideDescription = !string.IsNullOrWhiteSpace(row.OverrideDescription) ? row.OverrideDescription.Trim() : null
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
		Dictionary<int, PublicGameRecord> byAppId = new();
		Dictionary<int, PublicGameRecord> byBundleId = new();

		for (int i = 0; i < exportGames.Count; i++)
		{
			var game = exportGames[i];
			if (!(game.AppId > 0 || game.BundleId > 0))
				continue;
			
			var lookup = game.AppId > 0 ? byAppId : byBundleId;
			int id = game.AppId > 0 ? game.AppId.Value : game.BundleId.Value;

			if (lookup.TryGetValue(id, out PublicGameRecord mainRecord))
			{
				mainRecord.Keys.AddRange(game.Keys);
				exportGames.RemoveAt(i);
				i--;
			}
			else
				lookup[id] = game;
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

		var prices = data.Value<JToken>("price_overview");
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
		game.ContentWarnings = HttpUtility.HtmlDecode(data.Value<JToken>("content_descriptors")?.Value<string>("notes"));

		return true;
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
	public string Source { get; set; }
	public string Title { get; set; }
	public int? OverrideAppId { get; set; }
	public int? OverrideBundleId { get; set; }
	public string OverridePrice { get; set; }
	public string OverrideDescription { get; set; }
	public bool? IgnoreKey { get; set; }
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
	public string ContentWarnings { get; set; }

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
	public string ContentWarnings { get; }

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
		this.ContentWarnings = game.ContentWarnings;

		if (this.Type == "dlc")
			this.Type = "DLC";
	}

	/// <summary>Get the URL to the game's Steam store page.</summary>
	/// <param name="game">The game data.</param>
	private string GetUrl(GameRecord game)
	{
		if (game.AppId > 0)
			return $"https://store.steampowered.com/app/{game.AppId}";

		if (game.BundleId > 0)
			return $"https://store.steampowered.com/sub/{game.BundleId}";

		return null;
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
