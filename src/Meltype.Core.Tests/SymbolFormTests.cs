// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

using Meltype.Composition;
using Meltype.Config;
using Meltype.Input;

namespace Meltype.Tests;

/// <summary>
/// 日本語の中の記号の形 (、 ， , （ ( ！ ! …) は設定どおりにし、変換エンジンの学習で変わらないようにする。
/// Mozc は ， を 1 回 、 にして確定すると記号の幅を覚え、以後の ， ． をすべて半角の , . にしていた。
/// </summary>
internal static class SymbolFormTests
{
    /// <summary>
    /// 記号の幅を半角と覚えてしまった Mozc の代わり。全角の記号は読みも文字も半角にして 1 文字の文節で返す
    /// (本物の Mozc も ， の読みを , にして返す)。comma を渡すと、， の文字をそれにする (、 を覚えてしまった Mozc)。
    /// </summary>
    private sealed class HalfWidthSymbolConverter(char? comma = null) : IKanjiConverter, ILearningConverter
    {
        private readonly CompositionTests.FakeConverter _inner = new();
        public List<string> Learned { get; } = [];

        public string? Convert(string hiragana) => string.Concat(ConvertClauses(hiragana)!.Select(c => c.Text));

        public IReadOnlyList<ConversionClause>? ConvertClauses(string hiragana, string? context = null)
        {
            var result = new List<ConversionClause>();
            var start = 0;
            for (var i = 0; i <= hiragana.Length; i++)
            {
                if (i < hiragana.Length && !IsSymbol(hiragana[i])) continue;
                if (i > start)
                {
                    var run = hiragana[start..i];
                    result.Add(new ConversionClause(run, _inner.Convert(run) ?? run));
                }
                if (i < hiragana.Length)
                {
                    var half = hiragana[i] is >= '！' and <= '～' ? (char)(hiragana[i] - 0xFEE0) : hiragana[i];
                    result.Add(new ConversionClause(half.ToString(), (half == ',' && comma is { } c ? c : half).ToString()));
                }
                start = i + 1;
            }
            return result;
        }

        private static bool IsSymbol(char c) => c is >= '！' and <= '～' && !char.IsLetterOrDigit(c) || c is '、' or '。' or '「' or '」' or '・';

        public void Learn(string? context, IReadOnlyList<ConversionClause> clauses)
        {
            lock (Learned) Learned.Add(string.Join("|", clauses.Select(c => $"{c.Reading}={c.Text}")));
        }
    }

    [Test]
    public static void SymbolForms_KeepsReadingForms()
    {
        Assert.Equal("今日は，", SymbolForms.Keep("今日は,", "きょうは，"));
        Assert.Equal("今日は，", SymbolForms.Keep("今日は、", "きょうは，"));
        Assert.Equal("今日は．", SymbolForms.Keep("今日は.", "きょうは．"));
        Assert.Equal("（テスト）", SymbolForms.Keep("(テスト)", "（てすと）"));
        Assert.Equal("はい！そう？", SymbolForms.Keep("はい!そう?", "はい！そう？"));
        // 数字の桁区切りは打ったとおり半角
        Assert.Equal("1,000円", SymbolForms.Keep("1，000円", "1,000えん"));
        // 記号の無い読みから変換エンジンが作った記号 (3かける4 → 3×4、ほし → ☆) はそのまま
        Assert.Equal("3×4", SymbolForms.Keep("3×4", "3かける4"));
        Assert.Equal("☆", SymbolForms.Keep("☆", "ほし"));
        // 読みと記号の数が違う (英語の文節の , が読みに入っていない) ときは、対応が分からないので変えない
        Assert.Equal("Unity,価格，", SymbolForms.Keep("Unity,価格，", "，かかく"));
    }

    [Test]
    public static void Punctuation_SettingWinsOverLearnedWidth()
    {
        foreach (var style in Enum.GetValues<PunctuationStyle>())
        {
            var (comma, period) = style switch
            {
                PunctuationStyle.FullWidthCommaPeriod => ('，', '．'),
                PunctuationStyle.FullWidthCommaKuten => ('，', '。'),
                PunctuationStyle.ToutenFullWidthPeriod => ('、', '．'),
                _ => ('、', '。'),
            };
            foreach (var converter in new[] { new HalfWidthSymbolConverter(), new HalfWidthSymbolConverter(comma: '、'), new HalfWidthSymbolConverter(comma: '，') })
            {
                foreach (var (typed, expected) in new[]
                {
                    // ライブ変換で Enter
                    ("kyouha,soudesu.\n", $"今日は{comma}そうです{period}"),
                    // Space で変換して Enter
                    ("kyouha,soudesu. \n", $"今日は{comma}そうです{period}"),
                    // 変換中に , を打って続ける (確定してから ， で始まる新しい入力になる)
                    ("kyouha ,soudesu\n", $"今日は{comma}そうです"),
                })
                {
                    var k = new CompositionTests.Keyboard(live: true, converter: converter) { Punctuation = style };
                    k.Type(typed);
                    Assert.Equal(expected, k.Host.Document, $"{style} {typed.Trim()}");
                }
            }
        }
    }

    [Test]
    public static void OtherSymbols_SettingWinsOverLearnedWidth()
    {
        // ！ ？ は全角、( ) は設定どおり (全角にする設定なら （ ）)
        var k = new CompositionTests.Keyboard(live: true, converter: new HalfWidthSymbolConverter());
        k.Type("kyouha!soudesu?\n");
        Assert.Equal("今日は！そうです？", k.Host.Document);

        var parentheses = new CompositionTests.Keyboard(live: true, converter: new HalfWidthSymbolConverter()) { FullWidthParentheses = true };
        parentheses.Type("kyouha(soudesu) \n");
        Assert.Equal("今日は（そうです）", parentheses.Host.Document);
    }

    [Test]
    public static void ResizedClause_KeepsSettingForm()
    {
        // 文節の区切りを動かして変換し直しても、記号は設定どおり (読みが変換エンジンの , にならない)
        var k = new CompositionTests.Keyboard(live: true, converter: new HalfWidthSymbolConverter()) { Punctuation = PunctuationStyle.FullWidthCommaPeriod };
        k.Type("kyouha,soudesu ");
        Assert.Equal("今日は|，|そうです", string.Join("|", k.Host.View!.Clauses!));
        // Shift+→ で 今日は に ， を含める (きょうは， を変換し直す)
        k.Key(VirtualKeys.LShift);
        k.Press(VirtualKeys.Right);
        Assert.Equal("今日は，|そうです", string.Join("|", k.Host.View.Clauses!));
        k.Key(VirtualKeys.LShift, up: true);
        k.Type("\n");
        Assert.Equal("今日は，そうです", k.Host.Document);
    }

    [Test]
    public static void SymbolClauses_AreNotTaughtToEngine()
    {
        // 記号を含む文節は変換エンジンに覚えさせない (覚えさせると、記号の幅がほかの語の記号にまで移る)
        var converter = new HalfWidthSymbolConverter();
        var k = new CompositionTests.Keyboard(live: true, converter: converter) { Punctuation = PunctuationStyle.FullWidthCommaPeriod };
        k.Type("kyouha,soudesu. ");
        // ， の文節で候補を選び直しても (、 にしても) 覚えさせない
        k.Press(VirtualKeys.Right);
        k.Press(VirtualKeys.Space);
        k.Type("\n");
        for (var i = 0; i < 50 && converter.Learned.Count < 2; i++) Thread.Sleep(20);
        lock (converter.Learned)
        {
            Assert.True(converter.Learned.Count > 0, "語の文節は覚えさせる");
            Assert.True(converter.Learned.All(l => !l.Any(c => SymbolForms.Family(c) is not null && c != '|' && c != '=')), string.Join(" / ", converter.Learned));
        }
    }

    [Test]
    public static void Predictions_KeepSettingForm()
    {
        var directory = Path.Combine(Path.GetTempPath(), "meltype-symbol-forms-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // 前の版で記号の形が変わったまま覚えた語句は、読むときに読みの形に直す (同じ語句はまとめる)
            var path = Path.Combine(directory, "phrases.txt");
            File.WriteAllLines(path, ["，よりはやく\t,より早く\t1\t639288655676948524", "，よりはやく\t，より早く\t2\t639288655676948000"]);
            var phrases = new PhraseHistory(path);
            Assert.Equal("，より早く", string.Join(" ", phrases.StartingWith("，")));
            // 覚えるときも読みの形で
            phrases.Remember("ひくいので，", "低いので,");
            Assert.Equal("低いので，", string.Join(" ", phrases.StartingWith("ひくい")));

            // 選び直した変換 (学習) も、予測に出すときは読みの形
            var history = new ConversionHistory(null);
            history.Remember("りゆう，", "理由,");
            var predictor = new Predictor(null, null, history);
            Assert.Equal("理由，", string.Join(" ", predictor.PredictJapanese("りゆ")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
