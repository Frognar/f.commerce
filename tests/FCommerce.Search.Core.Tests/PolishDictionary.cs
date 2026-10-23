using System.IO.Compression;
using System.Text;
using FCommerce.Search.Core.Analysis.Hunspell;

namespace FCommerce.Search.Core.Tests;

/// <summary>The real Polish dictionary, parsed once per test run.</summary>
internal static class PolishDictionary
{
    private static readonly Lazy<string> Affix = new(() => Read("pl_PL.aff.gz"));
    private static readonly Lazy<string> Dictionary = new(() => Read("pl_PL.dic.gz"));
    private static readonly Lazy<HunspellLemmatizer> Lemmatizer = new(() => HunspellLemmatizer.Parse(Affix.Value, Dictionary.Value));

    public static HunspellLemmatizer Instance => Lemmatizer.Value;

    public static string AffixText => Affix.Value;

    public static string DictionaryText => Dictionary.Value;

    private static string Read(string name)
    {
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Dictionaries", name));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
