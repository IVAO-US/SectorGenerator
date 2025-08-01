﻿#define OSM
using CIFPReader;

using ManualAdjustments;

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

		Console.Write("Downloading FAA ARCGIS data..."); await Console.Out.FlushAsync();
		var (artccBoundaries, artccNeighbours, faaArtccs) = await LoadArtccsAsync();
		Console.WriteLine(" Done!");

		bool IsInArtccC(string artcc, ICoordinate point) => artccBoundaries[artcc].Any(b => IsInPolygon(b, point));

		Console.Write("Queueing MRVA data downloads..."); await Console.Out.FlushAsync();
		Mrva? mrvas = null;
		Task mrvaLoader = Task.Run(async () => mrvas = await Mrva.LoadMrvasAsync());
		Console.WriteLine(" Done!");

#if OSM
		Console.Write("Queueing OSM downloads..."); await Console.Out.FlushAsync();
		Osm? osm = null;
		Task osmLoader = Task.Run(async () => osm = await LoadOsmAsync());
		Console.WriteLine(" Done!");
#endif

		Console.Write("Downloading CIFPs..."); await Console.Out.FlushAsync();
		CIFP? cifp = null;
		Task cifpLoader = Task.Run(async () => cifp = await LoadCifpAsync());
		Console.WriteLine(" Done!");

		List<ManualAdjustment> manualAdjustments = [];
		if (config.ManualAdjustmentsFolder is string maf && Directory.Exists(maf))
		{
			Console.Write($"Located manual adjustments... "); await Console.Out.FlushAsync();
			string[] files = [.. Directory.EnumerateFiles(maf, "*.maf", SearchOption.AllDirectories)];
			Console.Write($"{files.Length} files detected... "); await Console.Out.FlushAsync();

			foreach (string filepath in files)
				if (Parsing.Parse(File.ReadAllText(filepath)) is ParseResult<ManualAdjustment[]> res)
					manualAdjustments.AddRange(res.Result);

			Console.WriteLine($"Done! Loaded {manualAdjustments.Count} adjustments.");
		}

		await cifpLoader;
		foreach (var airport in cifp!.Aerodromes.Values)
			if (cifp.Fixes.TryGetValue(airport.Identifier, out var apFixes))
				apFixes.Add(airport.Location);
			else
				cifp.Fixes[airport.Identifier] = [airport.Location];

		foreach (var runway in cifp.Runways.Values.SelectMany(r => r))
			if (cifp.Fixes.TryGetValue($"{runway.Airport}/RW{runway.Identifier}", out var rwyFixes))
				rwyFixes.Add(runway.Endpoint);
			else
				cifp.Fixes[$"{runway.Airport}/RW{runway.Identifier}"] = [runway.Endpoint];

		Dictionary<string, HashSet<AddProcedure>> addedProcedures = [];
		HashSet<NamedCoordinate> vfrFixes = [];
		HashSet<(string Filter, ICoordinate[] Points)> vfrRoutes = [];
		Dictionary<string, (string Colour, HashSet<IDrawableGeo> Drawables)> videoMaps = [];
		if (manualAdjustments.Count > 0)
		{
			Console.Write($"Applying {manualAdjustments.Count} manual adjustments..."); await Console.Out.FlushAsync();

			// Fixes
			foreach (AddFix af in manualAdjustments.Where(a => a is AddFix).Cast<AddFix>())
				if (cifp.Fixes.TryGetValue(af.Name, out var prevKnownFixes))
				{
					ICoordinate newFix = af.Position.Resolve(cifp);
					Coordinate newFixPos = newFix.GetCoordinate();

					if (!prevKnownFixes.Any(pnf => pnf.Latitude == newFixPos.Latitude && pnf.Longitude == newFixPos.Longitude))
						prevKnownFixes.Add(newFix);
				}
				else
					cifp.Fixes.Add(af.Name, [af.Position.Resolve(cifp)]);

			foreach (RemoveFix rf in manualAdjustments.Where(a => a is RemoveFix).Cast<RemoveFix>())
				cifp.Fixes.Remove(rf.Fix.Coordinate is NamedCoordinate nc ? nc.Name : rf.Fix.FixName!.Name);

			// VFR fixes
			foreach (AddVfrFix vf in manualAdjustments.Where(a => a is AddVfrFix).Cast<AddVfrFix>())
				vfrFixes.Add(new(vf.Name, vf.Position.Resolve(cifp).GetCoordinate()));

			// VFR routes
			foreach (AddVfrRoute vr in manualAdjustments.Where(a => a is AddVfrRoute).Cast<AddVfrRoute>())
				vfrRoutes.Add((vr.Filter, [.. vr.Points.Select(p => {
					ICoordinate resolvedCoord = p.Resolve(cifp);

					if (resolvedCoord is NamedCoordinate nc)
					{
						if (cifp.Fixes.TryGetValue(nc.Name, out var prevKnownFixes))
						{
							if (!prevKnownFixes.Contains(nc))
								prevKnownFixes.Add(nc);
						}
						else
							cifp.Fixes.Add(nc.Name, [nc]);

						vfrFixes.Add(nc);
					}

					return resolvedCoord;
				})]));

			// Airways (in-place)
			HashSet<PossiblyResolvedWaypoint> failedResolutions = [];
			foreach (AddAirway aw in manualAdjustments.Where(a => a is AddAirway).Cast<AddAirway>())
				for (int idx = 0; idx < aw.Points.Length; ++idx)
				{
					try
					{
						aw.Points[idx] = new(aw.Points[idx].Resolve(cifp), null, null);
					}
					catch
					{
						failedResolutions.Add(aw.Points[idx]);
					}
				}

			if (failedResolutions.Count > 0)
			{
				Console.Error.WriteLine("Waypoint resolution failed! Please define the following waypoints manually:");
				foreach (PossiblyResolvedWaypoint wp in failedResolutions)
					Console.Error.WriteLine(wp.FixName?.Name ?? wp.ToString());

				Environment.Exit(-1);
			}

			// Videomaps
			foreach (AddGeo geo in manualAdjustments.Where(a => a is AddGeo).Cast<AddGeo>())
				if (videoMaps.TryGetValue(geo.Tag, out var preExistingGeos))
				{
					preExistingGeos.Drawables.UnionWith(geo.Geos.Select(g => { g.Resolve(cifp); return g; }));

					if (preExistingGeos.Colour == "#FF999999")
						videoMaps[geo.Tag] = (geo.Colour, preExistingGeos.Drawables);
				}
				else
					videoMaps.Add(geo.Tag, (geo.Colour, [.. geo.Geos.Select(g => { g.Resolve(cifp); return g; })]));

			// Procedures
			addedProcedures = manualAdjustments
				.Where(a => a is AddProcedure).Cast<AddProcedure>()
				.GroupBy(p => p.Airport)
				.ToDictionary(
					g => g.Key,
					g => g.Select(i => i with { Geos = [.. i.Geos.SelectMany<IDrawableGeo, IDrawableGeo>(g => g.Resolve(cifp) ? [g] : [])] }).ToHashSet()
				);

			foreach (RemoveProcedure proc in manualAdjustments.Where(a => a is RemoveProcedure).Cast<RemoveProcedure>())
				if (cifp.Procedures.TryGetValue(proc.Identifier, out var procs))
					procs.RemoveWhere(p => p.Airport == proc.Airport);

			Console.WriteLine(" Done!");
		}

		// Generate copy-pasteable Webeye shapes for each of the ARTCCs.
		(string Artcc, string Shape)[] artccWebeyeShapes = [..
			artccBoundaries.Where(b => faaArtccs.Contains(b.Key)).Select(b => (
				b.Key,
				string.Join("\r\n", [.. b.Value.SelectMany(v => v).Reverse().Select(p => $"{p.Latitude:00.0####}:{(p.Longitude > 0 ? p.Longitude - 360 : p.Longitude):000.0####}")])
			))
		];

		if (!Directory.Exists("webeye"))
			Directory.CreateDirectory("webeye");

		foreach (var (artcc, shape) in artccWebeyeShapes)
			File.WriteAllText(Path.ChangeExtension(Path.Join("webeye", artcc), "txt"), shape);

		Console.Write("Allocating airports to centers..."); await Console.Out.FlushAsync();
		Dictionary<string, HashSet<Aerodrome>> centerAirports = [];

		foreach (var artcc in faaArtccs)
			centerAirports.Add(artcc, [..
				cifp.Aerodromes.Values.Where(a => IsInArtccC(artcc, a.Location))
					.Concat(config.SectorAdditionalAirports.TryGetValue(artcc, out var addtl) ? addtl.SelectMany<string, Aerodrome>(a => cifp.Aerodromes.TryGetValue(a, out var r) ? [r] : []) : [])
			]);

		Console.WriteLine(" Done!");

		Console.Write("Allocating runways to centers..."); await Console.Out.FlushAsync();
		Dictionary<string, HashSet<(string Airport, HashSet<Runway> Runways)>> centerRunways = [];

		foreach (var artcc in faaArtccs)
			centerRunways.Add(artcc, [.. cifp.Runways.Where(kvp => centerAirports[artcc].Select(ad => ad.Identifier).Contains(kvp.Key)).Select(kvp => (kvp.Key, kvp.Value))]);

		Console.WriteLine(" Done!");

		Console.Write("Getting ATC positions..."); await Console.Out.FlushAsync();
		var atcPositions = await GetAtcPositionsAsync(apiToken, "K", "TJ", "PH", "PA");
		Dictionary<string, HashSet<JsonObject>> positionArtccs = [];
		foreach (var p in atcPositions)
		{
			void add(string artcc)
			{
				if (positionArtccs.TryGetValue(artcc, out var set))
					set.Add(p);
				else
					positionArtccs.Add(artcc, [p]);
			}

			string facility = p["composePosition"]!.GetValue<string>().Split("_")[0];

			if (facility.StartsWith("KZ"))
				add(facility[1..]);
			else if (TraconCenters.TryGetValue(facility, out string? artcc))
				add(artcc);
			else if (!centerAirports.Any(kvp => kvp.Value.Any(ad => ad.Identifier == facility)))
			{
				if ((p["airportId"] ?? p["centerId"])?.GetValue<string>() is string pos && centerAirports.Any(kvp => kvp.Value.Any(ad => ad.Identifier == pos)))
				{
					foreach (var artccFind in centerAirports.Where(kvp => kvp.Value.Any(ad => ad.Identifier == pos)))
						add(artccFind.Key);

					continue;
				}

				add(facility[..2] switch {
					"TJ" => "ZSU",
					"PH" => "PHZH",
					"PA" => "PAZA",
					"PG" => "ZUA",
					"MY" => "ZMA",
					_ => "ZZZ"
				});
			}
			else
				foreach (var artccFind in centerAirports.Where(kvp => kvp.Value.Any(ad => ad.Identifier == facility)))
					add(artccFind.Key);
		}

		Console.WriteLine(" Done!");

		// Make sure folders are in place.
		if (!Directory.Exists(config.OutputFolder))
			Directory.CreateDirectory(config.OutputFolder);

		foreach (string existingIsc in Directory.EnumerateFiles(config.OutputFolder, "*.isc"))
			File.Delete(existingIsc);

		string includeFolder = Path.Combine(config.OutputFolder, "Include");
		if (Directory.Exists(includeFolder))
			Directory.Delete(includeFolder, true);

		Directory.CreateDirectory(includeFolder);
		includeFolder = Path.Combine(includeFolder, "US");
		Directory.CreateDirectory(includeFolder);
		string polygonFolder = Path.Combine(includeFolder, "polygons");
		Directory.CreateDirectory(polygonFolder);

		Console.Write("Generating shared navigation data..."); await Console.Out.FlushAsync();
		WriteNavaids(includeFolder, cifp);
		Console.WriteLine(" Done!");

		Console.Write("Generating procedures..."); await Console.Out.FlushAsync();
		var apProcFixes = await WriteProceduresAsync(cifp, addedProcedures, includeFolder);
		Console.WriteLine($" Done!");

		Console.Write("Generating video maps..."); await Console.Out.FlushAsync();
		string videoMapsFolder = Path.Combine(includeFolder, "videomaps");
		if (!Directory.Exists(videoMapsFolder))
			Directory.CreateDirectory(videoMapsFolder);

		WriteVideoMaps(videoMaps, videoMapsFolder);
		Console.WriteLine($" Done!");

		Console.Write("Generating MRVAs..."); await Console.Out.FlushAsync();
		string mvaFolder = Path.Combine(includeFolder, "mvas");

		if (!Directory.Exists(mvaFolder))
			Directory.CreateDirectory(mvaFolder);

		await mrvaLoader;
		if ((mrvas?.Volumes.Count ?? 0) is 0)
			Console.WriteLine(" Failed! (bypassing)");
		else
		{
			IEnumerable<(string FilePath, string Contents)> mrvaFiles = mrvas!.Volumes.AsParallel().AsUnordered().Select(arg => {
				try
				{
					var (fn, volume) = arg;
					StringBuilder mvaFile = new();

					foreach (var seg in volume)
					{
						// Place the label line.
						var (lat, lon) = mrvas.PlaceLabel(seg);
						mvaFile.AppendLine($"L;{seg.Name};{lat:00.0####};{lon:000.0####};{seg.MinimumAltitude / 100:000};8;");

						// Add all the segments for that volume.
						foreach (var bp in seg.BoundaryPoints)
								mvaFile.AppendLine($"T;{seg.Name};{bp.Latitude:00.0####};{bp.Longitude:000.0####};");
					}

					return (FilePath: Path.Combine(mvaFolder, fn + ".mva"), Contents: mvaFile.ToString());
				}
				catch (IOException) { /* File in use. */ }
				return (FilePath: "", Contents: "");
			}).Where(p => !string.IsNullOrEmpty(p.FilePath));

			foreach (var (path, contents) in mrvaFiles)
				await File.WriteAllTextAsync(path, contents);

			Console.WriteLine($" Done!");
		}

#if OSM
		Console.Write("Awaiting OSM download..."); await Console.Out.FlushAsync();
		await osmLoader;
		Console.WriteLine(" Done!");

		Console.Write("Partitioning airport data..."); await Console.Out.FlushAsync();
		Osm apBoundaries = osm!.GetFiltered(g =>
			g is Way or Relation &&
			g["aeroway"] == "aerodrome" &&
			g["icao"] is not null &&
			g["abandoned"] is null
		);

		Console.WriteLine($" Done!");
		Console.Write("Filtering OSM data..."); await Console.Out.FlushAsync();

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

		Console.WriteLine($" Done!");
		Console.Write("Discovering missing ICAOs..."); await Console.Out.FlushAsync();

		Dictionary<string, Way[]> artccOsmOnlyIcaos =
			apBoundaries.GetFiltered(apb => !cifp.Aerodromes.ContainsKey(apb["icao"]!)).Group(
				artccBoundaries
				.Where(kvp => faaArtccs.Contains(kvp.Key))
				.ToDictionary(
					b => b.Key,
					b => new Way(0, [.. b.Value.SelectMany(v => v).Select(n => new Node(0, n.Latitude, n.Longitude, FrozenDictionary<string, string>.Empty))], FrozenDictionary<string, string>.Empty)
				)
			).ToDictionary(
				kvp => kvp.Key,
				kvp => kvp.Value.WaysAndBoundaries().ToArray());

		if (artccOsmOnlyIcaos.TryGetValue("ZMA", out var osmOnlyZma))
			artccOsmOnlyIcaos["ZMA"] = [.. osmOnlyZma, .. apBoundaries.GetFiltered(apb => apb["icao"]?.StartsWith("MY") ?? false).WaysAndBoundaries()];

		Console.WriteLine($" Done!");
		Console.Write("Generating labels, centerlines, and coastline..."); await Console.Out.FlushAsync();
		WriteGeos(includeFolder, apOsms);
		Console.WriteLine($" Done!");
		Console.Write("Generating polygons..."); await Console.Out.FlushAsync();

		var polygonBlocks = apOsms.AsParallel().AsUnordered().Select(input => {
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
		Console.Write("Drawing online polys..."); await Console.Out.FlushAsync();

		// Online polys

		File.WriteAllText(Path.Combine(polygonFolder, "online.ply"), $@"{string.Join("\r\n\r\n", artccBoundaries.Select(kvp => WebeyeAirspaceDrawing.ToPolyfillPath($"{ArtccIcao(kvp.Key)}_CTR", "CTR", [.. kvp.Value.SelectMany(p => p)])))}


		{string.Join("\r\n\r\n",
		positionArtccs.Values.SelectMany(v => v)
			.Where(p => p["composePosition"] is not null && p["position"]?.GetValue<string>() is "APP" or "DEP" or "CTR" or "FSS" && p["regionMap"] is JsonArray map && map.Count > 1)
			.Select(p => WebeyeAirspaceDrawing.ToPolyfillPath(p["composePosition"]!.GetValue<string>(), p["position"]!.GetValue<string>(), p["regionMap"]!.AsArray()))
		)}

		"
		+ string.Join("\r\n\r\n",
		centerAirports.Values.SelectMany(v => v).DistinctBy(ad => ad.Identifier)
			.Select(ad => apBoundaryWays.TryGetValue(ad.Identifier, out var retval) ? (ad.Identifier, retval) : ((string, Way)?)null)
			.Where(ap => ap is not null)
			.Cast<(string Icao, Way Boundary)>()
		.Select(ap => (
				Pos: string.Join(' ',
					positionArtccs.Values.SelectMany(v => v)
						.Where(p => p["airportId"]?.GetValue<string>() == ap.Icao && p["position"]?.GetValue<string>() == "TWR")
						.Select(p => p["composePosition"]!.GetValue<string>())
				),
				Bounds: ap.Boundary
			))
			.Select(ap => WebeyeAirspaceDrawing.ToPolyfillPath(ap.Pos, "TWR", ap.Bounds))
	)
	);
		Console.WriteLine($" Done!");
#endif

		// Write ISCs
		Parallel.ForEach(faaArtccs, async (artcc, _, _) => {
			Airport[] ifrAirports = [.. centerAirports[artcc].Where(ad => ad is Airport ap && ap.IFR).Cast<Airport>()];

			if (ifrAirports.Length == 0)
			{
				Console.Write($"({artcc} skipped (no airports)) ");
				return;
			}

			(double Latitude, double Longitude) centerpoint = (
				ifrAirports.Average(ap => (double)ap.Location.Latitude),
				ifrAirports.Average(ap => (double)(ap.Location.Longitude > 0 ? ap.Location.Longitude - 360 : ap.Location.Longitude))
			);

			// Info
			double cosLat = Math.Cos(centerpoint.Latitude * Math.PI / 180);

			string infoBlock = $@"[INFO]
{Dms(centerpoint.Latitude, false)}
{Dms(centerpoint.Longitude, true)}
60
{60 * Math.Abs(cosLat):00}
{-ifrAirports.Average(ap => ap.MagneticVariation):00.0000}
US/{artcc};US/labels;US/geos;US/polygons;US/procedures;US/navaids;US/mvas;US/videomaps
";
			string artccFolder = Path.Combine(includeFolder, artcc);
			if (!Directory.Exists(artccFolder))
				Directory.CreateDirectory(artccFolder);

			string[] applicableVideoMaps = [.. videoMaps.Where(kvp => kvp.Value.Drawables.Any(g => g.ReferenceCoordinates.Any(p => IsInArtccC(artcc, p)))).Select(kvp => kvp.Key)];

			// Colours
			string defineBlock = $@"[DEFINE]
TAXIWAY;#FF999A99;
APRON;#FFB9BBBB;
OUTLINE;#FF000000;
BUILDING;#FF773333;
RUNWAY;#FF555555;
STOPBAR;#FFB30000;
{string.Join("\r\n", applicableVideoMaps.Select(g => $"{g};{videoMaps[g].Colour};"))}
";

			// ATC Positions
			string atcBlock = "[ATC]\r\nF;atc.atc\r\n";
			if (positionArtccs.TryGetValue(artcc, out var artccPositions))
				File.WriteAllLines(Path.Combine(artccFolder, "atc.atc"), [..
					artccPositions.Select(p => $"{p["composePosition"]!.GetValue<string>()};{p["frequency"]!.GetValue<decimal>():000.000};K PA PH TJ;")
				]);

			// Airports (main)
			string airportBlock = "[AIRPORT]\r\nF;airports.ap\r\n";
			File.WriteAllLines(Path.Combine(artccFolder, "airports.ap"), [..
			centerAirports[artcc].Select(ad => $"{ad.Identifier};{ad.Elevation.ToMSL().Feet};18000;{ad.Location.Latitude:00.0####};{ad.Location.Longitude:000.0####};{ad.Name.TrimEnd()};")
#if OSM
				.Concat(
					artccOsmOnlyIcaos.TryGetValue(artcc, out var aooi)
					? aooi.Select(w => $"{w["icao"]!};0;18000;{w.Nodes.Average(n => n.Latitude):00.0####};{w.Nodes.Average(n => n.Longitude):000.0####};{w["name"] ?? "Unknown Airport"};")
					: []
				)
#endif
			]);

			// Runways
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

			// Airways
			Airway[] inScopeLowAirways = [..
				cifp.Airways.Where(kvp => kvp.Key[0] is 'V' or 'T')
					.Where(kvp => !manualAdjustments.Any(a => a is RemoveAirway raw && raw.Identifier.Equals(kvp.Key, StringComparison.InvariantCultureIgnoreCase)))
					.SelectMany(kvp => kvp.Value.Where(v => v.Count() >= 2 && v.Any(p => IsInArtccC(artcc, p.Point))))
			];

			Airway[] inScopeHighAirways = [..
				cifp.Airways.Where(kvp => kvp.Key[0] is 'Q' or 'J')
					.Where(kvp => !manualAdjustments.Any(a => a is RemoveAirway raw && raw.Identifier.Equals(kvp.Key, StringComparison.InvariantCultureIgnoreCase)))
					.SelectMany(kvp => kvp.Value.Where(v => v.Count() >= 2 && v.Any(p => IsInArtccC(artcc, p.Point))))
			];

			string airwaysBlock = $@"[LOW AIRWAY]
F;airways.low

[HIGH AIRWAY]
F;airways.high
";

			File.WriteAllLines(Path.Combine(artccFolder, "airways.low"), [
				// CIFPs
				..inScopeLowAirways.SelectMany(v => (string[])[
					$"L;{v.Identifier};{v.Skip(v.Count() / 2).First().Point.Latitude:00.0####};{v.Skip(v.Count() / 2).First().Point.Longitude:000.0####};",
					..v.Select(p => $"T;{v.Identifier};{p.Name ?? p.Point.Latitude.ToString("00.0####")};{p.Name ?? p.Point.Longitude.ToString("000.0####")};")
				]),
				
				// Manual additions
				..manualAdjustments.Where(a => a is AddAirway aw && aw.Type == AddAirway.AirwayType.Low && aw.Points.Any(p => IsInArtccC(artcc, p.Coordinate ?? new Coordinate()))).Cast<AddAirway>().SelectMany(aw => (string[])[
					$"L;{aw.Identifier};{aw.Points.Skip(aw.Points.Length / 2).First().Coordinate!.Latitude:00.0####};{aw.Points.Skip(aw.Points.Length / 2).First().Coordinate!.Longitude:000.0####};",
					..aw.Points.Select(p => {
						if (p.Coordinate is NamedCoordinate nc)
							return $"T;{aw.Identifier};{nc.Name};{nc.Name};";
						else
							return $"T;{aw.Identifier};{p.Coordinate!.Latitude:00.0####};{p.Coordinate.Longitude:000.0####};";
					})
				])
			]);

			File.WriteAllLines(Path.Combine(artccFolder, "airways.high"), [
				// CIFPs
				..inScopeHighAirways.SelectMany(v => (string[])[
					$"L;{v.Identifier};{v.Skip(v.Count() / 2).First().Point.Latitude:00.0####};{v.Skip(v.Count() / 2).First().Point.Longitude:000.0####};",
					..v.Select(p => $"T;{v.Identifier};{p.Name ?? p.Point.Latitude.ToString("00.0####")};{p.Name ?? p.Point.Longitude.ToString("000.0####")};")
				]),

				// Manual additions
				..manualAdjustments.Where(a => a is AddAirway aw && aw.Type == AddAirway.AirwayType.High && aw.Points.Any(p => IsInArtccC(artcc, p.Coordinate ?? new Coordinate()))).Cast<AddAirway>().SelectMany(aw => (string[])[
					$"L;{aw.Identifier};{aw.Points.Skip(aw.Points.Length / 2).First().Coordinate!.Latitude:00.0####};{aw.Points.Skip(aw.Points.Length / 2).First().Coordinate!.Longitude:000.0####};",
					..aw.Points.Select(p => {
						if (p.Coordinate is NamedCoordinate nc)
							return $"T;{aw.Identifier};{nc.Name};{nc.Name};";
						else
							return $"T;{aw.Identifier};{p.Coordinate!.Latitude:00.0####};{p.Coordinate.Longitude:000.0####};";
					})
				])
			]);

			// Fixes
			string fixesBlock = "[FIXES]\r\nF;fixes.fix\r\n";
			(string Key, Coordinate Point)[] fixes = [..cifp.Fixes.SelectMany(g => g.Value.Select(v => (g.Key, Point: v.GetCoordinate())))
		.Where(f => IsInArtccC(artcc, f.Point))
		.Concat(cifp.Navaids.SelectMany(g => g.Value.Select(v => (g.Key, Point: v.Position))))];

			File.WriteAllLines(Path.Combine(artccFolder, "fixes.fix"), [..
		fixes
			.Concat(inScopeLowAirways.SelectMany(aw => aw.Where(p => p.Name is string n && !fixes.Any(f => f.Key == n))).Select(p => (Key: p.Name!, Point: p.Point.GetCoordinate())))
			.Concat(inScopeHighAirways.SelectMany(aw => aw.Where(p => p.Name is string n && !fixes.Any(f => f.Key == n))).Select(p => (Key: p.Name!, Point: p.Point.GetCoordinate())))
			.Concat(manualAdjustments.Where(a => a is AddAirway).Cast<AddAirway>().SelectMany(aw => aw.Points.Where(wp => wp.Coordinate is NamedCoordinate p && p.Name is string n && !fixes.Any(f => f.Key == n))).Select(p => { var nc = (NamedCoordinate)p.Coordinate!; return (Key: nc.Name!, Point: nc.Position); }))
			.Concat(centerAirports[artcc].SelectMany(icao => apProcFixes.TryGetValue(icao.Identifier, out var fixes) ? fixes : []).Select(p => (Key: p.Name!, Point: p.GetCoordinate())))
			.Select(f => $"{f.Key};{f.Point.Latitude:00.0####};{f.Point.Longitude:000.0####};")
			]);

			// Navaids
			string navaidBlock = "[NDB]\r\nF;ndb.ndb\r\n\r\n[VOR]\r\nF;vor.vor\r\n";

			// ARTCC boundaries
			string artccBlock = $@"[ARTCC]
F;artcc.artcc

[ARTCC LOW]
F;low.artcc

[ARTCC HIGH]
F;high.artcc
";

			IEnumerable<string> generateBoundary(string artcc)
			{
				int iter = 0;
				foreach ((double Latitude, double Longitude)[] points in artccBoundaries[artcc])
				{
					++iter;
					if (points.Length < 2)
						continue;

					yield return $"L;{artcc};{points.Average(bp => bp.Latitude):00.0####};{points.Average(bp => bp.Longitude):000.0####};7;";

					foreach (var bp in points)
						yield return $"T;{artcc}_{iter};{bp.Latitude:00.0####};{bp.Longitude:000.0####};";
				}
			}

			File.WriteAllText(Path.Combine(artccFolder, "artcc.artcc"), $@"{string.Join("\r\n", generateBoundary(artcc))}
{string.Join("\r\n", artccNeighbours[artcc].Select(n => string.Join("\r\n", generateBoundary(n))))}
");

			CifpAirspaceDrawing ad = new(cifp.Airspaces.Where(ap => ap.Regions.Any(r => r.Boundaries.Any(b => IsInArtccC(artcc, b.Vertex)))));
			File.WriteAllText(Path.Combine(artccFolder, "low.artcc"), ad.ClassBPaths + "\r\n\r\n" + ad.ClassCPaths + "\r\n\r\n" + ad.ClassDPaths + "\r\n\r\n" + ad.ClassBLabels);
			File.WriteAllText(Path.Combine(artccFolder, "high.artcc"),
				string.Join("\r\n",
					positionArtccs[artcc].Where(p => p["position"]?.GetValue<string>() is "APP" && p["regionMap"] is JsonArray region && region.Count > 0 && p["airportId"] is not null)
					.Select(p => WebeyeAirspaceDrawing.ToArtccPath(p["airportId"]!.GetValue<string>(), p["regionMap"]!.AsArray()))
				)
			);

			// VFR Fixes
			string vfrBlock = "[VFRENR]\r\nF;vfr.vfi\r\n\r\n";

			File.WriteAllLines(Path.Combine(artccFolder, "vfr.vfi"), [..
				fixes
					.Where(f => f.Key.StartsWith("VP"))
					.Concat(vfrFixes.Select(f => (Key: f.Name, Point: f.GetCoordinate())))
					.Where(f => IsInArtccC(artcc, f.Point))
					.Select(f => $"{f.Key};;{f.Point.Latitude:00.0####};{f.Point.Longitude:000.0####};")
			]);

			// VFR Routes
			vfrBlock += "[VFRRTEENR]\r\nF;vfr.vrt\r\n\r\n";

			(string Filter, ICoordinate[] Points)[] applicableRoutes = [..
				vfrRoutes.Where(r => r.Points.Any(c => IsInArtccC(artcc, c)))
			];

			File.WriteAllLines(
				Path.Combine(artccFolder, "vfr.vrt"),
				applicableRoutes.Select((route, routeIdx) =>
					string.Join("\r\n", route.Points.Concat(route.Points[1..^1].Reverse()).Select(r => {
						if (r is NamedCoordinate nc)
							return $"{route.Filter};{routeIdx + 1};{nc.Name};{nc.Name};";

						Coordinate c = r.GetCoordinate();
						return $"{route.Filter};{routeIdx + 1};{c.Latitude:00.0####};{c.Longitude:000.0####};";
					})))
			);

			// MRVAs
			string mvaBlock = $@"[MVA]
{string.Join("\r\n", mrvas!.Volumes.Keys.Select(k => "F;" + k + ".mva"))}
";

			// Airports (additional)
			File.AppendAllLines(Path.Combine(artccFolder, "airports.ap"), [..
				mrvas.Volumes.Keys
					.Where(k => !centerAirports[artcc].Any(ad => ad.Identifier == k)).Select(k =>
						$"{k};{mrvas.Volumes[k].Min(s => s.MinimumAltitude)};18000;" +
						$"{mrvas.Volumes[k].Average(s => s.BoundaryPoints.Average((Func<(double Latitude, double _), double>)(bp => bp.Latitude))):00.0####};{mrvas.Volumes[k].Average(s => s.BoundaryPoints.Average((Func<(double _, double Longitude), double>)(bp => bp.Longitude))):000.0####};" +
						$"{k} TRACON;"
					)
			]);

			// Geo file references
			string geoBlock = @$"[GEO]
F;coast.geo
{string.Join("\r\n", applicableVideoMaps.Select(vm => $"F;{Path.ChangeExtension(vm, "geo")}"))}
{string.Join("\r\n", centerAirports[artcc].Select(ap => $"F;{ap.Identifier}.geo"))}
";
#if OSM
			geoBlock += (artccOsmOnlyIcaos.TryGetValue(artcc, out var aoois) ? string.Join("\r\n", aoois.Where(ap => (ap["icao"] ?? ap["faa"]) is not null).Select(ap => $"F;{ap["icao"] ?? ap["faa"]}.geo")) : "") + "\r\n";
#endif

			// Polyfills for dynamic sectors
			string polyfillBlock = $@"[FILLCOLOR]
F;online.ply
";

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

			Console.Write($"{artcc} "); await Console.Out.FlushAsync();
		});

		Console.WriteLine(" All Done!");
	}

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
				apOsm.GetFiltered(g => g["aeroway"] is "parking_position")
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

	static void WriteVideoMaps(Dictionary<string, (string Colour, HashSet<IDrawableGeo> Drawables)> layers, string geoFolder) => Parallel.ForEach(layers, kvp => {
		string layerName = kvp.Key;
		StringBuilder fileContents = new();
		fileContents.AppendLine($"// {layerName} - {kvp.Value.Drawables.Count} geos");

		foreach (IDrawableGeo geo in kvp.Value.Drawables)
		{
			ICoordinate? last = null;
			fileContents.AppendLine($"// {geo.GetType().Name}");

			foreach (ICoordinate? next in geo.Draw().Take(10000))
			{
				if (next is null)
					fileContents.AppendLine("// BREAK");
				else if (last is ICoordinate l && next is ICoordinate n)
					fileContents.AppendLine($"{l.Latitude:00.0####};{l.Longitude:000.0####};{n.Latitude:00.0####};{n.Longitude:000.0####};{layerName};");

				last = next;
			}
		}

		File.WriteAllText(Path.Combine(geoFolder, Path.ChangeExtension(layerName, ".geo")), fileContents.ToString());
	});

	static async Task<FrozenDictionary<string, HashSet<NamedCoordinate>>> WriteProceduresAsync(CIFP cifp, Dictionary<string, HashSet<AddProcedure>> addedProcedures, string includeFolder)
	{
		string procedureFolder = Path.Combine(includeFolder, "procedures");
		Directory.CreateDirectory(procedureFolder);

		ConcurrentDictionary<string, HashSet<NamedCoordinate>> apProcFixes = [];
		Procedures procs = new(cifp);

		var tecRoutes = await Tec.GetRoutesAsync();
		var (tecLines, tecFixes) = Procedures.TecLines(cifp, tecRoutes);
		File.WriteAllLines(Path.Combine(procedureFolder, "KSCT.sid"), tecLines);
		apProcFixes["KSCT"] = [.. tecFixes];

		Parallel.ForEach(cifp.Aerodromes.Values, airport => {
			var (sidLines, sidFixes) = procs.AirportSidLines(airport.Identifier);
			var (starLines, starFixes) = procs.AirportStarLines(airport.Identifier);
			var (iapLines, iapFixes) = procs.AirportApproachLines(airport.Identifier);

			if (addedProcedures.TryGetValue(airport.Identifier, out var addedProcs))
			{
				foreach (var proc in addedProcs)
				{
					HashSet<NamedCoordinate> fixes = [];

					string[] allLines = [
						$"{airport.Identifier};{string.Join(':', cifp.Runways[airport.Identifier].Select(r => r.Identifier))};{proc.Identifier};{airport.Identifier};{airport.Identifier};{(proc.Type == AddProcedure.ProcedureType.IAP ? "3;" : "")}",
						..proc.Geos.Select(g =>
					{
						bool nextBreak = true;
						List<string> lines = [];

						foreach (ICoordinate? c in g.Draw())
						{
							if (c is null)
							{
								nextBreak = true;
								continue;
							}

							if (c is NamedCoordinate nc)
							{
								lines.Add($"{nc.Name};{nc.Name};{(nextBreak ? "<br>;" : "")}");
								fixes.Add(nc);
							}
							else
								lines.Add($"{c.Latitude:00.0####};{c.Longitude:000.0####};{(nextBreak ? "<br>;" : "")}");

							nextBreak = false;
						}

						return string.Join("\r\n", lines);
					})];

					switch (proc.Type)
					{
						case AddProcedure.ProcedureType.SID:
							sidLines = [.. sidLines, .. allLines];
							sidFixes = [.. sidFixes, .. fixes];
							break;

						case AddProcedure.ProcedureType.STAR:
							starLines = [.. starLines, .. allLines];
							starFixes = [.. starFixes, .. fixes];
							break;

						case AddProcedure.ProcedureType.IAP:
							iapLines = [.. iapLines, .. allLines];
							iapFixes = [.. iapFixes, .. fixes];
							break;
					}
				}
			}

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

	static async Task<(
		Dictionary<string, HashSet<(double Latitude, double Longitude)[]>> artccBoundaries,
		Dictionary<string, string[]> artccNeighbours,
		string[] faaArtccs
	)> LoadArtccsAsync()
	{
		for (int iterations = 0; iterations < 3; ++iterations)
		{
			try
			{
				return await ArtccBoundaries.GetBoundariesAsync();
			}
			catch (IOException) { /* Sometimes things choke. */ }
			catch (TimeoutException) { /* Sometimes things choke. */ }
			catch (TaskCanceledException) { /* Sometimes things choke. */ }
		}

		throw new Exception("Could not download FAA ARCGIS data.");
	}

	static async Task<Osm> LoadOsmAsync()
	{
		for (int iterations = 0; iterations < 3; ++iterations)
		{
			try
			{
				DateTimeOffset start = DateTimeOffset.Now;
				Osm res = await Osm.Load();
				TimeSpan loadTime = DateTimeOffset.Now - start;
				Console.Write($"OSM download succeeded in {loadTime.TotalMinutes:0} mins, {loadTime.Seconds} secs");
				return res;
			}
			catch (IOException) { /* Sometimes things choke. */ }
			catch (TimeoutException) { /* Sometimes things choke. */ }
			catch (TaskCanceledException) { /* Sometimes things choke. */ }

			Console.Write("OSM download failed... Retrying...");
			await Task.Delay(TimeSpan.FromSeconds(15)); // Give it a breather.
		}

		throw new Exception("Could not download OSM data.");
	}

	static async Task<CIFP> LoadCifpAsync()
	{
		for (int iterations = 0; iterations < 3; ++iterations)
		{
			try
			{
				CIFP retval = await CIFP.LoadAsync();

				if (!Directory.Exists("cifp-reduced"))
					retval.SaveReduced();

				return retval;
			}
			catch (IOException) { /* Sometimes things choke. */ }
			catch (TimeoutException) { /* Sometimes things choke. */ }
			catch (TaskCanceledException) { /* Sometimes things choke. */ }
		}

		throw new Exception("Could not download CIFPs.");
	}
}
