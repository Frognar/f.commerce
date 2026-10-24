using System.IO.Compression;
using System.Text;
using FCommerce.Search.Core.Analysis;
using FCommerce.Search.Core.Analysis.Hunspell;

namespace FCommerce.Infrastructure.Search;

/// <summary>Analyzers by language; the Polish dictionary is embedded in this assembly and loaded on first use.</summary>
internal static class Analyzers
{
    private static readonly Lazy<Analyzer> PolishAnalyzer = new(() =>
        Analyzer.Polish(HunspellLemmatizer.Parse(ReadResource("Dictionaries.pl_PL.aff.gz"), ReadResource("Dictionaries.pl_PL.dic.gz"))));

    private static readonly Analyzer EnglishAnalyzer = Analyzer.English();

    public static Analyzer For(string language) =>
        language.Split('-')[0].ToLowerInvariant() switch
        {
            "pl" => PolishAnalyzer.Value,
            "en" => EnglishAnalyzer,
            var other => Analyzer.Simple(other),
        };

    private static string ReadResource(string name)
    {
        using var resource = typeof(Analyzers).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource '{name}' is missing.");
        using var gzip = new GZipStream(resource, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
