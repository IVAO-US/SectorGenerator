using CIFPReader;

namespace SectorGenerator;

internal class SectorTweaks
{
	public SectorTweaks(string tweaksFolder, CIFP cifp)
	{
		if (!Directory.Exists(tweaksFolder))
			return;

		foreach (string file in Directory.EnumerateFiles(tweaksFolder, "*.tweaks", SearchOption.AllDirectories))
			ProcessTweaks([..File.ReadAllLines(file).Select(l => l.Split("//")[0].TrimEnd()).Where(l => !string.IsNullOrWhiteSpace(l))]);
	}

	void ProcessTweaks(string[] lines)
	{
		for (int lineNum = 0; lineNum < lines.Length; ++lineNum)
		{
			if (lines[lineNum][^1] != ':' || lines[lineNum].Any(char.IsWhiteSpace))
				continue;

			lineNum = lines[lineNum][..^1].ToLowerInvariant() switch {
				"sids" => ProcessSids(lines, lineNum + 1),
				"stars" => ProcessStars(lines, lineNum + 1),
				"iaps" or "approachs" or "apps" => ProcessIaps(lines, lineNum + 1),
				_ => lineNum + 1
			};
		}
	}

	int ProcessSids(string[] lines, int lineNum)
	{
		int indent = lines[lineNum].TakeWhile(char.IsWhiteSpace).Count();
		int localIndent;

		while ((localIndent = lines[lineNum].TakeWhile(char.IsWhiteSpace).Count()) >= indent)
		{
			string header = lines[lineNum++].TrimStart();
			string[] body = [];
			if (header.EndsWith(':'))
				(header, body) = (header[..^1], [.. lines[lineNum..].TakeWhile(l => l.TakeWhile(char.IsWhiteSpace).Count() > localIndent)]);

			switch (header.Split()[0].ToLowerInvariant())
			{
				case "add":
					SID sid = new();
					break;
			}
		}

		throw new NotImplementedException();
	}

	int ProcessStars(string[] lines, int lineNum)
	{
		int indent = lines[lineNum].TakeWhile(char.IsWhiteSpace).Count();
		throw new NotImplementedException();
	}

	int ProcessIaps(string[] lines, int lineNum)
	{
		int indent = lines[lineNum].TakeWhile(char.IsWhiteSpace).Count();
		throw new NotImplementedException();
	}
}
