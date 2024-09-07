#define OSM
using CIFPReader;

using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using WSleeman.Osm;

using static SectorGenerator.Helpers;

namespace SectorGenerator;

public class Program
{
	public static async Task Main()
	{
		Config config = File.Exists("config.json") ? JsonSerializer.Deserialize<Config>(File.ReadAllText("config.json")) : Config.Default;

		Console.Write("Getting IVAO API token..."); await Console.Out.FlushAsync();
		(string apiToken, string apiRefreshToken) = await GetApiKeysAsync(config);
		Console.WriteLine($" Done! (Refresh: {apiRefreshToken})");

		// Long loading threads in parallel!
		Console.Write("Downloading ARTCC boundaries, CIFPs, and OSM data..."); await Console.Out.FlushAsync();
		CIFP? cifp = null;
#if OSM
		Osm? osm = null;
#endif
		Dictionary<string, (double Latitude, double Longitude)[]> artccBoundaries = [];
		Dictionary<string, string[]> artccNeighbours = [];
		string[] faaArtccs = [];

		for (int iterations = 0; iterations < 3; ++iterations)
		{
			try
			{
				await Task.WhenAll([
					Task.Run(async () => (artccBoundaries, artccNeighbours, faaArtccs) = await ArtccBoundaries.GetBoundariesAsync(config.BoundaryFilePath)),
					Task.Run(() => cifp ??= CIFP.Load()),
#if OSM
					Task.Run(async () => osm ??= await Osm.Load())
#endif
				]);
				break;
			}
			catch (TimeoutException) { /* Sometimes things choke. */ }
			catch (TaskCanceledException) { /* Sometimes things choke. */ }
		}

#if OSM
		// Keep the compiler happy with fallback checks.
		if (cifp is null || osm is null)
		{
			Console.WriteLine(" FAILED!");
			return;
		}
		Console.WriteLine(" Done!");
#endif

		// Generate copy-pasteable Webeye shapes for each of the ARTCCs.
		(string Artcc, string Shape)[] artccWebeyeShapes = [..
			artccBoundaries.Select(b => (
				b.Key,
				string.Join("\r\n", b.Value.Reverse().Select(p => $"{p.Latitude:00.0####}:{(p.Longitude > 0 ? p.Longitude - 360 : p.Longitude):000.0####}").ToArray())
			))
		];

		if (!Directory.Exists("webeye"))
			Directory.CreateDirectory("webeye");

		foreach (var (artcc, shape) in artccWebeyeShapes)
			File.WriteAllText(Path.ChangeExtension(Path.Join("webeye", artcc), "txt"), shape);

		Console.Write("Allocating airports to centers..."); await Console.Out.FlushAsync();
		Dictionary<string, HashSet<Aerodrome>> centerAirports = [];

		foreach (var (artcc, points) in artccBoundaries)
			centerAirports.Add(artcc, [..
				cifp.Aerodromes.Values.Where(a => IsInPolygon(points, ((double)a.Location.Latitude, (double)a.Location.Longitude)))
					.Concat(config.SectorAdditionalAirports.TryGetValue(artcc, out var addtl) ? addtl.Select(a => cifp.Aerodromes[a]) : [])
			]);

		Console.WriteLine(" Done!");

		Console.Write("Allocating runways to centers..."); await Console.Out.FlushAsync();
		Dictionary<string, HashSet<(string Airport, HashSet<Runway> Runways)>> centerRunways = [];

		foreach (var (artcc, points) in artccBoundaries)
			centerRunways.Add(artcc, [.. cifp.Runways.Where(kvp => centerAirports[artcc].Select(ad => ad.Identifier).Contains(kvp.Key)).Select(kvp => (kvp.Key, kvp.Value))]);

		Console.WriteLine(" Done!");

		Console.Write("Getting ATC positions..."); await Console.Out.FlushAsync();
		var atcPositions = await GetAtcPositionsAsync(apiToken, "K", "TJ", "PH", "PA");
		Dictionary<string, JsonObject[]> positionArtccs = atcPositions.GroupBy(p =>
		{
			string facility = p["composePosition"]!.GetValue<string>().Split("_")[0];

			if (facility.StartsWith("KZ"))
				return facility[1..];
			else if (TraconCenters.TryGetValue(facility, out string? artcc))
				return artcc;
			else if (!centerAirports.Any(kvp => kvp.Value.Any(ad => ad.Identifier == facility)))
			{
				if ((p["airportId"] ?? p["centerId"])?.GetValue<string>() is string pos && centerAirports.Any(kvp => kvp.Value.Any(ad => ad.Identifier == pos)))
					return centerAirports.First(kvp => kvp.Value.Any(ad => ad.Identifier == pos)).Key;

				return facility[..2] switch {
					"TJ" => "ZSU",
					"PH" => "ZHN",
					"PA" => "ZAN",
					"PG" => "ZUA",
					_ => "ZZZ"
				};
			}
			else
				return centerAirports.First(kvp => kvp.Value.Any(ad => ad.Identifier == facility)).Key;
		}).ToDictionary(g => g.Key, g => g.ToArray());

		Console.WriteLine(" Done!");

		// Make sure folders are in place.
		if (!Directory.Exists(config.OutputFolder))
			Directory.CreateDirectory(config.OutputFolder);

#if OSM
		foreach (string existingIsc in Directory.EnumerateFiles(config.OutputFolder, "*.isc"))
			File.Delete(existingIsc);
#endif

		string includeFolder = Path.Combine(config.OutputFolder, "Include");
#if OSM
		if (Directory.Exists(includeFolder))
			Directory.Delete(includeFolder, true);
#endif

		Directory.CreateDirectory(includeFolder);
		includeFolder = Path.Combine(includeFolder, "US");
		Directory.CreateDirectory(includeFolder);
		string polygonFolder = Path.Combine(includeFolder, "polygons");
		Directory.CreateDirectory(polygonFolder);

		Console.Write("Generating shared navigation data..."); await Console.Out.FlushAsync();
		WriteNavaids(includeFolder, cifp);
		Console.WriteLine(" Done!");

#if OSM
		Console.Write("Partitioning airport data..."); await Console.Out.FlushAsync();
		Osm apBoundaries = osm.GetFiltered(g =>
			g is Way or Relation &&
			g["aeroway"] == "aerodrome" &&
			g["icao"] is not null &&
			g["abandoned"] is null
		);

		Dictionary<string, Way> apBoundaryWays = apBoundaries.WaysAndBoundaries()
				.Select(w => (w["icao"], w))
				.OrderBy(kvp => kvp.w.Tags.ContainsKey("military") ? 1 : 0)
				.DistinctBy(kvp => kvp.Item1)
				.Where(kvp => kvp.Item1 is not null)
				.ToDictionary(kvp => kvp.Item1!, kvp => kvp.w);

		IDictionary<string, Osm> apOsms = osm.GetFiltered(item => item is not Node n || n["aeroway"] is "parking_position").Group(
			apBoundaryWays,
			30
		);

		Dictionary<string, Way[]> artccOsmOnlyIcaos =
			apBoundaries.GetFiltered(apb => !cifp.Aerodromes.ContainsKey(apb["icao"]!)).Group(
				artccBoundaries.ToDictionary(b => b.Key, b => new Way(0, [.. b.Value.Select(n => new Node(0, n.Latitude, n.Longitude, FrozenDictionary<string, string>.Empty))], FrozenDictionary<string, string>.Empty))
			).ToDictionary(
				kvp => kvp.Key,
				kvp => kvp.Value.WaysAndBoundaries().ToArray());

		Console.WriteLine($" Done!");
		Console.Write("Generating labels, centerlines, and coastline..."); await Console.Out.FlushAsync();
		WriteGeos(includeFolder, apOsms);
		Console.WriteLine($" Done!");
		Console.Write("Generating polygons..."); await Console.Out.FlushAsync();

		var polygonBlocks = apOsms.AsParallel().AsUnordered().Select(input =>
		{
			var (icao, apOsm) = input;
			StringBuilder tfls = new();

			// Aprons
			foreach (Way apron in apOsm.GetFiltered(g => g["aeroway"] is "apron").WaysAndBoundaries())
			{
				tfls.AppendLine($"STATIC;APRON;1;APRON;");

				foreach (Node n in apron.Nodes.Append(apron.Nodes[0]))
					tfls.AppendLine($"{n.Latitude:00.0#####};{n.Longitude:000.0#####};");
			}

			// Buildings
			foreach (Way building in apOsm.GetFiltered(g => g["aeroway"] is "terminal" or "hangar").WaysAndBoundaries())
			{
				tfls.AppendLine($"STATIC;BUILDING;1;BUILDING;");

				foreach (Node n in building.Nodes.Append(building.Nodes[0]))
					tfls.AppendLine($"{n.Latitude:00.0#####};{n.Longitude:000.0#####};");
			}

			// Taxiways
			Taxiways taxiways = new(
				icao,
				apOsm.GetFiltered(g => g is Way w && w["aeroway"] is "taxiway")
			);
			foreach (Way txw in taxiways.BoundingBoxes)
			{
				tfls.AppendLine($"STATIC;TAXIWAY;1;TAXIWAY;");

				foreach (Node n in txw.Nodes)
					tfls.AppendLine($"{n.Latitude:00.0#####};{n.Longitude:000.0#####};");
			}

			// Helipads
			foreach (Way helipad in apOsm.GetFiltered(g => g["aeroway"] is "helipad").WaysAndBoundaries())
			{
				tfls.AppendLine($"STATIC;RUNWAY;1;RUNWAY;");

				foreach (Node n in helipad.Nodes)
					tfls.AppendLine($"{n.Latitude:00.0#####};{n.Longitude:000.0#####};");
			}

			double rwWidth = cifp.Runways.TryGetValue(icao, out var rws) ? rws.Average(rw => rw.Width * 0.00000137) : 0.0002;
			// Runways
			foreach (Way rw in apOsm.GetFiltered(g => g["aeroway"] is "runway").WaysAndBoundaries().Select(rw => rw.Inflate(rwWidth)))
			{
				tfls.AppendLine($"STATIC;RUNWAY;1;RUNWAY;");

				foreach (Node n in rw.Nodes)
					tfls.AppendLine($"{n.Latitude:00.0#####};{n.Longitude:000.0#####};");
			}

			return (icao, tfls.ToString());
		});

		foreach (var (icao, tfl) in polygonBlocks.Where(i => i.Item2.Length > 0))
			File.WriteAllText(Path.Combine(polygonFolder, icao + ".tfl"), tfl);

		Console.WriteLine($" Done!");
#endif
		Console.Write("Generating procedures..."); await Console.Out.FlushAsync();
		var apProcFixes = await WriteProceduresAsync(cifp, includeFolder);
		Console.WriteLine($" Done!");
#if OSM
		Console.Write("Generating MRVAs..."); await Console.Out.FlushAsync();
		// Dummy loader to force all the downloading.
		_ = new Mrva([]);
		Console.WriteLine($" Done!");
		ConcurrentDictionary<string, bool> mrvaWrites = [];

		Parallel.ForEach(faaArtccs, async (artcc, _, _) =>
		{
			WriteIsc(
				includeFolder, artcc, cifp, config,
				artccBoundaries, artccNeighbours, faaArtccs, centerAirports, positionArtccs, artccOsmOnlyIcaos, centerRunways,
				apProcFixes, apBoundaryWays
			);

			Console.Write($"{artcc} "); await Console.Out.FlushAsync();
		});
#endif

		Console.WriteLine(" All Done!");
	}

	static string ArtccIcao(string faa) => faa switch {
		"ZSU" => "TJZS",
		"ZAN" => "PAZA",
		"ZHN" => "PHZH",
		"ZGU" => "KZAK",
		_ => "K" + faa
	};

	static void WriteNavaids(string includeFolder, CIFP cifp)
	{
		string navaidFolder = Path.Combine(includeFolder, "navaids");
		Directory.CreateDirectory(navaidFolder);

		File.WriteAllLines(Path.Combine(navaidFolder, "ndb.ndb"), [..cifp.Navaids.SelectMany(kvp => kvp.Value).Where(nv => nv is NDB).Cast<NDB>()
			.Select(ndb => $"{ndb.Identifier} ({ndb.Name});{ndb.Channel};{ndb.Position.Latitude:00.0####};{ndb.Position.Longitude:000.0####};0;")
			.Concat(cifp.Navaids.SelectMany(kvp => kvp.Value).Where(nv => nv is DME).Cast<DME>()
			.Select(dme => $"{dme.Identifier} ({dme.Name});{dme.Channel};{dme.Position.Latitude:00.0####};{dme.Position.Longitude:000.0####};0;"))]);
		File.WriteAllLines(Path.Combine(navaidFolder, "vor.vor"), [..cifp.Navaids.SelectMany(kvp => kvp.Value).Where(nv => nv is VOR).Cast<VOR>()
			.Select(vor => $"{vor.Identifier} ({vor.Name});{vor.Frequency:000.000};{vor.Position.Latitude:00.0####};{vor.Position.Longitude:000.0####};0;")]);
	}

	static async Task<(string apiToken, string refreshToken)> GetApiKeysAsync(Config config)
	{
		using Oauth oauth = new();
		JsonNode jsonNode;
		if ((config.IvaoApiRefresh ?? Environment.GetEnvironmentVariable("IVAO_REFRESH")) is string apiRefresh)
			jsonNode = await oauth.GetOpenIdFromRefreshTokenAsync(apiRefresh);
		else
			jsonNode = await oauth.GetOpenIdFromBrowserAsync();

		return (
			jsonNode["access_token"]!.GetValue<string>(),
			jsonNode["refresh_token"]!.GetValue<string>()
		);
	}

	static void WriteGeos(string includeFolder, IDictionary<string, Osm> apOsms)
	{
		string geoFolder = Path.Combine(includeFolder, "geos");
		Directory.CreateDirectory(geoFolder);
		string labelFolder = Path.Combine(includeFolder, "labels");
		Directory.CreateDirectory(labelFolder);

		const double CHAR_WIDTH = 0.0001;
		foreach (var (icao, apOsm) in apOsms)
		{
			List<string> gtsLabels = [];

			// Aprons & Buildings
			foreach (Way location in apOsm.GetFiltered(g => g["aeroway"] is "apron" or "terminal" or "hangar" or "helipad" && ((g["name"] ?? g["ref"]) is not null)).WaysAndBoundaries())
			{
				string label = (location["name"] ?? location["ref"])!;
				gtsLabels.Add($"{label};{icao};{location.Nodes.Average(n => n.Latitude) - CHAR_WIDTH:00.0####};{location.Nodes.Average(n => n.Longitude) - label.Length * CHAR_WIDTH / 2:000.0####};");
			}

			// Gates
			Gates gates = new(
				icao,
				apOsm.GetFiltered(g => g is Way w && w["aeroway"] is "parking_position")
			);
			gtsLabels.AddRange(gates.Labels.Split("\r\n", StringSplitOptions.RemoveEmptyEntries));
			File.WriteAllLines(Path.Combine(labelFolder, icao + ".gts"), [.. gtsLabels]);

			Osm taxiwayOsm = apOsm.GetFiltered(g => g is Way w && w["aeroway"] is "taxiway" or "taxilane");
			// Taxiways
			Taxiways taxiways = new(icao, taxiwayOsm);
			string[] txilabels = taxiways.Labels.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
			File.WriteAllLines(Path.Combine(labelFolder, icao + ".txi"), txilabels);

			StringBuilder stoplineGeos = new();
			// Stopbars
			foreach (Way stopline in apOsm.GetFiltered(g => g is Way w && w.Nodes.Length > 1 && w["aeroway"] is "holding_position").Ways.Values)
				foreach ((Node from, Node to) in stopline.Nodes[..^1].Zip(stopline.Nodes[1..]))
					stoplineGeos.AppendLine($"{from.Latitude:00.0####};{from.Longitude:000.0####};{to.Latitude:00.0####};{to.Longitude:000.0####};STOPLINE;");

			// Geos
			File.WriteAllText(Path.Combine(geoFolder, icao + ".geo"), gates.Routes + "\r\n\r\n" + taxiways.Centerlines + "\r\n\r\n" + stoplineGeos.ToString());
		}

		Way[] coastlineGeos = Coastline.LoadTopologies("coastline")['i'];

		File.WriteAllLines(
			Path.Combine(geoFolder, "coast.geo"),
			coastlineGeos.Where(w => w.Nodes.Length >= 2 && w.Nodes.Any(n => ((n.Latitude > 15 && n.Longitude < -50 && (n.Latitude < 49 || n.Longitude < -129.5))) || (n.Longitude >= 131.5 && n.Latitude >= 0 && n.Latitude < 21))).SelectMany(w =>
				w.BreakAntimeridian().Nodes.Zip(w.Nodes.Skip(1).Append(w.Nodes[0])).Select(np =>
					Math.Abs(np.First.Longitude - np.Second.Longitude) > 180
					? "// BREAK AT ANTIMERIDIAN."
					: $"{np.First.Latitude:00.0####};{np.First.Longitude:000.0####};{np.Second.Latitude:00.0####};{np.Second.Longitude:000.0####};COAST;"
				)
			)
		);
	}

	static async Task<FrozenDictionary<string, HashSet<NamedCoordinate>>> WriteProceduresAsync(CIFP cifp, SectorTweaks tweaks, string includeFolder)
	{
		string procedureFolder = Path.Combine(includeFolder, "procedures");
		Directory.CreateDirectory(procedureFolder);

		ConcurrentDictionary<string, HashSet<NamedCoordinate>> apProcFixes = [];
		Procedures procs = new(cifp, tweaks);

		var tecRoutes = await Tec.GetRoutesAsync();
		var (tecLines, tecFixes) = Procedures.TecLines(cifp, tecRoutes);
		File.WriteAllLines(Path.Combine(procedureFolder, "KSCT.sid"), tecLines);
		apProcFixes["KSCT"] = [.. tecFixes];

		Parallel.ForEach(cifp.Aerodromes.Values, airport =>
		{
			var (sidLines, sidFixes) = procs.AirportSidLines(airport.Identifier);
			var (starLines, starFixes) = procs.AirportStarLines(airport.Identifier);
			var (iapLines, iapFixes) = procs.AirportApproachLines(airport.Identifier);

			if (sidLines.Length > 0)
				File.WriteAllLines(Path.Combine(procedureFolder, airport.Identifier + ".sid"), sidLines);

			if (starLines.Length > 0)
			{
				File.WriteAllLines(Path.Combine(procedureFolder, airport.Identifier + ".str"), starLines);

				if (iapLines.Length > 0)
					File.AppendAllLines(Path.Combine(procedureFolder, airport.Identifier + ".str"), iapLines);
			}
			else if (iapLines.Length > 0)
				File.WriteAllLines(Path.Combine(procedureFolder, airport.Identifier + ".str"), iapLines);

			apProcFixes[airport.Identifier] = [.. sidFixes, .. starFixes, .. iapFixes];
		});

		return apProcFixes.ToFrozenDictionary();
	}

	static void WriteIsc(
		string includeFolder, string artcc, CIFP cifp, Config config,
		IDictionary<string, (double Latitude, double Longitude)[]> artccBoundaries,
		IDictionary<string, string[]> artccNeighbours,
		string[] faaArtccs,
		IDictionary<string, HashSet<Aerodrome>> centerAirports,
		IDictionary<string, JsonObject[]> positionArtccs,
		IDictionary<string, Way[]> artccOsmOnlyIcaos,
		IDictionary<string, HashSet<(string Airport, HashSet<Runway> Runways)>> centerRunways,
		IDictionary<string, HashSet<NamedCoordinate>> apProcFixes,
		IDictionary<string, Way> apBoundaryWays
	)
	{
		string mvaFolder = Path.Combine(includeFolder, "mvas");
		if (!Directory.Exists(mvaFolder))
			Directory.CreateDirectory(mvaFolder);

		Airport[] ifrAirports = [.. centerAirports[artcc].Where(ad => ad is Airport ap && ap.IFR).Cast<Airport>()];

		if (ifrAirports.Length == 0)
		{
			Console.Write($"({artcc} skipped (no airports))");
			return;
		}

		(double Latitude, double Longitude) centerpoint = (
			ifrAirports.Average(ap => (double)ap.Location.Latitude),
			ifrAirports.Average(ap => (double)(ap.Location.Longitude > 0 ? ap.Location.Longitude - 360 : ap.Location.Longitude))
		);

		// Info.
		double cosLat = Math.Cos(centerpoint.Latitude * Math.PI / 180);

		string infoBlock = $@"[INFO]
{Dms(centerpoint.Latitude, false)}
{Dms(centerpoint.Longitude, true)}
60
{60 * Math.Abs(cosLat):00}
{-ifrAirports.Average(ap => ap.MagneticVariation):00.0000}
US/{artcc};US/labels;US/geos;US/polygons;US/procedures;US/navaids;US/mvas
";
		string artccFolder = Path.Combine(includeFolder, artcc);
		if (!Directory.Exists(artccFolder))
			Directory.CreateDirectory(artccFolder);

		// Colours.
		string defineBlock = $@"[DEFINE]
TAXIWAY;#FF999A99;
APRON;#FFB9BBBB;
OUTLINE;#FF000000;
BUILDING;#FF773333;
RUNWAY;#FF555555;
STOPBAR;#FFB30000;
";

		// ATC Positions.
		string atcBlock = "[ATC]\r\nF;atc.atc\r\n";
		string allPositions = string.Join(' ', positionArtccs[artcc].Select(p => p["composePosition"]!.GetValue<string>()));
		File.WriteAllLines(Path.Combine(artccFolder, "atc.atc"), [..
			positionArtccs[artcc].Select(p => $"{p["composePosition"]!.GetValue<string>()};{p["frequency"]!.GetValue<decimal>():000.000};{allPositions};")
		]);

		// Airports (main).
		string airportBlock = "[AIRPORT]\r\nF;airports.ap\r\n";
		File.WriteAllLines(Path.Combine(artccFolder, "airports.ap"), [..
			centerAirports[artcc].Select(ad => $"{ad.Identifier};{ad.Elevation.ToMSL().Feet};18000;{ad.Location.Latitude:00.0####};{ad.Location.Longitude:000.0####};{ad.Name.TrimEnd()};")
				.Concat(
					artccOsmOnlyIcaos.TryGetValue(artcc, out var aooi)
					? aooi.Select(w => $"{w["icao"]!};0;18000;{w.Nodes.Average(n => n.Latitude):00.0####};{w.Nodes.Average(n => n.Longitude):000.0####};{w["name"] ?? "Unknown Airport"};")
					: []
				)
		]);

		// Runways.
		string runwayBlock = "[RUNWAY]\r\nF;runways.rw\r\n";
		File.WriteAllText(Path.Combine(artccFolder, "runways.rw"), string.Join(
		"\r\n",
		centerRunways[artcc].SelectMany(crg =>
			crg.Runways
				.Where(rw => rw.Identifier.CompareTo(rw.OppositeIdentifier) <= 0 && crg.Runways.Any(rw2 => rw.OppositeIdentifier == rw2.Identifier))
				.Select(rw => (Primary: rw, Opposite: crg.Runways.First(rw2 => rw2.Identifier == rw.OppositeIdentifier)))
				.Select(rws => $"{crg.Airport};{rws.Primary.Identifier};{rws.Opposite.Identifier};{rws.Primary.TDZE.ToMSL().Feet};{rws.Opposite.TDZE.ToMSL().Feet};" +
							   $"{(int)rws.Primary.Course.Degrees};{(int)rws.Opposite.Course.Degrees};" +
							   $"{rws.Primary.Endpoint.Latitude:00.0####};{rws.Primary.Endpoint.Longitude:000.0####};{rws.Opposite.Endpoint.Latitude:00.0####};{rws.Opposite.Endpoint.Longitude:000.0####};")
		).Append(
			"KSCT;TEC;TEC;100;100;0;0;0;0;0;0;"
		)
	));

		// Airways.
		Airway[] inScopeLowAirways = [.. cifp.Airways.Where(kvp => kvp.Key[0] is 'V' or 'T').SelectMany(kvp => kvp.Value.Where(v => v.Count() >= 2 && v.Any(p => IsInPolygon(artccBoundaries[artcc], ((double)p.Point.Latitude, (double)p.Point.Longitude)))))];
		Airway[] inScopeHighAirways = [.. cifp.Airways.Where(kvp => kvp.Key[0] is 'Q' or 'J').SelectMany(kvp => kvp.Value.Where(v => v.Count() >= 2 && v.Any(p => IsInPolygon(artccBoundaries[artcc], ((double)p.Point.Latitude, (double)p.Point.Longitude)))))];
		string airwaysBlock = $@"[LOW AIRWAY]
F;airways.low

[HIGH AIRWAY]
F;airways.high
";

		File.WriteAllLines(Path.Combine(artccFolder, "airways.low"), inScopeLowAirways.SelectMany(v => (string[])[
			$"L;{v.Identifier};{v.Skip(v.Count() / 2).First().Point.Latitude:00.0####};{v.Skip(v.Count() / 2).First().Point.Longitude:000.0####};",
		..v.Select(p => $"T;{v.Identifier};{p.Name ?? p.Point.Latitude.ToString("00.0####")};{p.Name ?? p.Point.Longitude.ToString("000.0####")};")
		]));

		File.WriteAllLines(Path.Combine(artccFolder, "airways.high"), inScopeHighAirways.SelectMany(v => (string[])[
			$"L;{v.Identifier};{v.Skip(v.Count() / 2).First().Point.Latitude:00.0####};{v.Skip(v.Count() / 2).First().Point.Longitude:000.0####};",
		..v.Select(p => $"T;{v.Identifier};{p.Name ?? p.Point.Latitude.ToString("00.0####")};{p.Name ?? p.Point.Longitude.ToString("000.0####")};")
		]));

		// Fixes.
		string fixesBlock = "[FIXES]\r\nF;fixes.fix\r\n";
		(string Key, Coordinate Point)[] fixes = [..cifp.Fixes.SelectMany(g => g.Value.Select(v => (g.Key, Point: v.GetCoordinate())))
		.Where(f => IsInPolygon(artccBoundaries[artcc], ((double)f.Point.Latitude, (double)f.Point.Longitude)))
		.Concat(cifp.Navaids.SelectMany(g => g.Value.Select(v => (g.Key, Point: v.Position))))];

		File.WriteAllLines(Path.Combine(artccFolder, "fixes.fix"), [..
		fixes
			.Concat(inScopeLowAirways.SelectMany(aw => aw.Where(p => p.Name is string n && !fixes.Any(f => f.Key == n))).Select(p => (Key: p.Name!, Point: p.Point.GetCoordinate())))
			.Concat(inScopeHighAirways.SelectMany(aw => aw.Where(p => p.Name is string n && !fixes.Any(f => f.Key == n))).Select(p => (Key: p.Name!, Point: p.Point.GetCoordinate())))
			.Concat(centerAirports[artcc].SelectMany(icao => apProcFixes.TryGetValue(icao.Identifier, out var fixes) ? fixes : []).Select(p => (Key: p.Name!, Point: p.GetCoordinate())))
			.Select(f => $"{f.Key};{f.Point.Latitude:00.0####};{f.Point.Longitude:000.0####};")
		]);

		// Navaids.
		string navaidBlock = "[NDB]\r\nF;ndb.ndb\r\n\r\n[VOR]\r\nF;vor.vor\r\n";

		// ARTCC boundaries.
		string artccBlock = $@"[ARTCC]
F;artcc.artcc

[ARTCC LOW]
F;low.artcc

[ARTCC HIGH]
F;high.artcc
";

		IEnumerable<string> generateBoundary(string artcc)
		{
			(double Latitude, double Longitude)[] points =
				faaArtccs.Contains(artcc)
				? artccBoundaries[artcc]
				: [.. artccBoundaries[artcc], .. artccBoundaries[artcc].Reverse()];

			var pairs = points.Zip(points[1..].Append(points[0])).Append((First: points[0], Second: points[0]));

			return pairs.SelectMany(bps => (string[])[$"T;{artcc};{bps.First.Latitude:00.0####};{bps.First.Longitude:000.0####};"])
				.Prepend($"L;{artcc};{artccBoundaries[artcc].Average(bp => bp.Latitude):00.0####};{artccBoundaries[artcc].Average(bp => bp.Longitude):000.0####};7;");
		}

		File.WriteAllText(Path.Combine(artccFolder, "artcc.artcc"), $@"{string.Join("\r\n", artccBoundaries[artcc].Append(artccBoundaries[artcc][0]).Select(bp => $"T;{artcc};{bp.Latitude:00.0####};{bp.Longitude:000.0####};"))}
{string.Join("\r\n", artccNeighbours[artcc].Select(n => string.Join("\r\n", generateBoundary(n))))}
");

		CifpAirspaceDrawing ad = new(cifp.Airspaces.Where(ap => ap.Regions.Any(r => r.Boundaries.Any(b => IsInPolygon(artccBoundaries[artcc], ((double)b.Vertex.Latitude, (double)b.Vertex.Longitude))))));
		File.WriteAllText(Path.Combine(artccFolder, "low.artcc"), ad.ClassBPaths + "\r\n\r\n" + ad.ClassCPaths + "\r\n\r\n" + ad.ClassDPaths + "\r\n\r\n" + ad.ClassBLabels);
		File.WriteAllText(Path.Combine(artccFolder, "high.artcc"),
			string.Join("\r\n",
				positionArtccs[artcc].Where(p => p["position"]?.GetValue<string>() is "APP" && p["regionMap"] is JsonArray region && region.Count > 0 && p["airportId"] is not null)
				.Select(p => WebeyeAirspaceDrawing.ToArtccPath(p["airportId"]!.GetValue<string>(), p["regionMap"]!.AsArray()))
			)
		);

		// TODO: VFR Routes
		string vfrBlock = "[VFRFIX]\r\nF;vfr.fix\r\n";

		File.WriteAllLines(Path.Combine(artccFolder, "vfr.fix"), [..
		fixes
			.Where(f => f.Key.StartsWith("VP"))
			.Select(f => $"{f.Key};{f.Point.Latitude:00.0####};{f.Point.Longitude:000.0####};")
		]);

		// MRVAs
		Mrva mrvas = new(artccBoundaries[artcc]);
		string mvaBlock = $@"[MVA]
{string.Join("\r\n", mrvas.Volumes.Keys.Select(k => "F;" + ArtccIcao(k) + ".mva"))}
";

		string genLabelLine(string volume, Mrva.MrvaSegment seg)
		{
			var (lat, lon) = mrvas.PlaceLabel(seg);
			return $"L;{seg.Name};{lat:00.0####};{lon:000.0####};{seg.MinimumAltitude / 100:000};8;";
		}

		foreach (var (fn, volume) in mrvas.Volumes)
			try
			{
				File.WriteAllLines(Path.Combine(mvaFolder, ArtccIcao(fn) + ".mva"),
					volume.Select(seg => string.Join("\r\n",
						seg.BoundaryPoints.Select(bp => $"T;{seg.Name};{bp.Latitude:00.0####};{bp.Longitude:000.0####};")
										  .Prepend(genLabelLine(fn, seg))
					))
				);
			}
			catch (IOException) { /* File in use. */ }

		// Airports (additional).
		File.AppendAllLines(Path.Combine(artccFolder, "airports.ap"), [..
				mrvas.Volumes.Keys
					.Where(k => !centerAirports[artcc].Any(ad => ad.Identifier == ArtccIcao(k))).Select(k =>
						$"{ArtccIcao(k)};{mrvas.Volumes[k].Min(s => s.MinimumAltitude)};18000;" +
						$"{mrvas.Volumes[k].Average(s => s.BoundaryPoints.Average((Func<(double Latitude, double _), double>)(bp => bp.Latitude))):00.0####};{mrvas.Volumes[k].Average(s => s.BoundaryPoints.Average((Func<(double _, double Longitude), double>)(bp => bp.Longitude))):000.0####};" +
						$"{k} TRACON;"
					)
		]);

		// Geo file references.
		string geoBlock = @$"[GEO]
F;coast.geo
{string.Join("\r\n", centerAirports[artcc].Select(ap => $"F;{ap.Identifier}.geo"))}
{(artccOsmOnlyIcaos.TryGetValue(artcc, out var aoois) ? string.Join("\r\n", aoois.Where(ap => (ap["icao"] ?? ap["faa"]) is not null).Select(ap => $"F;{ap["icao"] ?? ap["faa"]}.geo")) : "")}
";

		// Polyfills for dynamic sectors.
		string polyfillBlock = $@"[FILLCOLOR]
F;online.ply
";
		File.WriteAllText(Path.Combine(artccFolder, "online.ply"), $@"{WebeyeAirspaceDrawing.ToPolyfillPath($"{ArtccIcao(artcc)}_CTR", "CTR", artccBoundaries[artcc])}

{string.Join("\r\n\r\n",
		positionArtccs[artcc]
			.Where(p => p["composePosition"] is not null && p["position"]?.GetValue<string>() is "APP" or "DEP" or "CTR" or "FSS" && p["regionMap"] is JsonArray map && map.Count > 1)
			.Select(p => WebeyeAirspaceDrawing.ToPolyfillPath(p["composePosition"]!.GetValue<string>(), p["position"]!.GetValue<string>(), p["regionMap"]!.AsArray()))
	)}

{string.Join("\r\n\r\n",
		centerAirports[artcc]
			.Select(ad => apBoundaryWays.TryGetValue(ad.Identifier, out var retval) ? (ad.Identifier, retval) : ((string, Way)?)null)
			.Where(ap => ap is not null)
			.Cast<(string Icao, Way Boundary)>()
			.Select(ap => (
				Pos: string.Join(' ',
					positionArtccs[artcc]
						.Where(p => p["airportId"]?.GetValue<string>() == ap.Icao && p["position"]?.GetValue<string>() == "TWR")
						.Select(p => p["composePosition"]!.GetValue<string>())
				),
				Bounds: ap.Boundary
			))
			.Select(ap => WebeyeAirspaceDrawing.ToPolyfillPath(ap.Pos, "TWR", ap.Bounds))
	)}");

		File.WriteAllText(Path.Combine(config.OutputFolder, $"{ArtccIcao(artcc)}.isc"), $@"{infoBlock}
{defineBlock}
{atcBlock}
{airportBlock}
{runwayBlock}
{fixesBlock}
{navaidBlock}
{airwaysBlock}
{vfrBlock}
{mvaBlock}
{artccBlock}
{geoBlock}
{polyfillBlock}");
	}
}