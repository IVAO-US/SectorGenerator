﻿using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using static CIFPReader.ProcedureLine;

namespace CIFPReader;

public record CIFP(GridMORA[] MORAs, Airspace[] Airspaces, Dictionary<string, Aerodrome> Aerodromes, Dictionary<string, HashSet<ICoordinate>> Fixes, Dictionary<string, HashSet<Navaid>> Navaids, Dictionary<string, HashSet<Airway>> Airways, Dictionary<string, HashSet<Procedure>> Procedures, Dictionary<string, HashSet<Runway>> Runways)
{
	private CIFP() : this([], [], [], [], [], [], [], []) { }

	public int Cycle => Aerodromes.Values.Max(a => a.Cycle);

	public static async Task<CIFP> LoadAsync(string? directory = null)
	{
		if (string.IsNullOrWhiteSpace(directory))
			directory = Environment.CurrentDirectory;

		if (directory.StartsWith("https://", StringComparison.InvariantCultureIgnoreCase) || directory.StartsWith("http://", StringComparison.InvariantCultureIgnoreCase))
		{
			string[] filenames = [
				"aerodrome.json",
				"airspace.json",
				"airway.json",
				"fix.json",
				"mora.json",
				"navaid.json",
				"procedure.json",
				"runway.json",
			];

			string uriPrefix = directory.TrimEnd('/') + '/';
			directory = Environment.CurrentDirectory;
			string outDir = Path.Combine(directory, "cifp");
			// Check if we need to pull everything or not.
			if (!File.Exists(Path.Combine(outDir, "aerodrome.json")) || File.GetLastWriteTime(Path.Combine(outDir, "aerodrome.json")) < DateTime.Now.AddDays(-3))
			{
				HttpClient client = new();
				int complete = 0;

				if (!Directory.Exists(outDir))
					Directory.CreateDirectory(outDir);

				Parallel.ForEach(filenames, async fp => {
					using FileStream file = File.Create(Path.Combine(outDir, fp));
					await (await client.GetStreamAsync(uriPrefix + fp)).CopyToAsync(file);
					Interlocked.Increment(ref complete);
				});

				while (complete < filenames.Length)
					Task.Delay(250).Wait();
			}
		}
		else if (directory.StartsWith("s3://", StringComparison.InvariantCultureIgnoreCase))
		{
			directory = directory[5..];
			string bucketName = directory.Split('/')[0];
			string prefix = directory[bucketName.Length..].Trim('/') + '/';
			directory = Environment.CurrentDirectory;
			string outDir = Path.Combine(directory, "cifp");

			// Check if we need to pull everything or not.
			if (!File.Exists(Path.Combine(outDir, "aerodrome.json")) || File.GetLastWriteTime(Path.Combine(outDir, "aerodrome.json")) < DateTime.Now.AddDays(-3))
			{
				Amazon.S3.Transfer.TransferUtility util = new(Amazon.RegionEndpoint.USWest2);
				await util.DownloadDirectoryAsync(bucketName, prefix, outDir);
			}
		}

		directory = Path.GetFullPath(directory);

		string zipPath = Path.Combine(directory, "CIFP.zip"),
			   cifpPath = Path.Combine(directory, "FAACIFP18"),
			   outputPath = Path.Combine(directory, "cifp");

		if (File.Exists(zipPath))
		{
			using (ZipArchive archive = ZipFile.OpenRead(zipPath))
			{
				var zae = archive.GetEntry("FAACIFP18") ?? throw new FileNotFoundException("ZIP archive doesn't contain FAACIFP18.");

				zae.ExtractToFile(cifpPath);
			}
			File.Delete(zipPath);
		}

		if (File.Exists(cifpPath))
		{
			CIFP retval = new(File.ReadAllLines(cifpPath));

			if (Directory.Exists("cifp"))
				Directory.Delete("cifp", true);

			retval.Save(directory);
			File.Delete(cifpPath);
			return retval;
		}
		else if (Directory.Exists(outputPath))
			return new(
				JsonSerializer.Deserialize<GridMORA[]>(File.ReadAllText(Path.Combine(outputPath, "mora.json"))) ?? throw new Exception(),
				JsonSerializer.Deserialize<Airspace[]>(File.ReadAllText(Path.Combine(outputPath, "airspace.json"))) ?? throw new Exception(),
				new(JsonSerializer.Deserialize<Aerodrome[]>(File.ReadAllText(Path.Combine(outputPath, "aerodrome.json")))?.Select(a => new KeyValuePair<string, Aerodrome>(a.Identifier, a)) ?? throw new Exception()),
				JsonSerializer.Deserialize<Dictionary<string, HashSet<ICoordinate>>>(File.ReadAllText(Path.Combine(outputPath, "fix.json"))) ?? throw new Exception(),
				JsonSerializer.Deserialize<Dictionary<string, HashSet<Navaid>>>(File.ReadAllText(Path.Combine(outputPath, "navaid.json"))) ?? throw new Exception(),
				JsonSerializer.Deserialize<Dictionary<string, HashSet<Airway>>>(File.ReadAllText(Path.Combine(outputPath, "airway.json"))) ?? throw new Exception(),
				JsonSerializer.Deserialize<Dictionary<string, HashSet<Procedure>>>(File.ReadAllText(Path.Combine(outputPath, "procedure.json"))) ?? throw new Exception(),
				JsonSerializer.Deserialize<Dictionary<string, HashSet<Runway>>>(File.ReadAllText(Path.Combine(outputPath, "runway.json"))) ?? throw new Exception()
			);
		else
		{
			HttpClient cli = new();
			string pageListing = await cli.GetStringAsync(@"https://aeronav.faa.gov/Upload_313-d/cifp/");
#pragma warning disable IDE0079
#pragma warning disable SYSLIB1045 // Convert to 'GeneratedRegexAttribute'.
			Regex cifpZip = new(@"CIFP_\d+\.zip");
#pragma warning restore SYSLIB1045 // Convert to 'GeneratedRegexAttribute'.
			string currentCifp = cifpZip.Matches(pageListing).Last().Value;
			byte[] cifpDat = await cli.GetByteArrayAsync(@"https://aeronav.faa.gov/Upload_313-d/cifp/" + currentCifp);

			if (!Directory.Exists(Path.GetDirectoryName(zipPath)))
				Directory.CreateDirectory(Path.GetDirectoryName(zipPath) ?? directory);

			File.WriteAllBytes(zipPath, cifpDat);
			return await LoadAsync(directory);
		}
	}

	private void Save(string? directory = null)
	{
		directory ??= Environment.CurrentDirectory;
		string outputPath = Path.Combine(directory, "cifp");
		JsonSerializerOptions opts = new() { WriteIndented = true };

		Directory.CreateDirectory(outputPath);
		File.WriteAllText(Path.Combine(outputPath, "mora.json"), JsonSerializer.Serialize(MORAs, opts));
		File.WriteAllText(Path.Combine(outputPath, "airspace.json"), JsonSerializer.Serialize(Airspaces, opts));
		File.WriteAllText(Path.Combine(outputPath, "aerodrome.json"), JsonSerializer.Serialize(Aerodromes.Values.ToArray(), opts));
		File.WriteAllText(Path.Combine(outputPath, "fix.json"), JsonSerializer.Serialize(Fixes, opts));
		File.WriteAllText(Path.Combine(outputPath, "navaid.json"), JsonSerializer.Serialize(Navaids, opts));
		File.WriteAllText(Path.Combine(outputPath, "airway.json"), JsonSerializer.Serialize(Airways, opts));
		File.WriteAllText(Path.Combine(outputPath, "procedure.json"), JsonSerializer.Serialize(Procedures, opts));
		File.WriteAllText(Path.Combine(outputPath, "runway.json"), JsonSerializer.Serialize(Runways, opts));
	}

	public void SaveReduced(string? directory = null)
	{
		directory ??= Environment.CurrentDirectory;
		string outputPath = Path.Combine(directory, "cifp-reduced");
		JsonSerializerOptions opts = new() { WriteIndented = false };

		Dictionary<string, ControlledAirspace.AirspaceClass> classes = Airspaces.GroupBy(a => a.Center.Trim()).ToDictionary(
			g => g.Key,
			g => g.Min(a => a.Class)
		);

		HashSet<string> bravosAndCharlies = [.. classes.Where(kvp => kvp.Value is ControlledAirspace.AirspaceClass.B or ControlledAirspace.AirspaceClass.C).Select(kvp => kvp.Key)];

		Directory.CreateDirectory(outputPath);
		File.WriteAllText(Path.Combine(outputPath, "mora.json"), JsonSerializer.Serialize(MORAs, opts));
		File.WriteAllText(Path.Combine(outputPath, "airspace.json"), JsonSerializer.Serialize(Airspaces.Where(a => a.Class is ControlledAirspace.AirspaceClass.B or ControlledAirspace.AirspaceClass.C).ToArray(), opts));
		File.WriteAllText(Path.Combine(outputPath, "aerodrome.json"), JsonSerializer.Serialize(Aerodromes.Values.Where(a => bravosAndCharlies.Contains(a.Identifier)).ToArray(), opts));
		File.WriteAllText(Path.Combine(outputPath, "fix.json"), JsonSerializer.Serialize(Fixes, opts));
		File.WriteAllText(Path.Combine(outputPath, "navaid.json"), JsonSerializer.Serialize(Navaids, opts));
		File.WriteAllText(Path.Combine(outputPath, "airway.json"), JsonSerializer.Serialize(Airways, opts));
		File.WriteAllText(Path.Combine(outputPath, "procedure.json"), JsonSerializer.Serialize(Procedures.ToDictionary(p => p.Key, p => p.Value.Where(pc => bravosAndCharlies.Contains(pc.Airport ?? "ZZZZ")).ToHashSet()), opts));
		File.WriteAllText(Path.Combine(outputPath, "runway.json"), JsonSerializer.Serialize(Runways.ToDictionary(r => r.Key, r => r.Value.Where(rw => bravosAndCharlies.Contains(rw.Airport)).ToHashSet()), opts));
	}

	public CIFP(string[] cifpFileLines) : this()
	{
		List<GridMORA> moras = [];
		List<Airspace> airspaces = [];
		List<SIDLine> sidSteps = [];
		List<STARLine> starSteps = [];
		List<ApproachLine> iapSteps = [];
		List<AirwayFixLine> awLines = [];

		RecordLine[] rls = [.. cifpFileLines.SkipWhile(l => l.StartsWith("HDR")).AsParallel().AsOrdered().Select(l => { try { return RecordLine.Parse(l); } catch { return null; } }).Where(rl => rl is not null).Cast<RecordLine>()];

		for (int lineIndex = 0; lineIndex < rls.Length; ++lineIndex)
		{
			switch (rls[lineIndex])
			{
				case GridMORA mora:
					moras.Add(mora);
					break;

				case ControlledAirspace al:
					List<ControlledAirspace> segments = [al];

					while (rls[++lineIndex] is ControlledAirspace ca && ca.Center == al.Center && ca.MultiCD == al.MultiCD)
						segments.Add(ca);

					--lineIndex;

					airspaces.Add(new([.. segments]));
					break;

				case RestrictiveAirspace _:
					continue;

				case Navaid nav:
					if (!Navaids.ContainsKey(nav.Identifier))
						Navaids.Add(nav.Identifier, []);
					Navaids[nav.Identifier].Add(nav);

					if (!Fixes.ContainsKey(nav.Identifier))
						Fixes.Add(nav.Identifier, []);
					Fixes[nav.Identifier].Add(nav.Position);
					break;

				case SIDLine sl:
					sidSteps.Add(sl);

					while (rls[++lineIndex] is SIDLine s)
						sidSteps.Add(s);

					--lineIndex;
					break;

				case STARLine sl:
					starSteps.Add(sl);

					while (rls[++lineIndex] is STARLine s)
						starSteps.Add(s);

					--lineIndex;
					break;

				case ApproachLine al:
					iapSteps.Add(al);

					while (rls[++lineIndex] is ApproachLine s)
						iapSteps.Add(s);

					--lineIndex;
					break;

				case Waypoint wp:
					if (!Fixes.ContainsKey(wp.Identifier))
						Fixes.Add(wp.Identifier, []);
					Fixes[wp.Identifier].Add(wp.Position);
					break;

				case PathPoint pp:
					if (!Fixes.ContainsKey(pp.Runway))
						Fixes.Add(pp.Runway, []);
					if (!Fixes.ContainsKey(pp.Airport + "/" + pp.Runway))
						Fixes.Add(pp.Airport + "/" + pp.Runway, []);

					Fixes[pp.Runway].Add(pp.Position);
					Fixes[pp.Airport + "/" + pp.Runway].Add(pp.Position);
					break;

				case AirwayFixLine af:
					awLines.Add(af);

					for (int seqNum = af.SequenceNumber;
						rls[++lineIndex] is AirwayFixLine f && f.AirwayIdentifier == af.AirwayIdentifier && f.SequenceNumber > seqNum;
						seqNum = f.SequenceNumber)
						awLines.Add(f);

					--lineIndex;
					break;

				case Aerodrome a:
					if (a is Airport)
						Aerodromes.Add(a.Identifier, a);
					if (!Fixes.ContainsKey(a.Identifier))
						Fixes.Add(a.Identifier, []);
					Fixes[a.Identifier].Add(a.Location);
					break;

				case Runway r:
					if (!Fixes.ContainsKey("RW" + r.Identifier))
						Fixes.Add("RW" + r.Identifier, []);
					if (!Fixes.ContainsKey(r.Airport + "/" + r.Identifier))
						Fixes.Add(r.Airport + "/" + r.Identifier, []);
					Fixes["RW" + r.Identifier].Add(r.Endpoint);
					Fixes[r.Airport + "/" + r.Identifier].Add(r.Endpoint);

					if (!Runways.ContainsKey(r.Airport))
						Runways.Add(r.Airport, []);

					Runways[r.Airport].Add(r);
					break;

				case AirportMSA _:
					continue;

				case null:
					continue;

				default:
					throw new NotImplementedException();
			}
		}

		ConcurrentDictionary<string, HashSet<Procedure>> procs = [];

		Task.WaitAll(Task.Run(() => {
			string procName = string.Empty, procAp = string.Empty;
			List<AirwayFixLine> awAccumulator = [];
			foreach (AirwayFixLine afl in awLines)
			{
				if (procName != afl.AirwayIdentifier
				 || (awAccumulator.Count != 0 && awAccumulator.Last().SequenceNumber >= afl.SequenceNumber))
				{
					if (awAccumulator.Count != 0)
					{
						if (awAccumulator.Count > 1)
						{
							Airway aw = new(procName, [.. awAccumulator], Fixes);

							if (!Airways.ContainsKey(aw.Identifier))
								Airways.Add(aw.Identifier, []);

							Airways[aw.Identifier].Add(aw);
						}

						awAccumulator.Clear();
					}

					procName = afl.AirwayIdentifier;
				}

				awAccumulator.Add(afl);
			}

			if (awAccumulator.Count != 0)
			{
				Airway aw = new(procName, [.. awAccumulator], Fixes);

				if (!Airways.ContainsKey(aw.Identifier))
					Airways.Add(aw.Identifier, []);

				Airways[aw.Identifier].Add(aw);
			}
		}), Task.Run(() => {
			string procName = string.Empty, procAp = string.Empty;
			List<SIDLine> sidAccumulator = [];
			foreach (SIDLine sl in sidSteps)
			{
				if ((sl.Airport, sl.Name) != (procAp, procName))
				{
					if (sidAccumulator.Count != 0)
					{
						SID sid = new([.. sidAccumulator], Fixes, Navaids, Aerodromes);

						if (!procs.ContainsKey(sid.Name))
							procs.TryAdd(sid.Name, []);
						procs[sid.Name].Add(sid);

						sidAccumulator.Clear();
					}

					procAp = sl.Airport;
					procName = sl.Name;
				}
				sidAccumulator.Add(sl);
			}

			if (sidAccumulator.Count != 0)
			{
				SID sid = new([.. sidAccumulator], Fixes, Navaids, Aerodromes);

				if (!procs.ContainsKey(sid.Name))
					procs.TryAdd(sid.Name, []);
				procs[sid.Name].Add(sid);
			}
		}), Task.Run(() => {
			string procName = string.Empty, procAp = string.Empty;
			List<STARLine> starAccumulator = [];
			foreach (STARLine sl in starSteps)
			{
				if ((sl.Airport, sl.Name) != (procAp, procName))
				{
					if (starAccumulator.Count != 0)
					{
						STAR star = new([.. starAccumulator], Fixes, Navaids, Aerodromes);

						if (!procs.ContainsKey(star.Name))
							procs.TryAdd(star.Name, []);
						procs[star.Name].Add(star);

						starAccumulator.Clear();
					}

					procAp = sl.Airport;
					procName = sl.Name;
				}
				starAccumulator.Add(sl);
			}

			if (starAccumulator.Count != 0)
			{
				STAR star = new([.. starAccumulator], Fixes, Navaids, Aerodromes);

				if (!procs.ContainsKey(star.Name))
					procs.TryAdd(star.Name, []);
				procs[star.Name].Add(star);
			}
		}), Task.Run(() => {
			string procName = string.Empty, procAp = string.Empty;
			List<ApproachLine> iapAccumulator = [];
			foreach (ApproachLine al in iapSteps)
			{
				if ((al.Airport, al.Name) != (procAp, procName))
				{
					if (iapAccumulator.Count != 0)
					{
						Approach iap = new([.. iapAccumulator], Fixes, Navaids, Aerodromes);

						if (!procs.ContainsKey(iap.Name))
							procs.TryAdd(iap.Name, []);
						procs[iap.Name].Add(iap);

						iapAccumulator.Clear();
					}

					procAp = al.Airport;
					procName = al.Name;
				}
				iapAccumulator.Add(al);
			}

			if (iapAccumulator.Count != 0)
			{
				Approach iap = new([.. iapAccumulator], Fixes, Navaids, Aerodromes);

				if (!procs.ContainsKey(iap.Name))
					procs.TryAdd(iap.Name, []);
				procs[iap.Name].Add(iap);
			}
		}));

		Procedures = procs.ToDictionary();
		(MORAs, Airspaces) = ([.. moras], [.. airspaces]);
	}
}

public record RecordLine(string Client, string Header, int FileRecordNumber, int Cycle)
{
	public RecordLine() : this("", "", 0, 0) { }

	public static RecordLine? Parse(string line) =>
		line[4] switch {
			'A' or 'U' => AirspaceLine.Parse(line),
			'D' => Navaid.Parse(line),
			'E' => EnrouteLine.Parse(line),
			'P' or 'H' => Aerodrome.Parse(line),

			_ => null
		};

	[DebuggerStepThrough]
	protected static void Fail(int charPos) =>
		throw new FormatException($"Invalid record format; failed on character {charPos}.");

	[DebuggerStepThrough]
	protected static void Check(string line, Index from, Index to, params string[] expected)
	{
		if (!expected.Contains(line[from..to]))
			Fail(from.Value);
	}

	[DebuggerStepThrough]
	protected static void CheckEmpty(string line, Index from, Index to) =>
		Check(line, from, to, new string(' ', to.Value - from.Value));
}

[JsonConverter(typeof(UnresolvedRadialJsonConverter))]
public record UnresolvedRadial(UnresolvedWaypoint Station, MagneticCourse Bearing) : IProcedureEndpoint, IProcedureVia
{
	public TrueCourse GetTrueCourse(Coordinate position, Course currentCourse, TimeSpan refreshRate, bool onGround) => throw new Exception();
	public bool IsConditionReached(PathTermination termination, (Coordinate position, Altitude altitude, dynamic? reference) context, decimal tolerance) => throw new Exception();

	public Radial Resolve(Dictionary<string, HashSet<Navaid>> navaids, Coordinate? reference = null)
	{
		var station = Station.Resolve(navaids, reference);
		return new(station, Bearing.ToMagnetic(station.MagneticVariation));
	}

	public Radial Resolve(Dictionary<string, HashSet<Navaid>> navaids, UnresolvedWaypoint? reference = null)
	{
		var station = Station.Resolve(navaids, reference);
		return new(station, Bearing.ToMagnetic(station.MagneticVariation));
	}

	public class UnresolvedRadialJsonConverter : JsonConverter<UnresolvedRadial>
	{
		public override UnresolvedRadial Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
			throw new JsonException();

		public override void Write(Utf8JsonWriter writer, UnresolvedRadial value, JsonSerializerOptions options) =>
			throw new JsonException();
	}
}

public record Radial(Navaid Station, MagneticCourse Bearing) : IProcedureEndpoint, IProcedureVia
{
	private const decimal RADIAL_TRACKING_TOLERANCE = 0.5m; // Half a degree

	private decimal Magvar => Station.MagneticVariation ?? throw new Exception("Cannot fly radials of DME.");

	public TrueCourse GetTrueCourse(Coordinate position, Course currentCourse, TimeSpan refreshRate, bool onGround)
	{
		if (Station is null)
			throw new Exception("Cannot fly a floating radial.");

		(TrueCourse? currentbearing, decimal distance) = Station.Position.GetBearingDistance(position);
		MagneticCourse currentRadial = currentbearing?.ToMagnetic(Magvar) ?? new(0, Magvar);
		decimal radialError = Bearing.Angle(currentRadial);

		if (distance < 0.1m)
			return IProcedureVia.TurnTowards(currentCourse, Bearing, refreshRate, onGround);

		if (radialError + RADIAL_TRACKING_TOLERANCE < 0)
			return IProcedureVia.TurnTowards(currentCourse, Bearing + 45, refreshRate, onGround);
		else if (radialError - RADIAL_TRACKING_TOLERANCE > 0)
			return IProcedureVia.TurnTowards(currentCourse, Bearing - 45, refreshRate, onGround);
		else
			return IProcedureVia.TurnTowards(currentCourse, Bearing - radialError, refreshRate, onGround);
	}

	public bool IsConditionReached(PathTermination termination, (Coordinate position, Altitude altitude, dynamic? reference) context, decimal tolerance)
	{
		if (Station is null)
			throw new Exception("Cannot reach a floating radial.");

		if (termination.HasFlag(PathTermination.UntilCrossing))
		{
			TrueCourse contextBearing = Station.Position.GetBearingDistance(context.position).bearing ?? throw new ArgumentException("Reference shouldn't be on top of endpoint.");

			if (context.reference is Coordinate refC)
			{
				TrueCourse? refBearing = Station.Position.GetBearingDistance(refC).bearing;
				if (refBearing is null)
					return true;
				else
					return (refBearing < Bearing.ToTrue()) ^ (contextBearing < Bearing.ToTrue());
			}
			else
				return Math.Abs(Bearing.Angle(contextBearing)) <= RADIAL_TRACKING_TOLERANCE;
		}
		else
			throw new NotImplementedException();
	}

	public Coordinate? GetIntersectionPoint(ICoordinate otherPoint, Course otherBearing)
	{
		if (Bearing.ToTrue().Degrees == otherBearing.ToTrue().Degrees)
			return null;

		Coordinate here = Station.Position.GetCoordinate(),
				   there = otherPoint.GetCoordinate(),
				   checkPoint = there;

		TrueCourse radial = Bearing.ToTrue(),
				   otherRadial = otherBearing.ToTrue();

		decimal distance = here.DistanceTo(there);
		bool startPositive = here.GetBearingDistance(there).bearing!.ToTrue().Degrees - radial.Degrees >= 0;

		while (Math.Abs(here.GetBearingDistance(checkPoint).bearing!.ToTrue().Degrees - radial.Degrees) > 0.5m)
		{
			decimal error = here.GetBearingDistance(checkPoint).bearing!.ToTrue().Degrees - radial.Degrees;
			distance += Math.Abs(error) / 10 * (startPositive ^ (error >= 0) ? -1 : 1);

			checkPoint = there.FixRadialDistance(otherRadial, distance);
		}

		return checkPoint;
	}
}

[JsonConverter(typeof(UnresolvedDistanceJsonConverter))]
public record UnresolvedDistance(UnresolvedWaypoint Point, decimal NMI) : IProcedureEndpoint
{
	public bool IsConditionReached(PathTermination termination, (Coordinate position, Altitude altitude, dynamic? reference) context, decimal tolerance) =>
		throw new Exception("Resolve this endpoint first.");

	public Distance Resolve(Dictionary<string, HashSet<ICoordinate>> fixes, Coordinate? reference = null) => new(Point.Resolve(fixes, reference), NMI);
	public Distance Resolve(Dictionary<string, HashSet<ICoordinate>> fixes, UnresolvedWaypoint? reference = null) => new(Point.Resolve(fixes, reference), NMI);

	public class UnresolvedDistanceJsonConverter : JsonConverter<UnresolvedDistance>
	{
		public override UnresolvedDistance Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
			throw new JsonException();

		public override void Write(Utf8JsonWriter writer, UnresolvedDistance value, JsonSerializerOptions options) =>
			throw new JsonException();
	}
}

[JsonConverter(typeof(UnresolvedFixRadialDistanceJsonConverter))]
public record UnresolvedFixRadialDistance(UnresolvedWaypoint Reference, MagneticCourse Bearing, decimal NMI) : IProcedureEndpoint
{
	public bool IsConditionReached(PathTermination termination, (Coordinate position, Altitude altitude, dynamic? reference) context, decimal tolerance) =>
		throw new Exception("Resolve this endpoint first.");

	public Coordinate Resolve(Dictionary<string, HashSet<ICoordinate>> fixes, Dictionary<string, HashSet<Navaid>> navaids, Coordinate? reference = null)
	{
		Coordinate fix = Reference.Resolve(fixes, reference).GetCoordinate();
		return fix.FixRadialDistance(Bearing.Resolve(navaids.GetLocalMagneticVariation(fix).Variation), NMI);
	}

	public Coordinate Resolve(Dictionary<string, HashSet<ICoordinate>> fixes, Dictionary<string, HashSet<Navaid>> navaids, UnresolvedWaypoint? reference)
	{
		Coordinate fix = Reference.Resolve(fixes, reference).GetCoordinate();
		return fix.FixRadialDistance(Bearing.Resolve(navaids.GetLocalMagneticVariation(fix).Variation), NMI);
	}

	public class UnresolvedFixRadialDistanceJsonConverter : JsonConverter<UnresolvedFixRadialDistance>
	{
		public override UnresolvedFixRadialDistance Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
			throw new JsonException();

		public override void Write(Utf8JsonWriter writer, UnresolvedFixRadialDistance value, JsonSerializerOptions options) =>
			throw new JsonException();
	}
}

public record Distance(ICoordinate? Point, decimal NMI) : IProcedureEndpoint
{
	public bool IsConditionReached(PathTermination termination, (Coordinate position, Altitude altitude, dynamic? reference) context, decimal tolerance) =>
		(Point ?? (ICoordinate)context.reference!).GetCoordinate().DistanceTo(context.position) >= NMI;
}

[JsonConverter(typeof(RacetrackJsonConverter))]
public record Racetrack(ICoordinate? Point, UnresolvedWaypoint? Waypoint, Course InboundCourse, decimal? Distance, TimeSpan? Time, bool LeftTurns = false) : IProcedureVia
{
	private const decimal FIX_CROSSING_MAX_ERROR = 0.1m;

	private HoldState? state = null;
	private EntryType? entry = null;
	private Coordinate? abeamPoint = null;
	private DateTime? abeamTime = null;
	private bool stable = true;

	public TrueCourse GetTrueCourse(Coordinate position, Course currentCourse, TimeSpan refreshRate, bool onGround)
	{
		if (Distance is null && Time is null)
			throw new Exception("Racetrack must have a distance or time defined.");
		if (Point is null)
			throw new Exception("Cannot fly a floating racetrack.");

		if (state is null)
		{
			state = HoldState.Entry;
			entry = (LeftTurns, -currentCourse.Angle(InboundCourse)) switch {
				(false, >= -70 and <= 110) or
				(true, <= 70 and >= -110) => EntryType.Direct,
				(false, < -70) or
				(true, > 70) => EntryType.Parallel,
				(false, > 110) or
				(true, < -110) => EntryType.Teardrop
			};

			stable = true;
		}

		(TrueCourse? fixBearing, decimal distance) = position.GetCoordinate().GetBearingDistance(Point.GetCoordinate());
		switch (state.Value)
		{
			case HoldState.Entry:
				if (distance < FIX_CROSSING_MAX_ERROR)
				{
					state = HoldState.Outbound;
					stable = false;
				}

				return IProcedureVia.TurnTowards(currentCourse, fixBearing ?? currentCourse, refreshRate, onGround, stable ? null : LeftTurns);

			case HoldState.Inbound:
				if (!stable && Math.Abs(currentCourse.Angle(InboundCourse)) < 1)
					stable = true;

				if (distance < FIX_CROSSING_MAX_ERROR)
				{
					state = HoldState.Outbound;
					abeamPoint = null;
					abeamTime = null;
					entry = null;
					stable = false;
				}

				return IProcedureVia.TurnTowards(currentCourse, fixBearing ?? InboundCourse, refreshRate, onGround, stable ? null : LeftTurns);

			case HoldState.Outbound:
				switch (entry)
				{
					case EntryType.Direct:
						entry = null;
						goto case null;

					case EntryType.Parallel:
						abeamTime ??= DateTime.UtcNow;

						if ((DateTime.UtcNow - abeamTime.Value).TotalMinutes < 1)
						{
							stable = true;
							return IProcedureVia.TurnTowards(currentCourse, InboundCourse.Reciprocal, refreshRate, onGround);
						}

						abeamTime = null;
						stable = false;
						state = HoldState.Inbound;
						return GetTrueCourse(position, currentCourse, refreshRate, onGround);

					case EntryType.Teardrop:
						abeamTime ??= DateTime.UtcNow;

						if ((DateTime.UtcNow - abeamTime.Value).TotalMinutes < 1)
						{
							stable = true;
							return IProcedureVia.TurnTowards(currentCourse, InboundCourse.Reciprocal + (LeftTurns ? 30 : -30), refreshRate, onGround);
						}

						abeamTime = null;
						stable = false;
						state = HoldState.Inbound;
						return GetTrueCourse(position, currentCourse, refreshRate, onGround);

					case null:
						if (!stable && Math.Abs(currentCourse.Angle(InboundCourse.Reciprocal)) < 1)
						{
							abeamPoint = position;
							abeamTime = DateTime.UtcNow;
							stable = true;
						}

						if (!stable)
							return IProcedureVia.TurnTowards(currentCourse, InboundCourse.Reciprocal, refreshRate, onGround, LeftTurns);

						if ((Distance is not null && abeamPoint!.Value.DistanceTo(position) >= Distance)
						 || (Time is not null && DateTime.UtcNow - abeamTime!.Value >= Time))
						{
							abeamPoint = null;
							abeamTime = null;
							stable = false;
							state = HoldState.Inbound;
							return GetTrueCourse(position, currentCourse, refreshRate, onGround);
						}
						else
							return IProcedureVia.TurnTowards(currentCourse, InboundCourse.Reciprocal, refreshRate, onGround);

					default:
						throw new Exception("Unreachable");
				}

			default:
				throw new Exception("Unreachable");
		}
	}

	private enum HoldState
	{
		Entry,
		Inbound,
		Outbound
	}

	private enum EntryType
	{
		Direct,
		Parallel,
		Teardrop
	}

	public class RacetrackJsonConverter : JsonConverter<Racetrack>
	{
		public override Racetrack? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			if (reader.TokenType != JsonTokenType.StartObject)
				throw new JsonException();

			ICoordinate? point = null;
			Course? inboundCourse = null;
			decimal? distance = null;
			TimeSpan? time = null;
			bool left = false;

			while (reader.Read())
			{
				if (reader.TokenType == JsonTokenType.EndObject)
					break;

				string prop = reader.GetString() ?? throw new JsonException();
				reader.Read();
				switch (prop)
				{
					case "Point":
						point = JsonSerializer.Deserialize<ICoordinate>(ref reader, options);
						break;

					case "InboundCourse":
						inboundCourse = JsonSerializer.Deserialize<Course>(ref reader, options);
						break;

					case "Leg" when reader.TokenType == JsonTokenType.Number:
						distance = reader.GetDecimal();
						break;

					case "Leg":
						time = JsonSerializer.Deserialize<TimeSpan>(ref reader, options);
						break;

					case "LeftTurns":
						left = reader.GetBoolean();
						break;

					default:
						throw new JsonException();
				}
			}

			if (reader.TokenType != JsonTokenType.EndObject)
				throw new JsonException();

			if (point is null || inboundCourse is null || (distance is null && time is null))
				throw new JsonException();
			return new(point, null, inboundCourse, distance, time, left);
		}

		public override void Write(Utf8JsonWriter writer, Racetrack value, JsonSerializerOptions options)
		{
			if (value.Point is null)
				throw new ArgumentNullException(nameof(value));

			writer.WriteStartObject();
			writer.WritePropertyName("Point"); JsonSerializer.Serialize(writer, value.Point, options);
			writer.WritePropertyName("InboundCourse"); JsonSerializer.Serialize(writer, value.InboundCourse, options);
			writer.WritePropertyName("Leg");
			if (value.Distance is decimal d)
				writer.WriteNumberValue(d);
			else if (value.Time is TimeSpan t)
				JsonSerializer.Serialize(writer, t, options);
			else
				throw new ArgumentException("Racetrack must have leg distance or time.", nameof(value));

			if (value.LeftTurns)
				writer.WriteBoolean("LeftTurns", true);
			writer.WriteEndObject();
		}
	}
}

[JsonConverter(typeof(ArcJsonConverter))]
public record Arc(ICoordinate? Centerpoint, UnresolvedWaypoint? Centerwaypoint, decimal Radius, MagneticCourse ArcTo) : IProcedureVia
{
	private const decimal ARC_RADIUS_TOLERANCE = 0.1m;

	public TrueCourse GetTrueCourse(Coordinate position, Course currentCourse, TimeSpan refreshRate, bool onGround)
	{
		if (Centerpoint is null)
			throw new Exception("Cannot fly a floating arc.");
		if (Radius <= 0)
			throw new Exception("Cannot fly an arc with 0 radius.");

		(TrueCourse? bearing, decimal distance) = Centerpoint.GetCoordinate().GetBearingDistance(position);


		if (bearing is null || distance + ARC_RADIUS_TOLERANCE < Radius)
			return IProcedureVia.TurnTowards(currentCourse, bearing ?? ArcTo.ToTrue(), refreshRate, onGround);
		else if (distance - ARC_RADIUS_TOLERANCE > Radius)
			return IProcedureVia.TurnTowards(currentCourse, bearing.Reciprocal, refreshRate, onGround);

		Course targetBearing =
			bearing.Angle(ArcTo) > 0
			? bearing + 90  // Clockwise
			: bearing - 90; // Anticlockwise

		return IProcedureVia.TurnTowards(currentCourse, targetBearing, refreshRate, onGround);
	}

	public class ArcJsonConverter : JsonConverter<Arc>
	{
		public override Arc? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			if (reader.TokenType != JsonTokenType.StartObject)
				throw new JsonException();

			ICoordinate? centerpoint = null;
			MagneticCourse? arcTo = null;
			decimal? radius = null;

			while (reader.Read())
			{
				if (reader.TokenType == JsonTokenType.EndObject)
					break;

				string prop = reader.GetString() ?? throw new JsonException();
				reader.Read();
				switch (prop)
				{
					case "Centerpoint":
						centerpoint = JsonSerializer.Deserialize<ICoordinate>(ref reader, options);
						break;

					case "Radius":
						radius = reader.GetDecimal();
						break;

					case "ArcTo":
						arcTo = JsonSerializer.Deserialize<Course>(ref reader, options)?.ToMagnetic(null);
						break;

					default:
						throw new JsonException();
				}
			}

			if (reader.TokenType != JsonTokenType.EndObject)
				throw new JsonException();

			if (centerpoint is null || radius is null || arcTo is null || arcTo.Variation is null)
				throw new JsonException();

			return new(centerpoint, null, radius!.Value, arcTo!);
		}

		public override void Write(Utf8JsonWriter writer, Arc value, JsonSerializerOptions options)
		{
			if (value.Centerpoint is null || value.ArcTo.Variation is null)
				throw new ArgumentNullException(nameof(value));

			writer.WriteStartObject();
			writer.WritePropertyName("Centerpoint"); JsonSerializer.Serialize(writer, value.Centerpoint, options);
			writer.WritePropertyName("Radius"); writer.WriteNumberValue(value.Radius);
			writer.WritePropertyName("ArcTo"); JsonSerializer.Serialize<Course>(writer, value.ArcTo, options);
			writer.WriteEndObject();
		}
	}
}

[JsonConverter(typeof(AltitudeRestrictionJsonConverter))]
public record AltitudeRestriction(Altitude? Minimum, Altitude? Maximum)
{
	public static AltitudeRestriction Unrestricted => new(null, null);
	public bool IsUnrestricted => Minimum is null && Maximum is null;

	public static AltitudeRestriction FromDescription(AltitudeDescription description, Altitude? alt1, Altitude? alt2)
	{
		if ((alt1 ?? alt2) is null)
			return Unrestricted;
		else ArgumentNullException.ThrowIfNull(alt1);

		if ((char)description == 'I')
			// Intercept altitude given to be nice; ignore it.
			alt2 = null;
		else if ((char)description == 'G')
			// Restriction is above glideslope, so it's called out here as a warning.
			// Most of the time they're actually the same, just the ILS08 into KBUR is higher.
			alt2 = null;
		else if ("JHV".Contains((char)description) && alt2 is null)
			// KCOS & KILM have typoes in the procedures where a J or H is used instead of a +.
			description = AltitudeDescription.AtOrAbove;
		else if ((char)description == 'X' && alt1 == alt2)
			alt2 = null;

		description = (char)description switch {
			' ' or 'X' => AltitudeDescription.At,
			'J' or 'H' or 'V' => AltitudeDescription.Between,
			'I' or 'G' => AltitudeDescription.At,

			_ => description
		};

		if (description == AltitudeDescription.AtOrAbove && alt2 is not null)
		{
			// A couple of strange procedures here. Likely irregularities, though this includes one into KDTW.

			(description, alt1, alt2) = (alt1, alt2) switch {
				(Altitude a, Altitude b) when a == b => (AltitudeDescription.AtOrAbove, a, null),
				(Altitude a, Altitude b) when a < b => (AltitudeDescription.Between, a, b),
				(Altitude a, Altitude b) when a > b => (AltitudeDescription.AtOrAbove, a, null),
				_ => throw new Exception("Unreachable")
			};
		}
		else if (description == AltitudeDescription.AtOrBelow && alt2 is not null && alt2 > alt1)
			// Curse you KMEM!
			description = AltitudeDescription.Between;

		if (description == AltitudeDescription.Between && alt2 is null)
			throw new ArgumentNullException(nameof(alt2), "Between altitude restrictions need two altitudes.");
		else if (description != AltitudeDescription.Between && alt2 is not null)
			throw new ArgumentOutOfRangeException(nameof(alt2), "Single altitude restrictions should not be passed two altitudes.");

		return description switch {
			AltitudeDescription.Between when alt1.Feet > alt2?.Feet => new(alt2, alt1),
			AltitudeDescription.Between => new(alt1, alt2),
			AltitudeDescription.At => new(alt1, alt1),

			AltitudeDescription.AtOrAbove => new(alt1, null),
			AltitudeDescription.AtOrBelow => new(null, alt1),

			_ => throw new ArgumentOutOfRangeException(nameof(description), "Provided altitude description is unknown.")
		};
	}

	public bool IsInRange(Altitude altitude) =>
		(Minimum ?? Altitude.MinValue) <= altitude && (Maximum ?? Altitude.MaxValue) >= altitude;

	public enum AltitudeDescription
	{
		AtOrAbove = '+',
		AtOrBelow = '-',
		At = '@',
		Between = 'B'
	}

	public override string ToString()
	{
		string retval = "";
		if (Minimum is not null)
			retval += @$"MIN {Minimum.Feet / 100:000} ";
		if (Maximum is not null)
			retval += $@"MAX {Maximum.Feet / 100:000}";

		retval = retval.Trim();

		if (Minimum is not null && Minimum == Maximum)
			retval = $@"{Minimum.Feet / 100:000}";

		return string.IsNullOrWhiteSpace(retval) ? "Unrestricted" : retval;
	}

	public class AltitudeRestrictionJsonConverter : JsonConverter<AltitudeRestriction>
	{
		public override AltitudeRestriction? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			if (reader.TokenType == JsonTokenType.Null && reader.Read())
				return Unrestricted;
			else if (reader.TokenType != JsonTokenType.StartArray || !reader.Read())
				throw new JsonException();

			Altitude? minimum = reader.TokenType == JsonTokenType.Null ? null : JsonSerializer.Deserialize<Altitude>(ref reader, options);

			if (!reader.Read())
				throw new JsonException();

			Altitude? maximum = reader.TokenType == JsonTokenType.Null ? null : JsonSerializer.Deserialize<Altitude>(ref reader, options);

			if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray)
				throw new JsonException();

			return new(minimum, maximum);
		}

		public override void Write(Utf8JsonWriter writer, AltitudeRestriction value, JsonSerializerOptions options)
		{
			if (value.IsUnrestricted)
				writer.WriteNullValue();
			else
			{
				writer.WriteStartArray();

				if (value.Minimum is Altitude min)
					JsonSerializer.Serialize(writer, min, options);
				else
					writer.WriteNullValue();

				if (value.Maximum is Altitude max)
					JsonSerializer.Serialize(writer, max, options);
				else
					writer.WriteNullValue();

				writer.WriteEndArray();
			}
		}
	}
}

[JsonConverter(typeof(SpeedRestrictionJsonConverter))]
public record SpeedRestriction(uint? Minimum, uint? Maximum)
{
	public static SpeedRestriction Unrestricted => new(null, null);
	public bool IsUnrestricted => Minimum is null && Maximum is null;

	public bool IsInRange(uint speed) =>
		(Minimum ?? uint.MinValue) <= speed && (Maximum ?? uint.MaxValue) >= speed;

	public override string ToString()
	{
		string retval = "";
		if (Minimum is not null)
			retval += $"MIN {Minimum}K ";
		if (Maximum is not null)
			retval += $"MAX {Maximum}K";
		retval = retval.Trim();

		if (Minimum is not null && Minimum == Maximum)
			retval = $"AT {Minimum}K";

		return string.IsNullOrWhiteSpace(retval) ? "Unrestricted" : retval;
	}

	public class SpeedRestrictionJsonConverter : JsonConverter<SpeedRestriction>
	{
		public override SpeedRestriction? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			if (reader.TokenType == JsonTokenType.Null && reader.Read())
				return Unrestricted;
			else if (reader.TokenType != JsonTokenType.StartArray || !reader.Read())
				throw new JsonException();

			uint? minimum = reader.TokenType == JsonTokenType.Null ? null : reader.TokenType == JsonTokenType.Number ? reader.GetUInt32() : throw new JsonException();

			if (!reader.Read())
				throw new JsonException();

			uint? maximum = reader.TokenType == JsonTokenType.Null ? null : reader.TokenType == JsonTokenType.Number ? reader.GetUInt32() : throw new JsonException();

			if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray)
				throw new JsonException();

			return new(minimum, maximum);
		}

		public override void Write(Utf8JsonWriter writer, SpeedRestriction value, JsonSerializerOptions options)
		{
			if (value.IsUnrestricted)
				writer.WriteNullValue();
			else
			{
				writer.WriteStartArray();

				if (value.Minimum is uint min)
					writer.WriteNumberValue(min);
				else
					writer.WriteNullValue();

				if (value.Maximum is uint max)
					writer.WriteNumberValue(max);
				else
					writer.WriteNullValue();

				writer.WriteEndArray();
			}
		}
	}
}

[JsonConverter(typeof(UnresolvedWaypointJsonConverter))]
public class UnresolvedWaypoint(string name) : IProcedureEndpoint
{
	public string Name { get; init; } = name;
	protected Coordinate? Position { get; init; }

	public NamedCoordinate Resolve(Dictionary<string, HashSet<ICoordinate>> fixes) => Resolve(fixes, new Coordinate(0, 0));
	public NamedCoordinate Resolve(Dictionary<string, HashSet<ICoordinate>> fixes, Coordinate? reference = null) =>
		Position?.Name(Name) ?? fixes.Concretize(Name, refCoord: reference);
	public NamedCoordinate Resolve(Dictionary<string, HashSet<ICoordinate>> fixes, UnresolvedWaypoint? reference = null) =>
		Position?.Name(Name) ?? fixes.Concretize(Name, refString: reference?.Name);

	public Navaid Resolve(Dictionary<string, HashSet<Navaid>> fixes, Coordinate? reference = null) =>
		fixes.Concretize(Name, refCoord: reference ?? Position);
	public Navaid Resolve(Dictionary<string, HashSet<Navaid>> fixes, UnresolvedWaypoint? reference = null) =>
		fixes.Concretize(Name, refString: reference?.Name);

	public bool TryResolve(Dictionary<string, HashSet<ICoordinate>> fixes, [NotNullWhen(true)] out NamedCoordinate? coord) => TryResolve(fixes, out coord, new Coordinate(0, 0));
	public bool TryResolve(Dictionary<string, HashSet<ICoordinate>> fixes, [NotNullWhen(true)] out NamedCoordinate? coord, Coordinate? reference = null)
	{
		if (Position?.Name(Name) is NamedCoordinate nc)
		{
			coord = nc;
			return true;
		}

		return fixes.TryConcretize(Name, out coord, refCoord: reference);
	}

	public bool TryResolve(Dictionary<string, HashSet<ICoordinate>> fixes, [NotNullWhen(true)] out NamedCoordinate? coord, UnresolvedWaypoint? reference = null)
	{
		if (Position?.Name(Name) is NamedCoordinate nc)
		{
			coord = nc;
			return true;
		}

		return fixes.TryConcretize(Name, out coord, refString: reference?.Name);
	}

	public bool IsConditionReached(PathTermination termination, (Coordinate position, Altitude altitude, dynamic? reference) context, decimal tolerance) =>
		throw new Exception("Waypoint must be resolved.");

	public override string ToString() => Name;

	public class UnresolvedWaypointJsonConverter : JsonConverter<UnresolvedWaypoint>
	{
		public override UnresolvedWaypoint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
			throw new JsonException();

		public override void Write(Utf8JsonWriter writer, UnresolvedWaypoint value, JsonSerializerOptions options) =>
			throw new JsonException();
	}
}

public static class Extensions
{
	public static bool TryConcretize(this Dictionary<string, HashSet<ICoordinate>> fixes, string wp, [NotNullWhen(true)] out NamedCoordinate? coord, Coordinate? refCoord = null, string? refString = null)
	{
		if (!fixes.TryGetValue(wp, out HashSet<ICoordinate>? value))
		{
			coord = null;
			return false;
		}

		if (value.Count == 1)
		{
			coord = value.Single() switch {
				NamedCoordinate nc => nc,
				Coordinate c => new(wp, c),
				_ => null
			};

			return coord is not null;
		}

		if (refCoord is not null)
		{
			coord = value.MinBy(wp => wp.GetCoordinate().DistanceTo(refCoord.Value)) switch {
				NamedCoordinate nc => nc,
				Coordinate c => new(wp, c),
				_ => null
			};

			return coord is not null;
		}
		else if (refString is not null)
		{
			if (!fixes.TryGetValue(refString, out HashSet<ICoordinate>? fix))
				throw new ArgumentException($"Unknown waypoint {refString}.", nameof(refString));

			coord = value.MinBy(wp => fix.Min(rwp => wp.GetCoordinate().DistanceTo(rwp.GetCoordinate()))) switch {
				NamedCoordinate nc => nc,
				Coordinate c => new(wp, c),
				_ => null
			};

			return coord is not null;
		}

		coord = null;
		return false;
	}


	public static NamedCoordinate Concretize(this Dictionary<string, HashSet<ICoordinate>> fixes, string wp, Coordinate? refCoord = null, string? refString = null)
	{
		if (TryConcretize(fixes, wp, out var res, refCoord, refString))
			return res.Value;
		else
			throw new Exception($"Unknown waypoint {wp}.");
	}

	public static Navaid Concretize(this Dictionary<string, HashSet<Navaid>> navaids, string wp, Coordinate? refCoord = null, string? refString = null)
	{
		if (!navaids.TryGetValue(wp, out HashSet<Navaid>? value))
			throw new ArgumentException($"Unknown waypoint {wp}.", nameof(wp));

		if (value.Count == 1)
			return value.Single();

		if (refCoord is not null)
			return value.MinBy(wp => wp.Position.DistanceTo(refCoord.Value))!;
		else if (refString is not null)
		{
			if (!navaids.TryGetValue(refString, out HashSet<Navaid>? navaid))
				throw new ArgumentException($"Unknown waypoint {refString}.", nameof(refString));

			return value.MinBy(wp => navaid.Min(rwp => wp.Position.DistanceTo(rwp.Position)))!;
		}
		else
			throw new Exception($"Could not resolve waypoint {wp} without context.");
	}

	public static (Coordinate Reference, decimal Variation) GetLocalMagneticVariation(this Dictionary<string, HashSet<Navaid>> navaids, Coordinate refCoord) =>
		navaids.Values.SelectMany(ns => ns).OrderBy(na => refCoord.DistanceTo(na.Position)).Where(n => n.MagneticVariation is not null).Select(n => (n.Position, n.MagneticVariation!.Value)).First();

	public static (ICoordinate Reference, decimal Variation) GetLocalMagneticVariation(this Dictionary<string, Aerodrome> aerodromes, Coordinate refCoord) =>
		aerodromes.Values.OrderBy(a => refCoord.DistanceTo(a.Location.GetCoordinate())).First() switch {
			Airport ap => (ap.Location, ap.MagneticVariation),
			Heliport hp => (hp.Location, hp.MagneticVariation),
			_ => throw new NotImplementedException()
		};
}
