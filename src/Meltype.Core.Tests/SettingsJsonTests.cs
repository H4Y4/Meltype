// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 0924haruto12

using Meltype.Config;

namespace Meltype.Tests;

internal static class SettingsJsonTests
{
    [Test]
    public static void SettingsJson_PreservesEnumsAndSpacingAcrossSaveLoad()
    {
        var directory = Path.Combine(Path.GetTempPath(), "meltype-settings-json-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "config.json");
            var original = new Settings
            {
                SpaceAroundEnglish = true, InputStyle = InputStyle.Kana,
                DetectionLevel = DetectionLevel.Manual, Mode = InputMode.Keyboard,
            }.Normalize();
            original.Save(path);
            Assert.True(original.ToJson().Contains("\"Keyboard\"") && original.ToJson().Contains("\"Kana\""), "enum の文字列表現を維持する");
            var loaded = Settings.Load(path);
            Assert.True(loaded.SpaceAroundEnglish, "空白設定を読み込む");
            Assert.Equal(InputStyle.Kana, loaded.InputStyle);
            Assert.Equal(DetectionLevel.Manual, loaded.DetectionLevel);
            Assert.Equal(InputMode.Keyboard, loaded.Mode);
            Assert.True(!File.Exists(path + ".broken"), "有効な設定を壊れたファイルとして扱わない");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public static void SettingsJson_StillAcceptsCommentsTrailingCommasAndNumericEnums()
    {
        var directory = Path.Combine(Path.GetTempPath(), "meltype-settings-json-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "config.json");
            File.WriteAllText(path, "{ /* synthetic */ \"SettingsVersion\": 5, \"SpaceAroundEnglish\": true, \"InputStyle\": 1, }");
            var loaded = Settings.Load(path);
            Assert.True(loaded.SpaceAroundEnglish, "コメント・末尾カンマを受け入れる");
            Assert.Equal(InputStyle.Kana, loaded.InputStyle, "旧形式の数値 enum を受け入れる");
            Assert.True(!File.Exists(path + ".broken"), "有効な旧形式を壊れたファイルとして扱わない");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public static void SettingsJson_ControlKeys_RoundTripsByName()
    {
        Assert.Equal(ControlKeyStyle.Atok, new Settings().ControlKeys, "既定は ATOK 式");
        var directory = Path.Combine(Path.GetTempPath(), "meltype-settings-json-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "config.json");
            new Settings { ControlKeys = ControlKeyStyle.Mac }.Normalize().Save(path);
            Assert.True(File.ReadAllText(path).Contains("\"ControlKeys\": \"Mac\""), "名前で保存する");
            Assert.Equal(ControlKeyStyle.Mac, Settings.Load(path).ControlKeys);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public static void SettingsJson_ControlKeys_MissingIsAtokAndCaseInsensitive()
    {
        var directory = Path.Combine(Path.GetTempPath(), "meltype-settings-json-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "config.json");
            File.WriteAllText(path, "{ \"SettingsVersion\": 5, \"SpaceAroundEnglish\": true }");
            Assert.Equal(ControlKeyStyle.Atok, Settings.Load(path).ControlKeys, "書いていなければ ATOK 式");
            // ほかの列挙型 (Punctuation) と同じく、名前の大文字小文字は問わず、数値でも書ける
            File.WriteAllText(path, "{ \"SettingsVersion\": 5, \"ControlKeys\": \"mac\", \"Punctuation\": \"fullwidthcommaperiod\" }");
            var loaded = Settings.Load(path);
            Assert.Equal(ControlKeyStyle.Mac, loaded.ControlKeys, "大文字小文字は問わない");
            Assert.Equal(PunctuationStyle.FullWidthCommaPeriod, loaded.Punctuation, "Punctuation も同じ");
            File.WriteAllText(path, "{\"SettingsVersion\": 5, \"ControlKeys\": \"Mac\"}");
            var minimal = Settings.Load(path);
            Assert.Equal(ControlKeyStyle.Mac, minimal.ControlKeys, "ControlKeys だけの config.json");
            Assert.Equal(new Settings().Punctuation, minimal.Punctuation, "ほかの設定は既定値");
            File.WriteAllText(path, "{ \"SettingsVersion\": 5, \"ControlKeys\": 1 }");
            Assert.Equal(ControlKeyStyle.Mac, Settings.Load(path).ControlKeys, "数値でも読める (ほかの列挙型と同じ)");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public static void SettingsJson_ControlKeys_UnknownValueBehavesLikeOtherEnums()
    {
        var directory = Path.Combine(Path.GetTempPath(), "meltype-settings-json-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "config.json");
            // 知らない名前・null・配列・空文字は、ほかの列挙型 (Punctuation) と同じく config.json 全体が既定値に戻り、.broken に退避される
            foreach (var key in new[] { "ControlKeys", "Punctuation" })
                foreach (var value in new[] { "\"Emacs\"", "null", "[1]", "\"\"" })
                {
                    File.WriteAllText(path, "{ \"SettingsVersion\": 5, \"" + key + "\": " + value + ", \"SpaceAroundEnglish\": true }");
                    var loaded = Settings.Load(path);
                    Assert.Equal(ControlKeyStyle.Atok, loaded.ControlKeys, key + "=" + value);
                    Assert.True(!loaded.SpaceAroundEnglish, "設定全体が既定値に戻る: " + key + "=" + value);
                    Assert.True(File.Exists(path + ".broken"), ".broken に退避する: " + key + "=" + value);
                    File.Delete(path + ".broken");
                }
            // 定義の無い数値は、ほかの列挙型と同じくそのまま読む。入力中の Ctrl キーでは Mac 以外 = ATOK 式として動く
            File.WriteAllText(path, "{ \"SettingsVersion\": 5, \"ControlKeys\": 99, \"SpaceAroundEnglish\": true }");
            var undefined = Settings.Load(path);
            Assert.True(undefined.ControlKeys != ControlKeyStyle.Mac, "定義の無い数値は Mac にならない");
            Assert.True(undefined.SpaceAroundEnglish, "定義の無い数値では設定全体は戻らない");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public static void SettingsFile_ReloadsWhenChanged()
    {
        // config.json が書き換えられたら読み直す (開いたままの入力欄にもすぐ効く)。調べるのは 1 秒に 1 回まで。
        var directory = Path.Combine(Path.GetTempPath(), "meltype-settings-file-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "config.json");
            File.WriteAllText(path, "{ \"SettingsVersion\": 5, \"HalfWidthSymbols\": \"\" }");
            long now = 0;
            var file = new SettingsFile(path, () => now);
            Assert.Equal("", file.Current.HalfWidthSymbols, "最初に読む");

            void Write(string json, int seconds)
            {
                File.WriteAllText(path, json);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(seconds));
            }
            Write("{ \"SettingsVersion\": 5, \"HalfWidthSymbols\": \"*\" }", 10);
            now = 500;
            Assert.Equal("", file.Current.HalfWidthSymbols, "1 秒たつまでは調べない");
            now = 1000;
            Assert.Equal("*", file.Current.HalfWidthSymbols, "書き換えられたら読み直す");

            // 書いている途中・書き間違いのファイルは読めないので、前の設定のまま。.broken にも退避しない
            Write("{ \"SettingsVersion\": 5, \"HalfWidthSymbols\": ", 20);
            now = 2000;
            Assert.Equal("*", file.Current.HalfWidthSymbols, "読めなければ前の設定のまま");
            Assert.True(!File.Exists(path + ".broken"), "読み直しでは .broken に退避しない");
            Write("{ \"SettingsVersion\": 5, \"HalfWidthSymbols\": \"*>\" }", 30);
            now = 3000;
            Assert.Equal("*>", file.Current.HalfWidthSymbols, "書き終わったら読み直す");
            // 入力欄を作るときは、間隔を待たずに調べる
            Write("{ \"SettingsVersion\": 5, \"HalfWidthSymbols\": \"*>#\" }", 40);
            Assert.Equal("*>", file.Current.HalfWidthSymbols, "間隔の中は調べない");
            Assert.Equal("*>#", file.Refresh().HalfWidthSymbols, "Refresh はすぐ調べる");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public static void SettingsJson_LatinKeyboardLayout()
    {
        // Mac 版だけが使う「英字のレイアウト」。書かなければ空 (指定なし)、前後の空白は取る
        Assert.Equal("", new Settings().LatinKeyboardLayout, "既定は指定なし");
        var path = Path.Combine(Path.GetTempPath(), "meltype-latin-layout-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{ \"SettingsVersion\": 5, \"LatinKeyboardLayout\": \" com.apple.keylayout.US \" }");
            Assert.Equal("com.apple.keylayout.US", Settings.Load(path).LatinKeyboardLayout, "config.json から読む");
            File.WriteAllText(path, "{ \"SettingsVersion\": 5, \"LatinKeyboardLayout\": null }");
            Assert.Equal("", Settings.Load(path).LatinKeyboardLayout, "null は指定なし");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public static void SettingsJson_KeepsMigratedSettingsWhenSaveFails()
    {
        var directory = Path.Combine(Path.GetTempPath(), "meltype-settings-json-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config.json");
        try
        {
            File.WriteAllText(path, "{ \"SettingsVersion\": 4, \"Mode\": \"Keyboard\", \"SpaceAroundEnglish\": true }");
            // 移行のあとの保存を失敗させる。Windows は読み取り専用のファイルに置き換えられない。
            // Mac・Linux は読み取り専用でも置き換えられるので、.tmp の場所にフォルダーを置いて書けなくする
            if (OperatingSystem.IsWindows()) File.SetAttributes(path, FileAttributes.ReadOnly);
            else Directory.CreateDirectory(path + ".tmp");
            var loaded = Settings.Load(path);
            Assert.Equal(InputMode.Keyboard, loaded.Mode, "保存に失敗しても読み込んだ値を使う");
            Assert.True(loaded.SpaceAroundEnglish, "保存に失敗しても既定値に戻さない");
            Assert.Equal(Settings.CurrentVersion, loaded.SettingsVersion, "移行は済ませる");
            Assert.True(loaded.AppRules.Any(r => r.Process == "maya.exe"), "移行で足したアプリの設定を残す");
            Assert.True(!File.Exists(path + ".broken"), "保存の失敗を壊れたファイルとして扱わない");
        }
        finally
        {
            foreach (var file in Directory.GetFiles(directory)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public static void SettingsJson_ReadsRemovedChoicesAsCurrentOnes()
    {
        // 消した動作モード「IME 自動切替」は Meltype IME、消した入力方式「両方を判定」はローマ字入力として読む。
        // ほかの設定は既定値に戻さない (知らない名前で読めないと、設定がすべて既定値に戻る)
        var directory = Path.Combine(Path.GetTempPath(), "meltype-settings-json-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "config.json");
            File.WriteAllText(path, "{ \"SettingsVersion\": 5, \"Mode\": \"AutoSwitch\", \"TsfIntroduced\": true, \"InputStyle\": \"Both\", \"SpaceAroundEnglish\": true, \"IdleFlushMs\": 900, " +
                                    "\"ActiveProfile\": \"仕事\", \"Profiles\": [ { \"Name\": \"仕事\" }, { \"Name\": \"趣味\", \"Values\": { \"InputStyle\": \"Both\", \"LiveConversion\": false } } ] }");
            var loaded = Settings.Load(path);
            Assert.Equal(InputMode.Tsf, loaded.Mode);
            Assert.True(!loaded.TsfIntroduced, "初めて起動したときと同じく、Meltype IME を使えなければ Meltype キーボードに戻す (Program.ShouldFallBackToKeyboard)");
            Assert.Equal(InputStyle.Romaji, loaded.InputStyle);
            Assert.True(loaded.SpaceAroundEnglish, "ほかの設定はそのまま");
            Assert.True(!File.Exists(path + ".broken"), "壊れたファイルとして扱わない");
            Assert.True(File.ReadAllText(path).Contains("\"Mode\": \"Tsf\""), "読んだときに移した内容を保存する");
            Assert.True(Settings.Load(path).Mode == InputMode.Tsf && !Settings.Load(path).TsfIntroduced, "次に読んだときも同じ");
            var hobby = loaded.SwitchProfile("趣味");
            Assert.Equal(InputStyle.Romaji, hobby.InputStyle, "プロファイルに残っている「両方を判定」も読める");
            Assert.True(!hobby.LiveConversion, "プロファイルのほかの値はそのまま");
            Assert.True(loaded.ToJson().Contains("\"Mode\": \"Tsf\"") && loaded.ToJson().Contains("\"InputStyle\": \"Romaji\""), "保存するときは今の名前で書く");

            var imported = new Settings().ImportProfile("{\"format\":\"meltype-profile\",\"name\":\"前の版\",\"values\":{\"InputStyle\":\"Both\",\"SpaceAroundEnglish\":true}}");
            Assert.True(imported is { InputStyle: InputStyle.Romaji, SpaceAroundEnglish: true }, "前の版で書き出したプロファイルも読み込める");

            // ほかのモードの人と、プロファイルの中に前の版の "Mode" が残っているだけの人は、TsfIntroduced を変えない
            File.WriteAllText(path, "{ \"Mode\": \"Keyboard\", \"TsfIntroduced\": true, \"Profiles\": [ { \"Name\": \"標準\", \"Values\": { \"Mode\": \"AutoSwitch\" } } ] }");
            Assert.True(Settings.Load(path) is { Mode: InputMode.Keyboard, TsfIntroduced: true }, "動作モードは Meltype キーボードのまま");

            // 数値で書いたもの: 1 は消した IME 自動切替、入力方式の 2 は消した「両方を判定」
            File.WriteAllText(path, "{ \"SettingsVersion\": 5, \"Mode\": 1, \"InputStyle\": 2 }");
            Assert.True(Settings.Load(path) is { Mode: InputMode.Tsf, InputStyle: InputStyle.Romaji }, "初期値にする");
            File.WriteAllText(path, "{ \"SettingsVersion\": 5, \"Mode\": 2 }");
            Assert.Equal(InputMode.Tsf, Settings.Load(path).Mode, "Meltype IME の番号は前の版のまま");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public static void ModeIndicatorColors_AreParsed()
    {
        // #359: 入力モードの表示の色は #RRGGBB (# は無くても・#RGB でも)。空・読めない値は既定の色
        Assert.True(Settings.ParseColor("#D32F2F") == (0xD3, 0x2F, 0x2F), "#RRGGBB");
        Assert.True(Settings.ParseColor("007acc") == (0x00, 0x7A, 0xCC), "# なし・小文字");
        Assert.True(Settings.ParseColor("#fff") == (255, 255, 255), "#RGB");
        Assert.True(Settings.ParseColor("") is null && Settings.ParseColor("red") is null && Settings.ParseColor("#12345") is null, "読めない値");
        Assert.Equal("", new Settings().ModeIndicatorDirectColor, "既定は空 (今までの色)");
    }
}
