// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

namespace Meltype.Composition;

/// <summary>文節 1 つ分の変換結果。Reading はその文節の読み (ひらがな)。</summary>
public sealed record ConversionClause(string Reading, string Text);

/// <summary>
/// 助詞だけの文節の読み。変換エンジンは助詞だけの文節を漢字にしてしまう (github + に + push → 二) ので、かなのまま使う。
/// CompositionController (文節の候補) と HybridConverter (Zenzai の結果の当てはめ) で同じものを使う。
/// </summary>
internal static class JapaneseParticles
{
    private static readonly HashSet<string> Readings = ["は", "が", "を", "に", "で", "と", "も", "へ", "の", "や", "か", "から", "まで", "より"];

    internal static bool IsParticle(string reading) => Readings.Contains(reading);
}

/// <summary>
/// ひらがな → 漢字かな混じりの変換。Meltype 自身は変換の辞書を持たず、OS や外部の変換エンジンを借りる
/// (Windows: Microsoft IME、Mac / Linux: Mozc など)。
/// </summary>
public interface IKanjiConverter
{
    /// <summary>全体を 1 回で変換する。変換できなければ null。</summary>
    string? Convert(string hiragana);

    /// <summary>
    /// 文節に区切って変換する。区切れなければ null。context は直前の確定済みの文字列 (変換の文脈として使うだけで、結果には含めない)。
    /// </summary>
    IReadOnlyList<ConversionClause>? ConvertClauses(string hiragana, string? context = null);
}

/// <summary>
/// 文脈を読んで変換するニューラルの変換エンジン (Mac 版の azooKey の Zenzai)。次の点が変わる:
///   1. 文脈には、キャレットの前の文字に加えて、同じ入力の中で前にある区間 (meltype + のへんかんせいど の meltype) も渡す。
///      前の文字が英語でも渡す (API + のしよう → の仕様)。
///   2. 文脈付きの結果をそのまま使い、区切りを見比べるための文脈なしの変換をしない
///      (文脈で区切りが崩れにくく、打つたびに 2 回変換すると遅い)。
///   3. 変換する読みのすぐ後ろに続く文字も渡す (かいとう + した冷凍食品 → 解凍、きどうを + debug → 起動を)。
/// </summary>
public interface IContextAwareConverter
{
    /// <summary>
    /// 前後の文脈つきで、文節に区切って変換する。区切れなければ null。
    /// following は、変換する読みのすぐ後ろに続く文字: 入力欄の確定済みの文字か、同じ入力の中の英単語。
    /// 変換前のかなは渡さない (変換エンジンがかなに引きずられて、結果までかなになる)。
    /// </summary>
    IReadOnlyList<ConversionClause>? ConvertClauses(string hiragana, string? context, string? following);
}

/// <summary>
/// 広い文脈を使える変換エンジン (Zenzai)。ConvertClauses に渡す文脈は、Mozc / Microsoft IME に合わせて同じ文の最大 10 文字だけだが、
/// ニューラルモデルは前の文までの長い文脈ほど強い (文脈の無い きょう → 強 が、「昨日」だけで 今日 になる)。
/// 入力を始めるたびに、キャレットの前の日本語の文章 (文をまたいで最大 100 文字) を入れる。無ければ null。
/// </summary>
public interface IWideContextConverter
{
    string? WideContext { get; set; }
}

/// <summary>
/// 文節ごとの候補に足す「その読みだけの変換」を、Zenzai を使わずに出せる変換エンジン (HybridConverter)。
/// 文節の数だけ頼むので、Zenzai に頼むと 1 文節ごとに待たされる (CPU のみで、文節に分かれた文の変換 1 回あたり中央値 98 ミリ秒増えた)。
/// 最初の候補 (文の中での変換結果) は ConvertClauses のままなので、Zenzai の結果が使われる。
/// </summary>
public interface IStandaloneConverter
{
    string? ConvertStandalone(string hiragana);
}

/// <summary>確定した変換を覚えられる変換エンジン (Mozc)。次から同じ読みで同じ文字列を先に出す。</summary>
public interface ILearningConverter
{
    /// <summary>ユーザーが確定した文節 (読みと文字列、区切りも確定したとおり) を覚えさせる。context は直前の確定済みの文字列。</summary>
    void Learn(string? context, IReadOnlyList<ConversionClause> clauses);
}
