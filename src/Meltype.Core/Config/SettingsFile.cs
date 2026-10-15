// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

namespace Meltype.Config;

/// <summary>
/// config.json を読み、書き換えられたら読み直す。設定の変更を、開いたままの入力欄にもすぐ効かせる
/// (Mac・Linux。VS Code・Chrome などは入力欄の本体を作り直さないので、作るときに読むだけだとアプリを開き直すまで効かなかった)。
/// 書き換えを見るのは checkIntervalMs に 1 回まで (キーを打つたびにファイルを調べない)。
/// 読み直しで読めなかったとき (書いている途中・書き間違い) は、前の設定のまま使う。
/// </summary>
internal sealed class SettingsFile
{
    private readonly string _path;
    private readonly Func<long> _now;
    private readonly long _checkIntervalMs;
    private Settings _current;
    private DateTime _writeTime;
    private long _checkedAt;

    public SettingsFile(string path, Func<long>? now = null, long checkIntervalMs = 1000)
    {
        _path = path;
        _now = now ?? (() => Environment.TickCount64);
        _checkIntervalMs = checkIntervalMs;
        _current = Settings.Load(path);
        _writeTime = WriteTime(path);
        _checkedAt = _now();
    }

    public string Path => _path;

    /// <summary>今の設定。ファイルが書き換えられていれば読み直したもの。</summary>
    public Settings Current
    {
        get
        {
            var now = _now();
            if (now - _checkedAt < _checkIntervalMs) return _current;
            _checkedAt = now;
            var writeTime = WriteTime(_path);
            if (writeTime == _writeTime) return _current;
            if (Settings.TryReload(_path) is { } reloaded)
            {
                _current = reloaded;
                _writeTime = writeTime;
                Diagnostics.Log.Info("config.json が書き換えられたので読み直しました");
            }
            return _current;
        }
    }

    private static DateTime WriteTime(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default; }
        catch { return default; }
    }
}
