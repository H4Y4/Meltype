// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 H4Y4

namespace Meltype.Composition;

/// <summary>
/// 前後の文脈を読むニューラルの変換エンジン (Mac 版の Zenzai) を、ワーカー (<see cref="BackgroundCallbackConverter"/>) 越しに
/// <see cref="IContextAwareConverter"/> として本体に渡す。本体は、手がかり辞書を控えめに使い、学習を先にし、前の文の文脈と
/// 後ろの文脈 (確定済みの文字・後ろの英単語) も渡す。変換はワーカーを通すので、入力のスレッドは待たない。
/// ワーカーは clausesWithFollowing を渡して作ること (渡さないと後ろの文脈は捨てられる)。
/// </summary>
public sealed class ContextAwareBackgroundConverter(BackgroundCallbackConverter worker) : IKanjiConverter, IContextAwareConverter, IBackgroundConverter
{
    public ConversionBudget Budget => worker.Budget;

    public event Action? ResultReady
    {
        add => worker.ResultReady += value;
        remove => worker.ResultReady -= value;
    }

    public string? Convert(string hiragana) => worker.Convert(hiragana);

    public IReadOnlyList<ConversionClause>? ConvertClauses(string hiragana, string? context = null) => worker.ConvertClauses(hiragana, context);

    public IReadOnlyList<ConversionClause>? ConvertClauses(string hiragana, string? context, string? following) =>
        worker.ConvertClauses(hiragana, context, following);

    /// <summary>候補の一覧 (ワーカーの候補)。</summary>
    public IReadOnlyList<string> Candidates(string reading) => worker.Candidates(reading);
}
