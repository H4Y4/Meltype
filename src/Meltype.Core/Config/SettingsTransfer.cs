// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

namespace Meltype.Config;

/// <summary>
/// 設定をファイルに書き出す・ファイルから読み込む (別の PC や Windows ⇔ Mac で同じ設定を使うため)。
/// ファイルは Windows の設定画面の「書き出す...」と同じ形 (*.meltype-profile.json。Settings.ExportProfile) で、どの OS で書き出したものも読める。
/// 読み込んだ設定は新しいプロファイルとして足して使う (今の設定はプロファイルとして残る)。
/// Meltype の ON/OFF・動作モード・ログ・更新など PC ごとの項目は書き出さず、読み込んでも変えない。
/// </summary>
internal static class SettingsTransfer
{
    /// <summary>書き出すファイルの拡張子。</summary>
    public const string FileExtension = ".meltype-profile.json";

    /// <summary>読み込むファイルの大きさの上限 (設定だけなら数十 KB もない)。</summary>
    public const long MaxFileBytes = 1024 * 1024;

    /// <summary>configPath の今の設定 (使っているプロファイル) を destination に書き出す。書き出したプロファイルの名前を返す。</summary>
    public static string Export(string configPath, string destination)
    {
        var settings = Settings.Load(configPath).Normalize();
        File.WriteAllText(destination, settings.ExportProfile());
        return settings.ActiveProfile;
    }

    /// <summary>
    /// source を新しいプロファイルとして configPath の設定に足し、それに切り替えて保存する。足したプロファイルの名前を返す。
    /// Meltype の設定として読めない・大きすぎるファイルなら null (設定は変えない)。
    /// </summary>
    public static string? Import(string configPath, string source)
    {
        if (new FileInfo(source).Length > MaxFileBytes) return null;
        if (Settings.Load(configPath).Normalize().ImportProfile(File.ReadAllText(source)) is not { } next) return null;
        next.Save(configPath);
        return next.ActiveProfile;
    }
}
