// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

namespace Meltype.Input;

/// <summary>
/// 指で押している Ctrl・Alt・Win (フックに届いた物理キーの押下/解放で追いかける)。
/// アプリから見た状態 (GetAsyncKeyState) は、フックで握りつぶしたキーや、ほかのソフトが送る間に離したことにしたキーを反映しないので、それとは別に持つ。
/// 書き換えるのはフックのスレッドだけ。読むのはどのスレッドからでもよい (確定した文字を送るときに UI スレッドから読む)。
/// </summary>
internal sealed class HeldModifiers
{
    private readonly object _gate = new();
    private readonly HashSet<int> _held = [];

    public bool Any
    {
        get { lock (_gate) return _held.Count > 0; }
    }

    /// <summary>このキー (LControl など) を指で押しているか。</summary>
    public bool IsHeld(int vk)
    {
        lock (_gate) return _held.Contains(vk);
    }

    public void OnKey(int vk, bool isDown)
    {
        if (!IsCommandModifier(vk)) return;
        lock (_gate)
        {
            if (isDown) _held.Add(vk);
            else _held.Remove(vk);
        }
    }

    /// <summary>離したのにフックに届かなかったキー (ロック画面・UAC の画面に移っている間に離した) を外す。isDown はアプリから見た状態。</summary>
    public void Resync(Func<int, bool> isDown)
    {
        lock (_gate) _held.RemoveWhere(vk => !isDown(vk));
    }

    private static bool IsCommandModifier(int vk) =>
        vk is VirtualKeys.LControl or VirtualKeys.RControl or VirtualKeys.LMenu or VirtualKeys.RMenu or VirtualKeys.LWin or VirtualKeys.RWin;
}
