using FCommerce.Search.Core.Analysis;

namespace FCommerce.Search.Core.Tests;

public sealed class FoldingAndTokenizingTests
{
    [Theory]
    [InlineData("Łożysko", "lozysko")]
    [InlineData("ŚRUBA żółta", "sruba zolta")]
    [InlineData("Straße Ø10 Œuvre", "strasse o10 oeuvre")]
    public void Folds_case_and_diacritics(string input, string expected)
    {
        Assert.Equal(expected, Folding.Fold(input));
    }

    [Fact]
    public void Splits_on_everything_but_letters_and_digits()
    {
        Assert.Equal(["sr", "m8x40", "a2", "o10"], Tokenizer.Tokenize("SR-M8x40-A2, Ø10!").Select(t => t.Text));
        Assert.Empty(Tokenizer.Tokenize("  --  "));
    }
}

public sealed class PolishLemmatizerTests
{
    [Theory]
    [InlineData("śruby", "sruba")]
    [InlineData("śrubą", "sruba")]
    [InlineData("srubami", "sruba")]
    [InlineData("wierteł", "wiertlo")]
    [InlineData("wiertla", "wiertlo")]
    [InlineData("wiertłami", "wiertlo")]
    [InlineData("kołki", "kolek")]
    [InlineData("kolka", "kolek")]
    [InlineData("łożyska", "lozysko")]
    [InlineData("nakrętek", "nakretka")]
    [InlineData("opasek", "opaska")]
    [InlineData("podkładkami", "podkladka")]
    [InlineData("kluczy", "klucz")]
    [InlineData("rękawice", "rekawica")]
    [InlineData("ocynkowanych", "ocynkowac")] // participle → verb, consistently for index and query
    [InlineData("nierdzewnej", "nierdzewny")] // negation is kept, see below
    public void Reduces_inflected_forms_typed_with_or_without_diacritics(string word, string lemma)
    {
        Assert.Contains(lemma, PolishDictionary.Instance.Lemmas(Folding.Fold(word)));
    }

    [Fact]
    public void Negating_prefix_stays_part_of_the_lemma()
    {
        Assert.DoesNotContain("rdzewny", PolishDictionary.Instance.Lemmas("nierdzewna"));
        Assert.Contains("rdzewny", PolishDictionary.Instance.Lemmas("rdzewna"));
    }

    [Fact]
    public void Unknown_words_have_no_lemmas()
    {
        Assert.Empty(PolishDictionary.Instance.Lemmas("xqzvbrk"));
        Assert.True(PolishDictionary.Instance.RootCount > 200_000);
    }

    [Fact]
    public void Polish_analyzer_falls_back_to_rules_for_unknown_words_and_keeps_codes()
    {
        var analyzer = Analyzer.Polish(PolishDictionary.Instance);

        var tokens = analyzer.Analyze("Wiertła Bosch SDS-plus M8x40");

        Assert.Equal(["wiertla", "bosch", "sds", "plus", "m8x40"], tokens.Select(t => t.Surface));
        Assert.Contains("wiertlo", tokens[0].Lemmas);
        Assert.Equal(["m8x40"], tokens[4].Lemmas);
    }
}

public sealed class StemmerTests
{
    [Theory]
    [InlineData("bolts", "bolt")]
    [InlineData("boxes", "box")]
    [InlineData("batteries", "battery")]
    [InlineData("glasses", "glass")]
    [InlineData("stainless", "stainless")]
    [InlineData("m8s", "m8s")]
    public void English_light_stemmer_strips_plurals(string word, string stem)
    {
        Assert.Equal(stem, Stemmers.EnglishLight(word));
    }

    [Theory]
    [InlineData("hiltiego", "hilti")]
    [InlineData("makity", "makit")]
    [InlineData("din933", "din933")]
    [InlineData("abc", "abc")]
    public void Polish_fallback_strips_endings_conservatively(string word, string stem)
    {
        Assert.Equal(stem, Stemmers.PolishFallback(word));
    }
}
