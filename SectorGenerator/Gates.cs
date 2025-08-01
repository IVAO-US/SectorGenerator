﻿using WSleeman.Osm;

namespace SectorGenerator;

internal class Gates(string airport, Osm taxiways)
{
	private readonly Osm _osm = taxiways;

	public string Labels => string.Join(
		"\r\n",
		_osm.WaysAndBoundaries().Where(w => w.Tags.ContainsKey("ref")).SelectMany(Taxiways.SpacePointsOnWay)
		.Concat(_osm.Nodes.Values.Where(n => n.Tags.ContainsKey("ref")).Select(n => (Label: n["ref"] ?? "", n.Latitude, n.Longitude)))
		.Select(l => $"{l.Label};{airport};{l.Latitude:00.0######};{l.Longitude:000.0######};")
	);

	public string Routes => string.Join(
		"\r\n",
		_osm.WaysAndBoundaries().Select(w => string.Join(
			"\r\n",
			w.Nodes[..^1].Zip(w.Nodes[1..]).Select(n => $"{n.First.Latitude:00.0######};{n.First.Longitude:000.0######};{n.Second.Latitude:00.0######};{n.Second.Longitude:000.0######};PIER;")
		))
	);
}
