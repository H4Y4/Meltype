// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

namespace Meltype.Composition;

/// <summary>
/// 日本語の中の記号の形 (全角・半角、、 か ，) は、Meltype が設定どおりに作った読みのものを使う。
/// 変換エンジンは学習で記号の幅を覚えて、ほかの語の記号まで変えてしまう (Mozc は ， を 1 回 、 にして確定すると、
/// 以後の ， ． をすべて半角の , . にする)。変換結果・学習した変換・予測変換の記号は、読みの記号の形に戻す。
/// </summary>
public static class SymbolForms
{
    /// <summary>
    /// 同じ記号の別の形をまとめた種類。記号でない文字 (かな・漢字・英数字・長音) は null。
    /// 読点 (、，,) と句点 (。．.) は、句読点の設定で入れ替わるので、それぞれ 1 つの種類にする。
    /// </summary>
    internal static char? Family(char c)
    {
        if (char.IsLetterOrDigit(c)) return null;
        return c switch
        {
            '、' or '，' or ',' or '､' => ',',
            '。' or '．' or '.' or '｡' => '.',
            '「' or '｢' or '[' or '［' => '[',
            '」' or '｣' or ']' or '］' => ']',
            '・' or '･' => '・',
            '～' or '〜' or '~' => '~',
            '’' or '‘' or '\'' => '\'',
            '”' or '“' or '"' => '"',
            '￥' or '¥' or '\\' => '¥',
            '　' or ' ' => ' ',
            >= '！' and <= '～' => (char)(c - 0xFEE0),
            >= '!' and <= '~' => c,
            _ => null,
        };
    }

    /// <summary>読みに、形を保つ記号があるか。</summary>
    public static bool Contains(string reading) => reading.Any(c => Family(c) is not null);

    /// <summary>
    /// converted の記号を、reading の同じ種類の記号の形にする (前から順に対応させる)。文字数は変わらない。
    /// ある種類の記号の数が converted と reading で違うときは、対応が分からないので、その種類は変えない。
    /// </summary>
    public static string Keep(string converted, string reading)
    {
        if (converted.Length == 0 || !Contains(reading)) return converted;
        var expected = new Dictionary<char, Queue<char>>();
        foreach (var c in reading)
        {
            if (Family(c) is not { } family) continue;
            if (!expected.TryGetValue(family, out var queue)) expected[family] = queue = new Queue<char>();
            queue.Enqueue(c);
        }
        var counts = new Dictionary<char, int>();
        foreach (var c in converted)
            if (Family(c) is { } family) counts[family] = counts.GetValueOrDefault(family) + 1;
        char[]? chars = null;
        for (var i = 0; i < converted.Length; i++)
        {
            if (Family(converted[i]) is not { } family || !expected.TryGetValue(family, out var queue) || queue.Count != counts[family]) continue;
            // 対応させた分は数からも減らし、残りの数とそろえたままにする
            counts[family]--;
            var form = queue.Dequeue();
            if (form == converted[i]) continue;
            chars ??= converted.ToCharArray();
            chars[i] = form;
        }
        return chars is null ? converted : new string(chars);
    }
}
