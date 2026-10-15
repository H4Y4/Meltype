// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

namespace Meltype.Composition;

/// <summary>
/// 入力中 (変換ボックス・Meltype IME の変換中) かを、ほかのソフトから読めるようにする名前付きのイベント (Local\Meltype.Composing)。
/// 入力中の間だけシグナル状態になる。キーの割り当てを変えるソフト (AutoHotkey の Emacs 風の割り当て) が、入力中は Ctrl+K などを
/// Meltype に譲るのに使う (Mac と同じく、入力中の Ctrl+K はカタカナ、入力していないときは行末まで削除)。
/// AutoHotkey からは OpenEvent と WaitForSingleObject (待ち時間 0) で読む (docs/USAGE.md)。
/// 入力の本体ごと (変換ボックス・Meltype IME のつながりごと) に入力中かを覚え、どれか 1 つでも入力中ならシグナル状態にする。
/// </summary>
internal static class ComposingSignal
{
    public const string Name = @"Local\Meltype.Composing";

    private static readonly object Gate = new();
    private static readonly HashSet<object> Composing = [];
    private static EventWaitHandle? _event;
    private static bool _failed;

    /// <summary>owner (入力の本体) が入力中か。入力が終わった・つながりが切れたら false で呼ぶ。</summary>
    public static void Update(object owner, bool composing)
    {
        lock (Gate)
        {
            var changed = composing ? Composing.Add(owner) : Composing.Remove(owner);
            if (!changed || Event() is not { } signal) return;
            if (Composing.Count > 0) signal.Set();
            else signal.Reset();
        }
    }

    private static EventWaitHandle? Event()
    {
        if (_event is not null || _failed) return _event;
        try
        {
            _event = new EventWaitHandle(false, EventResetMode.ManualReset, Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            // 同じ名前のほかの種類のものがある・開けない: 公開しないだけで、入力には関係しない
            _failed = true;
            Diagnostics.Log.Warn($"入力中かを公開する {Name} を作れませんでした: {ex.Message}");
        }
        return _event;
    }
}
